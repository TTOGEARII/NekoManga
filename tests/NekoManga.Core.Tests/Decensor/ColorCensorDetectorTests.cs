using NekoManga.Core.Decensor;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>ColorCensorDetector.BuildMask — 흑백 원고 위에 덮인 '색 있는' 검열(색 막대·틴트 모자이크).</summary>
public class ColorCensorDetectorTests
{
    private static readonly Scalar SaturatedBlue = new(220, 40, 0);   // BGR, HSV 채도 255
    private static readonly Rect Bar = new(120, 140, 160, 24);

    /// <summary>흰 종이에 선화와 회색 톤이 있는 흑백 원고.</summary>
    private static Mat MonochromePage()
    {
        var img = SolidPage(400, 300);
        Cv2.Line(img, new Point(20, 30), new Point(380, 60), Black, 2);
        Cv2.Circle(img, new Point(320, 220), 40, Black, 3);
        Cv2.Rectangle(img, new Rect(20, 200, 120, 80), GrayColor(150), -1);
        return img;
    }

    [Fact]
    public void BuildMask_SaturatedBarOnMonochromePage_IsMasked()
    {
        // 선화·망점·먹칠은 전부 무채색이라, 흑백 원고의 채도 덩어리는 검열일 확률이 매우 높다.
        using var img = MonochromePage();
        Cv2.Rectangle(img, Bar, SaturatedBlue, -1);

        using var mask = ColorCensorDetector.BuildMask(img, null, grow: 0, out var satFrac, out var compCount);

        Assert.True(Coverage(mask, Bar) >= 0.95, $"색 막대 덮음 {Coverage(mask, Bar):P1}");
        Assert.Equal(CountIn(mask, Bar), CountAll(mask));   // 막대 밖(선화·톤)은 한 픽셀도 안 들어간다
        Assert.Equal(1, compCount);
        Assert.Equal(Bar.Width * Bar.Height / (400.0 * 300), satFrac, 3);
    }

    [Fact]
    public void BuildMask_ColorIllustration_IsSkippedEntirely()
    {
        // 채도가 페이지 전반(>25%)이면 컬러 일러스트·표지다 — 그 안의 색 막대 모양도 검열로 보지 않는다.
        using var img = SolidPage(300, 300);
        Cv2.Rectangle(img, new Rect(0, 0, 300, 150), new Scalar(30, 30, 200), -1);
        Cv2.Rectangle(img, new Rect(60, 200, 160, 24), SaturatedBlue, -1);

        using var mask = ColorCensorDetector.BuildMask(img, null, grow: 0, out var satFrac, out var compCount);

        Assert.Null(mask);
        Assert.True(satFrac > 0.25, $"satFrac {satFrac:F3}");
        Assert.Equal(0, compCount);
    }

    [Theory]
    [InlineData("monochrome")]   // 무채색뿐
    [InlineData("paleTint")]     // 누런 종이·옅은 세피아: 약한 채도(S<90)는 JPEG 색 번짐 수준이라 거른다
    public void BuildMask_PageWithoutStrongSaturation_ReturnsNull(string kind)
    {
        using var img = MonochromePage();
        if (kind == "paleTint")
        {
            using var tint = new Mat(img.Size(), MatType.CV_8UC3, new Scalar(200, 215, 235));
            Cv2.Min(img, tint, img);
            Cv2.Rectangle(img, Bar, new Scalar(180, 200, 230), -1);
        }

        using var mask = ColorCensorDetector.BuildMask(img, null, grow: 0, out var satFrac, out var compCount);

        Assert.Null(mask);
        Assert.Equal(0, satFrac);
        Assert.Equal(0, compCount);
    }

    [Fact]
    public void BuildMask_SmallColorSpeck_IsIgnoredWhileBarIsKept()
    {
        // 작은 색 얼룩(면적 300px 미만)은 JPEG 색잡음으로 보고 버린다.
        using var img = MonochromePage();
        Cv2.Rectangle(img, Bar, SaturatedBlue, -1);
        var speck = new Rect(360, 120, 12, 12);
        Cv2.Rectangle(img, speck, new Scalar(0, 0, 230), -1);

        using var mask = ColorCensorDetector.BuildMask(img, null, grow: 0, out _, out var compCount);

        Assert.True(Coverage(mask, Bar) >= 0.95, $"색 막대 덮음 {Coverage(mask, Bar):P1}");
        Assert.Equal(0, CountIn(mask, speck));
        Assert.Equal(1, compCount);
    }

    [Theory]
    [InlineData(0.8, false)]   // 절반 넘게 글자 블록(색 말풍선 등)과 겹치면 버린다
    [InlineData(0.2, true)]    // 조금 걸친 정도면 검열로 남긴다
    public void BuildMask_ComponentOverlappingExcludedRect_IsDroppedOnlyWhenMostlyInside(double overlap, bool kept)
    {
        using var img = MonochromePage();
        Cv2.Rectangle(img, Bar, SaturatedBlue, -1);
        var exclude = new Rect(Bar.X, Bar.Y - 10, (int)(Bar.Width * overlap), Bar.Height + 20);

        using var mask = ColorCensorDetector.BuildMask(img, [exclude], grow: 0, out _, out var compCount);

        Assert.Equal(kept, mask != null);
        Assert.Equal(kept ? 1 : 0, compCount);
    }

    [Theory]
    [InlineData("grayscale")]   // 한 채널 그림 — 색이 있을 수 없다
    [InlineData("empty")]
    public void BuildMask_InputWithoutColorChannels_ReturnsNull(string kind)
    {
        using var img = kind == "empty" ? new Mat() : new Mat(200, 300, MatType.CV_8UC1, new Scalar(128));

        using var mask = ColorCensorDetector.BuildMask(img, null, grow: 0, out var satFrac, out var compCount);

        Assert.Null(mask);
        Assert.Equal(0, satFrac);
        Assert.Equal(0, compCount);
    }
}
