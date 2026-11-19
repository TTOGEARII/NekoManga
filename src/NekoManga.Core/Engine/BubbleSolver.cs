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
        var roi = Enlarge(block, SolidRoiRatio, img.Width, img.Height);
        using var im = new Mat(img, roi);
        using var msk = new Mat(textMask, roi);
        if (Cv2.CountNonZero(msk) == 0) return true; // 지울 게 없음

        if (SolidPlan(im, msk) is not { } plan) return false;
        using var ballonMask = plan.Mask;
        im.SetTo(plan.Mean, ballonMask);
        return true;
    }

    /// <summary><see cref="TrySolidFill"/>이 블록을 넓히는 배율 — 풍선 테두리가 들어오게.</summary>
    public const double SolidRoiRatio = 1.7;

    /// <summary>단색 풍선 판정(BT check_need_inpaint): 글을 감싸는 테두리(없으면 창 테두리) 안의 글 밖 픽셀이 한 색이면 그 마스크(호출자가 해제)와 평균색, 아니면 null.
    /// <see cref="TrySolidFill"/>(칠하기)과 <see cref="IsInBubble"/>(판정)이 같이 쓴다.</summary>
    private static (Mat Mask, Scalar Mean)? SolidPlan(Mat im, Mat msk)
    {
        var ballonMask = ExtractBallonMask(im, msk);
        if (ballonMask == null) return null;

        // 풍선 내부의 비텍스트 픽셀 통계
        using var nonText = new Mat();
        Cv2.BitwiseNot(msk, nonText);
        Cv2.BitwiseAnd(ballonMask, nonText, nonText);
        if (Cv2.CountNonZero(nonText) < 32) { ballonMask.Dispose(); return null; }

        Cv2.MeanStdDev(im, out var mean, out var std, nonText);
        double stdMax = Math.Max(std.Val0, Math.Max(std.Val1, std.Val2));
        double stdSpread = StdOf(std.Val0, std.Val1, std.Val2);
        double thresh = stdSpread > 1 ? 7 : 10; // BT와 동일한 판정
        if (stdMax >= thresh) { ballonMask.Dispose(); return null; }
        return (ballonMask, new Scalar(mean.Val0, mean.Val1, mean.Val2));
    }

    /// <summary><see cref="IsInBubble"/>의 창 — 블록을 이 배율로 넓히되, 쪽 너비의 이 비율(가로)·높이의 이 비율(세로)보다는 작지 않게.
    /// "え?" 같은 짧은 글은 풍선이 글의 몇 배라 블록 배율만으로는 테두리가 창 밖으로 나간다(합성 시험 IsInBubble_ShortWordInBigBubble).</summary>
    public const double BubbleRoiRatio = 3.0, BubbleRoiMinWidthOfPage = 0.4, BubbleRoiMinHeightOfPage = 0.3;
    /// <summary>풍선 안(글 밖) 픽셀의 표준편차 상한 — 이보다 어지러우면 그림 선이 우연히 닫힌 것(칸·몸 윤곽)으로 본다. 흰·회색·옅은 톤 풍선은 10~35.</summary>
    public const double BubbleInsideStdMax = 40;

    /// <summary>블록의 글이 말풍선 안에 있는지 — 글을 전부 감싸는 닫힌 테두리가 창(<see cref="BubbleRoiRatio"/>) **안쪽에서** 닫히고 그 안이 그림으로 어지럽지 않으면 풍선.
    /// 창 가장자리에 닿는 것은 억지로 닫은 테두리(<see cref="ExtractBallonMask"/>의 흰 사각형)나 창보다 큰 칸이라 풍선이 아니다 — 흰 바탕에 테두리 없이 놓인 글,
    /// 그림 위에 그려진 효과음·손글씨, 칸 테두리만 감싸는 글은 false. 번역을 말풍선 안의 글로 좁히는 데 쓴다(AppSettings.TranslateBubbleOnly, 2026-11-18 사용자 —
    /// 그런 글은 번역기가 폭주하기 쉽고(95자 원문 → 32,772자 "아아아…") 식자해도 그림을 가린다). 그림을 바꾸지 않는다.</summary>
    public static bool IsInBubble(Mat img, Mat textMask, Rect block) => IsInBubble(img, textMask, block, out _);

    /// <summary><see cref="IsInBubble"/>의 판정 사유 — 진단·하네스용.</summary>
    public enum BubbleVerdict { NoText, NoOutline, TouchesWindow, Busy, Bubble, PlainBackground }

    public static bool IsInBubble(Mat img, Mat textMask, Rect block, out BubbleVerdict why)
    {
        why = BubbleVerdict.NoText;
        var roi = Enlarge(block, BubbleRoiRatio, img.Width, img.Height);
        int minW = (int)(img.Width * BubbleRoiMinWidthOfPage), minH = (int)(img.Height * BubbleRoiMinHeightOfPage);   // 비율이 1 미만이라 늘 쪽 안이다
        if (roi.Width < minW || roi.Height < minH)
        {
            int cw = Math.Max(roi.Width, minW), ch = Math.Max(roi.Height, minH);
            int x = Math.Clamp(block.X + block.Width / 2 - cw / 2, 0, img.Width - cw), y = Math.Clamp(block.Y + block.Height / 2 - ch / 2, 0, img.Height - ch);
            roi = new Rect(x, y, cw, ch);
        }
        using var im = new Mat(img, roi);
        // 이 블록의 글만 남긴다 — 창이 커서 이웃 풍선의 글이 들어오면 ExtractBallonMask가 그 글까지 감싸는 테두리를 찾고(없다), 창 가장자리에 걸린 글이
        // 억지로 닫는 사각형을 끊어 아무것도 못 찾는다(실제 원고 248블록 중 207이 그랬다, 2026-11-18)
        using var view = new Mat(textMask, roi);   // ROI 머리도 해제한다(finalizer에 맡기지 않게)
        using var msk = view.Clone();
        // 블록을 15% 넓힌 자리(이웃 풍선의 글은 밖). 폭 30px 미만의 세로 기둥은 15%가 1~2px뿐이라 감지기가 7px쯤 불린 획이 잘려 획 가장자리가 좁은 고리 후보로 남는다(검수 2026-11-19) — 적어도 8px은 둔다
        var own = (Enlarge(block, 1.15, img.Width, img.Height) | Pad(block, 8, img.Width, img.Height)) & roi;
        using (var keep = new Mat(msk.Size(), MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.Rectangle(keep, new Rect(own.X - roi.X, own.Y - roi.Y, own.Width, own.Height), Scalar.White, -1);
            Cv2.BitwiseAnd(msk, keep, msk);
        }
        if (Cv2.CountNonZero(msk) == 0) return true;   // 판정할 획이 없다 — 모르면 번역 쪽으로(효과음 꼴이어도 조용히 빼지 않는다, 검수 2026-11-19)
        if (ClosedOutline(im, msk, roi, img.Size(), out why)) return true;

        // 닫힌 테두리는 못 찾았지만 바탕이 단색이면(지금 글자 지우기가 LaMa 없이 칠하는 조건 — SolidPlan) 테두리를 못 읽은 풍선이거나 흰 바탕의 글이다 — 번역한다.
        // 실제 원고 248블록(2026-11-18, CTD 마스크): 테두리 규칙만으로는 칸에 붙은 풍선·흐린 테두리·흰 바탕의 대사 28블록이 빠졌다. 그림 위에 그려진 글만 남는다.
        var near = Enlarge(block, SolidRoiRatio, img.Width, img.Height);
        using var imNear = new Mat(img, near);
        using var mskNear = new Mat(textMask, near);
        if (Cv2.CountNonZero(mskNear) > 0 && SolidPlan(imNear, mskNear) is { } plain)
        {
            plain.Mask.Dispose();
            why = BubbleVerdict.PlainBackground;
            return true;
        }
        return false;
    }

    /// <summary><see cref="ClosedOutline"/>의 테두리 찾기 값 — 실제 원고 448블록(CTD 마스크, 2026-11-18)의 격자 탐색: BT 기본값(70/140·3px·전부 덮임)은 220블록이 풍선,
    /// 이 값(글 마스크 9px 깎기·80% 덮임·canny 40/100·9px 잇기·쪽 가장자리 허용)은 291. 13px 잇기는 테두리가 뭉쳐 192로 되레 준다.</summary>
    public const int BubbleTextErode = 9, BubbleCanny1 = 40, BubbleCanny2 = 100, BubbleEdgeDilate = 9;
    public const double BubbleCover = 0.8;

    /// <summary>글을 감싸는 닫힌 테두리가 창 안쪽에서 닫히고(창에 닿지 않고 — 쪽 가장자리에 잘린 풍선은 예외) 그 안이 그림으로 어지럽지 않은가 — <see cref="IsInBubble"/>의 첫 규칙.</summary>
    private static bool ClosedOutline(Mat im, Mat msk, Rect roi, Size img, out BubbleVerdict why)
    {
        why = BubbleVerdict.NoOutline;
        using var core = new Mat();
        using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(BubbleTextErode, BubbleTextErode));
        Cv2.Erode(msk, core, erodeKernel);
        using var ballonMask = ExtractBallonMask(im, msk, BubbleCanny1, BubbleCanny2, BubbleEdgeDilate, BubbleCover, Cv2.CountNonZero(core) > 0 ? core : null);
        if (ballonMask == null) return false;
        var bounds = Cv2.BoundingRect(ballonMask);
        why = BubbleVerdict.TouchesWindow;
        // 창에 닿음 = 닫힌 풍선이 아니다(억지로 닫은 사각형·칸). 창이 쪽 가장자리에 있는 쪽은 예외 — 쪽 가장자리에 잘린 풍선은 풍선이다
        bool left = bounds.X <= 1 && roi.X > 0, top = bounds.Y <= 1 && roi.Y > 0;
        bool right = bounds.Right >= im.Cols - 1 && roi.Right < img.Width, bottom = bounds.Bottom >= im.Rows - 1 && roi.Bottom < img.Height;
        if (left || top || right || bottom) return false;

        why = BubbleVerdict.Busy;
        using var nonText = new Mat();
        Cv2.BitwiseNot(msk, nonText);
        Cv2.BitwiseAnd(ballonMask, nonText, nonText);
        if (Cv2.CountNonZero(nonText) < 32) return false;
        Cv2.MeanStdDev(im, out _, out var std, nonText);
        if (Math.Max(std.Val0, Math.Max(std.Val1, std.Val2)) > BubbleInsideStdMax) return false;
        why = BubbleVerdict.Bubble;
        return true;
    }

    /// <summary>풍선 마스크(255=풍선 내부, 텍스트 포함). 못 찾으면 null.
    /// 기본값은 BT 그대로(글자 지우기 <see cref="TrySolidFill"/>가 쓴다 — 바꾸면 지움 결과가 달라진다). <see cref="IsInBubble"/>은 테두리를 더 잘 잇는 값을 넘긴다:
    /// <paramref name="core"/>(깎은 글 마스크)로 글 상자·덮임을 재고 <paramref name="cover"/>만큼만 덮여도 후보로 — CTD 마스크가 풍선 테두리에 닿아 '글을 전부 감싸는' 테두리를 못 찾던 것(실측 2026-11-18).</summary>
    private static Mat? ExtractBallonMask(Mat im, Mat textMask, int canny1 = 70, int canny2 = 140, int dilate = 3, double cover = 1.0, Mat? core = null)
    {
        int h = im.Rows, w = im.Cols;
        var measured = core ?? textMask;
        long textSum = Cv2.Sum(measured).Val0 > 0 ? (long)(Cv2.Sum(measured).Val0) : 0;
        if (textSum == 0) return null;

        using var blur = new Mat();
        Cv2.GaussianBlur(im, blur, new Size(3, 3), 0);
        using var canny = new Mat();
        Cv2.Canny(blur, canny, canny1, canny2, 3, true);
        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilate, dilate));
        Cv2.Dilate(canny, canny, dilateKernel);

        // 텍스트 bbox
        using var nz = new Mat();
        Cv2.FindNonZero(measured, nz);
        if (nz.Empty()) return null;
        var br = Cv2.BoundingRect(nz);

        // 닫히지 않은 풍선 대비: 가장자리에 흰 사각형을 둘러 강제로 닫는다
        Cv2.Rectangle(canny, new Rect(0, 0, w - 1, h - 1), Scalar.White, 1);
        using (var notText = new Mat())
        {
            if (dilate > 3)   // 기본 경로(TrySolidFill)는 3 — 거기서는 전과 같다. core가 비는 작은 글(9px 깎기에 다 깎인 글)도 띠는 빼야 한다(검수 2026-11-19)
            {
                // 글 획의 가장자리도 dilate만큼 번졌다 — 마스크 밖으로 삐져나온 그 띠가 글을 감싸는 '테두리'로 잡히지 않게 같은 폭으로 넓혀 뺀다(합성 시험 PlainBackground)
                using var wide = new Mat();
                using var wideKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilate, dilate));
                Cv2.Dilate(textMask, wide, wideKernel);
                Cv2.BitwiseNot(wide, notText);
            }
            else Cv2.BitwiseNot(textMask, notText);
            Cv2.BitwiseAnd(canny, notText, canny);
            // 글이 창 가장자리에 닿으면(쪽 가장자리의 블록 — 창이 쪽에 잘려 더 못 넓힌다) 위의 AND가 억지로 닫는 사각형까지 지워 풍선 안이 그림 밖과 이어지고,
            // FindContours는 그것을 구멍으로 보지 않아 풍선을 못 찾았다(합성 시험 BubbleCutByThePageEdge, 검수 2026-11-19). 풍선 판정 경로에서만 사각형을 다시 그린다 — 지우기 경로(dilate 3)는 전과 같다
            if (dilate > 3) Cv2.Rectangle(canny, new Rect(0, 0, w - 1, h - 1), Scalar.White, 1);
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
            Cv2.BitwiseAnd(tmp, measured, overlap);
            if ((long)Cv2.Sum(overlap).Val0 >= cover * textSum)
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

    /// <summary>사각형을 사방으로 px만큼 넓힌다(그림 안으로 자른다) — <see cref="IsInBubble"/>이 이 블록의 글만 남기는 자리의 최소 여백.</summary>
    private static Rect Pad(Rect r, int px, int imW, int imH)
    {
        int x1 = Math.Max(0, r.X - px), y1 = Math.Max(0, r.Y - px);
        int x2 = Math.Min(imW, r.Right + px), y2 = Math.Min(imH, r.Bottom + px);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    private static double StdOf(params double[] v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average());
    }
}
