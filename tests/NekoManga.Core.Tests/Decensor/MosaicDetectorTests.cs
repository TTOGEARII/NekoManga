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

    /// <summary>폭이 블록 몇 칸뿐인 모자이크 띠.</summary>
    private static Mat MosaicStrip(int width, int height)
    {
        var img = SolidPage(width, height, gray: 170);
        DrawMosaic(img, new Rect(0, 0, width, height), Block);
        return img;
    }
}
