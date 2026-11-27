using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>글자 읽기 엔진의 공통 꼴 — 말풍선 블록의 조각(BGR)들을 받아 글을 돌려준다(넣은 순서 그대로, 못 읽은 조각은 "").</summary>
public interface ITextRecognizer
{
    bool IsReady { get; }
    string[] RecognizeBatch(IReadOnlyList<Mat> crops);
}

/// <summary>Hayai OCR v2.7 Supernova(ONNX — 원 모델 JustANormalTinkerer/hayai-ocr-v2.7-supernova는 Apache-2.0, 같은 작성자의 변환본)로 말풍선 블록을 통째로 읽는다.
/// SigLIP2 NaFlex 인코더(조각 모양 그대로 16px 패치 128~384개) + 12층 디코더, 일본어·중국어·한국어·영어. 줄 감지 없이 세로·가로·손글씨를 한 번에 읽는다.
///
/// **왜 manga-ocr에서 바꿨나(2026-11-27, 이 앱의 조각 72개·정답 710자, 글자만 견준 오류율)**: manga-ocr 4.93% → Hayai 2.25%.
/// 인쇄체 대사는 비슷하고(6자 → 4자 / 599자) 손글씨·효과음에서 갈린다(11자 → 1자 / 54자: "キューキュー"를 "もーもー", "ちゅ"를 "ちぃ"로 읽던 것).
/// 잘못 읽은 손글씨가 번역에 그대로 들어가던 것이 번역 품질의 한 원인이었다. 평가 방법·수치는 notes/translate-letter.md.
///
/// 모델은 둘이다: prefill(그림 + 시작 토큰 → 첫 글자 점수와 KV 캐시) → decode(앞 토큰 하나 + 캐시 → 다음 글자 점수와 늘어난 캐시)를 끝 토큰까지 되풀이한다.
/// **prefill은 조각마다 따로, decode만 한 쪽을 묶어 돌린다** — 시간의 대부분은 prefill의 그림 인코더이고(CPU에서 조각마다 260ms쯤) 묶으면 큰 조각에 맞춘 채움 때문에 오히려 느려진다
/// (실측, 크기가 섞인 32조각: 조각마다 261ms → 16개 묶음 323ms → 32개 묶음 388ms). decode는 묶을수록 싸다(조각마다 129ms → 30ms).
/// 글이 끝난 조각은 묶음에서 뺀다(캐시에서 그 줄을 걷어 낸다) — 긴 글 하나가 나머지 조각을 끝까지 끌고 가지 않게.
///
/// **CPU에서만 돈다(GPU를 쓰지 않는다 — 시험하고 접었다, 2026-11-27 RTX 2070 SUPER · onnxruntime 1.24.4 DirectML)**:
/// ① 받은 파일 그대로는 DirectML이 실행하지 못한다(Reshape 292개의 allowzero=1 — "running Reshape node 'node_view'" 8007023E. 파일의 그 바이트를 0으로 고치면 돈다).
/// ② 반정밀도(fp16) 파일은 GPU에서 **오류 없이 틀린 글**을 냈다(조각 83개 가운데 4개가 구절을 되풀이: "捨てないで〜!!捨てないで〜!!"). 전정밀도는 83개가 CPU와 같았다.
/// ③ 한 쪽을 한 묶음(32조각)으로 돌리면 decode가 GPU를 멈추게 했다(887A0006, 그래프 융합을 꺼도 같다) — 장치가 내려가 글자 감지기·LaMa까지 그 작업 내내 CPU로 떨어지고(다섯 쪽 95초 → 137초),
///    멈추기 직전의 묶음은 **전부 빈 글**로 나왔다(GPU가 0만 돌려줬다 — 오류는 다음 실행에서야 났다).
/// ④ 멈추지 않는 작은 묶음(패치 한도별 16조각)으로는 실제 쪽에서 블록마다 186~262ms — CPU보다 나을 것이 없다(되풀이 한 단계가 GPU에서 30ms쯤 든다: 단계마다 입력 크기가 달라진다).
/// 얻는 것이 없고 잃을 것(다른 모델의 GPU, 틀린 글)이 크다. 다시 시도한다면 이 네 가지를 먼저 본다.
///
/// **속도(Ryzen 5 3600, 반정밀도 파일)**: 조각마다 206~252ms(조각 294개·83개). manga-ocr는 GPU에서 52~107ms, CPU에서 262~438ms —
/// GPU로 manga-ocr를 돌리던 PC에서는 글자 읽기가 블록마다 0.1~0.2초 느려지고(다섯 쪽 62블록: 7.2초 → 20.1초, 전체 95초 → 110초), CPU로 돌리던 PC에서는 같거나 빠르다.
/// 더 줄일 길(재 둠, 넣지 않음): 패치 한도를 128로 낮추면 조각마다 139ms이고 16자 이하의 글(블록의 88%)은 읽은 것이 거의 같다(259개 가운데 5개가 작은 っ 하나 차이).
/// 17자 넘는 글은 갈린다(26개 가운데 11개) — 작성자의 한도 고르기를 그대로 둔다. 진단 NEKO_HAYAI_PATCHES로 다시 잴 수 있다.</summary>
public class HayaiOcr(OnnxEnv env) : ITextRecognizer
{
    public const string PrefillFile = "hayai_prefill.onnx";
    public const string DecodeFile = "hayai_decode.onnx";
    public const string PosFile = "hayai_pos.npy";
    public const string TokenizerFile = "hayai_tokenizer.json";
    public static readonly string[] Files = { PrefillFile, DecodeFile, PosFile, TokenizerFile };

