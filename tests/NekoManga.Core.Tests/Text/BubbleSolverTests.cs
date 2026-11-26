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
    public void TrySolidFill_PaintsOnlyTheTextStrokes_NotTheWholeBubble()
    {
        // 풍선 안쪽 전체를 칠하면 글과 떨어진 가는 선·점(머리카락 끝, 옅은 톤)까지 지워진다(사용자 보고 2026-11-26: 네모난 흰 자리가 테두리·머리카락을 덮음) — 글 획 둘레만 칠한다
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);   // 250..318 × 210..260
        DilateLikeDetector(mask);
        page.Set(250, 340, new Vec3b(200, 200, 200));   // 풍선 안, 글에서 22px 떨어진 옅은 점 — 1.7배 창(x 220~349) 안이지만 획 둘레 밖. 편차에는 안 잡힐 만큼 작다
        using var before = page.Clone();

        Assert.True(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        Assert.Equal(new Vec3b(200, 200, 200), page.Get<Vec3b>(250, 340));   // 글과 떨어진 곳은 그대로(전에는 창 안의 닫힌 영역 전체를 칠해 255가 됐다)
        using var changed = ChangedMask(before, page, 0);
        using var far = new Mat(changed, new Rect(glyphs.Right + 20, 150, 100, 200));   // 글 오른쪽 20px 밖부터는 아무것도 안 바뀐다
        Assert.Equal(0, Cv2.CountNonZero(far));
    }

    [Fact]
    public void TrySolidFill_FaintStrandsNextToTheText_LeavesImageForLama()
    {
        // 큰 흰 풍선에서 글 바로 옆으로 옅은 선(머리카락)이 지나가면, 풍선 전체로 잰 편차는 작아 '단색'으로 통과하고 선이 덩어리째 지워졌다(사용자 보고 2026-11-26)
        // — 글 둘레 띠로 재면 선이 잡혀 LaMa에 넘긴다
        using var page = SolidPage(700, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 350, 250, 300, 200);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 10, 4, glyph: 16);   // 196×76
        DilateLikeDetector(mask);
        foreach (int y in new[] { glyphs.Top - 10, glyphs.Bottom + 10 })   // 글 위아래 10px — 마스크(7px 불림) 바로 밖, 190 밝기 1px(에지로 안 잡힌다)
            Cv2.Line(page, new Point(glyphs.Left - 20, y), new Point(glyphs.Right + 20, y), new Scalar(190, 190, 190), 1);
        using var before = page.Clone();

        Assert.False(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var changed = ChangedMask(before, page, 0);
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    [Fact]
    public void TrySolidFill_SmallMarkTheDetectorMissed_IsPaintedWithTheText()
    {
        // 감지기가 놓친 후리가나·점: 풍선을 통째로 칠하던 v1.18.0은 가려 줬고, 획만 칠하게 바꾼 뒤로는 그 얼룩 하나 때문에 '단색이 아니다'가 되어 LaMa로 넘어가고 얼룩도 남았다(검토 2026-11-26).
        // 글 둘레 띠 안에 갇힌 작은 얼룩은 글의 일부로 보고 같이 칠한다
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);   // 250..318 × 210..260
        DilateLikeDetector(mask);
        var mark = new Rect(glyphs.Right + 8, 220, 4, 6);      // 글 오른쪽 8px(마스크에서 5px)의 24px짜리 얼룩 — 마스크에 없다
        page.Rectangle(mark, Scalar.Black, -1);

        Assert.True(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var where = new Mat(page, new Rect(mark.X - 1, mark.Y - 1, mark.Width + 2, mark.Height + 2));
        using var gray = where.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out double darkest, out _);
        Assert.True(darkest >= 250, $"얼룩이 남았다: 가장 어두운 픽셀 {darkest}");
        Assert.Equal(new Vec3b(255, 255, 255), page.Get<Vec3b>(glyphs.Y + 7, glyphs.X + 7));   // 글 획도 칠했다
    }

    [Fact]
    public void TrySolidFill_BigUnmaskedInkNextToTheText_LeavesImageForLama()
    {
        // 얼룩이 아니라 덩어리(글 옆의 그림, 통째로 놓친 글자 줄): 띠의 1.5%를 넘는다 — 단색 바탕으로 보지 않는다(칠해서 지우지 않는다)
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);
        page.Rectangle(new Rect(glyphs.Right + 6, 212, 8, 40), Scalar.Black, -1);   // 320px
        using var before = page.Clone();

        Assert.False(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var changed = ChangedMask(before, page, 0);
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    [Fact]
    public void TrySolidFill_ShortStrandPokingIntoTheBand_IsNotPainted()
    {
        // 띠 바깥에서 들어와 띠 안에서 끝나는 짧은 선(머리카락 끝): 픽셀 수는 얼룩만큼 적어도 띠 가장자리에 닿아 있다 — 갇힌 얼룩이 아니라 그림의 끝이다.
        // 얼룩으로 알고 칠하면 선이 띠 가장자리에서 끊긴다. 글 획은 전처럼 칠한다(이만한 선은 편차에 안 잡혀 단색이다)
        using var page = SolidPage(700, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 350, 250, 300, 200);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 10, 4, glyph: 16);
        DilateLikeDetector(mask);
        Cv2.Line(page, new Point(glyphs.Right + 9, 240), new Point(glyphs.Right + 40, 240), Scalar.Black, 1);   // 마스크에서 6px 떨어진 데서 띠(15px) 밖 22px까지
        using var before = page.Clone();

        Assert.True(BubbleSolver.TrySolidFill(page, mask, RectOf(BlockOf(glyphs, 4))));

        using var changed = ChangedMask(before, page, 0);
        using var strand = new Mat(changed, new Rect(glyphs.Right + 9, 238, 32, 5));
        Assert.Equal(0, Cv2.CountNonZero(strand));
        Assert.Equal(new Vec3b(255, 255, 255), page.Get<Vec3b>(glyphs.Y + 8, glyphs.X + 8));
    }

    // ── 흰 테(フチ) 더하기 (2026-11-26 사용자 보고: 그림·톤 위 글의 흰 테가 마스크 밖에 남아 허연 글자 모양 얼룩) ──

    /// <summary>망점 톤 위에 흰 테(8px)를 두른 검은 글 — 실제 원고의 그림 위 대사·효과음 꼴.</summary>
    private static (Mat Page, Mat Mask, Rect Glyphs) OutlinedTextOnTone()
    {
        var page = SolidPage(500, 400, 255);
        for (int y = 0; y < 400; y += 5)
            for (int x = 0; x < 500; x += 5)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);   // 흰 테
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);   // 7px 불림 — 테의 안쪽 3px만 덮는다
        return (page, mask, glyphs);
    }

    [Fact]
    public void AddTextHalo_OutlinedTextOnTone_AddsTheWhiteRimToTheMask()
    {
        var (page, mask, glyphs) = OutlinedTextOnTone();
        using (page) using (mask)
        {
            using var into = mask.Clone();
            AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

            int cx = glyphs.X + glyphs.Width / 2, cy = glyphs.Y + glyphs.Height / 2;
            // 테(글 가장자리에서 8px)의 흰 픽셀이 마스크에 들어갔다 — 감지기 마스크(3px 불림)는 못 덮던 자리. 남으면 LaMa가 톤 대신 흰 테를 남긴다
            foreach (var (x, y) in new[] { (glyphs.X - 6, cy), (glyphs.Right + 5, cy), (cx, glyphs.Y - 6), (cx, glyphs.Bottom + 5) })
            {
                Assert.Equal(0, mask.At<byte>(y, x));
                Assert.Equal(255, into.At<byte>(y, x));
            }
            // 테 밖의 톤(글에서 20px)은 더하지 않았다
            Assert.Equal(0, into.At<byte>(cy, glyphs.X - 20));
            Assert.True(Cv2.CountNonZero(into) > Cv2.CountNonZero(mask));
        }
    }

    /// <summary>촘촘한 망점 톤(4px 간격 — 흰 픽셀 69%). 5px 간격의 옅은 톤(80%)은 흰 면이 조금만 끼어도 '획 바로 밖이 바깥보다 뚜렷이 희다'(0.15)에 못 미친다.</summary>
    private static Mat DenseTone(int width, int height)
    {
        var page = SolidPage(width, height, 255);
        for (int y = 0; y < height; y += 4)
            for (int x = 0; x < width; x += 4)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        return page;
    }

    /// <summary>망점 톤 위의 두 줄(세로 글 두 기둥, 사이 26px) — 흰 판이 두 줄과 그 사이를 통째로 덮고 있다(글 뒤에 깐 흰 테가 서로 이어진 꼴).</summary>
    private static (Mat Page, Mat Mask, Rect Left, Rect Right) TwoOutlinedColumnsOnTone()
    {
        var page = DenseTone(500, 400);
        var mask = EmptyMask(page);
        var left = new Rect(200, 120, 14, 5 * 14 + 4 * 4);
        var right = new Rect(left.Right + 26, 120, 14, left.Height);
        Cv2.Rectangle(page, new Rect(left.X - 6, left.Y - 6, right.Right - left.X + 12, left.Height + 12), Scalar.White, -1);   // 흰 판
        DrawGlyphs(page, mask, left.X, left.Y, 1, 5);
        DrawGlyphs(page, mask, right.X, right.Y, 1, 5);
        DilateLikeDetector(mask);
        return (page, mask, left, right);
    }

    [Fact]
    public void AddTextHalo_WhiteGapBetweenTwoOutlinedColumns_IsAddedToo()
    {
        // 줄과 줄 사이의 흰 판: 양쪽 마스크에서 8px씩 더하고도 가운데 4px가 남는다 — 남으면 글을 지운 자리에 줄 사이 모양의 흰 띠가 그대로 있다(실제 13쪽의 "したい / よお…" 사이)
        var (page, mask, left, right) = TwoOutlinedColumnsOnTone();
        using (page) using (mask)
        {
            using var into = mask.Clone();
            var block = new Rect(left.X - 4, left.Y - 4, right.Right - left.X + 8, left.Height + 8);

            AddTextHalo(page, mask, block, into);

            int mid = (left.Right + right.X) / 2, cy = left.Y + left.Height / 2;
            Assert.Equal(255, into.At<byte>(cy, mid));                 // 두 줄의 한가운데(획에서 13px)
            Assert.Equal(255, into.At<byte>(left.Y - 4, mid));         // 흰 판의 위쪽 끝 가까이
            Assert.Equal(0, into.At<byte>(cy, left.X - 20));           // 판 밖의 톤은 아니다
        }
    }

    [Fact]
    public void AddTextHalo_WhiteAreaReachingFarBeyondTheText_IsNotTakenAsAGap()
    {
        // 글 옆에서 시작해 멀리까지 이어진 흰 면(살의 하이라이트·흰 옷): 글 뒤에 깐 판이 아니라 그림이다 — 테(8px)까지만 더하고 그 너머는 두어야 한다
        using var page = DenseTone(500, 400);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16 + 150, glyphs.Height + 16), Scalar.White, -1);   // 흰 테(8px) + 오른쪽으로 150px 더 이어진 흰 면
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        {
            using var into = mask.Clone();

            AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

            int cy = glyphs.Y + glyphs.Height / 2;
            Assert.Equal(255, into.At<byte>(cy, glyphs.Right + 6));    // 테
            Assert.Equal(0, into.At<byte>(cy, glyphs.Right + 12));     // 획에서 12px — 흰 면의 일부지만 글에 갇힌 틈이 아니다(창 밖으로 이어진다)
        }
    }

    [Fact]
    public void AddTextHalo_WhiteTailRunningAwayFromTheText_IsNotTakenAsAGap()
    {
        // 흰 테에서 뻗어 나간 가는 흰 띠(꼬리 — 풍선의 꼬리, 빛줄기): 창 안에서 끝나지만 획에서 40px 넘게 멀어진다. 글에 갇힌 틈이 아니다
        using var page = DenseTone(700, 400);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(150, 150, 20 * 14 + 19 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        int cy = glyphs.Y + glyphs.Height / 2;
        Cv2.Rectangle(page, new Rect(glyphs.Right + 8, cy - 5, 45, 10), Scalar.White, -1);   // 테의 오른쪽 끝에서 45px 더(획에서 53px까지) — 1.3배 창(여백 55px) 안에서 끝난다
        DrawGlyphs(page, mask, glyphs.X, glyphs.Y, 20, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Equal(255, into.At<byte>(cy, glyphs.Right + 6));    // 테
        Assert.Equal(0, into.At<byte>(cy, glyphs.Right + 14));     // 꼬리
        Assert.Equal(0, into.At<byte>(cy, glyphs.Right + 40));
    }

    [Fact]
    public void AddTextHalo_WhiteSpotNearTheTextButApartFromItsRim_IsLeftAlone()
    {
        // 글 가까이(획에서 20px쯤)의 작은 흰 면 — 톤을 사이에 두고 테와 떨어져 있다. 글 뒤에 깐 판이 아니라 그림의 하이라이트다
        using var page = DenseTone(500, 400);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        var spot = new Rect(glyphs.Right + 16, glyphs.Y + 10, 12, 12);   // 테의 끝(획에서 8px)과 8px 떨어진 흰 점 — 블록의 1.3배 창 안
        Cv2.Rectangle(page, spot, Scalar.White, -1);
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        AddTextHalo(page, mask, new Rect(glyphs.X - 4, glyphs.Y - 4, glyphs.Width + 60, glyphs.Height + 8), into);

        Assert.Equal(255, into.At<byte>(glyphs.Y + 16, glyphs.Right + 6));    // 테는 더했다
        using var where = new Mat(into, spot);
        Assert.Equal(0, Cv2.CountNonZero(where));
    }

    // ── 꽉 찬 말풍선은 흰 테가 아니다 (검토 2026-11-27) ──
    // 글이 풍선을 거의 채우면 "획 바로 밖은 희고 바깥 띠(8~14px)는 덜 희다"가 성립한다 — 바깥 띠가 풍선 테두리와 그 밖에 걸친다. 그것을 흰 테로 읽으면 풍선 안쪽의 흰 자리가
    // 통째로 구멍이 되고, 메울 재료는 풍선 밖의 그림뿐이다(풍선이 사라진다). 흰 테는 선 없이 톤·그림에 바로 닿고, 풍선의 흰 자리는 그려진 선으로 둘러싸여 있다

    [Theory]
    [InlineData(128)]   // 풍선 밖이 회색 톤
    [InlineData(255)]   // 풍선 밖이 흰 종이
    public void AddTextHalo_TextFillingAnOutlinedBubble_AddsNothing(int outside)
    {
        using var page = SolidPage(600, 500, outside);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 60, 46);   // 안쪽 120×92 — 글(마스크 74×56) 둘레에 18~23px
        var glyphs = DrawGlyphs(page, mask, 266, 225, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Null(added);
        using var diff = new Mat();
        Cv2.Absdiff(mask, into, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    [Fact]
    public void AddTextHalo_TextCrowdingAnOutlinedBubbleWithHatchLinesInside_AddsNothing()
    {
        // 글이 꽉 찬 풍선의 남은 흰 자리에 빗금 몇 줄이 있다(음영선·머리카락): 그래도 풍선이다 — 흰 자리의 좁은 귀퉁이에 든 가는 선 몇 줄로 '무늬 든 풍선의 흰 테'가 되면
        // 풍선 안쪽이 통째로 구멍이 된다(검토 2026-11-27). 흰 테인지는 풍선 안에서 다시 견준다: 획 바로 밖이 안쪽의 바깥 띠보다 뚜렷이 희어야 한다
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 46, 35);   // 안쪽 92×70
        for (int y = 232; y <= 267; y += 7)   // 왼쪽 초승달(글과 테두리 사이 19px)의 빗금 여섯 줄 — 풍선 안의 흰 픽셀만 칠한다
            for (int x = 255; x <= 272; x++)
                for (int t = 0; t < 2; t++)
                    if (page.At<Vec3b>(y + t, x).Item0 == 255) page.Set(y + t, x, new Vec3b(0, 0, 0));
        var glyphs = DrawGlyphs(page, mask, 276, 225, 4, 3);   // 오른쪽으로 치우쳐 마스크가 테두리에 걸친다
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Null(added);
    }

    [Fact]
    public void AddTextHalo_TextFillingASpikyOutlinedBubble_AddsNothing()
    {
        // 외침 풍선(삐죽삐죽한 테두리)에 글이 꽉 찼다: 테두리가 들쭉날쭉해도 그려진 선으로 둘러싸인 흰 자리는 풍선의 안쪽이다
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        var points = new List<Point>();
        for (int i = 0; i < 40; i++)
        {
            double a = 2 * Math.PI * i / 40, grow = i % 2 == 0 ? 0 : 9;   // 한 칸 걸러 9px 삐져나온 가시
            points.Add(new Point((int)Math.Round(300 + (60 + grow) * Math.Cos(a)), (int)Math.Round(250 + (46 + grow) * Math.Sin(a))));
        }
        Cv2.FillPoly(page, [points], Scalar.White);
        Cv2.Polylines(page, [points], true, Scalar.Black, 3);
        var glyphs = DrawGlyphs(page, mask, 266, 225, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Null(added);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(6)]    // 좁은 꼬리: 갇힌 흰 틈을 가릴 때(7×7 깎기) 끊겨, 풍선 안쪽이 통째로 '틈'이 될 수 있다
    public void AddTextHalo_TextFillingAnOutlinedBubbleWithATail_AddsNothing(int tailWidth)
    {
        // 풍선의 꼬리(흰 안쪽이 창 밖으로 이어진다)가 있어도 풍선이다 — '흰 자리가 창 끝에 닿으면 넓은 바탕'으로 보면 꼬리 달린 풍선마다 흰 테로 읽힌다(검토 2026-11-27)
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 60, 46);
        page.Rectangle(new Rect(300 - tailWidth / 2 - 3, 292, tailWidth + 6, 60), Scalar.Black, -1);   // 꼬리의 테두리(3px) — 흰 테를 재는 창(아래 끝 304) 밖으로 나간다
        page.Rectangle(new Rect(300 - tailWidth / 2, 286, tailWidth, 66), Scalar.White, -1);           // 꼬리의 흰 안쪽(풍선 안쪽과 이어진다)
        var glyphs = DrawGlyphs(page, mask, 266, 225, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Null(added);
    }

    [Fact]
    public void AddTextHalo_TextCrowdingTheSidesOfATallOutlinedBubble_AddsNothing()
    {
        // 위아래로 넉넉한 풍선(흰 안쪽이 창 위아래로 나간다)에서 글이 양옆 테두리에 바짝 붙었다: 바깥 띠에 테두리와 풍선 밖이 들어와 '덜 희다'가 되지만, 풍선 안에서 견주면 띠도 희다
        using var page = SolidPage(600, 600, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 300, 46, 130);
        var glyphs = DrawGlyphs(page, mask, 266, 275, 4, 3);   // 양옆 여백 12px, 위아래 105px
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.Null(added);
    }

    [Fact]
    public void AddTextHalo_OutlinedTextOnFlatBlack_StillAddsTheRim()
    {
        // 먹칠 위의 흰 테 글: 흰 자리의 바로 밖이 온통 어둡지만 선이 아니라 면이다(그 너머도 어둡다) — 풍선 테두리로 읽지 않는다
        using var page = SolidPage(500, 400, 0);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.NotNull(added);
        Assert.Equal(255, into.At<byte>(glyphs.Y + 25, glyphs.X - 6));
        Assert.True(added!.Value.Contains(new Point(glyphs.X - 6, glyphs.Y + 25)));   // 더한 자리의 상자를 돌려준다(호출부가 구멍을 모을 창을 거기까지 넓힌다)
    }

    /// <summary>가로 줄무늬(굵기 thick, 간격 pitch) 위에 흰 테(8px)를 두른 검은 글 — 빗금·줄무늬 톤·속도선 위의 대사.</summary>
    private static (Mat Page, Mat Mask, Rect Glyphs) OutlinedTextOnStripes(int pitch, int thick)
    {
        var page = SolidPage(500, 400, 255);
        for (int y = 0; y < 400; y += pitch) page.Rectangle(new Rect(0, y, 500, thick), Scalar.Black, -1);
        var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        return (page, mask, glyphs);
    }

    [Theory]
    [InlineData(7, 2)]    // 줄 사이 흰 틈이 5px — '꽉 찬 흰 면'으로 읽혀 흰 테와 이어진다(틈을 따라 창 끝까지)
    [InlineData(9, 3)]
    [InlineData(4, 1)]    // 촘촘한 줄무늬 — 틈(3px)은 흰 면이 아니다
    [InlineData(16, 8)]   // 굵은 줄(8px) — 흰 틈에 묻히지 않아 틈의 가장자리를 둘러싼 '선'으로 남는다. 그래도 그 흰 자리는 창 끝까지 이어진 바탕이지 이 글을 둘러싼 풍선이 아니다
    public void AddTextHalo_OutlinedTextOverStripes_StillAddsTheRim(int pitch, int thick)
    {
        // 줄무늬의 줄은 양쪽이 다 흰 틈이다 — 풍선 테두리(안은 희고 밖은 그림)가 아니다. 흰 틈을 '풍선 안쪽', 줄을 '둘러싼 선'으로 읽으면 흰 테를 더하지 않아
        // 지운 자리에 글자 모양의 흰 테가 남는다(검토 2026-11-27 — 줄무늬 시험이 3px 틈뿐이라 놓쳤다)
        var (page, mask, glyphs) = OutlinedTextOnStripes(pitch, thick);
        using (page) using (mask)
        {
            using var into = mask.Clone();

            var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

            Assert.NotNull(added);
            int cx = glyphs.X + glyphs.Width / 2, cy = glyphs.Y + glyphs.Height / 2;
            foreach (var (x, y) in new[] { (glyphs.X - 6, cy), (glyphs.Right + 5, cy), (cx, glyphs.Y - 6), (cx, glyphs.Bottom + 5) })
                Assert.Equal(255, into.At<byte>(y, x));
        }
    }

    [Fact]
    public void AddTextHalo_OutlinedTextInsideASmallStripedBubble_StillAddsTheRim()
    {
        // 줄무늬가 든 작은 풍선(창 안에 다 들어온다) 안의 흰 테 글: 풍선 테두리가 둘러 있지만 그 안은 흰 면이 아니라 무늬다 — 흰 테를 더해야 한다(무늬는 뒤에서 이어 메운다).
        // '테두리로 둘러싸인 흰 풍선에 글이 꽉 찼다'는 안쪽이 맨 흰 종이일 때만이다
        using var page = SolidPage(500, 400, 128);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        var inner = new Rect(glyphs.X - 18, glyphs.Y - 18, glyphs.Width + 36, glyphs.Height + 36);
        DrawBox(page, inner);
        for (int y = inner.Y + 3; y < inner.Bottom; y += 7) page.Rectangle(new Rect(inner.X, y, inner.Width, 2), Scalar.Black, -1);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        DrawGlyphs(page, mask, glyphs.X, glyphs.Y, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        var added = AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        Assert.NotNull(added);
        Assert.Equal(255, into.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));
    }

    [Fact]
    public void DrawnOutlineInterior_InkOfAnotherTextAroundTheWhite_IsNotTakenForAnOutline()
    {
        // 흰 자리를 둘러싼 것이 그려진 선이 아니라 다른 글의 획이다(큰 속 빈 글자 안의 작은 글): others로 알려 주면 선으로 세지 않는다 — 모르면 '테두리 있는 풍선'으로 읽힌다
        using var page = SolidPage(400, 400, 128);
        var hollow = new Rect(120, 120, 160, 160);
        page.Rectangle(hollow, Scalar.Black, -1);
        page.Rectangle(new Rect(hollow.X + 8, hollow.Y + 8, hollow.Width - 16, hollow.Height - 16), Scalar.White, -1);
        using var mask = EmptyMask(page);
        DrawGlyphs(page, mask, 170, 175, 3, 2);
        DilateLikeDetector(mask);
        using var others = EmptyMask(page);
        others.Rectangle(hollow, Scalar.White, -1);
        others.Rectangle(new Rect(hollow.X + 8, hollow.Y + 8, hollow.Width - 16, hollow.Height - 16), Scalar.Black, -1);
        DilateLikeDetector(others);
        using var gray = page.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var known = BubbleSolver.DrawnOutlineInterior(gray, mask, others);
        using var unknown = BubbleSolver.DrawnOutlineInterior(gray, mask);

        Assert.Null(known);
        Assert.NotNull(unknown);
    }

    [Fact]
    public void DrawnOutlineInterior_TextWiderThanTheBubble_StillFindsTheInterior()
    {
        // 글이 좁은 풍선의 양옆으로 넘쳤다: 글 바로 밖 띠(3px)의 60%가 풍선 밖(회색)과 테두리다 — 종이 밝기를 띠의 가운뎃값으로 재면 풍선 밖의 회색이 '종이'가 되어
        // 회색 바탕까지 흰 자리로 세어지고, 테두리가 그 안에 묻혀 풍선이 안 보인다(검토 2026-11-27). 종이는 띠의 밝은 쪽에서 잰다
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 30, 60);
        DrawGlyphs(page, mask, 266, 225, 4, 3);   // 글 266~334 — 풍선의 흰 안쪽(270~330)과 테두리를 넘어간다
        DilateLikeDetector(mask);
        using var gray = page.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var interior = BubbleSolver.DrawnOutlineInterior(gray, mask);

        Assert.NotNull(interior);
        Assert.Equal(255, interior!.At<byte>(205, 300));   // 글 위쪽의 풍선 안
        Assert.Equal(0, interior.At<byte>(250, 250));      // 풍선 밖의 회색 바탕
    }

    [Theory]
    [InlineData(128)]   // 풍선 밖이 회색 톤
    [InlineData(255)]   // 풍선 밖도 흰 종이 — 테두리를 넘은 마스크가 풍선 밖의 흰 종이에도 닿는다. 그쪽까지 글 둘레의 흰 자리로 치면 사이의 테두리가 묻혀 풍선이 안 보인다
    public void RegenStats_TextTouchingTheOutlineOfAWhiteBubble_NotWanted(int outside)
    {
        // 넉넉한 흰 풍선에서 글이 한쪽 테두리에 닿았다(풍선 밖은 회색 톤): 구멍 둘레의 고리에 테두리와 풍선 밖이 들어오지만 그것은 풍선의 끝이지 구멍을 지나가는 그림이 아니다.
        // 고리는 풍선 안쪽에서만 잰다 — 흰 풍선은 전처럼 LaMa로 간다(v1.18.0과 같은 길)
        using var page = SolidPage(700, 500, outside);
        using var mask = EmptyMask(page);
        DrawBubble(page, 350, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 350 + 150 - 68 - 2, 225, 4, 3);   // 오른쪽 끝이 테두리에서 2px — 마스크(3px 불림)는 테두리 위에 걸친다
        DilateLikeDetector(mask);

        var (wanted, white, _) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.False(wanted, $"흰 비율 {white:f2}");
    }

    [Fact]
    public void IsInBubble_PlainRightAroundTheText_ButArtElsewhereInTheWindow_IsNotAPlainBackground()
    {
        // 그림 위(풍선 아님)에 놓인 글: 글 바로 둘레 15px만 평평하고 조금 벗어나면 그림이 있다. 글자 지우기의 단색 판정은 그 띠만 보지만(획만 칠하니까),
        // '단색 바탕 = 테두리를 못 읽은 풍선'이라는 **풍선 판정**은 v1.18.0처럼 창(1.7배) 전체가 평평할 때만이다 — 띠만 보면 그림 위의 효과음이 대사로 넘어가 번역되고 지워진다(검토 2026-11-27)
        using var page = SolidPage(600, 500, 255);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);   // 68×50
        DilateLikeDetector(mask);
        page.Rectangle(new Rect(glyphs.Right + 21, glyphs.Y + 10, 4, 30), Scalar.Black, -1);   // 글에서 21px — 띠(획에서 15px) 밖, 1.7배 창(글에서 좌우 30px) 안의 먹 조각(창 테두리에는 안 닿게 — 닿으면 테두리의 일부로 읽혀 빠진다)
        var block = RectOf(BlockOf(glyphs, 4));

        Assert.False(BubbleSolver.IsInBubble(page, mask, block, out var why));
        Assert.NotEqual(BubbleSolver.BubbleVerdict.PlainBackground, why);
        using var copy = page.Clone();
        Assert.True(BubbleSolver.TrySolidFill(copy, mask, block));   // 지우기는 띠만 본다 — 획만 칠하고 먹 조각은 건드리지 않는다
        Assert.Equal(new Vec3b(0, 0, 0), copy.At<Vec3b>(glyphs.Y + 20, glyphs.Right + 22));
    }

    [Fact]
    public void AddTextHalo_TextInWhiteBubble_AddsNothing()
    {
        // 흰 풍선 안의 글은 둘레가 전부 희다 — 테가 아니라 풍선이므로 마스크를 키우지 않는다(키우면 LaMa 구멍만 커진다)
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        using var diff = new Mat();
        Cv2.Absdiff(mask, into, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    [Fact]
    public void AddTextHalo_TextOnStripes_AddsNothing()
    {
        // 줄무늬 톤(줄무늬 풍선) 위의 글: 획 바로 밖도 그 바깥도 같은 줄무늬다 — 흰 테가 아니다. 예전 규칙(바깥 띠의 흰 비율 ≤ 85%면 더한다)은 줄 사이의 흰 틈을
        // 8px까지 구멍에 넣어, 글이 꽉 찬 풍선에서는 구멍이 풍선 테두리와 그 밖의 그림에 닿았다 — 그러면 메울 때 밖의 그림이 풍선 안으로 딸려 들어온다(실제 26쪽의 줄무늬 풍선 둘)
        using var page = SolidPage(500, 400);
        for (int y = 0; y < 400; y += 4) Cv2.Line(page, new Point(0, y), new Point(499, y), Scalar.Black, 1);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        using var into = mask.Clone();

        AddTextHalo(page, mask, RectOf(BlockOf(glyphs, 4)), into);

        using var diff = new Mat();
        Cv2.Absdiff(mask, into, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    // ── SDXL로 다시 그릴 자리 고르기 (RegenStats, 2026-11-26 사용자 "말풍선 밖의 대사나 투명한 말풍선 부분" 만, "흰 풍선 안은 LaMa 그대로") ──
    // LaMa가 못 하는 것은 구멍을 지나가는 선·톤을 잇는 일이다 — 구멍 바로 밖의 좁은 고리가 평평한 흰(또는 검은) 바탕이면 LaMa로 충분하다.

    /// <summary>줄무늬(가로 1px 검은 줄, pitch px 간격)를 그린다 — 속도선·빗금·줄무늬 톤의 대역.</summary>
    private static void DrawStripes(Mat page, int pitch)
    {
        int rows = page.Rows, right = page.Cols - 1;
        for (int y = 0; y < rows; y += pitch)
            Cv2.Line(page, new Point(0, y), new Point(right, y), Scalar.Black, 1);
    }

    [Fact]
    public void RegenStats_TextInWhiteBubble_NotWanted()
    {
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);

        var (wanted, white, _) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.False(wanted);
        Assert.True(white >= 0.9, $"white={white:f2}");
    }

    [Theory]
    [InlineData(220)]   // 누런 스캔의 종이
    [InlineData(190)]   // 옅은 회색으로 칠한 풍선
    public void RegenStats_TextInAnOffWhiteBubble_NotWanted(int paper)
    {
        // '평평하다'는 희다(225 이상)·검다(30 이하)가 아니라 한 가지 밝기라는 뜻이다 — 누런 스캔·회색 풍선의 글이 전부 '그림이 맞닿은 자리'로 읽히면
        // v1.18.0이 LaMa로 지우던 블록이 죄다 무늬 메우기로(켜져 있으면 SDXL로) 넘어간다(검토 2026-11-27)
        using var page = SolidPage(600, 500, 120);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 150, 100, fill: paper);
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);

        var (wanted, white, black) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.False(wanted, $"흰 비율 {white:f2} 검은 비율 {black:f2}");
    }

    [Fact]
    public void RegenStats_TextTouchingTheOutlineOfAnOffWhiteBubble_NotWanted()
    {
        // 종이가 225보다 어두워도 테두리 있는 풍선은 풍선이다 — 흰 자리의 밝기는 글 바로 밖의 종이에서 잰다
        using var page = SolidPage(700, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 350, 250, 150, 100, fill: 215);
        var glyphs = DrawGlyphs(page, mask, 350 + 150 - 68 - 2, 225, 4, 3);
        DilateLikeDetector(mask);

        var (wanted, white, black) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.False(wanted, $"흰 비율 {white:f2} 검은 비율 {black:f2}");
    }

    [Fact]
    public void RegenStats_TextOverStripes_Wanted()
    {
        using var page = SolidPage(500, 400);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);

        var (wanted, white, black) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.True(wanted, $"white={white:f2} black={black:f2}");
        Assert.InRange(white, 0.6, 0.85);   // 4px마다 1px 검은 줄 = 흰 75%
    }

    [Fact]
    public void RegenStats_AFewThinStrandsCrossingTheText_Wanted()
    {
        // 풍선 안으로 머리카락·빗금 몇 가닥이 지나가는 글(사용자가 보낸 26쪽의 두 풍선): 고리의 흰 비율이 0.93·0.95였다 — 깨끗한 흰 풍선(여섯 블록 모두 1.00)과는 갈리지만
        // 문턱이 0.90이면 '평평한 흰 바탕'으로 읽혀 LaMa에 남는다. 가는 선은 고리의 몇 %뿐이다
        using var page = SolidPage(500, 400);
        foreach (int dx in new[] { -30, 0, 30 })
            Cv2.Line(page, new Point(150 + dx, 100), new Point(330 + dx, 260), Scalar.Black, 2);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);

        var (wanted, white, _) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.InRange(white, 0.88, 0.975);
        Assert.True(wanted, $"white={white:f3}");
    }

    [Fact]
    public void RegenStats_OutlinedTextOnHalftone_Wanted()
    {
        // 톤 위 흰 테 글: 테는 AddTextHalo가 구멍에 넣었으므로 고리는 테 밖의 톤에 놓인다
        using var page = SolidPage(500, 400);
        for (int y = 0; y < 400; y += 4)
            for (int x = 0; x < 500; x += 4)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y - 8, glyphs.Width + 16, glyphs.Height + 16), Scalar.White, -1);
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        var block = RectOf(BlockOf(glyphs, 4));
        using var erase = mask.Clone();
        AddTextHalo(page, mask, block, erase);

        var (wanted, white, _) = RegenStats(page, erase, block);

        Assert.True(wanted, $"white={white:f2}");
    }

    [Fact]
    public void RegenStats_WhiteTextOnFlatBlack_NotWanted()
    {
        using var page = SolidPage(500, 400, 0);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 200, 150, 4, 3, ink: 255);
        DilateLikeDetector(mask);

        var (wanted, _, black) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 4)));

        Assert.False(wanted);
        Assert.True(black >= 0.9, $"black={black:f2}");
    }

    [Fact]
    public void RegenStats_NeighbouringText_IsNotCountedAsArt()
    {
        // 옆 블록의 글자(지울 마스크에 같이 들어 있다)가 고리에 걸려도 "그림"으로 세지 않는다 — 나뉜 풍선의 옆 줄이 흰 풍선을 대상으로 만들지 않게
        using var page = SolidPage(500, 400);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 200, 150, 4, 3);
        DrawGlyphs(page, mask, 200 + glyphs.Width + 10, 150, 4, 3);   // 10px 옆의 다른 블록
        DilateLikeDetector(mask);

        var (wanted, white, _) = RegenStats(page, mask, RectOf(BlockOf(glyphs, 2)));

        Assert.False(wanted);
        Assert.True(white >= 0.9, $"white={white:f2}");
    }

    [Fact]
    public void RegenStats_BlockAtTheImageCorner_DoesNotThrow()
    {
        using var page = SolidPage(200, 150);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 0, 0, 3, 2);
        DilateLikeDetector(mask);

        var (wanted, _, _) = RegenStats(page, mask, new Rect(0, 0, glyphs.Width + 4, glyphs.Height + 4));

        Assert.True(wanted);   // 그림 밖으로 나간 고리는 잘리고, 남은 고리로 잰다
    }

    [Fact]
    public void RegenStats_EmptyMask_NotWanted()
    {
        using var page = SolidPage(200, 150);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);

        Assert.Equal((false, 0.0, 0.0), RegenStats(page, mask, new Rect(50, 50, 60, 40)));
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

        Assert.True(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why));
        Assert.Equal(BubbleSolver.BubbleVerdict.Bubble, why);   // 단색 바탕 폴백이 아니라 닫힌 테두리 규칙으로 — 폴백만으로도 참이 나와 테두리 규칙이 깨져도 모르던 것(검수 2026-11-19)

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

        Assert.True(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why));
        Assert.Equal(BubbleSolver.BubbleVerdict.Bubble, why);   // 최소 창 규칙이 테두리를 창 안에 넣었다 — 폴백이면 이 규칙이 깨져도 모른다
    }

    [Fact]
    public void IsInBubble_BubbleCutByThePageEdge_True()
    {
        // 쪽 가장자리에 잘린 풍선: 테두리가 창의 왼쪽 끝에 닿지만 창도 쪽의 왼쪽 끝이다 — 억지로 닫은 사각형이 아니라 잘린 풍선이다(ClosedOutline의 예외)
        using var page = SolidPage(600, 500, 200);
        using var mask = EmptyMask(page);
        DrawBubble(page, 40, 250, 90, 60);
        var glyphs = DrawGlyphs(page, mask, 6, 225, 4, 3);
        DilateLikeDetector(mask);

        bool inBubble = BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why);
        Assert.Equal(BubbleSolver.BubbleVerdict.Bubble, why);
        Assert.True(inBubble);
    }

    [Fact]
    public void IsInBubble_NeighbourTextInsideTheWindow_StillJudgesOwnBubble()
    {
        // 창(쪽의 40%×30%)이 커서 옆 풍선 밖의 글이 들어온다 — 이 블록의 글만 남겨 재야 자기 풍선의 테두리를 찾는다(실제 원고 248블록 중 207이 이 길, 2026-11-18)
        using var page = SolidPage(600, 500, 200);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 90, 60);
        var glyphs = DrawGlyphs(page, mask, 266, 225, 4, 3);
        DrawGlyphs(page, mask, 396, 190, 1, 3);   // 풍선 밖 바로 옆의 다른 글(창 안)
        DilateLikeDetector(mask);

        bool inBubble = BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why);
        Assert.Equal(BubbleSolver.BubbleVerdict.Bubble, why);
        Assert.True(inBubble);
    }

    [Fact]
    public void IsInBubble_PageSmallerThanTheMinimumWindow_DoesNotThrow()
    {
        // 작은 쪽(썸네일·조각): 최소 창(쪽의 40%×30% = 80×45)이 쪽보다 클 수 없다 — 창은 블록의 3배(120×66)로 잡히고 그 안에 풍선(테두리 포함 106×56)이 든다
        using var page = SolidPage(200, 150, 200);
        using var mask = EmptyMask(page);
        DrawBubble(page, 100, 75, 50, 25);
        var glyphs = DrawGlyphs(page, mask, 86, 68, 2, 1);
        DilateLikeDetector(mask);

        bool inBubble = BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why);
        Assert.Equal(BubbleSolver.BubbleVerdict.Bubble, why);
        Assert.True(inBubble);
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
            // 칸은 창(블록 68×50의 가운데 (284,235) → 240×150) 안에 들어와야 '닫힌 테두리'로 잡힌다 — 전에는 520×420 칸이 창 밖이라 빗금 경우와 같은 길을 탔다(검수 2026-11-19)
            var inner = new Rect(184, 180, 200, 110);
            DrawBox(page, inner);
            using var art = new Mat(page, inner);
            for (int k = -110; k < 200; k += 9) Cv2.Line(art, new Point(k, 0), new Point(k + 110, 110), Scalar.Black, 2);   // 칸 안은 그림
        }
        var glyphs = DrawGlyphs(page, mask, 250, 210, 4, 3);
        DilateLikeDetector(mask);

        Assert.False(BubbleSolver.IsInBubble(page, mask, RectOf(BlockOf(glyphs, 4)), out var why));
        if (kind == "panel") Assert.Equal(BubbleSolver.BubbleVerdict.Busy, why);   // 닫힌 테두리는 있지만 안이 그림으로 어지럽다(BubbleInsideStdMax)
        else Assert.NotEqual(BubbleSolver.BubbleVerdict.Bubble, why);
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
    public void IsInBubble_NoTextInMask_TrueKeepsItForTranslation()
    {
        // 글 마스크에 이 블록의 획이 없으면 판정할 수 없다 — 모르면 번역 쪽(true)으로 둔다. 전에는 false라 효과음 꼴이면 조용히 빠졌다(검수 2026-11-19)
        using var page = SolidPage(600, 500);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 90, 60);

        Assert.True(BubbleSolver.IsInBubble(page, mask, new Rect(246, 206, 76, 58), out var why));
        Assert.Equal(BubbleSolver.BubbleVerdict.NoText, why);
    }
}
