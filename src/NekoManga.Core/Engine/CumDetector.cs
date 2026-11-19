using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>질내사정의 자동 판단 — WD 태거(SmilingWolf/wd-v1-4-moat-tagger-v2, Apache-2.0)를 검열 조각 둘레의 자른 그림에 돌려 사정 계열 태그의 점수를 본다.
/// 2026-11-16 사용자 "여자거에서 구멍에 전부다 질내사정으로 나오는데 실제 이미지에서 질내사정한 부분만": 부위 감지 모델(nipple·penis·pussy)에는 그런 분류가 없고,
/// 밝기·모양 규칙(선으로 닫힌 작은 흰 덩어리 세기)은 사용자 원고 18쪽의 조각 124개에서 조각마다 0~84개로 흩어져 가릴 수 없었다(흰 검열·컷·말풍선이 다 걸린다).
/// 태거는 같은 조각들에서 cum_in_pussy를 0.00~0.77로 갈랐다(정답 라벨은 없다 — 그래서 '자동 판단'은 결과 창에 표시로 보이고 사용자가 고칠 수 있다).
///
/// 입력: 조각 가운데의 정사각 창(한 변 = max(448, 조각의 긴 변×배수), 그림보다 크면 그림의 짧은 변)을 448×448로(줄일 때 Area, 키울 때 Cubic), BGR 0~255 float, NHWC.
/// 출력: [1, 9083] 시그모이드 점수(등급 4 + 일반·캐릭터 태그, selected_tags.csv의 줄 순서). 창을 조각의 3배·5배 둘로 보고 높은 쪽을 쓴다 — 창 크기에 따라 점수가 크게 달랐다
/// (같은 조각이 3배 0.36 / 5배 0.77, 반대로 0.45 / 0.07).
/// 모델 파일: 런타임의 <see cref="ModelFile"/>(구성요소), 없으면 연결된 ComfyUI의 WD14 노드가 받아 둔 같은 파일(<see cref="ComfyNodeModel"/> — SHA-256이 같다).</summary>
public class CumDetector(OnnxEnv env, string? comfyRoot = null)
{
    public const string ModelFile = "wdtagger.onnx";
    public const string ComfyNodeModel = "wd-v1-4-moat-tagger-v2.onnx";
    internal const int Size = 448;
    /// <summary>보는 태그의 출력 번호(selected_tags.csv에서 머리 줄을 뺀 줄 번호) — cum_in_pussy, cumdrip, overflow, after_vaginal, internal_cumshot.
    /// 'cum' 하나만으로는 보지 않는다: 남성 쪽의 사정·애액에도 높게 나온다(같은 원고에서 남성 조각 0.88).</summary>
    internal static readonly int[] Tags = [370, 978, 1265, 1762, 2483];
    internal static readonly double[] WindowScales = [3, 5];

    /// <summary>쓸 모델(런타임의 파일 이름, 또는 ComfyUI 노드 폴더의 전체 경로 — OnnxEnv는 전체 경로도 그대로 연다). 없으면 null.</summary>
    public string? Model { get; } = Resolve(env, comfyRoot);

    public bool IsReady => Model != null;

    private static string? Resolve(OnnxEnv env, string? comfyRoot)
    {
        if (env.ModelExists(ModelFile)) return ModelFile;
        return ComfyNodeCopy(comfyRoot);
    }

    /// <summary>연결된 ComfyUI의 WD14 노드가 받아 둔 같은 모델의 경로 — 없으면 null. 구성요소의 설치 판정(ComponentInstaller.IsInstalled)도 이것을 본다(있으면 다시 받지 않는다).</summary>
    public static string? ComfyNodeCopy(string? comfyRoot)
    {
        if (string.IsNullOrWhiteSpace(comfyRoot)) return null;
        var inNode = Path.Combine(comfyRoot, "ComfyUI", "custom_nodes", "ComfyUI-WD14-Tagger", "models", ComfyNodeModel);
        return File.Exists(inNode) ? inNode : null;
    }

    /// <summary>조각 둘레에 질내사정의 흔적이 있는 정도(0~1) — 사정 계열 태그 점수의 최댓값을 창 둘에서 재어 높은 쪽. 모델이 없으면 예외(호출자가 IsReady를 먼저 본다).</summary>
    public float Score(Mat bgr, Rect piece)
    {
        var model = Model ?? throw new FileNotFoundException(Loc.F($"엔진 모델이 없다냥: {ModelFile} — 설정 → 구성요소에서 설치해 줘냥."));
        float best = 0; Rect last = default;
        foreach (double k in WindowScales)
        {
            var w = WindowFor(piece, bgr.Size(), k);
            if (w == last) continue;   // 작은 조각은 두 창이 같다(둘 다 448)
            last = w;
            best = Math.Max(best, Run(model, bgr, w));
        }
        return best;
    }

    /// <summary>조각 가운데의 정사각 창 — 한 변 = max(448, 긴 변×<paramref name="k"/>), 그림의 짧은 변을 넘지 않게, 그림 안으로 밀어 넣는다.</summary>
    internal static Rect WindowFor(Rect piece, OpenCvSharp.Size img, double k)
    {
        int side = Math.Min(Math.Max(Size, (int)(Math.Max(piece.Width, piece.Height) * k)), Math.Min(img.Width, img.Height));
        int cx = piece.X + piece.Width / 2, cy = piece.Y + piece.Height / 2;
        return new Rect(Math.Clamp(cx - side / 2, 0, img.Width - side), Math.Clamp(cy - side / 2, 0, img.Height - side), side, side);
    }

    /// <summary>창을 잘라 모델 입력으로 — 448×448×3, BGR 순서, 0~255 float(NHWC 한 줄).</summary>
    internal static float[] Preprocess(Mat img, Rect window)
    {
        using var roi = new Mat(img, window);
        using var bgr = new Mat();
        if (roi.Channels() == 1) Cv2.CvtColor(roi, bgr, ColorConversionCodes.GRAY2BGR);
        else if (roi.Channels() == 4) Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);
        else roi.CopyTo(bgr);
        using var sized = new Mat();
        Cv2.Resize(bgr, sized, new OpenCvSharp.Size(Size, Size), 0, 0, window.Width > Size ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        using var f = new Mat();
        sized.ConvertTo(f, MatType.CV_32FC3);
        var data = new float[Size * Size * 3];
        Marshal.Copy(f.Data, data, 0, data.Length);   // ConvertTo가 새로 만든 Mat이라 이어져 있다
        return data;
    }

    private float Run(string model, Mat img, Rect window)
    {
        var tensor = new DenseTensor<float>(Preprocess(img, window), new[] { 1, Size, Size, 3 });
        // CPU 고정 — 조각마다 0.5초쯤이고, 이 모델의 DirectML 수치는 검증한 적이 없다(부위 감지기와 같은 까닭)
        var session = env.Get(model, cpuOnly: true);
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor) });
        var scores = results.First().AsTensor<float>();
        if (scores.Length <= Tags.Max()) throw new InvalidDataException("wdtagger: unexpected output size " + scores.Length);   // loc:none
        float best = 0;
        foreach (int i in Tags) best = Math.Max(best, scores.GetValue(i));
        return best;
    }
}
