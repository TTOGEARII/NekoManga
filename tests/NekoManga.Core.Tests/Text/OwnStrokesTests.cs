using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>어느 지울 블록의 것도 아닌 획(BubbleSolver.DropUnownedStrokes) — 감지기가 마스크에만 넣고 상자로는 안 잡은 효과음이
/// 대사 블록의 지우기 창(1.15배)·합친 블록의 빈 모서리에 걸린 만큼 지워지던 것(2026-11-27, 실제 05쪽 "ちゅ").</summary>
public class OwnStrokesTests
{
    private static Mat Mask(int width, int height, params Rect[] strokes)
    {
        var m = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        foreach (var s in strokes) m.Rectangle(s, Scalar.White, -1);
        return m;
    }

    private static bool Filled(Mat mask, Rect r)
    {
        using var part = new Mat(mask, r);
        return Cv2.CountNonZero(part) == r.Width * r.Height;
    }

    private static bool Empty(Mat mask, Rect r)
    {
        using var part = new Mat(mask, r);
        return Cv2.CountNonZero(part) == 0;
    }

    [Fact]
    public void DropUnownedStrokes_StrokeInTheEmptyCornerOfAMergedBlock_IsDropped()
    {
        // 엇갈린 두 기둥을 합친 블록(ㄱ자): 감싼 상자의 빈 모서리에 상자 밖으로 삐져나가는 획이 있다 — 어느 기둥의 글도 아니다(손글씨 효과음).
        // 감싼 상자로 모으면 상자에 걸린 윗부분만 지워져 반 토막이 남는다
        var right = new Rect(300, 100, 60, 200); var left = new Rect(200, 160, 60, 240);
        var glyphsRight = new Rect(310, 110, 40, 180); var glyphsLeft = new Rect(210, 170, 40, 220);
        var sfx = new Rect(290, 330, 60, 110);   // 감싼 상자(200~360 × 100~400)의 오른쪽 아래 모서리, 아래로 40px 나간다
        using var strokes = Mask(600, 600, glyphsRight, glyphsLeft, sfx);

        int dropped = BubbleSolver.DropUnownedStrokes(strokes, [[right, left]]);

        Assert.Equal(sfx.Width * sfx.Height, dropped);
        Assert.True(Empty(strokes, sfx));
        Assert.True(Filled(strokes, glyphsRight) && Filled(strokes, glyphsLeft));
    }

    [Fact]
    public void DropUnownedStrokes_SmallMarkJustOutsideTheBox_ButInsideItsWindow_Stays()
    {
        // 감지기 상자가 놓친 문장 부호(상자 4px 밖, 1.15배 창 안): 전처럼 그 블록이 지운다
        var block = new Rect(200, 200, 100, 200);
        var glyphs = new Rect(210, 210, 80, 180); var mark = new Rect(240, 404, 8, 8);   // 창의 아래 끝 = 400 + 15
        using var strokes = Mask(600, 600, glyphs, mark);

        Assert.Equal(0, BubbleSolver.DropUnownedStrokes(strokes, [[block]]));
        Assert.True(Filled(strokes, mark));
    }

    [Fact]
    public void DropUnownedStrokes_BigStrokeReachingIntoTheWindowFromOutside_IsDropped()
    {
        // 옆의 큰 획이 창에 2px 걸쳤다: 대부분이 상자 밖이고 창 밖으로 이어진다 — 걸친 2px만 지우면 획의 옆구리가 패인다
        var block = new Rect(200, 200, 100, 200);
        var glyphs = new Rect(210, 210, 80, 180); var big = new Rect(305, 250, 115, 80);   // 창의 오른쪽 끝 = 300 + 7
        using var strokes = Mask(600, 600, glyphs, big);

        Assert.Equal(big.Width * big.Height, BubbleSolver.DropUnownedStrokes(strokes, [[block]]));
        Assert.True(Empty(strokes, big));
        Assert.True(Filled(strokes, glyphs));
    }

    [Fact]
    public void DropUnownedStrokes_GlyphOfTheBlockStickingOutOfItsWindow_Stays()
    {
        // 블록의 글자가 창 밖으로 삐져나갔다(감지기 상자가 빡빡했다): 대부분이 상자 안이면 그 블록의 글이다
        var block = new Rect(200, 200, 100, 200);
        var tall = new Rect(240, 170, 20, 90);   // 상자 위로 30px(창은 15px) — 상자 안 60/90
        using var strokes = Mask(600, 600, tall);

        Assert.Equal(0, BubbleSolver.DropUnownedStrokes(strokes, [[block]]));
        Assert.True(Filled(strokes, tall));
    }

