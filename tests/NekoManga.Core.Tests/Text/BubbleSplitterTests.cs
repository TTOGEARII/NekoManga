using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>말풍선 판정(BubbleSplitter) — 한 블록에 묶인 여러 풍선 나누기(Split)와 기둥마다 갈라진 한 풍선 합치기(MergeSiblings).
/// 회색 쪽에 테두리 있는 풍선을 그리고, 글자 마스크는 감지기처럼 7px 불려 쓴다.</summary>
public class BubbleSplitterTests
{
    [Theory]
    [InlineData("touching")]   // 테두리가 맞닿은 두 풍선
    [InlineData("joined")]     // 안쪽이 잘록한 목으로 이어진 결합 풍선
    public void Split_TextOfTwoBubblesInOneBlock_GivesOneBlockPerBubble(string layout)
    {
        using var page = SolidPage(800, 500, 150);
        using var mask = EmptyMask(page);
        Rect left, right;
        if (layout == "touching")
        {
            DrawBubble(page, 260, 250, 120, 150);
            DrawBubble(page, 500, 250, 120, 150);
            left = DrawGlyphs(page, mask, 230, 170, 3, 8);
            right = DrawGlyphs(page, mask, 470, 170, 3, 8);
        }
        else
        {
            DrawJoinedBubbles(page, 3, (280, 250, 130, 110), (500, 250, 130, 110));
            left = DrawGlyphs(page, mask, 240, 190, 3, 7);
            right = DrawGlyphs(page, mask, 460, 190, 3, 7);
        }
        DilateLikeDetector(mask);
        var block = new DetectedBlock(left.X - 2, left.Y - 2, right.Right + 2, right.Bottom + 2, 0.8f, true);

        var parts = BubbleSplitter.Split(page, mask, block);

        // 풍선마다 블록 하나: 제 풍선의 글은 다 담고 옆 풍선의 글은 건드리지 않는다
        Assert.Equal(2, parts.Count);
        var leftPart = Assert.Single(parts, p => Covers(p, left));
        var rightPart = Assert.Single(parts, p => Covers(p, right));
        Assert.False(Touches(leftPart, right));
        Assert.False(Touches(rightPart, left));
        // 나눈 블록은 원래 블록의 신뢰도·언어를 물려받는다
        Assert.All(parts, p =>
        {
            Assert.Equal(0.8f, p.Confidence);
            Assert.True(p.LangJa);
        });
    }

    [Theory]
    [InlineData("close-columns")]    // 한 풍선 안에서 붙어 있는 글 기둥 둘
    [InlineData("speck")]            // 글 옆의 작은 잡티
    [InlineData("too-little-ink")]   // 나눠 볼 만큼 글이 없다
    public void Split_TextOfOneBubble_ReturnsOriginalBlock(string layout)
    {
        // 의미 있는 글 덩어리가 하나뿐이면 풍선 하나다 (잡티·너무 적은 글은 덩어리로 치지 않는다) — 원래 블록을 그대로 돌려준다
        using var page = SolidPage(800, 500, 150);
        using var mask = EmptyMask(page);
        DetectedBlock block;
        switch (layout)
        {
            case "close-columns":
            {
                DrawBubble(page, 400, 250, 250, 150);
                var a = DrawGlyphs(page, mask, 330, 170, 3, 8);
                var b = DrawGlyphs(page, mask, 390, 170, 3, 8);
                block = new DetectedBlock(a.X - 2, a.Y - 2, b.Right + 2, b.Bottom + 2, 0.9f, true);
                break;
            }
            case "speck":
            {
                DrawBubble(page, 400, 250, 250, 150);
                var a = DrawGlyphs(page, mask, 330, 170, 3, 8);
                Cv2.Rectangle(mask, new Rect(450, 300, 4, 4), Scalar.White, -1);
                block = new DetectedBlock(a.X - 2, a.Y - 2, a.X + 132, a.Bottom + 12, 0.9f, true);
                break;
            }
            default:
            {
                DrawBubble(page, 260, 250, 120, 150);
                DrawBubble(page, 500, 250, 120, 150);
                var a = DrawGlyphs(page, mask, 250, 240, 1, 1, glyph: 6);
                var b = DrawGlyphs(page, mask, 490, 240, 1, 1, glyph: 6);
                block = new DetectedBlock(a.X - 2, a.Y - 2, b.Right + 2, b.Bottom + 2, 0.9f, true);
                break;
            }
        }
        if (layout != "too-little-ink") DilateLikeDetector(mask);

        Assert.Equal(new[] { block }, BubbleSplitter.Split(page, mask, block));
    }

