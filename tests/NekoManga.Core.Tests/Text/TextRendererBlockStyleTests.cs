using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>풍선 하나의 글자 속성(결과 창 식자 탭의 "선택한 풍선") — 크기(px)·글꼴·외곽선(px·색).
/// px는 결과 그림의 픽셀이다 — 쪽의 해상도나 원본→결과 비율에 따라 달라지지 않는다.</summary>
public class TextRendererBlockStyleTests
{
    private static AppSettings Arial() => new() { LetterFontFamily = "Arial" };

    private static TextBlockInfo Block(int x1, int y1, int x2, int y2, string text) =>
        new(x1, y1, x2, y2, "", text, 20, false, "#000000", ManualRect: true);

    /// <summary>회색(150) 쪽에 그린 검은 글의 테두리 사각형.</summary>
    private static Rect Ink(Mat img)
    {
        using var m = new Mat();
        Cv2.InRange(img, new Scalar(0, 0, 0), new Scalar(60, 60, 60), m);
        return Cv2.BoundingRect(m);
    }

    private static int Count(Mat img, Scalar lo, Scalar hi)
    {
        using var m = new Mat();
        Cv2.InRange(img, lo, hi, m);
        return Cv2.CountNonZero(m);
    }

    [Fact]
    public void Render_BlockWithSizePx_DrawsAtThatSize_NotTheFittedSize()
    {
        // 자동 크기는 풍선에 맞춘 크기와 쪽 공통 크기 가운데 작은 쪽이라 "이 풍선만 크게"가 먹지 않았다 — px를 적으면 그 크기 그대로 그린다
        using var page = SolidPage(600, 1457, 150);
        var b = Block(100, 600, 500, 900, "HHHH") with { OutlinePx = 0 };

        using var small = TextRenderer.Render(page, [b with { SizePx = 20 }], 1, 1, Arial(), out var s1);
        using var large = TextRenderer.Render(page, [b with { SizePx = 40 }], 1, 1, Arial(), out var s2);

        Assert.Equal(20f, s1[0], 1);
        Assert.Equal(40f, s2[0], 1);
        Assert.InRange((double)Ink(large).Height / Ink(small).Height, 1.8, 2.2);
    }

    [Fact]
    public void Render_BlockWithSizePx_LargerThanItsPlace_IsNotShrunk()
    {
        using var page = SolidPage(600, 1457, 150);
        var b = Block(250, 700, 350, 740, "HHHH HHHH") with { OutlinePx = 0, SizePx = 60 };   // 자리 100×40에 60px 글

        using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out var sizes);

