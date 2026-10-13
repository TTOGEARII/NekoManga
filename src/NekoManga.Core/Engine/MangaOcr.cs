using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>manga-ocr(ONNX)로 말풍선 블록을 통째로 인식한다. 세로쓰기·후리가나 포함 일본어 특화.
/// ViT 인코더(224×224, (x−0.5)/0.5 정규화) + 자기회귀 디코더([CLS]=2 시작, [SEP]=3 종료) 그리디 생성.
/// 디코더는 KV 캐시 없이 매 스텝 재계산 — 말풍선 길이(수십 토큰)에선 충분히 빠르다.</summary>
public class MangaOcr(OnnxEnv env)
{
    public const string EncoderFile = "ocr_encoder.onnx";
    public const string DecoderFile = "ocr_decoder.onnx";
    public const string VocabFile = "ocr_vocab.txt";
    private const int MaxTokens = 96;

    private string[]? _vocab;

    public bool IsReady => env.ModelExists(EncoderFile) && env.ModelExists(DecoderFile) && env.ModelExists(VocabFile);

    /// <summary>블록 크롭(BGR)을 인식해 텍스트를 돌려준다. 빈 결과면 "".</summary>
    public string Recognize(Mat crop)
    {
        _vocab ??= File.ReadAllLines(env.ModelPath(VocabFile));

        using var resized = crop.Resize(new Size(224, 224), interpolation: InterpolationFlags.Linear);
        var pixel = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
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
                    pixel[0, 0, y, x] = row[x * 3 + 2] / 127.5f - 1f;
                    pixel[0, 1, y, x] = row[x * 3 + 1] / 127.5f - 1f;
                    pixel[0, 2, y, x] = row[x * 3 + 0] / 127.5f - 1f;
                }
            }
        }

        var encoder = env.Get(EncoderFile);
        using var encOut = encoder.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", pixel) });
        var hiddenSrc = encOut.First().AsTensor<float>();
        // 디코더 호출마다 재사용할 수 있게 복사해 둔다 (encOut dispose 대비)
        var hidden = new DenseTensor<float>(hiddenSrc.ToArray(), hiddenSrc.Dimensions.ToArray());

        var decoder = env.Get(DecoderFile);
        var ids = new List<long> { 2 }; // [CLS]
        for (int step = 0; step < MaxTokens; step++)
        {
            var inputIds = new DenseTensor<long>(ids.ToArray(), new[] { 1, ids.Count });
            using var decOut = decoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            });
            var logits = decOut.First().AsTensor<float>(); // [1, len, vocab]
            int last = ids.Count - 1;
            int vocabSize = logits.Dimensions[2];
            int best = 0; float bestV = float.MinValue;
            for (int v = 0; v < vocabSize; v++)
            {
                float lv = logits[0, last, v];
                if (lv > bestV) { bestV = lv; best = v; }
            }
            if (best == 3) break; // [SEP]
            ids.Add(best);
        }

        var sb = new System.Text.StringBuilder();
        foreach (var id in ids.Skip(1))
        {
            if (id < 5 || id >= _vocab.Length) continue; // 특수 토큰 제외
            var tok = _vocab[id];
            if (tok.StartsWith("##")) tok = tok[2..];
            if (tok.StartsWith("<unused")) continue;
            sb.Append(tok);
        }
        return sb.ToString().Trim();
    }
}