    [Fact]
    public void DropUnownedStrokes_StrokeOfAnotherErasedBlock_Stays()
    {
        // 다른 지울 블록의 글은 그 블록의 것이다 — 어느 블록의 창에 걸쳤든 남는다
        var a = new Rect(200, 200, 100, 200); var b = new Rect(304, 200, 100, 200);
        var glyphsA = new Rect(210, 210, 80, 180); var glyphsB = new Rect(306, 210, 96, 180);   // B의 글이 A의 창(307까지)에 걸친다
        using var strokes = Mask(600, 600, glyphsA, glyphsB);

        Assert.Equal(0, BubbleSolver.DropUnownedStrokes(strokes, [[a], [b]]));
        Assert.True(Filled(strokes, glyphsA) && Filled(strokes, glyphsB));
    }

    [Fact]
    public void DropUnownedStrokes_BlobJoiningTheBlocksGlyphsToABigOutsideStroke_LosesOnlyTheOutsidePart()
    {
        // 대사의 획과 옆의 큰 획(효과음)이 마스크에서 한 덩어리로 붙었다: 덩어리의 대부분이 상자 밖이어도 상자 안의 대사는 지울 획에 남는다 — 통째로 버리면 대사가 번역문 밑에 그대로 남는다.
        // 밖으로 이어진 효과음 쪽만 빠진다(전에는 창에 걸린 만큼 지워졌다)
        var block = new Rect(200, 200, 100, 200);
        var glyphs = new Rect(210, 210, 80, 180);
        var bridge = new Rect(290, 280, 30, 10);      // 대사에서 효과음으로 이어진 마스크
        var big = new Rect(320, 180, 160, 260);       // 상자 밖의 큰 획(덩어리의 2/3쯤)
        using var strokes = Mask(600, 600, glyphs, bridge, big);

        int dropped = BubbleSolver.DropUnownedStrokes(strokes, [[block]]);

        Assert.True(Filled(strokes, glyphs));
        Assert.True(Empty(strokes, big));
        Assert.Equal(big.Width * big.Height + 20 * 10, dropped);   // 큰 획 + 이음매의 상자 밖 부분(300~320)
    }

    [Fact]
    public void OwnBoxes_BlockMergedFromTwoDetectorBoxes_IsThoseBoxes()
    {
        var right = new Rect(300, 100, 60, 200); var left = new Rect(200, 160, 60, 240);
        var merged = new Rect(196, 96, 168, 308);   // 합친 블록은 조각의 마스크로 다시 재서 감지기 상자와 몇 px 다르다
        var elsewhere = new Rect(600, 100, 80, 200);

        Assert.Equal([right, left], EmbeddedTranslator.OwnBoxes(merged, [right, elsewhere, left]));
    }

    [Theory]
    [InlineData("one-box")]        // 감지기 상자 하나에서 나온 블록(대부분) — 상자와 몇 px 달라도 블록 자신
    [InlineData("piece-of-a-box")] // 큰 감지기 상자를 풍선별로 나눈 조각 — 그 상자는 이 블록 안에 들지 않는다
    [InlineData("no-box")]
    public void OwnBoxes_OtherBlocks_AreThemselves(string layout)
    {
        var block = new Rect(200, 100, 100, 300);
        IReadOnlyList<Rect> detected = layout switch
        {
            "one-box" => [new Rect(204, 105, 92, 310)],
            "piece-of-a-box" => [new Rect(180, 90, 400, 330), new Rect(100, 250, 300, 400)],   // 둘 다 이 블록에 걸치지만 어느 쪽도 절반 넘게 들지는 않는다
            _ => [],
        };

        Assert.Equal([block], EmbeddedTranslator.OwnBoxes(block, detected));
    }

    [Fact]
    public void DropUnownedStrokes_NoErasedBlocks_DropsNothing()
    {
        using var strokes = Mask(300, 300, new Rect(100, 100, 40, 40));

        Assert.Equal(0, BubbleSolver.DropUnownedStrokes(strokes, []));
        Assert.Equal(1600, Cv2.CountNonZero(strokes));
    }

