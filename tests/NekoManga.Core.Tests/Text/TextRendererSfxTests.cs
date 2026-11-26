using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>효과음 블록(TextBlockInfo.Sfx, 2026-11-26 시험 기능 — CarrotMangaTranslator 참고)의 식자: 풍선을 재지 않고 블록 자리에,
/// 자리에 맞춘 고정 크기로, 굵게, 높이가 폭의 1.5배를 넘으면 글자를 한 줄에 하나씩 쌓는다. 쪽 공통 크기에는 끼지 않는다.</summary>
public class TextRendererSfxTests
{
    private static AppSettings Arial() => new() { LetterFontFamily = "Arial" };

    private static TextBlockInfo Sfx(int x1, int y1, int x2, int y2, string text, bool vertical) =>
        new(x1, y1, x2, y2, "ドン", text, 20, vertical, "#000000", Sfx: true);

    private static TextBlockInfo Bubble(int x1, int y1, int x2, int y2, string text) =>
        new(x1, y1, x2, y2, "", text, 20, false, "#000000", ManualRect: true);

    [Fact]
    public void Render_HorizontalSfx_OneLineSizedToTheBlockHeight()
    {
        using var page = SolidPage(600, 800, 255);
        var b = Sfx(100, 100, 400, 180, "쾅", vertical: false);   // 300×80 — 풍선이 아니라 블록 높이가 크기를 정한다

        using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out var sizes);
        using var ink = ChangedMask(page, img);

        Assert.Single(InkLines(ink));
        Assert.InRange(sizes[0], 50, 80);
        var bounds = Bounds(ink)!.Value;
        Assert.InRange(bounds.Y, 90, 200);   // 블록 자리 안(위아래로 조금 넘칠 수 있다)
    }

    [Fact]
    public void Render_VerticalSfx_StacksOneCharacterPerLine()
    {
        using var page = SolidPage(600, 800, 255);
        var b = Sfx(100, 100, 180, 400, "두근두근", vertical: true);   // 80×300, 높이가 폭의 1.5배 넘음

        using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out var sizes);
        using var ink = ChangedMask(page, img);

        // 그려진 줄 = 글자 하나씩(잉크 줄로 세면 한글 한 글자 안의 빈 줄이 줄을 가른다 — '근'의 위아래 획)
        Assert.Equal(["두", "근", "두", "근"], TextRenderer.DrawnLines(page, [b], 1, 1, Arial(), 0));
        var bounds = Bounds(ink)!.Value;
        Assert.True(bounds.Height > bounds.Width * 2, $"{bounds}");   // 세로로 길게 쌓였다
        Assert.InRange(bounds.Width, 20, 100);                        // 블록 폭(80) 안팎 — 외곽선만큼 넘칠 수 있다
        Assert.InRange(sizes[0], 30, 72);
    }

    [Fact]
    public void CommonSize_IgnoresSfxBlocks()
    {
        // 작은 효과음 둘이 끼면 중앙값이 효과음 크기로 내려가 대사가 깨알 글씨가 된다 — 효과음은 쪽 공통 크기에 끼지 않는다
        using var page = SolidPage(600, 1457, 255);
        var bubble = Bubble(100, 600, 500, 900, "HHHH HHHH");
        var small1 = Sfx(20, 20, 60, 60, "쾅", false);
        var small2 = Sfx(20, 80, 60, 120, "쾅", false);

        var alone = TextRenderer.CommonSize(page, [bubble], 1, 1, Arial());
        var withSfx = TextRenderer.CommonSize(page, [bubble, small1, small2], 1, 1, Arial());

        Assert.True(alone > 20, $"{alone}");
        Assert.Equal(alone, withSfx, 0.01);
    }

    [Fact]
    public void SfxStyle_BoldByDefault_VerticalStacksCharacters_ExplicitValuesKept()
    {
        var h = Sfx(0, 0, 100, 40, "쾅 쾅", vertical: false);
        Assert.Equal(1, TextRenderer.SfxStyle(h).BoldOverride);
        Assert.Equal("쾅 쾅", TextRenderer.SfxStyle(h).Translation);           // 가로는 글 그대로
        Assert.Equal(0, TextRenderer.SfxStyle(h with { BoldOverride = 0 }).BoldOverride);   // 사용자가 '보통'으로 바꾼 것은 지킨다

        var v = TextRenderer.SfxStyle(Sfx(0, 0, 40, 200, "두근 두근", vertical: true));
        Assert.Equal("두\n근\n두\n근", v.Translation);                        // 빈칸은 빼고 한 줄에 한 글자
        Assert.True(v.ManualLines);

        var plain = Bubble(0, 0, 100, 40, "대사");
        Assert.Same(plain, TextRenderer.SfxStyle(plain));                     // 효과음이 아니면 손대지 않는다
    }

    [Fact]
    public void Sidecar_SfxFlag_SurvivesJsonRoundTrip()
    {
        var b = Sfx(1, 2, 3, 4, "쾅", false);
        var json = System.Text.Json.JsonSerializer.Serialize(b);
        var back = System.Text.Json.JsonSerializer.Deserialize<TextBlockInfo>(json)!;
        Assert.True(back.Sfx);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<TextBlockInfo>("{\"X1\":0,\"Y1\":0,\"X2\":1,\"Y2\":1,\"SourceText\":\"\",\"Translation\":\"a\",\"FontSize\":1,\"Vertical\":false,\"FontColorHex\":\"#000000\"}")!.Sfx);   // 옛 json
    }
}
