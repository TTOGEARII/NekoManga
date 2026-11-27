using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>manga-ocr-nar-small의 입력 만들기 — 모델 저장소의 preprocess.py를 그대로 옮긴 것.
/// 조각을 가로세로 비율을 지킨 채 14px 패치 324개 안에 드는 가장 큰 크기로 맞추고(Pillow BICUBIC), 패치 격자의 왼쪽 위에 놓고,
/// 남는 자리는 리사이즈한 그림의 테두리 픽셀 가운뎃값으로 채운 뒤 CLIP 평균·표준편차로 정규화해 패치로 편다.
/// 원본과 어긋나면 모델은 학습 때와 다른 입력을 받는다(오류 없이 읽기만 나빠진다) — 시험(NarOcrTests)이 원본의 값으로 붙든다.</summary>
internal static class NarPrep
{
    public const int Budget = 324, Patch = 14, Feat = Patch * Patch * 3;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// <summary>패치 격자(행·열)와 리사이즈할 크기. 배율은 원본처럼 이분 탐색 60번으로 찾는다(같은 배정밀도 연산 — 경계에서 한 패치가 갈리지 않게).</summary>
    public static (int Gh, int Gw, int Rw, int Rh) ResizedSize(int height, int width)
    {
        double lo = 1e-4, hi = 1000.0;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Math.Ceiling(height * mid / Patch) * Math.Ceiling(width * mid / Patch) <= Budget) lo = mid;
            else hi = mid;
        }
        int gh = (int)Math.Ceiling(height * lo / Patch), gw = (int)Math.Ceiling(width * lo / Patch);
        // 파이썬의 round()는 반올림 자리가 딱 절반이면 짝수 쪽으로 간다 — Math.Round의 기본과 같다
        int rw = Math.Max(1, Math.Min(gw * Patch, (int)Math.Round(width * lo)));
        int rh = Math.Max(1, Math.Min(gh * Patch, (int)Math.Round(height * lo)));
        return (gh, gw, rw, rh);
    }

    /// <summary>RGB 8비트 조각을 모델이 볼 크기로 리사이즈한다(Pillow BICUBIC과 같은 바이트 — <see cref="PilResample"/>).</summary>
    public static byte[] Resize(byte[] rgb, int w, int h, out int gh, out int gw, out int rw, out int rh)
    {
        (gh, gw, rw, rh) = ResizedSize(h, w);
        return PilResample.Bicubic(rgb, w, h, rw, rh);
    }

    /// <summary>리사이즈한 그림의 테두리(윗줄·아랫줄·왼쪽 칸·오른쪽 칸 — 모서리는 두 번) 픽셀의 채널별 가운뎃값. 수가 늘 짝수라 가운데 둘의 평균이고 소수는 버린다(원본의 astype(uint8)).</summary>
    private static byte[] BorderMedian(byte[] small, int rw, int rh)
    {
        var median = new byte[3];
        var values = new byte[2 * rw + 2 * rh];
        for (int ch = 0; ch < 3; ch++)
        {
            int k = 0;
            for (int x = 0; x < rw; x++) values[k++] = small[x * 3 + ch];
            for (int x = 0; x < rw; x++) values[k++] = small[((rh - 1) * rw + x) * 3 + ch];
            for (int y = 0; y < rh; y++) values[k++] = small[y * rw * 3 + ch];
            for (int y = 0; y < rh; y++) values[k++] = small[(y * rw + rw - 1) * 3 + ch];
            Array.Sort(values);
            median[ch] = (byte)((values[k / 2 - 1] + values[k / 2]) / 2);
        }
        return median;
    }

    /// <summary>조각(RGB 8비트, 행 우선)을 모델 입력으로 편다: p [324 × 588] 패치(패치 안은 채널 → 줄 → 칸), v [324] 쓰는 패치 1·나머지 0,
    /// r·c [324] 패치의 줄·칸 번호. 쓰지 않는 뒤쪽은 전부 0으로 덮는다(버퍼를 조각마다 다시 써도 되게). 돌려주는 것은 패치 격자(행·열).</summary>
    public static (int Gh, int Gw) Fill(byte[] rgb, int w, int h, float[] p, float[] v, int[] r, int[] c)
    {
        var small = Resize(rgb, w, h, out int gh, out int gw, out int rw, out int rh);
        var fill = BorderMedian(small, rw, rh);
        var lut = new float[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            lut[ch] = new float[256];
            for (int value = 0; value < 256; value++) lut[ch][value] = (value / 255f - Mean[ch]) / Std[ch];
        }
        int n = gh * gw, plane = Patch * Patch;
        for (int pr = 0; pr < gh; pr++)
        for (int pc = 0; pc < gw; pc++)
        {
            int at = (pr * gw + pc) * Feat;
            for (int y = 0; y < Patch; y++)
            {
                int yy = pr * Patch + y;
                for (int x = 0; x < Patch; x++)
                {
                    int xx = pc * Patch + x, o = y * Patch + x;
                    bool inside = yy < rh && xx < rw;
                    int src = (yy * rw + xx) * 3;
                    for (int ch = 0; ch < 3; ch++) p[at + ch * plane + o] = lut[ch][inside ? small[src + ch] : fill[ch]];
                }
            }
        }
        Array.Clear(p, n * Feat, p.Length - n * Feat);
        for (int i = 0; i < v.Length; i++)
        {
            bool used = i < n;
            v[i] = used ? 1f : 0f;
            r[i] = used ? i / gw : 0;
            c[i] = used ? i % gw : 0;
        }
        return (gh, gw);
    }
}

