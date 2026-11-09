using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>manga-ocr(ONNX)로 말풍선 블록을 통째로 인식한다. 세로쓰기·후리가나 포함 일본어 특화.
/// ViT 인코더(224×224, (x−0.5)/0.5 정규화) + 자기회귀 디코더([CLS]=2 시작, [SEP]=3 종료) 그리디 생성.
///
/// **한 쪽의 블록을 한 묶음으로 읽는다** (2026-11-10). 디코더는 KV 캐시가 없어 글자 하나를 낼 때마다 한 번씩 돌려야 하는데,
/// 블록마다 따로 돌리면 실행 횟수가 "블록 수 × 글자 수"가 된다(실측: 블록당 0.33초, 10블록 쪽 3초 남짓 — 사용자가 다른 도구보다 느리다고 지적).
/// 묶어서 돌리면 실행 횟수가 "가장 긴 블록의 글자 수"로 줄어든다. 먼저 끝난 블록은 채움 토큰을 이어 붙이고 결과만 무시한다
/// (디코더는 앞쪽 글자만 보므로 뒤에 붙는 것은 앞의 결과에 영향이 없다).</summary>
public class MangaOcr(OnnxEnv env)
{
    public const string EncoderFile = "ocr_encoder.onnx";
    public const string DecoderFile = "ocr_decoder.onnx";
    public const string VocabFile = "ocr_vocab.txt";
    private const int MaxTokens = 96;
    /// <summary>한 번에 묶는 블록 수 (그래픽 메모리를 지나치게 쓰지 않게).</summary>
    private const int BatchSize = 16;
    private const long Cls = 2, Sep = 3, Pad = 0;

    private string[]? _vocab;
    private bool _batchBroken;   // 이 모델이 묶음 실행을 못 받으면 한 블록씩으로 내려간다

    public bool IsReady => env.ModelExists(EncoderFile) && env.ModelExists(DecoderFile) && env.ModelExists(VocabFile);

    /// <summary>블록 크롭(BGR)을 인식해 텍스트를 돌려준다. 빈 결과면 "".</summary>
    public string Recognize(Mat crop) => RecognizeBatch([crop])[0];

    /// <summary>블록 크롭 여러 장을 한꺼번에 인식한다. 돌려주는 순서는 넣은 순서와 같다.</summary>
    public string[] RecognizeBatch(IReadOnlyList<Mat> crops)
    {
        _vocab ??= File.ReadAllLines(env.ModelPath(VocabFile));
        var result = new string[crops.Count];
        int step = _batchBroken ? 1 : BatchSize;
        for (int start = 0; start < crops.Count; start += step)
        {
            var part = crops.Skip(start).Take(step).ToList();
            string[] texts;
            try
            {
                texts = Run(part);
            }
            catch (OnnxRuntimeException) when (part.Count > 1)
            {
                // 묶음 크기가 고정된 모델이거나 그래픽 메모리가 모자란다 — 한 블록씩 읽는다
                _batchBroken = true;
                texts = part.Select(c => Run([c])[0]).ToArray();
                step = 1;
            }
            texts.CopyTo(result, start);
        }
        return result;
    }

    private string[] Run(IReadOnlyList<Mat> crops)
    {
        int n = crops.Count;
        var pixel = new DenseTensor<float>(new[] { n, 3, 224, 224 });
        for (int i = 0; i < n; i++)
        {
            using var resized = crops[i].Resize(new Size(224, 224), interpolation: InterpolationFlags.Linear);
            unsafe
            {
                byte* p = (byte*)resized.DataPointer;
                long stride = (long)resized.Step();
                for (int y = 0; y < 224; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < 224; x++)
                    {
                        // BGR → RGB, (x/255 − 0.5)/0.5
                        pixel[i, 0, y, x] = row[x * 3 + 2] / 127.5f - 1f;
                        pixel[i, 1, y, x] = row[x * 3 + 1] / 127.5f - 1f;
                        pixel[i, 2, y, x] = row[x * 3 + 0] / 127.5f - 1f;
                    }
                }
            }
        }

        // Guard: GPU에서 실행 오류가 나면 CPU로 내려 한 번 더 (OnnxEnv.Guard)
        using var encOut = env.Guard(() => env.Get(EncoderFile).Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", pixel) }), EncoderFile);
        var hiddenSrc = encOut.First().AsTensor<float>();
        // 디코더 호출마다 재사용할 수 있게 복사해 둔다 (encOut dispose 대비)
        var hidden = new DenseTensor<float>(hiddenSrc.ToArray(), hiddenSrc.Dimensions.ToArray());

        var ids = Enumerable.Range(0, n).Select(_ => new List<long> { Cls }).ToArray();
        var done = new bool[n];
        for (int step = 0; step < MaxTokens && done.Contains(false); step++)
        {
            int length = step + 1;
            var flat = new long[n * length];
            for (int i = 0; i < n; i++) ids[i].CopyTo(flat, i * length);
            var inputIds = new DenseTensor<long>(flat, new[] { n, length });
            using var decOut = env.Guard(() => env.Get(DecoderFile).Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            }), DecoderFile);
            var logits = decOut.First().AsTensor<float>(); // [n, length, vocab]
            int vocabSize = logits.Dimensions[2];
            // 인덱서([i, t, v])는 느리다 — 평탄 버퍼에서 마지막 자리의 줄만 훑는다
            var buffer = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray().AsSpan();
            for (int i = 0; i < n; i++)
            {
                if (done[i]) { ids[i].Add(Pad); continue; }
                var row = buffer.Slice((i * length + length - 1) * vocabSize, vocabSize);
                int best = 0; float bestV = float.MinValue;
                for (int v = 0; v < vocabSize; v++)
                    if (row[v] > bestV) { bestV = row[v]; best = v; }
                if (best == Sep) { done[i] = true; ids[i].Add(Pad); }
                else ids[i].Add(best);
            }
        }

        var texts = new string[n];
        for (int i = 0; i < n; i++)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var id in ids[i].Skip(1))
            {
                if (id < 5 || id >= _vocab!.Length) continue; // 특수 토큰(채움 포함) 제외
                var tok = _vocab[id];
                if (tok.StartsWith("##")) tok = tok[2..];
                if (tok.StartsWith("<unused")) continue;
                sb.Append(tok);
            }
            texts[i] = sb.ToString().Trim();
        }
        return texts;
    }
}