    [Theory]
    [InlineData(false, 1)]   // 같은 상자 안에서 위아래로 띄어 쓴 글 — 한 풍선
    [InlineData(true, 2)]    // 사이를 먹선이 가로지른다 — 다른 칸(풍선)
    public void Split_StackedTextInOneColumn_InkLineBetweenDecides(bool inkLine, int expectedParts)
    {
        using var page = SolidPage(600, 700, 150);
        using var mask = EmptyMask(page);
        DrawBox(page, new Rect(150, 100, 300, 500));
        var top = DrawGlyphs(page, mask, 250, 150, 3, 6);
        var bottom = DrawGlyphs(page, mask, 250, 400, 3, 6);
        if (inkLine) Cv2.Line(page, new Point(150, 340), new Point(450, 340), Scalar.Black, 3);
        DilateLikeDetector(mask);
        var block = new DetectedBlock(top.X - 2, top.Y - 2, top.Right + 2, bottom.Bottom + 2, 0.9f, true);

        var parts = BubbleSplitter.Split(page, mask, block);

        Assert.Equal(expectedParts, parts.Count);
        if (expectedParts == 1) Assert.Equal(block, parts[0]);
        else Assert.False(Touches(Assert.Single(parts, p => Covers(p, top)), bottom));
    }

    [Fact]
    public void Split_StackedTextInOneColumn_OverlappedBubblesWithoutSeam_GivesOneBlockPerBubble()
    {
        // 위아래로 겹친 두 풍선(이음선 없음)의 글이 같은 기둥에 있다 — 사이에 먹선이 없어도 목이 가르면 다른 풍선이다.
        // 실측(2026-11-16, 번역본 대조 117·139쪽): 여기서 다시 붙이면 한 블록이 두 풍선에 걸쳐 남는다
        using var page = SolidPage(600, 800, 150);
        using var mask = EmptyMask(page);
        DrawJoinedBubbles(page, 3, (300, 250, 150, 170), (300, 530, 150, 170));
        var top = DrawGlyphs(page, mask, 250, 150, 3, 9);
        var bottom = DrawGlyphs(page, mask, 250, 470, 3, 9);
        DilateLikeDetector(mask);
        var block = new DetectedBlock(top.X - 2, top.Y - 2, top.Right + 2, bottom.Bottom + 2, 0.9f, true);

        var parts = BubbleSplitter.Split(page, mask, block);

        Assert.Equal(2, parts.Count);
        Assert.False(Touches(Assert.Single(parts, p => Covers(p, top)), bottom));
        Assert.False(Touches(Assert.Single(parts, p => Covers(p, bottom)), top));
    }

    [Fact]
    public void Split_MaskOfWrongSize_ReturnsOriginalBlockInsteadOfThrowing()
    {
        // 나누기는 보너스다 — 무엇이 잘못돼도 원래 블록 그대로 돌려주고 번역은 계속된다
        using var page = SolidPage(800, 500, 150);
        using var mask = new Mat(10, 10, MatType.CV_8UC1, Scalar.White);
        var block = new DetectedBlock(100, 100, 400, 300, 0.5f, false);

        Assert.Equal(new[] { block }, BubbleSplitter.Split(page, mask, block));
    }

