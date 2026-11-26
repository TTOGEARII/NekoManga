using NekoManga.Core.Decensor;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>글자 지운 자리 다시 그리기(TextRegen, 2026-11-26)의 계산 쪽 — 대상 마스크를 창으로 묶기, 컬러 쪽 가리기, 그린 결과의 '빈 조각' 판정.
/// 모델도 서버도 없다(그리는 길은 TextRegenRunTests가 가짜 ComfyUI로 본다).</summary>
public class TextRegenPlanTests
{
    private static Mat MaskOf(int width, int height, params Rect[] rects)
    {
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in rects) mask.Rectangle(r, Scalar.White, -1);
        return mask;
    }

    /// <summary>가로 1px 검은 줄을 pitch px마다 그은 BGR 그림 — 속도선·빗금 바탕의 대역(pitch 4 = 흰 75%).</summary>
    private static Mat Stripes(int width, int height, int pitch = 4)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < height; y += pitch) img.Line(0, y, width - 1, y, Scalar.Black, 1);
        return img;
    }

    // ── 창으로 묶기 ──

    /// <summary>창들을 구해 (창, 그 창이 맡은 구멍의 픽셀 수, 구멍 그림의 크기)로 — 구멍 Mat은 여기서 해제한다.</summary>
    private static List<(RegenWindow W, int HolePx, Size HoleSize)> Plan(Mat mask, int cropSize = 1024)
    {
        var ws = TextRegenPlan.WindowsFor(mask, mask.Size(), cropSize);
        try { return ws.Select(w => (w.Window, Cv2.CountNonZero(w.Hole), w.Hole.Size())).ToList(); }
        finally { foreach (var w in ws) w.Dispose(); }
    }

    [Fact]
    public void WindowsFor_EmptyMask_NoWindows()
    {
        using var mask = MaskOf(2064, 2880);
        Assert.Empty(Plan(mask));
    }

    [Fact]
    public void WindowsFor_TwoNearBlocks_ShareOneWindowAtFullResolution()
    {
        var a = new Rect(300, 300, 120, 200); var b = new Rect(500, 350, 120, 200);
        using var mask = MaskOf(2064, 2880, a, b);

        var (w, holePx, holeSize) = Assert.Single(Plan(mask));

        Assert.Equal(new Size(1024, 1024), w.Window.Size);
        Assert.Equal((1024, 1024), (w.SendW, w.SendH));   // 줄이지 않고 원본 해상도로 간다
        Assert.True(w.Window.Contains(a.TopLeft) && w.Window.Contains(new Point(a.Right - 1, a.Bottom - 1)));
        Assert.True(w.Window.Contains(b.TopLeft) && w.Window.Contains(new Point(b.Right - 1, b.Bottom - 1)));
        // 이 창이 맡은 구멍 = 두 블록 전부(창 크기의 그림, 불리기 전 마스크 그대로)
        Assert.Equal(w.Window.Size, holeSize);
        Assert.Equal(a.Width * a.Height + b.Width * b.Height, holePx);
    }

    [Fact]
    public void WindowsFor_AnotherGroupsHoleInsideTheWindow_IsLeftToItsOwnWindow()
    {
        // 창은 1024라 다른 묶음의 구멍이 걸쳐 들어온다 — 그 구멍(의 일부)까지 그리면 구멍 한가운데에 이음매가 생긴다. 창마다 제 묶음의 구멍만 맡는다
        var a = new Rect(300, 300, 120, 200); var c = new Rect(300, 900, 120, 200);   // 합친 상자의 긴 변 830 > 640 → 다른 묶음, 창은 서로 겹친다
        using var mask = MaskOf(2064, 2880, a, c);

        var ws = Plan(mask);

        Assert.Equal(2, ws.Count);
        Assert.True(ws[0].W.Window.IntersectsWith(c) && ws[1].W.Window.IntersectsWith(a));   // 전제: 남의 구멍이 창에 걸친다
        Assert.All(ws, w => Assert.Equal(120 * 200, w.HolePx));
    }

    [Fact]
    public void WindowsFor_TwoFarBlocks_GetTheirOwnWindows_TopFirst()
    {
        var low = new Rect(300, 2000, 120, 200); var high = new Rect(1500, 300, 120, 200);
        using var mask = MaskOf(2064, 2880, low, high);

        var ws = Plan(mask);

        Assert.Equal(2, ws.Count);
        Assert.True(ws[0].W.Window.Contains(new Point(1560, 400)));   // 위의 것이 먼저
        Assert.True(ws[1].W.Window.Contains(new Point(360, 2100)));
    }

    [Fact]
    public void WindowsFor_BlockLongerThanTheLimit_GetsItsOwnScaledWindow()
    {
        // 긴 변이 cropSize/1.6(640)을 넘는 블록은 혼자 창을 갖고, 창이 커져 줄여 보낸다(재생성과 같은 규칙). 옆의 작은 블록은 같이 묶이지 않는다
        var tall = new Rect(200, 200, 150, 900); var small = new Rect(400, 300, 60, 60);
        using var mask = MaskOf(2064, 2880, tall, small);

        var ws = Plan(mask).Select(w => w.W).ToList();

        Assert.Equal(2, ws.Count);
        var big = ws.Single(w => w.Window.Height > 1024);
        Assert.Equal((int)Math.Ceiling((900 + 2 * TextRegenPlan.MergePx) * 1.6), big.Window.Height);
        Assert.True(big.SendH <= 1024 && big.SendH % 8 == 0 && big.Scale < 1.0);
        Assert.Contains(ws, w => w.Window.Size == new Size(1024, 1024) && w.Window.Contains(small.TopLeft));
    }

    [Fact]
    public void WindowsFor_PageSmallerThanTheCrop_WindowIsTheWholePage_SendIsAMultipleOf8()
    {
        using var mask = MaskOf(610, 805, new Rect(200, 300, 100, 80));

        var (w, _, holeSize) = Assert.Single(Plan(mask));

        Assert.Equal(new Rect(0, 0, 610, 805), w.Window);
        Assert.Equal((608, 800), (w.SendW, w.SendH));
        Assert.Equal(new Size(610, 805), holeSize);   // 구멍은 창 크기 그대로 — 보내는 크기로 줄이는 것은 그릴 때
    }

    [Fact]
    public void WindowsFor_GlyphsOfOneBlock_AreOneBox()
    {
        // 한 블록의 글자들은 서로 떨어진 획이다 — 불려서 한 덩어리로 본다(글자마다 창을 만들지 않는다)
        using var mask = MaskOf(2064, 2880, new Rect(300, 300, 30, 30), new Rect(340, 300, 30, 30), new Rect(300, 340, 30, 30));

        Assert.Single(Plan(mask));
    }

    // ── 컬러 쪽 ──

    [Fact]
    public void IsColorPage_GrayPage_False()
    {
        using var gray = new Mat(200, 300, MatType.CV_8UC3, new Scalar(128, 128, 128));
        using var stripes = Stripes(300, 200);
        Assert.False(TextRegenPlan.IsColorPage(gray));
        Assert.False(TextRegenPlan.IsColorPage(stripes));
    }

    [Fact]
    public void IsColorPage_TintedPage_True()
    {
        using var tinted = new Mat(200, 300, MatType.CV_8UC3, new Scalar(200, 160, 120));
        Assert.True(TextRegenPlan.IsColorPage(tinted));
    }

    [Fact]
    public void IsColorPage_OneChannel_False()
    {
        using var one = new Mat(200, 300, MatType.CV_8UC1, new Scalar(128));
        Assert.False(TextRegenPlan.IsColorPage(one));
    }

    // ── 빈 조각(그린 자리가 둘레보다 눈에 띄게 흰가) ──

    // 둘레는 **그리기 전 그림에서, 붙이는 범위(구멍 + skip px) 밖**을 잰다 — 구멍 바로 밖은 SDXL이 같이 다시 그리는 자리(노이즈 마스크를 넓힌다)라 흰 칠이 거기까지 번지면 견줄 것이 없다.
    private const int Skip = 10;   // TextRegenInpaintGrow 8 + 2

    [Fact]
    public void PieceStats_HoleLeftBlankInStripedArt_IsABlankPiece()
    {
        var hole = new Rect(100, 100, 80, 60);
        using var page = Stripes(400, 300);
        using var result = page.Clone();
        result.Rectangle(hole, Scalar.White, -1);   // 줄무늬 바탕인데 구멍만 흰 칠로 메워졌다
        using var mask = RectMask(400, 300, hole);

        var stats = TextRegenCheck.PieceStats(result, page, mask, Skip);

        var (inHole, rim) = Assert.Single(stats);
        Assert.Equal(1.0, inHole, 2);
        Assert.InRange(rim, 0.65, 0.85);
        Assert.Equal(1, TextRegenCheck.BlankCount(stats));
    }

    [Fact]
    public void PieceStats_WhiteSpillsOverTheRegeneratedRing_IsStillABlankPiece()
    {
        // SDXL은 구멍을 넓힌 범위까지 다시 그린다 — 흰 칠이 그 고리까지 번져도(결과 전체가 흰 그림이어도) 그리기 전 그림의 둘레와 견주므로 빈 조각으로 잡힌다
        using var page = Stripes(400, 300);
        using var result = new Mat(300, 400, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(400, 300, new Rect(100, 100, 80, 60));

        Assert.Equal(1, TextRegenCheck.BlankCount(TextRegenCheck.PieceStats(result, page, mask, Skip)));
    }

    [Fact]
    public void PieceStats_HoleDrawnLikeItsSurroundings_IsNotBlank()
    {
        using var page = Stripes(400, 300);
        using var mask = RectMask(400, 300, new Rect(100, 100, 80, 60));

        var stats = TextRegenCheck.PieceStats(page, page, mask, Skip);

        Assert.Single(stats);
        Assert.Equal(0, TextRegenCheck.BlankCount(stats));
    }

    [Fact]
    public void PieceStats_WhiteHoleOnWhitePaper_IsNotBlank()
    {
        // 둘레가 흰 종이면 흰 결과가 맞다 — 재생성의 "흰 비율 0.6 이상 = 실패"를 그대로 쓰면 이런 자리를 헛되이 다시 그린다
        using var page = new Mat(300, 400, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(400, 300, new Rect(100, 100, 80, 60));

        Assert.Equal(0, TextRegenCheck.BlankCount(TextRegenCheck.PieceStats(page, page, mask, Skip)));
    }

    [Fact]
    public void PieceStats_TinyPieces_AreIgnored()
    {
        using var page = Stripes(400, 300);
        using var mask = RectMask(400, 300, new Rect(100, 100, 10, 10));   // 100px < RegenWhiteCheck.MinPiecePx(300)

        Assert.Empty(TextRegenCheck.PieceStats(page, page, mask, Skip));
    }

    [Fact]
    public void PieceStats_TwoPieces_MeasuredSeparately()
    {
        var blank = new Rect(60, 60, 80, 60); var drawn = new Rect(240, 160, 80, 60);
        using var page = Stripes(400, 300);
        using var result = page.Clone();
        result.Rectangle(blank, Scalar.White, -1);
        using var mask = MaskOf(400, 300, blank, drawn);

        var stats = TextRegenCheck.PieceStats(result, page, mask, Skip);

        Assert.Equal(2, stats.Count);
        Assert.Equal(1, TextRegenCheck.BlankCount(stats));
    }

    [Fact]
    public void PieceStats_PieceAtTheImageEdge_DoesNotThrow()
    {
        using var page = Stripes(400, 300);
        using var mask = RectMask(400, 300, new Rect(0, 0, 60, 40));

        Assert.Single(TextRegenCheck.PieceStats(page, page, mask, Skip));
    }

    [Fact]
    public void IsBetter_FewerBlankPiecesWins_ThenTheSmallerExcess()
    {
        (double, double)[] blank = [(1.0, 0.75)], drawn = [(0.76, 0.75)], slightlyPale = [(0.85, 0.75)];
        Assert.True(TextRegenCheck.IsBetter(drawn, blank));
        Assert.False(TextRegenCheck.IsBetter(blank, drawn));
        Assert.True(TextRegenCheck.IsBetter(drawn, slightlyPale));    // 둘 다 빈 조각은 아니지만 둘레와 더 가까운 쪽
        Assert.False(TextRegenCheck.IsBetter(drawn, drawn));           // 같으면 먼저 그린 것을 둔다
        // 더 검게 그린 것이 더 좋은 것은 아니다 — 둘레보다 흰 만큼만 센다(먹칠이 많은 결과를 고르지 않는다)
        (double, double)[] inky = [(0.30, 0.75)];
        Assert.False(TextRegenCheck.IsBetter(inky, drawn));
    }

    [Fact]
    public void BlankCount_HoleOnlyPartlyWhite_IsNotBlank_EvenWhenWhiterThanTheFarRing()
    {
        // 흰 풍선과 먹칠 사이에 걸친 구멍: 제대로 그리면 반쯤 희다. 둘레(구멍에서 12~20px 떨어진 고리)가 먹 쪽에 치우쳐 0.30이어도 흰 칠로 메워진 것이 아니다 —
        // 빈 조각은 구멍이 거의 다 흰 것이다. 이것을 빈 조각으로 세면 멀쩡한 그림을 한 번 더 그린다(재시도 기본 1 — 창 하나에 30~60초, 검토 2026-11-26)
        (double, double)[] straddling = [(0.55, 0.30)], blank = [(0.97, 0.30)], paleOnTone = [(0.86, 0.60)];
        Assert.Equal(0, TextRegenCheck.BlankCount(straddling));
        Assert.Equal(1, TextRegenCheck.BlankCount(blank));
        Assert.Equal(1, TextRegenCheck.BlankCount(paleOnTone));
        Assert.False(TextRegenCheck.IsBetter(blank, straddling));
    }

    [Fact]
    public void IsBetter_APieceThatIsNotBlank_DoesNotScoreByGettingDarker()
    {
        // 조각 A는 두 번 다 빈 조각, 조각 B는 걸친 구멍(반쯤 흰 것이 맞다): 두 번째 시도에서 B가 더 검게 그려졌다고 그쪽을 고르지 않는다 —
        // 초과분은 '거의 다 흰' 조각에서만 센다(빈 조각의 기준과 같은 하한). 안 그러면 먹칠이 많은 결과로 갈아탄다(검토 2026-11-27)
        (double, double)[] first = [(0.98, 0.30), (0.55, 0.30)], darkerB = [(0.98, 0.30), (0.30, 0.30)];
        Assert.False(TextRegenCheck.IsBetter(darkerB, first));
        // 빈 조각 A가 덜 희어진 것은 나아진 것이다
        (double, double)[] lessBlankA = [(0.90, 0.30), (0.55, 0.30)];
        Assert.True(TextRegenCheck.IsBetter(lessBlankA, first));
    }
}
