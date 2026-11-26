using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>대사 블록에 붙어 잡힌 손글씨 효과음 가르기(BubbleSplitter.SplitOffGiants)와, 지우지 않을 블록의 획 지키기(EmbeddedTranslator.ProtectStrokes).
/// 2026-11-26 사용자 "이런 부분은 효과음이야": 풍선 안의 대사와 그 아래 그림 위에 크게 그린 효과음이 한 블록으로 잡혀, 대사를 지우면서 효과음의 획까지 LaMa로 지워졌다(실제 05쪽).</summary>
public class SfxSplitTests
{
    /// <summary>회색 톤 쪽 — 흰 풍선 안의 작은 글(글자끼리 붙지 않게 간격 8)과, 풍선 밖 아래에 크게 그린 속 빈 획(효과음). 감지기는 그 획도 글로 본다(마스크에 통째로).</summary>
    private static (Mat Page, Mat Mask, Rect Glyphs, Rect Stroke) BubbleWithAGiantStrokeBelow()
    {
        var page = SolidPage(900, 1100, 150);
        var mask = EmptyMask(page);
        DrawBubble(page, 300, 300, 170, 130);
        var glyphs = DrawGlyphs(page, mask, 240, 250, 6, 4, glyph: 14, gap: 8);
        Point p = new(430, 470), q = new(620, 800);
        Cv2.Line(page, p, q, Scalar.Black, 60);
        Cv2.Line(page, p, q, Scalar.White, 44);   // 속이 빈 글자(袋文字)
        Cv2.Line(mask, p, q, Scalar.White, 60);
        DilateLikeDetector(mask);
        return (page, mask, glyphs, new Rect(400, 440, 250, 390));
    }

    private static DetectedBlock Union(Rect a, Rect b)
    {
        var u = a.Union(b);
        return new DetectedBlock(u.X - 4, u.Y - 4, u.Right + 4, u.Bottom + 4, 0.9f, true);
    }

    [Fact]
    public void SplitOffGiants_BubbleTextWithAGiantStrokeOutside_SplitsTheStrokeOffAsHandLettered()
    {
        var (page, mask, glyphs, stroke) = BubbleWithAGiantStrokeBelow();
        using (page) using (mask)
        {
            var parts = BubbleSplitter.SplitOffGiants(page, mask, [Union(glyphs, stroke)]);

            Assert.Equal(2, parts.Count);
            var text = Assert.Single(parts, b => !b.HandLettered);
            var sfx = Assert.Single(parts, b => b.HandLettered);
            var textRect = RectOf(text); var sfxRect = RectOf(sfx);
            // 대사 블록은 풍선 안의 글에 맞게 줄었다 — 효과음 쪽으로 뻗지 않는다
            Assert.True(textRect.Contains(new Point(glyphs.X + 5, glyphs.Y + 5)) && textRect.Contains(new Point(glyphs.Right - 5, glyphs.Bottom - 5)));
            Assert.True(textRect.Bottom < 440, $"대사 블록의 아래 끝 {textRect.Bottom}");
            Assert.True(sfxRect.Contains(new Point(525, 635)));   // 획의 한가운데
            Assert.False(sfxRect.Contains(new Point(glyphs.X + 50, glyphs.Y + 30)));
        }
    }

    [Fact]
    public void SplitOffGiants_TextWhollyInsideItsBubble_IsLeftAlone()
    {
        using var page = SolidPage(900, 1100, 150);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 300, 170, 130);
        var glyphs = DrawGlyphs(page, mask, 240, 250, 6, 4, glyph: 14, gap: 8);
        DilateLikeDetector(mask);
        var block = BlockOf(glyphs, 4);

