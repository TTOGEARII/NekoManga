using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>검열 마스크와 짝을 이루는 '종류 지도'(CV_8UC1)의 값 — 어느 엔진이 그 자리를 찾았는지를 제거 단계까지 들고 간다.</summary>
public static class CensorKind
{
    public const byte None = 0;
    /// <summary>Aletheia 막대(단색 덮기) → DeepCreamPy bar 모델.</summary>
    public const byte Bar = 1;
    /// <summary>Aletheia 모자이크 → DeepCreamPy mosaic 모델.</summary>
    public const byte Mosaic = 2;
    /// <summary>기존 방식(CensorNet 흰선·규칙 감지기)으로 찾은 자리 → 기존 방식(LaMa)으로 복원.</summary>
    public const byte Legacy = 3;
}

/// <summary>검열 감지·제거의 주력 엔진 (2026-11-09, 사용자 지시 "기존 감지·제거를 Aletheia Lens로 대체, 못 잡는 흰 막대선은 기존 방식으로").
///
/// **감지** = Aletheia Lens의 Mask R-CNN(<see cref="AletheiaDetector"/>: 막대·모자이크) + **기존 방식 보완**:
///   - Auto: CensorNet의 **흰 검출만** 더한다. 실측(mashiro Confusion 흰선 실라벨 78개, 적중 = 라벨의 30% 이상 덮음):
///     Aletheia 51 · CensorNet c11 72 · 합집합 75 — Aletheia는 굵은 흰 막대는 잡지만 가는 흰선 24개를 놓쳤다.
///   - WhiteBar: CensorNet의 흰 검출을 높은 임계(<see cref="AletheiaDecensor.WhiteBarAssistConf"/>)로 더한다. CensorNet이 없으면 규칙 감지기(흰 막대·흰 스트립).
///   - Color: 색 검열 규칙 감지기(채도)를 더한다.
/// 설정의 '무엇을 검열로 볼까요'(DecensorTarget)는 그대로 뜻을 유지한다: BlackBar/WhiteBar/Color는 막대 인스턴스 중 그 색조만,
/// Mosaic은 모자이크 인스턴스만, Auto는 전부.
///
/// **제거(기본 모드)** = 마스크의 4-연결 성분마다 종류 지도의 다수결로 엔진을 고른다: 막대 → DeepCreamPy bar, 모자이크 → DeepCreamPy mosaic,
/// 기존 방식 → LaMa. 종류를 모르는 성분(사용자가 손으로 칠한 곳·옛 사이드카)은 막대 모델 — Aletheia의 수동 복원(repair_manual_bars)과 같다.</summary>
public sealed class AletheiaDecensor(AppSettings settings, OnnxEnv env)
{
    private readonly AletheiaDetector _detector = new(env);
    private readonly AletheiaInpainter _inpainter = new(env);

    public bool DetectorReady => _detector.IsReady;
    public bool InpainterReady => _inpainter.IsReady;

    public const string DetectorMissing = "검열 감지 모델(Aletheia Lens)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.";
    public const string InpainterMissing = "검열 복원 모델(Aletheia Lens)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.";

    private enum Tone { Dark, Bright, Color }

    /// <summary>'흰색 막대만'에서 전용 AI(CensorNet)의 흰 검출을 받는 강한 임계 — 기본 0.40보다 높다(2026-11-11, 사용자 확인).</summary>
    /// <remarks>실측(사용자 설정 경로, 흰선 실라벨 78개 · 검열 없는 흑백 원고 표본 72쪽의 발화):
    /// 규칙 감지 53개 · 69쪽 170만 px / 전용 AI 0.40 73개 · 13쪽 19.5만 px / 0.60 71개 · 10쪽 7.5만 px / 0.75 71개 · 9쪽 6.5만 px(그중 7쪽 2.9만 px은 Aletheia 자체 검출).
    /// 오탐 자리의 확신도가 0.56~0.82라 조금 올려서는 안 걸러진다. 표본 13쪽에 맞춘 값이니 전용 AI를 다시 학습하면 다시 잰다.</remarks>
    internal const float WhiteBarAssistConf = 0.75f;

    /// <summary>'흰색 막대만'의 보완을 전용 AI로 하는가 — 전용 AI가 설치돼 있을 때만. 없으면 예전처럼 규칙 감지기로 보완한다(색 검열은 늘 규칙 감지기).</summary>
    internal static bool WhiteBarAssistUsesNet(string target, bool censorNetReady) => target == "WhiteBar" && censorNetReady;