/// <summary>manga-ocr-nar-small의 글자표(nar_vocabulary_6817.json의 "characters") — 번호가 곧 글자의 자리이고, 글자 수(6,817)가 끝 표시다.</summary>
internal sealed class NarVocab
{
    private readonly string[] _chars;

    private NarVocab(string[] chars) => _chars = chars;

    /// <summary>끝 표시의 번호.</summary>
    public int Eos => _chars.Length;

    /// <summary>글자표를 읽는다. 그 꼴이 아니면(다른 파일·내려받다 깨진 파일) <see cref="InvalidDataException"/> — 조용히 빈 글을 내지 않는다.</summary>
    public static NarVocab Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var chars = doc.RootElement.GetProperty("characters").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            if (chars.Length == 0) throw new InvalidDataException(Path.GetFileName(path));
            return new NarVocab(chars);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException(Path.GetFileName(path), ex);
        }
    }

    /// <summary>모델이 낸 번호들을 글로 푼다 — 첫 끝 표시에서 끊고, 표 밖의 번호는 건너뛴다.</summary>
    public string Decode(ReadOnlySpan<int> ids)
    {
        var sb = new StringBuilder(ids.Length);
        foreach (int id in ids)
        {
            if (id == Eos) break;
            if (id >= 0 && id < _chars.Length) sb.Append(_chars[id]);
        }
        return sb.ToString();
    }
}

