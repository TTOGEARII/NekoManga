using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>RegenRegion.Build — 재생성·커스텀 검열 제거가 다시 그릴 범위: 검열 조각마다 겹치는 부위 상자(여백)로, 없으면 조각을 배율만큼 넓힌 타원으로.</summary>
public class RegenRegionTests
{
    private const int W = 1000, H = 1000;

    private static Mat MaskWith(params Rect[] pieces)
    {
        var m = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in pieces) m.Rectangle(r, Scalar.White, -1);
        return m;
    }

    private static byte At(Mat m, int x, int y) => m.At<byte>(y, x);

    [Fact]
    public void Build_ComponentWithPart_FillsPaddedPartRect()
    {
        using var mask = MaskWith(new Rect(400, 500, 200, 30));
        using var region = RegenRegion.Build(mask, [new Rect(300, 350, 400, 300)], pad: 0.1, growRatio: 0.6);
        // 상자 (300,350,400,300)을 가로 40·세로 30 넓힌 (260,320)~(700,680)이 채워진다
        Assert.Equal(255, At(region, 261, 321)); Assert.Equal(255, At(region, 699, 679));
        Assert.Equal(255, At(region, 261, 679)); Assert.Equal(255, At(region, 699, 321));
        Assert.Equal(0, At(region, 250, 310)); Assert.Equal(0, At(region, 710, 690));
        Assert.Equal(255, At(region, 500, 515));   // 조각 자체도 남는다
    }

    [Fact]
    public void Build_PartNotOverlapping_Ignored()
    {
        using var mask = MaskWith(new Rect(400, 500, 200, 30));
        using var region = RegenRegion.Build(mask, [new Rect(50, 50, 100, 100)], 0.1, 0.6);
        Assert.Equal(0, At(region, 100, 100));     // 겹치지 않는 부위는 다시 그리지 않는다
        Assert.Equal(255, At(region, 500, 515));   // 조각은 확장 타원으로
        Assert.Equal(255, At(region, 500, 440));   // 타원 안쪽(위로 80 넓힘)
    }

    [Fact]
    public void Build_NoParts_FallsBackToExpansion()
    {
        // 조각 (400,500,200,30), 배율 0.6 → 좌우 120, 위아래 max(0.6×30=18, 0.4×200=80)=80 → (280,420)~(720,610)에 내접하는 타원
        using var mask = MaskWith(new Rect(400, 500, 200, 30));
        using var region = RegenRegion.Build(mask, [], 0.1, 0.6);
        Assert.Equal(255, At(region, 500, 515));   // 중심
        Assert.Equal(255, At(region, 500, 430));   // 위쪽 안
        Assert.Equal(255, At(region, 700, 515));   // 오른쪽 끝 근처(타원 장축 위)
        Assert.Equal(0, At(region, 281, 421));     // 사각형 모서리는 타원 밖
        Assert.Equal(0, At(region, 270, 515));     // 사각형 밖
        Assert.Equal(0, At(region, 500, 405));
    }

    [Fact]
    public void Build_VerticalPiece_ExpandsSideways()
    {
        // 세로 막대 (500,300,30,200): 위아래 0.6×200=120, 좌우 max(0.6×30=18, 0.4×200=80)=80 → (420,180)~(610,620)
        using var mask = MaskWith(new Rect(500, 300, 30, 200));
        using var region = RegenRegion.Build(mask, [], 0.1, 0.6);
        Assert.Equal(255, At(region, 440, 400));   // 왼쪽으로 넓힌 안쪽
        Assert.Equal(0, At(region, 410, 400));
        Assert.Equal(255, At(region, 515, 200));
    }

    [Fact]
    public void Build_TwoComponentsOnePart_SingleRegion()
    {
        using var mask = MaskWith(new Rect(400, 500, 80, 30), new Rect(520, 500, 80, 30));
        using var region = RegenRegion.Build(mask, [new Rect(380, 450, 240, 120)], 0.1, 0.6);
        Assert.Equal(255, At(region, 500, 515));   // 두 조각 사이도 채워진다
        using var labels = new Mat();
        Assert.Equal(2, Cv2.ConnectedComponents(region, labels));   // 바탕 + 영역 하나
    }

    [Fact]
    public void Build_EdgeComponent_ClipsToImage()
    {
        using var mask = MaskWith(new Rect(0, 0, 100, 20));
        using var region = RegenRegion.Build(mask, [], 0.1, 0.6);
        Assert.Equal(mask.Size(), region.Size());
        Assert.Equal(255, At(region, 0, 0));
        Assert.Equal(255, At(region, 150, 10));    // 오른쪽으로 60 넓힌 자리
    }

    [Fact]
    public void Build_EmptyMask_ReturnsEmpty()
    {
        using var mask = MaskWith();
        using var region = RegenRegion.Build(mask, [new Rect(10, 10, 50, 50)], 0.1, 0.6);
        Assert.Equal(0, Cv2.CountNonZero(region));
    }

    [Fact]
    public void Build_GrowZero_KeepsPieceOnly()
    {
        using var mask = MaskWith(new Rect(400, 500, 200, 30));
        using var region = RegenRegion.Build(mask, [], 0.1, growRatio: 0);
        Assert.Equal(Cv2.CountNonZero(mask), Cv2.CountNonZero(region));   // 배율 0 = 지금과 같은 마스크
    }


    // ── 종류별 태그 고르기 (재생성 v3) ──

    [Theory]
    [InlineData(RegenKind.Male, "M", "F", "M")]
    [InlineData(RegenKind.Female, "M", "F", "F")]
    [InlineData(RegenKind.Both, "M", "F", "M, F")]
    [InlineData(RegenKind.Unknown, "M", "F", "M, F")]     // 검출이 없으면(확장 경로) 둘 다
    [InlineData(RegenKind.Both, "M", "", "M")]            // 빈 쪽은 뺀다
    [InlineData(RegenKind.Female, "M", "  ", "")]
    public void RegenTags_For_PicksByKind(RegenKind kind, string male, string female, string expected)
    {
        var s = new AppSettings { RegenBodyTagsMale = male, RegenBodyTagsFemale = female };
        Assert.Equal(expected, RegenTags.For(kind, s));
    }
}