    /// <summary>검열 마스크(255=지울 곳, DecensorMaskGrow 적용)와 종류 지도를 만든다. 검출이 없으면 둘 다 null.</summary>
    /// <param name="exclude">글자 블록 — 기존 방식 보완(CensorNet 말풍선 필터·규칙 감지기)에만 쓴다. Aletheia 검출에는 원본처럼 필터를 걸지 않는다.</param>
    public Mat? Detect(Mat img, IReadOnlyList<Rect>? exclude, out string summary, out Mat? kinds)
    {
        kinds = null;
        if (!_detector.IsReady) throw new FileNotFoundException(DetectorMissing);
        string target = settings.DecensorTarget;
        int grow = settings.DecensorMaskGrow;
        // **기본(내장) 복원이면 Aletheia가 찾은 자리는 원본과 똑같이 1px(3×3)만 불린다** (2026-11-09, 사용자 "NekoManga는 너무 뭉개진다 — Aletheia가 낫다").
        // 설정의 마스크 확장(기본 8px)까지 더했더니 칠하는 면적이 1.42배가 되고 이웃 막대 415곳 중 119곳이 한 덩어리로 붙어 복원 상자가 커져서
        // (256px 출력을 더 크게 늘려 붙임) 원본보다 흐려졌다 — 원본이 칠한 자리의 라플라시안 분산 5,497 → 3,270 (실측 64쪽).
        // 마스크 확장 설정은 넓은 마스크를 전제하는 쪽(GPU 방식의 ComfyUI 복원, LaMa로 지우는 보완 자리)에만 쓴다.
        bool basic = settings.DecensorMode == "Basic";
        int aleGrow = basic ? 0 : grow;
        int assistGrow = basic && settings.DecensorAssistRepair == "Aletheia" ? 1 : grow;

        using var bar = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        using var mosaic = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        int nBar = 0, nMosaic = 0, nOtherTone = 0;
        var instances = _detector.Detect(img);
        try
        {
            foreach (var inst in instances)
            {
                Mat dst;
                if (inst.Cls == AletheiaDetector.ClassMosaic)
                {
                    if (target is not ("Auto" or "Mosaic")) continue;
                    dst = mosaic; nMosaic++;
                }
                else
                {
                    if (target == "Mosaic") continue;
                    if (target != "Auto")
                    {
                        var want = target switch { "WhiteBar" => Tone.Bright, "Color" => Tone.Color, _ => Tone.Dark };
                        if (ToneOf(img, inst) != want) { nOtherTone++; continue; }
                    }
                    dst = bar; nBar++;
                }
                using var roi = new Mat(dst, inst.Box);
                Cv2.BitwiseOr(roi, inst.Mask, roi);
            }
        }
        finally { foreach (var inst in instances) inst.Dispose(); }

        // 원본은 검출을 3×3으로 한 번 불린다(apply_cover의 dilation=0 → 빈 커널 = OpenCV 기본 3×3).
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using Mat? kg = aleGrow > 0 ? Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(aleGrow * 2 + 1, aleGrow * 2 + 1)) : null;
        foreach (var m in new[] { bar, mosaic })
        {
            if (Cv2.CountNonZero(m) == 0) continue;
            Cv2.Dilate(m, m, k3);
            if (kg != null) Cv2.Dilate(m, m, kg);
        }

        // ── 기존 방식 보완 ──
        Mat? legacy = null;
        string legacyNote = "";
        if (target == "Auto" && settings.DecensorLegacyAssist != "None")
        {
            var net = new CensorNet(env);
            if (net.IsReady)
            {
                bool all = settings.DecensorLegacyAssist == "All";
                legacy = net.BuildMask(img, exclude, assistGrow, out _, bubbleFilter: settings.DecensorBubbleFilter,
                    white: true, black: all, mosaic: all || settings.DecensorLegacyAssist == "WhiteMosaic");
                legacyNote = " + 전용 AI 보완";
            }
            else legacyNote = " (전용 AI 미설치 — 보완 없음)";
        }
        else if (target is "WhiteBar" or "Color")
        {
            // 흰 막대의 보완은 전용 AI의 흰 검출로 한다 — 규칙 흰 막대 감지는 검열 없는 쪽에서도 칸 사이 여백 같은 가는 흰 조각을 수십 곳씩 잡았고,
            // 규칙이 잡는 라벨은 전용 AI가 전부 잡는다(수치는 WhiteBarAssistConf). 전용 AI가 없는 PC에서는 예전처럼 규칙 감지기.
            var net = new CensorNet(env);
            if (WhiteBarAssistUsesNet(target, net.IsReady))
            {
                legacy = net.BuildMask(img, exclude, assistGrow, out _, bubbleFilter: settings.DecensorBubbleFilter,
                    white: true, black: false, mosaic: false, conf: WhiteBarAssistConf);
                legacyNote = " + 전용 AI 흰선";
            }
            else
            {
                var s2 = settings.ShallowClone(); s2.DecensorMaskGrow = assistGrow;
                legacy = MosaicDetector.BuildMaskForTarget(s2, img, exclude, out _);
                legacyNote = $" + 기존 {(target == "WhiteBar" ? "흰 막대" : "색")} 감지";
            }
        }

