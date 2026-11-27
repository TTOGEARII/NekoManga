using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>말풍선 휴리스틱 — BT의 extract_ballon_mask/check_need_inpaint 이식에서 출발.
/// 블록 주변에서 "텍스트를 전부 감싸는 가장 작은 닫힌 컨투어"를 풍선으로 보고, 글 획 둘레의 바탕이 단색이면 LaMa 없이 **획만** 그 색으로 칠한다(흰 풍선 대부분, 매우 빠름 — <see cref="TrySolidFill"/>).
/// 그 밖에 글자 지우기가 쓰는 판단들: 흰 테·갇힌 흰 틈(<see cref="TextHalo"/>), 테두리 있는 풍선의 안쪽(<see cref="DrawnOutlineInterior"/>), 구멍 둘레의 무늬 판정(<see cref="RegenStats"/>),
/// 감지기가 놓친 획(<see cref="AddMissedStrokes"/>), 풍선 안/밖(<see cref="IsInBubble"/>).</summary>
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

    /// <summary>이 블록의 글 획에 두른 흰 테(フチ)와 테에 갇힌 흰 틈의 마스크(<paramref name="window"/> 크기, 호출자가 해제) — 지울 자리에 더할 것. 더할 것이 없으면 null.
    /// 2026-11-26 사용자 보고: 그림·톤 위 대사·효과음은 획을 흰 테로 두르는데 감지기 마스크는 획만 덮어 테가 허연 글자 모양 얼룩으로 남았다(05쪽 "ぞくっ", 13쪽 "…えっち").
    /// 테 = 획 마스크 바로 밖 <see cref="HaloPx"/>px 안의 흰 픽셀.
    /// **테인지는 안팎을 견줘서 정한다**: 획 바로 밖(<see cref="HaloNearPx"/>px)의 흰 비율이 그 바깥 띠(HaloPx~HaloPx+HaloRingPx)의 흰 비율보다 <see cref="HaloContrast"/> 넘게 클 때만.
    /// 흰 풍선(안팎이 다 희다)도, 줄무늬 톤·줄무늬 풍선(안팎이 같은 무늬다)도 테가 아니다. 처음 규칙("바깥 띠의 흰 비율이 85% 이하면 더한다")은 줄무늬의 흰 틈을 8px까지 구멍에 넣어,
    /// 글이 꽉 찬 줄무늬 풍선에서 구멍이 풍선 테두리와 그 밖의 그림에 닿았다 — 메울 때 밖의 머리카락이 풍선 안으로 딸려 들어왔다(실제 26쪽 둘).
    /// 실측(획 바로 밖/바깥 띠): 줄무늬 풍선 0.46/0.57 · 0.52/0.52(차 ≤ 0) / 어두운 톤 위 흰 테 0.53/0.07 · 0.38/0.08 · 0.30/0.03 / 먹칠+줄무늬 0.48/0.25 / 옅은 톤·속도선 위의 흰 여백 0.97/0.75 · 0.97/0.76.
    /// 테를 더한 블록은 **테에 갇힌 흰 틈**(줄과 줄 사이까지 이어 깐 흰 판의 가운데)도 더한다 — 아래 본문.
    /// **테두리 선으로 둘러싸인 흰 풍선에 글이 꽉 찬 것은 테가 아니다**(<see cref="DrawnOutlineInterior"/> — 풍선 안쪽에서 다시 견준다, 본문).
    /// **이 블록의 획만 본다** — 지울 자리를 모으는 1.15배 창 안의 획에서 다른 블록의 상자(<paramref name="pageBlocks"/>)에 든 것을 뺀 것. 창(<paramref name="window"/>)에 들어온 다른 글에는 테를 두르지 않고,
    /// 그 글자는 흰 비율을 잴 때도 뺀다(검은 글자가 '덜 흰 바깥 띠'로 읽히지 않게). 전에는 창 안의 획 전부에 테를 둘렀다 — 구멍을 모으는 자리를 테가 닿는 데까지 넓히자
    /// 칸 테두리 너머 다른 칸의 효과음까지 이 블록의 구멍에 들어갔다(실제 05쪽 "ん", 2026-11-27).
    /// 호출부(<see cref="EmbeddedTranslator.EraseBlock"/>)는 이것을 지울 마스크에 더하고 구멍을 모으는 자리를 테가 닿는 데까지 넓힌다: 1.15배 창은 좁은 줄(폭 40px이면 좌우 3px)에서
    /// 테의 바깥쪽을 놓쳐 글 양옆에 1~3px의 흰 띠가 남았다(검토 2026-11-27).</summary>
    internal static Mat? TextHalo(Mat img, Mat textMask, Rect block, out Rect window, IReadOnlyList<Rect>? pageBlocks = null)
    {
        // 창: 블록의 1.3배 — 좁은 줄(폭 40px이면 좌우 6px)에서도 테(HaloPx)와 견줄 바깥 띠(HaloRingPx)·그 너머(HaloFarPx)가 다 들어오게 적어도 그만큼은 넓힌다
        var roi = window = Enlarge(block, 1.3, img.Width, img.Height) | Pad(block, HaloPx + HaloRingPx + HaloFarGapPx + HaloFarPx + 2, img.Width, img.Height);
        using var im = new Mat(img, roi);
        using var all = new Mat(textMask, roi);
        using var msk = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        var own = Enlarge(block, 1.15, img.Width, img.Height) & roi;
        var ownHere = new Rect(own.X - roi.X, own.Y - roi.Y, own.Width, own.Height);
        using (var from = new Mat(all, ownHere)) using (var to = new Mat(msk, ownHere)) from.CopyTo(to);
        if (pageBlocks != null)
        {
            // 다른 블록의 상자에 든 획은 그 블록의 것이다(제 상자와 겹친 자리는 제 것) — EraseBlock이 제 구멍을 가릴 때와 같은 규칙. 창(1.15배)에 걸쳐 들어온 옆 블록의 획에 테를 두르면
            // 그 블록의 흰 판·흰 풍선이 이 블록의 구멍에 들어가 이쪽의 무늬로 메워진다(검토 2026-11-27)
            using var theirs = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
            foreach (var other in pageBlocks)
            {
                if (other == block) continue;
                var cut = other & roi;
                if (cut.Width > 0 && cut.Height > 0) theirs.Rectangle(new Rect(cut.X - roi.X, cut.Y - roi.Y, cut.Width, cut.Height), Scalar.White, -1);
            }
            var mine = block & roi;
            if (mine.Width > 0 && mine.Height > 0) theirs.Rectangle(new Rect(mine.X - roi.X, mine.Y - roi.Y, mine.Width, mine.Height), Scalar.Black, -1);
            msk.SetTo(Scalar.Black, theirs);
        }
        if (Cv2.CountNonZero(msk) == 0) return null;
        using var others = new Mat();
        Cv2.Subtract(all, msk, others);
        using var gray = im.Channels() == 1 ? im.Clone() : im.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var white = new Mat();
        Cv2.Threshold(gray, white, HaloWhite, 255, ThresholdTypes.Binary);

        using var nearKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * HaloPx + 1, 2 * HaloPx + 1));
        using var near = new Mat();
        Cv2.Dilate(msk, near, nearKernel);
        using var farKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * (HaloPx + HaloRingPx) + 1, 2 * (HaloPx + HaloRingPx) + 1));
        using var far = new Mat();
        Cv2.Dilate(msk, far, farKernel);
        using var ring = new Mat();
        Cv2.Subtract(far, near, ring);   // 테 바깥의 띠
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * HaloNearPx + 1, 2 * HaloNearPx + 1));
        using var close = new Mat();
        Cv2.Dilate(msk, close, closeKernel);
        Cv2.Subtract(close, msk, close);   // 획 바로 밖
        using var hit = new Mat();
        // 띠의 흰 비율 — 다른 글의 자리는 세지 않는다(그 글의 검은 획이 '덜 흰 띠'로 읽히면 흰 종이 위의 글도 흰 테로 보인다). 띠가 비면 음수
        double WhiteShare(Mat band)
        {
            band.SetTo(Scalar.Black, others);
            int px = Cv2.CountNonZero(band);
            if (px == 0) return -1;
            Cv2.BitwiseAnd(band, white, hit);
            return Cv2.CountNonZero(hit) / (double)px;
        }
        double ringWhite = WhiteShare(ring), closeWhite = WhiteShare(close);
        if (ringWhite < 0 || closeWhite < 0) return null;
        if (closeWhite - ringWhite < HaloContrast) return null;
        // 흰 테는 흰 것이 거기서 끝난다 — 바깥 띠 너머가 다시 희면(흰 종이 위의 글 옆을 선 하나가 지나간다 · 흰 종이 위의 테두리 있는 풍선) 띠에 걸린 것은 그림의 선이지 테의 끝이 아니다.
        // 창을 넓히기 전에는 작은 블록의 띠가 창에 잘려 이런 자리가 걸러졌다(우연히). 실측(흰 테를 더한 실제 여섯 블록, 바로 밖/띠/너머): 0.97/0.76/0.70 · 0.31/0.03/0.02 · 0.97/0.75/0.74 ·
        // 0.50/0.23/0.22 · 0.53/0.07/0.06 · 0.38/0.08/0.07 — 너머는 띠와 같거나 더 어둡다(검토 2026-11-27)
        using (var beyondKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * (HaloPx + HaloRingPx + HaloFarGapPx + HaloFarPx) + 1, 2 * (HaloPx + HaloRingPx + HaloFarGapPx + HaloFarPx) + 1)))
        using (var gapKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * (HaloPx + HaloRingPx + HaloFarGapPx) + 1, 2 * (HaloPx + HaloRingPx + HaloFarGapPx) + 1)))
        using (var outer = new Mat()) using (var gap = new Mat())
        {
            Cv2.Dilate(msk, outer, beyondKernel);
            Cv2.Dilate(msk, gap, gapKernel);
            Cv2.Subtract(outer, gap, outer);
            double outerWhite = WhiteShare(outer);
            if (outerWhite >= 0 && closeWhite - outerWhite < HaloContrast) return null;
        }
        // 글이 꽉 찬 말풍선도 "바로 밖은 희고 바깥 띠는 덜 희다"(띠가 테두리와 그 밖에 걸친다). 그려진 선으로 둘러싸인 흰 자리는 풍선의 안쪽이다 —
        // 흰 테인지는 그 **안쪽에서** 다시 견준다: 안쪽에 든 바깥 띠도 희면(또는 띠가 안쪽에 아예 없으면) 테가 아니다. 안쪽의 띠가 덜 희면 무늬가 든 풍선 안의 흰 테 글이다(무늬는 뒤에서 이어 메운다).
        // 무늬가 얼마나 들었는가(비율)로 가르지 않는다: 글이 꽉 찬 풍선은 남은 흰 자리가 좁아, 귀퉁이의 빗금 몇 줄로도 비율이 넘어 풍선 안쪽이 통째로 구멍이 됐다.
        // 흰 자리가 창 끝에 닿았는가로도 가르지 않는다: 꼬리 달린 풍선·한쪽이 넉넉한 풍선·쪽 가장자리에 잘린 풍선이 다 닿는다(검토 2026-11-27 — 둘 다 한 번씩 넣었다 뺀 규칙)
        using (var interior = DrawnOutlineInterior(gray, msk, others))
            if (interior != null)
            {
                Cv2.BitwiseAnd(ring, interior, ring);
                double insideWhite = WhiteShare(ring);
                if (insideWhite < 0 || closeWhite - insideWhite < HaloContrast) return null;
            }

        var added = new Mat();
        try
        {
            Cv2.BitwiseAnd(near, white, added);   // 획 바로 밖의 흰 픽셀(마스크 안쪽은 이미 들어 있다)

            // 테에 갇힌 흰 틈(줄과 줄 사이·글 둘레에 구름처럼 깐 흰 판): 획에서 HaloPx씩 더하고도 남으면 글을 지운 자리에 판 모양의 흰 얼룩이 남는다(실제 13쪽 — 남은 덩어리 둘, 획에서 가장 먼 곳이 33·38px).
            // '꽉 찬 흰 면'(둘레 7×7이 전부 흰 픽셀 — 망점 톤의 점 사이 흰 틈은 아니다)의 덩어리 가운데, 테에 닿아 있고 통째로 획에서 PocketReachPx 안에 있는 것만.
            // 테에서 떨어진 흰 면(글 근처의 하이라이트)과 멀리까지 이어진 흰 면은 그림이다. 실측: 같은 블록의 다른 흰 덩어리는 테에 안 닿거나(가장 먼 곳 24px), 창 밖으로 이어진다(55~103px)
            using var solidKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * PocketSolidPx + 1, 2 * PocketSolidPx + 1));
            using var rest = new Mat();
            Cv2.Erode(white, rest, solidKernel);
            using (var covered = new Mat())
            {
                Cv2.BitwiseOr(all, added, covered);
                rest.SetTo(Scalar.Black, covered);
            }
            if (Cv2.CountNonZero(rest) > 0)
            {
                using var reachKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * PocketReachPx + 1, 2 * PocketReachPx + 1));
                using var beyond = new Mat();
                Cv2.Dilate(msk, beyond, reachKernel);
                Cv2.BitwiseNot(beyond, beyond);
                using var byHalo = new Mat();
                using (var touchKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * PocketTouchPx + 1, 2 * PocketTouchPx + 1)))
                    Cv2.Dilate(added, byHalo, touchKernel);
                using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
                int n = Cv2.ConnectedComponentsWithStats(rest, labels, stats, cent, PixelConnectivity.Connectivity8);
                using var pockets = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
                using var comp = new Mat(); using var outside = new Mat();
                bool any = false;
                for (int i = 1; i < n; i++)
                {
                    var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                        stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
                    if (box.X <= 0 || box.Y <= 0 || box.Right >= roi.Width || box.Bottom >= roi.Height) continue;   // 창 밖으로 이어진다
                    using var lab = new Mat(labels, box); using var away = new Mat(beyond, box); using var touch = new Mat(byHalo, box);
                    Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
                    Cv2.BitwiseAnd(comp, away, outside);
                    if (Cv2.CountNonZero(outside) > 0) continue;
                    Cv2.BitwiseAnd(comp, touch, outside);
                    if (Cv2.CountNonZero(outside) == 0) continue;   // 테에서 떨어져 있다
                    using var pocket = new Mat(pockets, box);
                    Cv2.BitwiseOr(pocket, comp, pocket);
                    any = true;
                }
                if (any)
                {
                    Cv2.Dilate(pockets, pockets, solidKernel);   // 깎아서 본 만큼 되돌린다 — 흰 픽셀로만
                    Cv2.BitwiseAnd(pockets, white, pockets);
                    Cv2.BitwiseOr(added, pockets, added);
                }
            }
            added.SetTo(Scalar.Black, others);   // 남의 글자 자리(그 마스크 밑의 흰 픽셀)는 그 글의 것이다 — 이 블록의 구멍에 넣지 않는다
            if (Cv2.CountNonZero(added) == 0) { added.Dispose(); return null; }
            return added;
        }
        catch { added.Dispose(); throw; }
    }

    /// <summary>글 둘레의 흰 자리가 **그려진 테두리 선**으로 둘러싸여 있으면(테두리 있는 말풍선 안의 글) 그 안쪽(흰 자리 + 그 사이를 지나는 가는 선, 창 크기의 마스크 — 호출자가 해제), 아니면 null.
    /// 흰 테(フチ)는 선 없이 톤·그림·먹칠에 바로 닿는다. 가르는 법:
    /// ① 글에 닿은 '꽉 찬 흰 면'(5×5 열림 — 망점의 흰 틈은 턴다)을 모으고, 그 사이를 지나는 가는 것(7×7 닫힘)까지를 **안쪽**으로 본다. 줄무늬·빗금·속도선의 가는 줄은 양쪽이 다 흰 틈이라 안쪽에 묻힌다 —
    ///    줄을 '둘러싼 선'으로 세면 줄무늬 위의 흰 테 글이 풍선으로 읽힌다(틈이 5px 넘는 줄무늬에서 흰 틈이 창 끝까지 이어져, 그 가장자리인 줄들이 전부 선으로 세어졌다).
    ///    글에 조금만 닿은(가장 많이 닿은 면의 <see cref="OutlineContactShare"/> 미만) 흰 면은 뺀다 — 테두리를 살짝 넘은 마스크가 스친 풍선 밖의 흰 종이다
    ///    (넣으면 사이의 테두리가 안쪽에 묻혀 풍선이 안 보인다).
    /// ② 안쪽의 가장자리(글과 맞닿은 곳은 빼고 — 글이 테두리를 넘어간 자리의 마스크 가장자리는 풍선 밖이다) 바로 밖 4px에 선처럼 이어진 어두운 것(넓이 12px·길이 8px 이상 — 망점·줄무늬의 끝은
    ///    작은 조각이다)이 <see cref="OutlineCover"/> 이상 둘러 있고 — ③ 양쪽이 다 안쪽인 선은 세지 않는다(굵은 줄무늬의 줄은 닫힘에 안 묻히지만 양쪽이 다 흰 틈이다, <see cref="OutlineBothSides"/>) —
    /// ④ 그 너머(8~12px)는 어둡지 않을 때(먹칠 위의 흰 테는 너머도 어둡다).
    /// '흰'의 문턱은 글 바로 밖의 종이 밝기에서 잰다(띠의 밝은 쪽 — <see cref="OutlinePaperRank"/> 분위 — 에서 <see cref="OutlinePaperTol"/>을 뺀 값): 누런 스캔·옅은 회색 풍선도 풍선이다.
    /// 가운뎃값으로 재지 않는다 — 글이 테두리를 넘으면 띠의 절반 넘게가 테두리와 풍선 밖이다. 깨끗한 쪽(종이 255)이면 전과 같은 225다.
    /// <paramref name="others"/>(다른 글의 획 — 같은 풍선의 옆 줄, 옆에 붙은 다른 블록)는 없는 셈 친다(흰 종이로 본다): 그 글자의 검은 획이 '둘러싼 선'으로도 '어두운 너머'로도 세어지지 않게.
    /// 실측(실제 세 쪽, 2026-11-27 — 흰 테를 재는 창에서): 흰 테를 더한 여섯 블록은 전부 안쪽이 없다(선이 둘러 있는 비율 0.15·0.18 / 0.77·0.79·1.00인 셋은 너머의 어두운 비율이 0.80·0.57·0.97 /
    /// 하나는 흰 면이 없다). 테두리 있는 흰 풍선 열한 블록은 둘러 있는 비율 1.00, 너머 0.00~0.42. 빗금이 지나가는 풍선 둘은 0.68·0.81이라 풍선으로 읽지 않는다(고리를 좁히지 않는다 — 무늬로 메운다).
    /// 알고 둔 것: 먹칠·짙은 톤 위에 놓인 꽉 찬 풍선은 흰 테와 가르지 못한다(너머가 어둡다). 흰 테 바깥에 가는 검은 선을 한 번 더 두른 글(이중 테)은 풍선으로 읽혀 테가 남는다 —
    /// 풍선 안쪽을 통째로 지우는 쪽이 더 큰 사고라 그쪽을 막는다.</summary>
    internal static Mat? DrawnOutlineInterior(Mat gray, Mat msk, Mat? others = null)
    {
        int w = gray.Cols, h = gray.Rows;
        using var plain = others == null ? null : gray.Clone();
        plain?.SetTo(new Scalar(255), others!);
        var page = plain ?? gray;
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var e3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        using var e4 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        using var touch = new Mat();
        Cv2.Dilate(msk, touch, e3);

        // 종이 밝기 = 글 바로 밖 3px 띠의 밝은 쪽(잉크·테두리·풍선 밖이 띠의 대부분이어도 종이가 한 줌은 있다)
        int paper;
        using (var beside = new Mat())
        {
            Cv2.Subtract(touch, msk, beside);
            if (others != null) beside.SetTo(Scalar.Black, others);
            paper = LevelAt(page, beside, OutlinePaperRank);
        }
        if (paper < 0) return null;   // 글 바로 밖에 잴 자리가 없다
        using var white = new Mat(); using var dark = new Mat();
        Cv2.Threshold(page, white, paper - OutlinePaperTol, 255, ThresholdTypes.Binary);
        Cv2.Threshold(page, dark, OutlineDark - 1, 255, ThresholdTypes.BinaryInv);
        using var solid = new Mat();
        using (var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5))) Cv2.MorphologyEx(white, solid, MorphTypes.Open, k5);
        solid.SetTo(Scalar.Black, msk);

        // ① 글에 닿은 흰 면
        using var labels = new Mat();
        int n = Cv2.ConnectedComponents(solid, labels, PixelConnectivity.Connectivity8);
        if (n <= 1) return null;
        labels.GetArray(out int[] lab);
        using var touchFlat = touch.Clone();   // msk는 ROI일 수 있다 — 이어진 메모리로
        touchFlat.GetArray(out byte[] near);
        // 글에 **제대로** 닿은 흰 면만 모은다(닿은 길이가 가장 많이 닿은 면의 OutlineContactShare 이상): 테두리를 살짝 넘은 마스크는 풍선 밖의 흰 종이에도 몇 px 닿는다 —
        // 그쪽까지 넣으면 닫힘이 사이의 테두리를 안쪽에 묻어 풍선이 안 보인다.
        // 알고 둔 것: 풍선 안에서 글까지 이어진 빗금 사이의 흰 칸도 조금만 닿아 빠진다 — 그 빗금은 RegenStats의 고리에 안 세어져 LaMa로 간다. '창 안에 갇힌 칸은 넣는다'로 풀어 봤더니
        // 실제 쪽(속도선 위의 뾰족한 흰 풍선, 글이 테두리에 닿았다)에서 풍선 밖 속도선 사이의 흰 틈이 안쪽으로 들어와 풍선이 안 보였고, 풍선 안에 속도선이 그려졌다(2026-11-27 — 되돌림)
        var contact = new int[n];
        for (int i = 0; i < lab.Length; i++) if (lab[i] != 0 && near[i] != 0) contact[lab[i]]++;
        int most = contact.Max();
        if (most == 0) return null;
        var zoneBytes = new byte[lab.Length];
        for (int i = 0; i < lab.Length; i++) if (lab[i] != 0 && contact[lab[i]] >= OutlineContactShare * most) zoneBytes[i] = 255;
        using var zone = new Mat(h, w, MatType.CV_8UC1);
        zone.SetArray(zoneBytes);
        // 안쪽 = 흰 면 + 그 사이를 지나는 가는 것(양쪽이 다 흰 면인 줄 — 풍선 안의 빗금, 줄무늬·속도선의 줄)
        var inside = new Mat();
        try
        {
            using (var k7 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7))) Cv2.MorphologyEx(zone, inside, MorphTypes.Close, k7);

            // ② 안쪽의 가장자리에 선이 둘러 있는가
            using var z = new Mat();
            Cv2.BitwiseOr(inside, msk, z);
            using var edge = new Mat();
            Cv2.Dilate(inside, edge, k3);
            edge.SetTo(Scalar.Black, z);
            int edgePx = Cv2.CountNonZero(edge);
            if (edgePx < 40) { inside.Dispose(); return null; }
            using var band = new Mat();
            Cv2.Dilate(z, band, e4);
            band.SetTo(Scalar.Black, z);
            Cv2.BitwiseAnd(band, dark, band);
            using var bl = new Mat(); using var bs = new Mat(); using var bc = new Mat();
            int bn = Cv2.ConnectedComponentsWithStats(band, bl, bs, bc, PixelConnectivity.Connectivity8);
            using var line = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
            using var comp = new Mat(); using var beside = new Mat();
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            for (int i = 1; i < bn; i++)
            {
                int cw = bs.At<int>(i, (int)ConnectedComponentsTypes.Width), ch = bs.At<int>(i, (int)ConnectedComponentsTypes.Height);
                if (bs.At<int>(i, (int)ConnectedComponentsTypes.Area) < 12 || Math.Max(cw, ch) < 8) continue;
                // ③ 선의 양쪽이 다 안쪽이면 풍선의 테두리가 아니라 무늬의 줄이다(굵기 7px 넘는 줄무늬 — 닫힘에 안 묻힌다): 그런 선은 '둘러싼 선'으로 세지 않는다.
                // 테두리는 한쪽만 안쪽이다(선 옆 2px의 절반쯤). 선마다 따로 본다 — 줄무늬에서도 흰 자리의 맨 위·맨 아래 줄은 한쪽만 안쪽이라, 한데 재면 섞여서 문턱에 걸친다
                var box = new Rect(bs.At<int>(i, (int)ConnectedComponentsTypes.Left) - 2, bs.At<int>(i, (int)ConnectedComponentsTypes.Top) - 2, cw + 4, ch + 4) & new Rect(0, 0, w, h);
                using var l = new Mat(bl, box); using var dst = new Mat(line, box); using var zHere = new Mat(z, box);
                Cv2.InRange(l, new Scalar(i), new Scalar(i), comp);
                Cv2.Dilate(comp, beside, k5);
                beside.SetTo(Scalar.Black, comp);
                int besidePx = Cv2.CountNonZero(beside);
                Cv2.BitwiseAnd(beside, zHere, beside);
                if (besidePx > 0 && Cv2.CountNonZero(beside) >= OutlineBothSides * besidePx) continue;
                Cv2.BitwiseOr(dst, comp, dst);
            }
            Cv2.Dilate(line, line, e3);
            Cv2.BitwiseAnd(line, edge, line);
            if (Cv2.CountNonZero(line) < OutlineCover * edgePx) { inside.Dispose(); return null; }
            // ④ 선 너머가 어두우면 선이 아니라 면(먹칠 위의 흰 테)
            using var farOuter = new Mat(); using var farInner = new Mat();
            using (var e12 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(25, 25)))
            using (var e8 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(17, 17)))
            {
                Cv2.Dilate(z, farOuter, e12);
                Cv2.Dilate(z, farInner, e8);
            }
            Cv2.Subtract(farOuter, farInner, farOuter);
            int farPx = Cv2.CountNonZero(farOuter);
            Cv2.BitwiseAnd(farOuter, dark, farOuter);
            if (farPx > 0 && Cv2.CountNonZero(farOuter) >= OutlineFarDark * farPx) { inside.Dispose(); return null; }
            return inside;
        }
        catch { inside.Dispose(); throw; }
    }

    /// <summary>마스크 안 픽셀의 밝기 가운뎃값(0~255) — 마스크가 비었으면 -1.</summary>
    internal static int MedianOf(Mat gray, Mat mask) => LevelAt(gray, mask, 0.5);

    /// <summary>마스크 안 픽셀을 어두운 것부터 줄 세웠을 때 <paramref name="rank"/>(0~1) 자리의 밝기(0~255) — 마스크가 비었으면 -1.</summary>
    private static int LevelAt(Mat gray, Mat mask, double rank)
    {
        int n = Cv2.CountNonZero(mask);
        if (n == 0) return -1;
        using var hist = new Mat();
        Cv2.CalcHist(new[] { gray }, new[] { 0 }, mask, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        float seen = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += hist.At<float>(v);
            if (seen >= rank * n) return v;
        }
        return 255;
    }

    /// <summary><see cref="DrawnOutlineInterior"/>: 어두운 것(선)으로 보는 밝기(미만) · 종이보다 이만큼 어두운 데까지 흰 자리로 본다(종이 255면 225).</summary>
    internal const int OutlineDark = 110, OutlinePaperTol = 30;
    /// <summary><see cref="DrawnOutlineInterior"/>: 안쪽의 가장자리 가운데 선이 둘러 있어야 하는 비율 · 선 너머(8~12px)의 어두운 비율이 이 이상이면 선이 아니라 면 ·
    /// 글에 닿은 길이가 가장 많이 닿은 흰 면의 이 비율은 돼야 글 둘레의 흰 자리로 친다 · 선 옆 2px의 이 비율 넘게 안쪽이면 선의 양쪽이 다 흰 면(무늬의 줄) ·
    /// 종이 밝기를 읽는 자리(글 바로 밖 띠의 밝기 분위).</summary>
    internal const double OutlineCover = 0.85, OutlineFarDark = 0.5, OutlineContactShare = 0.2, OutlineBothSides = 0.75, OutlinePaperRank = 0.9;

    /// <summary><see cref="TextHalo"/>: 테로 보는 거리(획 마스크 밖 px) · 견주는 바깥 띠의 폭 · 흰 문턱 · '획 바로 밖'으로 재는 거리.</summary>
    internal const int HaloPx = 8, HaloRingPx = 6, HaloWhite = 225, HaloNearPx = 4;
    /// <summary>바깥 띠 너머 — 띠에서 <see cref="HaloFarGapPx"/> 띄운 <see cref="HaloFarPx"/>px 폭의 띠(획에서 16~24px). 여기가 다시 희면 흰 테가 아니다.</summary>
    internal const int HaloFarGapPx = 2, HaloFarPx = 8;
    /// <summary>획 바로 밖의 흰 비율이 바깥 띠보다 이만큼 넘게 커야 흰 테다. 실측의 '테' 쪽 최솟값 0.21과 '테 아님' 쪽 최댓값 0.00 사이.</summary>
    internal const double HaloContrast = 0.15;
    /// <summary><see cref="TextHalo"/>의 갇힌 흰 틈: '꽉 찬 흰 면'으로 보는 둘레 반지름(7×7) · 덩어리가 통째로 획에서 이 안에 있어야 틈이다 · 테에 닿았다고 보는 거리.</summary>
    internal const int PocketSolidPx = 3, PocketReachPx = 40, PocketTouchPx = 4;

    /// <summary>이 블록의 글자 지운 자리 둘레에 무늬·그림이 있는가 — 있으면 둘레의 무늬로 메우고(TextureFill, 2026-11-27) 켜 두면 SDXL로 다시 그린다(TextRegen, 2026-11-26).
    /// 구멍 = <paramref name="area"/>(쪽 좌표) 크기의 마스크 <paramref name="own"/> — 블록의 1.15배 창 안의 지울 마스크에, 창 밖으로 나간 제 흰 테를 더한 것(<see cref="EmbeddedTranslator.EraseBlock"/>이 짓는다.
    /// 넓어진 자리의 남의 획은 이 블록의 구멍이 아니다). <paramref name="eraseMask"/>(쪽 전체의 지울 마스크)는 고리에서 다른 글자를 빼는 데만 쓴다.
    /// 구멍 **바로 밖의 좁은 고리**(<see cref="RegenRimInner"/>~<see cref="RegenRimOuter"/>px)가 **평평하면**(거의 전부가 한 가지 밝기 — 흰 풍선 안·빈 종이·먹 바탕·옅은 회색 풍선) LaMa로 충분하다.
    /// 그 밖(선·톤·빗금이 구멍에 맞닿은 자리)이 대상이다. 돌려주는 흰(≥ <see cref="RegenWhite"/>)·검은(≤ <see cref="RegenBlack"/>) 비율은 진단용 수치다.
    /// LaMa 만화판이 못 하는 것은 구멍을 지나가는 가는 선을 잇는 일이다(실제 쪽 05·26: 속도선·빗금 위 글이 뿌옇게 뭉개졌다).
    /// 넓은 띠(<see cref="BandPx"/> 15)로 재지 않는다: 흰 풍선의 테두리와 풍선 밖 그림이 띠에 들어와 빗금 든 풍선과 같은 점수가 된다(설계 검토 2026-11-26).
    /// 지울 마스크에 같이 든 다른 블록의 글자는 고리에서 뺀다 — 옆 줄의 글자를 "그림"으로 세지 않게. <paramref name="img"/>는 **지우기 전** 그림이어야 한다.</summary>
    internal static (bool Wanted, double White, double Black) RegenStats(Mat img, Mat eraseMask, Rect area, Mat own)
    {
        var bounds = new Rect(0, 0, img.Width, img.Height);
        var inner = area;
        if (inner.Width <= 0 || inner.Height <= 0 || (inner & bounds) != inner || own.Size() != inner.Size) return (false, 0, 0);
        int pad = RegenRimOuter + 2;
        var roi = new Rect(inner.X - pad, inner.Y - pad, inner.Width + 2 * pad, inner.Height + 2 * pad) & bounds;
        using var hole = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        using (var dst = new Mat(hole, new Rect(inner.X - roi.X, inner.Y - roi.Y, inner.Width, inner.Height)))
            own.CopyTo(dst);
        if (Cv2.CountNonZero(hole) == 0) return (false, 0, 0);

        using var outerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * RegenRimOuter + 1, 2 * RegenRimOuter + 1));
        using var innerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * RegenRimInner + 1, 2 * RegenRimInner + 1));
        using var far = new Mat(); using var near = new Mat();
        Cv2.Dilate(hole, far, outerKernel);
        Cv2.Dilate(hole, near, innerKernel);
        using var rim = new Mat();
        Cv2.Subtract(far, near, rim);
        using var others = new Mat();   // 이 블록의 구멍이 아닌 지울 자리(다른 글)
        using (var every = new Mat(eraseMask, roi)) using (var notText = new Mat())
        {
            Cv2.BitwiseNot(every, notText);
            Cv2.BitwiseAnd(rim, notText, rim);
            Cv2.Subtract(every, hole, others);
        }
        int n = Cv2.CountNonZero(rim);
        if (n < 32) return (false, 0, 0);

        using var im = new Mat(img, roi);
        using var gray = im.Channels() == 1 ? im.Clone() : im.CvtColor(ColorConversionCodes.BGR2GRAY);
        // 테두리 있는 말풍선 안의 글이면 고리를 풍선 안쪽에서만 잰다(검토 2026-11-27): 글이 테두리에 닿은 흰 풍선은 고리에 테두리와 풍선 밖이 들어와 '그림이 맞닿은 자리'로 읽혔다 —
        // 그것은 풍선의 끝이지 구멍을 지나가는 그림이 아니다(v1.18.0은 그런 풍선을 LaMa로 지웠다). 풍선 안의 흰 자리를 지나가는 빗금은 안쪽에 들어 있어 전처럼 센다
        // (글까지 이어진 빗금은 못 센다 — 빗금 사이의 흰 칸이 글에 조금만 닿아 안쪽에서 빠진다. DrawnOutlineInterior의 '알고 둔 것').
        // 안쪽에 고리가 거의 없으면(글이 풍선을 꽉 채웠다) 평평한 바탕으로 본다
        using (var inside = DrawnOutlineInterior(gray, hole, others))
            if (inside != null)
            {
                Cv2.BitwiseAnd(rim, inside, rim);
                n = Cv2.CountNonZero(rim);
                if (n < 32) return (false, 1, 0);
            }
        using var white = new Mat(); using var black = new Mat();
        Cv2.Threshold(gray, white, RegenWhite - 1, 255, ThresholdTypes.Binary);      // > 224 = ≥ 225
        Cv2.Threshold(gray, black, RegenBlack, 255, ThresholdTypes.BinaryInv);       // ≤ 30
        Cv2.BitwiseAnd(white, rim, white);
        Cv2.BitwiseAnd(black, rim, black);
        double w = Cv2.CountNonZero(white) / (double)n, b = Cv2.CountNonZero(black) / (double)n;
        // 평평한가 = 고리의 거의 전부가 **한 가지 밝기**(가운뎃값 ± RegenFlatTol)인가. 흰 종이(가운뎃값 255)면 '225 이상', 먹(0)이면 '30 이하'와 같은 말이라 깨끗한 쪽의 판정은 전과 같다.
        // 희다·검다의 절대 문턱만 보던 때는 누런 스캔·옅은 회색 풍선의 고리(31~224)가 흰 것도 검은 것도 아니라, v1.18.0이 LaMa로 지우던 블록이 전부 대상이 됐다(검토 2026-11-27)
        int level = MedianOf(gray, rim);
        using var same = new Mat();
        Cv2.InRange(gray, new Scalar(level - RegenFlatTol), new Scalar(level + RegenFlatTol), same);
        Cv2.BitwiseAnd(same, rim, same);
        return (Cv2.CountNonZero(same) < RegenFlatRatio * n, w, b);
    }

    /// <summary>감지기가 놓친 글 획을 지울 글의 획 마스크 <paramref name="strokes"/>에 더한다. 돌려주는 값 = 더한 획의 픽셀 수(넓히기 전), 없으면 0.
    /// 2026-11-27 사용자 "이런 부분은 잘 지워지지도 않았어"(26쪽의 줄무늬 풍선): 칸 테두리에 잘린 줄의 아래 글자 셋과 가운데 줄의 작은 글자 하나가 감지기 마스크에 없어 그대로 남았다.
    /// 블록 상자 안의 '굵고 어두운 성분'(밝기 &lt; <see cref="MissedDark"/>, 3×3 열림으로 1~2px 줄무늬·가는 선을 턴 것) 가운데, 마스크에 닿은(또는 그렇게 더한 것에 닿은) 글자 꼴 성분을
    /// 감지기 마스크처럼 <see cref="MissedGrowPx"/> 넓혀 더한다. 상자 밖은 건드리지 않는다(칸 테두리에 붙은 글자는 상자의 변에서 잘라 본다 — 감지기 상자가 테두리 바로 옆에서 시작한다).
    /// **상자가 깨끗할 때만 한다**: 마스크가 덮지 않은 성분 가운데 하나라도 글자답지 않으면 — 글자보다 크다·굵다, 상자의 변에 잘렸는데 그 변에 마스크가 닿지 않는다,
    /// 두 변에서 잘렸다(모서리를 지나는 풍선 테두리. 칸에 잘린 글자는 한 변에서만 잘린다), 변에 잘린 것이 글 획보다 한참 가늘다 — 그 블록은 손대지 않는다.
    /// 그런 성분이 글자 수에 견줘 많아도(톤의 점) 하지 않는다. 그림 위의 글·손글씨 효과음처럼 상자에 그림이 섞인 블록은 어느 것이 글인지 가릴 수 없다 — 그림 조각을 지우느니 놓친 글자를 남긴다.
    /// 실측(실제 세 쪽 33블록, 2026-11-27): 더한 블록 둘(26쪽의 줄무늬 풍선 둘 — 놓친 글자 여덟 조각, 반만 덮인 글자 둘) / 그림이 섞여 손대지 않은 블록 14 / 놓친 것이 없는 블록 17.
    /// '같은 줄에 글자 한 칸 틈으로 이어진다'로 더하는 규칙은 쓰지 않는다 — 풍선 테두리의 호와 속도선 조각을 글로 잡았다(05쪽 둘, 26쪽 하나). 마스크에 닿은 것만.
    /// **덜 덮인 굵은 글자**: 온전히 덮인 글자(<see cref="MissedCovered"/>)가 하나도 없는 상자는 견줄 기준이 없다 — 어두운 성분이 전부 <see cref="MissedUnderCovered"/> 넘게 덮였을 때만
    /// 그것들을 글자로 보고 마저 덮는다(본문).</summary>
    internal static int AddMissedStrokes(Mat img, Mat strokes, Rect block)
    {
        block &= new Rect(0, 0, img.Width, img.Height);
        if (block.Width < 4 || block.Height < 4) return 0;
        using var msk = new Mat(strokes, block);
        if (Cv2.CountNonZero(msk) == 0) return 0;
        using var im = new Mat(img, block);
        using var gray = im.Channels() == 1 ? im.Clone() : im.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var thick = new Mat();
        Cv2.Threshold(gray, thick, MissedDark - 1, 255, ThresholdTypes.BinaryInv);
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(thick, thick, MorphTypes.Open, openKernel);

        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(thick, labels, stats, cent, PixelConnectivity.Connectivity8);
        if (n <= 1) return 0;
        using var dist = new Mat();
        Cv2.DistanceTransform(thick, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);

        // 성분을 둘로: 마스크가 덮은 것(글자 — 굵기·크기의 기준)과 덮지 않은 것(후보)
        var glyphs = new List<(double Thick, double Dim, double Area)>();
        var extras = new List<(int Label, Rect Box, int Area, double Thick, int Covered)>();
        using var comp = new Mat(); using var hit = new Mat();
        for (int i = 1; i < n; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < MissedMinArea) continue;
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            using var lab = new Mat(labels, box); using var d = new Mat(dist, box); using var m = new Mat(msk, box);
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            Cv2.MinMaxLoc(d, out _, out double half, out _, out _, comp);
            Cv2.BitwiseAnd(comp, m, hit);
            int covered = Cv2.CountNonZero(hit);
            if (covered >= MissedCovered * area) glyphs.Add((half * 2, Math.Max(box.Width, box.Height), area));
            else extras.Add((i, box, area, half * 2, covered));
        }
        if (extras.Count == 0) return 0;
        // 온전히 덮인 글자가 하나도 없다 — 굵은 글자는 감지기 마스크가 획의 속만 덮는다(실제 26쪽 "だめ": 두 글자가 붙은 한 덩어리, 덮인 비율 0.61 — 남은 획이 구멍을 둘러싸 메울 때 먹이 번졌다).
        // 견줄 글자가 없으니 '글자답다'는 못 따진다: 상자 안의 어두운 것이 전부 절반 넘게 덮였을 때만 그것들을 글자로 보고 마저 덮는다. 먹 바탕의 흰 글은 바탕이 0.20~0.34만 덮인다(실측 넷)
        bool underCovered = glyphs.Count == 0;
        if (underCovered && extras.Any(e => e.Covered < MissedUnderCovered * e.Area)) return 0;
        if (!underCovered && extras.Count > Math.Max(MissedMaxCount, MissedMaxPerGlyph * glyphs.Count)) return 0;   // 톤의 점처럼 잔 것이 깔린 상자

        // 기준은 넓이로 무게를 준 가운뎃값 — 마스크에 든 잔 조각(탁점·톤의 점)이 기준을 끌어내리지 않게
        double stroke = underCovered ? 0 : WeightedMedian(glyphs.Select(g => (g.Thick, g.Area)));
        double dim = underCovered ? 0 : WeightedMedian(glyphs.Select(g => (g.Dim, g.Area)));
        bool SideTouched(Rect side) { using var s = new Mat(msk, side); return Cv2.CountNonZero(s) > 0; }
        int bw = block.Width, bh = block.Height;
        bool left = SideTouched(new Rect(0, 0, 2, bh)), right = SideTouched(new Rect(bw - 2, 0, 2, bh)),
             top = SideTouched(new Rect(0, 0, bw, 2)), bottom = SideTouched(new Rect(0, bh - 2, bw, 2));
        foreach (var e in underCovered ? [] : extras)
        {
            bool cutL = e.Box.X <= 0, cutT = e.Box.Y <= 0, cutR = e.Box.Right >= bw, cutB = e.Box.Bottom >= bh;
            int cuts = (cutL ? 1 : 0) + (cutT ? 1 : 0) + (cutR ? 1 : 0) + (cutB ? 1 : 0);
            bool onMaskSide = (!cutL || left) && (!cutT || top) && (!cutR || right) && (!cutB || bottom);
            bool glyphLike = Math.Max(e.Box.Width, e.Box.Height) <= MissedSizeMax * dim && e.Thick <= MissedThickMax * stroke
                && (cuts == 0 || (cuts == 1 && onMaskSide && e.Thick >= MissedCutThickMin * stroke));
            if (!glyphLike) return 0;   // 상자에 글 아닌 것이 있다 — 손대지 않는다
        }

        // 마스크에 닿은 것부터 더하고, 더한 것에 닿은 것을 이어서 더한다(반만 덮인 줄의 아래 글자들)
        using var add = new Mat(bh, bw, MatType.CV_8UC1, Scalar.Black);
        using var cur = msk.Clone();
        using var near = new Mat();
        using var touchKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * MissedTouchPx + 1, 2 * MissedTouchPx + 1));
        using var growKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * MissedGrowPx + 1, 2 * MissedGrowPx + 1));
        int added = 0;
        for (bool changed = true; changed && extras.Count > 0;)
        {
            changed = false;
            Cv2.Dilate(cur, near, touchKernel);
            for (int k = extras.Count - 1; k >= 0; k--)
            {
                var e = extras[k];
                using var lab = new Mat(labels, e.Box); using var nearRoi = new Mat(near, e.Box);
                Cv2.InRange(lab, new Scalar(e.Label), new Scalar(e.Label), comp);
                Cv2.BitwiseAnd(comp, nearRoi, hit);
                if (Cv2.CountNonZero(hit) == 0) continue;
                using var addRoi = new Mat(add, e.Box);
                Cv2.BitwiseOr(addRoi, comp, addRoi);
                added += e.Area;
                extras.RemoveAt(k);
                changed = true;
            }
            if (!changed) break;
            Cv2.Dilate(add, cur, growKernel);
            Cv2.BitwiseOr(cur, msk, cur);
        }
        if (added == 0) return 0;
        Cv2.Dilate(add, add, growKernel);
        Cv2.BitwiseOr(msk, add, msk);
        return added;
    }

    /// <summary>어느 지울 블록의 것도 아닌 획을 지울 글의 획 마스크 <paramref name="strokes"/>에서 비운다. 돌려주는 값 = 비운 픽셀 수.
    /// 감지기는 상자로 안 잡은 글(손글씨 효과음)의 획도 마스크에는 넣는다. 그런 획이 대사 블록의 지우기 창(1.15배)에 걸리면 걸린 만큼만 지워져 반 토막이 남고,
    /// 엇갈린 기둥을 합친 블록은 감싼 상자의 빈 모서리에 든 효과음을 제 글처럼 지웠다(2026-11-27 실제 05쪽: 속도선 위 대사 옆의 "ちゅ" — v1.18.0도 그랬다).
    /// <paramref name="owners"/>: 지울 블록마다 그 블록의 상자들(<see cref="EmbeddedTranslator.OwnBoxes"/> — 감지기 상자 둘 이상을 합친 블록은 그 상자들, 아니면 블록 자신).
    /// **상자 안의 획은 늘 그 블록의 것이다.** 상자 밖으로 나간 조각(상자를 뺀 획의 성분)만 따진다: ① 어느 상자의 1.15배 창 **안에 통째로** 들면 남긴다
    /// (상자가 놓친 문장 부호·마스크가 상자보다 조금 넓은 가장자리 — 전처럼 그 블록이 지운다) ② 그 조각이 딸린 획 성분의 넓이 가운데 <see cref="OwnShare"/> 이상이 상자 안이면 남긴다
    /// (창 밖으로 삐져나간 제 글자). 둘 다 아니면 남의 획이다 — 번역하지 않는 글은 지우지도 않는다. 대사의 획과 효과음의 획이 마스크에서 한 덩어리로 붙어 있어도
    /// 상자 안의 대사는 남고 밖의 효과음 쪽만 빠진다.</summary>
    internal static int DropUnownedStrokes(Mat strokes, IReadOnlyList<IReadOnlyList<Rect>> owners)
    {
        if (owners.Count == 0) return 0;   // 지울 블록이 없으면 가릴 것도 없다
        int w = strokes.Cols, h = strokes.Rows;
        var bounds = new Rect(0, 0, w, h);
        // 지울 블록들의 상자(전부 합쳐서 — 두 블록에 반씩 걸친 성분도 누군가의 것이다)와 창
        using var boxes = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
        var windows = new List<Rect>();
        foreach (var parts in owners)
            foreach (var p in parts)
            {
                var box = p & bounds;
                if (box.Width <= 0 || box.Height <= 0) continue;
                boxes.Rectangle(box, Scalar.White, -1);
                windows.Add(Enlarge(p, 1.15, w, h));
            }
        // 상자 안의 획은 늘 그 블록의 것이다 — 따질 것은 상자 밖으로 나간 조각뿐
        using var outside = new Mat();
        using (var notBoxes = new Mat())
        {
            Cv2.BitwiseNot(boxes, notBoxes);
            Cv2.BitwiseAnd(strokes, notBoxes, outside);
        }
        if (Cv2.CountNonZero(outside) == 0) return 0;

        using var whole = new Mat(); using var wholeStats = new Mat(); using var cent = new Mat();
        int wholeCount = Cv2.ConnectedComponentsWithStats(strokes, whole, wholeStats, cent, PixelConnectivity.Connectivity8);
        using var pieces = new Mat(); using var pieceStats = new Mat();
        int pieceCount = Cv2.ConnectedComponentsWithStats(outside, pieces, pieceStats, cent, PixelConnectivity.Connectivity8);
        whole.GetArray(out int[] wholeOf);
        pieces.GetArray(out int[] pieceOf);
        boxes.GetArray(out byte[] inBox);
        var inside = new int[wholeCount];    // 성분마다 상자 안에 든 픽셀 수
        var parent = new int[pieceCount];    // 조각이 딸린 성분
        for (int i = 0; i < wholeOf.Length; i++)
        {
            if (wholeOf[i] == 0) continue;
            if (inBox[i] != 0) inside[wholeOf[i]]++;
            else parent[pieceOf[i]] = wholeOf[i];
        }

        var drop = new bool[pieceCount];
        bool any = false;
        for (int i = 1; i < pieceCount; i++)
        {
            var box = new Rect(pieceStats.At<int>(i, (int)ConnectedComponentsTypes.Left), pieceStats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                pieceStats.At<int>(i, (int)ConnectedComponentsTypes.Width), pieceStats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            if (windows.Any(win => win.Contains(box))) continue;
            if (inside[parent[i]] >= OwnShare * wholeStats.At<int>(parent[i], (int)ConnectedComponentsTypes.Area)) continue;
            drop[i] = any = true;
        }
        if (!any) return 0;
        var clear = new byte[pieceOf.Length];
        int dropped = 0;
        for (int i = 0; i < pieceOf.Length; i++) if (drop[pieceOf[i]]) { clear[i] = 255; dropped++; }
        using var clearMask = new Mat(h, w, MatType.CV_8UC1);
        clearMask.SetArray(clear);
        strokes.SetTo(Scalar.Black, clearMask);
        return dropped;
    }

    /// <summary><see cref="DropUnownedStrokes"/>: 획 성분의 넓이 가운데 이만큼이 지울 블록의 상자 안에 있으면, 상자 밖으로 나간 부분도 그 블록의 글이다(창 안에 통째로 든 조각은 따지지 않는다).</summary>
    internal const double OwnShare = 0.5;

    /// <summary><see cref="AddMissedStrokes"/>: 어두운 것으로 보는 밝기(미만) · 성분의 최소 넓이 · 마스크 밖으로 닿았다고 보는 거리(px) · 더할 때 넓히는 폭(감지기 마스크의 불림과 같은 3px).</summary>
    internal const int MissedDark = 110, MissedMinArea = 12, MissedTouchPx = 2, MissedGrowPx = 3;
    /// <summary><see cref="AddMissedStrokes"/>: 성분의 이 비율 넘게 마스크가 덮으면 이미 든 글자 · 후보의 긴 변 ≤ 글자 크기의 이 배 · 굵기 ≤ 글 획의 이 배 · 변에 잘린 후보의 굵기 ≥ 글 획의 이 배(가는 테두리 선을 거른다).</summary>
    internal const double MissedCovered = 0.9, MissedSizeMax = 1.3, MissedThickMax = 1.7, MissedCutThickMin = 0.6;
    /// <summary><see cref="AddMissedStrokes"/>: 온전히 덮인 글자가 없는 상자에서, 어두운 성분이 전부 이 비율 넘게 덮였으면 덜 덮인 굵은 글자로 본다.</summary>
    internal const double MissedUnderCovered = 0.5;
    /// <summary><see cref="AddMissedStrokes"/>: 후보가 이 수와 글자 수 × 비율 가운데 큰 쪽을 넘으면 톤으로 본다. 실측: 놓친 글자가 있던 블록 8/21·2/15, 굵은 톤 위 블록 19/23.</summary>
    internal const int MissedMaxCount = 3;
    internal const double MissedMaxPerGlyph = 0.6;

    /// <summary>무게를 준 가운뎃값 — 값으로 줄 세워 무게의 누적이 절반을 넘는 첫 값.</summary>
    private static double WeightedMedian(IEnumerable<(double Value, double Weight)> items)
    {
        var sorted = items.OrderBy(v => v.Value).ToList();
        double total = sorted.Sum(v => v.Weight), run = 0;
        foreach (var v in sorted)
        {
            run += v.Weight;
            if (run >= total / 2) return v.Value;
        }
        return sorted.Count > 0 ? sorted[^1].Value : 0;
    }

    /// <summary><see cref="RegenStats"/>: 고리의 안·바깥 거리(구멍 밖 px) · 흰/검은 문턱(진단 수치) · 평평하다고 보는 밝기의 폭(고리의 가운뎃값 ± 이만큼 — 흰 종이면 225 이상, 먹이면 30 이하와 같다).</summary>
    internal const int RegenRimInner = 2, RegenRimOuter = 8, RegenWhite = 225, RegenBlack = 30, RegenFlatTol = 30;
    /// <summary>고리 가운데 한 가지 밝기(가운뎃값 ± <see cref="RegenFlatTol"/> — 흰 종이면 흰 비율, 먹이면 검은 비율과 같다)인 비율이 이 이상이면 평평한 바탕(LaMa 그대로). 0.98 = 실제 원고 세 쪽(2026-11-26, 사용자가 테스트한 05·13·26쪽 — LaMa로 간 20블록)의 수치로 정했다:
    /// 깨끗한 흰 풍선 여섯 블록은 전부 1.00, 사용자가 "배경에 맞게 채워 달라"고 보낸 자리는 속도선 위 0.71, 머리카락·빗금이 지나가는 풍선 둘 0.93·0.95, 톤 위 0.02~0.45.
    /// 처음 값 0.90은 빗금 든 풍선 둘을 평평한 바탕으로 읽었다(가는 선은 고리의 5~7%뿐이다). 1.00과 0.96 사이가 비어 있어 그 사이에 둔다 — 낮추면 그 두 자리가 LaMa에 남고,
    /// 더 올리면 압축 잡음 한두 픽셀로 흰 풍선이 SDXL로 간다(창 하나에 1분).</summary>
    internal const double RegenFlatRatio = 0.98;

    /// <summary><see cref="TrySolidFill"/>이 블록을 넓히는 배율 — 풍선 테두리가 들어오게.</summary>
    public const double SolidRoiRatio = 1.7;

    /// <summary>단색 바탕 판정(BT check_need_inpaint에서 출발). 두 가지로 잰다:
    /// **지우기(<see cref="TrySolidFill"/>, 기본)** — 글 획 둘레 띠(<see cref="BandPx"/>)의 글 밖 픽셀이 한 색이면 **칠할 자리(획 둘레 <see cref="FillPx"/> + 띠에 갇힌 작은 얼룩)**와 평균색.
    /// **풍선 판정(<see cref="IsInBubble"/>, <paramref name="wholeBalloon"/>)** — v1.18.0 그대로 글을 감싸는 테두리(없으면 창 테두리) 안 전체가 한 색이면 그 영역과 평균색.
    /// 어느 쪽이든 아니면 null. 돌려준 마스크는 호출자가 해제한다.</summary>
    private static (Mat Mask, Scalar Mean)? SolidPlan(Mat im, Mat msk, bool wholeBalloon = false)
    {
        var ballonMask = ExtractBallonMask(im, msk);
        if (ballonMask == null) return null;
        if (wholeBalloon)
        {
            // 풍선 판정(IsInBubble의 '단색 바탕')은 v1.18.0 그대로 영역 전체로 잰다 — 띠만 보면 그림 위의 글도 둘레 15px만 평평하면 '테두리를 못 읽은 풍선'이 되어,
            // 풍선 밖 효과음이 대사로 넘어가 번역되고 지워진다(검토 2026-11-27). 풍선 판정의 실측(448블록)도 이 잣대의 것이다
            using var all = new Mat();
            Cv2.BitwiseNot(msk, all);
            Cv2.BitwiseAnd(ballonMask, all, all);
            if (Cv2.CountNonZero(all) < 32) { ballonMask.Dispose(); return null; }
            Cv2.MeanStdDev(im, out var m0, out var s0, all);
            double max0 = Math.Max(s0.Val0, Math.Max(s0.Val1, s0.Val2));
            if (max0 >= (StdOf(s0.Val0, s0.Val1, s0.Val2) > 1 ? 7 : 10)) { ballonMask.Dispose(); return null; }
            return (ballonMask, new Scalar(m0.Val0, m0.Val1, m0.Val2));
        }

        // 풍선 바탕이 한 가지 색인지는 글 둘레 띠(획에서 BandPx 안, 풍선 안쪽에서 테두리 선 RimPx는 뺀 곳)의 글 아닌 픽셀로만 잰다.
        // 풍선 전체(1.7배 창 안의 닫힌 영역)로 재면 넓은 흰 부분이 글 옆을 지나는 머리카락·옅은 톤을 묻어 '단색'으로 통과했고, 그때 그 영역을 통째로 칠해
        // 네모난 흰 자리가 풍선 테두리·바깥 그림을 덮었다(사용자 보고 2026-11-26, 네 장). 칠하는 자리도 글 획 둘레(FillPx)만 — 풍선 전체가 아니다.
        using var bandKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * BandPx + 1, 2 * BandPx + 1));
        using var band = new Mat();
        Cv2.Dilate(msk, band, bandKernel);
        using var rimKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * RimPx + 1, 2 * RimPx + 1));
        using var inner = new Mat();
        Cv2.Erode(ballonMask, inner, rimKernel);
        using var nonText = new Mat();
        Cv2.BitwiseNot(msk, nonText);
        Cv2.BitwiseAnd(nonText, band, nonText);
        Cv2.BitwiseAnd(nonText, inner, nonText);
        if (Cv2.CountNonZero(nonText) < 32) { ballonMask.Dispose(); return null; }

        // 감지기가 놓친 작은 얼룩(후리가나·점)은 바탕에서 빼고 잰 뒤 글과 같이 칠한다 — 없으면 전과 똑같이 잰다
        using var marks = EnclosedMarks(im, nonText, band, inner);
        if (marks != null) Cv2.Subtract(nonText, marks, nonText);

        Cv2.MeanStdDev(im, out var mean, out var std, nonText);
        double stdMax = Math.Max(std.Val0, Math.Max(std.Val1, std.Val2));
        double stdSpread = StdOf(std.Val0, std.Val1, std.Val2);
        double thresh = stdSpread > 1 ? 7 : 10; // BT와 동일한 판정(문턱은 그대로 — 재는 자리만 띠로 좁혔다)
        if (stdMax >= thresh) { ballonMask.Dispose(); return null; }

        // 칠할 자리: 글 획(감지기가 7px쯤 불린 마스크)을 FillPx 더 넓힌 것 ∩ 풍선 안 — 안티에일리어싱 가장자리까지 덮고 풍선 밖·테두리 밖은 안 건드린다
        using var fillKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * FillPx + 1, 2 * FillPx + 1));
        var fill = new Mat();
        Cv2.Dilate(msk, fill, fillKernel);
        if (marks != null) Cv2.BitwiseOr(fill, marks, fill);
        Cv2.BitwiseAnd(fill, ballonMask, fill);
        ballonMask.Dispose();
        return (fill, new Scalar(mean.Val0, mean.Val1, mean.Val2));
    }

    /// <summary>글 둘레 띠 안에 **갇힌** 작은 얼룩(감지기가 놓친 후리가나·점)의 자리(<see cref="MarkGrowPx"/> 넓힌 것), 없으면 null.
    /// 풍선을 통째로 칠하던 v1.18.0은 이런 것을 가려 줬다. 획만 칠하게 바꾼 뒤로는 24px짜리 얼룩 하나가 띠의 편차를 넘겨 깨끗한 흰 풍선이 LaMa로 넘어갔고 얼룩도 남았다(검토 2026-11-26).
    /// 얼룩 = 띠의 글 아닌 픽셀 가운데 바탕(가운뎃값)에서 <see cref="MarkDeviation"/> 넘게 벗어난 것의 성분. 띠 가장자리(띠 밖·풍선 테두리 쪽)에 닿은 성분은 얼룩이 아니다 —
    /// 밖에서 들어온 선(머리카락)이다. 그런 것은 빼지도 칠하지도 않는다(전과 같다: 편차에 잡히면 LaMa로 간다). 갇힌 것의 합이 띠의 <see cref="MarkMaxRatio"/>를 넘으면 얼룩이 아니라 그림·놓친 글자 줄이다.</summary>
    private static Mat? EnclosedMarks(Mat im, Mat nonText, Mat band, Mat inner)
    {
        int total = Cv2.CountNonZero(nonText);
        using var gray = im.Channels() == 1 ? im.Clone() : im.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var hist = new Mat();
        Cv2.CalcHist(new[] { gray }, new[] { 0 }, nonText, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        int median = 0;
        for (double run = 0; median < 256; median++)
        {
            run += hist.At<float>(median);
            if (run >= total / 2.0) break;
        }
        using var off = new Mat();
        Cv2.Absdiff(gray, new Scalar(median), off);
        Cv2.Threshold(off, off, MarkDeviation, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(off, nonText, off);
        int offPx = Cv2.CountNonZero(off);
        if (offPx == 0 || offPx > MarkScanRatio * total) return null;   // 톤·그림 위 — 성분을 셀 것도 없다

        // 띠 가장자리 = 띠∩풍선 안쪽의 바깥(1px 안쪽까지). 여기에 닿은 성분은 밖에서 들어온 것이다
        using var edge = new Mat();
        Cv2.BitwiseAnd(band, inner, edge);
        Cv2.BitwiseNot(edge, edge);
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edge, edge, k3);

        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(off, labels, stats, cent, PixelConnectivity.Connectivity8);
        var marks = new Mat(im.Rows, im.Cols, MatType.CV_8UC1, Scalar.Black);
        int kept = 0;
        using var comp = new Mat(); using var hit = new Mat();
        for (int i = 1; i < n; i++)
        {
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            using var lab = new Mat(labels, box); using var e = new Mat(edge, box);
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            Cv2.BitwiseAnd(comp, e, hit);
            if (Cv2.CountNonZero(hit) > 0) continue;
            using var dst = new Mat(marks, box);
            Cv2.BitwiseOr(dst, comp, dst);
            kept += stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
        }
        if (kept == 0 || kept > MarkMaxRatio * total) { marks.Dispose(); return null; }
        using var grow = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * MarkGrowPx + 1, 2 * MarkGrowPx + 1));
        Cv2.Dilate(marks, marks, grow);
        return marks;
    }

    /// <summary><see cref="EnclosedMarks"/>: 바탕에서 이만큼 넘게 벗어나면 얼룩 픽셀 · 칠할 때 넓히는 폭(흐린 가장자리).</summary>
    internal const int MarkDeviation = 40, MarkGrowPx = 2;
    /// <summary><see cref="EnclosedMarks"/>: 갇힌 얼룩의 합이 띠(글 아닌 픽셀)의 이 비율을 넘으면 얼룩이 아니다(후리가나 한두 자 30~50px ÷ 띠 5천~1만 5천 px = 0.3~1%) · 벗어난 픽셀이 이 비율을 넘으면 세지도 않는다(톤).</summary>
    internal const double MarkMaxRatio = 0.015, MarkScanRatio = 0.10;

    /// <summary><see cref="SolidPlan"/>이 바탕색을 재는 띠의 폭(글 획에서 px) · 풍선 안쪽에서 빼는 테두리 선 폭(에지 3px 번짐 + 여유) · 칠하는 자리를 획에서 넓히는 폭(안티에일리어싱).</summary>
    internal const int BandPx = 15, RimPx = 4, FillPx = 5;

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
        if (Cv2.CountNonZero(mskNear) > 0 && SolidPlan(imNear, mskNear, wholeBalloon: true) is { } plain)
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

    /// <summary>사각형을 사방으로 px만큼 넓힌다(그림 안으로 자른다) — <see cref="IsInBubble"/>이 이 블록의 글만 남기는 자리의 최소 여백, <see cref="TextHalo"/>가 흰 테를 재는 창의 최소 여백.</summary>
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
