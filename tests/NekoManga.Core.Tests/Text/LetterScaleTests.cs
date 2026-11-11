using System.Drawing;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;
using Rect = OpenCvSharp.Rect;

namespace NekoManga.Core.Tests;

/// <summary>블록 좌표(원본 기준) ↔ 식자할 그림 좌표의 축별 비율(LetterScale) — 최종 식자와 결과 창 편집 미리보기가 같이 쓴다.
/// 채색한 쪽은 가로·세로 비율이 조금 다르다: 여기서는 원본 1000×1500 → 채색 832×1240(0.832 · 0.8267)을 쓴다.</summary>
public class LetterScaleTests
{
    private static AppSettings Arial() => new() { LetterFontFamily = "Arial" };

    private static TextBlockInfo Manual(int x1, int y1, int x2, int y2, string text) =>
        new(x1, y1, x2, y2, "", text, 20, false, "#000000", ManualRect: true);

    // ───────── 비율 정하기 (Resolve) ─────────

    [Fact]
    public void Resolve_StoredSourceSize_GivesPerAxisRatiosAndIgnoresReferenceFile()
    {
        // 박제한 원본 크기가 있으면 그것으로 — 그 자리의 원본이 다른 해상도로 바뀌었어도(블록 좌표는 번역할 때의 원본 기준) 읽지 않는다
        using var t = new TempDir();
        using var replaced = SolidPage(2000, 3000);
        SavePng(replaced, t.At("원본.png"));

        var s = LetterScale.Resolve(832, 1240, 1000, 1500, t.At("원본.png"));

        Assert.Equal(0.832, s.X, 9);
        Assert.Equal(1240 / 1500.0, s.Y, 9);
        Assert.NotEqual(s.X, s.Y);
    }

    [Theory]
    [InlineData(0, 0)]        // 원본 크기 없이 부른 호출(파이프라인의 첫 식자)·손으로 만든 사이드카 — 앱이 쓴 사이드카는 v0.6.0부터 늘 크기가 있다
    [InlineData(1000, 0)]     // 한 축만 있는 것도 모르는 것으로 본다
    [InlineData(0, 1500)]
    public void Resolve_NoStoredSourceSize_UsesReferenceFileSizePerAxis(int srcW, int srcH)
    {
        using var t = new TempDir();
        using var source = SolidPage(1000, 1500);
        SavePng(source, t.At("원고 フォルダ", "원본.png"));

        var s = LetterScale.Resolve(832, 1240, srcW, srcH, t.At("원고 フォルダ", "원본.png"));

        Assert.Equal(0.832, s.X, 9);
        Assert.Equal(1240 / 1500.0, s.Y, 9);
    }

    [Theory]
    [InlineData("missing")]      // 원고 폴더를 옮겼거나 원본을 지웠다
    [InlineData("unreadable")]   // 그 자리에 그림이 아닌 파일
    [InlineData("empty")]        // 0바이트
    [InlineData("null")]         // 참조 경로 없이 부름
    public void Resolve_NoStoredSizeAndNoReadableSource_KeepsBlockCoordinates(string reference)
    {
        using var t = new TempDir();
        string? path = reference == "null" ? null : t.At("원본.png");
        if (reference == "unreadable") File.WriteAllText(path!, "이건 그림이 아니다");
        if (reference == "empty") File.WriteAllBytes(path!, []);

        Assert.Equal(LetterScale.One, LetterScale.Resolve(832, 1240, 0, 0, path));
    }

    // ───────── 그림 좌표 → 블록 좌표 (ToSource) ─────────

