using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>[고친 장 모두 다시 렌더링](2026-11-13 사용자 요청)의 근거 — 결과 창이 끝난 장의 마스크를 고쳐 저장하면 사이드카에 Pending이 남고,
/// 그 마스크로 결과물을 다시 쓴 뒤에만 지워진다. 묶음 다시 렌더링(RedoPagesAsync)은 준비를 한 번만 하고, 한 장이 실패해도 나머지를 계속한다.
/// 모델 없이 돈다: 검열 제거를 켠 시험은 '검열 없음'으로 고쳐 둔 장을 쓴다(복원기까지 가지 않는다). LaMa 자리에는 준비 확인만 통과시키는 가짜 파일.</summary>
public class MaskPendingTests
{
    private const int W = 60, H = 40;
    private static readonly PipelineRunner.RedoPageOptions DecensorOnly = new(DecensorAndLetterOnly: true);

    /// <summary>검열 제거(기본 복원)를 켠 설정 — 결과 창·메인 창이 다시 렌더링에 쓰는 것과 같다(채색·번역 없음).</summary>
    private static AppSettings DecensorOn(TempDir t, bool review = false)
    {
        var s = new AppSettings
        {
            EnableTranslate = false, EnableDecensor = true, EnableAutoColor = false, DecensorMode = "Basic",
            DecensorReview = review, DecensorReviewMode = "PerPage", ReuseStoredMasks = true, CollectCorrections = false, OnnxGpu = "Off",
            RuntimeRoot = t.At("runtime"), OutputRoot = "",
        };
        WriteBytesFile(Path.Combine(s.RuntimeRoot, "models", LamaInpainter.ActiveModelFile), 64);
        return s;
    }

    /// <summary>Aletheia 모델 확인을 건너뛰는 러너(시험 이음새) — 여기 시험들은 감지기를 부르지 않는다.</summary>
    private static PipelineRunner Runner(AppSettings s) => new(s) { DetectorOverride = (_, _) => null };

    /// <summary>한 번 돌려 둔 결과 자리(저장 마스크·pre·base·식자 블록·지난 결과물). skip = '이 페이지는 검열 없음' 표시.</summary>
    private static (string Src, string OutPath) Slot(TempDir t, AppSettings s, string name, bool pending, bool skip = true, bool confirmed = true)
    {
        var src = WriteImageFile(t.At("vol1", name), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(W, H, seed: 4);
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip, edited: true, confirmed, pending: pending);
        using var baseImg = ColorImage(W, H, seed: 5);
        ImgIo.Write(LetterSidecar.BasePathFor(outPath), baseImg);
        new LetterSidecar(src, W, H, LetterSidecar.BasePathFor(outPath),
            [new TextBlockInfo(5, 5, 50, 30, "あ", "아", 12, false, "#000000")], MaskSidecar.LengthOf(src)).Save(outPath);
        WriteImageFile(outPath, seed: 9);   // 지난 결과물
        return (src, outPath);
    }

    // ── 사이드카의 표시 ──