    [Fact]
    public void MergeSiblings_ColumnsOfSameBubble_BecomeOneBlock()
    {
        // 세로쓰기 풍선의 글 기둥이 기둥마다 따로 잡힌 것 — 둘을 감싸는 블록 하나로, 신뢰도는 큰 쪽, 언어는 어느 한쪽이라도 일본어면 일본어
        using var page = SolidPage(800, 600, 150);
        using var mask = EmptyMask(page);
        DrawBubble(page, 400, 300, 200, 220);
        var first = DrawGlyphs(page, mask, 410, 180, 1, 10);
        var second = DrawGlyphs(page, mask, 375, 180, 1, 8);
        DilateLikeDetector(mask);
        var a = BlockOf(first, 3, 0.6f, false);
        var b = BlockOf(second, 3, 0.9f, true);

        var merged = Assert.Single(BubbleSplitter.MergeSiblings(page, mask, new[] { a, b }));

        Assert.Equal(new DetectedBlock(b.X1, a.Y1, a.X2, a.Y2, 0.9f, true), merged);
    }

    [Theory]
    [InlineData("touching-bubbles")]   // 테두리가 맞닿은 두 풍선의 기둥
    [InlineData("joined-with-seam")]   // 결합 풍선 — 이음선이 두 기둥 사이를 지난다
    [InlineData("stacked-with-rule")]  // 위아래 두 줄 사이를 칸 선이 가른다
    [InlineData("far-apart")]          // 같은 흰 바탕이지만 글자 크기보다 훨씬 멀다
    public void MergeSiblings_BlocksOfDifferentBubbles_StayApartInOriginalOrder(string layout)
    {
        using var page = SolidPage(800, 600, layout == "far-apart" ? 255 : 150);
        using var mask = EmptyMask(page);
        Rect first, second;
        switch (layout)
        {
            case "touching-bubbles":
                DrawBubble(page, 330, 300, 60, 200);
                DrawBubble(page, 456, 300, 60, 200);
                first = DrawGlyphs(page, mask, 400, 180, 1, 10);
                second = DrawGlyphs(page, mask, 370, 180, 1, 10);
                break;
            case "joined-with-seam":
                DrawJoinedBubbles(page, 3, (330, 300, 130, 180), (520, 300, 130, 180));
                Cv2.Line(page, new Point(429, 100), new Point(429, 500), Scalar.Black, 2);
                first = DrawGlyphs(page, mask, 440, 200, 1, 10);
                second = DrawGlyphs(page, mask, 410, 200, 1, 10);
                break;
            case "stacked-with-rule":
                DrawBox(page, new Rect(100, 100, 500, 300));
                Cv2.Line(page, new Point(100, 219), new Point(600, 219), Scalar.Black, 2);
                first = DrawGlyphs(page, mask, 250, 200, 8, 1);
                second = DrawGlyphs(page, mask, 250, 226, 8, 1);
                break;
            default:
                first = DrawGlyphs(page, mask, 500, 180, 1, 10);
                second = DrawGlyphs(page, mask, 200, 180, 1, 10);
                break;
        }
        DilateLikeDetector(mask);
        // 읽기 순서와 다르게 넣는다 — 합칠 것이 없으면 순서도 손대지 않는다
        var blocks = new[] { BlockOf(second, 3), BlockOf(first, 3) };

        Assert.Equal(blocks, BubbleSplitter.MergeSiblings(page, mask, blocks));
    }

