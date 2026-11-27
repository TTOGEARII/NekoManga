using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>Hayai OCR(v2.7 Supernova)의 입력 만들기(<see cref="HayaiPrep"/>) — 모델 없이 볼 수 있는 부분.
/// 기준값은 전부 원본 파이썬(모델 저장소의 inference_onnx.py)과 Pillow 12에서 뽑았다(2026-11-27, 뽑는 스크립트는 작업 노트 translate-letter.md).
/// 이 값들이 원본과 어긋나면 모델은 학습 때와 다른 크기·다른 픽셀을 받는다 — 오류 없이 읽기만 나빠지므로 여기서 잡는다.</summary>
public class HayaiPrepTests
{
    /// <summary>시험용 그림 — 기준값을 뽑을 때 쓴 것과 같은 식(R·G·B가 서로 다르게 변해 채널·행·열이 뒤바뀌면 드러난다).</summary>
    private static byte[] Pattern(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 3;
            rgb[o] = (byte)((x * 37 + y * 11) % 256);
            rgb[o + 1] = (byte)((x * 5 + y * 71 + 40) % 256);
            rgb[o + 2] = (byte)((x * x + y * 3) % 256);
        }
        return rgb;
    }

    [Theory]
    // 높이, 너비, 패치 한도 → 목표 높이, 목표 너비, 패치 행, 패치 열 (원본 _naflex_target_size)
    [InlineData(400, 100, 128, 336, 96, 21, 6)]
    [InlineData(400, 100, 192, 432, 112, 27, 7)]
    [InlineData(400, 100, 256, 512, 128, 32, 8)]
    [InlineData(400, 100, 384, 608, 160, 38, 10)]
    [InlineData(100, 400, 256, 128, 512, 8, 32)]
    [InlineData(64, 64, 128, 176, 176, 11, 11)]
    [InlineData(64, 64, 384, 256, 256, 16, 16)]      // 4배(MaxScale)에서 막힌다 — 한도를 올려도 더 커지지 않는다
    [InlineData(30, 20, 128, 144, 96, 9, 6)]         // 아주 작은 조각: 4배로는 패치가 64개(MinPatches)에 못 미쳐 둘째 탐색이 더 키운다
    [InlineData(30, 20, 384, 144, 96, 9, 6)]
    [InlineData(900, 240, 256, 480, 128, 30, 8)]     // 큰 조각은 줄인다
    [InlineData(1200, 150, 384, 864, 112, 54, 7)]
    [InlineData(150, 1200, 128, 64, 512, 4, 32)]
    public void TargetSize_MatchesTheReference(int h, int w, int maxPatches, int th, int tw, int nh, int nw)
    {
        Assert.Equal((th, tw, nh, nw), HayaiPrep.TargetSize(h, w, maxPatches));
    }

    [Theory]
    // 높이, 너비 → 고른 패치 한도, 패치 수, 시각 토큰 수 (원본 _select_best_max_patches)
    [InlineData(400, 100, 384, 380, 95)]
    [InlineData(100, 400, 384, 380, 95)]
    [InlineData(64, 64, 256, 256, 64)]
    [InlineData(30, 20, 128, 54, 15)]
    [InlineData(900, 240, 384, 380, 95)]
    [InlineData(1200, 150, 384, 378, 108)]
    [InlineData(150, 1200, 384, 378, 108)]
    [InlineData(500, 480, 384, 380, 100)]
    [InlineData(33, 700, 384, 340, 86)]
    [InlineData(16, 16, 128, 64, 16)]
    [InlineData(2000, 1500, 384, 374, 99)]
    [InlineData(310, 95, 256, 252, 70)]
    [InlineData(187, 422, 384, 377, 105)]
    [InlineData(733, 61, 384, 384, 96)]
    [InlineData(120, 128, 384, 380, 100)]
    public void SelectBudget_MatchesTheReference(int h, int w, int budget, int patches, int visTokens)
    {
        Assert.Equal((budget, patches, visTokens), HayaiPrep.SelectBudget(h, w));
    }

    [Fact]
    public void AaWeights_ShrinkingAveragesNeighbours_GrowingInterpolates()
    {
        // 4칸 → 3칸(줄임: 삼각 필터를 배율만큼 넓힌다), 4칸 → 6칸(늘림: 두 이웃 사이의 선형 보간, 가장자리는 한 칸만)
        float[] shrink = { 0.7f, 0.3f, 0, 0, 0, 0.5f, 0.5f, 0, 0, 0, 0.3f, 0.7f };
        float[] grow =
        {
            1, 0, 0, 0,   0.5f, 0.5f, 0, 0,   0, 0.833333f, 0.166667f, 0,
            0, 0.166667f, 0.833333f, 0,   0, 0, 0.5f, 0.5f,   0, 0, 0, 1,
        };
        AssertClose(shrink, HayaiPrep.AaWeights(3, 4), 1e-5f);
        AssertClose(grow, HayaiPrep.AaWeights(6, 4), 1e-5f);
    }

    [Fact]
    public void PosEmbed_ResamplesTheGridToThePatchGrid()
    {
        // 4×4 격자(값 = 차례 × 0.5 − 3, 차원 2)를 2행 × 3열로 — 원본의 einsum("ih,jw,hwd->ijd")
        var grid = new float[4 * 4 * 2];
        for (int i = 0; i < grid.Length; i++) grid[i] = i * 0.5f - 3f;
        float[] expected = { 0.15714f, 0.65714f, 1.35714f, 1.85714f, 2.55714f, 3.05714f, 6.44286f, 6.94286f, 7.64286f, 8.14286f, 8.84286f, 9.34286f };

        AssertClose(expected, HayaiPrep.PosEmbed(grid, side: 4, dim: 2, nh: 2, nw: 3), 1e-4f);
    }

    [Theory]
    // Pillow Image.resize(BILINEAR)의 바이트 그대로 — 줄임(가로세로), 늘림, 가로는 줄이고 세로는 늘림, 같은 크기
    [InlineData(7, 5, 4, 3, "184902525006965a11d0611f299e0763870ba79116e198243a620c743010b83a1ba64129")]
    [InlineData(4, 3, 9, 7, "002800062900172b01272d013830034832045834066936086f37090232000833001935012937013a3a034a3c045a3e066b40087141090651020c52021d54032d56033e59054e5b065e5d086f5f0a75600b0b6f03117003227204327404437706537907637b09747d0b7a7e0c108d04168e0427900537920548950758970868990a799b0c7f9c0d14ac061aad062baf073bb1074cb4095cb60a6cb80c7dba0e83bb0f16b6061cb7062db9073dbb074ebe095ec00a6ec20c7fc40e85c50f")]
    [InlineData(8, 3, 5, 6, "0f2a00473204823a0dbc411a5d492c123c014a4405854c0ebf531b605b2d175f024f67068a6f0fc4761c657e2e1d8304558b08909311ca9a1e6ba23022a6055aae0995b612cfbd1f70c53125b8065dc00a98c813d2cf2073d732")]
    [InlineData(6, 6, 6, 6, "002800252d014a32046f3709943c10b941190b6f033074045579077a7e0c9f8313c4881c16b6063bbb0760c00a85c50faaca16cfcf1f21fd0946020a6b070d900c12b51119da16222c440c51490d764e109b5315c0581ce55d25378b0f5c9010819513a69a18cb9f1ff0a428")]
    public void Bilinear_GivesTheSameBytesAsPillow(int w, int h, int outW, int outH, string expectedHex)
    {
        var result = HayaiPrep.Bilinear(Pattern(w, h), w, h, outW, outH);

        Assert.Equal(expectedHex, Convert.ToHexString(result).ToLowerInvariant());
    }

    [Fact]
    public void WritePatches_GoesPatchByPatch_RowsThenColumnsThenRgb_ScaledToPlusMinusOne()
    {
        // 32×16 그림 = 16픽셀 패치 둘(가로로). 패치 안은 줄 → 칸 → R·G·B 차례, 값은 v × 2/255 − 1
        var dest = new float[2 * HayaiPrep.PatchDim];
        HayaiPrep.WritePatches(Pattern(32, 16), tw: 32, th: 16, dest);

        AssertClose(new[] { -1f, -0.686275f, -1f, -0.709804f, -0.647059f, -0.992157f }, dest[..6], 1e-5f);        // 첫 패치의 (0,0)·(0,1) 픽셀
        AssertClose(new[] { -0.913725f, -0.129412f, -0.976471f }, dest[48..51], 1e-5f);                          // 첫 패치의 둘째 줄 첫 픽셀(= 그림의 (1,0))
        AssertClose(new[] { -0.372549f, -0.058823f, -1f }, dest[HayaiPrep.PatchDim..(HayaiPrep.PatchDim + 3)], 1e-5f);   // 둘째 패치의 첫 픽셀(= 그림의 (0,16))
        Assert.Equal(0.866667f, dest[^1], 5);                                                                    // 둘째 패치의 마지막 값(= 그림의 (15,31)의 B)
    }

    private static void AssertClose(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"[{i}] 기대 {expected[i]}, 실제 {actual[i]}");
    }
}
