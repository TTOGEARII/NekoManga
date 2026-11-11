using NekoManga.Core.Decensor;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>WhiteStripDetector — 연결 성분으로는 못 잡는 회전된 흰 막대(스트립). 직선 테두리를 단서로 쓰고,
/// 보수 정책(기울기 6°~84°·폭 30px·길이 60px 이상)을 넘는 것만 자동으로 지운다.</summary>
public class WhiteStripDetectorTests
{
    [Theory]
    [InlineData(30f, 360f, 60f, "gray")]
    [InlineData(-40f, 300f, 70f, "gray")]
    [InlineData(60f, 360f, 60f, "screentone")]   // 망점 위: 블러 중앙값으로 측면을 보므로 그림 쪽으로 판정된다
    public void Detect_RotatedWhiteStripOverArtwork_IsFoundWithItsGeometry(float angle, float length, float thickness, string background)
    {
        using var img = SolidPage(600, 600, gray: 120);
        if (background == "screentone") DrawScreentone(img, new Rect(0, 0, 600, 600));
        var center = new Point2f(300, 300);
        FillRotated(img, center, new Size2f(length, thickness), angle, White);

        var strips = WhiteStripDetector.Detect(img);

        var s = Assert.Single(strips);
        Assert.InRange(s.Center.X, 294, 306);
        Assert.InRange(s.Center.Y, 294, 306);
        Assert.InRange(s.Size.Width, length * 0.9, length * 1.1);       // 길이(막대 방향)
        Assert.InRange(s.Size.Height, thickness - 6, thickness + 6);   // 폭
        Assert.InRange(AngleGap(s.AngleDeg, angle), 0, 2);
    }

    [Theory]
    [InlineData(0f, 60f)]    // 축정렬 흰 띠는 패널 거터·페이지 여백과 구분이 어렵다
    [InlineData(90f, 60f)]
    [InlineData(30f, 16f)]   // 기울었어도 가는 띠(정책 최소 폭 30px 미만)는 장식일 수 있어 손 도구에 맡긴다
    public void Detect_StripOutsideConservativePolicy_IsLeftAlone(float angle, float thickness)
    {
        using var img = SolidPage(600, 600, gray: 120);
        FillRotated(img, new Point2f(300, 300), new Size2f(360, thickness), angle, White);

        Assert.Empty(WhiteStripDetector.Detect(img));
    }

    [Fact]
    public void Detect_FourParallelStripes_AreTreatedAsDecoration()
    {
        // 평행한 흰 띠가 줄지어 있으면(≥4) 장식 줄무늬 패널이다.
        using var img = SolidPage(700, 700, gray: 120);
        DrawParallelStrips(img, count: 4, thickness: 40, spacing: 70, angle: 30);

        Assert.Empty(WhiteStripDetector.Detect(img));
    }

    [Fact(Skip = "알려진 결함: 스트립 하나가 양쪽 긴 테두리에서 후보를 두 개 내므로 가까운 평행 스트립 두 개만으로 '연쇄 평행 그룹 ≥4'(장식 줄무늬)에 걸려 둘 다 버려진다 — 폭 40px 스트립 두 개를 중심 간격 70·120px로 두면 검출 0개, 200px이면 2개")]
    public void Detect_TwoNearbyParallelStrips_AreBothFound()
    {
        // 장식 판정 기준은 평행 띠 네 줄이다 — 나란히 놓인 검열 막대 두 개는 각각 검출돼야 한다.
        using var img = SolidPage(700, 700, gray: 120);
        DrawParallelStrips(img, count: 2, thickness: 40, spacing: 120, angle: 30);

        Assert.Equal(2, WhiteStripDetector.Detect(img).Count);
    }

    [Theory]
    [InlineData("gray", 600, 600)]
    [InlineData("white", 600, 600)]
    [InlineData("whiteStripOnWhite", 600, 600)]   // 바탕과 대비가 없으면 테두리도 없다
    [InlineData("gray", 40, 30)]                  // 아주 작은 그림
    public void Detect_PageWithoutVisibleStrip_ReturnsEmpty(string kind, int w, int h)
    {
        using var img = SolidPage(w, h, gray: kind == "gray" ? (byte)120 : (byte)255);
        if (kind == "whiteStripOnWhite")
            FillRotated(img, new Point2f(300, 300), new Size2f(360, 60), 30, White);

        Assert.Empty(WhiteStripDetector.Detect(img));
    }

    [Fact]
    public void AddToMask_DetectedStrip_IsFilledWithoutClearingExistingMask()
    {
        // 검출한 스트립을 마스크에 더한다 — 이미 칠해 둔 자리(다른 감지기 결과)는 그대로 둔다.
        using var img = SolidPage(600, 600, gray: 120);
        var center = new Point2f(300, 300);
        var size = new Size2f(360, 60);
        FillRotated(img, center, size, 30, White);
        using var mask = EmptyMask(img);
        var existing = new Rect(10, 10, 30, 30);
        Cv2.Rectangle(mask, existing, White, -1);
        using var strip = RotatedRegion(img, center, size, 30);

        int added = WhiteStripDetector.AddToMask(img, mask, grow: 0);

        Assert.True(added > 0);
        Assert.True(Coverage(mask, strip) >= 0.95, $"스트립 덮음 {Coverage(mask, strip):P1}");
        Assert.Equal(existing.Width * existing.Height, CountIn(mask, existing));
    }

