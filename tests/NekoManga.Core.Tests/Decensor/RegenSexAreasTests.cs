using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>자리별 태그(RegenSexAreas) — 남녀 조각이 한 영역(자른 그림 한 장)에 같이 있을 때 그림을 조각의 성별로 나눠 자리마다 그 성별의 태그만 넣는다.
/// 사용자(2026-11-15) "모든 페이지가 남/여 구별되서 들어가야해": 사용자 원고의 조각 146개 중 97개는 한쪽 근거가 있는데 한쪽 태그를 받는 것은 50개였다 — 태그가 영역 하나에 한 줄이라서.</summary>
public class RegenSexAreasTests
{
    private const byte M = RegenRegion.MaleCode, F = RegenRegion.FemaleCode, B = RegenRegion.BothCode;
    private static AppSettings S(bool perPiece = true, bool bySex = true, string male = "penis", string female = "pussy") =>
        new() { RegenTagsBySex = bySex, RegenTagsPerPiece = perPiece, RegenBodyTagsMale = male, RegenBodyTagsFemale = female };

    private static Mat Map(int w, int h, params (Rect R, byte Code)[] pieces)
    {
        var m = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
        foreach (var (r, code) in pieces) m.Rectangle(r, new Scalar(code), -1);
        return m;
    }

    private static void DisposeAll(List<RegenTagArea>? areas) { if (areas != null) foreach (var a in areas) a.Dispose(); }

    [Fact]
    public void Split_TwoSexes_EachPixelGoesToTheNearestPiece()
    {
        using var map = Map(200, 100, (new Rect(20, 40, 20, 20), M), (new Rect(160, 40, 20, 20), F));   // 남성 조각 x 20~39, 여성 조각 x 160~179 — 가운데는 x≈99.5
        var areas = RegenSexAreas.Split(map, S());
        try
        {
            Assert.NotNull(areas);
            Assert.Equal(["penis", "pussy"], areas!.Select(a => a.Tags));
            Assert.Equal([RegenKind.Male, RegenKind.Female], areas.Select(a => a.Sex));
            var (male, female) = (areas[0].Mask, areas[1].Mask);
            Assert.Equal(map.Size(), male.Size());
            Assert.Equal((255, 0), (male.At<byte>(50, 30), female.At<byte>(50, 30)));     // 남성 조각 위
            Assert.Equal((255, 0), (male.At<byte>(5, 90), female.At<byte>(5, 90)));       // 조각 밖이지만 남성 조각이 더 가깝다
            Assert.Equal((0, 255), (male.At<byte>(95, 110), female.At<byte>(95, 110)));   // 여성 조각이 더 가깝다
            // 자리들이 그림을 빈틈없이, 겹치지 않게 덮는다 — 덮이지 않은 곳은 ComfyUI가 0으로 나눈다(프롬프트 없는 자리)
            using var both = new Mat(); Cv2.BitwiseAnd(male, female, both);
            Assert.Equal(0, Cv2.CountNonZero(both));
            Assert.Equal(200 * 100, Cv2.CountNonZero(male) + Cv2.CountNonZero(female));
        }
        finally { DisposeAll(areas); }
    }

    [Fact]
    public void Split_UndecidedPiece_GetsBothTagsOnItsSide()
    {
        // 여성 0.27 조각 + 근거 없는 조각(시험 원고 3번 — 그 조각은 남성 쪽이었다): 모르는 자리에는 남녀를 같이 넣는다
        using var map = Map(200, 100, (new Rect(20, 40, 20, 20), F), (new Rect(160, 40, 20, 20), B));
        var areas = RegenSexAreas.Split(map, S());
        try
        {
            Assert.NotNull(areas);
            Assert.Equal(["pussy", "penis, pussy"], areas.Select(a => a.Tags));
            Assert.Equal(255, areas[1].Mask.At<byte>(50, 170));
        }
        finally { DisposeAll(areas); }
    }

