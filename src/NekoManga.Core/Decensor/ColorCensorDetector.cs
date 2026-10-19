using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>채도(색) 기반 검열 검출 — 흑백 원고에 덮인 '색이 있는' 검열을 찾는다.
///
/// 실측(2026-10-16 eval 세트): 검은선 계열로 라벨된 원고의 상당수가 실제로는 **채도 높은
/// 색 막대/틴트 모자이크**였다 — Benantoka=파란 막대, mashiro=적색 막대, Sumiyao=따뜻한
/// 색 틴트 모자이크. CensorNet(흑/백/모자이크 합성 학습)은 색 막대를 아예 못 봐 검출 0이었고,
/// 그래서 통째로 남았다("검은선 잔존").
///
/// 흑백 원고에서 채도 높은 색은 원리적으로 이례적이다(선화·망점·먹칠은 전부 무채색) —
/// 따라서 '거의 흑백인 페이지 위의 채도 덩어리'는 검열일 확률이 매우 높다. 이 신호는
/// 회색조 오탐원(장식 줄무늬·효과음 붓획)과 **직교**한다: 그것들은 전부 무채색이라 채도 마스크에
/// 안 걸린다. 그래서 Auto 경로(= AI가 1순위)에 보강으로 더해도 기존 오탐이 되살아나지 않는다.
///
/// 가드: ① 전체 채도 비율이 높으면(>25%) 컬러 일러스트/표지로 보고 아무것도 안 한다.
///       ② 개별 성분이 페이지의 25%를 넘으면(큰 색 면) 버린다. ③ 작은 얼룩(JPEG 색잡음)은
///       면적 하한·열기로 제거. ④ 말풍선 등 exclude와 많이 겹치면 버린다.</summary>
public static class ColorCensorDetector
{
    /// <summary>채도 검열 마스크(8UC1, 255=검열). 없으면 null.
    /// satFrac/compCount는 진단 로그용.</summary>
    public static Mat? BuildMask(Mat imgBgr, IReadOnlyList<Rect>? exclude, int grow,
        out double satFrac, out int compCount)
    {
        satFrac = 0; compCount = 0;
        if (imgBgr.Empty() || imgBgr.Channels() < 3) return null;

        long total = (long)imgBgr.Rows * imgBgr.Cols;
        using var hsv = new Mat();
        Cv2.CvtColor(imgBgr, hsv, ColorConversionCodes.BGR2HSV);
        var ch = Cv2.Split(hsv);
        using var hMat = ch[0]; using var satCh = ch[1]; using var vMat = ch[2];

        // 강한 채도만 (S≥90). 약한 채도는 JPEG 크로마 번짐이라 거른다.
        using var sat = new Mat();
        Cv2.Threshold(satCh, sat, 90, 255, ThresholdTypes.Binary);
        // 아주 어둡거나(먹칠 경계 크로마) 하는 저명도 잡음 억제는 하지 않는다 —
        // 검열 막대는 중간 명도의 선명한 색이라 그대로 남는다.
        satFrac = Cv2.CountNonZero(sat) / (double)total;

        // ① 컬러 페이지 가드 — 채도가 페이지 전반이면 일러스트/표지다.
        if (satFrac > 0.25 || satFrac < 0.0008) return null;

        // 얼룩 제거(열기) → 같은 검열 뭉치의 막대들 잇기(닫기)
        Cv2.MorphologyEx(sat, sat, MorphTypes.Open,
            Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
        Cv2.MorphologyEx(sat, sat, MorphTypes.Close,
            Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(13, 13)));

        using var lab = new Mat();
        using var st = new Mat();
        using var ce = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(sat, lab, st, ce);
        double maxComp = total * 0.25;   // 페이지의 1/4 넘는 색 면은 검열이 아니다
        var mask = new Mat(imgBgr.Rows, imgBgr.Cols, MatType.CV_8UC1, Scalar.Black);
        bool any = false;
        for (int i = 1; i < n; i++)
        {
            int area = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < 300 || area > maxComp) continue;
            var br = new Rect(
                st.At<int>(i, (int)ConnectedComponentsTypes.Left),
                st.At<int>(i, (int)ConnectedComponentsTypes.Top),
                st.At<int>(i, (int)ConnectedComponentsTypes.Width),
                st.At<int>(i, (int)ConnectedComponentsTypes.Height));
            // 말풍선 등과 크게 겹치면(절반 이상) 버린다 — 색 말풍선 오탐 방지
            if (exclude != null)
            {
                double inter = 0;
                foreach (var e in exclude)
                {
                    var r = e & br;
                    if (r.Width > 0 && r.Height > 0) inter += (double)r.Width * r.Height;
                }
                if (inter >= area * 0.5) continue;
            }
            using var comp = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            mask.SetTo(Scalar.White, comp);
            any = true;
            compCount++;
        }
        if (!any) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }
}
