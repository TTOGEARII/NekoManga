using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary>SDXL에 보낼 창 하나 — 그림 좌표의 창(<see cref="RegenWindow"/>)과 **이 창이 맡은 구멍**(창 크기의 마스크, 255 = 다시 그릴 자리).
/// 창은 1024라 다른 묶음의 구멍이 걸쳐 들어오는데, 그것까지 그리면 그 구멍 한가운데에 이음매가 생긴다(창의 가장자리에서 잘린 부분만 다른 시드로 그려진다) — 그래서 구멍을 창마다 따로 든다.</summary>
internal sealed record TextRegenWindow(RegenWindow Window, Mat Hole) : IDisposable
{
    public void Dispose() => Hole.Dispose();
}

/// <summary>글자 지운 자리 다시 그리기(TextRegen, 2026-11-26)의 계산 쪽 — 대상 마스크(EmbeddedTranslator가 남긴 <c>.textregen.png</c>)를 SDXL에 보낼 창으로 묶고,
/// 컬러 쪽을 가린다. 그리는 길은 <see cref="PipelineRunner.TextRegenAsync"/>, 대상 블록을 고르는 기준은 <see cref="Engine.BubbleSolver.RegenStats"/>.</summary>
internal static class TextRegenPlan
{
    /// <summary>마스크를 이 px만큼(2×+1 타원) 불려 한 블록의 글자 획을 한 덩어리로 본다 — 글자마다 창을 만들지 않게. 불린 상자를 그대로 묶음의 상자로 쓴다(여유).</summary>
    internal const int MergePx = 15;
    /// <summary>쪽 평균 채도(B·G·R 최대−최소)가 이보다 크면 컬러 쪽 — 기본 프롬프트가 흑백 만화용이라 컬러 표지에 회색 조각이 생긴다.
    /// 흑백 스캔은 0~2(압축 잡음), 채색 결과는 12 이상(<see cref="PipelineRunner.ColorizedChromaGain"/>의 실측)이라 그 사이에 둔다.</summary>
    internal const double ColorPageChroma = 6.0;

    /// <summary>대상 마스크 → 보낼 창들(위에서 아래, 왼쪽에서 오른쪽). 가까운 블록은 한 창으로 묶는다: 합친 상자의 긴 변이 <c>cropSize / 1.6</c> 이하면 같은 묶음 —
    /// 그래야 <see cref="RegenCrop.WindowFor"/>의 창이 cropSize로 유지돼 줄이지 않고 간다. 혼자서 그 길이를 넘는 블록은 제 창을 갖고 줄여 보낸다(재생성과 같은 규칙).
    /// 창 하나에 1분쯤 드니 창 수가 곧 시간이다. 빈 마스크면 빈 목록. 창마다 제 묶음의 구멍만 든다(<see cref="TextRegenWindow"/>) — 호출자가 해제.</summary>
    internal static List<TextRegenWindow> WindowsFor(Mat regenMask, Size img, int cropSize)
    {
        var windows = new List<TextRegenWindow>();
        if (regenMask.Empty() || Cv2.CountNonZero(regenMask) == 0) return windows;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * MergePx + 1, 2 * MergePx + 1));
        using var grown = new Mat();
        Cv2.Dilate(regenMask, grown, kernel);
        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(grown, labels, stats, cent, PixelConnectivity.Connectivity8);
        var boxes = new List<(int Label, Rect Box)>();
        for (int i = 1; i < n; i++)
            boxes.Add((i, new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height))));

        int limit = (int)(cropSize / 1.6);
        var groups = new List<(Rect Box, List<int> Labels)>();
        foreach (var (label, b) in boxes.OrderBy(b => b.Box.Y).ThenBy(b => b.Box.X))
        {
            int at = groups.FindIndex(g => { var u = g.Box.Union(b); return Math.Max(u.Width, u.Height) <= limit; });
            if (at >= 0) { groups[at].Labels.Add(label); groups[at] = (groups[at].Box.Union(b), groups[at].Labels); }
            else groups.Add((b, [label]));
        }
        foreach (var (box, labelsOfGroup) in groups)
        {
            var w = RegenCrop.WindowFor(box, img, cropSize);
            // 이 창의 구멍 = 창 안에서 이 묶음의 덩어리(불린 성분)에 드는, 불리기 전 마스크의 픽셀
            var hole = new Mat(w.Window.Height, w.Window.Width, MatType.CV_8UC1, Scalar.Black);
            using (var lab = new Mat(labels, w.Window)) using (var one = new Mat())
                foreach (int label in labelsOfGroup)
                {
                    Cv2.InRange(lab, new Scalar(label), new Scalar(label), one);
                    Cv2.BitwiseOr(hole, one, hole);
                }
            using (var src = new Mat(regenMask, w.Window)) Cv2.BitwiseAnd(hole, src, hole);
            windows.Add(new TextRegenWindow(w, hole));
        }
        return windows;
    }

    /// <summary>컬러 쪽인가 — 한 채널 그림은 아니다.</summary>
    internal static bool IsColorPage(Mat bgr) => bgr.Channels() >= 3 && PipelineRunner.MeanChroma(bgr, null) > ColorPageChroma;
}

