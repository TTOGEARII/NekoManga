using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>맞닿은 말풍선 묶음을 풍선별 블록으로 나눈다.
/// CTD의 blk 검출은 가까운 텍스트를 한 블록으로 묶기 때문에, 테두리가 붙은 말풍선 2~3개가
/// 블록 하나로 잡히면 OCR·번역·식자가 전부 한 덩어리로 흘러 풍선 경계를 무시한 식자가 된다
/// (실제 원고 07p — 세 풍선의 대사가 한 문단으로 합쳐짐).
///
/// 판별 근거(실측): 검출기가 텍스트 마스크를 이미 7px 팽창시켜 두므로, 같은 풍선 안의
/// 글 기둥들은 여기서 5px만 더 불리면 반드시 한 덩어리로 붙는다 (단일 풍선 4곳 전부 1덩어리).
/// 반면 다른 풍선의 글 사이에는 테두리·여백이 있어 떨어져 남는다 (2풍선→2, 3풍선→3 일치).
/// 결합 풍선(내부 흰 영역이 이어진 형태)도 글 간격은 유지되므로 이 기준이 통한다 —
/// 흰 영역 분리나 침식(목 자르기)은 결합 풍선에서 전부 실패했다.</summary>
public static class BubbleSplitter
{
    /// <summary>블록 안 텍스트가 떨어진 덩어리 여럿이면 덩어리별 블록, 아니면 원래 블록 하나.</summary>
    public static List<DetectedBlock> Split(Mat img, Mat textMask, DetectedBlock block)
    {
        var one = new List<DetectedBlock> { block };
        try
        {
            var rect = new Rect(block.X1, block.Y1, block.X2 - block.X1, block.Y2 - block.Y1);
            // 1.25배로는 가장자리 글이 잘려 덩어리 수를 틀리게 센다 (실측) — 넉넉히 1.6배
            var roi = BubbleSolver.Enlarge(rect, 1.6, img.Width, img.Height);
            using var tm = new Mat(textMask, roi);
            if (Cv2.CountNonZero(tm) < 300) return one;

            using var tdil = new Mat();
            Cv2.Dilate(tm, tdil, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)));
            using var tLabels = new Mat();
            using var tStats = new Mat();
            using var tCent = new Mat();
            int nText = Cv2.ConnectedComponentsWithStats(tdil, tLabels, tStats, tCent, PixelConnectivity.Connectivity8);
            if (nText <= 2) return one; // 덩어리 하나 — 풍선 하나

            long totalArea = 0;
            var comps = new List<(int Area, Rect Box)>();
            for (int ti = 1; ti < nText; ti++)
            {
                int area = tStats.At<int>(ti, (int)ConnectedComponentsTypes.Area);
                totalArea += area;
                comps.Add((area, new Rect(
                    tStats.At<int>(ti, (int)ConnectedComponentsTypes.Left),
                    tStats.At<int>(ti, (int)ConnectedComponentsTypes.Top),
                    tStats.At<int>(ti, (int)ConnectedComponentsTypes.Width),
                    tStats.At<int>(ti, (int)ConnectedComponentsTypes.Height))));
            }

            // 잡티(획 조각·테두리 잔상)는 버리고, 의미 있는 덩어리가 둘 이상일 때만 분할.
            // 1.6배 ROI에 끌려 들어온 이웃 블록의 글(원래 박스 밖)은 제외 — 중복 블록 방지.
            var origLocal = new Rect(rect.X - roi.X, rect.Y - roi.Y, rect.Width, rect.Height);
            var majors = comps.Where(c =>
            {
                if (c.Area < Math.Max(150, totalArea * 0.06)) return false;
                var inter = c.Box.Intersect(origLocal);
                return inter.Width > 0 && inter.Height > 0 &&
                       (double)inter.Width * inter.Height >= 0.3 * c.Box.Width * c.Box.Height;
            }).ToList();
            if (majors.Count < 2) return one;

