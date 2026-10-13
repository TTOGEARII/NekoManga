using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>말풍선 휴리스틱 — BT의 extract_ballon_mask/check_need_inpaint 이식.
/// 블록 주변에서 "텍스트를 전부 감싸는 가장 작은 닫힌 컨투어"를 풍선으로 보고,
/// 풍선 안 비텍스트 픽셀이 단색이면 LaMa 없이 그 색으로 칠해버린다 (흰 풍선 90% 처리, 매우 빠름).</summary>
public static class BubbleSolver
{
    /// <summary>블록 ROI에서 단색 풍선이면 텍스트를 배경색으로 칠하고 true.
    /// 아니면 false (호출자가 LaMa로 처리).</summary>
    public static bool TrySolidFill(Mat img, Mat textMask, Rect block)
    {
        // 블록을 1.7배 넓혀 풍선 테두리가 들어오게
        var roi = Enlarge(block, 1.7, img.Width, img.Height);
        using var im = new Mat(img, roi);
        using var msk = new Mat(textMask, roi);
        if (Cv2.CountNonZero(msk) == 0) return true; // 지울 게 없음

        using var ballonMask = ExtractBallonMask(im, msk);
        if (ballonMask == null) return false;

        // 풍선 내부의 비텍스트 픽셀 통계
        using var nonText = new Mat();
        Cv2.BitwiseNot(msk, nonText);
        Cv2.BitwiseAnd(ballonMask, nonText, nonText);
        if (Cv2.CountNonZero(nonText) < 32) return false;

        Cv2.MeanStdDev(im, out var mean, out var std, nonText);
        double stdMax = Math.Max(std.Val0, Math.Max(std.Val1, std.Val2));
        double stdSpread = StdOf(std.Val0, std.Val1, std.Val2);
        double thresh = stdSpread > 1 ? 7 : 10; // BT와 동일한 판정
        if (stdMax >= thresh) return false;

        im.SetTo(new Scalar(mean.Val0, mean.Val1, mean.Val2), ballonMask);
        return true;
    }

    /// <summary>풍선 마스크(255=풍선 내부, 텍스트 포함). 못 찾으면 null.</summary>
    private static Mat? ExtractBallonMask(Mat im, Mat textMask)
    {
        int h = im.Rows, w = im.Cols;
        long textSum = Cv2.Sum(textMask).Val0 > 0 ? (long)(Cv2.Sum(textMask).Val0) : 0;
        if (textSum == 0) return null;

        using var blur = new Mat();
        Cv2.GaussianBlur(im, blur, new Size(3, 3), 0);
        using var canny = new Mat();
        Cv2.Canny(blur, canny, 70, 140, 3, true);
        Cv2.Dilate(canny, canny, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));

        // 텍스트 bbox
        using var nz = new Mat();
        Cv2.FindNonZero(textMask, nz);
        if (nz.Empty()) return null;
        var br = Cv2.BoundingRect(nz);

        // 닫히지 않은 풍선 대비: 가장자리에 흰 사각형을 둘러 강제로 닫는다
        Cv2.Rectangle(canny, new Rect(0, 0, w - 1, h - 1), Scalar.White, 1);
        using (var notText = new Mat())
        {
            Cv2.BitwiseNot(textMask, notText);
            Cv2.BitwiseAnd(canny, notText, canny);
        }

        Cv2.FindContours(canny, out var cons, out _, RetrievalModes.CComp, ContourApproximationModes.ApproxNone);
        double minArea = (double)w * h;
        Mat? best = null;
        foreach (var con in cons)
        {
            var cb = Cv2.BoundingRect(con);
            // 텍스트 bbox를 완전히 포함해야 풍선 후보
            if (cb.X > br.X || cb.Y > br.Y || cb.Right < br.Right || cb.Bottom < br.Bottom) continue;
            var tmp = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
            Cv2.DrawContours(tmp, new[] { con }, -1, Scalar.White, -1);
            using var overlap = new Mat();
            Cv2.BitwiseAnd(tmp, textMask, overlap);
            if ((long)Cv2.Sum(overlap).Val0 >= textSum)
            {
                double area = Cv2.ContourArea(con);
                if (area < minArea)
                {
                    minArea = area;
                    best?.Dispose();
                    best = tmp;
                    continue;
                }
            }
            tmp.Dispose();
        }
        return best;
    }

    public static Rect Enlarge(Rect r, double ratio, int imW, int imH)
    {
        int cw = (int)(r.Width * ratio), ch = (int)(r.Height * ratio);
        int x = r.X - (cw - r.Width) / 2, y = r.Y - (ch - r.Height) / 2;
        int x1 = Math.Max(0, x), y1 = Math.Max(0, y);
        int x2 = Math.Min(imW, x + cw), y2 = Math.Min(imH, y + ch);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    private static double StdOf(params double[] v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average());
    }
}