        Assert.Equal(60f, sizes[0], 1);
        Assert.True(Ink(img).Height > 80, $"{Ink(img)}");      // 줄이지 않고 넘치게 그린다(두 줄)
    }

    [Fact]
    public void Render_SameSizePx_OnDoubleResolutionPage_IsTheSamePixels()
    {
        using var page = SolidPage(600, 1457, 150);
        using var big = SolidPage(1200, 2914, 150);
        var b = Block(100, 600, 500, 900, "HHHH") with { OutlinePx = 0, SizePx = 24 };

        using var a = TextRenderer.Render(page, [b], 1, 1, Arial(), out _);
        using var c = TextRenderer.Render(big, [b], 2, 2, Arial(), out _);

        Assert.InRange((double)Ink(c).Height / Ink(a).Height, 0.9, 1.1);   // px는 결과 그림의 픽셀 — 비율(scale)을 곱하지 않는다
    }

    [Fact]
    public void Render_BlockWithoutSizePx_IsSizedAutomaticallyAsBefore()
    {
        using var page = SolidPage(600, 1457, 150);
        var b = Block(100, 600, 500, 900, "HHHH");

        var plan = TextRenderer.PlanLayout(page, [b], 1, 1, Arial());
        using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out var sizes);

        Assert.False(plan[0].Fixed);
        Assert.True(sizes[0] > 0 && sizes[0] <= plan[0].Size + 0.01f);
    }

    [Fact]
    public void Render_OutlinePx_SetsThisBlocksOutlineWidth_ZeroMeansNone()
    {
        using var page = SolidPage(600, 1457, 150);
        var b = Block(100, 600, 500, 900, "HHHH") with { SizePx = 40 };
        int White(Mat m) => Count(m, new Scalar(230, 230, 230), new Scalar(255, 255, 255));

        using var none = TextRenderer.Render(page, [b with { OutlinePx = 0 }], 1, 1, Arial(), out _);
        using var page1 = TextRenderer.Render(page, [b], 1, 1, Arial(), out _);                       // 쪽 설정(자동 두께)
        using var thin = TextRenderer.Render(page, [b with { OutlinePx = 2 }], 1, 1, Arial(), out _);
        using var thick = TextRenderer.Render(page, [b with { OutlinePx = 8 }], 1, 1, Arial(), out _);

        Assert.Equal(0, White(none));
        Assert.True(White(page1) > 0);
        Assert.True(White(thin) > 0);
        Assert.True(White(thick) > 2 * White(thin), $"{White(thick)} vs {White(thin)}");
    }

    [Fact]
    public void Render_OutlineColor_ColorsOnlyThatBlock()
    {
        using var page = SolidPage(600, 1457, 150);
        var red = Block(100, 300, 500, 500, "HHHH") with { OutlineColor = "#FF0000" };
        var plain = Block(100, 900, 500, 1100, "HHHH");

        using var img = TextRenderer.Render(page, [red, plain], 1, 1, Arial(), out _);

        using var top = new Mat(img, new Rect(0, 0, 600, 700));
        using var bottom = new Mat(img, new Rect(0, 700, 600, 757));
        var lo = new Scalar(0, 0, 200); var hi = new Scalar(60, 60, 255);   // BGR 빨강
        Assert.True(Count(top, lo, hi) > 0);
        Assert.Equal(0, Count(bottom, lo, hi));
    }

    [Fact]
    public void Render_BlockFontFamily_OverridesThePageFont()
    {
        using var page = SolidPage(600, 1457, 150);
        var b = Block(100, 600, 500, 900, "HHHH iiii") with { SizePx = 28, OutlinePx = 0 };

        using var arial = TextRenderer.Render(page, [b], 1, 1, Arial(), out _);
        using var same = TextRenderer.Render(page, [b with { FontFamily = "Arial" }], 1, 1, new AppSettings { LetterFontFamily = "Courier New" }, out _);
        using var mono = TextRenderer.Render(page, [b with { FontFamily = "Courier New" }], 1, 1, Arial(), out _);

        PipelineTestHelpers.AssertSamePixels(arial, same);
        Assert.NotEqual(Ink(arial).Width, Ink(mono).Width);
    }

    [Fact]
    public void Render_SkippedBlock_IsNotDrawnButKeepsItsSizeAndTheOthersLayout()
    {
        // 결과 창: 글을 고치는 중인 풍선은 텍스트박스가 보여 주므로 그림에서는 뺀다 — 다른 풍선의 자리·크기는 그대로여야 한다
        using var page = SolidPage(600, 1457, 150);
        var a = Block(100, 300, 500, 500, "HHHH");
        var b = Block(100, 900, 500, 1100, "HHHH");

        using var all = TextRenderer.Render(page, [a, b], 1, 1, Arial(), out var s1);
        using var skip = TextRenderer.Render(page, [a, b], 1, 1, Arial(), out var s2, skip: 0);

        Assert.Equal(s1, s2);
        using var top = new Mat(skip, new Rect(0, 0, 600, 700));
        using var topPage = new Mat(page, new Rect(0, 0, 600, 700));
        PipelineTestHelpers.AssertSamePixels(topPage, top);
        using var bottom = new Mat(skip, new Rect(0, 700, 600, 757));
        using var bottomAll = new Mat(all, new Rect(0, 700, 600, 757));
        PipelineTestHelpers.AssertSamePixels(bottomAll, bottom);
    }

    [Fact]
    public void AutoOutlinePx_IsTheWidthThePageSettingDraws()
    {
        // 자동 두께: 펜 폭 = max(2.5, 크기/7) × 배율, 글자 밖으로 보이는 것은 그 절반
        Assert.Equal(35 / 7.0 / 2, TextRenderer.AutoOutlinePx(35, new AppSettings()), 3);
        Assert.Equal(0, TextRenderer.AutoOutlinePx(35, new AppSettings { LetterOutlineScale = 0 }), 3);
    }

    [Fact]
    public void LetterSidecar_RoundTripsBlockStyle_AndOldJsonReadsAsAutomatic()
    {
        using var t = new TempDir();
        var outPath = t.At("p.png");
        var basePath = LetterSidecar.BasePathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        using (var page = SolidPage(60, 40)) SavePng(page, basePath);
        var styled = Block(1, 2, 30, 20, "가") with { SizePx = 11.5, FontFamily = "Arial", OutlinePx = 0.8, OutlineColor = "#112233" };
        new LetterSidecar(t.At("src.png"), 60, 40, basePath, [styled, Block(1, 2, 30, 20, "나")]).Save(outPath);

        var loaded = LetterSidecar.Load(outPath)!;

        Assert.Equal(styled, loaded.Blocks[0]);
        Assert.Equal(0, loaded.Blocks[1].SizePx);
        Assert.Equal(-1, loaded.Blocks[1].OutlinePx);
        // 옛 사이드카(새 칸이 없는 json)
        var json = File.ReadAllText(LetterSidecar.JsonPathFor(outPath));
        foreach (var key in new[] { "SizePx", "FontFamily", "OutlinePx", "OutlineColor" })
            json = System.Text.RegularExpressions.Regex.Replace(json, ",\"" + key + "\":(\"[^\"]*\"|[-0-9.]+)", "");
        Assert.DoesNotContain("SizePx", json);
        File.WriteAllText(LetterSidecar.JsonPathFor(outPath), json);
        var old = LetterSidecar.Load(outPath)!;
        Assert.Equal(0, old.Blocks[0].SizePx);
        Assert.Equal("", old.Blocks[0].FontFamily);
        Assert.Equal(-1, old.Blocks[0].OutlinePx);
        Assert.Equal("", old.Blocks[0].OutlineColor);
    }

    // ── 쪽 공통 크기를 붙들기: 풍선 하나를 고쳐도 다른 풍선의 자동 크기가 따라 바뀌지 않는다(2026-11-17 사용자 "텍스트박스 별로 각각 개별로 수정이 되어야해")
    private static readonly TextBlockInfo CoA = Block(100, 100, 330, 220, "HHHH HHHH HHHH");
    private static readonly TextBlockInfo CoB = Block(550, 500, 730, 600, "HHHH HHHH HHHH");
    private static readonly TextBlockInfo CoC = Block(100, 900, 240, 980, "HHHH HHHH HHHH");

    public static TheoryData<string> OneBlockEdits => new() { "longer", "smaller", "bigger", "hidden" };

    private static TextBlockInfo Edited(string how) => how switch
    {
        "longer" => CoB with { Translation = "HHHH HHHH HHHH HHHH HHHH HHHH HHHH HHHH" },
        "smaller" => CoB with { X2 = 650, Y2 = 560 },
        "bigger" => CoB with { X1 = 500, X2 = 950, Y1 = 450, Y2 = 800 },
        _ => CoB with { Hidden = true },
    };

    [Theory]
    [MemberData(nameof(OneBlockEdits))]
    public void Render_WithoutFrozenCommonSize_EditingOneBlock_MovesTheOthers(string how)
    {
        // 붙들지 않으면(자동 식자) 쪽 공통 크기 = 풍선별 맞춤 크기의 중앙값이라 한 풍선이 바뀌면 다른 풍선이 따라 바뀐다 — 이 시험이 아래 시험의 전제다
        using var page = SolidPage(1000, 1457, 255);
        using var a = TextRenderer.Render(page, [CoA, CoB, CoC], 1, 1, Arial(), out var before);
        using var b = TextRenderer.Render(page, [CoA, Edited(how), CoC], 1, 1, Arial(), out var after);

        Assert.NotEqual(before[0], after[0]);
    }

    [Theory]
    [MemberData(nameof(OneBlockEdits))]
    public void Render_WithFrozenCommonSize_EditingOneBlock_LeavesTheOthersSizes(string how)
    {
        using var page = SolidPage(1000, 1457, 255);
        double common = TextRenderer.CommonSize(page, [CoA, CoB, CoC], 1, 1, Arial());
        using var a = TextRenderer.Render(page, [CoA, CoB, CoC], 1, 1, Arial(), out var before);
        using var b = TextRenderer.Render(page, [CoA, Edited(how), CoC], 1, 1, Arial(), out var after, commonSize: common);

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[2], after[2]);
    }

    [Fact]
    public void Render_WithTheComputedCommonSize_IsTheSamePictureAsAutomatic()
    {
        using var page = SolidPage(1000, 1457, 255);
        double common = TextRenderer.CommonSize(page, [CoA, CoB, CoC], 1, 1, Arial());
        using var auto = TextRenderer.Render(page, [CoA, CoB, CoC], 1, 1, Arial(), out _);
        using var frozen = TextRenderer.Render(page, [CoA, CoB, CoC], 1, 1, Arial(), out _, commonSize: common);

        Assert.True(common > 0);
        PipelineTestHelpers.AssertSamePixels(auto, frozen);
    }

    [Fact]
    public void Render_FrozenCommonSize_StillFollowsThePageSizeScale()
    {
        using var page = SolidPage(1000, 1457, 255);
        var roomy = Block(100, 100, 900, 700, "HHHH");
        var s = new AppSettings { LetterFontFamily = "Arial", LetterSizeScale = 2 };
        using var a = TextRenderer.Render(page, [roomy], 1, 1, Arial(), out var one, commonSize: 12);
        using var b = TextRenderer.Render(page, [roomy], 1, 1, s, out var two, commonSize: 12);

        Assert.Equal(12f, one[0], 1);
        Assert.Equal(24f, two[0], 1);
    }

    [Fact]
    public void LetterSidecar_RoundTripsCommonSize_AndOldJsonReadsAsZero()
    {
        using var t = new TempDir();
        var outPath = t.At("p.png");
        var basePath = LetterSidecar.BasePathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        using (var page = SolidPage(60, 40)) SavePng(page, basePath);
        new LetterSidecar(t.At("src.png"), 60, 40, basePath, [Block(1, 2, 30, 20, "A")], CommonSize: 23.5).Save(outPath);

        Assert.Equal(23.5, LetterSidecar.Load(outPath)!.CommonSize);
        var json = File.ReadAllText(LetterSidecar.JsonPathFor(outPath)).Replace(",\"CommonSize\":23.5", "");
        Assert.DoesNotContain("CommonSize", json);
        File.WriteAllText(LetterSidecar.JsonPathFor(outPath), json);
        Assert.Equal(0, LetterSidecar.Load(outPath)!.CommonSize);
    }
}
