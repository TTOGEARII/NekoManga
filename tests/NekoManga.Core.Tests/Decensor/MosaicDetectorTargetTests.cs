using NekoManga.Core.Decensor;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>MosaicDetector.BuildMaskForTarget — 설정의 '무엇을 검열로 볼까요'(DecensorTarget)에 맞는 감지기 고르기.
/// 바탕은 그림 대용의 중간 회색(140)이라 흰 막대도 바탕과 구분된다. 모자이크 칸·막대 좌표는 8px 격자에 맞춘다.</summary>
public class MosaicDetectorTargetTests
{
    private static readonly Rect BlackBarRect = new(32, 32, 160, 24);
    private static readonly Rect WhiteBarRect = new(32, 120, 160, 24);
    private static readonly Rect MosaicRect = new(256, 32, 96, 96);
    private static readonly Rect ColorBarRect = new(256, 200, 160, 24);

    private static AppSettings Settings(string target) => new()
    {
        DecensorTarget = target,
        DecensorBlockSize = 8,
        DecensorThreshold = 15,
        DecensorMaskGrow = 0,
        DecensorBarMinAspect = 1.8,
        DecensorBarMinArea = 350,
    };

    [Theory]
    [InlineData("BlackBar", "black", new[] { "white", "color" })]
    [InlineData("WhiteBar", "white", new[] { "black", "color" })]
    [InlineData("Color", "color", new[] { "black", "white" })]
    [InlineData("Mosaic", "mosaic", new[] { "black", "white" })]
    public void BuildMaskForTarget_ExplicitTarget_UsesOnlyThatDetector(string target, string own, string[] untouched)
    {
        // 한 번에 한 종류만 추적한다 — 네 종류가 다 있는 페이지에서도 고른 종류만 마스크에 들어간다.
        using var img = SolidPage(480, 360, gray: 140);
        Cv2.Rectangle(img, BlackBarRect, Black, -1);
        Cv2.Rectangle(img, WhiteBarRect, White, -1);
        DrawMosaic(img, MosaicRect, 8);
        Cv2.Rectangle(img, ColorBarRect, new Scalar(200, 60, 0), -1);   // 채도 높은 파란 막대
        var regions = new Dictionary<string, Rect>
        {
            ["black"] = BlackBarRect, ["white"] = WhiteBarRect, ["mosaic"] = MosaicRect, ["color"] = ColorBarRect,
        };

        using var mask = MosaicDetector.BuildMaskForTarget(Settings(target), img, null, out var kindUsed);

        Assert.Equal(target, kindUsed);
        Assert.True(Coverage(mask, regions[own]) >= 0.95, $"{target}: 자기 종류 덮음 {Coverage(mask, regions[own]):P1}");
        foreach (var other in untouched)
            Assert.Equal(0, CountIn(mask, regions[other]));
    }

    [Theory]
    [InlineData("blackBarAndMosaic", "BlackBar")]   // 오탐이 가장 적은 검은 막대가 먼저다
    [InlineData("mosaicOnly", "Mosaic")]
    [InlineData("whiteBarOnly", "WhiteBar")]        // 흰색은 원고의 흰 띠와 헷갈리기 쉬워 마지막에 본다
    public void BuildMaskForTarget_Auto_PicksMostPlausibleKindInPriorityOrder(string page, string expected)
    {
        using var img = SolidPage(480, 360, gray: 140);
        var bigBlack = new Rect(40, 40, 240, 32);
        var bigWhite = new Rect(40, 200, 240, 32);
        var mosaic = new Rect(320, 160, 96, 96);
        if (page == "blackBarAndMosaic") Cv2.Rectangle(img, bigBlack, Black, -1);
        if (page is "blackBarAndMosaic" or "mosaicOnly") DrawMosaic(img, mosaic, 8);
        if (page == "whiteBarOnly") Cv2.Rectangle(img, bigWhite, White, -1);
        var expectedRegion = expected switch { "BlackBar" => bigBlack, "Mosaic" => mosaic, _ => bigWhite };

        using var mask = MosaicDetector.BuildMaskForTarget(Settings("Auto"), img, null, out var kindUsed);

        Assert.Equal(expected, kindUsed);
        Assert.True(Coverage(mask, expectedRegion) >= 0.95, $"{page}: 덮음 {Coverage(mask, expectedRegion):P1}");
    }

    [Theory]
    [InlineData("clean")]           // 흰 종이에 가는 선화뿐인 깨끗한 페이지
    [InlineData("lineArtOnTone")]   // 평탄한 톤(피부 등) 위의 같은 선화 — 모자이크 격자가 아니다
    [InlineData("smallBarOnly")]    // 큰 막대 기준(최대 성분 6000px)에 못 미치는 막대 하나
    [InlineData("manyFragments")]   // 큰 막대가 있어도 조각이 많으면 효과음 붓획 쪽으로 본다
    public void BuildMaskForTarget_Auto_WithoutConvincingCensor_ReportsNone(string page)
    {
        using var img = SolidPage(480, 360, gray: page == "clean" ? (byte)255 : (byte)140);
        switch (page)
        {
            case "clean":
            case "lineArtOnTone":
                Cv2.Line(img, new Point(40, 300), new Point(440, 260), Black, 2);
                Cv2.Line(img, new Point(60, 40), new Point(200, 330), Black, 2);
                break;
            case "smallBarOnly":
                Cv2.Rectangle(img, new Rect(96, 96, 104, 32), Black, -1);
                break;
            case "manyFragments":
                Cv2.Rectangle(img, new Rect(40, 40, 240, 32), Black, -1);
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                        Cv2.Rectangle(img, new Rect(40 + col * 104, 120 + row * 72, 64, 24), Black, -1);
                break;
        }

        using var mask = MosaicDetector.BuildMaskForTarget(Settings("Auto"), img, null, out var kindUsed);

        Assert.Null(mask);
        Assert.Equal("없음", kindUsed);
    }
}
