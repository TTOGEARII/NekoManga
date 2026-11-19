using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>단색 풍선 글자 지우기(BubbleSolver) — 합성 풍선에서 LaMa 없이 칠해도 되는 경우와 안 되는 경우를 가른다.</summary>
public class BubbleSolverTests
{
    [Theory]
    // 가운데를 지키며 크기만 배율대로 바꾼다 (넓히기·좁히기)
    [InlineData(100, 100, 40, 20, 1.5, 1000, 90, 95, 60, 30)]
    [InlineData(100, 100, 40, 40, 0.5, 1000, 110, 110, 20, 20)]
    // 이미지 가장자리에서는 이미지 안으로 잘린다
    [InlineData(0, 0, 40, 40, 2.0, 100, 0, 0, 60, 60)]
    [InlineData(80, 80, 20, 20, 2.0, 100, 70, 70, 30, 30)]
    public void Enlarge_RatioNearOrAwayFromEdge_ScalesAroundCenterWithinImage(int x, int y, int w, int h, double ratio, int imageSize,
        int ex, int ey, int ew, int eh)
    {
        var r = BubbleSolver.Enlarge(new Rect(x, y, w, h), ratio, imageSize, imageSize);

        Assert.Equal(new Rect(ex, ey, ew, eh), r);
        Assert.True(new Rect(0, 0, imageSize, imageSize).Contains(r));
    }