    [Fact]
    public void Write_Pending_RoundTrips_AndIsOffUnlessAsked()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, marked) = Slot(t, s, "01.jpg", pending: true);
        Assert.True(MaskSidecar.Load(marked)!.Pending);

        // 러너가 쓰는 마스크(새 감지·검토 결과)는 pending을 넘기지 않는다 — 기본이 켜지면 막 감지한 장이 전부 '고친 장'이 된다
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));
        MaskSidecar.Write(marked, src, mask, null, null, "감지", skip: false, edited: false, confirmed: false);
        Assert.False(MaskSidecar.Load(marked)!.Pending);
    }

    [Fact]
    public void Load_JsonWrittenBeforeTheFlagExisted_IsNotPending()
    {
        // v1.9.1까지의 사이드카에는 Pending이 없다 — 그대로 읽히고 고친 장으로 세지 않는다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);
        var json = File.ReadAllText(MaskSidecar.JsonPathFor(outPath));
        const string field = ""","Pending":true""";
        Assert.Contains(field, json);
        File.WriteAllText(MaskSidecar.JsonPathFor(outPath), json.Replace(field, ""));

        var sc = MaskSidecar.Load(outPath);
        Assert.NotNull(sc);
        Assert.False(sc.Pending);
        Assert.False(MaskSidecar.IsPendingFor(outPath, src));
    }

    [Fact]
    public void IsPendingFor_NeedsTheFlag_TheSameSource_AndThePreImage()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);
        var (src2, plain) = Slot(t, s, "02.jpg", pending: false);
        var other = WriteImageFile(t.At("vol2", "01.jpg"), seed: 3, width: 90, height: 60);   // 이름만 같은 다른 원고(길이가 다르다)

        Assert.True(MaskSidecar.IsPendingFor(outPath, src));
        Assert.False(MaskSidecar.IsPendingFor(plain, src2));                       // 표시 없음
        Assert.False(MaskSidecar.IsPendingFor(outPath, other));                    // 남의 사이드카
        Assert.False(MaskSidecar.IsPendingFor(t.At("vol1", "none.png"), src));     // 사이드카 없음
        File.Delete(MaskSidecar.PrePathFor(outPath));
        Assert.False(MaskSidecar.IsPendingFor(outPath, src));                      // 검열 제거 전 원고가 없으면 다시 렌더링할 수 없다
    }

    // ── 끝난 장인가 — 결과 창은 한 번도 끝나지 않은 장(멈춘 검토 대기·실패한 장)의 마스크 저장을 '고친 장'으로 세지 않는다 ──

    [Fact]
    public void HasResultFor_PngOrAsIsCopy_ButNeverTheSourceItself()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        Assert.False(PipelineRunner.HasResultFor(outPath, src));   // 1차만 돌고 멈춘 장 — 사이드카가 있어도 결과물은 없다
        WriteImageFile(outPath, seed: 9);
        Assert.True(PipelineRunner.HasResultFor(outPath, src));    // png 결과물
        File.Delete(outPath);
        File.Copy(src, PipelineRunner.AsIsPathFor(outPath, src));
        Assert.True(PipelineRunner.HasResultFor(outPath, src));    // '원본 그대로' 복사본

        // 출력 폴더 = 원고 폴더면 '원본 그대로'는 원본 자신이다 — 늘 있는 파일이라 끝났다는 근거가 못 된다
        var beside = Path.Combine(Path.GetDirectoryName(src)!, "01_jpg.png");
        Assert.Equal(src, PipelineRunner.AsIsPathFor(beside, src));
        Assert.False(PipelineRunner.HasResultFor(beside, src));
    }

    // ── 다시 렌더링이 표시를 지운다 — 그 마스크로 결과물을 쓴 뒤에만 ──

    [Fact]
    public async Task RedoPageAsync_PendingMask_IsClearedOnceTheResultIsWritten()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);
        var before = MaskSidecar.Load(outPath)!;
        var resultBefore = File.ReadAllBytes(outPath);

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        Assert.NotEqual(resultBefore, File.ReadAllBytes(outPath));
        Assert.Equal(before with { Pending = false, Colorized = false }, MaskSidecar.Load(outPath));   // 표시를 지우고 채색 여부를 적는다 — 저장 시각·수정됨·확인됨은 그대로
        Assert.False(MaskSidecar.IsPendingFor(outPath, src));
    }

    [Fact]
    public async Task RedoPageAsync_PendingMaskNeverConfirmed_EndsClearedAndConfirmed()
    {
        // 감지가 없던 장(확인된 적 없는 사이드카)에 놓친 검열을 칠해 저장한 경우 — 표시를 지운 뒤 '확인됨'을 찍을 때 표시가 되살아나면 안 된다
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true, confirmed: false);

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        var after = MaskSidecar.Load(outPath)!;
        Assert.False(after.Pending);
        Assert.True(after.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_DecensorOff_KeepsThePendingMark()
    {
        // 검열 제거를 끈 다시 돌리기는 마스크를 쓰지 않는다 — 결과물이 새로 쓰여도 고친 마스크는 아직 반영 전이다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true, skip: false);
        var resultBefore = File.ReadAllBytes(outPath);

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        Assert.NotEqual(resultBefore, File.ReadAllBytes(outPath));   // 전제: 결과물은 다시 쓰였다
        Assert.True(MaskSidecar.IsPendingFor(outPath, src));
    }

    [Fact]
    public async Task RedoPageAsync_Cancelled_KeepsThePendingMark()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);
        using var cts = new CancellationTokenSource(); cts.Cancel();

        using (var runner = Runner(s))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RedoPageAsync(src, new ProgressLog(), cts.Token, options: DecensorOnly));

        Assert.True(MaskSidecar.IsPendingFor(outPath, src));
    }

    /// <summary>보고를 받는 자리에서 한 번 끼어드는 진행 수집기 — 2차가 도는 사이 결과 창이 마스크를 또 저장하거나, [멈추기]를 누르거나, 시간 초과가 나는 상황을 만든다.</summary>
    private sealed class HookedProgress(Func<PageProgress, bool> when, Action then) : IProgress<PageProgress>
    {
        private bool _fired;
        public List<PageProgress> Items { get; } = [];
        public void Report(PageProgress value)
        {
            lock (Items) Items.Add(value);
            if (!_fired && when(value)) { _fired = true; then(); }
        }
    }

    [Fact]
    public async Task RedoPageAsync_MaskSavedAgainWhileRendering_StaysPending()
    {
        // 결과물은 옛 마스크로 만들어졌다 — 그사이 저장한 마스크는 아직 반영 전이니 표시를 지우면 안 된다
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);
        var first = MaskSidecar.Load(outPath)!;
        var progress = new HookedProgress(p => p.Stage == "식자 중이다냥", () =>
        {
            while (DateTime.Now == first.SavedAt) Thread.Sleep(1);   // 저장 시각이 반드시 달라지게
            using var again = RectMask(W, H, new Rect(20, 20, 10, 10));
            MaskSidecar.Write(outPath, src, again, null, null, first.Summary, skip: true, edited: true, confirmed: true, pending: true);
        });

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, progress, CancellationToken.None, options: DecensorOnly);

        Assert.Contains(progress.Items, p => p.Stage == "식자 중이다냥");   // 전제: 끼어드는 자리를 지났다
        Assert.True(MaskSidecar.IsPendingFor(outPath, src));
    }

    [Fact]
    public async Task RedoPageAsync_MaskSavedWithoutTheMarkWhileRendering_EndsMarked()
    {
        // 한 번도 끝나지 않은 장(차례를 기다리거나 도는 중인 장)의 마스크를 결과 창이 고쳐 저장하면 표시 없이 저장한다 — 끝난 장이 아니어서다.
        // 그 저장이 2차가 마스크를 읽은 뒤였으면 결과물은 옛 마스크로 만들어졌다: 끝난 지금 러너가 표시를 켠다(안 켜면 '저장했어요'라 하고 아무 데도 반영되지 않는다 — 2026-11-13 점검 4번)
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: false);
        var first = MaskSidecar.Load(outPath)!;
        var progress = new HookedProgress(p => p.Stage == "식자 중이다냥", () =>
        {
            while (DateTime.Now == first.SavedAt) Thread.Sleep(1);
            using var again = RectMask(W, H, new Rect(20, 20, 10, 10));
            MaskSidecar.Write(outPath, src, again, null, null, first.Summary, skip: true, edited: true, confirmed: true, pending: false);
        });

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, progress, CancellationToken.None, options: DecensorOnly);

        Assert.Contains(progress.Items, p => p.Stage == "식자 중이다냥");   // 전제: 끼어드는 자리를 지났다
        Assert.True(MaskSidecar.IsPendingFor(outPath, src));
    }

    [Fact]
    public async Task RedoPageAsync_NothingSavedMeanwhile_IsNotMarked()
    {
        // 표시는 그사이 저장이 있었을 때만 — 고치지 않은 장을 다시 렌더링했다고 '고친 장'이 되면 안 된다
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: false);

        using (var runner = Runner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        Assert.False(MaskSidecar.Load(outPath)!.Pending);
    }

    [Fact]
    public async Task RunAsync_WithoutColoring_RecordsThatThePageIsNotColorized()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: false);
        Assert.Null(MaskSidecar.Load(outPath)!.Colorized);   // 전제: 옛 사이드카에는 적힌 것이 없다

        var log = new ProgressLog();
        using (var runner = Runner(s))
            await runner.RunAsync([src], log, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        Assert.False(MaskSidecar.Load(outPath)!.Colorized);
    }

    [Fact]
    public void Write_Colorized_RoundTrips_AndOldJsonReadsAsUnknown()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: false);
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));
        MaskSidecar.Write(outPath, src, mask, null, null, "감지", skip: false, edited: false, confirmed: true, colorized: true);
        Assert.True(MaskSidecar.Load(outPath)!.Colorized);

        var json = File.ReadAllText(MaskSidecar.JsonPathFor(outPath));
        var field = "," + '"' + "Colorized" + '"' + ":true";
        Assert.Contains(field, json);
        File.WriteAllText(MaskSidecar.JsonPathFor(outPath), json.Replace(field, ""));   // v1.9.1까지의 json 모양
        Assert.Null(MaskSidecar.Load(outPath)!.Colorized);
    }

    [Fact]
    public async Task RunAsync_ReusesAPendingStoredMask_AndClearsTheMark()
    {
        // 작업을 통째로 다시 돌려도 저장 마스크가 그대로 쓰인다(ReuseStoredMasks) — 그 결과물에는 고친 마스크가 반영됐으니 고친 장에서 빠진다
        using var t = new TempDir(); var s = DecensorOn(t);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true);

        var log = new ProgressLog();
        using (var runner = Runner(s))
            await runner.RunAsync([src], log, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        Assert.Contains(log.Items, p => p.Stage.StartsWith("저장된 마스크"));   // 전제: 새로 감지하지 않고 저장 마스크를 썼다
        Assert.False(MaskSidecar.Load(outPath)!.Pending);
    }

    [Fact]
    public async Task RunAsync_PerPage_MaskSavedWhileWaiting_IsTheOneTheMarkIsClearedAgainst()
    {
        // 검토 대기 중에 결과 창이 저장한 마스크를 확인 때 다시 읽는다 — 표시는 그 사이드카(새 저장 시각)를 기준으로 지워야 한다.
        // 1차가 읽은 옛 저장 시각으로 견주면 방금 반영한 마스크의 표시가 남아, 끝난 장이 '고친 장'으로 또 모인다
        using var t = new TempDir(); var s = DecensorOn(t, review: true);
        var (src, outPath) = Slot(t, s, "01.jpg", pending: true, skip: false, confirmed: false);
        var first = MaskSidecar.Load(outPath)!;

        var log = new ProgressLog();
        using (var runner = Runner(s))
        {
            runner.PageWaiting += pg =>
            {
                while (DateTime.Now == first.SavedAt) Thread.Sleep(1);
                using var again = RectMask(W, H, new Rect(20, 20, 10, 10));
                MaskSidecar.Write(outPath, src, again, null, null, first.Summary, skip: true, edited: true, confirmed: false, pending: true);
                runner.ConfirmPage(pg.File);
            };
            await runner.RunAsync([src], log, CancellationToken.None);
        }

        Assert.Null(log.FinalOf(src).Error);
        Assert.Contains(log.Items, p => p.Stage.StartsWith("검토 대기"));   // 전제: 확인을 기다렸다
        var after = MaskSidecar.Load(outPath)!;
        Assert.False(after.Pending);
        Assert.True(after.Confirmed);
    }

    // ── 묶음 다시 렌더링 ──

    [Fact]
    public async Task RedoPagesAsync_OnePageFails_TheOthersStillRender()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var a = Slot(t, s, "01.jpg", pending: true);
        var b = Slot(t, s, "02.jpg", pending: true);
        var c = Slot(t, s, "03.jpg", pending: true);
        var pre = MaskSidecar.PrePathFor(b.OutPath);
        var preBytes = File.ReadAllBytes(pre);
        File.WriteAllBytes(pre, []);   // 02의 검열 제거 전 원고가 깨졌다
        var bResult = File.ReadAllBytes(b.OutPath);
        var log = new ProgressLog();

        (int Done, int Failed) r;
        using (var runner = Runner(s))
            r = await runner.RedoPagesAsync([a, b, c], log, CancellationToken.None, DecensorOnly);

        Assert.Equal((2, 1), r);
        Assert.Null(log.FinalOf(a.Src).Error);
        Assert.NotNull(log.FinalOf(b.Src).Error);   // 그 장의 끝 줄로 까닭을 알린다
        Assert.Null(log.FinalOf(c.Src).Error);
        Assert.Equal(bResult, File.ReadAllBytes(b.OutPath));   // 실패한 장의 지난 결과물은 그대로
        File.WriteAllBytes(pre, preBytes);
        Assert.Equal([false, true, false], new[] { a, b, c }.Select(p => MaskSidecar.IsPendingFor(p.OutPath, p.Src)));   // 실패한 장만 남는다
    }

    [Fact]
    public async Task RedoPagesAsync_TimeoutStyleCancellation_FailsThatPageOnly()
    {
        // HTTP 타임아웃도 취소 예외 계열이다 — 사용자가 멈춘 것이 아니면 그 장만 실패로 두고 다음 장으로 간다(통째로 멈추면 묶음이 말없이 끝난다)
        using var t = new TempDir(); var s = DecensorOn(t);
        var a = Slot(t, s, "01.jpg", pending: true);
        var b = Slot(t, s, "02.jpg", pending: true);
        var aResult = File.ReadAllBytes(a.OutPath);
        var progress = new HookedProgress(p => p.File == a.Src && p.Stage == "식자 중이다냥", () => throw new TaskCanceledException("시간 초과"));

        (int Done, int Failed) r;
        using (var runner = Runner(s))
            r = await runner.RedoPagesAsync([a, b], progress, CancellationToken.None, DecensorOnly);

        Assert.Equal((1, 1), r);
        Assert.Contains(progress.Items, p => p.File == a.Src && p.Done && p.Error != null);
        Assert.Equal(aResult, File.ReadAllBytes(a.OutPath));
        Assert.Equal([true, false], new[] { a, b }.Select(p => MaskSidecar.IsPendingFor(p.OutPath, p.Src)));
    }

    [Fact]
    public async Task RedoPagesAsync_CancelledBetweenPages_StopsAndLeavesTheRestPending()
    {
        using var t = new TempDir(); var s = DecensorOn(t);
        var a = Slot(t, s, "01.jpg", pending: true);
        var b = Slot(t, s, "02.jpg", pending: true);
        var bResult = File.ReadAllBytes(b.OutPath);
        using var cts = new CancellationTokenSource();
        var progress = new HookedProgress(p => p.Done && p.File == a.Src, cts.Cancel);   // 첫 장이 끝나자마자 [멈추기]

        using (var runner = Runner(s))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RedoPagesAsync([a, b], progress, cts.Token, DecensorOnly));

        Assert.False(MaskSidecar.IsPendingFor(a.OutPath, a.Src));
        Assert.True(MaskSidecar.IsPendingFor(b.OutPath, b.Src));
        Assert.Equal(bResult, File.ReadAllBytes(b.OutPath));
        Assert.DoesNotContain(progress.Items, p => p.File == b.Src && p.Done);   // 멈춘 장을 실패로 보고하지 않는다
    }

    [Fact]
    public async Task RedoPagesAsync_PreparationFails_ThrowsOnceInsteadOfFailingEveryPage()
    {
        // 준비(모델 확인·엔진 깨우기)는 묶음에 한 번 — 장마다 하면 엔진이 안 뜰 때 장 수만큼 한도(4분)를 기다린다. 까닭은 예외 하나로 올라간다
        using var t = new TempDir(); var s = DecensorOn(t);
        File.Delete(Path.Combine(s.RuntimeRoot, "models", LamaInpainter.ActiveModelFile));   // 기본 복원 모델이 없다
        var a = Slot(t, s, "01.jpg", pending: true);
        var b = Slot(t, s, "02.jpg", pending: true);
        var results = new[] { File.ReadAllBytes(a.OutPath), File.ReadAllBytes(b.OutPath) };
        var log = new ProgressLog();

        using (var runner = Runner(s))
            await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RedoPagesAsync([a, b], log, CancellationToken.None, DecensorOnly));

        Assert.DoesNotContain(log.Items, p => p.Done);
        Assert.Equal(results, new[] { File.ReadAllBytes(a.OutPath), File.ReadAllBytes(b.OutPath) });
        Assert.Equal([true, true], new[] { a, b }.Select(p => MaskSidecar.IsPendingFor(p.OutPath, p.Src)));
    }
}
