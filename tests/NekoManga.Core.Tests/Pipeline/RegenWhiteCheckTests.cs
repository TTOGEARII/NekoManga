using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>재생성 결과의 '평평한 흰 조각' 판정(RegenWhiteCheck) — 2026-11-20 사용자 "또 흰색으로 칠해진다": 조각의 3분의 1쯤이 어느 CN 강도에서든 흰 칠로 나와(같은 시드 A/B 두 번),
/// 결과를 재서 흰 조각이 있으면 새 시드로 다시 그린다. 하네스(regen_stats3.py·regenab Measure)와 같은 기준: 조각(8연결 성분, 300px 이상)마다 지운 자리(막대 픽셀)의 ≥235 비율이 0.6 이상.</summary>
public class RegenWhiteCheckTests
{
    private static Mat Window(byte v) => new(300, 300, MatType.CV_8UC3, new Scalar(v, v, v));

    [Fact]
    public void PieceWhiteRatios_WhitePieceIsOne_DrawnPieceIsZero_SpecksAreSkipped()
    {
        using var erase = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black);
        erase.Rectangle(new Rect(20, 20, 60, 30), Scalar.White, -1);     // 위 조각: 결과가 흰 칠
        erase.Rectangle(new Rect(20, 100, 60, 30), Scalar.White, -1);    // 아래 조각: 결과에 그림(회색)
        erase.Rectangle(new Rect(200, 200, 10, 10), Scalar.White, -1);   // 100px 티끌 — 세지 않는다
        using var result = Window(255);
        result.Rectangle(new Rect(20, 100, 60, 30), new Scalar(128, 128, 128), -1);

        var ratios = RegenWhiteCheck.PieceWhiteRatios(result, erase, tight: null);

        Assert.Equal([1.0, 0.0], ratios);
        Assert.Equal(1, RegenWhiteCheck.FlatWhiteCount(ratios));
    }

    [Fact]
    public void PieceWhiteRatios_MeasuresOnlyTheErasedBarPixels_WhenTightMaskIsGiven()
    {
        // 막대만 지우는 설정(RegenEraseTight)에서는 조각 안의 막대 픽셀만 새로 그려진다 — 막대 사이에 남긴 원고(흰 종이)를 흰 칠로 세면 안 된다
        using var erase = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black);
        erase.Rectangle(new Rect(20, 20, 60, 30), Scalar.White, -1);
        using var tight = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black);
        tight.Rectangle(new Rect(20, 20, 30, 30), Scalar.White, -1);     // 왼쪽 절반만 막대
        using var result = Window(255);
        result.Rectangle(new Rect(20, 20, 30, 30), new Scalar(128, 128, 128), -1);   // 막대 자리는 그려졌고 오른쪽 절반(원고)은 흰 종이

        Assert.Equal([0.0], RegenWhiteCheck.PieceWhiteRatios(result, erase, tight));
    }

    [Fact]
    public void PieceWhiteRatios_TightMaskEmptyInsideAPiece_FallsBackToTheWholePiece()
    {
        // 흰 막대·모자이크 조각은 막대 픽셀이 없다(TightenErase가 비운다) — 조각 전체로 잰다
        using var erase = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black);
        erase.Rectangle(new Rect(20, 20, 60, 30), Scalar.White, -1);
        using var tight = new Mat(300, 300, MatType.CV_8UC1, Scalar.Black);
        using var result = Window(255);

        Assert.Equal([1.0], RegenWhiteCheck.PieceWhiteRatios(result, erase, tight));
    }

    [Theory]
    [InlineData(new[] { 0.59, 0.6, 1.0 }, 2)]
    [InlineData(new double[0], 0)]
    public void FlatWhiteCount_CountsRatiosFromPointSixUp(double[] ratios, int expected) =>
        Assert.Equal(expected, RegenWhiteCheck.FlatWhiteCount(ratios));

    [Theory]
    [InlineData(new[] { 0.9 }, new[] { 0.5 }, true)]            // 흰 조각 1 → 0
    [InlineData(new[] { 0.9, 0.9 }, new[] { 0.7, 0.9 }, true)]  // 둘 다 흰 조각 2개, 가장 흰 값은 같지만 다음 조각이 덜 희다 — 바꾼다(검수 2026-11-20: 가장 흰 값만 보면 덜 흰 결과를 버린다)
    [InlineData(new[] { 0.9, 0.9 }, new[] { 0.8, 0.8 }, true)]  // 흰 조각 수가 같으면 가장 흰 조각부터 차례로 견준다
    [InlineData(new[] { 0.9, 0.5 }, new[] { 0.9, 0.7 }, false)] // 가장 흰 값은 같고 다음 조각이 더 희다 — 둔다
    [InlineData(new[] { 0.5 }, new[] { 0.5 }, false)]           // 같으면 바꾸지 않는다
    public void IsBetter_FewerFlatWhitePieces_ThenLessWhitePieceByPiece(double[] best, double[] candidate, bool expected) =>
        Assert.Equal(expected, RegenWhiteCheck.IsBetter(candidate, best));

    [Fact]
    public void RetrySeed_FirstAttemptIsTheSeedItself_LaterOnesDifferAndStayInRange()
    {
        Assert.Equal(7L, RegenWhiteCheck.RetrySeed(7, 0));
        Assert.NotEqual(7L, RegenWhiteCheck.RetrySeed(7, 1));
        Assert.NotEqual(RegenWhiteCheck.RetrySeed(7, 1), RegenWhiteCheck.RetrySeed(7, 2));
        // 같은 첫 시드면 같은 재시도 시드 — 설정에서 시드를 고정해 둔 사람도 같은 결과를 다시 얻는다
        Assert.Equal(RegenWhiteCheck.RetrySeed(7, 2), RegenWhiteCheck.RetrySeed(7, 2));
        // 2단계가 +1을 쓰므로 시드는 uint 범위 안에서 끝이 1 남아야 한다(RegenSeedFor와 같은 범위)
        long near = uint.MaxValue - 1;
        for (int a = 0; a < 6; a++) Assert.InRange(RegenWhiteCheck.RetrySeed(near, a), 0, uint.MaxValue - 1);
    }
}