    // 모델의 특수 토큰 번호(hayai_onnx_config.json) — 글자표의 어휘(0~15999) 밖이다
    internal const int Pad = 16000, Bos = 16001, Eos = 16002;
    /// <summary>조각 하나에서 내는 토큰 수의 상한(원본 max_new_tokens) — 끝 토큰을 내지 않고 되풀이에 빠진 조각이 한없이 돌지 않게.</summary>
    internal const int MaxNewTokens = 128;
    /// <summary>글자 내기 되풀이를 한 번에 묶는 조각 수 — 보통의 한 쪽(10~25블록)이 한 묶음에 든다(원본의 기본값과 같다).</summary>
    private const int BatchSize = 32;
    /// <summary>KV 캐시의 꼴 [층, 묶음, KV 머리, 자리, 차원]의 고정된 축.</summary>
    private const int Layers = 12, Heads = 2, HeadDim = 64;

    private static readonly string[] PrefillInputs = { "pixel_values", "pos_embeds", "spatial_shapes", "bos_ids", "l_vis_arange" };
    private static readonly string[] PrefillOutputs = { "logits", "k_cache", "v_cache", "vis_valid" };
    private static readonly string[] DecodeInputs = { "input_ids", "past_attention_mask", "position_id", "k_cache_in", "v_cache_in" };
    private static readonly string[] DecodeOutputs = { "logits", "k_cache_out", "v_cache_out" };

    private readonly object _load = new();
    private HayaiTokenizer? _tokenizer;
    private float[]? _posGrid;
    private int _posSide, _posDim;
    private readonly ConcurrentDictionary<(int Nh, int Nw), float[]> _posCache = new();

    public bool IsReady => Files.All(env.ModelExists);

    /// <summary>블록 조각(BGR)을 읽어 글을 돌려준다. 빈 결과면 "".</summary>
    public string Recognize(Mat crop) => RecognizeBatch([crop])[0];

    /// <summary>블록 조각 여러 장을 읽는다. 돌려주는 순서는 넣은 순서와 같다.</summary>
    public string[] RecognizeBatch(IReadOnlyList<Mat> crops)
    {
        EnsureLoaded();
        var items = crops.Select(Prepare).ToArray();
        var result = new string[crops.Count];
        foreach (var batch in Batches(items.Select(p => (p.VisTokens, p.Nh * p.Nw)).ToArray(), BatchSize))
        {
            var texts = Run(batch.Select(i => items[i]).ToArray());
            for (int k = 0; k < batch.Length; k++) result[batch[k]] = texts[k];
        }
        return result;
    }

    /// <summary>묶음 짜기: 작은 조각부터(시각 토큰 수 → 패치 수) <paramref name="batchSize"/>개씩 — 한 쪽은 보통 한 묶음이고, 넘치면 크기가 비슷한 것끼리 갈린다(캐시의 채움이 적게).
    /// 돌려주는 것은 묶음마다 조각 번호들(넣은 순서의 번호).</summary>
    internal static int[][] Batches(IReadOnlyList<(int VisTokens, int Patches)> items, int batchSize) =>
        Enumerable.Range(0, items.Count).OrderBy(i => items[i].VisTokens).ThenBy(i => items[i].Patches).Chunk(batchSize).ToArray();