/// <summary>글자 자리를 그린 결과의 '빈 조각' 판정 — 구멍의 조각이 **제 둘레보다** 눈에 띄게 흰가.
/// 재생성의 흰 조각 검사(<see cref="RegenWhiteCheck"/>: 흰 비율 ≥ 0.6 = 실패)를 그대로 쓰지 않는다(설계 검토 2026-11-26): 글자 자리의 바탕은 속도선·빗금처럼 원래 흰 종이가 대부분이라
/// 제대로 그린 결과도 0.6을 넘어 창마다 헛되이 다시 그리고, '덜 흰 것 고르기'가 먹칠이 가장 많은 결과를 고른다. 그래서 조각 바로 밖(원본 그대로인 자리)과 견준다.</summary>
internal static class TextRegenCheck
{
    /// <summary>조각의 흰 비율이 둘레의 흰 비율보다 이만큼 넘게 크면 빈 조각(그림 있는 바탕인데 흰 칠로 메워졌다).</summary>
    internal const double BlankExcess = 0.15;
    /// <summary>둘레 = 조각 밖 (붙이는 범위 + 이 거리)px 사이의 고리.</summary>
    internal const int RimInner = 2, RimOuter = 10;
    /// <summary>다시 그린 것이 더 낫다고 보려면 흰 초과분의 합이 이만큼은 작아야 한다 — 소수점 차이로 먹칠이 많은 결과로 갈아타지 않게.</summary>
    internal const double ExcessMargin = 0.02;