/// <summary>manga-ocr-nar-small(muscgab/manga-ocr-nar-small — 일본어 만화 말풍선 전용, 5,400만 매개변수)로 말풍선 블록을 통째로 읽는다.
/// 글자를 하나씩 내는 되풀이가 없다: 조각 하나를 한 번 돌리면 글 전체(65자까지)가 나온다 — 그래서 빠르다.
///
/// **왜 기본 엔진인가(2026-11-27, 이 앱의 조각 72개·정답 710자, 글자만 견준 오류율)**: manga-ocr 4.93% · Hayai v2.7 2.25% · 이 모델 2.11%(손글씨·효과음 54자: 11 · 1 · 2자).
/// Hayai와 정확도가 같고 훨씬 빠르다 — 속도는 아래. 사용자 결정("1번 작업해주고 hayai는 선택할 수 있게").
/// **가중치의 라이선스는 CC BY-NC-SA 4.0(비영리)이다** — 사용자가 비영리 개인 프로젝트로 우리 모델 릴리스에 바꾸지 않고 다시 올렸다(라이선스 전문·고지 포함, ComponentCatalog 주석).
/// 상업적으로 쓰는 사람은 설정에서 Hayai OCR(Apache-2.0)이나 manga-ocr를 고른다.
/// 한 번에 65자까지만 낸다 — 그보다 긴 글(줄이 많은 후기·안내문)이나 조각에 견줘 글이 너무 많은 블록은 글 전체가 깨지므로, 그런 읽기만 줄 사이에서 갈라 따로 읽는다(<see cref="NarSplit"/>). 일본어만.
///
/// 입력은 조각마다 크기가 고정이라(패치 324개 자리) GPU(DirectML)에서도 돈다. 그래프 융합은 끄고(<see cref="OnnxEnv.Unfused"/> — 융합한 모델이 이 PC의 GPU를 멈추게 한 일이 셋 있다),
/// 프로세스마다 첫 조각 몇 개를 CPU로도 읽어 글이 같을 때만 이후 GPU를 쓴다. 한 쪽을 읽는 사이 GPU에서 내려가면 그 쪽을 CPU로 다시 읽는다(멈추기 직전의 GPU는 오류 없이 쓰레기를 낸다).</summary>
public class NarOcr(OnnxEnv env) : ITextRecognizer
{
    public const string ModelFile = "ocr_nar.onnx";
    public const string VocabFile = "ocr_nar_vocab.json";
    public static readonly string[] Files = { ModelFile, VocabFile };

    /// <summary>GPU에서 돌 때의 세션 이름 — 그래프 융합을 끈다. CPU 전용 대조 세션은 파일 이름 그대로.</summary>
    private static readonly string GpuName = OnnxEnv.Unfused(ModelFile);
    /// <summary>GPU 대조에 쓰는 조각 수의 상한 — 첫 쪽이 커도 이만큼만 CPU로 다시 읽는다.</summary>
    private const int CheckCrops = 4;

    private static readonly string[] Inputs = { "P", "V", "R", "C", "H", "W" };
    private static readonly string[] Outputs = { "ids" };

    /// <summary>이 프로세스에서 GPU(DirectML)가 낸 글이 CPU와 같다고 확인됐는가 — null이면 아직 대조 전.</summary>
    private static bool? _gpuVerified;
    private static readonly object VerifyLock = new();

    private readonly object _load = new();
    private NarVocab? _vocab;

    public bool IsReady => Files.All(env.ModelExists);

    /// <summary>블록 조각(BGR)을 읽어 글을 돌려준다. 빈 결과면 "".</summary>
    public string Recognize(Mat crop) => RecognizeBatch([crop])[0];

    /// <summary>블록 조각 여러 장을 읽는다. 돌려주는 순서는 넣은 순서와 같다.</summary>
    public string[] RecognizeBatch(IReadOnlyList<Mat> crops)
    {
        if (crops.Count == 0) return [];
        lock (_load) _vocab ??= NarVocab.Load(env.ModelPath(VocabFile));
        var items = crops.Select(OcrCrop.ToRgb).ToArray();
        if (_gpuVerified == true || !OnGpu) return Read(items);
        lock (VerifyLock)
        {
            if (_gpuVerified == true || !OnGpu) return Read(items);
            var probe = items[..CheckCount(items.Length)];
            var (texts, same) = CrossCheck(() => Run(probe, cpuOnly: true), () => Run(probe, cpuOnly: false));
            same &= OnGpu;   // 대조하는 사이 실행 오류로 CPU에 내려갔으면(Guard) 확인된 것이 아니다
            _gpuVerified = same;
            if (same) env.Release(ModelFile, cpuOnly: true);   // 대조에만 쓴 CPU 세션
            else env.Demote(GpuName);                          // 이후 Get은 CPU 세션을 준다
            if (Environment.GetEnvironmentVariable("NEKO_ONNX_DBG") == "1")
                Console.Error.WriteLine($"[onnx] {ModelFile}: GPU 대조({probe.Length}조각) {(same ? "일치 — 이후 GPU" : "불일치 — CPU로 내려요")}");   // loc:none
            // 대조한 것이 전부면 CPU의 글을 그대로 쓰고, 일부였으면 마저 읽는다(확인된 GPU로, 아니면 방금 내린 CPU로)
            return probe.Length == items.Length ? texts : Read(items);
        }
    }