            // 같은 풍선 안에서 세로로 띄어 쓴 덩어리 재결합 — 같은 글 기둥(x 겹침 큼)이고
            // 사이에 먹선(풍선 테두리)이 없으면 한 풍선이다 (p05 'ええっ!?'가 둘로 갈라진 실측 오분할).
            // 다른 풍선이면 x가 어긋나거나(결합 풍선) 사이에 테두리 먹선이 지나간다.
            using var imRoi = new Mat(img, roi);
            using var gray = new Mat();
            Cv2.CvtColor(imRoi, gray, ColorConversionCodes.BGR2GRAY);
            // 단, 겹친 풍선(이음선 없이 안이 이어진 풍선)이 위아래로 놓이면 사이에 먹선이 없다 — 목이 가르면 다시 붙이지 않는다(NeckBetween).
            Mat? pageGray = null;
            bool Neck(Rect a, Rect b)
            {
                if (pageGray == null) { pageGray = new Mat(); Cv2.CvtColor(img, pageGray, ColorConversionCodes.BGR2GRAY); }
                DetectedBlock At(Rect r) => new(roi.X + r.X, roi.Y + r.Y, roi.X + r.Right, roi.Y + r.Bottom, block.Confidence, block.LangJa);
                return NeckBetween(pageGray, At(a), At(b));
            }
            var parent = Enumerable.Range(0, majors.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            try
            {
                for (int a = 0; a < majors.Count; a++)
                    for (int b = a + 1; b < majors.Count; b++)
                        if (SameBubble(gray, majors[a].Box, majors[b].Box) && !Neck(majors[a].Box, majors[b].Box))
                            parent[Find(a)] = Find(b);
            }
            finally { pageGray?.Dispose(); }
            majors = majors
                .Select((c, i) => (Comp: c, Root: Find(i)))
                .GroupBy(t => t.Root)
                .Select(grp => grp.Select(t => t.Comp).Aggregate((x, y) => (x.Area + y.Area, x.Box.Union(y.Box))))
                .ToList();
            if (majors.Count < 2) return one;

            var result = new List<DetectedBlock>();
            foreach (var g in majors)
            {
                int x1 = Math.Max(0, roi.X + g.Box.X - 2);
                int y1 = Math.Max(0, roi.Y + g.Box.Y - 2);
                int x2 = Math.Min(img.Width, roi.X + g.Box.Right + 2);
                int y2 = Math.Min(img.Height, roi.Y + g.Box.Bottom + 2);
                if (x2 - x1 < 4 || y2 - y1 < 4) continue;
                result.Add(new DetectedBlock(x1, y1, x2, y2, block.Confidence, block.LangJa));
            }
            return result.Count >= 2 ? result : one;
        }
        catch
        {
            return one; // 분할은 보너스 — 실패하면 원래 블록 그대로
        }
    }

    /// <summary>같은 풍선 안에서 따로 잡힌 이웃 블록을 하나로 합친다 — Split의 반대 방향 보정.
    ///
    /// 실측(401ちゃん 05·06·11·12·13·22p): 세로쓰기 풍선의 글 기둥들이 기둥마다 블록으로 갈라졌다
    /// ("ここんとこ" | "ずっとじゃないですかー", "そう" | "だなぁ...", "して" | "いっぱい", "すまんな..." | "不器用で").
    /// 기둥 하나는 폭이 40~60px뿐이라 각 블록이 좁은 세로 띠 안에 번역문을 억지로 넣고, 옆 기둥의 번역과
    /// 겹쳐 그려졌다. 원문도 반 토막씩 번역돼 "요즘" / "맨날이잖아요ー"처럼 문장이 끊긴다.
    ///
    /// 기준: 두 블록이 ① 나란히(세로 겹침 ≥ 작은 쪽 높이의 35%, 가로 틈 ≤ 글자 크기)거나
    /// ② 위아래로(가로 겹침 ≥ 작은 쪽 폭의 절반, 세로 틈 ≤ 글자 크기) 붙어 있고, ③ 사이에 먹선(풍선 테두리)이
    /// 없으면 같은 풍선이다. 글자 크기는 블록의 짧은 변(세로쓰기면 기둥 폭, 가로쓰기면 줄 높이)으로 잡는다.
    /// 다른 풍선끼리는 테두리가 사이를 지나가거나(결합 풍선 포함) 틈이 글자 크기보다 훨씬 넓다.</summary>
    public static List<DetectedBlock> MergeSiblings(Mat img, Mat textMask, IReadOnlyList<DetectedBlock> blocks)
    {
        if (blocks.Count < 2) return blocks.ToList();
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

            var parent = Enumerable.Range(0, blocks.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (int a = 0; a < blocks.Count; a++)
                for (int b = a + 1; b < blocks.Count; b++)
                    if (Adjacent(blocks[a], blocks[b]) && SameWhiteRegion(gray, textMask, blocks[a], blocks[b])
                        && !NeckBetween(gray, blocks[a], blocks[b]))
                        parent[Find(a)] = Find(b);

            var merged = blocks
                .Select((blk, i) => (Blk: blk, Root: Find(i)))
                .GroupBy(t => t.Root)
                .Select(grp => grp.Select(t => t.Blk).Aggregate((x, y) => new DetectedBlock(
                    Math.Min(x.X1, y.X1), Math.Min(x.Y1, y.Y1), Math.Max(x.X2, y.X2), Math.Max(x.Y2, y.Y2),
                    Math.Max(x.Confidence, y.Confidence), x.LangJa || y.LangJa)))
                .ToList();
            if (merged.Count == blocks.Count) return blocks.ToList();
            merged.Sort((a, b2) => a.Y1 != b2.Y1 ? a.Y1.CompareTo(b2.Y1) : b2.X1.CompareTo(a.X1)); // 위→아래, 오른→왼
            return merged;
        }
        catch
        {
            return blocks.ToList(); // 합치기는 보너스 — 실패하면 그대로
        }
    }

    /// <summary>대사 블록에 붙어 잡힌 **손글씨 효과음의 획**을 갈라낸다 — 풍선 안의 글과, 그 풍선 밖에 놓인 훨씬 굵고 큰 획이 한 블록이면 둘로 나누고 뒤쪽에 <see cref="DetectedBlock.HandLettered"/>를 단다.
    /// 2026-11-26 사용자 "이런 부분은 효과음이야"(실제 05쪽): 풍선 안 대사와 그 아래 그림 위의 큰 "ぐぷ"가 감지기 마스크에서 이어져(Split이 한 덩어리로 보고, MergeSiblings가 옆 기둥과 다시 합쳐)
    /// 한 블록이 됐고, 대사를 지우면서 효과음의 획까지 LaMa로 지워졌다. OCR은 대사만 읽어 글로는 효과음인지 알 길이 없다 — 그림으로 가른다.
    /// 기준(그 블록의 실측: 풍선이 마스크의 35%를 품고, 밖의 덩어리는 넓이 38,729px·굵기 120px, 안의 글 덩어리는 굵기 45px. 다른 31블록의 풍선 밖 조각은 넓이 1,600px·굵기 27px 이하):
    /// ① 글을 가장 많이 품은 밝은 닫힌 영역(풍선 — 톤의 1~2px 줄·점은 닫기 연산으로 지우고 본다)이 창 가장자리에 닿지 않고 ② 블록 마스크의 <see cref="GiantCoverMin"/>~<see cref="GiantCoverMax"/>를 품으며
    /// ③ 풍선 밖 덩어리의 넓이가 쪽의 <see cref="GiantAreaFrac"/> 이상이고 굵기가 쪽 너비의 <see cref="GiantThickFrac"/> 이상이면서 풍선 안 글 덩어리 굵기(중앙값)의 <see cref="GiantThickRatio"/>배 이상.
    /// 풍선 밖으로 벗어난 **같은 크기의 대사**는 ③에 걸리지 않아 그대로다(갈라내면 번역되지 않고 남는다). 풍선이 없는 블록(그림 위의 글)은 가를 근거가 없어 그대로 둔다.</summary>
    public static List<DetectedBlock> SplitOffGiants(Mat img, Mat textMask, IReadOnlyList<DetectedBlock> blocks)
    {
        var result = new List<DetectedBlock>(blocks.Count + 1);
        Mat? gray = null;
        try
        {
            foreach (var b in blocks)
            {
                (DetectedBlock Text, DetectedBlock Giant)? parts = null;
                try
                {
                    if (gray == null) { gray = new Mat(); Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY); }
                    parts = GiantOutsideBubble(gray, textMask, b);
                }
                catch { }   // 가르기는 보너스 — 실패하면 원래 블록 그대로
                if (parts is { } p) { result.Add(p.Text); result.Add(p.Giant); }
                else result.Add(b);
            }
        }
        finally { gray?.Dispose(); }
        return result;
    }

    /// <summary><see cref="SplitOffGiants"/>: 풍선이 품어야 하는 블록 마스크의 비율 범위 · 풍선 밖 덩어리의 넓이 하한(쪽 넓이 대비) · 굵기 하한(쪽 너비 대비) · 풍선 안 글 굵기 대비 배율.</summary>
    internal const double GiantCoverMin = 0.10, GiantCoverMax = 0.90, GiantAreaFrac = 0.001, GiantThickFrac = 0.025, GiantThickRatio = 2.0;
    /// <summary><see cref="SplitOffGiants"/>: 밝은 성분의 이 비율 넘게 블록 마스크가 덮으면 풍선이 아니라 글자 · 가른 뒤 어느 블록에도 못 드는 풍선 밖 마스크가 이 픽셀 수와 블록 마스크의 이 비율 가운데 큰 쪽을 넘으면 가르지 않는다.</summary>
    internal const double GlyphLikeCover = 0.8, OrphanMaxShare = 0.05;
    internal const int OrphanMaxPx = 300;

    private static (DetectedBlock Text, DetectedBlock Giant)? GiantOutsideBubble(Mat gray, Mat textMask, DetectedBlock block)
    {
        var page = new Rect(0, 0, gray.Width, gray.Height);
        var rect = new Rect(block.X1, block.Y1, block.X2 - block.X1, block.Y2 - block.Y1) & page;
        if (rect.Width < 8 || rect.Height < 8) return null;
        // 풍선 전체가 들어오게 넉넉한 창 — 긴 변의 절반(작은 블록은 150px)
        int pad = Math.Max(150, Math.Max(rect.Width, rect.Height) / 2);
        var win = new Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad) & page;

        using var blockMask = new Mat(win.Height, win.Width, MatType.CV_8UC1, Scalar.Black);
        using (var src = new Mat(textMask, rect))
        using (var dst = new Mat(blockMask, new Rect(rect.X - win.X, rect.Y - win.Y, rect.Width, rect.Height)))
            src.CopyTo(dst);
        int total = Cv2.CountNonZero(blockMask);
        if (total < 300) return null;

        // 밝은 영역: 톤의 가는 줄·점(1~2px)은 3×3 닫기로 지우고 본다 — 줄무늬 풍선도 한 덩어리가 된다
        using var g = new Mat(gray, win);
        using var closed = new Mat();
        using (var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3))) Cv2.MorphologyEx(g, closed, MorphTypes.Close, k3);
        using var bright = new Mat();
        Cv2.Threshold(closed, bright, 199, 255, ThresholdTypes.Binary);
        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bright, labels, stats, cent, PixelConnectivity.Connectivity4);
        if (n <= 1) return null;

        // 풍선 후보 = 블록 마스크와 가장 많이 겹치는 밝은 성분들. 속이 빈 효과음 글자의 흰 속도 밝은 성분이라(획이 굵으면 풍선보다 더 많이 겹친다)
        // 하나만 보지 않고 위에서 셋까지 차례로 본다: "안은 가는 글, 밖은 굵은 획"이 성립하는 후보가 풍선이다(효과음의 속을 풍선으로 잡으면 밖이 가는 글이라 성립하지 않는다)
        var candidates = new List<(int Label, int Count)>();
        for (int i = 1; i < n; i++)
        {
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < 400) continue;
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            using var lab = new Mat(labels, box); using var comp = new Mat(); using var hit = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            using (var bm = new Mat(blockMask, box)) Cv2.BitwiseAnd(comp, bm, hit);
            int count = Cv2.CountNonZero(hit);
            // 마스크에 거의 다 덮인 밝은 성분은 풍선이 아니라 글자다(먹 바탕의 흰 글, 속 빈 글자의 흰 속) — 풍선의 흰 안쪽은 글 획 밖으로 넓게 퍼져 있다.
            // 이것을 풍선으로 보면 흰 글의 작은 글자가 '풍선', 큰 글자가 '풍선 밖의 거대한 획'이 되어 큰 글자가 효과음으로 떨어져 나간다(검토 2026-11-27)
            if (count >= GlyphLikeCover * stats.At<int>(i, (int)ConnectedComponentsTypes.Area)) continue;
            if (count > 0) candidates.Add((i, count));
        }
        foreach (var (label, _) in candidates.OrderByDescending(c => c.Count).Take(3))
            if (GiantOutside(labels, stats, label, blockMask, total, win, page, block) is { } parts)
                return parts;
        return null;
    }

    /// <summary><see cref="GiantOutsideBubble"/>의 후보 하나: 밝은 성분 label을 풍선으로 보고, 풍선 안의 글과 풍선 밖의 굵고 큰 획으로 갈리는지 본다.</summary>
    private static (DetectedBlock Text, DetectedBlock Giant)? GiantOutside(Mat labels, Mat stats, int bestLabel, Mat blockMask, int total, Rect win, Rect page, DetectedBlock block)
    {
        var bb = new Rect(stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left), stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top),
            stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width), stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height));
        // 창 가장자리에 닿은 영역은 다 못 본 것이다(쪽 가장자리는 예외) — 닫힌 풍선인지 모른다
        bool cut = (bb.X <= 0 && win.X > 0) || (bb.Y <= 0 && win.Y > 0) || (bb.Right >= win.Width && win.Right < page.Width) || (bb.Bottom >= win.Height && win.Bottom < page.Height);
        if (cut) return null;

        // 풍선 = 그 성분 + 성분에 둘러싸인 구멍(글자 획): 바깥에서 흘려 넣어 닿지 않는 곳이 안이다
        using var bubble = new Mat();
        Cv2.InRange(labels, new Scalar(bestLabel), new Scalar(bestLabel), bubble);
        using (var padded = new Mat())
        {
            Cv2.CopyMakeBorder(bubble, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.Black);
            Cv2.FloodFill(padded, new Point(0, 0), new Scalar(128));
            using var inner = new Mat(padded, new Rect(1, 1, bubble.Width, bubble.Height));
            using var holes = new Mat();
            Cv2.InRange(inner, new Scalar(0), new Scalar(0), holes);
            Cv2.BitwiseOr(bubble, holes, bubble);
        }
        using (var k4 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9))) Cv2.Dilate(bubble, bubble, k4);   // 테두리 선 위의 획 가장자리까지

        using var inside = new Mat(); using var outside = new Mat(); using var notBubble = new Mat();
        Cv2.BitwiseAnd(blockMask, bubble, inside);
        Cv2.BitwiseNot(bubble, notBubble);
        Cv2.BitwiseAnd(blockMask, notBubble, outside);
        double cover = Cv2.CountNonZero(inside) / (double)total;
        if (cover < GiantCoverMin || cover > GiantCoverMax) return null;

        var insideComps = Thicknesses(inside, minArea: 60);
        if (insideComps.Count == 0) return null;
        var sorted = insideComps.Select(c => c.Thick).OrderBy(t => t).ToList();
        double medianThick = sorted[sorted.Count / 2];
        double minArea = GiantAreaFrac * page.Width * page.Height;
        double minThick = Math.Max(GiantThickFrac * page.Width, GiantThickRatio * medianThick);
        var giants = Thicknesses(outside, minArea: 60).Where(c => c.Area >= minArea && c.Thick >= minThick).ToList();
        if (giants.Count == 0) return null;

        var giantBox = giants.Select(c => c.Box).Aggregate((a, b) => a.Union(b));
        var textBox = insideComps.Select(c => c.Box).Aggregate((a, b) => a.Union(b));
        // 풍선 밖에 거대한 획 말고도 글이 남으면(풍선 밖으로 이어진 보통 크기의 대사) 가르지 않는다 — 그 글은 대사 블록(풍선 안)에도 효과음 블록에도 들지 못해
        // 읽히지도 번역되지도 지워지지도 않는다(검토 2026-11-27). 효과음에 딸린 작은 조각(탁점)은 효과음 상자 안에 있어 세지 않는다
        using (var orphans = outside.Clone())
        {
            var near = new Rect(giantBox.X - 8, giantBox.Y - 8, giantBox.Width + 16, giantBox.Height + 16) & new Rect(0, 0, orphans.Cols, orphans.Rows);
            orphans.Rectangle(near, Scalar.Black, -1);
            if (Cv2.CountNonZero(orphans) > Math.Max(OrphanMaxPx, OrphanMaxShare * total)) return null;
        }
        DetectedBlock At(Rect r, bool hand) => new(
            Math.Max(0, win.X + r.X - 2), Math.Max(0, win.Y + r.Y - 2), Math.Min(page.Width, win.X + r.Right + 2), Math.Min(page.Height, win.Y + r.Bottom + 2),
            block.Confidence, block.LangJa, hand);
        return (At(textBox, false), At(giantBox, true));
    }

    /// <summary>마스크의 덩어리(8연결, minArea 이상)마다 상자·넓이·굵기(거리 변환 최댓값의 두 배 — 가장 굵은 곳의 폭).</summary>
    private static List<(Rect Box, int Area, double Thick)> Thicknesses(Mat mask, int minArea)
    {
        var list = new List<(Rect Box, int Area, double Thick)>();
        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cent, PixelConnectivity.Connectivity8);
        if (n <= 1) return list;
        using var dist = new Mat();
        Cv2.DistanceTransform(mask, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        for (int i = 1; i < n; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < minArea) continue;
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            using var lab = new Mat(labels, box); using var comp = new Mat(); using var d = new Mat(dist, box);
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            Cv2.MinMaxLoc(d, out _, out double max, out _, out _, comp);
            list.Add((box, area, max * 2));
        }
        return list;
    }

    private static bool Adjacent(DetectedBlock a, DetectedBlock b)
    {
        int wa = a.X2 - a.X1, ha = a.Y2 - a.Y1, wb = b.X2 - b.X1, hb = b.Y2 - b.Y1;
        double charSize = Math.Max(Math.Min(wa, ha), Math.Min(wb, hb)); // 글자 한 자 크기 ≈ 짧은 변
        double maxGap = Math.Max(12, charSize * 0.9);

        int oy = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        int gapX = Math.Max(a.X1, b.X1) - Math.Min(a.X2, b.X2);
        if (oy >= 0.35 * Math.Min(ha, hb) && gapX <= maxGap) return true;   // 나란한 기둥

        int ox = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        int gapY = Math.Max(a.Y1, b.Y1) - Math.Min(a.Y2, b.Y2);
        return ox >= 0.5 * Math.Min(wa, wb) && gapY <= maxGap;               // 위아래로 이어진 줄
    }

    /// <summary>두 블록이 같은 흰 영역(풍선 내부) 안에 있는가 — 밝은 픽셀(≥200) 또는 글자 획(textMask)을
    /// 지나갈 수 있는 길로 보고, 두 블록 주변을 4-연결 성분으로 나눠 같은 성분이면 같은 풍선.
    /// 풍선 테두리·결합 풍선의 이음선은 연속된 먹선이라 성분을 가른다. 망점 톤은 점 사이가 밝아 이어진다
    /// (프레임 없는 글이 톤 위에 나란히 있으면 합쳐지는데, 틈이 글자 크기 이하라 한 대사일 가능성이 높다).
    /// 틈 사이만 훑던 이전 방식은 이음선이 블록 bbox 안쪽(글자 옆 여백)에 걸리면 못 봤다 — 실측 21p 결합 풍선
    /// "しおいたち…"|"どうなっちゃうか"가 합쳐져 글이 두 풍선에 걸쳐 그려짐.</summary>
    private static bool SameWhiteRegion(Mat gray, Mat textMask, DetectedBlock a, DetectedBlock b)
    {
        const int Margin = 24;
        int x1 = Math.Max(0, Math.Min(a.X1, b.X1) - Margin), y1 = Math.Max(0, Math.Min(a.Y1, b.Y1) - Margin);
        int x2 = Math.Min(gray.Cols, Math.Max(a.X2, b.X2) + Margin), y2 = Math.Min(gray.Rows, Math.Max(a.Y2, b.Y2) + Margin);
        var roi = new Rect(x1, y1, x2 - x1, y2 - y1);
        if (roi.Width < 2 || roi.Height < 2) return false;

        // 밝은 픽셀만 통로 — 글자 마스크(검출기가 7px 팽창)를 통로에 넣으면 글자 옆에 바짝 붙은 이음선까지
        // 덮어 두 풍선이 이어진다 (실측 21p "…" 옆 이음선). 글자 획은 벽이어도 자간의 흰 틈으로 풍선 안이 이어진다.
        using var g = new Mat(gray, roi);
        using var passable = new Mat();
        Cv2.Threshold(g, passable, 199, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        int n = Cv2.ConnectedComponents(passable, labels, PixelConnectivity.Connectivity4);
        if (n < 2) return false;

        // 블록 안에서 가장 넓은 흰 성분 = 그 블록이 놓인 풍선 내부 (획 안 구멍 같은 작은 성분에 속지 않게)
        int DominantLabel(DetectedBlock blk)
        {
            var r = new Rect(blk.X1 - roi.X, blk.Y1 - roi.Y, blk.X2 - blk.X1, blk.Y2 - blk.Y1)
                .Intersect(new Rect(0, 0, labels.Cols, labels.Rows));
            if (r.Width <= 0 || r.Height <= 0) return 0;
            var count = new Dictionary<int, int>();
            for (int y = r.Y; y < r.Bottom; y++)
                for (int x = r.X; x < r.Right; x++)
                {
                    int lb = labels.At<int>(y, x);
                    if (lb == 0) continue;
                    count[lb] = count.TryGetValue(lb, out var c) ? c + 1 : 1;
                }
            return count.Count == 0 ? 0 : count.MaxBy(kv => kv.Value).Key;
        }

        int la = DominantLabel(a), lb2 = DominantLabel(b);
        return la != 0 && la == lb2;
    }

    /// <summary>두 블록이 겹친 풍선(이음선 없이 안이 통째로 이어진 풍선 둘)의 서로 다른 풍선에 있는가 — 같은 흰 영역이어도 합치지 않는다.
    ///
    /// 겹친 풍선의 윤곽은 두 풍선이 만나는 곳에서 **양쪽이 마주 보고 패어** 잘록한 목이 된다. 한 풍선(타원·상자)의 윤곽은 볼록해서 그런 짝이 없다.
    /// 흰 영역의 바깥 윤곽에서 볼록 껍질에 못 미치는 패임(convexity defect)을 찾고, 패임 둘의 가장 깊은 점을 이은 선(목)이
    ///   ① 두 블록 중심 사이를 가르고(중심을 이은 선분과 만난다), ② 흰 영역 안을 지나고,
    ///   ③ 패임마다 깊이가 목 길이의 5% 이상(이면서 4px 이상)이고, ④ 패임이 걸친 껍질 변이 목 길이의 절반 이상이고
    ///      (두 풍선 사이의 패임은 풍선 크기만큼 넓다 — 구름·외침 풍선의 촘촘한 패임과 꼬리 옆 패임은 좁다),
    ///   ⑤ 목이 양쪽 풍선의 폭(블록 중심을 지나 목과 나란한 흰 구간)의 90% 이하일 때만 겹친 풍선이다.
    /// 흰 영역이 살피는 범위 밖으로 나가면(테두리 없는 글·열린 풍선) 따지지 않는다 — 예전처럼 합친다.
    ///
    /// 근거(2026-11-16, 같은 책의 원본·한글 번역본 204쪽을 쪽마다 맞대어 잼): 원본에서 이 기준(목 위에 글·그림이 없는 것)에 든 풍선 184개 중
    /// 179개(97%)가 번역본에서 풍선마다 따로 식자돼 있었다 — 목 위에 글이 없고, 양쪽 글의 양 비율이 원본과 같고(차이 중앙값 0.04),
    /// 글 덩어리 중심이 제 풍선 중심에 있다(어긋남 중앙값 = 풍선 크기의 5%). 목/풍선 폭 비율은 중앙값 0.43, 최대 0.86.
    /// 예전의 PinchedBetween(거리 변환의 잘록함만 봄)은 한 풍선 안의 글 기둥까지 갈라 되돌렸다 — 그래서 윤곽의 마주 보는 패임을 요구한다.</summary>
    private static bool NeckBetween(Mat gray, DetectedBlock a, DetectedBlock b) => NeckLobes(gray, a, b) != null;

    /// <summary><see cref="NeckBetween"/>의 목(두 끝점, gray 좌표) — 겹친 풍선이 아니면 null. 식자(TextRenderer.PlanLayout)가 풍선을 잴 때 이 선을 벽으로 삼는다.</summary>
    internal static (Point P, Point Q)? NeckLine(Mat gray, DetectedBlock a, DetectedBlock b) =>
        NeckLobes(gray, a, b) is { } n ? (n.P, n.Q) : null;

    /// <summary><see cref="NeckLine"/>의 목과, 목으로 가른 두 풍선의 안쪽 사각형(gray 좌표) — LobeA는 a가 놓인 풍선, LobeB는 b가 놓인 풍선.
    /// 사각형은 그 조각의 무게중심을 지나는 가로·세로 구간과 그 반쯤 위아래·좌우 구간 가운데 가장 좁은 것이다(TextRenderer.MeasureBubble이 흰 안쪽을 재는 방식과 같다).
    /// 조각의 무게중심이 조각 밖이면(심하게 굽은 풍선) 빈 사각형 — 그때는 식자가 예전처럼 블록에서 잰다.</summary>
    internal static (Point P, Point Q, Rect LobeA, Rect LobeB)? NeckLobes(Mat gray, DetectedBlock a, DetectedBlock b)
    {
        try
        {
            // 풍선 전체가 들어오게 넉넉히 — 블록(글 자리)은 풍선보다 작다. 그래도 흰 영역이 가장자리에 닿으면 두 배씩 넓혀 다시 본다
            // (사슬로 이어진 풍선은 블록보다 훨씬 크다). 쪽의 끝에 닿은 것은 닿은 대로 본다(NeckIn).
            int margin = Math.Max(24, 2 * Math.Max(Math.Max(a.X2 - a.X1, a.Y2 - a.Y1), Math.Max(b.X2 - b.X1, b.Y2 - b.Y1)));
            for (; ; margin *= 2)
            {
                int x1 = Math.Max(0, Math.Min(a.X1, b.X1) - margin), y1 = Math.Max(0, Math.Min(a.Y1, b.Y1) - margin);
                int x2 = Math.Min(gray.Cols, Math.Max(a.X2, b.X2) + margin), y2 = Math.Min(gray.Rows, Math.Max(a.Y2, b.Y2) + margin);
                var roi = new Rect(x1, y1, x2 - x1, y2 - y1);
                if (NeckIn(gray, roi, a, b, out var neck) is { } found) return found ? neck : null;
                if (roi.Width >= gray.Cols && roi.Height >= gray.Rows) return null;
            }
        }
        catch
        {
            return null;   // 판정은 보너스 — 실패하면 예전처럼 합친다
        }
    }

    /// <summary><see cref="NeckBetween"/>의 한 번 — roi 안에서 판정한다. 두 블록의 흰 영역이 roi 가장자리에 닿아 다 못 봤으면 null.</summary>
    private static bool? NeckIn(Mat gray, Rect roi, DetectedBlock a, DetectedBlock b, out (Point P, Point Q, Rect LobeA, Rect LobeB) neck)
    {
        neck = default;
        {
            if (roi.Width < 8 || roi.Height < 8) return false;

            using var g = new Mat(gray, roi);
            using var passable = new Mat();
            Cv2.Threshold(g, passable, 199, 255, ThresholdTypes.Binary);
            using var labels = new Mat();
            using var stats = new Mat();
            using var cents = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(passable, labels, stats, cents, PixelConnectivity.Connectivity4);
            if (n < 2) return false;

            int Dominant(DetectedBlock blk)
            {
                var r = new Rect(blk.X1 - roi.X, blk.Y1 - roi.Y, blk.X2 - blk.X1, blk.Y2 - blk.Y1)
                    .Intersect(new Rect(0, 0, labels.Cols, labels.Rows));
                var count = new Dictionary<int, int>();
                for (int y = r.Y; y < r.Bottom; y++)
                    for (int x = r.X; x < r.Right; x++)
                    {
                        int lb = labels.At<int>(y, x);
                        if (lb != 0) count[lb] = count.TryGetValue(lb, out var c) ? c + 1 : 1;
                    }
                return count.Count == 0 ? 0 : count.MaxBy(kv => kv.Value).Key;
            }
            int la = Dominant(a);
            if (la == 0 || la != Dominant(b)) return false;

            // 흰 영역이 살피는 범위의 가장자리에 닿았다 — 다 못 봤다. 다만 그 가장자리가 쪽의 끝이면 거기가 영역의 끝이다:
            // 쪽 가장자리에 잘린 풍선도 풍선이다(실측 2026-11-16: 위 풍선이 쪽 위에 잘린 겹친 풍선의 두 대사가 한 블록으로 합쳐져 큰 풍선 가운데에 좁게 쌓였다).
            // 쪽 끝에 닿은 넓은 흰 영역은 풍선이 아니라 쪽 바탕(칸 밖 여백)이다 — 따지지 않는다(쪽의 12% = 번역본 대조 때 풍선으로 본 크기의 상한).
            int left = stats.At<int>(la, (int)ConnectedComponentsTypes.Left), top = stats.At<int>(la, (int)ConnectedComponentsTypes.Top);
            int w = stats.At<int>(la, (int)ConnectedComponentsTypes.Width), h = stats.At<int>(la, (int)ConnectedComponentsTypes.Height);
            if ((left <= 0 && roi.X > 0) || (top <= 0 && roi.Y > 0)
                || (left + w >= roi.Width && roi.Right < gray.Cols) || (top + h >= roi.Height && roi.Bottom < gray.Rows)) return null;
            if ((left <= 0 || top <= 0 || left + w >= roi.Width || top + h >= roi.Height)
                && stats.At<int>(la, (int)ConnectedComponentsTypes.Area) > PageEdgeBubbleMax * gray.Cols * gray.Rows) return false;

            using var region = new Mat();
            Cv2.InRange(labels, new Scalar(la), new Scalar(la), region);
            Cv2.FindContours(region, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            if (contours.Length == 0) return false;
            var contour = contours.MaxBy(c => c.Length)!;
            if (contour.Length < 8) return false;
            using var filled = new Mat(region.Size(), MatType.CV_8UC1, Scalar.Black);   // 글자 획이 낸 구멍까지 메운 풍선 안
            Cv2.DrawContours(filled, new[] { contour }, 0, Scalar.White, -1);

            var hull = Cv2.ConvexHullIndices(contour);
            if (hull.Length < 3) return false;
            // 패임 하나에 뾰족한 곳(목의 끝)이 여럿일 수 있다 — 풍선 셋이 사슬로 이어지면 껍질 변 하나가 세 풍선의 꼭대기를 지나
            // 목 둘을 한 패임으로 덮고, 가장 깊은 점은 그중 하나뿐이다. 패임 자리(껍질 변과 윤곽 사이)의 볼록 껍질 꼭짓점이 그 뾰족한 곳들이다.
            var defects = new List<(Point Start, Point End, Point Far, double Depth)>();   // Start·End = 패임이 걸친 껍질 변의 두 끝
            foreach (var d in Cv2.ConvexityDefects(contour, hull))
            {
                double depth = d.Item3 / 256.0;
                if (depth < 4) continue;
                Point s = contour[d.Item0], e = contour[d.Item1];
                int count = ((d.Item1 - d.Item0 + contour.Length) % contour.Length) + 1;
                var arc = Enumerable.Range(0, count).Select(k => contour[(d.Item0 + k) % contour.Length]).ToArray();
                double edge = s.DistanceTo(e);
                double DepthOf(Point p) => edge < 1 ? 0 : Math.Abs(Cross(e.X - s.X, e.Y - s.Y, p.X - s.X, p.Y - s.Y)) / edge;
                var tips = Cv2.ConvexHullIndices(arc).Where(k => k > 1 && k < count - 2 && DepthOf(arc[k]) >= Math.Max(4, 0.5 * depth))
                    .OrderBy(k => k).ToList();
                // 둥근 끝은 꼭짓점 여럿으로 잡힌다 — 붙어 있는 것끼리 묶어 가장 깊은 하나만
                int near = Math.Max(3, count / 30);
                for (int k = 0; k < tips.Count;)
                {
                    int m = k;
                    while (m + 1 < tips.Count && tips[m + 1] - tips[m] <= near) m++;
                    var best = tips.Skip(k).Take(m - k + 1).MaxBy(t => DepthOf(arc[t]));
                    defects.Add((s, e, arc[best], DepthOf(arc[best])));
                    k = m + 1;
                }
            }
            if (defects.Count < 2) return false;

            bool Inside(double x, double y)
            {
                int xi = (int)Math.Round(x), yi = (int)Math.Round(y);
                return xi >= 0 && yi >= 0 && xi < filled.Cols && yi < filled.Rows && filled.At<byte>(yi, xi) != 0;
            }
            // 중심을 지나 (ux, uy) 방향으로 풍선 안이 이어지는 길이
            double Chord(double cx, double cy, double ux, double uy)
            {
                if (!Inside(cx, cy)) return 0;
                int len = 0;
                foreach (int s in new[] { 1, -1 })
                    for (int k = 1; Inside(cx + s * ux * k, cy + s * uy * k); k++) len++;
                return len;
            }
            // 뾰족한 곳이 풍선 안쪽으로 파고든 방향(껍질 변에서 그 점으로) 쪽에 q가 있는가 — 마주 보는 짝만 목이다(같은 쪽에 나란한 둘은 아니다)
            bool Faces((Point Start, Point End, Point Far, double Depth) d, Point q)
            {
                double ex = d.End.X - d.Start.X, ey = d.End.Y - d.Start.Y, e2 = ex * ex + ey * ey;
                if (e2 < 1) return false;
                double t = ((d.Far.X - d.Start.X) * ex + (d.Far.Y - d.Start.Y) * ey) / e2;
                double nx = d.Far.X - (d.Start.X + t * ex), ny = d.Far.Y - (d.Start.Y + t * ey);
                return nx * (q.X - d.Far.X) + ny * (q.Y - d.Far.Y) > 0;
            }
            var necks = new List<(int I, int J, double Len)>();
            for (int i = 0; i < defects.Count; i++)
                for (int j = i + 1; j < defects.Count; j++)
                {
                    var (p, q) = (defects[i].Far, defects[j].Far);
                    double dx = q.X - p.X, dy = q.Y - p.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 8) continue;
                    if (defects[i].Depth < NeckDepth * len || defects[j].Depth < NeckDepth * len) continue;
                    if (defects[i].Start.DistanceTo(defects[i].End) < NeckSpan * len || defects[j].Start.DistanceTo(defects[j].End) < NeckSpan * len) continue;
                    if (!Faces(defects[i], q) || !Faces(defects[j], p)) continue;
                    int steps = (int)len, inside = 0;
                    for (int k = 1; k < steps; k++)
                        if (Inside(p.X + dx * k / steps, p.Y + dy * k / steps)) inside++;
                    if (inside < 0.9 * (steps - 1)) continue;
                    necks.Add((i, j, len));
                }
            foreach (var (i, j, len) in necks)
            {
                // 같은 끝에서 더 짧은 후보가 있으면 이것은 엇갈린 짝이다 — 사슬로 이어진 풍선에서 왼쪽 목의 위 끝과 오른쪽 목의 아래 끝을 이은 대각선이
                // 가운데 풍선 안의 두 기둥을 갈랐다(합성 시험)
                if (necks.Any(o => o.Len < 0.9 * len && (o.I == i || o.J == i || o.I == j || o.J == j))) continue;
                var (p, q) = (defects[i].Far, defects[j].Far);
                double dx = q.X - p.X, dy = q.Y - p.Y;
                // 목이 두 블록을 가른다: 풍선 안을 목에서 자르면 두 블록이 서로 다른 쪽에 있다.
                // 처음에는 "두 중심이 목의 양쪽에 있고 목의 두 끝이 중심을 이은 선의 양쪽에 있다"로 봤는데, 블록이 제 풍선의 한쪽에 치우치면
                // (효과음까지 품어 길쭉해진 블록 등) 중심선이 목을 비켜 지나가 겹친 풍선을 놓쳤다 — 실측 2026-11-16, 비스듬히 겹친 뾰족한 풍선 두 쌍:
                // 중심선이 목의 연장선을 목 길이의 8%·39% 밖에서 지났다.
                using var cut = filled.Clone();
                double ux = dx / len, uy = dy / len;
                Cv2.Line(cut, new Point((int)Math.Round(p.X - 3 * ux), (int)Math.Round(p.Y - 3 * uy)),
                    new Point((int)Math.Round(q.X + 3 * ux), (int)Math.Round(q.Y + 3 * uy)), Scalar.Black, 3);
                using var parts = new Mat();
                Cv2.ConnectedComponents(cut, parts, PixelConnectivity.Connectivity4);
                // 블록이 놓인 쪽(블록 자리의 풍선 안 픽셀이 가장 많은 조각)과 그 픽셀들의 무게중심 — 조각 하나에 70% 넘게 있어야 그쪽이라고 본다
                (int Part, double X, double Y) SideOf(DetectedBlock blk)
                {
                    var r = new Rect(blk.X1 - roi.X, blk.Y1 - roi.Y, blk.X2 - blk.X1, blk.Y2 - blk.Y1).Intersect(new Rect(0, 0, parts.Cols, parts.Rows));
                    var sum = new Dictionary<int, (int N, long X, long Y)>();
                    int all = 0;
                    for (int y = r.Y; y < r.Bottom; y++)
                        for (int x = r.X; x < r.Right; x++)
                        {
                            int lb = parts.At<int>(y, x);
                            if (lb == 0) continue;
                            var s = sum.TryGetValue(lb, out var v) ? v : default;
                            sum[lb] = (s.N + 1, s.X + x, s.Y + y);
                            all++;
                        }
                    if (all == 0) return default;
                    var best = sum.MaxBy(kv => kv.Value.N);
                    return best.Value.N < 0.7 * all ? default : (best.Key, (double)best.Value.X / best.Value.N, (double)best.Value.Y / best.Value.N);
                }
                var sa = SideOf(a);
                var sb = SideOf(b);
                if (sa.Part == 0 || sb.Part == 0 || sa.Part == sb.Part) continue;
                double lobe = Math.Min(Chord(sa.X, sa.Y, ux, uy), Chord(sb.X, sb.Y, ux, uy));
                if (len <= NeckRatio * lobe)
                {
                    // 조각의 안쪽 사각형: 무게중심을 지나는 구간과, 중심에서 끝까지의 반쯤 되는 곳의 구간 중 가장 좁은 것
                    Rect LobeOf(int part)
                    {
                        using var m = new Mat();
                        Cv2.InRange(parts, new Scalar(part), new Scalar(part), m);
                        var mo = Cv2.Moments(m, true);
                        if (mo.M00 < 1) return default;
                        int cx = (int)Math.Round(mo.M10 / mo.M00), cy = (int)Math.Round(mo.M01 / mo.M00);
                        bool In(int x, int y) => x >= 0 && y >= 0 && x < m.Cols && y < m.Rows && m.At<byte>(y, x) != 0;
                        if (!In(cx, cy)) return default;
                        (int Lo, int Hi) RunX(int x0, int y) { int l = x0, r = x0; while (In(l - 1, y)) l--; while (In(r + 1, y)) r++; return (l, r); }
                        (int Lo, int Hi) RunY(int x, int y0) { int t = y0, d = y0; while (In(x, t - 1)) t--; while (In(x, d + 1)) d++; return (t, d); }
                        var (l0, r0) = RunX(cx, cy);
                        var (t0, d0) = RunY(cx, cy);
                        int left = l0, right = r0, top = t0, bottom = d0;
                        foreach (int y in new[] { cy - (cy - t0) / 2, cy + (d0 - cy) / 2 })
                        {
                            var (l, r) = RunX(cx, y);
                            left = Math.Max(left, l); right = Math.Min(right, r);
                        }
                        foreach (int x in new[] { cx - (cx - l0) / 2, cx + (r0 - cx) / 2 })
                        {
                            var (t, d) = RunY(x, cy);
                            top = Math.Max(top, t); bottom = Math.Min(bottom, d);
                        }
                        return right - left < 8 || bottom - top < 8 ? default : new Rect(left + roi.X, top + roi.Y, right - left + 1, bottom - top + 1);
                    }
                    neck = (new Point(p.X + roi.X, p.Y + roi.Y), new Point(q.X + roi.X, q.Y + roi.Y), LobeOf(sa.Part), LobeOf(sb.Part));
                    return true;
                }
            }
            return false;
        }
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    /// <summary>겹친 풍선의 목: 풍선 폭 대비 목 길이의 상한 · 목 길이 대비 패임 깊이의 하한 · 목 길이 대비 패임이 걸친 껍질 변의 하한.</summary>
    private const double NeckRatio = 0.9, NeckDepth = 0.05, NeckSpan = 0.5;
    /// <summary>쪽 가장자리에 닿은 흰 영역을 풍선으로 보는 넓이의 상한(쪽 넓이 대비) — 그보다 넓으면 쪽 바탕이다.</summary>
    private const double PageEdgeBubbleMax = 0.12;

    /// <summary>(보관) 두 블록 사이 틈에 먹선이 지나가는가 — 마주 보는 변 사이만 훑어 bbox 안쪽 이음선을 놓친다.</summary>
    private static bool InkBetween(Mat gray, Mat textMask, DetectedBlock a, DetectedBlock b)
    {
        int gapX = Math.Max(a.X1, b.X1) - Math.Min(a.X2, b.X2);
        int gapY = Math.Max(a.Y1, b.Y1) - Math.Min(a.Y2, b.Y2);
        var samples = new List<(Point From, Point To)>();
        if (gapX > 0 && gapX >= gapY)
        {
            // 나란히: 세로 겹침 구간을 따라 가로로 훑는다
            var (l, r) = a.X2 <= b.X1 ? (a, b) : (b, a);
            int y1 = Math.Max(a.Y1, b.Y1), y2 = Math.Min(a.Y2, b.Y2);
            if (y2 <= y1) { y1 = Math.Min(a.Y1, b.Y1); y2 = Math.Max(a.Y2, b.Y2); }
            for (int k = 0; k < 9; k++)
            {
                int y = y1 + (y2 - y1) * (k + 1) / 10;
                samples.Add((new Point(l.X2, y), new Point(r.X1, y)));
            }
        }
        else if (gapY > 0)
        {
            var (t, d) = a.Y2 <= b.Y1 ? (a, b) : (b, a);
            int x1 = Math.Max(a.X1, b.X1), x2 = Math.Min(a.X2, b.X2);
            if (x2 <= x1) { x1 = Math.Min(a.X1, b.X1); x2 = Math.Max(a.X2, b.X2); }
            for (int k = 0; k < 9; k++)
            {
                int x = x1 + (x2 - x1) * (k + 1) / 10;
                samples.Add((new Point(x, t.Y2), new Point(x, d.Y1)));
            }
        }
        else
        {
            samples.Add((new Point((a.X1 + a.X2) / 2, (a.Y1 + a.Y2) / 2), new Point((b.X1 + b.X2) / 2, (b.Y1 + b.Y2) / 2)));
        }

        int inked = 0;
        foreach (var (p, q) in samples)
        {
            int steps = Math.Max(Math.Abs(q.X - p.X), Math.Abs(q.Y - p.Y));
            int darkRun = 0;
            bool hit = false;
            for (int s = 0; s <= steps && !hit; s++)
            {
                int x = p.X + (q.X - p.X) * s / Math.Max(1, steps);
                int y = p.Y + (q.Y - p.Y) * s / Math.Max(1, steps);
                if (x < 0 || y < 0 || x >= gray.Cols || y >= gray.Rows) continue;
                if (textMask.At<byte>(y, x) != 0) { darkRun = 0; continue; } // 글자 획은 먹선이 아니다
                if (gray.At<byte>(y, x) < 160) { if (++darkRun >= 2) hit = true; }
                else darkRun = 0;
            }
            if (hit) inked++;
        }
        return inked * 2 >= samples.Count;
    }

    /// <summary>두 텍스트 덩어리가 같은 풍선 안인가 — 같은 글 기둥(x 겹침 ≥ 절반)이고
    /// 중심을 잇는 길에 먹선(어두운 픽셀 2연속)이 없으면 같은 풍선으로 본다.</summary>
    private static bool SameBubble(Mat gray, Rect a, Rect b)
    {
        int ox = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        if (ox < 0.5 * Math.Min(a.Width, b.Width)) return false; // 기둥이 어긋남 — 딴 풍선 취급

        var ca = new Point(a.X + a.Width / 2, a.Y + a.Height / 2);
        var cb = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        int steps = Math.Max(Math.Abs(cb.X - ca.X), Math.Abs(cb.Y - ca.Y));
        if (steps == 0) return true;
        int darkRun = 0;
        for (int s = 0; s <= steps; s++)
        {
            int x = ca.X + (cb.X - ca.X) * s / steps;
            int y = ca.Y + (cb.Y - ca.Y) * s / steps;
            if (x < 0 || y < 0 || x >= gray.Cols || y >= gray.Rows) continue;
            // 덩어리 자신의 글자 획은 먹선이 아니다 — 박스 안은 건너뛴다
            bool inA = x >= a.X - 2 && x <= a.Right + 2 && y >= a.Y - 2 && y <= a.Bottom + 2;
            bool inB = x >= b.X - 2 && x <= b.Right + 2 && y >= b.Y - 2 && y <= b.Bottom + 2;
            if (inA || inB) { darkRun = 0; continue; }
            if (gray.At<byte>(y, x) < 160) { if (++darkRun >= 2) return false; }
            else darkRun = 0;
        }
        return true;
    }
}
