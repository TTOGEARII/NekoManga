using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>바탕 밝기에 따른 글자색·외곽선색 자동 반전(2026-11-26 사용자 "바탕 밝기에 따라 글자색·외곽선색 자동 반전 이렇게 처리해 주고").
/// 투명 풍선·검은 바탕의 글(풍선을 못 재 블록 자리에 그리는 글)이 흰 테두리 검은 글로 튀던 것을, 글이 놓이는 자리 아래 그림이 어두우면 두 색을 서로 바꿔 그린다.
/// 글자 색은 빨강·외곽선은 파랑으로 두고 어느 색이 그려졌는지 센다 — 기본 검정·흰색은 어두운 바탕·외곽선과 섞여 가를 수 없다.</summary>
public class TextRendererAutoContrastTests
{
    private static AppSettings RedOnBlue(double outline = 0, bool auto = true) => new()
    {
        LetterFontFamily = "Arial", LetterColorHex = "#FF0000", LetterOutlineColorHex = "#0000FF", LetterOutlineScale = outline, LetterAutoContrast = auto,
    };

    /// <summary>손으로 잡은 400×300 자리에 60px 글 — 자리는 풍선을 재지 않고 블록 그대로라 글 아래 바탕 밝기 = 쪽의 톤.</summary>
    private static TextBlockInfo Block() => new(100, 600, 500, 900, "", "HHHH", 20, false, "#FF0000", ManualRect: true, SizePx: 60);

    private static int Count(Mat img, Scalar lo, Scalar hi) { using var m = new Mat(); Cv2.InRange(img, lo, hi, m); return Cv2.CountNonZero(m); }
    private static int Red(Mat img) => Count(img, new Scalar(0, 0, 200), new Scalar(70, 70, 255));      // BGR — 안티에일리어싱 가장자리는 빼고 순색만
    private static int Blue(Mat img) => Count(img, new Scalar(200, 0, 0), new Scalar(255, 70, 70));
    private static int Green(Mat img) => Count(img, new Scalar(0, 200, 0), new Scalar(70, 255, 70));

    private static Mat Rendered(int tone, AppSettings s, TextBlockInfo? b = null)
    {
        using var page = SolidPage(600, 1457, tone);
        return TextRenderer.Render(page, [b ?? Block()], 1, 1, s, out _);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]    // 평균 밝기 128 미만이면 어두운 바탕
    public void Render_DarkBackground_TextTakesTheOutlineColor(int tone)
    {
        using var img = Rendered(tone, RedOnBlue());   // 외곽선 0 — 글자 색만 보인다
        Assert.True(Blue(img) > 100, $"blue {Blue(img)}");
        Assert.Equal(0, Red(img));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(136)]    // 128 이상은 밝은 바탕 — 전과 같다(흰 풍선은 LaMa가 메운 자리도 213~223)
    public void Render_BrightBackground_KeepsTheTextColor(int tone)
    {
        using var img = Rendered(tone, RedOnBlue());
        Assert.True(Red(img) > 100, $"red {Red(img)}");
        Assert.Equal(0, Blue(img));
    }

    [Fact]
    public void Render_DarkBackground_OutlineTakesTheTextColor()
    {
        // 외곽선 배율 4(60px 글에 펜 34px → 밖으로 17px)는 굵은 H의 획(9px쯤)보다 넓어 외곽선 픽셀이 글자 픽셀보다 많다
        using var dark = Rendered(60, RedOnBlue(outline: 4));
        using var bright = Rendered(255, RedOnBlue(outline: 4));
        Assert.True(Red(dark) > Blue(dark) && Blue(dark) > 0, $"dark red {Red(dark)} blue {Blue(dark)}");           // 어두움: 외곽선 빨강·글자 파랑
        Assert.True(Blue(bright) > Red(bright) && Red(bright) > 0, $"bright red {Red(bright)} blue {Blue(bright)}");   // 밝음: 외곽선 파랑·글자 빨강
    }

    [Fact]
    public void Render_AutoContrastOff_DarkBackgroundKeepsTheColors()
    {
        using var img = Rendered(60, RedOnBlue(auto: false));
        Assert.True(Red(img) > 100, $"red {Red(img)}");
        Assert.Equal(0, Blue(img));
    }

    [Fact]
    public void Render_BlockWithItsOwnTextColor_IsNotSwapped()
    {
        // 블록에 색을 직접 적은 풍선은 사용자의 선택 — 바탕이 어두워도 그 색 그대로(외곽선 색을 글자에 쓰지 않는다)
        using var img = Rendered(60, RedOnBlue(), Block() with { ColorOverride = "#00FF00" });
        Assert.True(Green(img) > 100, $"green {Green(img)}");
        Assert.Equal(0, Blue(img));
    }

    [Fact]
    public void Render_BlockWithItsOwnOutlineColor_IsNotSwapped()
    {
        using var img = Rendered(60, RedOnBlue(), Block() with { OutlineColor = "#00FF00" });
        Assert.True(Red(img) > 100, $"red {Red(img)}");
        Assert.Equal(0, Blue(img));
    }

    [Fact]
    public void Render_AutoTextColor_IsNotSwapped()
    {
        // 글자 색 auto(원문 색)는 이미 원문을 따른다 — 어두운 바탕의 흰 글은 OCR이 읽은 색으로 그려지므로 바꾸지 않는다
        var s = RedOnBlue();
        s.LetterColorHex = AppSettings.LetterColorAuto;
        using var img = Rendered(60, s, Block() with { FontColorHex = "#00FF00" });
        Assert.True(Green(img) > 100, $"green {Green(img)}");
        Assert.Equal(0, Blue(img));
    }

    [Fact]
    public void DarkBehind_MeasuresOnlyThePartInsideTheImage()
    {
        using var page = SolidPage(200, 200, 0);
        Assert.True(TextRenderer.DarkBehind(page, new System.Drawing.RectangleF(-50, -50, 100, 100)));   // 반이 밖 — 안쪽은 검다
        Assert.False(TextRenderer.DarkBehind(page, new System.Drawing.RectangleF(300, 300, 50, 50)));    // 다 밖 — 모르면 바꾸지 않는다
        Assert.False(TextRenderer.DarkBehind(page, new System.Drawing.RectangleF(10, 10, 0, 0)));        // 빈 자리
    }

    [Fact]
    public void DarkBehind_UsesTheMeanOfTheWholePlace()
    {
        using var page = SolidPage(200, 200, 255);
        Cv2.Rectangle(page, new Rect(0, 0, 100, 200), Scalar.Black, -1);   // 왼쪽 반 검정
        Assert.True(TextRenderer.DarkBehind(page, new System.Drawing.RectangleF(0, 0, 130, 200)));    // 검정 100열 : 흰 30열 → 평균 59
        Assert.False(TextRenderer.DarkBehind(page, new System.Drawing.RectangleF(70, 0, 130, 200)));   // 검정 30열 : 흰 100열 → 평균 196
    }
}