    [Fact]
    public void PrepareStrokes_WithTheMergedBlocksParts_LeavesAnUnboxedStrokeInItsEmptyCornerAlone()
    {
        // 쪽 단위로 잇는 자리: 합친 블록은 감싼 상자가 아니라 합치기 전 조각들로 '제 글'을 가린다
        using var page = SolidPage(600, 600);
        var right = new Rect(300, 100, 60, 200); var left = new Rect(200, 160, 60, 240);
        var merged = new Rect(200, 100, 160, 300);
        var glyphsRight = new Rect(310, 110, 40, 180); var glyphsLeft = new Rect(210, 170, 40, 220);
        var sfx = new Rect(290, 330, 60, 110);
        foreach (var r in new[] { glyphsRight, glyphsLeft, sfx }) page.Rectangle(r, Scalar.Black, -1);
        using var mask = Mask(600, 600, glyphsRight, glyphsLeft, sfx);

        using var strokes = EmbeddedTranslator.PrepareStrokes(page, mask, [], [merged], [[right, left]]);

        Assert.True(Empty(strokes, sfx), "빈 모서리의 효과음 획이 지울 획에 남았다");
        Assert.True(Filled(strokes, glyphsRight) && Filled(strokes, glyphsLeft));
        Assert.True(Filled(mask, sfx));   // 감지기 마스크는 그대로다
    }

    [Fact]
    public void PrepareStrokes_KeptBlockInsideAnErasedBlocksWindow_IsStillCleared()
    {
        // 그대로 두기로 한 블록(못 옮긴 효과음)이 지울 블록의 창(1.15배) 안에 통째로 들어 있다: '창 안에 통째로 든 조각은 그 블록이 지운다'에 걸리지 않게,
        // 그대로 둘 블록의 획은 먼저 비운다(ProtectStrokes) — 비우지 않으면 대사를 지울 때 같이 지워진다
        using var page = SolidPage(600, 600);
        var erased = new Rect(200, 100, 100, 300);
        var glyphs = new Rect(210, 110, 80, 280);
        var keptGlyph = new Rect(240, 406, 10, 10);   // 지울 블록 아래 6px — 창의 아래 끝은 422
        var kept = new Rect(236, 402, 18, 18);
        foreach (var r in new[] { glyphs, keptGlyph }) page.Rectangle(r, Scalar.Black, -1);
        using var mask = Mask(600, 600, glyphs, keptGlyph);

        using var strokes = EmbeddedTranslator.PrepareStrokes(page, mask, [kept], [erased]);

        Assert.True(Empty(strokes, kept));
        Assert.True(Filled(strokes, glyphs));
    }

    [Fact]
    public void PrepareStrokes_SfxReachingIntoOneOfTheMergedBoxes_IsNotAddedBackAsAMissedStroke()
    {
        // 빈 모서리의 효과음이 오른쪽 기둥의 상자에 8px 걸쳤다: 상자 밖 부분은 남의 획이라 비운다. 그런데 '놓친 획 더하기'를 감싼 상자로 돌리면
        // 상자에 걸친 조각에 닿은 '덮이지 않은 글자 꼴 성분'으로 읽어 효과음을 통째로 다시 더한다(검토 2026-11-27) — 놓친 획도 제 글의 상자 안에서만 찾는다
        using var page = SolidPage(600, 600);
        var right = new Rect(300, 100, 60, 200); var left = new Rect(200, 160, 60, 240);
        var merged = new Rect(200, 100, 160, 300);
        var glyphsRight = new Rect(310, 110, 40, 170); var glyphsLeft = new Rect(210, 170, 40, 220);
        var sfx = new Rect(310, 292, 40, 60);   // 위의 8px이 오른쪽 상자(아래 끝 300) 안
        foreach (var r in new[] { glyphsRight, glyphsLeft, sfx }) page.Rectangle(r, Scalar.Black, -1);
        using var mask = Mask(600, 600, glyphsRight, glyphsLeft, sfx);

        using var strokes = EmbeddedTranslator.PrepareStrokes(page, mask, [], [merged], [[right, left]]);

        Assert.True(Empty(strokes, new Rect(sfx.X - 4, 300, sfx.Width + 8, sfx.Bottom - 300 + 4)), "상자 밖의 효과음 획이 지울 획에 들어 있다");
        Assert.True(Filled(strokes, glyphsRight) && Filled(strokes, glyphsLeft));
    }
}
