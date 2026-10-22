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
            var parent = Enumerable.Range(0, majors.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (int a = 0; a < majors.Count; a++)
                for (int b = a + 1; b < majors.Count; b++)
                    if (SameBubble(gray, majors[a].Box, majors[b].Box))
                        parent[Find(a)] = Find(b);
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
                    if (Adjacent(blocks[a], blocks[b]) && SameWhiteRegion(gray, textMask, blocks[a], blocks[b]))
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
