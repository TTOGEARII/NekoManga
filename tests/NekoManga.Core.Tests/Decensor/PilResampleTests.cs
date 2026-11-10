using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>PilResample — Pillow의 Image.resize(NEAREST·BICUBIC)를 같은 정수 연산으로 옮긴 것.
/// 기준값은 Pillow 12.3에서 같은 입력을 리사이즈해 얻은 결과다(모델 입출력이 원본 Aletheia Lens와 같아야 한다).</summary>
public class PilResampleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(256)]
    public void NearestMap_SameSize_IsIdentity(int n)
    {
        Assert.Equal(Enumerable.Range(0, n), PilResample.NearestMap(n, n));
    }

    [Theory]
    [InlineData(4, 2, new[] { 1, 3 })]          // 칸 가운데에서 뽑는다 — OpenCV NEAREST라면 0, 2
    [InlineData(2, 4, new[] { 0, 0, 1, 1 })]
    [InlineData(3, 2, new[] { 0, 2 })]
    [InlineData(5, 3, new[] { 0, 2, 4 })]
    [InlineData(1, 5, new[] { 0, 0, 0, 0, 0 })]
    public void NearestMap_SmallSizes_MatchPillowSampleColumns(int inSize, int outSize, int[] expected)
    {
        Assert.Equal(expected, PilResample.NearestMap(inSize, outSize));
    }

    [Theory]
    [InlineData(1000, 256, 1, 998, 127872)]
    [InlineData(640, 256, 1, 638, 81792)]
    [InlineData(300, 256, 0, 299, 38272)]
    [InlineData(257, 256, 0, 256, 32768)]
    [InlineData(100, 256, 0, 99, 12672)]
    [InlineData(3, 256, 0, 2, 256)]
    public void NearestMap_ModelInputSizes_MatchPillowAndStayMonotonicInRange(int inSize, int outSize, int first, int last, int sum)
    {
        // 복원기는 성분 상자(최소 256, 이미지 크기로 자름)를 256으로 줄이거나 키운다 — 표본 열은 늘 그림 안이고 거꾸로 가지 않는다.
        var map = PilResample.NearestMap(inSize, outSize);

        Assert.Equal(outSize, map.Length);
        Assert.All(map, v => Assert.InRange(v, 0, inSize - 1));
        for (int i = 1; i < map.Length; i++) Assert.True(map[i - 1] <= map[i], $"{i}번째에서 거꾸로 감");
        Assert.Equal((first, last, sum), (map[0], map[^1], map.Sum()));
    }

    [Fact]
    public void Bicubic_SameSize_ReturnsEqualButIndependentCopy()
    {
        // 크기가 같으면 항등 — 다만 호출자가 결과를 고쳐 써도 원본이 바뀌지 않게 사본을 돌려준다.
        var src = new byte[] { 1, 2, 3, 40, 50, 60, 70, 80, 90, 100, 110, 120 };
        var before = (byte[])src.Clone();

        var dst = PilResample.Bicubic(src, 2, 2, 2, 2);

        Assert.Equal(before, dst);
        Assert.NotSame(src, dst);
        dst[0] = 255;
        Assert.Equal(before, src);
    }

    [Theory]
    [InlineData(5, 3, 13, 7)]
    [InlineData(9, 9, 2, 3)]
    [InlineData(256, 256, 37, 300)]
    public void Bicubic_SolidColor_StaysSolid(int w, int h, int outW, int outH)
    {
        // 계수 합이 1이므로 단색은 확대·축소 어느 쪽에서도 단색 그대로다(음수 로브가 있어도 넘치지 않는다).
        var src = new byte[w * h * 3];
        for (int i = 0; i < src.Length; i += 3) { src[i] = 10; src[i + 1] = 200; src[i + 2] = 77; }

        var dst = PilResample.Bicubic(src, w, h, outW, outH);

        Assert.Equal(outW * outH * 3, dst.Length);
        for (int i = 0; i < dst.Length; i += 3)
            Assert.Equal((10, 200, 77), (dst[i], dst[i + 1], dst[i + 2]));
    }

    [Fact]
    public void Bicubic_TwoPixelsToOne_AveragesWithHalfRoundedUp()
    {
        // 대칭인 두 이웃은 가중치가 같다 — (0+255)/2 = 127.5는 Pillow처럼 올림해 128. 채널은 서로 섞이지 않는다.
        var src = new byte[] { 0, 255, 10, 255, 0, 10 };

        Assert.Equal(new byte[] { 128, 128, 10 }, PilResample.Bicubic(src, 2, 1, 1, 1));
        Assert.Equal(new byte[] { 128, 128, 10 }, PilResample.Bicubic(src, 1, 2, 1, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bicubic_DoublingAStep_FollowsPillowCubicAndClipsOvershoot(bool horizontal)
    {
        // a = −0.5 3차 커널: 0→255 계단을 두 배로 늘리면 0, 53, 202, 255 (선형 보간이면 64, 191).
        // 양 끝은 커널의 음수 로브로 0 밑·255 위로 넘치는데 8비트로 잘린다. 가로·세로 패스가 같은 결과를 내야 한다.
        var src = new byte[] { 0, 0, 0, 255, 255, 255 };

        var dst = horizontal ? PilResample.Bicubic(src, 2, 1, 4, 1) : PilResample.Bicubic(src, 1, 2, 1, 4);

        Assert.Equal(new byte[] { 0, 0, 0, 53, 53, 53, 202, 202, 202, 255, 255, 255 }, dst);
    }

    [Fact]
    public void Bicubic_UpscaleBothAxes_MatchesPillowReference()
    {
        // 3×2 → 5×4 (Pillow 12.3 Image.resize(BICUBIC) 결과)
        var src = new byte[] { 0, 0, 0, 255, 128, 0, 40, 40, 40, 200, 10, 90, 255, 255, 255, 0, 60, 250 };
        var before = (byte[])src.Clone();
        var expected = new byte[]
        {
            0, 0, 0, 87, 44, 0, 255, 117, 0, 130, 72, 6, 26, 32, 25,
            41, 0, 16, 126, 61, 31, 255, 154, 53, 120, 90, 73, 19, 35, 86,
            155, 0, 62, 204, 93, 117, 255, 229, 202, 99, 126, 208, 5, 43, 207,
            213, 0, 85, 243, 110, 161, 255, 255, 255, 89, 144, 255, 0, 46, 255,
        };

        var dst = PilResample.Bicubic(src, 3, 2, 5, 4);

        Assert.Equal(expected, dst);
        Assert.Equal(before, src);   // 입력 버퍼는 건드리지 않는다
    }

    [Fact]
    public void Bicubic_DownscaleBothAxes_MatchesPillowReference()
    {
        // 6×4 → 2×3: 축소에서는 커널 폭이 배율만큼 넓어진다(Pillow 12.3 결과)
        var src = new byte[]
        {
            0, 0, 255, 50, 13, 215, 100, 26, 175, 150, 39, 135, 200, 52, 95, 250, 65, 55,
            7, 90, 235, 57, 103, 195, 107, 116, 155, 157, 129, 115, 207, 142, 75, 1, 155, 35,
            14, 180, 215, 64, 193, 175, 114, 206, 135, 164, 219, 95, 214, 232, 55, 8, 245, 15,
            21, 14, 195, 71, 27, 155, 121, 40, 115, 171, 53, 75, 221, 66, 35, 15, 79, 251,
        };
        var expected = new byte[] { 60, 32, 206, 177, 67, 96, 74, 159, 180, 131, 194, 68, 83, 75, 149, 143, 110, 97 };

        Assert.Equal(expected, PilResample.Bicubic(src, 6, 4, 2, 3));
    }
}