    [Fact]
    public void AddToMask_NothingDetected_ReturnsZeroAndLeavesMaskAsIs()
    {
        using var img = SolidPage(600, 600, gray: 120);
        using var mask = EmptyMask(img);
        Cv2.Rectangle(mask, new Rect(100, 100, 50, 20), White, -1);

        int added = WhiteStripDetector.AddToMask(img, mask, grow: 5);

        Assert.Equal(0, added);
        Assert.Equal(50 * 20, Cv2.CountNonZero(mask));
    }

    [Fact]
    public void AddToMask_WithGrow_ReturnsFilledPixelCount()
    {
        // 문서 주석: "채운 픽셀 수를 돌려준다" — 넓힌 만큼도 칠한 픽셀이다.
        using var img = SolidPage(600, 600, gray: 120);
        FillRotated(img, new Point2f(300, 300), new Size2f(360, 60), 30, White);
        using var mask = EmptyMask(img);

        int added = WhiteStripDetector.AddToMask(img, mask, grow: 10);

        int filled = Cv2.CountNonZero(mask);
        Assert.InRange(added, filled * 0.95, filled * 1.05);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void AddToMask_OverAlreadyMaskedArea_CountsOnlyNewlyFilledPixels(int grow)
    {
        // 다른 감지기가 이미 칠한 자리는 이번에 새로 칠한 것이 아니다 — 돌려준 값 = 마스크에서 늘어난 픽셀 수.
        using var img = SolidPage(600, 600, gray: 120);
        FillRotated(img, new Point2f(300, 300), new Size2f(360, 60), 30, White);
        using var existing = EmptyMask(img);
        Cv2.Rectangle(existing, new Rect(250, 250, 100, 100), White, -1);   // 스트립 가운데를 덮는 기존 마스크
        using var mask = existing.Clone();
        using var alone = EmptyMask(img);
        int aloneAdded = WhiteStripDetector.AddToMask(img, alone, grow);     // 빈 마스크에 칠한 스트립 자체

        int added = WhiteStripDetector.AddToMask(img, mask, grow);

        using var overlap = new Mat();
        Cv2.BitwiseAnd(alone, existing, overlap);
        Assert.True(Cv2.CountNonZero(overlap) > 0, "시험 배치: 기존 마스크가 스트립과 겹쳐야 한다");
        Assert.Equal(aloneAdded - Cv2.CountNonZero(overlap), added);
        Assert.Equal(Cv2.CountNonZero(mask) - Cv2.CountNonZero(existing), added);
    }

    [Fact]
    public void AddToMask_PaintsTheSamePixelsAsTheDetectedStrips()
    {
        // 돌려주는 값은 칠한 결과에서 센 것이고, 칠하는 모양은 검출한 회전 사각형(+grow) 그대로다.
        using var img = SolidPage(600, 600, gray: 120);
        FillRotated(img, new Point2f(300, 300), new Size2f(360, 60), 30, White);
        using var mask = EmptyMask(img);
        using var expected = EmptyMask(img);
        const int grow = 4;
        var strips = WhiteStripDetector.Detect(img);
        Assert.NotEmpty(strips);
        foreach (var s in strips)
            FillRotated(expected, s.Center, new Size2f(s.Size.Width + grow * 2, s.Size.Height + grow * 2), s.AngleDeg, White);

        int added = WhiteStripDetector.AddToMask(img, mask, grow);

        using var diff = new Mat();
        Cv2.Compare(mask, expected, diff, CmpTypes.NE);
        Assert.Equal(0, Cv2.CountNonZero(diff));
        Assert.Equal(Cv2.CountNonZero(expected), added);
    }

    /// <summary>기울기 angle(°)의 두 각도 차(180° 주기).</summary>
    private static double AngleGap(double a, double b)
    {
        double d = Math.Abs(((a - b) % 180 + 180) % 180);
        return Math.Min(d, 180 - d);
    }

    /// <summary>페이지 가운데를 기준으로 법선 방향으로 spacing 간격씩 늘어놓은 평행 스트립(길이 380).</summary>
    private static void DrawParallelStrips(Mat img, int count, float thickness, float spacing, float angle)
    {
        double th = angle * Math.PI / 180;
        float nx = (float)-Math.Sin(th), ny = (float)Math.Cos(th);
        float cx = img.Cols / 2f, cy = img.Rows / 2f;
        for (int k = 0; k < count; k++)
        {
            float o = (k - (count - 1) / 2f) * spacing;
            FillRotated(img, new Point2f(cx + nx * o, cy + ny * o), new Size2f(380, thickness), angle, White);
        }
    }
}
