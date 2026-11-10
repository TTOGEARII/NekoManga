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
