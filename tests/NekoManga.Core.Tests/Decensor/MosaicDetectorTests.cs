using NekoManga.Core.Decensor;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>MosaicDetector.BuildMask(모자이크 격자 판정)·ToMaskedPng.</summary>
public class MosaicDetectorTests
{
    private const int Block = 8, Threshold = 15;

    [Fact]
    public void BuildMask_GridMosaicOnSmoothTone_MarksPatchButNotDistantTone()
    {
        // 피부 같은 매끈한 중간 톤 위의 모자이크 — 평탄한 블록이면서 이웃과 색차(격자 경계)가 있는 곳만 모자이크다.
        // 매끈한 바탕은 블록끼리 색차가 없으므로 모자이크에 바로 붙은 한 칸 테두리 말고는 잡히면 안 된다.
        using var img = SolidPage(256, 256, gray: 170);
        var patch = new Rect(96, 96, 64, 64);
        DrawMosaic(img, patch, Block);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.NotNull(mask);
        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(0, 0, 256, 80)));      // 위쪽 바탕
        Assert.Equal(0, CountIn(mask, new Rect(0, 176, 256, 80)));    // 아래쪽 바탕
        Assert.Equal(0, CountIn(mask, new Rect(0, 0, 80, 256)));      // 왼쪽 바탕
        Assert.Equal(0, CountIn(mask, new Rect(176, 0, 80, 256)));    // 오른쪽 바탕
    }

    [Theory]
    [InlineData("flat")]        // 단색 면: 격자 경계가 없다
    [InlineData("gradient")]    // 완만한 그러데이션: 블록끼리 색차가 경계 기준보다 훨씬 작다
    [InlineData("screentone")]  // 망점: 블록 안이 평탄하지 않다
    [InlineData("tooNarrow")]   // 블록 3칸이 안 되는 폭: 격자를 판정할 수 없다
    public void BuildMask_PageWithoutMosaicGrid_ReturnsNull(string kind)
    {
        using var img = kind switch
        {
            "flat" => SolidPage(256, 256, gray: 170),
            "gradient" => Gradient(256, 256),
            "screentone" => Toned(256, 256),
            _ => MosaicStrip(width: 2 * Block, height: 256),
        };

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.Null(mask);
    }

    [Fact]
    public void BuildMask_LineArtOverFlatTone_IsNotMosaic()
    {
        // 피부·배경을 평탄한 톤으로 칠하고 외곽선을 그은 것은 흔한 원고다 — 격자 패턴이 아니므로 모자이크가 아니다.
        using var img = SolidPage(480, 360, gray: 140);
        Cv2.Line(img, new Point(40, 300), new Point(440, 260), Black, 2);
        Cv2.Line(img, new Point(60, 40), new Point(200, 330), Black, 2);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"선화 {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Theory]
    [InlineData(1, 0, 140)]
    [InlineData(2, 30, 200)]
    [InlineData(2, 90, 100)]
    [InlineData(3, 15, 60)]
    [InlineData(4, 60, 140)]
    [InlineData(4, 120, 100)]
    [InlineData(4, 165, 140)]
    public void BuildMask_ThinAntialiasedLineOverFlatTone_IsNotMosaicAtAnyAngle(int thickness, int angleDeg, byte tone)
    {
        // 원고의 외곽선은 안티에일리어스가 들어가 비스듬히 블록 두세 칸에 걸친다 — 4px 안팎이면 어느 각도로 지나가도
        // 선 너머가 같은 톤으로 이어지므로 격자 경계가 아니다. (고치기 전에는 이 선 하나로 7~9천 px가 잡혔다)
        using var img = SolidPage(480, 360, tone);
        double a = angleDeg * Math.PI / 180;
        int dx = (int)Math.Round(150 * Math.Cos(a)), dy = (int)Math.Round(150 * Math.Sin(a));
        Cv2.Line(img, new Point(240 - dx, 180 - dy), new Point(240 + dx, 180 + dy), Black, thickness, LineTypes.AntiAlias);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"{thickness}px·{angleDeg}° 선 {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Fact]
    public void BuildMask_MosaicBesideLineArtOnSameTone_KeepsMosaicAndDropsLines()
    {
        // 같은 톤 위에 외곽선과 진짜 모자이크가 함께 있는 쪽 — 선만 빠지고 모자이크는 그대로 덮여야 한다.
        using var img = SolidPage(320, 256, gray: 170);
        var patch = new Rect(160, 64, 96, 96);
        DrawMosaic(img, patch, Block);
        Cv2.Line(img, new Point(20, 20), new Point(120, 230), Black, 2);
        Cv2.Line(img, new Point(10, 200), new Point(130, 150), Black, 3);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.NotNull(mask);
        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(0, 0, 140, 256)));   // 선화 쪽
    }

    [Theory]
    [InlineData(12, 3, 5)]
    [InlineData(13, 5, 2)]
    [InlineData(20, 4, 6)]
    public void BuildMask_MosaicCellsMisalignedWithBlockGrid_AreStillFound(int tile, int offX, int offY)
    {
        // 실원고의 모자이크 칸은 감지 블록 격자와 어긋나 칸 경계가 블록을 가른다 — 그 블록은 평탄하지 않아도
        // 너머가 다른 칸(다른 톤)이므로 격자 경계로 세야 한다. 평탄한 블록끼리만 세면 이런 모자이크를 거의 다 놓친다.
        using var img = SolidPage(320, 320, gray: 170);
        var patch = new Rect(96 + offX, 96 + offY, tile * (128 / tile), tile * (128 / tile));
        DrawMosaic(img, patch, tile);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, patch) >= 0.95, $"칸 {tile}px: 모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(0, 0, 64, 64)));
        Assert.Equal(0, CountIn(mask, new Rect(256, 256, 64, 64)));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void BuildMask_MosaicOfBlackAndGrayCellsOnly_IsStillFound(int tile)
    {
        // 칸 톤이 검정·회색 둘뿐인 모자이크 — 고르게 검은 칸은 '평탄'으로 치지 않지만(밝기 20 이하) 선이 지나는 블록도 아니다.
        // 가는 선 건너뛰기가 검은 칸까지 건너뛰면 회색 칸끼리 같은 톤이라 격자가 통째로 빠진다.
        using var img = SolidPage(320, 320, gray: 255);
        var patch = new Rect(96, 96, 128, 128);
        for (int y = 0; y < patch.Height; y += tile)
        for (int x = 0; x < patch.Width; x += tile)
            Cv2.Rectangle(img, new Rect(patch.X + x, patch.Y + y, tile, tile), GrayColor(((x + y) / tile) % 2 == 0 ? 10 : 120), -1);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, patch) >= 0.9, $"칸 {tile}px: 모자이크 덮음 {Coverage(mask, patch):P1}");
    }

    [Fact]
    public void BuildMask_LoneFlatBlockAmidTexture_IsDroppedWhilePatchStays()
    {
        // 그림(망점) 한가운데 우연히 평탄한 블록 하나는 격자가 아니다 — 닫기·열기가 고립 블록을 지운다.
        // 같은 페이지의 진짜 모자이크 조각은 그대로 남아야 한다.
        using var img = Toned(256, 256);
        var patch = new Rect(32, 32, 64, 64);
        DrawMosaic(img, patch, Block);
        var lone = new Rect(184, 184, Block, Block);
        Cv2.Rectangle(img, lone, GrayColor(120), -1);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.NotNull(mask);
        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(160, 160, 56, 56)));
    }

    [Theory]
    [InlineData(7, 0, 140)]      // 고치기 전 9,472px
    [InlineData(8, 30, 140)]     // 3,200px
    [InlineData(8, 45, 200)]     // 9,600px
    [InlineData(10, 75, 100)]    // 4,992px
    [InlineData(12, 120, 140)]   // 7,424px
    [InlineData(12, 150, 200)]   // 5,312px
    public void BuildMask_ThickLineOverFlatTone_IsNotMosaicAtAnyAngle(int thickness, int angleDeg, byte tone)
    {
        // 5px를 넘는 굵은 선은 선이 지나는 블록이 여러 칸 이어져 '선 너머 같은 톤' 확인이 닿지 않는다 — 선 양옆이 후보 띠로 남는다.
        // 격자 근거(경계가 가로·세로 두 방향 다 축에 맞는가)가 없으므로 모자이크가 아니다: 비스듬한 선은 축에 안 맞고, 축에 맞는 선은 한 방향뿐이다.
        using var img = SolidPage(480, 360, tone);
        double a = angleDeg * Math.PI / 180;
        int dx = (int)Math.Round(150 * Math.Cos(a)), dy = (int)Math.Round(150 * Math.Sin(a));
        Cv2.Line(img, new Point(240 - dx, 180 - dy), new Point(240 + dx, 180 + dy), Black, thickness, LineTypes.AntiAlias);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"{thickness}px·{angleDeg}° 굵은 선 {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Theory]
    [InlineData(2, 12, 45)]    // 고치기 전 9,600px
    [InlineData(2, 14, 15)]    // 7,488px
    [InlineData(3, 16, 75)]    // 9,280px
    [InlineData(3, 20, 30)]    // 4,416px
    [InlineData(4, 20, 60)]    // 4,416px
    [InlineData(2, 10, 135)]   // 9,600px
    public void BuildMask_TwoNearbyParallelLinesOverFlatTone_AreNotMosaic(int thickness, int gap, int angleDeg)
    {
        // 가까이 나란한 선 둘 — 선 너머에서 처음 만나는 블록이 또 선이라 한 줄씩은 걸러지던 가는 선도 사이가 후보 띠로 남는다. 격자가 아니다.
        using var img = SolidPage(480, 360, gray: 140);
        double a = angleDeg * Math.PI / 180;
        double ux = Math.Cos(a), uy = Math.Sin(a);
        foreach (int side in new[] { -1, 1 })
        {
            double ox = -uy * gap / 2.0 * side, oy = ux * gap / 2.0 * side;
            Cv2.Line(img, new Point((int)Math.Round(240 - 150 * ux + ox), (int)Math.Round(180 - 150 * uy + oy)),
                new Point((int)Math.Round(240 + 150 * ux + ox), (int)Math.Round(180 + 150 * uy + oy)), Black, thickness, LineTypes.AntiAlias);
        }

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"{gap}px 떨어진 {thickness}px 선 둘({angleDeg}°) {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Theory]
    [InlineData("diagonal")]     // 비스듬한 직선 경계 (고치기 전 384px)
    [InlineData("horizontal")]   // 축에 맞는 직선 경계 — 경계가 한 방향뿐이다 (11,520px)
    [InlineData("disc")]         // 둥근 톤(볼·그림자) (5,184px)
    [InlineData("band")]         // 좁은 띠 톤 — 두 경계가 가까이 나란하다 (17,024px)
    [InlineData("stripes")]      // 가로 줄무늬 톤 — 칸은 나뉘지만 세로 경계가 없다 (36,992px)
    public void BuildMask_BoundaryBetweenFlatTones_IsNotMosaic(string kind)
    {
        // 평탄한 두 톤이 만나는 자리는 블록 평균 색차가 크지만 격자가 아니다(피부와 그림자, 옷과 배경).
        using var img = SolidPage(480, 360, gray: 170);
        switch (kind)
        {
            case "diagonal":
                Cv2.FillConvexPoly(img, new[] { new Point(0, 300), new Point(480, 120), new Point(480, 360), new Point(0, 360) }, GrayColor(100), LineTypes.AntiAlias);
                break;
            case "horizontal":
                Cv2.Rectangle(img, new Rect(0, 180, 480, 180), GrayColor(100), -1);   // 경계가 블록 한가운데(8의 배수가 아닌 줄)를 지난다
                break;
            case "disc":
                Cv2.Circle(img, new Point(240, 180), 90, GrayColor(100), -1, LineTypes.AntiAlias);
                break;
            case "band":
                Cv2.FillConvexPoly(img, Corners(new Point2f(240, 180), new Size2f(400, 20), 25), GrayColor(100), LineTypes.AntiAlias);
                break;
            case "stripes":
                for (int i = 0; i < 12; i++)
                    Cv2.Rectangle(img, new Rect(140, 92 + i * 12, 200, 12), GrayColor(i % 2 == 0 ? 110 : 150), -1);
                break;
        }

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"두 톤의 경계({kind}) {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Fact]
    public void BuildMask_MosaicOnPageWithLargeToneBoundary_KeepsMosaicAndDropsNonGridBlob()
    {
        // 모자이크가 있는 쪽이라도 큰 후보 덩어리 가운데 경계 방향이 축에 맞지 않는 것(비스듬한 띠 톤의 양옆)은 격자가 아니므로 버린다.
        // (고치기 전에는 띠 자리 12,000px 남짓이 모자이크와 함께 잡혔다)
        using var img = SolidPage(480, 360, gray: 170);
        var patch = new Rect(32, 32, 96, 96);
        DrawMosaic(img, patch, Block);
        Cv2.FillConvexPoly(img, Corners(new Point2f(320, 240), new Size2f(300, 20), 25), GrayColor(100), LineTypes.AntiAlias);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.NotNull(mask);
        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(160, 144, 320, 216)));   // 띠 톤 자리
    }

    [Fact]
    public void BuildMask_TinyMosaicFragment_IsKeptOnlyBesideGridEvidence()
    {
        // 근거로 삼는 최소 넓이(작은 쪽에서도 1,000px)에 못 미치는 조각(여기서는 8×24 — 칸 셋, 후보 덩어리 576px)은 경계 픽셀이 적어
        // 혼자서는 격자라고 판정하지 않는다 — 같은 쪽에 근거가 되는 모자이크가 있을 때만 받는다.
        // (실원고의 모자이크는 블록 판정에서 큰 덩어리와 작은 귀퉁이 조각들로 나뉘어 잡힌다. 이런 조각만 있는 쪽은 놓치는 것이 이 판정의 값이다.)
        var tiny = new Rect(320, 240, 8, 24);
        using var alone = SolidPage(480, 360, gray: 170);
        DrawMosaic(alone, tiny, Block);
        using var together = alone.Clone();
        var big = new Rect(32, 32, 96, 96);
        DrawMosaic(together, big, Block);

        using var maskAlone = MosaicDetector.BuildMask(alone, Block, Threshold, grow: 0);
        using var maskTogether = MosaicDetector.BuildMask(together, Block, Threshold, grow: 0);

        Assert.True(maskAlone == null, $"작은 조각 하나뿐인 쪽에서 {CountAll(maskAlone)}px가 잡혔다");
        Assert.True(Coverage(maskTogether, big) >= 0.99, $"큰 모자이크 덮음 {Coverage(maskTogether, big):P1}");
        Assert.True(Coverage(maskTogether, tiny) >= 0.99, $"작은 조각 덮음 {Coverage(maskTogether, tiny):P1}");
    }

    [Theory]
    [InlineData(480, 360, 16, 32, true)]      // 작은 쪽(바닥 1,000px): 후보 덩어리 1,024px
    [InlineData(480, 360, 16, 24, false)]     //   768px
    [InlineData(1500, 2160, 32, 32, true)]    // 324만 px 쪽(쪽 넓이/1800 = 1,800px): 2,048px
    [InlineData(1500, 2160, 24, 32, false)]   //   1,664px
    [InlineData(2040, 2880, 32, 56, true)]    // 큰 쪽(3,000px): 3,200px
    [InlineData(2040, 2880, 40, 40, false)]   //   2,880px — 알려진 한계: 큰 쪽에 이만한 모자이크 하나뿐이면 놓친다 (v1.8.2는 잡았다)
    public void BuildMask_LoneSmallMosaic_IsGridEvidenceOnlyAboveThePageRelativeFloor(int pageW, int pageH, int w, int h, bool found)
    {
        // 혼자 있는 작은 모자이크를 근거로 삼는 최소 넓이는 쪽 크기를 따른다: 쪽 넓이의 1/1800, 많아야 3,000px, 적어도 1,000px.
        // 같은 원고도 줄여서 올린 쪽은 모자이크가 그만큼 작다 — 넓이를 3,000px로 못 박으면 절반 크기 쪽에서 모자이크가 통째로 빠졌다.
        // 여기 값(넓이 기준의 양쪽)을 바꾸려면 MosaicDetector.KeepGridLike 상수의 주석대로 라벨 원고 평가를 다시 돌린다.
        using var img = SolidPage(pageW, pageH, gray: 170);
        var patch = new Rect(200, 160, w, h);
        DrawMosaic(img, patch, Block);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        if (found) Assert.True(Coverage(mask, patch) >= 0.99, $"{pageW}×{pageH} 쪽의 {w}×{h} 모자이크 덮음 {Coverage(mask, patch):P1}");
        else Assert.True(mask == null, $"{pageW}×{pageH} 쪽의 {w}×{h} 모자이크 {CountAll(mask)}px가 혼자서 근거가 됐다");
    }

    [Theory]
    [InlineData(5, true)]     // 축 비율 0.86
    [InlineData(8, false)]    // 0.80 — 큰 덩어리 기준(0.75)은 넘지만 작은 덩어리 기준(0.82)에 못 미친다
    public void BuildMask_LoneSmallMosaicTilted_NeedsStricterAxisShareThanLargeOnes(int degrees, bool found)
    {
        // 3,000px에 못 미치는 덩어리(여기서는 32×32, 칸 16px — 후보 덩어리 2,000px쯤)는 넓이라는 근거가 약해 축 비율을 더 엄하게 본다.
        using var src = SolidPage(480, 400, gray: 170);
        DrawMosaic(src, new Rect(224, 184, 32, 32), 16);
        using var img = Rotated(src, degrees);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        if (found) Assert.True(CountIn(mask, new Rect(232, 192, 16, 16)) == 256, $"{degrees}° 기운 작은 모자이크를 놓쳤다");
        else Assert.True(mask == null, $"{degrees}° 기운 작은 모자이크 {CountAll(mask)}px가 혼자서 근거가 됐다");
    }

    [Theory]
    [InlineData(8, 16, 32, 48, true)]     // 가로·세로 경계의 균형 0.46
    [InlineData(8, 24, 24, 48, false)]    // 0.31 — 세로로 긴 칸 세 줄: 한 방향 경계가 대부분이다
    public void BuildMask_LoneSmallMosaicWithOblongCells_NeedsBothEdgeDirections(int cellW, int cellH, int w, int h, bool found)
    {
        // 작은 덩어리도 가로·세로 경계가 다 있어야 근거가 된다(균형 0.35 이상 — 칸이 몇 개뿐이라 큰 덩어리의 0.45보다 느슨하다).
        using var img = SolidPage(480, 360, gray: 170);
        var patch = new Rect(200, 160, w, h);
        DrawCells(img, patch, cellW, cellH);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        if (found) Assert.True(Coverage(mask, patch) >= 0.99, $"칸 {cellW}×{cellH} 모자이크 덮음 {Coverage(mask, patch):P1}");
        else Assert.True(mask == null, $"칸 {cellW}×{cellH} 모자이크 {CountAll(mask)}px가 혼자서 근거가 됐다");
    }

    [Theory]
    [InlineData(12, "blur3", 0, 0)]
    [InlineData(16, "blur5", 0, 0)]
    [InlineData(12, "blur5", 3, 5)]
    [InlineData(8, "jpeg40", 0, 0)]      // JPEG의 8×8 격자에 맞은 자리 — 압축해도 칸 경계가 그대로 남는다 (어긋난 자리는 아래 '알려진 한계')
    [InlineData(22, "jpeg40", 0, 0)]
    [InlineData(16, "jpeg80", 3, 5)]
    [InlineData(12, "rotate2", 0, 0)]
    [InlineData(16, "rotate6", 0, 0)]    // 축에서 10° 안이면 축에 맞는 경계로 센다
    [InlineData(16, "shrink", 0, 0)]
    [InlineData(22, "enlarge", 0, 0)]
    public void BuildMask_SoftenedMosaic_StillHasGridEvidence(int tile, string variant, int dx, int dy)
    {
        // 실원고의 모자이크는 크기 조절·압축을 거쳐 칸 경계가 흐리고 스캔본은 조금 기울어 있다 — 그래도 경계 대부분은 축에 맞으므로 격자로 받아야 한다.
        // (dx,dy)는 조각 자리를 8px 격자에서 어긋나게 옮기는 양이다.
        using var img = Softened(tile, variant, dx, dy, out var inner);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, inner) >= 0.9, $"칸 {tile}px·{variant}·({dx},{dy}): 모자이크 덮음 {Coverage(mask, inner):P1}");
    }

    [Theory]
    [InlineData(8, "blur3", 0, 0)]       // 축 비율 0.74 — 칸 8px은 3×3 가우시안만으로도 모서리가 둥글어져 기준(0.75) 아래로 간다
    [InlineData(8, "blur5", 0, 0)]       // 0.70
    [InlineData(12, "jpeg60", 3, 5)]     // 0.71 — JPEG 8×8 격자와 어긋난 모자이크는 칸 경계 둘레에 압축 무늬가 생긴다
    [InlineData(12, "jpeg40", 3, 5)]     // 0.63
    [InlineData(16, "jpeg40", 3, 5)]     // 0.70
    [InlineData(16, "rotate14", 0, 0)]   // 0.08 — 10° 넘게 기운 격자는 축에 맞는 경계가 없다
    public void BuildMask_SoftenedMosaicBelowGridEvidence_IsMissed_KnownLimit(int tile, string variant, int dx, int dy)
    {
        // 알려진 한계의 기록이다(바라는 동작이 아니다): 격자 근거 판정은 이 모자이크들을 놓친다 — v1.8.2는 전부 잡았다(대신 톤 위의 선화·톤 경계도 잡았다).
        // 판정을 고쳐 이들을 잡게 되면 이 테스트를 위의 StillHasGridEvidence로 옮긴다. 줄 끝의 수는 후보 덩어리의 축 비율.
        using var img = Softened(tile, variant, dx, dy, out var inner);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(mask == null, $"칸 {tile}px·{variant}·({dx},{dy}): 이제 잡힌다(덮음 {Coverage(mask, inner):P1}) — 한계 기록을 고칠 것");
    }

    [Fact]
    public void BuildMask_LowContrastMosaic_StillCountsCellBoundariesAsEdges()
    {
        // 칸끼리 밝기가 8밖에 안 다른 모자이크(블록 색차 기준 EdgeDelta의 최소) — 그 칸 경계도 '밝기 경계'로 세어야 격자 근거가 된다.
        // 경계로 칠 단차(EdgeStep)를 8보다 크게 올리면 이런 모자이크는 경계가 하나도 없는 덩어리가 되어 근거가 사라진다.
        using var img = SolidPage(480, 360, gray: 170);
        var patch = new Rect(160, 96, 160, 160);
        for (int ty = 0; ty < 10; ty++)
            for (int tx = 0; tx < 10; tx++)
                Cv2.Rectangle(img, new Rect(patch.X + tx * 16, patch.Y + ty * 16, 16, 16), GrayColor((tx + ty) % 2 == 0 ? 140 : 148), -1);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, patch) >= 0.99, $"낮은 대비 모자이크 덮음 {Coverage(mask, patch):P1}");
    }

    [Theory]
    [InlineData("oblongCells", true)]      // 칸이 16×32인 모자이크 — 가로·세로 경계의 균형 0.50
    [InlineData("narrowStripes", false)]   // 폭 64의 가로 줄무늬 톤 — 축 비율 0.96이지만 균형 0.41: 세로 경계는 양 끝뿐이다
    public void BuildMask_LargeBlob_NeedsBalancedEdgeDirections(string kind, bool found)
    {
        // 큰 덩어리의 균형 기준(0.45)의 양쪽. 축에 맞는 그림(칸 테두리·네모 톤·줄무늬)은 대개 한 방향 경계가 훨씬 많다.
        using var img = SolidPage(480, 360, gray: 170);
        Rect area;
        if (kind == "oblongCells") DrawCells(img, area = new Rect(160, 80, 160, 192), 16, 32);
        else
        {
            area = new Rect(200, 92, 64, 160);
            for (int i = 0; i < 10; i++)
                Cv2.Rectangle(img, new Rect(area.X, area.Y + i * 16, area.Width, 16), GrayColor(i % 2 == 0 ? 110 : 150), -1);
        }

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        if (found) Assert.True(Coverage(mask, area) >= 0.99, $"{kind} 덮음 {Coverage(mask, area):P1}");
        else Assert.True(mask == null, $"{kind} {CountAll(mask)}px가 모자이크로 잡혔다");
    }

    [Fact]
    public void BuildMask_BlurredFineMosaicBesideGridEvidence_IsKeptThoughNotEvidenceItself()
    {
        // 큰 덩어리의 축 비율이 근거 기준(0.75)과 버림 기준(0.6) 사이면 근거는 못 되지만 버리지도 않는다 —
        // 흐려진 칸 8px 모자이크(0.69)는 혼자서는 놓치고(알려진 한계), 같은 쪽에 선명한 모자이크가 있으면 함께 잡힌다.
        using var alone = SolidPage(640, 480, gray: 170);
        var blurred = new Rect(320, 240, 160, 160);
        DrawMosaic(alone, blurred, Block);
        using (var roi = new Mat(alone, new Rect(296, 216, 208, 208)))
            Cv2.GaussianBlur(roi, roi, new Size(5, 5), 0);
        using var together = alone.Clone();
        var sharp = new Rect(32, 32, 96, 96);
        DrawMosaic(together, sharp, Block);

        using var maskAlone = MosaicDetector.BuildMask(alone, Block, Threshold, grow: 0);
        using var maskTogether = MosaicDetector.BuildMask(together, Block, Threshold, grow: 0);

        Assert.True(maskAlone == null, $"흐려진 칸 8px 모자이크 {CountAll(maskAlone)}px가 혼자서 근거가 됐다");
        Assert.True(Coverage(maskTogether, sharp) >= 0.99, $"선명한 모자이크 덮음 {Coverage(maskTogether, sharp):P1}");
        Assert.True(Coverage(maskTogether, new Rect(336, 256, 128, 128)) >= 0.9, $"흐려진 모자이크 덮음 {Coverage(maskTogether, new Rect(336, 256, 128, 128)):P1}");
    }

    [Fact]
    public void BuildMask_HalfSlantedToneOutlineBesideGridEvidence_IsDropped()
    {
        // 평행사변형 톤의 테두리 — 가로 두 변은 축에 맞고 기운 두 변은 맞지 않아 축 비율이 0.52다. 버림 기준(0.6) 아래이므로
        // 모자이크가 있는 쪽이어도 버린다(기준을 0.4로 내리면 남는다).
        using var img = SolidPage(640, 480, gray: 170);
        var patch = new Rect(32, 32, 96, 96);
        DrawMosaic(img, patch, Block);
        Cv2.FillConvexPoly(img, new[] { new Point(260, 220), new Point(380, 220), new Point(320, 400), new Point(200, 400) }, GrayColor(100), LineTypes.AntiAlias);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.Equal(0, CountIn(mask, new Rect(176, 192, 232, 240)));   // 평행사변형 자리
    }

    [Fact]
    public void BuildMask_SmallNonGridPiecesBesideGridEvidence_AreKept_KnownLimit()
    {
        // 알려진 한계의 기록이다: 버리는 판정(②)은 3,000px 이상인 덩어리에만 한다 — 작은 조각은 경계 픽셀이 적어 조각마다 거르면
        // 진짜 모자이크의 귀퉁이 조각이 빠진다. 그래서 모자이크가 있는 쪽에서는 격자가 아닌 작은 조각(둥근 톤의 테두리 조각들,
        // 큰 것이 1,344px·축 비율 0.58)도 함께 남는다.
        using var img = SolidPage(640, 480, gray: 170);
        var patch = new Rect(32, 32, 96, 96);
        DrawMosaic(img, patch, Block);
        Cv2.Circle(img, new Point(400, 300), 100, GrayColor(100), -1, LineTypes.AntiAlias);

        using var mask = MosaicDetector.BuildMask(img, Block, Threshold, grow: 0);

        Assert.True(Coverage(mask, patch) >= 0.99, $"모자이크 덮음 {Coverage(mask, patch):P1}");
        Assert.True(CountIn(mask, new Rect(280, 180, 240, 240)) >= 4000, $"둥근 톤 둘레의 작은 조각 {CountIn(mask, new Rect(280, 180, 240, 240))}px");
    }

    [Fact]
    public void BuildMask_SixteenBitImage_DoesNotThrow()
    {
        // 16비트 그림은 ImgIo.Read로는 들어오지 않지만 들어와도 예외로 죽으면 안 된다(v1.8.2는 죽지 않았다).
        // 16비트에서는 소벨 값(16S)이 short.MinValue로 포화하고, 그 값의 Math.Abs(short)가 OverflowException을 던졌다.
        using var img8 = SolidPage(480, 360, gray: 170);
        DrawMosaic(img8, new Rect(96, 96, 160, 160), 16);
        using var img16 = new Mat();
        img8.ConvertTo(img16, MatType.CV_16UC3, 257);

        var ex = Record.Exception(() => { using var mask = MosaicDetector.BuildMask(img16, Block, Threshold, grow: 0); });

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-4)]
    public void BuildMask_BlockSizeBelowTwo_BehavesAsTwo(int blockSize)
    {
        // 블록 크기 2 미만은 2로 올려 쓴다 — 0으로 나누거나 1px 블록(표준편차 0)으로 전부 평탄해지는 일이 없어야 한다.
        using var img = SolidPage(128, 128, gray: 170);
        DrawMosaic(img, new Rect(32, 32, 64, 64), Block);

        using var expected = MosaicDetector.BuildMask(img, 2, Threshold, grow: 0);
        using var actual = MosaicDetector.BuildMask(img, blockSize, Threshold, grow: 0);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        using var diff = new Mat();
        Cv2.Compare(expected, actual, diff, CmpTypes.NE);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    [Fact]
    public void ToMaskedPng_MaskedPixelsBecomeTransparentAndColorsArePreserved()
    {
        // ComfyUI LoadImage 계약: 알파 0 = 복원할 자리, 255 = 보존. 색은 원본 그대로 실려야 한다.
        using var img = new Mat(3, 4, MatType.CV_8UC3);
        using var mask = new Mat(3, 4, MatType.CV_8UC1, Scalar.Black);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                img.Set(y, x, new Vec3b((byte)(x * 60), (byte)(y * 100), (byte)(200 - x * 10 - y)));
        mask.Set(0, 1, (byte)255);
        mask.Set(1, 2, (byte)255);
        mask.Set(2, 3, (byte)192);   // 확실히 켜진 쪽(절반 이상)
        mask.Set(2, 0, (byte)64);    // 확실히 꺼진 쪽(절반 미만)

        var png = MosaicDetector.ToMaskedPng(img, mask);

        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        using var decoded = Cv2.ImDecode(png, ImreadModes.Unchanged);
        Assert.Equal(4, decoded.Channels());
        Assert.Equal(img.Size(), decoded.Size());
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
            {
                var src = img.At<Vec3b>(y, x);
                var got = decoded.At<Vec4b>(y, x);
                Assert.Equal((src.Item0, src.Item1, src.Item2), (got.Item0, got.Item1, got.Item2));
                bool hole = (x, y) is (1, 0) or (2, 1) or (3, 2);
                Assert.Equal(hole ? 0 : 255, got.Item3);
            }
    }

    [Theory]
    [InlineData("grayImage", "img")]     // 그림이 BGR 8비트가 아니다
    [InlineData("smallerMask", "mask")]  // 마스크가 그림보다 작다 — 검사 전에는 버퍼 밖을 읽었다
    [InlineData("largerMask", "mask")]
    [InlineData("colorMask", "mask")]    // 마스크가 한 채널이 아니다
    [InlineData("floatMask", "mask")]
    public void ToMaskedPng_InvalidInput_IsRejectedBeforePointerAccess(string kind, string param)
    {
        // 알파는 포인터로 직접 채운다 — 형식·크기가 어긋난 마스크는 메모리를 망가뜨릴 수 있으므로 먼저 막아야 한다.
        using var img = kind == "grayImage" ? new Mat(64, 64, MatType.CV_8UC1, Scalar.All(128)) : SolidPage(64, 64, gray: 128);
        using var mask = kind switch
        {
            "smallerMask" => new Mat(32, 32, MatType.CV_8UC1, Scalar.All(255)),
            "largerMask" => new Mat(64, 96, MatType.CV_8UC1, Scalar.All(255)),
            "colorMask" => new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255)),
            "floatMask" => new Mat(64, 64, MatType.CV_32FC1, Scalar.All(1)),
            _ => new Mat(64, 64, MatType.CV_8UC1, Scalar.All(255)),
        };

        var ex = Assert.Throws<ArgumentException>(() => MosaicDetector.ToMaskedPng(img, mask));

        Assert.Equal(param, ex.ParamName);
    }

    /// <summary>가로로 아주 완만한 회색 그러데이션(80→150) — 8px 블록 사이 색차가 2 남짓이다.</summary>
    private static Mat Gradient(int w, int h)
    {
        var img = SolidPage(w, h);
        for (int x = 0; x < w; x++)
            Cv2.Rectangle(img, new Rect(x, 0, 1, h), GrayColor(80 + x * 70 / (w - 1)), -1);
        return img;
    }

    private static Mat Toned(int w, int h)
    {
        var img = SolidPage(w, h);
        DrawScreentone(img, new Rect(0, 0, w, h));
        return img;
    }

    /// <summary>칸이 정사각형이 아닌(cellW×cellH) 모자이크 — 톤 규칙은 DrawMosaic과 같다.</summary>
    private static void DrawCells(Mat img, Rect area, int cellW, int cellH)
    {
        for (int ty = 0; ty < area.Height / cellH; ty++)
            for (int tx = 0; tx < area.Width / cellW; tx++)
                Cv2.Rectangle(img, new Rect(area.X + tx * cellW, area.Y + ty * cellH, cellW, cellH), GrayColor(60 + 20 * ((tx * 3 + ty * 5) % 8)), -1);
    }

    /// <summary>그림 가운데를 축으로 degrees만큼 돌린 사본(바이큐빅, 가장자리는 늘려 채움).</summary>
    private static Mat Rotated(Mat src, double degrees)
    {
        var dst = new Mat();
        using var rot = Cv2.GetRotationMatrix2D(new Point2f(src.Cols / 2f, src.Rows / 2f), degrees, 1);
        Cv2.WarpAffine(src, dst, rot, src.Size(), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return dst;
    }

    /// <summary>480×400 톤(170) 위 160×160 모자이크(칸 tile px, 자리 (160+dx, 120+dy))를 variant대로 무르게 만든 그림.
    /// inner = 가장자리(회전·보간으로 어긋나는 한 칸)를 뺀 모자이크 안쪽.
    /// variant: blur3·blur5(가우시안), jpegNN(품질 NN으로 압축했다 풀기), rotateN(N° 회전), shrink(0.75배, 면적 보간), enlarge(1.3배, 바이큐빅).</summary>
    private static Mat Softened(int tile, string variant, int dx, int dy, out Rect inner)
    {
        using var src = SolidPage(480, 400, gray: 170);
        var patch = new Rect(160 + dx, 120 + dy, 160, 160);
        DrawMosaic(src, new Rect(patch.X, patch.Y, tile * (patch.Width / tile), tile * (patch.Height / tile)), tile);
        var img = new Mat();
        double scale = 1;
        if (variant == "blur3") Cv2.GaussianBlur(src, img, new Size(3, 3), 0);
        else if (variant == "blur5") Cv2.GaussianBlur(src, img, new Size(5, 5), 0);
        else if (variant.StartsWith("jpeg"))
        {
            Cv2.ImEncode(".jpg", src, out var jpg, new[] { (int)ImwriteFlags.JpegQuality, int.Parse(variant[4..]) });
            using var decoded = Cv2.ImDecode(jpg, ImreadModes.Color);
            decoded.CopyTo(img);
        }
        else if (variant.StartsWith("rotate"))
        {
            img.Dispose();
            img = Rotated(src, int.Parse(variant[6..]));
        }
        else if (variant == "shrink") { scale = 0.75; Cv2.Resize(src, img, new Size(), scale, scale, InterpolationFlags.Area); }
        else { scale = 1.3; Cv2.Resize(src, img, new Size(), scale, scale, InterpolationFlags.Cubic); }
        inner = new Rect((int)((patch.X + 16) * scale), (int)((patch.Y + 16) * scale), (int)((patch.Width - 32) * scale), (int)((patch.Height - 32) * scale));
        return img;
    }

    /// <summary>폭이 블록 몇 칸뿐인 모자이크 띠.</summary>
    private static Mat MosaicStrip(int width, int height)
    {
        var img = SolidPage(width, height, gray: 170);
        DrawMosaic(img, new Rect(0, 0, width, height), Block);
        return img;
    }
}