    [Theory]
    [InlineData("side-by-side")]   // 좌우로 겹친 두 풍선
    [InlineData("stacked")]        // 위아래로 겹친 두 풍선
    [InlineData("diagonal")]       // 비스듬히 겹친 두 풍선
    [InlineData("small-on-big")]   // 큰 풍선 옆에 작은 풍선이 붙었다
    [InlineData("cut-by-page-edge")]   // 위아래로 겹친 두 풍선 — 위 풍선이 쪽 위 가장자리에 잘렸다(흰 영역이 쪽 끝에 닿는다)
    public void MergeSiblings_OverlappedBubblesWithoutSeam_KeepOneBlockPerBubble(string layout)
    {
        // 이음선 없이 안이 통째로 이어진 겹친 풍선 — 흰 영역은 하나지만 윤곽이 양쪽에서 잘록하게 패어 있다(목).
        // 목을 사이에 둔 두 글은 다른 풍선의 대사다: 합치지 않고 풍선마다 제 자리에 식자한다
        using var page = SolidPage(900, 700, 150);
        using var mask = EmptyMask(page);
        Rect first, second;
        switch (layout)
        {
            case "side-by-side":
                DrawJoinedBubbles(page, 3, (330, 300, 130, 180), (520, 300, 130, 180));
                first = DrawGlyphs(page, mask, 440, 220, 3, 9);
                second = DrawGlyphs(page, mask, 340, 220, 3, 9);
                break;
            case "stacked":
                DrawJoinedBubbles(page, 3, (400, 220, 150, 110), (400, 400, 150, 110));
                first = DrawGlyphs(page, mask, 330, 200, 8, 4);
                second = DrawGlyphs(page, mask, 330, 335, 8, 4);
                break;
            case "cut-by-page-edge":
                DrawJoinedBubbles(page, 3, (400, 50, 110, 80), (400, 180, 110, 80));   // 넓이는 쪽의 8%쯤
                first = DrawGlyphs(page, mask, 357, 15, 5, 4);
                second = DrawGlyphs(page, mask, 357, 150, 5, 4);
                break;
            case "diagonal":
                DrawJoinedBubbles(page, 3, (300, 250, 140, 120), (480, 380, 140, 120));
                first = DrawGlyphs(page, mask, 412, 290, 3, 5);
                second = DrawGlyphs(page, mask, 310, 240, 3, 5);
                break;
            default:
                DrawJoinedBubbles(page, 3, (360, 320, 190, 220), (600, 300, 90, 110));
                first = DrawGlyphs(page, mask, 570, 250, 3, 6);
                second = DrawGlyphs(page, mask, 470, 250, 3, 9);
                break;
        }
        DilateLikeDetector(mask);
        var blocks = new[] { BlockOf(first, 3), BlockOf(second, 3) };

        Assert.Equal(blocks, BubbleSplitter.MergeSiblings(page, mask, blocks));
    }

    [Fact]
    public void NeckLine_BlocksOffTheLineThroughTheNeck_StillFindsTheNeckBetweenThem()
    {
        // 비스듬히 겹친 두 풍선 — 글 블록이 제 풍선의 한쪽에 치우쳐 있으면 두 블록 중심을 이은 선이 목을 비켜 지나간다
        // (실측 2026-11-16: 뾰족한 풍선 둘이 비스듬히 겹친 곳에서 한 블록이 효과음까지 품어 길쭉했다 — 목은 찾았는데 "중심선이 목을 지나야 한다"에 걸려
        //  두 대사가 한 블록으로 합쳐지고 글이 풍선 밖으로 나갔다). 목이 두 블록을 서로 다른 쪽으로 가르면 겹친 풍선이다
        using var page = SolidPage(900, 700, 150);
        DrawJoinedBubbles(page, 3, (300, 250, 140, 120), (480, 380, 140, 120));
        using var gray = new Mat();
        Cv2.CvtColor(page, gray, ColorConversionCodes.BGR2GRAY);
        var upper = new DetectedBlock(360, 150, 420, 230, 1f, true);    // 왼쪽 위 풍선의 오른쪽 위
        var lower = new DetectedBlock(530, 330, 590, 410, 1f, true);    // 오른쪽 아래 풍선의 오른쪽
        var sameLobe = new DetectedBlock(200, 220, 260, 300, 1f, true); // 왼쪽 위 풍선의 왼쪽

        Assert.NotNull(BubbleSplitter.NeckLine(gray, upper, lower));
        Assert.Null(BubbleSplitter.NeckLine(gray, upper, sameLobe));
    }