    [Fact]
    public void Split_ThreeKinds_GiveThreeAreas_MaleFemaleBoth()
    {
        using var map = Map(300, 100, (new Rect(20, 40, 20, 20), F), (new Rect(140, 40, 20, 20), B), (new Rect(260, 40, 20, 20), M));
        var areas = RegenSexAreas.Split(map, S());
        try
        {
            Assert.NotNull(areas);
            Assert.Equal([RegenKind.Male, RegenKind.Female, RegenKind.Both], areas.Select(a => a.Sex));
            Assert.Equal(["penis", "pussy", "penis, pussy"], areas.Select(a => a.Tags));
            Assert.Equal(300 * 100, areas.Sum(a => Cv2.CountNonZero(a.Mask)));
        }
        finally { DisposeAll(areas); }
    }

    [Fact]
    public void Split_OneKindOnly_OrNothing_IsNull()
    {
        using var one = Map(200, 100, (new Rect(20, 40, 20, 20), M), (new Rect(160, 40, 20, 20), M));
        using var empty = Map(200, 100);
        Assert.Null(RegenSexAreas.Split(one, S()));
        Assert.Null(RegenSexAreas.Split(empty, S()));
    }

    [Fact]
    public void Split_AreasWithTheSameTags_AreMerged()
    {
        // 이 장의 여성 칸을 비웠다: 남성 자리와 남녀 자리의 태그가 같아진다 — 나눌 것이 없으면 한 줄 프롬프트로(null), 여성 자리가 따로 있으면 둘로
        using var twoKinds = Map(200, 100, (new Rect(20, 40, 20, 20), M), (new Rect(160, 40, 20, 20), B));
        Assert.Null(RegenSexAreas.Split(twoKinds, S(female: "")));

        using var threeKinds = Map(300, 100, (new Rect(20, 40, 20, 20), M), (new Rect(140, 40, 20, 20), B), (new Rect(260, 40, 20, 20), F));
        var areas = RegenSexAreas.Split(threeKinds, S(female: ""));
        try
        {
            Assert.NotNull(areas);
            Assert.Equal(["penis", ""], areas.Select(a => a.Tags));   // 여성 자리에는 부위 태그 없이 기본 프롬프트만
            Assert.Equal((255, 255, 0), (areas[0].Mask.At<byte>(50, 30), areas[0].Mask.At<byte>(50, 150), areas[0].Mask.At<byte>(50, 270)));
            Assert.Equal(255, areas[1].Mask.At<byte>(50, 270));
        }
        finally { DisposeAll(areas); }
    }

    [Fact]
    public void Pack_PutsTheAreasIntoRedGreenBlue()
    {
        using var map = Map(300, 100, (new Rect(20, 40, 20, 20), M), (new Rect(140, 40, 20, 20), F), (new Rect(260, 40, 20, 20), B));
        var areas = RegenSexAreas.Split(map, S());
        try
        {
            using var packed = RegenSexAreas.Pack(areas!);
            Assert.Equal((MatType.CV_8UC3, map.Size()), (packed.Type(), packed.Size()));
            // OpenCV는 B·G·R 순서 — 첫 자리 = red, 둘째 = green, 셋째 = blue (그래프의 ImageToMask 채널과 같은 순서: RegenSexAreas.Channels)
            Assert.Equal(new Vec3b(0, 0, 255), packed.At<Vec3b>(50, 30));
            Assert.Equal(new Vec3b(0, 255, 0), packed.At<Vec3b>(50, 150));
            Assert.Equal(new Vec3b(255, 0, 0), packed.At<Vec3b>(50, 270));
            Assert.Equal(["red", "green", "blue"], RegenSexAreas.Channels);
        }
        finally { DisposeAll(areas); }

        using var two = Map(200, 100, (new Rect(20, 40, 20, 20), M), (new Rect(160, 40, 20, 20), F));
        var twoAreas = RegenSexAreas.Split(two, S());
        try
        {
            using var packed = RegenSexAreas.Pack(twoAreas!);
            Assert.Equal(new Vec3b(0, 0, 255), packed.At<Vec3b>(50, 30));
            Assert.Equal(new Vec3b(0, 255, 0), packed.At<Vec3b>(50, 170));   // 셋째 채널은 비어 있다
        }
        finally { DisposeAll(twoAreas); }
    }