    private bool OnGpu => env.UsesGpu(GpuName);

    private string[] Read((byte[] Rgb, int W, int H)[] items) => RedoIfDemoted(() => OnGpu, () => Run(items, cpuOnly: false));

    /// <summary>GPU 대조에 쓰는 조각 수 — 앞에서부터 <see cref="CheckCrops"/>개까지.</summary>
    internal static int CheckCount(int crops) => Math.Min(crops, CheckCrops);

    /// <summary>같은 조각들을 CPU와 GPU로 읽어 견준다 — 돌려주는 글은 늘 CPU의 것, <c>Same</c>은 GPU의 글이 조각마다 같을 때만 true(GPU 실행이 던져도 false).</summary>
    internal static (string[] Texts, bool Same) CrossCheck(Func<string[]> onCpu, Func<string[]> onGpu)
    {
        var cpu = onCpu();
        bool same = false;
        try { same = onGpu().SequenceEqual(cpu); }
        catch { /* GPU 실행이 던졌다 — 호출부가 CPU로 내린다 (TextDetector의 대조와 같다) */ }
        return (cpu, same);
    }

    /// <summary>한 쪽을 읽는 사이 GPU에서 내려갔으면(실행 오류 → Guard가 CPU로) 그 쪽을 처음부터 다시 읽는다 — 멈추기 직전의 GPU가 낸 글은 믿을 수 없다.</summary>
    internal static string[] RedoIfDemoted(Func<bool> onGpu, Func<string[]> read)
    {
        bool before = onGpu();
        var texts = read();
        return before && !onGpu() ? read() : texts;
    }

    private string[] Run((byte[] Rgb, int W, int H)[] items, bool cpuOnly)
    {
        var p = new float[NarPrep.Budget * NarPrep.Feat];
        var v = new float[NarPrep.Budget];
        var r = new int[NarPrep.Budget];
        var c = new int[NarPrep.Budget];
        int[] gh = new int[1], gw = new int[1];
        // 입력 버퍼는 조각마다 다시 쓴다(텐서는 이 배열들을 그대로 본다)
        using var runOptions = new RunOptions();
        using var pV = OrtValue.CreateTensorValueFromMemory(p, [NarPrep.Budget, NarPrep.Feat]);
        using var vV = OrtValue.CreateTensorValueFromMemory(v, [NarPrep.Budget]);
        using var rV = OrtValue.CreateTensorValueFromMemory(r, [NarPrep.Budget]);
        using var cV = OrtValue.CreateTensorValueFromMemory(c, [NarPrep.Budget]);
        using var hV = OrtValue.CreateTensorValueFromMemory(gh, [1]);
        using var wV = OrtValue.CreateTensorValueFromMemory(gw, [1]);
        OrtValue[] values = [pV, vV, rV, cV, hV, wV];

        // 조각 하나(또는 긴 글을 가른 토막 하나)를 한 번 돌린다 — 65자 안에 끝 표시가 없으면 넘친 것이다(깨진 읽기인지는 NarSplit이 글을 보고 가린다)
        (string Text, bool Overflow) ReadOne(byte[] rgb, int w, int h)
        {
            (gh[0], gw[0]) = NarPrep.Fill(rgb, w, h, p, v, r, c);
            // Guard: GPU에서 실행 오류가 나면 CPU로 내려 한 번 더 (OnnxEnv.Guard). cpuOnly(GPU 대조용)는 CPU에 고정한 세션으로 돈다
            using var outs = cpuOnly
                ? env.Get(ModelFile, cpuOnly: true).Run(runOptions, Inputs, values, Outputs)
                : env.Guard(() => env.Get(GpuName).Run(runOptions, Inputs, values, Outputs), GpuName);
            var ids = outs[0].GetTensorDataAsSpan<int>();
            return (_vocab!.Decode(ids), ids.IndexOf(_vocab.Eos) < 0);
        }

        var texts = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
            texts[i] = NarSplit.Read(items[i].Rgb, items[i].W, items[i].H, ReadOne).Trim();
        return texts;
    }
}
