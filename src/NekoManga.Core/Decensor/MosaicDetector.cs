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

    /// <summary>흰색·검은색 막대(바) 검열을 찾는다. 그림 위에 덮인 단색 직사각형이 특징이다.
    ///
    /// 판정 조건 — 네 개를 모두 만족해야 한다 (말풍선·여백·패널 테두리 오탐 방지):
    ///  ① 거의 순백(≥whiteLevel) 또는 거의 순흑(≤blackLevel)인 연결 영역
    ///  ② 채워진 직사각형에 가까움 (면적 / 최소외접사각형 면적 ≥ 0.80)
    ///  ③ 가로세로비가 minAspect 이상 (막대는 길쭉하다)
    ///  ④ 이미지 가장자리에 닿지 않음 (페이지 여백·패널 사이 간격 제외) + 면적 상·하한
    ///
    /// excludeRects(말풍선 등)와 겹치는 후보는 버린다.</summary>
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

            for (int li = 1; li < n; li++) // 0 = 배경 레이블
            {
                int area = stats.At<int>(li, (int)ConnectedComponentsTypes.Area);
                if (area < minAreaPx || area > maxArea) continue;

                int bx = stats.At<int>(li, (int)ConnectedComponentsTypes.Left);
                int by = stats.At<int>(li, (int)ConnectedComponentsTypes.Top);
                int bw = stats.At<int>(li, (int)ConnectedComponentsTypes.Width);
                int bh = stats.At<int>(li, (int)ConnectedComponentsTypes.Height);
                var br = new Rect(bx, by, bw, bh);

                // ④ 가장자리에 닿으면 페이지 여백·패널 간격이다
                if (br.X <= 1 || br.Y <= 1 || br.Right >= img.Cols - 1 || br.Bottom >= img.Rows - 1) continue;

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
                // ② 채워진 직사각형인가
                if (area / (w * h) < 0.80) continue;
                // ③ 길쭉한가
                if (Math.Max(w, h) / Math.Min(w, h) < minAspect) continue;

                mask.SetTo(Scalar.White, comp);
                any = true;
            }
        }

        if (!any) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    /// <summary>두 마스크를 합친다 (한쪽이 null이면 나머지를 돌려준다). 둘 다 null이면 null.</summary>
    public static Mat? Combine(Mat? a, Mat? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        var m = new Mat();
        Cv2.BitwiseOr(a, b, m);
        a.Dispose();
        b.Dispose();
        return m;
    }

    /// <summary>강력 모드(ComfyUI 인페인트)용: 마스크를 알파 0으로 뚫은 RGBA PNG 바이트.</summary>
    public static byte[] ToMaskedPng(Mat img, Mat mask)
    {
        using var rgba = new Mat();
        Cv2.CvtColor(img, rgba, ColorConversionCodes.BGR2BGRA);
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