    [Theory]
    [InlineData(100, 80, 400, 220)]       // 쪽 위
    [InlineData(600, 1300, 900, 1440)]    // 쪽 아래 — 가로 비율 하나로 되돌리면 세로가 1292–1431이 된다
    [InlineData(0, 0, 1000, 1500)]
    [InlineData(333, 777, 334, 778)]
    public void ToSource_RectConvertedWithSameScale_ReturnsOriginalBlock(int x1, int y1, int x2, int y2)
    {
        // ToSource만 본다: 블록을 비율대로 그림 좌표로 바꾼 사각형을 같은 비율로 되돌리면 원래 블록이 나온다(축마다 자기 비율).
        // 결과 창이 상자를 놓을 때의 전체 경로(PlanLayout이 띄운 상자 → CommitBox → 다시 띄운 상자)는 아래 CommitBox_* 시험이 본다.
        var s = LetterScale.Resolve(832, 1240, 1000, 1500);

        var back = s.ToSource(x1 * s.X, y1 * s.Y, (x2 - x1) * s.X, (y2 - y1) * s.Y);

        Assert.Equal((x1, y1, x2, y2), back);
    }

    [Fact]
    public void ToSource_UsesEachAxisOwnRatio()
    {
        var s = new LetterScale(2.0, 0.5);

        Assert.Equal((100, 100, 300, 200), s.ToSource(200, 50, 400, 50));
    }

    [Fact]
    public void ToSource_MovedBox_KeepsItsSize()
    {
        // 왼쪽 위와 크기를 따로 반올림한다 — 옮기기만 한 상자의 폭·높이가 자리에 따라 1px씩 달라지지 않게
        var s = LetterScale.Resolve(832, 1240, 1000, 1500);
        var sizes = Enumerable.Range(0, 40)
            .Select(i => s.ToSource(100 + i * 0.37, 900 + i * 0.41, 249.6, 115.7))
            .Select(b => (b.X2 - b.X1, b.Y2 - b.Y1))
            .Distinct().ToList();

        Assert.Equal((300, 140), Assert.Single(sizes));
    }

    [Fact]
    public void ToSource_UnknownScale_KeepsCoordinates()
    {
        Assert.Equal((10, 20, 40, 60), default(LetterScale).ToSource(10, 20, 30, 40));
    }

    // ───────── 편집 상자 놓기 (CommitBox) ─────────
    //
    // 결과 창은 PlanLayout이 돌려준 글 자리를 편집 상자로 띄우고(RebuildLetterBoxes), 상자를 놓으면 CommitBox가 돌려준 사각형을 ManualRect 블록으로 저장한다.
    // 아래 시험은 그 경로 그대로다: PlanLayout → (옮김) → CommitBox → 블록 저장 → PlanLayout.

    private static LetterBox BoxOf(RectangleF r) => new(r.X, r.Y, r.Width, r.Height);

    private static TextBlockInfo Stored(TextBlockInfo b, (int X1, int Y1, int X2, int Y2) r) =>
        b with { X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2, ManualRect = true };

    /// <summary>네 변이 모두 tolerance(px) 안에서 같은가.</summary>
    private static void AssertSameBox(LetterBox expected, RectangleF actual, double tolerance, string what)
    {
        double worst = new[]
        {
            Math.Abs(actual.Left - expected.Left), Math.Abs(actual.Top - expected.Top),
            Math.Abs(actual.Right - (expected.Left + expected.Width)), Math.Abs(actual.Bottom - (expected.Top + expected.Height)),
        }.Max();
        Assert.True(worst <= tolerance, $"{what}: 놓은 자리 {expected} ≠ 다시 띄운 자리 {actual} (가장 큰 차이 {worst:0.00}px)");
    }

    /// <summary>쪽(1000×1500 원본을 w×h로)과 네모 풍선 하나, 손으로 잡은 블록 둘 + 그 풍선 안의 자동 블록 하나.</summary>
    private static (Mat Page, LetterScale Scale, List<TextBlockInfo> Blocks) PageWithBlocks(int w, int h)
    {
        var page = SolidPage(w, h);
        double sx = w / 1000.0, sy = h / 1500.0;
        DrawBox(page, Rect.FromLTRB((int)Math.Round(100 * sx), (int)Math.Round(560 * sy), (int)Math.Round(400 * sx), (int)Math.Round(780 * sy)));
        var blocks = new List<TextBlockInfo>
        {
            Manual(100, 80, 400, 220, "TOP AAA"),
            Manual(600, 1300, 900, 1440, "BOTTOM BBB"),
            new(150, 620, 350, 720, "", "MID CCC", 20, false, "#000000"),   // 풍선 안의 자동 블록
        };
        return (page, LetterScale.Resolve(w, h, 1000, 1500), blocks);
    }