    [Theory]
    [InlineData(255)]   // 흰 풍선
    [InlineData(200)]   // 옅은 회색 풍선 — 흰색이 아니라 그 풍선의 바탕색으로 칠해야 티가 안 난다
    public void TrySolidFill_PlainBubble_PaintsTextOverWithBubbleColor(int fill)
    {
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100, fill: fill);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);
        using var before = page.Clone();

        Assert.True(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        // 글자가 있던 자리는 풍선 바탕색 한 가지로 덮였다
        using var glyphArea = new Mat(page, glyphs);
        using var gray = glyphArea.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out double min, out double max);
        Assert.InRange(min, fill - 2, fill + 2);
        Assert.InRange(max, fill - 2, fill + 2);

        // 바뀐 픽셀은 모두 풍선 흰 안쪽에 있다 — 테두리와 풍선 밖 바탕은 그대로다
        using var interior = new Mat(page.Rows, page.Cols, MatType.CV_8UC1, Scalar.Black);
        Cv2.Ellipse(interior, new Point(300, 250), new Size(150, 100), 0, 0, 360, Scalar.White, -1);
        using var changed = ChangedMask(before, page, 0);
        using var outside = new Mat();
        Cv2.BitwiseNot(interior, outside);
        Cv2.BitwiseAnd(changed, outside, outside);
        Assert.True(Cv2.CountNonZero(changed) > 0);
        Assert.Equal(0, Cv2.CountNonZero(outside));
    }

    [Theory]
    [InlineData("screentone")]   // 망점 톤을 깐 풍선
    [InlineData("hatching")]     // 풍선 없이 빗금 그림 위에 놓인 글
    public void TrySolidFill_TexturedBackground_LeavesImageForLama(string texture)
    {
        // 풍선 안(글자 밖)이 한 가지 색이 아니면 칠하면 티가 난다 — 그림을 건드리지 않고 false로 LaMa에 넘긴다
        using var page = SolidPage(600, 500, texture == "screentone" ? 120 : 200);
        using var mask = EmptyMask(page);
        if (texture == "screentone")
        {
            DrawBubble(page, 300, 250, 150, 100);
            for (int y = 150; y < 350; y += 6)
                for (int x = 150; x < 450; x += 6)
                    Cv2.Circle(page, new Point(x, y), 1, new Scalar(90, 90, 90), -1);
        }
        else
        {
            for (int k = -500; k < 600; k += 9)
                Cv2.Line(page, new Point(k, 0), new Point(k + 500, 500), Scalar.Black, 2);
        }
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);
        using var before = page.Clone();

        Assert.False(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var changed = ChangedMask(before, page, 0);
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    [Fact]
    public void TrySolidFill_NoTextInMask_ReportsDoneAndChangesNothing()
    {
        // 블록 둘레에 지울 글자가 없으면 할 일이 없다 — LaMa로 넘기지 않고(true) 그림도 건드리지 않는다
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        DrawGlyphs(page, null, 250, 210, 4, 3);
        using var before = page.Clone();

        Assert.True(BubbleSolver.TrySolidFill(page, mask, new Rect(246, 206, 76, 58)));

        using var changed = ChangedMask(before, page, 0);
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    // ── 말풍선 안의 글인가 (번역을 풍선 안으로 좁히기, 2026-11-18) ──

    [Theory]
    [InlineData("ellipse")]   // 타원 풍선
    [InlineData("box")]       // 네모 해설 상자도 닫힌 테두리다
    [InlineData("gray")]      // 옅은 회색 바탕의 풍선
    public void IsInBubble_ClosedOutlineAroundText_True(string kind)
    {
        using var page = SolidPage(600, 500, 200);
        using var mask = EmptyMask(page);
        if (kind == "box") DrawBox(page, new Rect(210, 185, 180, 130));
        else DrawBubble(page, 300, 250, 90, 60, fill: kind == "gray" ? 215 : 255);
        var glyphs = DrawGlyphs(page, mask, 266, 225, 4, 3);   // 글 덩어리(68×50)의 가운데 = 풍선 가운데
        DilateLikeDetector(mask);
        using var before = page.Clone();

        Assert.True(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var changed = ChangedMask(before, page, 0);   // 판정만 한다 — 그림은 그대로
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    [Fact]
    public void IsInBubble_ShortWordInBigBubble_True()
    {
        // "え?" 같은 짧은 글은 풍선이 글보다 훨씬 크다 — 블록 배율로만 창을 잡으면 풍선 테두리가 창 밖으로 나가 풍선 밖으로 오판한다.
        // 창은 쪽 크기에 비례한 최소 크기를 가진다(BubbleSolver.IsInBubble)
        using var page = SolidPage(600, 500);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 75, 50);
        var glyphs = DrawGlyphs(page, mask, 293, 243, 1, 1);
        DilateLikeDetector(mask);

        Assert.True(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4))));
    }

    [Theory]
    [InlineData("hatching")]   // 빗금 그림 위의 글
    [InlineData("panel")]      // 글을 감싸는 닫힌 선이 칸 테두리뿐이고 칸 안은 그림
    public void IsInBubble_TextDrawnOverArt_False(string kind)
    {
        using var page = SolidPage(600, 500, 255);
        using var mask = EmptyMask(page);
        if (kind == "hatching")
            for (int k = -500; k < 600; k += 9) Cv2.Line(page, new Point(k, 0), new Point(k + 500, 500), Scalar.Black, 2);
        if (kind == "panel")
        {
            DrawBox(page, new Rect(40, 40, 520, 420));
            for (int k = -500; k < 600; k += 9) Cv2.Line(page, new Point(k, 40), new Point(k + 420, 460), Scalar.Black, 2);   // 칸 안은 그림
        }
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);

        Assert.False(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why));
        Assert.NotEqual(BubbleSolver.BubbleVerdict.Bubble, why);
    }

    [Fact]
    public void IsInBubble_PlainBackgroundWithoutOutline_TrueAsPlain()
    {
        // 테두리 없는 흰 바탕의 글 — 테두리를 못 읽은 풍선일 수도, 풍선 없는 대사일 수도 있다. 실제 원고에서 테두리 규칙만으로는 대사가 빠져(2026-11-18)
        // 바탕이 단색이면(지금 글자 지우기가 LaMa 없이 칠하는 조건) 번역한다 — 그림 위에 그려진 글만 거른다
        using var page = SolidPage(600, 500, 255);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);

        Assert.True(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why));
        Assert.Equal(BubbleSolver.BubbleVerdict.PlainBackground, why);
    }

    [Fact]
    public void IsInBubble_NoTextInMask_False()
    {
        using var page = SolidPage(600, 500);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 90, 60);

        Assert.False(BubbleSolver.IsInBubble(page, mask, new Rect(246, 206, 76, 58)));
    }
}