        Assert.Equal([block], BubbleSplitter.SplitOffGiants(page, mask, [block]));
    }

    [Fact]
    public void SplitOffGiants_SameSizeTextSpillingOutOfTheBubble_IsLeftAlone()
    {
        // 풍선 밖으로 벗어난 대사(같은 크기의 글)는 효과음이 아니다 — 갈라내면 그 글이 번역되지 않고 남는다. 가르는 것은 풍선 안의 글보다 훨씬 굵고 큰 획뿐
        using var page = SolidPage(900, 1100, 150);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 300, 120, 110);
        var glyphs = DrawGlyphs(page, mask, 240, 250, 12, 4, glyph: 14, gap: 8);   // 오른쪽 절반이 풍선 밖 톤 위에 놓인다
        DilateLikeDetector(mask);
        var block = BlockOf(glyphs, 4);

        Assert.Equal([block], BubbleSplitter.SplitOffGiants(page, mask, [block]));
    }

    [Fact]
    public void SplitOffGiants_NoBubbleAroundTheText_IsLeftAlone()
    {
        // 밝은 종이 위에 바로 놓인 글 + 큰 획: 닫힌 밝은 영역(풍선)이 없으면 무엇이 대사고 무엇이 효과음인지 가를 근거가 없다.
        // (바탕이 밝아야 '풍선 후보'까지 간다 — 전에는 톤 150이라 밝은 픽셀이 하나도 없어 후보를 보기도 전에 끝났다, 검토 2026-11-27. 밝은 바탕은 창 가장자리에 닿아 풍선이 아니다)
        using var page = SolidPage(900, 1100, 230);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 240, 250, 6, 4, glyph: 14, gap: 8);
        Point p = new(430, 470), q = new(620, 800);
        Cv2.Line(page, p, q, Scalar.Black, 60);
        Cv2.Line(mask, p, q, Scalar.White, 60);
        DilateLikeDetector(mask);
        var block = Union(glyphs, new Rect(400, 440, 250, 390));

        Assert.Equal([block], BubbleSplitter.SplitOffGiants(page, mask, [block]));
    }

    [Fact]
    public void SplitOffGiants_WhiteTextOnDark_WithASmallAndABigGlyph_IsLeftAlone()
    {
        // 먹 바탕의 흰 글: 밝은 성분은 풍선이 아니라 글자 자신이다. 작은 글자를 '풍선'으로, 큰 글자를 '풍선 밖의 거대한 획'으로 읽으면 큰 글자가 효과음으로 떨어져
        // 번역도 지우기도 안 된다(검토 2026-11-27). 풍선의 흰 안쪽은 글 획 밖으로 넓게 퍼져 있다 — 마스크에 거의 다 덮인 밝은 성분은 풍선 후보가 아니다
        using var page = SolidPage(900, 1100, 0);
        using var mask = EmptyMask(page);
        var small = new Rect(300, 300, 30, 30); var big = new Rect(360, 280, 90, 90);
        foreach (var g in new[] { small, big })
        {
            page.Rectangle(g, Scalar.White, -1);
            mask.Rectangle(g, Scalar.White, -1);
        }
        DilateLikeDetector(mask);
        var block = Union(small, big);

        Assert.Equal([block], BubbleSplitter.SplitOffGiants(page, mask, [block]));
    }

    [Fact]
    public void SplitOffGiants_OrdinaryTextAlsoLeftOutsideTheBubble_DoesNotSplit()
    {
        // 풍선 밖에 거대한 획과 함께 보통 크기의 글도 있다(풍선 밖으로 이어진 대사): 가르면 그 글은 대사 블록(풍선 안)에도 효과음 블록에도 들지 못해
        // 읽히지도 번역되지도 지워지지도 않는다(검토 2026-11-27) — 그럴 때는 가르지 않는다
        var (page, mask, glyphs, stroke) = BubbleWithAGiantStrokeBelow();
        using (page) using (mask)
        {
            using var loose = EmptyMask(page);
            var stray = DrawGlyphs(page, loose, 60, 560, 6, 3, glyph: 14, gap: 8);   // 풍선 아래 왼쪽, 톤 위
            DilateLikeDetector(loose);
            Cv2.BitwiseOr(mask, loose, mask);
            var all = glyphs.Union(stroke).Union(stray);
            var block = new DetectedBlock(all.X - 4, all.Y - 4, all.Right + 4, all.Bottom + 4, 0.9f, true);

            Assert.Equal([block], BubbleSplitter.SplitOffGiants(page, mask, [block]));
        }
    }

    [Fact]
    public void SplitOffGiants_KeepsTheOtherBlocks_InOrder()
    {
        var (page, mask, glyphs, stroke) = BubbleWithAGiantStrokeBelow();
        using (page) using (mask)
        {
            var before = new DetectedBlock(700, 40, 760, 120, 0.8f, true);
            var after = new DetectedBlock(40, 900, 120, 1000, 0.8f, true);

            var parts = BubbleSplitter.SplitOffGiants(page, mask, [before, Union(glyphs, stroke), after]);

            Assert.Equal(4, parts.Count);
            Assert.Equal(before, parts[0]);
            Assert.Equal(after, parts[3]);
            Assert.False(parts[1].HandLettered);
            Assert.True(parts[2].HandLettered);
        }
    }

    // ── 지우지 않을 블록의 획 지키기 ──

    [Fact]
    public void ProtectStrokes_ClearsTheStrokesOfBlocksThatAreNotErased()
    {
        // 그대로 두는 효과음(SfxMode=Keep)·읽지 못한 블록의 획이 지울 마스크에 남아 있으면, 옆 블록의 창(1.15배)에 걸친 만큼 LaMa로 같이 지워진다
        using var strokes = new Mat(300, 400, MatType.CV_8UC1, Scalar.Black);
        var kept = new Rect(40, 40, 100, 80); var dropped = new Rect(150, 40, 100, 200);
        strokes.Rectangle(new Rect(50, 50, 80, 60), Scalar.White, -1);
        strokes.Rectangle(new Rect(160, 50, 80, 180), Scalar.White, -1);

        EmbeddedTranslator.ProtectStrokes(strokes, keptAsIs: [dropped], erased: [kept]);

        using (var inDropped = new Mat(strokes, dropped)) Assert.Equal(0, Cv2.CountNonZero(inDropped));
        Assert.Equal(255, strokes.At<byte>(80, 90));   // 지울 블록의 획은 그대로
    }

    [Fact]
    public void ProtectStrokes_WhereRectsOverlap_TheErasedBlockKeepsItsOwnRect()
    {
        using var strokes = new Mat(300, 400, MatType.CV_8UC1, Scalar.White);
        var kept = new Rect(40, 40, 100, 80); var dropped = new Rect(120, 60, 100, 200);   // x 120~140, y 60~120이 겹친다

        EmbeddedTranslator.ProtectStrokes(strokes, keptAsIs: [dropped], erased: [kept]);

        Assert.Equal(255, strokes.At<byte>(90, 130));   // 겹친 자리는 지울 블록의 것
        Assert.Equal(0, strokes.At<byte>(200, 170));    // 그 밖의 '두는 블록' 자리는 비웠다
        Assert.Equal(255, strokes.At<byte>(280, 380));  // 어느 블록에도 안 든 획은 건드리지 않는다
    }
}
