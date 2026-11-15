using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>재생성(SDXL) 시드의 기록·고정 (2026-11-15, 사용자 "이 시드를 저장하는게 따로 있을까?").
/// 재생성이 쓴 시드를 영역 순서대로 마스크 사이드카에 적고(RegenSeeds), '고정'(SeedLock)한 장은 다음 재생성도 그 시드로 돈다.
/// ComfyUI 없이 볼 수 있는 것만 본다 — 사이드카의 값, 시드 고르기, 다른 방식으로 다시 돌려도 기록·고정이 남는지. 실제 생성은 실제 엔진으로 따로 확인한다.</summary>
public class RegenSeedTests
{
    private const int W = 60, H = 40;
    private static readonly PipelineRunner.RedoPageOptions DecensorOnly = new(DecensorAndLetterOnly: true);

    /// <summary>검열 제거(기본 복원)를 켠 설정 — 여기 시험은 '검열 없음'으로 표시한 장만 써서 복원기까지 가지 않는다. LaMa 자리에는 준비 확인만 통과시키는 가짜 파일.</summary>
    private static AppSettings DecensorOn(TempDir t)
    {
        var s = new AppSettings
        {
            EnableTranslate = false, EnableDecensor = true, EnableAutoColor = false, DecensorMode = "Basic",
            DecensorReview = false, DecensorReviewMode = "PerPage", ReuseStoredMasks = true, CollectCorrections = false, OnnxGpu = "Off",
            RuntimeRoot = t.At("runtime"), OutputRoot = "",
        };
        WriteBytesFile(Path.Combine(s.RuntimeRoot, "models", LamaInpainter.ActiveModelFile), 64);
        return s;
    }

    private static PipelineRunner Runner(AppSettings s) => new(s) { DetectorOverride = (_, _) => null };