    [Fact]
    public void ManualBlockFor_TextAreaOfManualBlock_ReturnsThatBlock()
    {
        // 손으로 잡은 블록 (100,80)–(400,220)의 글 자리는 한쪽에 폭의 5%·높이의 3%씩 들인 (115, 84.2) 270 × 131.6 — 그 역이다
        using var page = SolidPage(1000, 1500);
        var area = TextRenderer.PlanLayout(page, new[] { Manual(100, 80, 400, 220, "HELLO") }, 1, 1, Arial())[0].Rect;
        Assert.Equal(270f, area.Width, 2);
        Assert.Equal(131.6f, area.Height, 2);

        var block = TextRenderer.ManualBlockFor(area.X, area.Y, area.Width, area.Height);

        Assert.Equal(100, block.Left, 3);
        Assert.Equal(80, block.Top, 3);
        Assert.Equal(300, block.Width, 3);
        Assert.Equal(140, block.Height, 3);
    }

    [Theory]
    [InlineData(832, 1240, 0.37)]   // 채색한 쪽(가로 0.832 · 세로 0.8267), 축소해서 보는 중
    [InlineData(832, 1240, 1.0)]
    [InlineData(832, 1240, 2.5)]
    [InlineData(1000, 1500, 0.37)]  // 채색하지 않은 쪽(비율 1)
    [InlineData(1000, 1500, 1.0)]
    [InlineData(1000, 1500, 2.5)]
    public void CommitBox_BoxNotMoved_ChangesNothing(int w, int h, double zoom)
    {
        // 손잡이를 누르기만 하고 놓아도 "놓았다"가 온다 — 그때는 저장할 것이 없어야 한다(null).
        // v1.8.2까지는 띄운 상자를 그대로 블록에 저장해서 손으로 잡은 블록은 300×140 → 270×132로 줄고, 자동 블록은 잰 풍선 크기의 손으로 잡은 블록이 됐다.
        var (page, s, blocks) = PageWithBlocks(w, h);
        using var _ = page;
        var layouts = TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial());

