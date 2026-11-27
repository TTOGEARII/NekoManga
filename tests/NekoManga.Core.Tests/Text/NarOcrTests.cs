using System.Security.Cryptography;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>manga-ocr-nar-small(한 번에 다 읽는 일본어 OCR)의 입력 만들기(<see cref="NarPrep"/>)·글자표(<see cref="NarVocab"/>)·GPU 안전장치 — 모델 없이 볼 수 있는 부분.
/// 기준값은 모델 저장소의 preprocess.py와 Pillow 12에서 뽑았다(2026-11-27, `scratchpad/ocr_eval/goldens_nar.py` — 작업 노트 translate-letter.md).
/// 크기 고르기·리사이즈·패치 차례가 원본과 어긋나면 모델은 학습 때와 다른 입력을 받는다 — 오류 없이 읽기만 나빠진다.</summary>
public class NarOcrTests
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
    // 높이, 너비 → 패치 격자(행·열), 리사이즈한 너비·높이 (원본 resized_size) — 14px 패치가 324개를 넘지 않는 가장 큰 배율
    [InlineData(400, 100, 36, 9, 126, 504)]
    [InlineData(100, 400, 9, 36, 504, 126)]
    [InlineData(64, 64, 18, 18, 252, 252)]
    [InlineData(30, 20, 21, 14, 196, 294)]
    [InlineData(900, 240, 34, 9, 126, 472)]
    [InlineData(1200, 150, 48, 6, 84, 672)]
    [InlineData(500, 480, 18, 18, 242, 252)]
    [InlineData(33, 700, 4, 81, 1134, 53)]
    [InlineData(2000, 1500, 20, 15, 210, 280)]
    [InlineData(310, 95, 32, 10, 137, 448)]
    [InlineData(187, 422, 12, 27, 378, 168)]
    [InlineData(1, 1, 18, 18, 252, 252)]
    [InlineData(5, 3000, 1, 324, 4536, 8)]       // 아주 길쭉한 조각: 한 줄에 324칸
    [InlineData(253, 251, 18, 18, 250, 252)]
    public void ResizedSize_MatchesTheReference(int h, int w, int gh, int gw, int rw, int rh)
    {
        Assert.Equal((gh, gw, rw, rh), NarPrep.ResizedSize(h, w));
    }

    [Theory]
    // Pillow Image.resize(BICUBIC)의 바이트(sha256 앞 16자) — 늘림, 줄임, 가로는 늘리고 세로는 그대로에 가까운 길쭉한 조각
    [InlineData(23, 37, "aa58d42d08d10299")]
    [InlineData(411, 173, "e39f6623e291c6eb")]
    [InlineData(40, 500, "5271c5dfab0883c3")]
    public void Resize_GivesTheSameBytesAsPillow(int w, int h, string sha)
    {
        var small = NarPrep.Resize(Pattern(w, h), w, h, out _, out _, out int rw, out int rh);

        Assert.Equal(rw * rh * 3, small.Length);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(small))[..16].ToLowerInvariant());
    }

    [Theory]
    // 그림 → 모델 입력. 값은 원본 preprocess()의 것: 패치 안은 채널 → 줄 → 칸 차례(R 196개, G 196개, B 196개), CLIP 평균·표준편차로 정규화,
    // 리사이즈한 그림은 격자의 왼쪽 위에 놓고 남는 자리는 테두리 픽셀의 가운뎃값으로 채운다
    [InlineData(23, 37, 23, 14, new[] { -1.79226f, -1.30186f, -1.48022f, -1.79226f, -1.79226f }, -1.16453f, -1.66088f, -0.4706f, 19958.363)]
    [InlineData(411, 173, 12, 27, new[] { -1.74847f, -1.09176f, -1.48022f, -1.17913f, -1.58788f }, -1.00395f, 0.68947f, 0.05555f, 34575.635)]
    [InlineData(40, 500, 63, 5, new[] { -1.79226f, -1.24183f, -1.48022f, -1.66088f, -1.76307f }, -1.4127f, -0.58059f, 0.32573f, 34948.583)]
    public void Fill_MatchesTheReferencePreprocessing(int w, int h, int gh, int gw, float[] firstPatch, float secondPatch, float secondRow, float lastValue, double sum)
    {
        var p = new float[NarPrep.Budget * NarPrep.Feat];
        var v = new float[NarPrep.Budget];
        var r = new int[NarPrep.Budget];
        var c = new int[NarPrep.Budget];
        // 앞 조각의 값이 남아 있어도 덮어써야 한다 — 버퍼를 조각마다 다시 쓴다
        Array.Fill(p, 9f); Array.Fill(v, 9f); Array.Fill(r, 9); Array.Fill(c, 9);

        var grid = NarPrep.Fill(Pattern(w, h), w, h, p, v, r, c);

        Assert.Equal((gh, gw), grid);
        int n = gh * gw;
        // 첫 패치의 R[0,0]·G[0,0]·B[0,0]·R[0,1](오른쪽 칸)·R[1,0](아랫줄)
        Close(firstPatch[0], p[0]); Close(firstPatch[1], p[196]); Close(firstPatch[2], p[392]); Close(firstPatch[3], p[1]); Close(firstPatch[4], p[14]);
        Close(secondPatch, p[NarPrep.Feat]);                 // 오른쪽 패치의 첫 값
        Close(secondRow, p[gw * NarPrep.Feat]);              // 아랫줄 첫 패치의 첫 값
        Close(lastValue, p[n * NarPrep.Feat - 1]);           // 마지막 패치의 마지막 값 = 남는 자리(테두리 가운뎃값)의 B
        double total = 0;
        for (int i = 0; i < n * NarPrep.Feat; i++) total += p[i];
        Assert.True(Math.Abs(sum - total) < 0.05, $"합 기대 {sum}, 실제 {total}");
        // 패치 번호 → 줄·칸, 쓰는 패치만 1, 나머지는 전부 0
        Assert.Equal(n, v.Count(x => x == 1f));
        Assert.All(v.Skip(n), x => Assert.Equal(0f, x));
        Assert.Equal((1, 0, gh - 1, gw - 1), (r[gw], c[gw], r[n - 1], c[n - 1]));
        Assert.All(p.Skip(n * NarPrep.Feat), x => Assert.Equal(0f, x));
        Assert.All(r.Skip(n), x => Assert.Equal(0, x));
        Assert.All(c.Skip(n), x => Assert.Equal(0, x));
    }

    private static void Close(float expected, float actual) => Assert.True(Math.Abs(expected - actual) <= 2e-4f, $"기대 {expected}, 실제 {actual}");

    // ── 글자표 ──

    private static NarVocab Vocab(TempDir t, string json)
    {
        File.WriteAllText(t.At("vocab.json"), json);
        return NarVocab.Load(t.At("vocab.json"));
    }

    [Fact]
    public void Vocab_DecodesUpToTheEndMark()
    {
        // 번호 = 글자의 자리, 글자 수(여기서는 4)가 끝 표시. 끝 표시 뒤의 것은 버린다. 한 글자가 UTF-16 두 칸인 한자(𠮷)도 한 번호다
        using var t = new TempDir();
        var vocab = Vocab(t, "{\"schema\":\"x\",\"characters\":[\"あ\",\"ー\",\"𠮷\",\" \"]}");

        Assert.Equal(4, vocab.Eos);
        Assert.Equal("𠮷あ ー", vocab.Decode([2, 0, 3, 1, 4, 0, 0]));
        Assert.Equal("", vocab.Decode([4, 1, 1]));
        Assert.Equal("あー", vocab.Decode([0, 1]));          // 끝 표시가 없으면 전부
    }

    [Fact]
    public void Vocab_IgnoresNumbersOutsideTheTable()
    {
        using var t = new TempDir();
        var vocab = Vocab(t, "{\"characters\":[\"あ\",\"い\"]}");

        Assert.Equal("あい", vocab.Decode([0, 7, -1, 1]));
    }

    [Theory]
    [InlineData("{\"characters\":[]}")]                 // 빈 표
    [InlineData("{\"chars\":[\"あ\"]}")]                 // 다른 꼴의 파일
    [InlineData("<html>Not Found</html>")]             // 내려받다 받은 오류 쪽
    public void Vocab_NotACharacterTable_Throws(string content)
    {
        // 조용히 빈 글을 내지 않고 여기서 멈춘다
        using var t = new TempDir();
        File.WriteAllText(t.At("vocab.json"), content);

        Assert.Throws<InvalidDataException>(() => NarVocab.Load(t.At("vocab.json")));
    }

    // ── GPU 안전장치 ──

    [Fact]
    public void CrossCheck_SameTexts_GpuIsTrusted_AndTheCpuTextsAreReturned()
    {
        var (texts, same) = NarOcr.CrossCheck(() => ["가", "나"], () => ["가", "나"]);

        Assert.True(same);
        Assert.Equal(new[] { "가", "나" }, texts);
    }

    [Fact]
    public void CrossCheck_OneCropDiffers_GpuIsNotTrusted_AndTheCpuTextsAreReturned()
    {
        // GPU가 오류 없이 다른 글을 내는 일이 있다(글자 감지기는 빈 결과를, Hayai의 반정밀도 모델은 되풀이한 구절을 냈다) — 한 조각이라도 다르면 이후 CPU로
        var (texts, same) = NarOcr.CrossCheck(() => ["捨てないで", "あ"], () => ["捨てないで捨てないで", "あ"]);

        Assert.False(same);
        Assert.Equal(new[] { "捨てないで", "あ" }, texts);
    }

    [Fact]
    public void CrossCheck_GpuRunThrows_GpuIsNotTrusted_AndTheCpuTextsAreReturned()
    {
        var (texts, same) = NarOcr.CrossCheck(() => ["가"], () => throw new InvalidOperationException("DML"));

        Assert.False(same);
        Assert.Equal(new[] { "가" }, texts);
    }

    [Fact]
    public void RedoIfDemoted_GpuLostDuringThePage_ReadsThePageAgain()
    {
        // 실측(Hayai, 2026-11-27): GPU가 멈추기 직전의 실행은 오류 없이 0만 돌려줬고 오류는 다음 실행에서야 났다 — 그 쪽의 글이 전부 비었다.
        // 한 쪽을 읽는 사이 GPU에서 내려갔으면 그 쪽을 처음부터 다시 읽는다(이제 CPU로)
        bool gpu = true;
        int runs = 0;

        var texts = NarOcr.RedoIfDemoted(() => gpu, () =>
        {
            runs++;
            if (runs == 1) { gpu = false; return new[] { "!!!!" }; }
            return new[] { "あ" };
        });

        Assert.Equal(2, runs);
        Assert.Equal(new[] { "あ" }, texts);
    }

    [Theory]
    [InlineData(true)]    // GPU로 끝까지 돌았다
    [InlineData(false)]   // 처음부터 CPU였다
    public void RedoIfDemoted_NothingChanged_ReadsOnce(bool gpu)
    {
        int runs = 0;

        var texts = NarOcr.RedoIfDemoted(() => gpu, () => { runs++; return new[] { "あ" }; });

        Assert.Equal(1, runs);
        Assert.Equal(new[] { "あ" }, texts);
    }

    [Theory]
    // 조각 수 → 대조에 쓰는 수: 첫 쪽이 커도 몇 개만 CPU로 다시 읽는다
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(30, 4)]
    public void CheckCount_IsCapped(int crops, int expected)
    {
        Assert.Equal(expected, NarOcr.CheckCount(crops));
    }
}