    [Fact]
    public void MergeSiblings_ChainOfThreeOverlappedBubbles_KeepsThreeBlocks()
    {
        using var page = SolidPage(1000, 600, 150);
        using var mask = EmptyMask(page);
        DrawJoinedBubbles(page, 3, (250, 300, 130, 180), (440, 300, 130, 180), (630, 300, 130, 180));
        var blocks = new[]
        {
            BlockOf(DrawGlyphs(page, mask, 560, 220, 3, 9), 3),
            BlockOf(DrawGlyphs(page, mask, 468, 220, 3, 9), 3),   // 가운데 풍선 — 오른쪽 목 옆
            BlockOf(DrawGlyphs(page, mask, 372, 220, 3, 9), 3),   // 가운데 풍선 — 왼쪽 목 옆
            BlockOf(DrawGlyphs(page, mask, 270, 220, 3, 9), 3),
        };
        DilateLikeDetector(mask);

        var result = BubbleSplitter.MergeSiblings(page, mask, blocks);

        // 가운데 풍선의 두 기둥만 합쳐진다 — 양옆 풍선의 글은 따로
        Assert.Equal(new[]
        {
            blocks[0],
            new DetectedBlock(blocks[2].X1, blocks[2].Y1, blocks[1].X2, blocks[1].Y2, 0.9f, true),
            blocks[3],
        }, result);
    }

    [Theory]
    [InlineData("tail")]         // 꼬리 달린 풍선 — 꼬리 양옆의 패임은 두 글 사이를 지나지 않는다
    [InlineData("cloud")]        // 구름 풍선 — 패임이 촘촘하고 얕다
    [InlineData("burst")]        // 외침 풍선(뾰족뾰족) — 가시 사이의 패임
    [InlineData("almost-one")]   // 거의 다 겹친 두 타원 — 목이 풍선 폭과 다름없다(한 풍선)
    [InlineData("box-notch")]    // 한쪽 귀퉁이만 팬 상자 — 맞은편에 짝이 없다
    [InlineData("unframed")]     // 테두리 없는 흰 바탕의 글
    public void MergeSiblings_ColumnsOfOneBubbleWithConcaveOutline_StillBecomeOneBlock(string layout)
    {
        // 겹친 풍선을 가르는 기준이 한 풍선 안의 글 기둥을 가르면 안 된다(2026-11-10에 되돌린 PinchedBetween의 실패) — 윤곽이 패인 풍선도 예전처럼 한 블록
        using var page = SolidPage(900, 700, layout == "unframed" ? 255 : 150);
        using var mask = EmptyMask(page);
        switch (layout)
        {
            case "tail":
                DrawBubble(page, 400, 300, 200, 220);
                Cv2.FillConvexPoly(page, new[] { new Point(380, 500), new Point(430, 500), new Point(400, 640) }, Scalar.White);
                Cv2.Line(page, new Point(378, 518), new Point(400, 642), Scalar.Black, 3);
                Cv2.Line(page, new Point(432, 518), new Point(400, 642), Scalar.Black, 3);
                break;
            case "cloud":
                for (int pass = 0; pass < 2; pass++)
                    for (int k = 0; k < 14; k++)
                    {
                        double t = 2 * Math.PI * k / 14;
                        var c = new Point(400 + (int)(170 * Math.Cos(t)), 300 + (int)(190 * Math.Sin(t)));
                        Cv2.Circle(page, c, pass == 0 ? 58 : 55, pass == 0 ? Scalar.Black : Scalar.White, -1);
                    }
                Cv2.Ellipse(page, new Point(400, 300), new Size(170, 190), 0, 0, 360, Scalar.White, -1);
                break;
            case "burst":
            {
                var pts = new List<Point>();
                for (int k = 0; k < 24; k++)
                {
                    double t = 2 * Math.PI * k / 24, r = k % 2 == 0 ? 1.0 : 0.72;
                    pts.Add(new Point(400 + (int)(230 * r * Math.Cos(t)), 300 + (int)(250 * r * Math.Sin(t))));
                }
                Cv2.FillPoly(page, new[] { pts.ToArray() }, Scalar.White);
                Cv2.Polylines(page, new[] { pts.ToArray() }, true, Scalar.Black, 3);
                break;
            }
            case "almost-one":
                DrawJoinedBubbles(page, 3, (370, 300, 180, 220), (430, 300, 180, 220));
                break;
            case "box-notch":
                DrawBox(page, new Rect(200, 100, 400, 420));
                Cv2.Rectangle(page, new Rect(385, 97, 30, 60), new Scalar(150, 150, 150), -1);
                Cv2.Rectangle(page, new Rect(385, 97, 30, 60), Scalar.Black, 3);
                break;
        }
        var first = DrawGlyphs(page, mask, 410, 200, 3, 9);
        var second = DrawGlyphs(page, mask, 322, 200, 3, 9);
        DilateLikeDetector(mask);
        var a = BlockOf(first, 3);
        var b = BlockOf(second, 3);

        var merged = Assert.Single(BubbleSplitter.MergeSiblings(page, mask, new[] { a, b }));

        Assert.Equal(new DetectedBlock(b.X1, a.Y1, a.X2, a.Y2, 0.9f, true), merged);
    }

