using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>감지기가 놓친 글 획 더하기(<see cref="BubbleSolver.AddMissedStrokes"/>) — 2026-11-27 사용자 "이런 부분은 잘 지워지지도 않았어":
/// 26쪽의 줄무늬 풍선에서 칸 테두리에 잘린 줄의 아래 글자 셋과 가운데 줄의 글자 하나가 감지기 마스크에 없어 그대로 남았다.
/// 글 상자 안이 깨끗할 때만(굵고 어두운 것이 전부 글자답다) 마스크에 닿은 글자 꼴 성분을 더한다 — 그림이 섞인 상자는 손대지 않는다.</summary>
public class MissedStrokesTests
{
    /// <summary>가로 줄무늬(1px 검은 줄, pitch px 간격) — 줄무늬 풍선·줄무늬 톤의 대역.</summary>
    private static void Stripes(Mat page, int pitch = 4)
    {
        int rows = page.Rows, right = page.Cols - 1;
        for (int y = 0; y < rows; y += pitch) Cv2.Line(page, new Point(0, y), new Point(right, y), Scalar.Black, 1);
    }

    /// <summary>세로 한 줄(글자 n개)을 그리고 위의 masked개만 마스크에 넣는다 — 감지기가 줄의 아래쪽을 놓친 꼴.</summary>
    private static (Rect All, List<Rect> Cells) Column(Mat page, Mat mask, int x, int y, int n, int masked, int glyph = 14, int gap = 4, int ink = 0)
    {
        var cells = new List<Rect>();
        for (int i = 0; i < n; i++)
        {
            var cell = new Rect(x, y + i * (glyph + gap), glyph, glyph);
            page.Rectangle(cell, new Scalar(ink, ink, ink), -1);
            if (i < masked) mask.Rectangle(cell, Scalar.White, -1);
            cells.Add(cell);
        }
        return (new Rect(x, y, glyph, n * glyph + (n - 1) * gap), cells);
    }

    private static int Changed(Mat before, Mat after)
    {
        using var diff = new Mat();
        Cv2.Absdiff(before, after, diff);
        return Cv2.CountNonZero(diff);
    }

    [Fact]
    public void AddMissedStrokes_GlyphsRightBelowTheMaskedOnes_AreAdded()
    {
        using var page = SolidPage(300, 300);
        Stripes(page);
        using var mask = EmptyMask(page);
        var (all, cells) = Column(page, mask, 140, 60, n: 6, masked: 3);
        DilateLikeDetector(mask);
        var block = new Rect(all.X - 6, all.Y - 6, all.Width + 12, all.Height + 12);

        int added = BubbleSolver.AddMissedStrokes(page, mask, block);

        Assert.True(added > 0);
        foreach (var c in cells) Assert.Equal(255, mask.At<byte>(c.Y + c.Height / 2, c.X + c.Width / 2));
        // 더한 획은 감지기 마스크처럼 3px 넓혀 넣는다(가장자리의 흐린 픽셀까지) — 그보다 멀리는 아니다
        Assert.Equal(255, mask.At<byte>(cells[5].Bottom + 1, cells[5].X + 7));
        Assert.Equal(0, mask.At<byte>(cells[5].Bottom + 5, cells[5].X + 7));
    }

    [Fact]
    public void AddMissedStrokes_ColumnCutByThePanelEdge_AddsTheCutGlyphs_AndLeavesThePanelEdge()
    {
        // 칸 테두리(굵은 검은 세로 줄)에 왼쪽이 잘린 줄 — 글자와 테두리가 한 덩어리로 붙어 있다. 감지기 상자는 테두리 바로 오른쪽에서 시작하고, 마스크는 그 변에 닿아 있다
        using var page = SolidPage(300, 300);
        Stripes(page);
        using var mask = EmptyMask(page);
        var (all, cells) = Column(page, mask, 100, 60, n: 6, masked: 3);
        page.Rectangle(new Rect(90, 0, 15, 300), Scalar.Black, -1);   // 테두리 x 90..104 — 글자(x 100..113)의 왼쪽 5px를 덮는다
        DilateLikeDetector(mask);
        mask.Rectangle(new Rect(0, 0, 105, 300), Scalar.Black, -1);   // 감지기 마스크도 테두리에서 끊긴다
        var block = new Rect(105, all.Y - 6, 40, all.Height + 12);

        int added = BubbleSolver.AddMissedStrokes(page, mask, block);

        Assert.True(added > 0);
        foreach (var c in cells) Assert.Equal(255, mask.At<byte>(c.Y + c.Height / 2, 110));
        using var outside = new Mat(mask, new Rect(0, 0, 105, 300));
        Assert.Equal(0, Cv2.CountNonZero(outside));   // 상자 밖(칸 테두리)은 마스크에 넣지 않는다
    }

