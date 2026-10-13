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