        // 기본 복원에서는 **Aletheia가 찾은 자리는 Aletheia 것만** 쓴다 — 두 감지기가 같은 막대를 조금 다르게 그리므로 그대로 합치면 그 막대가
        // 넓은 쪽 마스크로 칠해지거나(다수결로 LaMa에 넘어감) 테두리 조각이 따로 남는다. 보완은 Aletheia 검출 둘레를 뺀 나머지에서, 가는 테두리 조각을 걷어낸 것만.
        if (legacy != null && basic)
        {
            using var ale = new Mat();
            Cv2.BitwiseOr(bar, mosaic, ale);
            if (Cv2.CountNonZero(ale) > 0)
            {
                int reach = assistGrow + 3;
                using var kr = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(reach * 2 + 1, reach * 2 + 1));
                using var near = new Mat();
                Cv2.Dilate(ale, near, kr);
                legacy.SetTo(Scalar.Black, near);
                using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                Cv2.MorphologyEx(legacy, legacy, MorphTypes.Open, k5);
                using var lab = new Mat(); using var st = new Mat(); using var ce = new Mat();
                int nc = Cv2.ConnectedComponentsWithStats(legacy, lab, st, ce);
                for (int i = 1; i < nc; i++)
                {
                    if (st.At<int>(i, (int)ConnectedComponentsTypes.Area) >= 150) continue;
                    using var comp = new Mat();
                    Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
                    legacy.SetTo(Scalar.Black, comp);
                }
            }
        }
        if (legacy != null) legacyNote += $" {Cv2.CountNonZero(legacy)}px";

        using (legacy)
        {
            // Aletheia가 찾은 자리가 우선 — 기존 방식 표시는 Aletheia가 안 덮은 곳에만 남는다
            var map = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
            if (legacy != null) map.SetTo(new Scalar(CensorKind.Legacy), legacy);
            map.SetTo(new Scalar(CensorKind.Bar), bar);
            map.SetTo(new Scalar(CensorKind.Mosaic), mosaic);

            var mask = new Mat();
            Cv2.Threshold(map, mask, 0, 255, ThresholdTypes.Binary);
            int px = Cv2.CountNonZero(mask);
            summary = $"Aletheia(막대 {nBar}·모자이크 {nMosaic}{(nOtherTone > 0 ? $"·다른 색조 {nOtherTone} 제외" : "")}){legacyNote} → {px}px";
            if (px == 0)
            {
                mask.Dispose(); map.Dispose();
                return null;
            }
            kinds = map;
            return mask;
        }
    }

    /// <summary>막대 인스턴스의 색조 — 마스크 안 픽셀의 채도·밝기 **중앙값**으로 본다(마스크가 막대 밖 그림으로 조금 번져도 흔들리지 않게).
    /// 실측(mashiro Confusion 34쪽, 막대 138개): 밝기 중앙값이 흰 막대 247~255·검은 막대 40~49로 완전히 갈린다.</summary>
    private static Tone ToneOf(Mat imgBgr, AletheiaDetector.Instance inst)
    {
        using var crop = new Mat(imgBgr, inst.Box);
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        var hs = new int[256]; var hg = new int[256];
        int total = 0;
        unsafe
        {
            for (int y = 0; y < crop.Rows; y++)
            {
                byte* pm = (byte*)inst.Mask.Ptr(y), ph = (byte*)hsv.Ptr(y), pgr = (byte*)gray.Ptr(y);
                for (int x = 0; x < crop.Cols; x++)
                {
                    if (pm[x] == 0) continue;
                    hs[ph[x * 3 + 1]]++; hg[pgr[x]]++; total++;
                }
            }
        }
        if (total == 0) return Tone.Dark;
        if (Median(hs, total) >= 80) return Tone.Color;
        return Median(hg, total) >= 128 ? Tone.Bright : Tone.Dark;
    }

    private static int Median(int[] hist, int total)
    {
        int acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc * 2 >= total) return v;
        }
        return 255;
    }

    /// <summary>mask(8UC1, 0이 아닌 곳 = 지울 곳)를 성분별로 맞는 엔진에 나눠 img(BGR)를 제자리에서 복원한다.
    /// 순서: 막대(DeepCreamPy bar) → 모자이크(DeepCreamPy mosaic) → 기존 방식(LaMa). 무엇을 몇 군데 돌렸는지 요약을 돌려준다.</summary>
    /// <param name="kinds">종류 지도(없으면 전부 막대 모델). 크기가 다르면 마스크 크기로 맞춘다.</param>
    public string Repair(Mat img, Mat mask, Mat? kinds, LamaInpainter lama)
    {
        if (!_inpainter.IsReady) throw new FileNotFoundException(InpainterMissing);
        if (mask.Type() != MatType.CV_8UC1 || mask.Size() != img.Size()) throw new ArgumentException("마스크는 그림과 같은 크기의 8비트 한 채널이어야 해요.", nameof(mask));
        using var bin = new Mat();
        Cv2.Threshold(mask, bin, 0, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        int n = Cv2.ConnectedComponents(bin, labels, PixelConnectivity.Connectivity4, MatType.CV_32S);
        if (n <= 1) return "지울 곳 없음";

        Mat? km = null;
        if (kinds != null && !kinds.Empty())
        {
            km = kinds;
            if (kinds.Size() != mask.Size())
            {
                km = new Mat();
                Cv2.Resize(kinds, km, mask.Size(), 0, 0, InterpolationFlags.Nearest);
            }
        }
        try
        {
            // 성분별 종류 다수결 (4-연결 = 복원기가 나누는 성분과 같다)
            var counts = new int[n, 4];
            var area = new int[n];
            unsafe
            {
                for (int y = 0; y < bin.Rows; y++)
                {
                    int* pl = (int*)labels.Ptr(y);
                    byte* pk = km == null ? null : (byte*)km.Ptr(y);
                    for (int x = 0; x < bin.Cols; x++)
                    {
                        int l = pl[x];
                        if (l == 0) continue;
                        area[l]++;
                        if (pk != null && pk[x] is > 0 and <= CensorKind.Legacy) counts[l, pk[x]]++;
                    }
                }
            }
            var kindOf = new byte[n];
            int nBar = 0, nMosaic = 0, nLegacy = 0;
            for (int l = 1; l < n; l++)
            {
                int cb = counts[l, CensorKind.Bar], cm = counts[l, CensorKind.Mosaic], cl = counts[l, CensorKind.Legacy];
                // 표시가 성분의 30%도 안 되면 사용자가 새로 칠한 자리로 보고 막대 모델(Aletheia 수동 복원과 같다)
                byte k = CensorKind.Bar;
                if ((cb + cm + cl) * 10 >= area[l] * 3)
                    k = cb >= cm && cb >= cl ? CensorKind.Bar : cm >= cl ? CensorKind.Mosaic : CensorKind.Legacy;
                // 설정 '보완으로 찾은 자리도 Aletheia로 지우기' — 기존 방식 표시 성분을 막대 모델로 돌린다
                if (k == CensorKind.Legacy && settings.DecensorAssistRepair == "Aletheia") k = CensorKind.Bar;
                kindOf[l] = k;
                if (k == CensorKind.Bar) nBar++; else if (k == CensorKind.Mosaic) nMosaic++; else nLegacy++;
            }

            using var barM = new Mat(bin.Rows, bin.Cols, MatType.CV_8UC1, Scalar.Black);
            using var mosaicM = new Mat(bin.Rows, bin.Cols, MatType.CV_8UC1, Scalar.Black);
            using var legacyM = new Mat(bin.Rows, bin.Cols, MatType.CV_8UC1, Scalar.Black);
            unsafe
            {
                for (int y = 0; y < bin.Rows; y++)
                {
                    int* pl = (int*)labels.Ptr(y);
                    byte* pb = (byte*)barM.Ptr(y), pm = (byte*)mosaicM.Ptr(y), pg = (byte*)legacyM.Ptr(y);
                    for (int x = 0; x < bin.Cols; x++)
                    {
                        int l = pl[x];
                        if (l == 0) continue;
                        (kindOf[l] == CensorKind.Bar ? pb : kindOf[l] == CensorKind.Mosaic ? pm : pg)[x] = 255;
                    }
                }
            }

            if (nBar > 0) _inpainter.Repair(img, barM, mosaic: false);
            if (nMosaic > 0) _inpainter.Repair(img, mosaicM, mosaic: true);
            if (nLegacy > 0)
            {
                if (!lama.IsReady)
                    throw new FileNotFoundException("복원 모델(LaMa)이 없어요 — 설정 → 구성요소에서 설치해 주세요.");
                lama.Inpaint(img, legacyM);
            }
            return $"막대 {nBar}·모자이크 {nMosaic}·기존 방식(LaMa) {nLegacy}";
        }
        finally { if (km != null && !ReferenceEquals(km, kinds)) km.Dispose(); }
    }
}
