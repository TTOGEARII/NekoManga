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