    [Fact]
    public void AddMissedStrokes_ThickLineAcrossTheBoxCorner_AddsNothing_EvenTheRealMissedGlyph()
    {
        // 손글씨처럼 마스크가 상자의 네 변에 다 닿은 블록: 모서리를 가로지르는 굵은 선(풍선 테두리)은 두 변에서 잘린다 — 칸에 잘린 글자는 한 변에서만 잘린다.
        // 상자 안에 글 아닌 것이 있으면 어느 것이 글인지 가릴 수 없다: 테두리 조각을 지우느니 놓친 글자를 남긴다(실제 05쪽 손글씨 블록에서 테두리 호 둘이 글로 잡혔다)
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var block = new Rect(100, 100, 80, 80);
        foreach (var g in new[] { new Rect(103, 103, 20, 20), new Rect(157, 157, 20, 20) })   // 왼쪽 위·오른쪽 아래의 글자 — 마스크가 네 변에 닿는다
        {
            page.Rectangle(g, Scalar.Black, -1);
            mask.Rectangle(g, Scalar.White, -1);
        }
        DilateLikeDetector(mask);
        var missed = new Rect(103, 127, 20, 20);   // 왼쪽 위 글자 바로 아래(4px) — 마스크에 없다
        page.Rectangle(missed, Scalar.Black, -1);
        Cv2.Line(page, new Point(162, 92), new Point(188, 118), Scalar.Black, 14);   // 오른쪽 위 모서리를 가로지르는 굵은 선
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_ArtInsideTheBox_AddsNothing()
    {
        // 그림 위의 글: 상자 아래로 먹칠이 들어와 있다 — 상자 안의 굵고 어두운 것이 전부 글이라고 볼 수 없다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 140, 60, n: 6, masked: 3);
        DilateLikeDetector(mask);
        var block = new Rect(all.X - 6, all.Y - 6, all.Width + 12, all.Height + 12);
        page.Rectangle(new Rect(0, block.Bottom - 10, 300, 60), Scalar.Black, -1);
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_PieceCutByASideTheMaskDoesNotReach_AddsNothing()
    {
        // 옆 블록의 글자가 상자의 오른쪽 변에 걸쳐 들어와 있다 — 이 블록의 마스크는 그 변에 닿지 않는다. 칸에 잘린 제 글자가 아니다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 140, 60, n: 6, masked: 4);   // 놓친 글자 둘 + 걸친 조각 하나 = 후보 셋(개수 규칙에는 안 걸린다)
        DilateLikeDetector(mask);
        var block = new Rect(all.X - 6, all.Y - 6, all.Width + 30, all.Height + 12);   // x 134..183
        page.Rectangle(new Rect(block.Right - 6, 80, 14, 14), Scalar.Black, -1);       // 오른쪽 변에 걸친 글자 꼴(상자 안 6px)
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_ThinLineCutByTheMaskSide_AddsNothing()
    {
        // 마스크가 닿은 변(칸 테두리 쪽)에서 들어온 가는 선 — 글 획(14px)보다 한참 가늘다(4px). 테두리에서 뻗은 그림 선이다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 100, 60, n: 6, masked: 4);
        page.Rectangle(new Rect(90, 0, 15, 300), Scalar.Black, -1);
        DilateLikeDetector(mask);
        mask.Rectangle(new Rect(0, 0, 105, 300), Scalar.Black, -1);
        var block = new Rect(105, all.Y - 30, 40, all.Height + 36);
        page.Rectangle(new Rect(90, all.Y - 22, 28, 4), Scalar.Black, -1);   // 테두리에서 상자 안으로 13px 들어온 4px 굵기의 선
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_LongStrokeInsideTheBox_AddsNothing()
    {
        // 상자 안을 지나는 긴 선(머리카락 한 가닥, 변에는 닿지 않는다) — 글자보다 길다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 140, 60, n: 6, masked: 4);
        DilateLikeDetector(mask);
        var block = new Rect(all.X - 6, all.Y - 6, all.Width + 40, all.Height + 12);
        Cv2.Line(page, new Point(all.Right + 14, all.Y + 4), new Point(all.Right + 22, all.Y + 64), Scalar.Black, 5);
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_SolidBlobAmongThinStrokedGlyphs_AddsNothing()
    {
        // 획이 가는 글자(3px 굵기의 빈 네모) 사이의 꽉 찬 덩어리 — 크기는 글자만 하지만 획보다 한참 굵다. 먹 덩어리(그림)다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var cells = new List<Rect>();
        for (int i = 0; i < 6; i++)
        {
            var cell = new Rect(140, 60 + i * 18, 14, 14);
            page.Rectangle(new Rect(cell.X + 1, cell.Y + 1, 12, 12), Scalar.Black, 3);   // 선이 가운데에서 1px씩 번져 14×14를 채운다
            if (i < 4) mask.Rectangle(cell, Scalar.White, -1);
            cells.Add(cell);
        }
        DilateLikeDetector(mask);
        var block = new Rect(134, 54, 60, 116);
        page.Rectangle(new Rect(170, 70, 14, 14), Scalar.Black, -1);
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_ThinStrokedGlyphsBelowTheMaskedOnes_AreAdded()
    {
        // 위의 대조: 덩어리가 없으면 같은 가는 획의 놓친 글자는 더한다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var cells = new List<Rect>();
        for (int i = 0; i < 6; i++)
        {
            var cell = new Rect(140, 60 + i * 18, 14, 14);
            page.Rectangle(new Rect(cell.X + 1, cell.Y + 1, 12, 12), Scalar.Black, 3);
            if (i < 4) mask.Rectangle(cell, Scalar.White, -1);
            cells.Add(cell);
        }
        DilateLikeDetector(mask);

        Assert.True(BubbleSolver.AddMissedStrokes(page, mask, new Rect(134, 54, 60, 116)) > 0);

        foreach (var c in cells) Assert.Equal(255, mask.At<byte>(c.Y + 1, c.X + 7));   // 위쪽 획 위
    }

    /// <summary>굵은 글자 둘이 붙은 한 덩어리(획 굵기 22px)를 상자에 꽉 차게 그리고, 마스크는 획의 속(양쪽으로 inset px 안)만 덮는다 — 감지기가 굵은 글자를 덮는 꼴.</summary>
    private static (Rect Block, Rect[] Strokes) BoldGlyphs(Mat page, Mat mask, int inset)
    {
        var block = new Rect(120, 80, 64, 128);
        var strokes = new[] { new Rect(120, 80, 64, 22), new Rect(120, 80, 22, 128), new Rect(120, 186, 64, 22), new Rect(142, 130, 42, 22) };
        foreach (var r in strokes) page.Rectangle(r, Scalar.Black, -1);
        foreach (var r in strokes) mask.Rectangle(new Rect(r.X + inset, r.Y + inset, r.Width - 2 * inset, r.Height - 2 * inset), Scalar.White, -1);
        return (block, strokes);
    }

    [Fact]
    public void AddMissedStrokes_BoldGlyphsTheMaskOnlyHalfCovers_AreCompleted()
    {
        // 굵은 글자는 감지기 마스크가 획의 속만 덮는다(실제 26쪽 "だめ" — 두 글자가 붙은 한 덩어리, 덮인 비율 0.61). 온전히 덮인 글자가 하나도 없어 '기준이 없다'고 그만두면
        // 마스크 밖의 획이 구멍을 둘러싸고 남아, 메울 때 그 먹이 구멍 안으로 번진다(글자 자리가 먹 덩어리가 된다 — 2026-11-27)
        using var page = SolidPage(300, 300);
        Stripes(page, 6);
        using var mask = EmptyMask(page);
        var (block, strokes) = BoldGlyphs(page, mask, inset: 4);   // 획 22px 가운데 14px — 넓이의 55%쯤

        int added = BubbleSolver.AddMissedStrokes(page, mask, block);

        Assert.True(added > 0);
        foreach (var r in strokes)
        {
            using var part = new Mat(mask, r);
            Assert.Equal(r.Width * r.Height, Cv2.CountNonZero(part));   // 획이 통째로 지울 자리에 들었다
        }
    }

    [Fact]
    public void AddMissedStrokes_NoGlyphIsFullyCovered_AndTheMaskBarelyTouchesTheDarkShape_AddsNothing()
    {
        // 온전히 덮인 글자가 없는데 마스크가 어두운 것의 가장자리만 스친다(획의 20%쯤): 글자의 속을 덮은 것이 아니다 — 먹 바탕·그림일 수 있으니 손대지 않는다
        using var page = SolidPage(300, 300);
        using var mask = EmptyMask(page);
        var (block, _) = BoldGlyphs(page, mask, inset: 9);   // 획 22px 가운데 4px
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));
        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_ToneDotsAllOverTheBox_AddsNothing()
    {
        // 굵은 망점 톤 위의 글: 점 하나하나는 작아 글자 꼴이지만 상자에 그런 것이 글자 수보다 훨씬 많다 — 톤이다. 마스크에 닿은 점을 글로 알고 구멍을 넓히지 않는다
        using var page = SolidPage(300, 300);
        for (int y = 6; y < 300; y += 12)
            for (int x = 6; x < 300; x += 12)
                Cv2.Circle(page, new Point(x, y), 3, Scalar.Black, -1);
        using var mask = EmptyMask(page);
        page.Rectangle(new Rect(142, 61, 16, 6 * 14 + 5 * 4 + 2), Scalar.White, -1);   // 글 자리의 점은 걷는다 — 글자가 점과 붙어 한 덩어리가 되지 않게(기준이 될 글자가 온전해야 한다)
        Column(page, mask, 143, 62, n: 6, masked: 6);
        DilateLikeDetector(mask);
        var block = new Rect(120, 48, 60, 132);   // 변이 점 사이(12의 배수)를 지나 점이 변에 잘리지 않는다 — 잘린 그림이 아니라 개수로 걸러지는지 본다
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, block));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_EverythingAlreadyMasked_ChangesNothing()
    {
        using var page = SolidPage(300, 300);
        Stripes(page);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 140, 60, n: 6, masked: 6);
        DilateLikeDetector(mask);
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, new Rect(all.X - 6, all.Y - 6, all.Width + 12, all.Height + 12)));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_WhiteTextOnBlack_ChangesNothing()
    {
        // 먹칠 위의 흰 글: 어두운 것은 바탕이다 — 바탕 덩어리가 상자의 변에 닿아 글 아닌 것으로 걸린다
        using var page = SolidPage(300, 300, 0);
        using var mask = EmptyMask(page);
        var (all, _) = Column(page, mask, 140, 60, n: 6, masked: 3, ink: 255);
        DilateLikeDetector(mask);
        using var before = mask.Clone();

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, new Rect(all.X - 6, all.Y - 6, all.Width + 12, all.Height + 12)));

        Assert.Equal(0, Changed(before, mask));
    }

    [Fact]
    public void AddMissedStrokes_EmptyMaskOrBoxOutsideThePage_DoesNothing()
    {
        using var page = SolidPage(200, 200);
        page.Rectangle(new Rect(80, 80, 14, 14), Scalar.Black, -1);
        using var mask = EmptyMask(page);

        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, new Rect(60, 60, 60, 60)));    // 마스크가 비었다 — 기준이 될 글자가 없다
        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, new Rect(190, 190, 40, 40)));  // 쪽에 걸친 상자
        Assert.Equal(0, BubbleSolver.AddMissedStrokes(page, mask, new Rect(400, 400, 40, 40)));  // 쪽 밖
        Assert.Equal(0, Cv2.CountNonZero(mask));
    }
}
