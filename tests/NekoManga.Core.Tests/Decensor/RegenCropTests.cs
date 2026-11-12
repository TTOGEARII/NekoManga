using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>재생성 v3의 자를 창(RegenCrop.WindowFor)과 영역 묶음(RegenRegion.BuildRegions) — 영역마다 주변을 1024 정사각으로 잘라 SDXL에 보낸다.</summary>
public class RegenCropTests
{
    [Fact]
    public void WindowFor_SmallRegionCenter_Is1024Square()
    {
        var w = RegenCrop.WindowFor(new Rect(1400, 1900, 200, 100), new Size(3000, 4000), 1024);
        Assert.Equal(new Size(1024, 1024), w.Window.Size);
        Assert.Equal(1500, w.Window.X + w.Window.Width / 2);     // 영역 중심에 맞춘다
        Assert.Equal(1950, w.Window.Y + w.Window.Height / 2);
        Assert.Equal((1024, 1024), (w.SendW, w.SendH));
        Assert.Equal(1.0, w.Scale, 6);
    }

    [Fact]
    public void WindowFor_NearEdge_ShiftsInside()
    {
        var w = RegenCrop.WindowFor(new Rect(10, 10, 100, 50), new Size(3000, 4000), 1024);
        Assert.Equal(new Rect(0, 0, 1024, 1024), w.Window);
    }

    [Fact]
    public void WindowFor_LargeRegion_ScalesDown()
    {
        // 긴 변 1500 → 창 2400(1.6배), 보내는 크기 1024, 되돌리기 비율 1024/2400
        var w = RegenCrop.WindowFor(new Rect(500, 500, 1500, 900), new Size(4000, 4000), 1024);
        Assert.Equal(new Size(2400, 2400), w.Window.Size);
        Assert.Equal((1024, 1024), (w.SendW, w.SendH));
        Assert.Equal(1024.0 / 2400, w.Scale, 6);
        Assert.True(w.Window.Contains(new Rect(500, 500, 1500, 900)));
    }

    [Fact]
    public void WindowFor_SmallImage_ClampsToImage()
    {
        var w = RegenCrop.WindowFor(new Rect(100, 100, 50, 50), new Size(800, 600), 1024);
        Assert.Equal(new Rect(0, 0, 800, 600), w.Window);
        Assert.Equal((800, 600), (w.SendW, w.SendH));
        Assert.Equal(1.0, w.Scale, 6);
    }

    [Theory]
    [InlineData(1000, 1000, 1000)]   // 8의 배수면 그대로
    [InlineData(1003, 1003, 1000)]   // 아니면 내린다
    public void WindowFor_SendIsMultipleOf8(int imgW, int imgH, int expectSend)
    {
        var w = RegenCrop.WindowFor(new Rect(400, 400, 50, 50), new Size(imgW, imgH), 1024);
        Assert.Equal((expectSend, expectSend), (w.SendW, w.SendH));
        Assert.Equal(0, w.SendW % 8);
    }

    // ── 영역 묶음 ──

    private static Mat MaskWith(Size img, params Rect[] pieces)
    {
        var m = new Mat(img, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in pieces) m.Rectangle(r, Scalar.White, -1);
        return m;
    }
    private static BodyPart Part(int cls, Rect r) => new(cls, 0.9f, r);

    [Theory]
    [InlineData(BodyPartDetector.ClsPenis, RegenKind.Male)]
    [InlineData(BodyPartDetector.ClsPussy, RegenKind.Female)]
    public void BuildRegions_KindFromParts(int cls, RegenKind expected)
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [Part(cls, new Rect(1300, 1700, 400, 400))], 0.1, 0.6, mask.Size(), 1024);
        try { Assert.Equal(expected, Assert.Single(regions).Kind); }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_BothClasses_Both_NoParts_Unknown()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var both = RegenRegion.BuildRegions(mask, [Part(BodyPartDetector.ClsPenis, new Rect(1300, 1700, 200, 400)), Part(BodyPartDetector.ClsPussy, new Rect(1500, 1700, 200, 400))], 0.1, 0.6, mask.Size(), 1024);
        var none = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024);
        try
        {
            Assert.Equal(RegenKind.Both, Assert.Single(both).Kind);
            Assert.Equal(RegenKind.Unknown, Assert.Single(none).Kind);
        }
        finally { foreach (var r in both.Concat(none)) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_OverlappingWindows_Merged()
    {
        // 300px 떨어진 두 조각 — 1024 창이 겹치므로 한 영역으로 (마스크 OR, 경계 합집합)
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(1500, 2000, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024);
        try
        {
            var r = Assert.Single(regions);
            Assert.True(r.Bounds.Contains(new Rect(1000, 2000, 200, 40)) && r.Bounds.Contains(new Rect(1500, 2000, 200, 40)));
            Assert.Equal(255, r.Erase.At<byte>(2020, 1100)); Assert.Equal(255, r.Erase.At<byte>(2020, 1600));
        }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_WindowsTouchButPiecesApart_Separate()
    {
        // 2000px 폭에 조각이 x≈500과 x≈1500 — 1024 창은 48px 겹치지만 어느 조각도 상대 창 안에 안 보인다 → 따로(합치면 창 1.9k → 0.53배 축소로 둘 다 흐려진다, 최종 검수 지적)
        using var mask = MaskWith(new Size(2000, 3000), new Rect(450, 1500, 100, 40), new Rect(1450, 1500, 100, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024);
        try { Assert.Equal(2, regions.Count); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_FarApart_Separate()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(200, 200, 200, 40), new Rect(2500, 3500, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024);
        try { Assert.Equal(2, regions.Count); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_EraseIsOnlyThePiece_RegionIsWider()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024);
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal(200 * 40, Cv2.CountNonZero(r.Erase));
            Assert.True(Cv2.CountNonZero(r.Region) > 200 * 40 * 3);
            Assert.Equal(mask.Size(), r.Region.Size()); Assert.Equal(mask.Size(), r.Erase.Size());
        }
        finally { foreach (var x in regions) x.Dispose(); }
    }
}