    /// <summary>한 번 돌려 둔 결과 자리(저장 마스크·pre·base·식자 블록·지난 결과물) — 사이드카에 시드 기록·고정을 넘긴 대로 적어 둔다.</summary>
    private static (string Src, string OutPath) Slot(TempDir t, AppSettings s, string? seeds, bool locked)
    {
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(W, H, seed: 4);
        var sc = MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: true, edited: true, confirmed: true);
        (sc with { RegenSeeds = seeds, SeedLock = locked }).Save(outPath);
        using var baseImg = ColorImage(W, H, seed: 5);
        ImgIo.Write(LetterSidecar.BasePathFor(outPath), baseImg);
        new LetterSidecar(src, W, H, LetterSidecar.BasePathFor(outPath),
            [new TextBlockInfo(5, 5, 50, 30, "あ", "아", 12, false, "#000000")], MaskSidecar.LengthOf(src)).Save(outPath);
        WriteImageFile(outPath, seed: 9);   // 지난 결과물
        return (src, outPath);
    }

    // ── 시드 글 ↔ 목록 ──

    [Fact]
    public void FormatSeeds_JoinsInRegionOrder()
    {
        Assert.Equal("460327666,1277331254", MaskSidecar.FormatSeeds([460327666, 1277331254]));
        Assert.Equal("7", MaskSidecar.FormatSeeds([7]));
        Assert.Null(MaskSidecar.FormatSeeds([]));   // 적을 것이 없으면 '기록 없음'
    }

    [Theory]
    [InlineData("460327666,1277331254", new long[] { 460327666, 1277331254 })]
    [InlineData(" 7 , 8 ", new long[] { 7, 8 })]
    [InlineData("4294967294", new long[] { 4294967294 })]   // 앱이 뽑는 범위의 끝(uint)도 그대로
    [InlineData(null, new long[0])]
    [InlineData("", new long[0])]
    [InlineData("7,x", new long[0])]     // 손으로 망가뜨린 기록은 통째로 없는 것으로 — 반만 맞는 시드로 돌지 않는다
    [InlineData("7,-1", new long[0])]
    [InlineData("7,,8", new long[0])]
    [InlineData("9223372036854775806", new long[] { 9223372036854775806 })]
    [InlineData("9223372036854775807", new long[0])]   // 2단계가 +1을 쓴다 — 넘치는 값은 받지 않는다
    public void ParseSeeds_ReadsBackOnlyAWellFormedList(string? text, long[] expected) =>
        Assert.Equal(expected, MaskSidecar.ParseSeeds(text));

    // ── 사이드카의 값 ──

    [Fact]
    public void Sidecar_SeedRecordAndLock_RoundTrip_AndOldJsonReadsAsNone()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (_, outPath) = Slot(t, s, "460327666,1277331254", locked: true);

        var sc = MaskSidecar.Load(outPath)!;
        Assert.Equal("460327666,1277331254", sc.RegenSeeds);
        Assert.True(sc.SeedLock);

        // v1.9.1까지의 사이드카에는 두 칸이 없다 — 그대로 읽히고 '기록 없음·고정 안 함'이다
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(MaskSidecar.JsonPathFor(outPath)))!.AsObject();
        Assert.True(json.Remove("RegenSeeds")); Assert.True(json.Remove("SeedLock"));
        File.WriteAllText(MaskSidecar.JsonPathFor(outPath), json.ToJsonString());
        var old = MaskSidecar.Load(outPath)!;
        Assert.Null(old.RegenSeeds);
        Assert.False(old.SeedLock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_OverTheSameSource_KeepsTheSeedRecordAndLock(bool freshDetection)
    {
        // 마스크를 다시 쓰는 길(결과 창 저장·검토 확인·새 감지)은 새 레코드를 만든다 — 시드 기록·고정은 그 장의 값이라 따라 지워지면 안 된다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);

        using var again = RectMask(W, H, new Rect(1, 1, 3, 3));
        var written = MaskSidecar.Write(outPath, src, again, null, null, "다시", skip: false, edited: true, confirmed: true, freshDetection: freshDetection);

        Assert.Equal("5,6", written.RegenSeeds);
        Assert.True(written.SeedLock);
        Assert.Equal(written, MaskSidecar.Load(outPath));
    }

    [Fact]
    public void Write_LockWithoutARecord_IsNotCarried()
    {
        // 고정 표시만 남은 사이드카(쓰다 끊김·손으로 고침) — 고정할 시드가 없는 표시를 끌고 다니면 다음에 뽑힌 시드가 말없이 고정된다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, seeds: null, locked: true);

        using var again = RectMask(W, H, new Rect(1, 1, 3, 3));
        var written = MaskSidecar.Write(outPath, src, again, null, null, "다시", skip: false, edited: true, confirmed: true);

        Assert.False(written.SeedLock);
    }

    [Fact]
    public void Write_OldJsonHeldBriefly_StillCarriesTheSeedRecordAndLock()
    {
        // 결과 창이 [지우기] 직전에 마스크를 다시 저장하는 그 순간 json을 백신·동기화가 잠깐 쥐었다 — '기록 없음'으로 읽고 새로 쓰면 고정이 말없이 풀려 새 시드로 돈다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };
        release.Start();

        using var again = RectMask(W, H, new Rect(1, 1, 3, 3));
        var written = MaskSidecar.Write(outPath, src, again, null, null, "다시", skip: false, edited: true, confirmed: true);

        release.Join();
        Assert.Equal("5,6", written.RegenSeeds);
        Assert.True(written.SeedLock);
        Assert.Equal(written, MaskSidecar.Load(outPath));
    }

    [Fact]
    public void Write_OldJsonHeldThroughout_ThrowsBeforeTouchingTheMask()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var maskBefore = File.ReadAllBytes(MaskSidecar.MaskPathFor(outPath));
        using var again = RectMask(W, H, new Rect(1, 1, 3, 3));

        using (new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => MaskSidecar.Write(outPath, src, again, null, null, "다시", skip: false, edited: true, confirmed: true));

        Assert.Equal(maskBefore, File.ReadAllBytes(MaskSidecar.MaskPathFor(outPath)));   // 마스크만 새것이고 json은 옛것인 반쪽 저장을 남기지 않는다
        var sc = MaskSidecar.Load(outPath)!;
        Assert.Equal("5,6", sc.RegenSeeds);
        Assert.True(sc.SeedLock);
    }

    [Fact]
    public void Write_ForAnotherSourceInTheSameSlot_DropsTheSeedRecord()
    {
        // 고정 출력 폴더에서 다른 원고가 같은 자리에 왔다 — 남의 시드로 고정되면 안 된다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (_, outPath) = Slot(t, s, "5,6", locked: true);
        var other = WriteImageFile(t.At("vol2", "01.jpg"), seed: 2, width: W + 4);   // 이름은 같고 길이가 다른 원고

        using var mask = RectMask(W, H, new Rect(1, 1, 3, 3));
        var written = MaskSidecar.Write(outPath, other, mask, null, null, "감지", skip: false, edited: false, confirmed: false);

        Assert.Null(written.RegenSeeds);
        Assert.False(written.SeedLock);
    }

    // ── 시드 고르기 ──

    [Fact]
    public void RegenSeedFor_LockedSeedOfThatRegion_ThenTheSettingsSeed_ThenANewOne()
    {
        var fixedSeed = new AppSettings { RegenSeed = 7 };
        var random = new AppSettings { RegenSeed = -1 };
        long[] locked = [10, 20];

        Assert.Equal(10, PipelineRunner.RegenSeedFor(random, locked, 0));
        Assert.Equal(20, PipelineRunner.RegenSeedFor(fixedSeed, locked, 1));     // 고정이 설정의 시드보다 먼저
        Assert.Equal(7, PipelineRunner.RegenSeedFor(fixedSeed, locked, 2));      // 고정한 것보다 영역이 늘었다 — 나머지는 설정대로
        Assert.Equal(7, PipelineRunner.RegenSeedFor(fixedSeed, null, 0));
        for (int i = 0; i < 20; i++)
            Assert.InRange(PipelineRunner.RegenSeedFor(random, locked, 2), 0, uint.MaxValue - 1L);   // -1 = 새로 뽑는다(2단계가 +1을 쓴다)
        Assert.NotEqual(PipelineRunner.RegenSeedFor(random, null, 0), PipelineRunner.RegenSeedFor(random, null, 0));
    }

    [Fact]
    public void LockedSeedsFor_OnlyALockedPageWithARecord_OfThatSource()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        Assert.Equal(new long[] { 5, 6 }, PipelineRunner.LockedSeedsFor(outPath, src));

        var other = WriteImageFile(t.At("vol2", "01.jpg"), seed: 2, width: W + 4);
        Assert.Null(PipelineRunner.LockedSeedsFor(outPath, other));               // 그 자리의 사이드카가 다른 원고 것

        (MaskSidecar.Load(outPath)! with { SeedLock = false }).Save(outPath);
        Assert.Null(PipelineRunner.LockedSeedsFor(outPath, src));                 // 적혀만 있고 고정하지 않았다

        (MaskSidecar.Load(outPath)! with { SeedLock = true, RegenSeeds = null }).Save(outPath);
        Assert.Null(PipelineRunner.LockedSeedsFor(outPath, src));                 // 고정했는데 기록이 없다(쓰다 끊김) — 고정할 시드가 없다

        Assert.Null(PipelineRunner.LockedSeedsFor(t.At("vol1", "none.png"), src)); // 사이드카가 없는 장
    }

    [Fact]
    public void LockedSeedsFor_JsonHeldBriefly_WaitsAndReadsIt()
    {
        // 결과 창이 막 고정을 적은 json을 백신·동기화가 잠깐 쥐었다 — '고정 안 함'으로 읽으면 고정한 장을 새 시드로 덮는다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };   // 전용 스레드 — 스레드풀은 느린 CI에서 밀린다
        release.Start();

        var seeds = PipelineRunner.LockedSeedsFor(outPath, src);

        release.Join();
        Assert.Equal(new long[] { 5, 6 }, seeds);
    }

    [Fact]
    public void LockedSeedsFor_JsonHeldThroughout_ThrowsInsteadOfPretendingItIsUnlocked()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        using var held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<IOException>(() => PipelineRunner.LockedSeedsFor(outPath, src));
        Assert.Contains("잠시 뒤", ex.Message);
    }

    // ── 2차가 끝난 장의 기록 ──

    private static MaskSidecar Record(string? seeds = "5,6", bool locked = true, bool? colorized = null, bool pending = true) =>
        new("C:/x/01.jpg", W, H, "감지", Skip: false, Edited: true, Confirmed: true, new DateTime(2026, 11, 15, 10, 0, 0), SrcLen: 9,
            Pending: pending, Colorized: colorized, RegenSeeds: seeds, SeedLock: locked);

    [Fact]
    public void FinishedRecord_WritesTheSeedsThisRenderUsed()
    {
        var sc = Record(locked: false);
        var next = PipelineRunner.FinishedRecord(sc, colorized: false, maskReadAt: sc.SavedAt, regenSeeds: [70264590, 24440221]);

        Assert.Equal(sc with { Colorized = false, Pending = false, RegenSeeds = "70264590,24440221" }, next);
    }

    [Fact]
    public void FinishedRecord_LockedPage_KeepsItsSeeds_AndOnlyAppendsNewRegions()
    {
        // 고정한 시드는 사용자가 고른 것이다 — 기록할 때도 바꾸지 않는다(2026-11-15 검수: 도는 사이에 고정하면 그 장이 뽑아 쓴 다른 시드로 고정이 옮겨 갔다)
        var sc = Record(seeds: "5,6", locked: true, colorized: false, pending: false);
        MaskSidecar After(params long[] used) => PipelineRunner.FinishedRecord(sc, colorized: false, maskReadAt: null, regenSeeds: used);

        Assert.Equal(sc, After(5, 6));                                   // 고정한 시드로 그렸다
        Assert.Equal(sc with { RegenSeeds = "5,6,77" }, After(5, 6, 77)); // 영역이 늘었다 — 새 영역의 시드도 고정에 든다
        Assert.Equal(sc, After(5));                                      // 영역이 줄었다 — 남은 시드를 버리지 않는다
        Assert.Equal(sc, After(8, 9));                                   // 도는 사이에 고정했다(이번 그림은 다른 시드) — 고른 시드를 그대로 둔다
        Assert.Equal(sc, After(5, 9, 77));                               // 앞이 다르면 덧붙이지도 않는다
    }

    [Fact]
    public void FinishedRecord_LockWithoutARecord_IsDropped()
    {
        var sc = Record(seeds: null, locked: true, colorized: false, pending: false);
        Assert.Equal(sc with { SeedLock = false }, PipelineRunner.FinishedRecord(sc, colorized: false, maskReadAt: null, regenSeeds: null));
        // 이번에 뽑아 쓴 시드는 적되, 아무도 고르지 않은 시드가 고정되지는 않는다
        Assert.Equal(sc with { SeedLock = false, RegenSeeds = "7" }, PipelineRunner.FinishedRecord(sc, colorized: false, maskReadAt: null, regenSeeds: [7]));
    }

    [Theory]
    [InlineData("5,6", true, new long[] { 8, 9 }, true)]      // 고정했는데 이번 그림은 다른 시드 — 도는 사이에 고정했다
    [InlineData("5,6", true, new long[] { 5, 9 }, true)]
    [InlineData("5,6", true, new long[] { 5, 6 }, false)]
    [InlineData("5,6", true, new long[] { 5 }, false)]        // 영역이 줄었을 뿐
    [InlineData("5,6", true, new long[] { 5, 6, 7 }, false)]  // 영역이 늘었을 뿐
    [InlineData("5,6", false, new long[] { 8, 9 }, false)]    // 고정하지 않은 장
    [InlineData(null, true, new long[] { 8 }, false)]
    [InlineData("5,6", true, null, false)]                    // 재생성이 돌지 않았다
    public void DrawnWithOtherSeeds_OnlyALockedPageWhoseSeedsThisRenderDidNotUse(string? recorded, bool locked, long[]? used, bool expected) =>
        Assert.Equal(expected, PipelineRunner.DrawnWithOtherSeeds(Record(seeds: recorded, locked: locked), used));

    [Fact]
    public void FinishedRecord_RenderWithoutRegeneration_LeavesTheSeedRecordAlone()
    {
        // 기본 복원·커스텀·검열 없음으로 돈 장 — 적힌 것은 '지난 재생성의 시드'로 남는다(고정한 장이 다른 방식으로 한 번 돌았다고 시드를 잃지 않는다)
        var sc = Record();
        Assert.Equal(sc with { Colorized = true }, PipelineRunner.FinishedRecord(sc, colorized: true, maskReadAt: null, regenSeeds: null));
        Assert.Equal(sc with { Colorized = true }, PipelineRunner.FinishedRecord(sc, colorized: true, maskReadAt: null, regenSeeds: []));
    }

    [Fact]
    public void FinishedRecord_MaskSavedMeanwhile_MarksThePagePending()
    {
        var sc = Record(pending: false);
        var next = PipelineRunner.FinishedRecord(sc, colorized: false, maskReadAt: sc.SavedAt.AddSeconds(-5), regenSeeds: null);
        Assert.True(next.Pending);
    }

    [Fact]
    public void StaleCleared_ForgetsTheSeedsOnlyWhenThisRenderUsedOtherOnes()
    {
        // 결과물을 쓰기 직전 — 쓴 뒤의 기록이 실패해도 지난 시드가 새 결과물 옆에 남지 않게
        var sc = Record(locked: false, colorized: false);
        Assert.Equal(sc, PipelineRunner.StaleCleared(sc, colorized: false, regenSeeds: [5, 6]));                          // 같은 시드로 다시 그렸다
        Assert.Equal(sc with { RegenSeeds = null }, PipelineRunner.StaleCleared(sc, colorized: false, regenSeeds: [7]));  // 다른 시드
        Assert.Equal(sc, PipelineRunner.StaleCleared(sc, colorized: false, regenSeeds: null));                            // 재생성이 돌지 않았다
        Assert.Equal(sc with { Colorized = null }, PipelineRunner.StaleCleared(sc, colorized: true, regenSeeds: null));   // 채색 여부는 전과 같이
        var none = Record(seeds: null, locked: false);
        Assert.Equal(none, PipelineRunner.StaleCleared(none, colorized: false, regenSeeds: [7]));                         // 적힌 것이 없으면 내릴 것도 없다
    }

    [Fact]
    public void StaleCleared_LockedPage_NeverLosesItsSeeds()
    {
        // 고정한 장의 시드를 내리면 기록이 실패했을 때 '고정했는데 시드가 없는' 장이 남고, 다음에 뽑힌 시드가 고정된다(2026-11-15 검수)
        var locked = Record(seeds: "5,6", locked: true, colorized: false);
        Assert.Equal(locked, PipelineRunner.StaleCleared(locked, colorized: false, regenSeeds: [7]));
        Assert.Equal(locked, PipelineRunner.StaleCleared(locked, colorized: false, regenSeeds: [5, 6, 7]));
        Assert.Equal(locked with { Colorized = null }, PipelineRunner.StaleCleared(locked, colorized: true, regenSeeds: [7]));
    }

    // ── 다른 방식으로 다시 돌려도 남는다 ──

    [Fact]
    public async Task RedoPageAsync_WithoutRegeneration_KeepsTheSeedRecordAndLock()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var resultBefore = File.ReadAllBytes(outPath);

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        Assert.NotEqual(resultBefore, File.ReadAllBytes(outPath));   // 전제: 결과물은 다시 쓰였다
        var after = MaskSidecar.Load(outPath)!;
        Assert.Equal("5,6", after.RegenSeeds);
        Assert.True(after.SeedLock);
    }

    [Fact]
    public async Task RunAsync_ReusingTheStoredMask_KeepsTheSeedRecordAndLock()
    {
        // [시작]으로 통째로 다시 돌려도 — 저장 마스크를 다시 쓰는 1차·확인 표시가 사이드카를 새로 써도 고정은 남는다
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "5,6", locked: true);

        var log = new ProgressLog();
        using (var runner = Runner(s))
            await runner.RunAsync([src], log, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        var after = MaskSidecar.Load(outPath)!;
        Assert.Equal("5,6", after.RegenSeeds);
        Assert.True(after.SeedLock);
    }

    [Fact]
    public async Task RunAsync_DetectingAfresh_OldJsonHeldThroughout_FailsThePageAndLeavesTheSidecarAlone()
    {
        // 새 감지가 옛 사이드카를 덮으려는데 그 json을 다른 프로그램이 끝내 쥐고 있다 — 고정을 버린 채 덮지 않고 그 장을 멈춘다(마스크도 그대로).
        // 지금은 결과 자리의 주인 확인(그 사이드카를 읽는다)이 먼저 멈춘다 — Write까지 가지 않는다(Write 자신의 같은 약속은 Write_OldJsonHeldThroughout…이 지킨다). 어느 쪽이 막든 결과는 같아야 한다
        using var t = new TempDir(); var s = DecensorOn(t);
        s.ReuseStoredMasks = false; s.DecensorReview = true;
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var maskBefore = File.ReadAllBytes(MaskSidecar.MaskPathFor(outPath));

        var log = new ProgressLog();
        using (new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var runner = new PipelineRunner(s) { DetectorOverride = (img, _) => RectMask(img.Width, img.Height, new Rect(20, 10, 8, 8)) })
            await runner.RunAsync([src], log, CancellationToken.None);

        Assert.Contains("잠시 뒤", log.FinalOf(src).Error);
        Assert.Equal(maskBefore, File.ReadAllBytes(MaskSidecar.MaskPathFor(outPath)));
        var sc = MaskSidecar.Load(outPath)!;
        Assert.Equal("5,6", sc.RegenSeeds);
        Assert.True(sc.SeedLock);
    }

    [Fact]
    public async Task RunAsync_DetectingAfresh_RewritesTheSidecar_ButKeepsTheSeedRecordAndLock()
    {
        // 저장 마스크를 다시 쓰지 않는 설정 — 1차가 새로 감지해 사이드카를 통째로 다시 쓴다(페이지별 검토)
        using var t = new TempDir(); var s = DecensorOn(t);
        s.ReuseStoredMasks = false; s.DecensorReview = true;
        var (src, outPath) = Slot(t, s, "5,6", locked: true);
        var before = MaskSidecar.Load(outPath)!;
        while (DateTime.Now == before.SavedAt) Thread.Sleep(1);
        MaskSidecar? detected = null;

        var log = new ProgressLog();
        using (var runner = new PipelineRunner(s) { DetectorOverride = (img, _) => RectMask(img.Width, img.Height, new Rect(20, 10, 8, 8)) })
        {
            runner.PageWaiting += pg =>
            {
                detected = MaskSidecar.Load(outPath);
                (detected! with { Skip = true }).Save(outPath);   // '검열 없음'으로 확인한다 — 복원기까지 가지 않는다
                runner.ConfirmPage(pg.File);
            };
            await runner.RunAsync([src], log, CancellationToken.None);
        }

        Assert.Null(log.FinalOf(src).Error);
        Assert.NotNull(detected);
        Assert.NotEqual(before.SavedAt, detected!.SavedAt);   // 전제: 1차가 새 감지로 사이드카를 다시 썼다
        Assert.Equal("5,6", detected.RegenSeeds);
        Assert.True(detected.SeedLock);
        var after = MaskSidecar.Load(outPath)!;
        Assert.Equal("5,6", after.RegenSeeds);
        Assert.True(after.SeedLock);
    }
}