    /// <summary>가장 높은 점수의 토큰을 잇는다. <paramref name="firstLogits"/> = prefill이 낸 첫 글자 점수(batch × 어휘).
    /// <paramref name="decode"/>(아직 안 끝난 줄들이 방금 고른 토큰, 그 줄 번호들(오름차순), 단계 번호 1~) = 그 줄들의 다음 글자 점수(줄 수 × 어휘, 같은 차례).
    /// 끝·채움 토큰이 나온 줄은 끝난 것이고 다음 단계부터 넘기지 않는다 — 원본(_run_batch)은 끝난 줄에 채움 토큰을 넣으며 끝까지 같이 돌리지만, 줄끼리는 서로 보지 않으므로 빼도 남은 줄의 결과는 같다.
    /// 돌려주는 줄에는 끝·채움 토큰도 들어 있다 — 글자표가 풀 때 건너뛴다.</summary>
    internal static List<int>[] Greedy(float[] firstLogits, int batch, Func<long[], int[], int, float[]> decode, int maxNewTokens = MaxNewTokens)
    {
        int vocab = firstLogits.Length / batch;
        var rows = new List<int>[batch];
        var active = new List<int>(batch);
        for (int i = 0; i < batch; i++)
        {
            int best = ArgMax(firstLogits.AsSpan(i * vocab, vocab));
            rows[i] = [best];
            if (best != Eos && best != Pad) active.Add(i);
        }
        for (int step = 1; step < maxNewTokens && active.Count > 0; step++)
        {
            var logits = decode(active.Select(i => (long)rows[i][^1]).ToArray(), active.ToArray(), step);
            var still = new List<int>(active.Count);
            for (int k = 0; k < active.Count; k++)
            {
                int best = ArgMax(logits.AsSpan(k * vocab, vocab));
                rows[active[k]].Add(best);
                if (best != Eos && best != Pad) still.Add(active[k]);
            }
            active = still;
        }
        return rows;
    }

    private static int ArgMax(ReadOnlySpan<float> row)
    {
        int best = 0;
        float bestV = float.NegativeInfinity;
        for (int v = 0; v < row.Length; v++)
            if (row[v] > bestV) { bestV = row[v]; best = v; }
        return best;
    }

    /// <summary>조각마다 따로 만든 KV 캐시([층, 1, 머리, 시각 토큰 + 1, 차원] — 끝자리가 시작 토큰)를 한 묶음의 캐시([층, 조각 수, 머리, 가장 긴 시각 토큰 수 + 1, 차원])로 합친다.
    /// 조각의 시각 토큰은 앞에서부터, 시작 토큰은 맨 끝자리에 놓고 그 사이(짧은 조각의 남는 자리)는 0으로 둔다 — 그 자리는 되풀이 때 가림(mask 0)으로 안 보인다.
    /// 원본이 크기 다른 조각을 한 묶음으로 prefill했을 때의 캐시와 같은 짜임이다(시각 토큰 → 채움 → 시작 토큰).</summary>
    internal static float[] MergeCaches(IReadOnlyList<float[]> caches, IReadOnlyList<int> visTokens, int layers, int heads, int headDim)
    {
        int b = caches.Count, longest = visTokens.Max(), slots = longest + 1;
        var merged = new float[layers * b * heads * slots * headDim];
        for (int j = 0; j < b; j++)
        {
            int vis = visTokens[j], own = vis + 1;
            for (int l = 0; l < layers; l++)
            for (int h = 0; h < heads; h++)
            {
                var src = caches[j].AsSpan((l * heads + h) * own * headDim, own * headDim);
                var dst = merged.AsSpan(((l * b + j) * heads + h) * slots * headDim, slots * headDim);
                src[..(vis * headDim)].CopyTo(dst);
                src[(vis * headDim)..].CopyTo(dst[(longest * headDim)..]);
            }
        }
        return merged;
    }

    /// <summary>KV 캐시([층, 줄, 나머지] 행 우선)에서 <paramref name="keep"/>의 줄만 남긴 새 캐시 — keep은 지금 캐시 안의 줄 자리(오름차순).</summary>
    internal static float[] KeepRows(ReadOnlySpan<float> cache, int layers, int rows, int[] keep)
    {
        int block = cache.Length / (layers * rows);
        var kept = new float[layers * keep.Length * block];
        for (int l = 0; l < layers; l++)
        for (int k = 0; k < keep.Length; k++)
            cache.Slice((l * rows + keep[k]) * block, block).CopyTo(kept.AsSpan((l * keep.Length + k) * block, block));
        return kept;
    }