        for (int i = 0; i < blocks.Count; i++)
        {
            var r = layouts[i].Rect;
            // 결과 창이 하는 대로: 화면 좌표(× 확대 배율)로 띄웠다가 ÷ 확대 배율로 되돌린다
            var shown = new LetterBox(r.X * zoom / zoom, r.Y * zoom / zoom, r.Width * zoom / zoom, r.Height * zoom / zoom);

            Assert.Null(s.CommitBox(shown, shown));
            Assert.Null(s.CommitBox(BoxOf(r), shown));   // 나눗셈의 끝자리 오차는 옮긴 것이 아니다
        }
    }

    [Fact]
    public void CommitBox_SmallestMouseMove_IsAMove()
    {
        // 화면 1px(가장 크게 확대한 4배에서 그림 0.25px)만 옮겨도 옮긴 것이다 — 같은 자리로 봐주는 오차는 그보다 훨씬 작아야 한다
        var s = LetterScale.Resolve(832, 1240, 1000, 1500);
        var shown = new LetterBox(100, 200, 250, 110);

        Assert.NotNull(s.CommitBox(shown, shown with { Left = 100.25 }));
        Assert.NotNull(s.CommitBox(shown, shown with { Top = 199.75 }));
        Assert.NotNull(s.CommitBox(shown, shown with { Width = 250.25 }));
        Assert.NotNull(s.CommitBox(shown, shown with { Height = 109.75 }));
    }

    [Theory]
    [InlineData(832, 1240)]
    [InlineData(1000, 1500)]
    public void CommitBox_BoxMovedSixTimes_RebuiltBoxStaysWhereDroppedAndBlockKeepsItsSize(int w, int h)
    {
        // 옮겨 놓기 → 상자 다시 띄우기를 여섯 번: 다시 띄운 상자는 놓은 자리에 있고(원본 좌표가 정수라 1px 안쪽 오차), 블록 크기는 그대로다.
        // v1.8.2까지는 놓을 때마다 90% × 94%로 줄어 300×140이 여섯 번 뒤 159×97이 됐다.
        var (page, s, blocks) = PageWithBlocks(w, h);
        using var _ = page;

        for (int cycle = 1; cycle <= 6; cycle++)
            for (int i = 0; i < blocks.Count; i++)
            {
                var shown = BoxOf(TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[i].Rect);
                var dropped = shown with { Left = shown.Left + (i == 1 ? -10 : 6), Top = shown.Top + (i == 1 ? -7 : 8) };

                var stored = s.CommitBox(shown, dropped);
                Assert.NotNull(stored);
                blocks[i] = Stored(blocks[i], stored.Value);

                var rebuilt = TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[i].Rect;
                AssertSameBox(dropped, rebuilt, 1.0, $"{cycle}번째, 블록 {i}");
            }

        // 손으로 잡은 블록 둘은 처음 크기 그대로 — 옮기기만 했다
        Assert.Equal((300, 140), (blocks[0].X2 - blocks[0].X1, blocks[0].Y2 - blocks[0].Y1));
        Assert.Equal((300, 140), (blocks[1].X2 - blocks[1].X1, blocks[1].Y2 - blocks[1].Y1));
        // 여섯 번 옮긴 거리만큼만 움직였다: 그림 좌표로 (−60, −42) → 원본 좌표로 ÷ 비율 (놓을 때마다 정수로 맞추는 오차 0.5px × 6)
        Assert.InRange(blocks[1].X1, 600 - 60 / s.X - 3, 600 - 60 / s.X + 3);
        Assert.InRange(blocks[1].Y1, 1300 - 42 / s.Y - 3, 1300 - 42 / s.Y + 3);
    }

    [Theory]
    [InlineData(832, 1240)]
    [InlineData(1000, 1500)]
    public void CommitBox_AutoBlockMoved_BecomesManualBlockWhoseBoxIsWhereItWasDropped(int w, int h)
    {
        // 자동으로 놓인 블록의 상자는 잰 풍선 안쪽이다(블록보다 크다). 옮겨 놓으면 그 상자가 글 자리인 손으로 잡은 블록이 된다
        var (page, s, blocks) = PageWithBlocks(w, h);
        using var _ = page;
        var layout = TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[2];
        Assert.True(layout.Round);   // 풍선을 쟀다
        var shown = BoxOf(layout.Rect);
        Assert.True(shown.Width > 200 * s.X + 20, $"잰 풍선이 블록보다 넓어야 한다: {shown}");
        var dropped = shown with { Left = shown.Left + 40, Top = shown.Top - 30 };

        blocks[2] = Stored(blocks[2], s.CommitBox(shown, dropped)!.Value);

        var rebuilt = TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[2];
        Assert.False(rebuilt.Round);
        AssertSameBox(dropped, rebuilt.Rect, 1.0, "자동 블록");
    }

    [Theory]
    [InlineData(832, 1240)]
    [InlineData(1000, 1500)]
    public void CommitBox_BoxResized_RebuiltBoxHasTheDroppedSize(int w, int h)
    {
        var (page, s, blocks) = PageWithBlocks(w, h);
        using var _ = page;

        for (int cycle = 1; cycle <= 6; cycle++)
        {
            var shown = BoxOf(TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[0].Rect);
            var dropped = shown with { Width = shown.Width + 12, Height = shown.Height + 9 };   // 오른쪽 아래 손잡이: 왼쪽 위는 그대로

            blocks[0] = Stored(blocks[0], s.CommitBox(shown, dropped)!.Value);

            AssertSameBox(dropped, TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[0].Rect, 1.0, $"{cycle}번째");
        }
    }

    [Fact]
    public void CommitBox_DroppedBox_IsWhereRenderBlocksDrawsTheText()
    {
        // 놓은 상자 안에 최종 식자가 그려진다 — 채색한 쪽(축별 비율)에서, 쪽 아래 블록으로
        using var t = new TempDir();
        var (page, s, blocks) = PageWithBlocks(832, 1240);
        using var _ = page;
        SavePng(page, t.At("채색.png"));
        var shown = BoxOf(TextRenderer.PlanLayout(page, blocks, s.X, s.Y, Arial())[1].Rect);
        var dropped = shown with { Left = shown.Left - 70, Top = shown.Top - 90 };

        blocks[1] = Stored(blocks[1], s.CommitBox(shown, dropped)!.Value);
        TextRenderer.RenderBlocks(t.At("채색.png"), t.At("없는 원본.png"), 1000, 1500, new[] { blocks[1] }, t.At("식자.png"), Arial());

        using var after = LoadColor(t.At("식자.png"));
        using var ink = ChangedMask(page, after);
        var b = Bounds(ink);
        Assert.NotNull(b);
        var drawn = new RectangleF(b.Value.X, b.Value.Y, b.Value.Width, b.Value.Height);
        var box = new RectangleF((float)dropped.Left - 1, (float)dropped.Top - 1, (float)dropped.Width + 2, (float)dropped.Height + 2);
        Assert.True(box.Contains(drawn), $"{drawn} not in {box}");
        Assert.InRange(drawn.X + drawn.Width / 2, dropped.Left + dropped.Width / 2 - 2.5, dropped.Left + dropped.Width / 2 + 2.5);
        Assert.InRange(drawn.Y + drawn.Height / 2, dropped.Top + dropped.Height / 2 - 3.5, dropped.Top + dropped.Height / 2 + 3.5);
    }

    // ───────── 편집 미리보기 = 최종 식자 ─────────

    [Fact]
    public void ResolveThenPlanLayout_ColorizedPage_PlacesBlocksWhereRenderBlocksDrawsThem()
    {
        // 결과 창 편집 미리보기는 Resolve한 비율로 PlanLayout을 불러 상자를 띄우고, 최종 식자(RenderBlocks)는 사이드카의 원본 크기로 그린다.
        // 둘이 같은 자리여야 한다 — v1.8.2까지 미리보기는 세로에도 가로 비율(0.832)을 써서 쪽 아래 블록의 상자가 글자보다 7px 아래에 놓였다.
        using var t = new TempDir();
        using var colored = SolidPage(832, 1240);
        SavePng(colored, t.At("채색.png"));
        var blocks = new[] { Manual(100, 80, 400, 220, "HELLO WORLD"), Manual(600, 1300, 900, 1440, "HELLO WORLD") };

        var s = LetterScale.Resolve(colored.Cols, colored.Rows, 1000, 1500, t.At("없는 원본.png"));
        var preview = TextRenderer.PlanLayout(colored, blocks, s.X, s.Y, Arial());
        TextRenderer.RenderBlocks(t.At("채색.png"), t.At("없는 원본.png"), 1000, 1500, blocks, t.At("식자.png"), Arial());

        using var after = LoadColor(t.At("식자.png"));
        using var ink = ChangedMask(colored, after);
        var halves = new[] { new Rect(0, 0, 832, 620), new Rect(0, 620, 832, 620) };
        for (int i = 0; i < blocks.Length; i++)
        {
            using var part = new Mat(ink, halves[i]);
            var b = Bounds(part);
            Assert.NotNull(b);
            var drawn = new RectangleF(b.Value.X + halves[i].X, b.Value.Y + halves[i].Y, b.Value.Width, b.Value.Height);
            var box = preview[i].Rect;
            Assert.True(box.Contains(drawn), $"[{i}] {drawn} not in {box}");
            Assert.InRange(drawn.X + drawn.Width / 2, box.X + box.Width / 2 - 2, box.X + box.Width / 2 + 2);
            Assert.InRange(drawn.Y + drawn.Height / 2, box.Y + box.Height / 2 - 3, box.Y + box.Height / 2 + 3);
        }
        // 아래 블록의 가운데는 세로 비율로 1370 × 0.8267 = 1132.5 — 가로 비율이었다면 1139.8
        Assert.InRange(preview[1].Rect.Y + preview[1].Rect.Height / 2, 1132f, 1133f);
    }
}
