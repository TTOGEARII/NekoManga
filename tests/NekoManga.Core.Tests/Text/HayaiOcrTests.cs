using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>Hayai OCR의 글자 내기 되풀이(<see cref="HayaiOcr.Greedy"/>)·묶음 짜기·캐시에서 끝난 줄 걷어 내기 — 모델 대신 정해 둔 점수를 내는 가짜 단계를 넣어 본다.
/// 줄마다 가장 높은 점수의 토큰을 잇고, 끝 토큰이 나온 줄은 다음 단계부터 돌리지 않는다(원본 _run_batch는 채움 토큰을 넣으며 같이 돌린다 — 남은 줄의 결과는 같다).</summary>
public class HayaiOcrTests
{
    private const int Vocab = 16004;

    /// <summary>줄마다 best[i]번 토큰의 점수가 가장 높은 점수 표.</summary>
    private static float[] Logits(params int[] best)
    {
        var logits = new float[best.Length * Vocab];
        for (int i = 0; i < best.Length; i++) logits[i * Vocab + best[i]] = 5f;
        return logits;
    }

    [Fact]
    public void Greedy_EachRowStopsAtItsOwnEnd_AndFinishedRowsAreNotRunAgain()
    {
        var fed = new List<(long[] Ids, int[] Rows, int Step)>();
        var script = new Queue<float[]>(new[]
        {
            Logits(11, 21, 31),             // 1단계: 세 줄
            Logits(HayaiOcr.Eos, 22, 32),   // 2단계: 첫 줄 끝
            Logits(23, HayaiOcr.Eos),       // 3단계: 남은 두 줄(1·2번) — 셋째 줄 끝
            Logits(HayaiOcr.Eos),           // 4단계: 남은 한 줄(1번) 끝
        });

        var rows = HayaiOcr.Greedy(Logits(10, 20, 30), batch: 3, (ids, active, step) => { fed.Add((ids, active, step)); return script.Dequeue(); });

        Assert.Equal(new[] { 10, 11 }, rows[0].Where(id => id < HayaiOcr.Pad));
        Assert.Equal(new[] { 20, 21, 22, 23 }, rows[1].Where(id => id < HayaiOcr.Pad));
        Assert.Equal(new[] { 30, 31, 32 }, rows[2].Where(id => id < HayaiOcr.Pad));
        // 단계 번호는 1부터. 넘기는 것은 아직 안 끝난 줄뿐 — 그 줄이 바로 앞 단계에서 고른 토큰과 줄 번호
        Assert.Equal(new[] { 1, 2, 3, 4 }, fed.Select(f => f.Step));
        Assert.Equal(new long[] { 10, 20, 30 }, fed[0].Ids);
        Assert.Equal(new[] { 0, 1, 2 }, fed[0].Rows);
        Assert.Equal(new long[] { 11, 21, 31 }, fed[1].Ids);
        Assert.Equal(new[] { 0, 1, 2 }, fed[1].Rows);
        Assert.Equal(new long[] { 22, 32 }, fed[2].Ids);
        Assert.Equal(new[] { 1, 2 }, fed[2].Rows);
        Assert.Equal(new long[] { 23 }, fed[3].Ids);
        Assert.Equal(new[] { 1 }, fed[3].Rows);
        Assert.Empty(script);   // 모든 줄이 끝나면 더 돌리지 않는다
    }

    [Fact]
    public void Greedy_FirstTokenIsTheEnd_RunsNoDecodeStep()
    {
        int calls = 0;
        var rows = HayaiOcr.Greedy(Logits(HayaiOcr.Eos), batch: 1, (_, _, _) => { calls++; return Logits(1); });

        Assert.Equal(0, calls);
        Assert.DoesNotContain(rows[0], id => id < HayaiOcr.Pad);
    }

    [Fact]
    public void Greedy_ARowThatNeverEnds_StopsAtTheTokenLimit()
    {
        // 끝 토큰을 내지 않는 조각(되풀이에 빠진 모델)이 한없이 돌지 않게 — 한도만큼만
        int calls = 0;
        var rows = HayaiOcr.Greedy(Logits(50), batch: 1, (_, _, _) => { calls++; return Logits(50); }, maxNewTokens: 7);

        Assert.Equal(7, rows[0].Count);
        Assert.Equal(6, calls);
    }