    /// <summary>구멍의 조각(8연결, <see cref="RegenWhiteCheck.MinPiecePx"/> 이상)마다 (그린 결과에서 조각 안의 흰 비율, **그리기 전 그림에서** 조각 둘레 고리의 흰 비율) — 라벨 순.
    /// 흰 = 회색 ≥ <see cref="RegenWhiteCheck.WhiteLevel"/>. 고리는 조각에서 <paramref name="skip"/> + <see cref="RimInner"/>~<see cref="RimOuter"/>px 떨어진 자리다:
    /// 구멍 바로 밖(skip = 붙이는 범위, PipelineRunner.TextRegenPasteReach)은 SDXL이 같이 다시 그리는 자리라 흰 칠이 거기까지 번진다 — 결과의 그 자리와 견주면 빈 조각을 못 잡는다(가짜 ComfyUI 시험에서 드러남).
    /// 고리에서 다른 조각은 뺀다. 고리가 없으면(그림을 꽉 채운 조각) 둘레 = 조각 값으로 둔다(빈 조각으로 치지 않는다). 세 그림은 같은 크기(창 크기)여야 한다.</summary>
    internal static List<(double Hole, double Rim)> PieceStats(Mat result, Mat original, Mat hole, int skip)
    {
        using var gray = result.Channels() == 1 ? result.Clone() : result.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var grayBefore = original.Channels() == 1 ? original.Clone() : original.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var bin = hole.Threshold(127, 255, ThresholdTypes.Binary);
        using var notHole = new Mat();
        Cv2.BitwiseNot(bin, notHole);
        using var white = gray.Threshold(RegenWhiteCheck.WhiteLevel - 1, 255, ThresholdTypes.Binary);   // > 234 = ≥ 235
        using var whiteBefore = grayBefore.Threshold(RegenWhiteCheck.WhiteLevel - 1, 255, ThresholdTypes.Binary);
        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cent, PixelConnectivity.Connectivity8);
        skip = Math.Max(0, skip);
        using var outerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * (skip + RimOuter) + 1, 2 * (skip + RimOuter) + 1));
        using var innerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * (skip + RimInner) + 1, 2 * (skip + RimInner) + 1));
        var bounds = new Rect(0, 0, bin.Cols, bin.Rows);
        var list = new List<(double Hole, double Rim)>();
        for (int i = 1; i < n; i++)
        {
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < RegenWhiteCheck.MinPiecePx) continue;
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            int pad = skip + RimOuter + 1;
            var roi = new Rect(box.X - pad, box.Y - pad, box.Width + 2 * pad, box.Height + 2 * pad) & bounds;
            using var lab = new Mat(labels, roi); using var piece = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), piece);
            using var far = new Mat(); using var near = new Mat(); using var rim = new Mat();
            Cv2.Dilate(piece, far, outerKernel);
            Cv2.Dilate(piece, near, innerKernel);
            Cv2.Subtract(far, near, rim);
            using (var others = new Mat(notHole, roi)) Cv2.BitwiseAnd(rim, others, rim);
            using var w = new Mat(white, roi); using var hit = new Mat();
            Cv2.BitwiseAnd(w, piece, hit);
            double inHole = Cv2.CountNonZero(hit) / (double)Cv2.CountNonZero(piece);
            int rimPx = Cv2.CountNonZero(rim);
            double around = inHole;
            if (rimPx > 0)
            {
                using var wb = new Mat(whiteBefore, roi);
                Cv2.BitwiseAnd(wb, rim, hit);
                around = Cv2.CountNonZero(hit) / (double)rimPx;
            }
            list.Add((inHole, around));
        }
        return list;
    }

    /// <summary>빈 조각의 수.</summary>
    internal static int BlankCount(IReadOnlyList<(double Hole, double Rim)> stats) => stats.Count(p => p.Hole - p.Rim > BlankExcess && p.Hole >= BlankMinWhite);

    /// <summary>빈 조각은 구멍이 거의 다 흰 것이다(흰 칠로 메워졌다) — 흰 비율이 이보다 낮으면 둘레보다 희더라도 무언가 그린 것이다. 둘레는 구멍에서 12~20px 떨어진 고리라,
    /// 흰 풍선과 먹칠 사이에 걸친 구멍(제대로 그리면 반쯤 희다)은 고리가 먹 쪽에 치우치기만 해도 '둘레보다 0.15 넘게 희다'에 걸려 한 번 더 그렸다(검토 2026-11-26).</summary>
    internal const double BlankMinWhite = 0.85;

    /// <summary>다시 그린 결과가 지금 것보다 나은가 — 빈 조각이 적으면, 같으면 둘레보다 흰 만큼(초과분)의 합이 <see cref="ExcessMargin"/> 넘게 작으면.
    /// 둘레보다 검게 그린 것은 점수가 되지 않는다(초과분 0) — 먹칠이 많은 쪽을 고르지 않는다.</summary>
    internal static bool IsBetter(IReadOnlyList<(double Hole, double Rim)> candidate, IReadOnlyList<(double Hole, double Rim)> best)
    {
        int bc = BlankCount(candidate), bb = BlankCount(best);
        if (bc != bb) return bc < bb;
        // 초과분은 '거의 다 흰' 조각에서만 센다(빈 조각의 기준과 같은 하한) — 걸친 구멍처럼 반쯤 흰 것이 맞는 조각이 더 검게 그려졌다고 점수를 주지 않는다(검토 2026-11-27)
        static double Excess(IReadOnlyList<(double Hole, double Rim)> s) => s.Where(p => p.Hole >= BlankMinWhite).Sum(p => Math.Max(0, p.Hole - p.Rim));
        return Excess(candidate) < Excess(best) - ExcessMargin;
    }
}