    [Fact]
    public void MergeSiblings_ChainOfColumns_MergesTransitivelyAndSortsInReadingOrder()
    {
        using var page = SolidPage(900, 700, 150);
        using var mask = EmptyMask(page);
        DrawBubble(page, 330, 350, 200, 220);
        DrawBubble(page, 720, 300, 120, 150);
        // 왼쪽 풍선: 기둥 셋이 이웃끼리만 가깝다 (양 끝 기둥은 서로 멀다)
        var c1 = BlockOf(DrawGlyphs(page, mask, 420, 200, 1, 10), 3);
        var c2 = BlockOf(DrawGlyphs(page, mask, 385, 200, 1, 10), 3);
        var c3 = BlockOf(DrawGlyphs(page, mask, 350, 200, 1, 10), 3);
        // 오른쪽 풍선: 같은 높이에서 시작하는 기둥 하나
        var other = BlockOf(DrawGlyphs(page, mask, 700, 200, 1, 6), 3);
        DilateLikeDetector(mask);

        var result = BubbleSplitter.MergeSiblings(page, mask, new[] { c3, other, c1, c2 });

        // 일본 만화 읽기 순서: 위→아래, 같은 높이면 오른쪽→왼쪽
        Assert.Equal(new[]
        {
            other,
            new DetectedBlock(c3.X1, c1.Y1, c1.X2, c1.Y2, c1.Confidence, true),
        }, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MergeSiblings_FewerThanTwoBlocks_ReturnsThemAsIs(int count)
    {
        // 합칠 상대가 없으면 그림도 보지 않는다 — 빈 Mat이어도 된다
        using var page = new Mat();
        using var mask = new Mat();
        var blocks = new[] { new DetectedBlock(10, 20, 30, 40, 0.5f, true) }.Take(count).ToArray();

        Assert.Equal(blocks, BubbleSplitter.MergeSiblings(page, mask, blocks));
    }

    private static bool Covers(DetectedBlock b, Rect r) =>
        b.X1 <= r.X && b.Y1 <= r.Y && b.X2 >= r.Right && b.Y2 >= r.Bottom;

    private static bool Touches(DetectedBlock b, Rect r) =>
        b.X1 < r.Right && r.X < b.X2 && b.Y1 < r.Bottom && r.Y < b.Y2;
}