    [Fact]
    public void Greedy_PadAsTheBestToken_AlsoEndsTheRow()
    {
        int calls = 0;
        var rows = HayaiOcr.Greedy(Logits(30), batch: 1, (_, _, _) => { calls++; return Logits(HayaiOcr.Pad); });

        Assert.Equal(new[] { 30 }, rows[0].Where(id => id < HayaiOcr.Pad));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MergeCaches_PutsEachCropsVisualTokensFirst_AndItsStartTokenInTheLastSlot()
    {
        // 층 2 · 머리 2 · 차원 1. 조각 A는 시각 토큰 1개(자리 2: 시각·시작), 조각 B는 3개(자리 4). 값 = 조각 × 1000 + 층 × 100 + 머리 × 10 + 자리
        static float[] Cache(int crop, int slots)
        {
            var c = new float[2 * 2 * slots];
            for (int l = 0; l < 2; l++)
            for (int h = 0; h < 2; h++)
            for (int s = 0; s < slots; s++)
                c[(l * 2 + h) * slots + s] = crop * 1000 + l * 100 + h * 10 + s;
            return c;
        }

        var merged = HayaiOcr.MergeCaches([Cache(1, 2), Cache(2, 4)], [1, 3], layers: 2, heads: 2, headDim: 1);

        // [층, 조각, 머리, 자리 4] — 짧은 조각 A: 시각 토큰(자리 0) · 빈 자리 둘(0) · 시작 토큰(원래 자리 1)이 맨 끝으로
        Assert.Equal(new float[]
        {
            1000, 0, 0, 1001,   1010, 0, 0, 1011,   2000, 2001, 2002, 2003,   2010, 2011, 2012, 2013,     // 층 0: A(머리 0·1), B(머리 0·1)
            1100, 0, 0, 1101,   1110, 0, 0, 1111,   2100, 2101, 2102, 2103,   2110, 2111, 2112, 2113,     // 층 1
        }, merged);
    }

    [Fact]
    public void KeepRows_TakesTheKeptRowsOutOfEveryLayer()
    {
        // 캐시 [층 2, 줄 3, 값 4] — 값 = 층 × 100 + 줄 × 10 + 자리. 0번·2번 줄만 남긴다
        var cache = new float[2 * 3 * 4];
        for (int l = 0; l < 2; l++)
        for (int r = 0; r < 3; r++)
        for (int x = 0; x < 4; x++)
            cache[(l * 3 + r) * 4 + x] = l * 100 + r * 10 + x;

        var kept = HayaiOcr.KeepRows(cache, layers: 2, rows: 3, keep: [0, 2]);

        Assert.Equal(new float[] { 0, 1, 2, 3, 20, 21, 22, 23, 100, 101, 102, 103, 120, 121, 122, 123 }, kept);
    }

    [Fact]
    public void Batches_OnePageIsOneBatch_WhateverTheCropSizes()
    {
        // 한 쪽의 조각은 크기가 달라도 한 묶음이다 — 패치 한도(128·192·256·384)마다 따로 묶으면 글자 내기 되풀이가 묶음 수만큼 돈다
        var items = new (int VisTokens, int Patches)[] { (95, 380), (15, 54), (64, 256), (86, 340), (16, 64), (108, 378), (95, 370) };

        var batches = HayaiOcr.Batches(items, batchSize: 32);

        Assert.Equal(new[] { 1, 4, 2, 3, 6, 0, 5 }, Assert.Single(batches));
    }

    [Fact]
    public void Batches_MoreCropsThanTheBatchSize_SplitSmallToLarge()
    {
        // 묶음 크기를 넘으면 작은 것부터(시각 토큰 수 → 패치 수) 나눈다 — 크기가 비슷한 조각끼리 묶여 채움(빈 패치)이 적다. 크기가 같으면 넣은 순서
        var items = new (int VisTokens, int Patches)[] { (95, 380), (15, 54), (64, 256), (86, 340), (16, 64), (108, 378), (95, 370), (95, 380) };

        var batches = HayaiOcr.Batches(items, batchSize: 3);

        Assert.Equal(new[] { new[] { 1, 4, 2 }, new[] { 3, 6, 0 }, new[] { 7, 5 } }, batches);
    }
}