    private static RegenRegionInfo RegionWithMap(Mat? map, Size img) =>
        new(new Rect(600, 600, 800, 800), new Mat(img, MatType.CV_8UC1, Scalar.Black), new Mat(img, MatType.CV_8UC1, Scalar.Black), RegenKind.Both, RegenKind.Both, null, map);

    [Fact]
    public void For_CutsTheMapToTheWindow_AtTheSendSize()
    {
        var img = new Size(2000, 2000);
        var map = Map(2000, 2000, (new Rect(700, 900, 100, 100), M), (new Rect(1100, 900, 100, 100), F));
        using var region = RegionWithMap(map, img);
        var w = new RegenWindow(new Rect(200, 500, 1000, 1000), 504, 504, 0.504);   // 그림 가운데가 아닌 창 1000 → 504로 줄여 보낸다
        var areas = RegenSexAreas.For(region, w, S());
        try
        {
            Assert.NotNull(areas);
            Assert.All(areas!, a => Assert.Equal(new Size(504, 504), a.Mask.Size()));
            Assert.Equal(255, areas[0].Mask.At<byte>(226, 277));   // 남성 조각의 가운데 (750,950) → 창 안 (550,450) → ×0.504
            Assert.Equal(255, areas[1].Mask.At<byte>(226, 479));   // 여성 조각의 가운데 (1150,950) → (950,450) → ×0.504
            // 두 조각 사이의 경계는 창 좌표로 x≈378(조각 끝 302와 시작 454의 가운데) — 창으로 자르지 않고 그림 전체를 줄였다면 x≈239다
            Assert.Equal((255, 0), (areas[0].Mask.At<byte>(226, 340), areas[1].Mask.At<byte>(226, 340)));
            Assert.Equal((0, 255), (areas[0].Mask.At<byte>(226, 420), areas[1].Mask.At<byte>(226, 420)));
        }
        finally { DisposeAll(areas); }
    }

    [Fact]
    public void For_SettingOff_OrNoMap_IsNull()
    {
        var img = new Size(2000, 2000);
        var w = new RegenWindow(new Rect(500, 500, 1000, 1000), 1000, 1000, 1.0);
        using var mixed = RegionWithMap(Map(2000, 2000, (new Rect(700, 900, 100, 100), M), (new Rect(1200, 900, 100, 100), F)), img);
        using var plain = RegionWithMap(null, img);
        Assert.Null(RegenSexAreas.For(mixed, w, S(perPiece: false)));   // 자리별 태그를 껐다 — 영역 하나에 한 줄(남녀 같이)
        Assert.Null(RegenSexAreas.For(mixed, w, S(bySex: false)));      // 성별 구분 자체를 껐다
        Assert.Null(RegenSexAreas.For(plain, w, S()));                  // 조각의 성별이 전부 같은 영역
    }

    [Fact]
    public void For_OtherSexOutsideTheWindow_IsNull()
    {
        // 지도에는 두 성별이 있지만 이 창에 보이는 것은 한쪽뿐이다(줄여 보내며 사라진 조각도 같다) — 나눌 것이 없다
        var img = new Size(2000, 2000);
        using var region = RegionWithMap(Map(2000, 2000, (new Rect(700, 900, 100, 100), M), (new Rect(1800, 100, 50, 50), F)), img);
        Assert.Null(RegenSexAreas.For(region, new RegenWindow(new Rect(500, 500, 1000, 1000), 1000, 1000, 1.0), S()));
    }

    [Theory]
    [InlineData("penis", "pussy", "자리별 태그(남성·여성)")]
    [InlineData("penis", "", "자리별 태그(남성·부위 태그 없음)")]
    public void Note_SaysWhichTagsGoWhere(string male, string female, string expected)
    {
        using var map = Map(300, 100, (new Rect(20, 40, 20, 20), M), (new Rect(260, 40, 20, 20), F));
        var s = S(male: male, female: female);
        var areas = RegenSexAreas.Split(map, s);
        try { Assert.Equal(expected, RegenSexAreas.Note(areas!, s)); }
        finally { DisposeAll(areas); }
    }
}
