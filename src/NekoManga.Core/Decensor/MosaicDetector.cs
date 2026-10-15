using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>모자이크 블록 패턴을 픽셀 분석으로 감지해 마스크를 만든다.
/// 모자이크 = 일정 크기 블록 안에서 픽셀값 표준편차가 낮은 패턴 (기존 remove_mosaic.py 휴리스틱 이식).</summary>
public static class MosaicDetector
{
    /// <summary>감지된 영역의 8UC1 마스크(255=검열)를 돌려준다. 하나도 없으면 null.
    /// img: BGR.
    ///
    /// 모자이크의 특징은 "블록 내부는 평탄한데 인접 블록과는 색이 뚜렷이 다르다"(격자 패턴)이다.
    /// 평탄함만 보면 피부·하늘 같은 매끈한 배경을 통째로 오탐하므로, 블록 평균색 그리드를 만들어
    /// 이웃과의 색차(격자 경계)가 충분한 평탄 블록만 모자이크 후보로 삼는다.</summary>
    public static Mat? BuildMask(Mat img, int blockSize, int threshold, int grow)
    {
        if (blockSize < 2) blockSize = 2;
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        int gw = img.Cols / blockSize, gh = img.Rows / blockSize;
        if (gw < 3 || gh < 3) return null;

        var meanGrid = new double[gh, gw];
        var flat = new bool[gh, gw];
        unsafe
        {
            byte* pg = (byte*)gray.DataPointer; long sg = (long)gray.Step();
            for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int by = gy * blockSize, bx = gx * blockSize;
                double sum = 0, sumSq = 0;
                int n = blockSize * blockSize;
                for (int y = by; y < by + blockSize; y++)
                for (int x = bx; x < bx + blockSize; x++)
                {
                    double v = pg[y * sg + x];
                    sum += v; sumSq += v * v;
                }
                double mean = sum / n;
                meanGrid[gy, gx] = mean;
                double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
                flat[gy, gx] = std < threshold && mean is > 20 and < 235;
            }
        }

        // 격자 경계 확인: 평탄 블록이면서 이웃(상하좌우) 중 하나와 색차가 임계 이상
        const double EdgeDelta = 8; // 인접 블록 평균색 차 (모자이크 격자 특징)
        var mask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        bool any = false;
        for (int gy = 0; gy < gh; gy++)
        for (int gx = 0; gx < gw; gx++)
        {
            if (!flat[gy, gx]) continue;
            double m = meanGrid[gy, gx];
            double maxDelta = 0;
            if (gx > 0) maxDelta = Math.Max(maxDelta, Math.Abs(m - meanGrid[gy, gx - 1]));
            if (gx < gw - 1) maxDelta = Math.Max(maxDelta, Math.Abs(m - meanGrid[gy, gx + 1]));
            if (gy > 0) maxDelta = Math.Max(maxDelta, Math.Abs(m - meanGrid[gy - 1, gx]));
            if (gy < gh - 1) maxDelta = Math.Max(maxDelta, Math.Abs(m - meanGrid[gy + 1, gx]));
            if (maxDelta >= EdgeDelta)
            {
                any = true;
                Cv2.Rectangle(mask, new Rect(gx * blockSize, gy * blockSize, blockSize, blockSize), Scalar.White, -1);
            }
        }
        if (!any) { mask.Dispose(); return null; }