    /// <summary>진단: NEKO_HAYAI_PATCHES=128|192|256|384이면 조각 모양으로 고르지 않고 그 패치 한도로 고정한다(속도·정확도를 견줄 때).</summary>
    private static readonly int? FixedBudget = int.TryParse(Environment.GetEnvironmentVariable("NEKO_HAYAI_PATCHES"), out int fixedBudget) && fixedBudget is 128 or 192 or 256 or 384 ? fixedBudget : null;   // loc:none

    private sealed record Prepared(float[] Patches, int Nh, int Nw)
    {
        public int VisTokens => (Nh + 1) / 2 * ((Nw + 1) / 2);
    }

    private void EnsureLoaded()
    {
        lock (_load)
        {
            if (_tokenizer != null) return;
            var (grid, rows, cols) = NpyFile.ReadFloat2D(env.ModelPath(PosFile));
            int side = (int)Math.Round(Math.Sqrt(rows));
            if (side * side != rows) throw new InvalidDataException(PosFile);
            (_posGrid, _posSide, _posDim) = (grid, side, cols);
            _tokenizer = HayaiTokenizer.Load(env.ModelPath(TokenizerFile));
        }
    }

    /// <summary>조각(BGR) → 패치. 조각 모양에 맞는 패치 한도를 고르고 그 크기로 Pillow BILINEAR 리사이즈한다(<see cref="HayaiPrep"/>).</summary>
    private static Prepared Prepare(Mat crop)
    {
        Mat? converted = null;
        if (crop.Type() != MatType.CV_8UC3)
        {
            converted = new Mat();
            Cv2.CvtColor(crop, converted, crop.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        }
        try
        {
            var bgr = converted ?? crop;
            int w = bgr.Width, h = bgr.Height;
            var rgb = new byte[w * h * 3];
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)bgr.Ptr(y);
                    int o = y * w * 3;
                    for (int x = 0; x < w; x++, o += 3)
                    {
                        rgb[o] = row[x * 3 + 2];
                        rgb[o + 1] = row[x * 3 + 1];
                        rgb[o + 2] = row[x * 3];
                    }
                }
            }
            int budget = FixedBudget ?? HayaiPrep.SelectBudget(h, w).Budget;
            var (th, tw, nh, nw) = HayaiPrep.TargetSize(h, w, budget);
            var scaled = (tw, th) == (w, h) ? rgb : HayaiPrep.Bilinear(rgb, w, h, tw, th);
            var patches = new float[nh * nw * HayaiPrep.PatchDim];
            HayaiPrep.WritePatches(scaled, tw, th, patches);
            return new Prepared(patches, nh, nw);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private string[] Run(Prepared[] part)
    {
        int b = part.Length, dim = _posDim, lVis = part.Max(p => p.VisTokens);
        using var runOptions = new RunOptions();

        // ── 그림 인코더 + 첫 글자(prefill): 조각마다 따로 ──
        float[]? first = null;
        int vocab = 0;
        var ks = new float[b][];
        var vs = new float[b][];
        var visValid = new long[b * lVis];   // [b, lVis] — 줄마다 실제 시각 토큰 자리(나머지는 0 = 채움)
        for (int j = 0; j < b; j++)
        {
            var p = part[j];
            int valid = p.Nh * p.Nw;
            int nKeep = Math.Max(HayaiPrep.PadMultiple, (valid + HayaiPrep.PadMultiple - 1) / HayaiPrep.PadMultiple * HayaiPrep.PadMultiple);
            var pixels = new float[nKeep * HayaiPrep.PatchDim];
            var pos = new float[nKeep * dim];
            p.Patches.CopyTo(pixels, 0);
            _posCache.GetOrAdd((p.Nh, p.Nw), k => HayaiPrep.PosEmbed(_posGrid!, _posSide, dim, k.Nh, k.Nw)).CopyTo(pos, 0);
            var arange = new long[p.VisTokens];
            for (int i = 0; i < arange.Length; i++) arange[i] = i;
            using var pixelsV = OrtValue.CreateTensorValueFromMemory(pixels, [1, nKeep, HayaiPrep.PatchDim]);
            using var posV = OrtValue.CreateTensorValueFromMemory(pos, [1, nKeep, dim]);
            using var shapesV = OrtValue.CreateTensorValueFromMemory(new long[] { p.Nh, p.Nw }, [1, 2]);
            using var bosV = OrtValue.CreateTensorValueFromMemory(new long[] { Bos }, [1, 1]);
            using var arangeV = OrtValue.CreateTensorValueFromMemory(arange, [arange.Length]);
            // CPU에 고정한 세션(cpuOnly) — GPU는 쓰지 않는다(클래스 주석)
            using var outs = env.Get(PrefillFile, cpuOnly: true).Run(runOptions, PrefillInputs, [pixelsV, posV, shapesV, bosV, arangeV], PrefillOutputs);
            var logits = outs[0].GetTensorDataAsSpan<float>();
            first ??= new float[b * (vocab = logits.Length)];
            logits.CopyTo(first.AsSpan(j * vocab));
            ks[j] = outs[1].GetTensorDataAsSpan<float>().ToArray();
            vs[j] = outs[2].GetTensorDataAsSpan<float>().ToArray();
            outs[3].GetTensorDataAsSpan<long>().CopyTo(visValid.AsSpan(j * lVis));
        }

        // ── 글자 내기 되풀이(decode): 한 묶음으로. 조각마다 길이가 다른 캐시를 가장 긴 것에 맞춰 합친다 ──
        var lengths = part.Select(p => p.VisTokens).ToArray();
        float[]? k = MergeCaches(ks, lengths, Layers, Heads, HeadDim), v = MergeCaches(vs, lengths, Layers, Heads, HeadDim);
        IDisposableReadOnlyCollection<OrtValue>? state = null;   // 앞 단계의 출력(점수 + 늘어난 캐시) — 첫 단계 전에는 없다(합친 캐시 k·v를 쓴다)
        var inCache = Enumerable.Range(0, b).ToArray();          // 캐시에 든 줄들(조각 번호, 오름차순)
        try
        {
            var rows = Greedy(first!, b, (ids, active, step) =>
            {
                // 지금까지의 자리 = 시각 토큰 + 이미 넣은 토큰(시작 토큰 + 앞 단계들) — 넣은 토큰 자리는 늘 1
                int n = active.Length, past = lVis + step;
                var mask = new long[n * past];
                for (int a = 0; a < n; a++)
                {
                    Array.Copy(visValid, active[a] * lVis, mask, a * past, lVis);
                    Array.Fill(mask, 1L, a * past + lVis, step);
                }
                // 끝난 줄이 생겼으면 캐시에서 그 줄을 걷어 내고 남은 줄만 돌린다
                var keep = n == inCache.Length ? null : active.Select(i => Array.IndexOf(inCache, i)).ToArray();
                float[]? kNow = k, vNow = v;
                if (state != null && keep != null)
                {
                    kNow = KeepRows(state[1].GetTensorDataAsSpan<float>(), Layers, inCache.Length, keep);
                    vNow = KeepRows(state[2].GetTensorDataAsSpan<float>(), Layers, inCache.Length, keep);
                }
                else if (state == null && keep != null)
                {
                    kNow = KeepRows(k, Layers, inCache.Length, keep);
                    vNow = KeepRows(v, Layers, inCache.Length, keep);
                }
                else if (state != null) kNow = vNow = null;   // 앞 단계의 출력을 그대로 넣는다(복사 없음)
                inCache = active;

                long[] shape = [Layers, n, Heads, past, HeadDim];
                using var idsV = OrtValue.CreateTensorValueFromMemory(ids, [n, 1]);
                using var maskV = OrtValue.CreateTensorValueFromMemory(mask, [n, past]);
                using var stepV = OrtValue.CreateTensorValueFromMemory(new long[] { step }, [1]);
                using var kV = kNow == null ? null : OrtValue.CreateTensorValueFromMemory(kNow, shape);
                using var vV = vNow == null ? null : OrtValue.CreateTensorValueFromMemory(vNow, shape);
                var prev = state;
                state = env.Get(DecodeFile, cpuOnly: true).Run(runOptions, DecodeInputs, [idsV, maskV, stepV, kV ?? prev![1], vV ?? prev![2]], DecodeOutputs);
                prev?.Dispose();
                k = v = null;   // 합친 캐시는 첫 단계에서만 쓴다
                return state[0].GetTensorDataAsSpan<float>().ToArray();
            });

            // 깨진 바이트(U+FFFD)는 글이 아니다 — 번역기에 보내지 않는다
            return rows.Select(r => _tokenizer!.Decode(r).Replace("\uFFFD", "").Trim()).ToArray();
        }
        finally
        {
            state?.Dispose();
        }
    }
}