        // 격자가 밀집한 영역만 남기기: 닫기(구멍 메움) → 열기(고립 블록 제거)
        var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(blockSize * 2 + 1, blockSize * 2 + 1));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open,
            Cv2.GetStructuringElement(MorphShapes.Rect, new Size(blockSize * 2 + 1, blockSize * 2 + 1)));
        if (Cv2.CountNonZero(mask) == 0) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    /// <summary>설정의 DecensorTarget("Auto"/"BlackBar"/"WhiteBar"/"Mosaic")에 맞는 검열 마스크.
    /// Auto는 세 경로를 가볍게 돌려 '가장 그럴듯한 한 종류'를 고른다 — 한 번에 한 종류 원칙은
    /// 유지하고 선택만 대신한다. 우선순위는 오탐이 적은 순서: 검은 막대 → 모자이크 → 흰 막대
    /// (흰색은 원고의 흰 띠와 혼동이 가장 잦다). kindUsed로 어떤 종류를 썼는지 알려준다.</summary>
    public static Mat? BuildMaskForTarget(AppSettings s, Mat img, IReadOnlyList<Rect>? excludeRects, out string kindUsed)
    {
        kindUsed = s.DecensorTarget;
        if (s.DecensorTarget == "Mosaic")
            return BuildMask(img, s.DecensorBlockSize, s.DecensorThreshold, s.DecensorMaskGrow);
        if (s.DecensorTarget is "BlackBar" or "WhiteBar")
            return BuildBarMask(img,
                white: s.DecensorTarget == "WhiteBar", black: s.DecensorTarget == "BlackBar",
                s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);

        // Auto — 각 후보를 실제로 만들어 보고 판정 기준을 넘는 첫 종류를 쓴다.
        // 기준은 총 픽셀이 아니라 **가장 큰 단일 성분** — 효과음 붓획 조각들은 작게 흩어져
        // 총합만 크다 (실측 p20: 붓획 오탐 47k px이지만 최대 성분 5.2k vs 진짜 막대 12k+).
        // 수용 기준: 큰 단일 막대(최대 성분 ≥6000px) + 성분 수 ≤8.
        // 효과음 붓획·굵은 효과선은 막대와 기하학적으로 구분이 안 되지만(실측 p20·p22),
        // 검열 페이지는 막대 몇 개뿐이고 효과음 페이지는 조각이 많다는 점으로 가른다.
        static (int Largest, int Count) CompStats(Mat m)
        {
            using var lab = new Mat();
            using var st = new Mat();
            using var ce = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(m, lab, st, ce);
            int best = 0, cnt = 0;
            for (int i = 1; i < n; i++)
            {
                int a = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (a < 60) continue;   // 먼지 조각은 개수에 안 센다
                cnt++;
                best = Math.Max(best, a);
            }
            return (best, cnt);
        }
        const int BigBar = 6000;
        static bool BarLike(Mat m) { var (l, c) = CompStats(m); return l >= BigBar && c <= 8; }

        var blackM = BuildBarMask(img, white: false, black: true,
            s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);
        if (blackM != null && BarLike(blackM)) { kindUsed = "BlackBar"; return blackM; }
        blackM?.Dispose();

        var mosaicM = BuildMask(img, s.DecensorBlockSize, s.DecensorThreshold, s.DecensorMaskGrow);
        if (mosaicM != null && Cv2.CountNonZero(mosaicM) >= 1500) { kindUsed = "Mosaic"; return mosaicM; }
        mosaicM?.Dispose();

        var whiteM = BuildBarMask(img, white: true, black: false,
            s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);
        if (whiteM != null && BarLike(whiteM)) { kindUsed = "WhiteBar"; return whiteM; }
        whiteM?.Dispose();

        kindUsed = "없음";
        return null;
    }

    /// <summary>흰색·검은색 막대(바) 검열을 찾는다. 그림 위에 덮인 단색 직사각형이 특징이다.
    ///
    /// 판정 조건 — 네 개를 모두 만족해야 한다 (말풍선·여백·패널 테두리 오탐 방지):
    ///  ① 거의 순백(≥whiteLevel) 또는 거의 순흑(≤blackLevel)인 연결 영역
    ///  ② 채워진 직사각형에 가까움 (면적 / 최소외접사각형 면적 ≥ 0.80)
    ///  ③ 가로세로비가 minAspect 이상 (막대는 길쭉하다)
    ///  ④ 이미지 가장자리에 닿지 않음 (페이지 여백·패널 사이 간격 제외) + 면적 상·하한
    ///
    /// ②③은 '연결 성분 하나 = 막대 하나'를 가정한다. 원고에서는 막대 여러 개가 맞닿는 일이 잦아
    /// (교차하거나 나란히 붙음) 뭉치면 직사각형도 길쭉하지도 않게 되어 통째로 탈락한다.
    /// 그래서 조건에서 떨어진 덩어리는 <see cref="DecomposeBars"/>로 곧은 띠들로 쪼개 다시 판정한다.
    ///
    /// excludeRects(말풍선 등)와 겹치는 후보는 버린다.</summary>
    // 주의: CTD 텍스트 마스크로 글자를 보호해 보려다 되돌렸다 — CTD가 검열 막대 자체를 글자 획으로
    // 잡아내서, 보호를 켜면 진짜 막대 검출률이 100%→15%로 무너졌다 (실측).
    public static Mat? BuildBarMask(Mat img, bool white, bool black,
        double minAspect, int minAreaPx, int grow, IReadOnlyList<Rect>? excludeRects = null)
    {
        if (!white && !black) return null;

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        const int WhiteLevel = 244, BlackLevel = 18;
        double maxArea = (double)img.Rows * img.Cols * 0.25; // 패널 전체 같은 큰 영역은 검열이 아니다

        var mask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        bool any = false;
        var whiteAccepted = new List<(Mat Comp, Rect Br)>();   // 흰 후보는 모아서 장식 줄무늬 검사 후 반영

        foreach (var isWhite in new[] { true, false })
        {
            if (isWhite && !white) continue;
            if (!isWhite && !black) continue;

            using var bin = new Mat();
            if (isWhite) Cv2.Threshold(gray, bin, WhiteLevel, 255, ThresholdTypes.Binary);
            else Cv2.Threshold(gray, bin, BlackLevel, 255, ThresholdTypes.BinaryInv);

            // 가는 선·점 노이즈 제거 (막대는 두껍다)
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open,
                Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)));

            // 컨투어(RETR_EXTERNAL) 대신 연결 성분을 쓴다 — 흰 페이지 배경이 바깥 컨투어를 차지하면
            // 그림 안쪽의 흰 막대가 계층에서 밀려 아예 조회되지 않는다 (실제로 놓쳤던 버그).
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);

            // 흰 막대는 원고 곳곳의 흰 띠(선 사이 여백·하이라이트·패널 사이)와 구분이 어렵다.
            // 그래서 흰쪽은 v0.4.0 그대로 '통째로 직사각형'만 인정하고 최소 면적도 내리지 않는다
            // (실측: 흰쪽에 띠 분해를 켜면 막대가 없는 깨끗한 페이지에서 오탐이 1613→5795px).
            int areaFloor = isWhite ? Math.Max(minAreaPx, 600) : minAreaPx;

            for (int li = 1; li < n; li++) // 0 = 배경 레이블
            {
                int area = stats.At<int>(li, (int)ConnectedComponentsTypes.Area);
                if (area < areaFloor || area > maxArea) continue;

                int bx = stats.At<int>(li, (int)ConnectedComponentsTypes.Left);
                int by = stats.At<int>(li, (int)ConnectedComponentsTypes.Top);
                int bw = stats.At<int>(li, (int)ConnectedComponentsTypes.Width);
                int bh = stats.At<int>(li, (int)ConnectedComponentsTypes.Height);
                var br = new Rect(bx, by, bw, bh);

                // ④ 가장자리에 닿아도 작으면 봐준다 — 페이지 여백·패널 간격은 이미지의 거의 전체
                // 폭/높이를 차지하지만, 화면 끝에 걸친 검열 막대는 한 조각일 뿐이다
                // (실측: 하단 끝까지 이어진 흰 줄무늬 검열이 이 규칙에 걸려 하나만 남았었다).
                bool edge = br.X <= 1 || br.Y <= 1 || br.Right >= img.Cols - 1 || br.Bottom >= img.Rows - 1;
                if (edge && (bw >= img.Cols * 0.4 || bh >= img.Rows * 0.4)) continue;

                // 말풍선 등 제외 영역과 겹치면 버린다
                if (excludeRects != null && excludeRects.Any(e => (e & br).Width > 0 && (e & br).Height > 0))
                    continue;

                // 이 성분만 뽑아 회전까지 고려한 직사각형성·비율을 본다
                using var comp = new Mat();
                Cv2.Compare(labels, li, comp, CmpTypes.EQ);
                using var compRoi = new Mat(comp, br);
                using var pts = new Mat();
                Cv2.FindNonZero(compRoi, pts);
                if (pts.Empty()) continue;

                var rr = Cv2.MinAreaRect(pts);
                double w = rr.Size.Width, h = rr.Size.Height;
                if (w < 2 || h < 2) continue;

                double fill = area / (w * h), aspect = Math.Max(w, h) / Math.Min(w, h);

                // ② 채워진 직사각형이고 ③ 길쭉하면 막대 하나다.
                // 짧고 뭉툭한 막대(실제 원고에 18×21 = 비율 1.17짜리가 있었다)도 있으므로 비율 조건을
                // 낮추되 두 가지를 요구한다: '꽉 찬 사각형'(원의 채움비는 π/4 ≈ 0.785라 효과음 말줄임표
                // 같은 점은 걸러진다)이고 '작을 것'. 크기 제한이 없으면 효과음의 굵은 붓획 토막
                // (74×53, 채움비 0.9)까지 막대로 보고 글자를 지워 버린다 (실측으로 확인).
                bool stubby = !isWhite && fill >= 0.90 && aspect >= 1.15 && Math.Max(w, h) <= 45;
                if ((fill >= 0.80 && aspect >= minAspect) || stubby)
                {
                    if (isWhite)
                    {
                        // 흰 막대는 얇다 — 짧은 변이 두꺼우면 문짝·벽 같은 흰 그림 면이다
                        // (실측: 348px짜리 문 패널이 막대로 잡혀 통째로 지워질 뻔했다)
                        if (Math.Min(w, h) > Math.Max(45, img.Cols * 0.045)) continue;
                        whiteAccepted.Add((comp.Clone(), br));
                    }
                    else
                    {
                        mask.SetTo(Scalar.White, comp);
                        any = true;
                    }
                    continue;
                }

                // 아니면 맞닿은 막대 뭉치일 수 있다 — 곧은 띠로 분해해 본다 (검은 막대만)
                if (isWhite) continue;
                using var bars = DecomposeBars(compRoi, area);
                if (bars == null) continue;
                using var dst = new Mat(mask, br);
                Cv2.BitwiseOr(dst, bars, dst);
                any = true;
            }
        }

        // ── 흰 후보 반영: 장식 줄무늬 그룹을 걸러낸 뒤에 ──
        // 겹겹이 쌓인 평행 띠(≥3, x겹침, 가까운 간격)는 ① 빗금 검열(띠 사이에 그림이 있다)이거나
        // ② 장식 줄무늬 패널(띠 사이가 밋밋한 톤이다). 사이 영역의 표준편차로 가른다
        // (실측 22p: 장식 패널 3묶음 147k px가 검열로 잡혀 그림이 지워질 뻔했다 — 톤 사이 std 낮음).
        if (whiteAccepted.Count > 0)
        {
            var drop = new bool[whiteAccepted.Count];
            var sortedIdx = Enumerable.Range(0, whiteAccepted.Count)
                .OrderBy(i => whiteAccepted[i].Br.Y).ToList();
            for (int a = 0; a < sortedIdx.Count; a++)
            {
                var grp = new List<int> { sortedIdx[a] };
                for (int b = a + 1; b < sortedIdx.Count; b++)
                {
                    var p = whiteAccepted[grp[^1]].Br;
                    var q = whiteAccepted[sortedIdx[b]].Br;
                    int xov = Math.Min(p.Right, q.Right) - Math.Max(p.X, q.X);
                    if (xov < Math.Min(p.Width, q.Width) * 0.5) continue;
                    int gap = q.Y - p.Bottom;
                    if (gap < 0 || gap > Math.Max(p.Height, q.Height) * 2.5) continue;
                    grp.Add(sortedIdx[b]);
                }
                if (grp.Count < 3) continue;
                // 띠 사이 영역이 밋밋하면 장식
                var flats = new List<bool>();
                for (int k = 1; k < grp.Count; k++)
                {
                    var p = whiteAccepted[grp[k - 1]].Br;
                    var q = whiteAccepted[grp[k]].Br;
                    int gx = Math.Max(p.X, q.X), gr = Math.Min(p.Right, q.Right);
                    int gy = p.Bottom + 2, gb = q.Y - 2;
                    if (gr - gx < 8 || gb - gy < 4) continue;
                    using var gapRoi = new Mat(gray, new Rect(gx, gy, gr - gx, gb - gy));
                    // 망점 톤은 픽셀 std가 높다 — 블러로 뭉개면 평탄한 톤은 std가 낮아지고
                    // 진짜 그림(선화)은 블러 후에도 구조가 남아 std가 높다.
                    // 가로 3등분 중 '한 구간이라도' 밋밋하면 장식 — 줄무늬 패널 위에 인물이
                    // 걸쳐 있어도 옆 구간은 톤이 이어진다 (빗금 검열은 전 구간이 그림이다).
                    using var gapBlur = new Mat();
                    Cv2.GaussianBlur(gapRoi, gapBlur, new Size(7, 7), 0);
                    int third = Math.Max(4, gapBlur.Cols / 3);
                    double minStd = double.MaxValue;
                    for (int tx3 = 0; tx3 + third <= gapBlur.Cols; tx3 += third)
                    {
                        using var part = new Mat(gapBlur, new Rect(tx3, 0, third, gapBlur.Rows));
                        Cv2.MeanStdDev(part, out _, out var stdP);
                        minStd = Math.Min(minStd, stdP.Val0);
                    }
                    flats.Add(minStd < 26);
                }
                if (flats.Count > 0 && flats.Count(f => f) >= flats.Count * 0.6)
                    foreach (var gi in grp) drop[gi] = true;
            }
            for (int i = 0; i < whiteAccepted.Count; i++)
            {
                if (!drop[i])
                {
                    mask.SetTo(Scalar.White, whiteAccepted[i].Comp);
                    any = true;
                }
                whiteAccepted[i].Comp.Dispose();
            }
        }

        // 회전 흰 스트립 추가 — 여백·흰 그림과 이어진 막대는 연결 성분으로 못 잡는다
        // (WhiteStripDetector 주석 참조: 직선 테두리 기반 + 보수 정책으로 장식 오탐 0 확인).
        if (white && WhiteStripDetector.AddToMask(img, mask, grow: 0) > 0)
            any = true;

        if (!any) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    /// <summary>맞닿은 막대 뭉치를 '곧은 띠'들로 분해한다. 덩어리(comp, 성분 하나만 담긴 ROI)를
    /// 여러 각도의 직선 커널로 열어(opening) 그 각도로 곧게 뻗은 띠만 남기고, 띠들의 합이
    /// 덩어리를 거의 다 덮을 때만 막대로 인정한다.
    ///
    /// 각도별로 따로 라벨링해야 한다 — 전 각도 결과를 먼저 OR하면 교차한 막대가 도로 한 덩어리가 된다.
    /// '띠들의 합으로 설명되는가'가 핵심 판별식이다: 검열 막대 뭉치는 곧은 띠 몇 개로 전부 덮이지만,
    /// 머리카락·검은 옷·효과음 붓획은 휘고 굵기가 변해 덮이지 않는다.
    /// (실측: 합성 원고에서 교차·짧은 막대 검출률 0% → 98%, 그림 손상 0px)</summary>
    private static Mat? DecomposeBars(Mat comp, int area)
    {
        const int BandThick = 9;     // 띠로 인정할 최소 두께
        const int BandLength = 26;   // 띠로 인정할 최소 길이
        const double BandFill = 0.85;   // 띠 외접사각형이 이만큼 차 있어야 한다
        const double BandAspect = 2.5;  // 띠는 길쭉하다
        const double Cover = 0.88;      // 띠들의 합이 덩어리를 덮는 비율

        var sub = new Mat(comp.Rows, comp.Cols, MatType.CV_8UC1, Scalar.Black);
        using var roi = new Mat(comp.Rows, comp.Cols, MatType.CV_8UC1);

        for (int deg = 0; deg < 180; deg += 10)
        {
            using var kernel = LineKernel(BandLength, BandThick, deg);
            using var opened = new Mat();
            Cv2.MorphologyEx(comp, opened, MorphTypes.Open, kernel);
            if (Cv2.CountNonZero(opened) == 0) continue;

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(opened, labels, stats, centroids);
            for (int li = 1; li < n; li++)
            {
                if (stats.At<int>(li, (int)ConnectedComponentsTypes.Area) < 60) continue;

                using var band = new Mat();
                Cv2.Compare(labels, li, band, CmpTypes.EQ);
                using var pts = new Mat();
                Cv2.FindNonZero(band, pts);
                if (pts.Empty()) continue;

                var rr = Cv2.MinAreaRect(pts);
                double w = rr.Size.Width, h = rr.Size.Height;
                if (w < 2 || h < 2) continue;
                if (Math.Max(w, h) / Math.Min(w, h) < BandAspect) continue;

                var box = Cv2.BoxPoints(rr).Select(p => (Point)p).ToArray();
                roi.SetTo(Scalar.Black);
                Cv2.FillConvexPoly(roi, box, Scalar.White);
                int total = Cv2.CountNonZero(roi);
                if (total == 0) continue;
                using var inside = new Mat();
                Cv2.BitwiseAnd(comp, roi, inside);
                if ((double)Cv2.CountNonZero(inside) / total < BandFill) continue;

                Cv2.FillConvexPoly(sub, box, Scalar.White);
            }
        }

        using var covered = new Mat();
        Cv2.BitwiseAnd(sub, comp, covered);
        if ((double)Cv2.CountNonZero(covered) / Math.Max(1, area) < Cover)
        {
            sub.Dispose();
            return null;
        }
        // 막대 끝의 안티에일리어싱까지 덮도록 띠 사각형 그대로(덩어리 밖은 제외하지 않고) 돌려준다.
        return sub;
    }

    /// <summary>지정한 각도로 누운 length×thick 직사각형 구조요소.</summary>
    private static Mat LineKernel(int length, int thick, double deg)
    {
        int size = (int)Math.Ceiling(Math.Sqrt(length * length + thick * thick)) | 1;
        var k = new Mat(size, size, MatType.CV_8UC1, Scalar.Black);
        var rr = new RotatedRect(new Point2f(size / 2f, size / 2f), new Size2f(length, thick), (float)deg);
        Cv2.FillConvexPoly(k, Cv2.BoxPoints(rr).Select(p => (Point)p).ToArray(), Scalar.White);
        return k;
    }

    /// <summary>강력 모드(ComfyUI 인페인트)용: 마스크를 알파 0으로 뚫은 RGBA PNG 바이트.</summary>
    /// <summary>ComfyUI LoadImage용 마스크 PNG (알파 0 = 재생성 영역).
    ///
    /// smoothInside=true면 마스크 안쪽을 **강하게 블러**한다. ComfyUI는 알파만 마스크로 쓰고
    /// RGB는 그대로 VAE 인코딩하므로 손을 안 대면 모자이크 격자가 결과에 새어 나온다.
    /// 그렇다고 주변 색으로 완전히 메우면(Telea) 실루엣까지 사라져 SDXL이 엉뚱한 걸 그린다
    /// (실측: 성기 자리에 얼굴을 그림). 블러는 격자만 지우고 형태·명암은 남겨 생성을 안내한다.</summary>
    public static byte[] ToMaskedPng(Mat img, Mat mask, bool smoothInside = false)
    {
        using var src = img.Clone();
        if (smoothInside)
        {
            // 블러 세기는 영역 크기에 맞춘다 — 작은 영역을 과하게 뭉개면 모델에 단서가 남지 않아
            // 밋밋한 회색 사각형이 나온다 (실측 020p 하단). 격자만 지울 만큼만 흐린다.
            using var nz = new Mat();
            Cv2.FindNonZero(mask, nz);
            int shortSide = 200;
            if (!nz.Empty())
            {
                var br = Cv2.BoundingRect(nz);
                shortSide = Math.Max(16, Math.Min(br.Width, br.Height));
            }
            double sigma = Math.Clamp(shortSide / 14.0, 3.0, 12.0);
            using var blurred = new Mat();
            Cv2.GaussianBlur(src, blurred, new Size(0, 0), sigma);
            using var soft = new Mat();
            Cv2.GaussianBlur(mask, soft, new Size(0, 0), 4);   // 경계도 부드럽게
            blurred.CopyTo(src, soft);
        }
        using var rgba = new Mat();
        Cv2.CvtColor(src, rgba, ColorConversionCodes.BGR2BGRA);
        unsafe
        {
            byte* pa = (byte*)rgba.DataPointer; long sa = (long)rgba.Step();
            byte* pm = (byte*)mask.DataPointer; long sm = (long)mask.Step();
            for (int y = 0; y < img.Rows; y++)
            for (int x = 0; x < img.Cols; x++)
                if (pm[y * sm + x] > 127) pa[y * sa + x * 4 + 3] = 0;
        }
        Cv2.ImEncode(".png", rgba, out var bytes);
        return bytes;
    }
}
