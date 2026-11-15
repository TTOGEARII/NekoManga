using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>읽지 못한 파일(0바이트·깨진 원고, 깨지거나 잠긴 마스크 사이드카)이 결과를 조용히 바꾸지 않고 그 장의 실패로 멈추는지 — 검열 제거를 켠 흐름까지 돌려 본다.
/// 모델 없이 돈다: 감지는 규칙 감지기(시험 이음새 DetectorOverride)(검은 막대)이고, 기본 복원의 LaMa 자리에는 준비 확인(파일 있음)만 통과시키는 가짜 파일을 둔다.
/// 여기 시험들은 복원기까지 가지 않는다 — 지울 곳이 남은 장이 2차로 가면 가짜 모델을 열다 실패한다(고치기 전 코드가 그 길로 샜다).</summary>
public class PipelineRunnerUnreadableTests
{
    private const int W = 400, H = 300;
    private static readonly Rect Bar = new(120, 130, 160, 40);

    /// <summary>검열 제거(기본 복원)를 켠 사용자 기본 흐름(페이지별 검토·저장 마스크 다시 쓰기) — 학습 재료 모으기는 끈다(%AppData% 아래에 쓴다).</summary>
    private static AppSettings DecensorSettings(TempDir t, string target = "BlackBar")
    {
        var s = new AppSettings
        {
            EnableTranslate = false, EnableDecensor = true, EnableAutoColor = false,
            DecensorMode = "Basic", DecensorTarget = target,
            DecensorReview = true, DecensorReviewMode = "PerPage", ReuseStoredMasks = true,
            CollectCorrections = false, OnnxGpu = "Off",
            RuntimeRoot = t.At("runtime"), OutputRoot = "",
        };
        WriteBytesFile(Path.Combine(s.RuntimeRoot, "models", LamaInpainter.ActiveModelFile), 64);
        return s;
    }

    /// <summary>모델 없이 돌리는 러너 — 감지를 규칙 감지기(검은 막대)로 바꿔 끼운다. '이전 방식' 엔진(규칙 감지기 경로)이 2026-11-12 삭제돼
    /// Aletheia 모델 없이는 감지가 안 되므로 시험 이음새 DetectorOverride를 쓴다.</summary>
    private static PipelineRunner RuleRunner(AppSettings s) =>
        new(s) { DetectorOverride = (img, exclude) => MosaicDetector.BuildMaskForTarget(s, img, exclude, out _) };

    /// <summary>밝은 바탕에 검은 막대 하나가 있는 원고 — 규칙 검은 막대 감지기(시험 이음새)가 그 막대를 찾는다.</summary>
    private static string WriteBarPage(string path, int x = 120)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new Mat(H, W, MatType.CV_8UC3, new Scalar(235, 235, 235));
        img.Rectangle(new Rect(x, 130, 160, 40), Scalar.Black, -1);
        Assert.True(ImgIo.Write(path, img));
        return path;
    }

    /// <summary>원고를 한 번 돌려 둔 결과 자리 — 검열을 지운 결과물과, 그 막대를 칠한 마스크·검열 제거 전 원고 사이드카.</summary>
    private static string WriteRedoSlot(AppSettings s, string src, bool skip = false)
    {
        var outPath = PipelineRunner.OutputPathFor(s, src);
        WriteImageFile(outPath, seed: 5, width: W, height: H);
        using var mask = RectMask(W, H, Bar);
        using var pre = ImgIo.Read(src);
        MaskSidecar.Write(outPath, src, mask, mask, pre, "검은 막대", skip, edited: true, confirmed: false);
        return outPath;
    }

    /// <summary>마스크 PNG를 그림으로 못 읽게 만든다 — 0바이트(쓰다 끊김) 또는 앞부분만 남은 PNG.</summary>
    private static void BreakMaskPng(string outPath, string how)
    {
        var path = MaskSidecar.MaskPathFor(outPath);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, how == "zero" ? [] : bytes[..(bytes.Length * 2 / 3)]);
        Assert.Null(MaskSidecar.LoadMask(outPath));   // 전제: 파일은 있는데 그림으로 못 읽는다
    }

    /// <summary>폴더 아래 파일 이름과 바이트 전부 — 아무것도 쓰거나 지우지 않았는지 통째로 비교한다.</summary>
    private static (string[] Names, byte[][] Bytes) Snapshot(string root)
    {
        var names = FilesUnder(root);
        return (names, names.Select(n => File.ReadAllBytes(Path.Combine(root, n))).ToArray());
    }

    private static void AssertUnchanged((string[] Names, byte[][] Bytes) before, string root)
    {
        var now = Snapshot(root);
        Assert.Equal(before.Names, now.Names);
        Assert.Equal(before.Bytes, now.Bytes);
    }

    // ── 결과 창의 '채색부터 다시' ──

    [Theory]
    [InlineData("zero")]        // 결과 창의 마스크 저장이 제자리 쓰기 중에 끊겨 0바이트로 남았다
    [InlineData("truncated")]   // 앞부분만 남은 PNG
    public async Task RedoPageAsync_StoredMaskPngUnreadable_StopsWithoutTouchingResultOrConfirmation(string how)
    {
        // 못 읽은 마스크를 '마스크 없음'으로 넘기면 2차가 검열 제거를 건너뛰고 검열 제거 전 원고로 결과물을 덮은 뒤 '확인됨'까지 찍었다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src);
        BreakMaskPng(outPath, how);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);

        using (var runner = RuleRunner(s))
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None));
            Assert.Contains(".mask.png", ex.Message);
        }

        AssertUnchanged(before, outDir);   // 결과물·사이드카 모두 그대로
        Assert.False(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Theory]
    [InlineData(true, true)]     // '검열 없음'으로 표시한 장 — 지울 곳이 없다
    [InlineData(false, false)]   // 검열 제거를 끄고 다시 돌린다
    public async Task RedoPageAsync_UnreadableMaskThatWouldNotBeUsed_StillRedoes(bool skip, bool decensor)
    {
        // 마스크를 쓰지 않는 다시 돌리기까지 못 읽은 마스크로 막지는 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        s.EnableDecensor = decensor;
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip);
        BreakMaskPng(outPath, "zero");
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
            await runner.RedoPageAsync(src, log, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        using var pre = ImgIo.Read(src);
        using var result = ImgIo.Read(outPath);
        AssertSamePixels(pre, result);   // 지울 곳이 없으니 검열 제거 전 원고가 그대로 결과가 된다
        Assert.True(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_PrePngUnreadable_FailsWithReadableMessageAndKeepsResult()
    {
        // 검열 제거 전 원고(.pre.png)가 0바이트면 마스크 크기 맞추기(Resize 0×0)의 OpenCV 단언문이 그대로 보였다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src);
        File.WriteAllBytes(MaskSidecar.PrePathFor(outPath), []);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);

        using (var runner = RuleRunner(s))
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None));
            Assert.Equal("이미지를 읽지 못했어요: 01.pre.png", ex.Message);
        }

        AssertUnchanged(before, outDir);
        Assert.False(MaskSidecar.Load(outPath)!.Confirmed);
    }

    // ── 읽지 못한 원고 ──

    [Theory]
    [InlineData("Color", "zero")]        // 색 검열 감지는 빈 그림에 '검열 없음'을 돌려줘 0바이트 '원본 그대로'로 성공했다
    [InlineData("Color", "truncated")]   // 깨진 jpg도 같은 길로 샜다
    [InlineData("BlackBar", "zero")]     // 막대 감지는 OpenCV 단언문(!_src.empty())으로 실패했다
    public async Task RunAsync_DecensorOnUnreadableSource_FailsWithReadableMessageAndWritesNothing(string target, string how)
    {
        using var t = new TempDir();
        var s = DecensorSettings(t, target);
        var src = t.At("vol1", "01.jpg");
        if (how == "zero") WriteBytesFile(src, 0);
        else
        {
            WriteBarPage(src);
            var bytes = File.ReadAllBytes(src);
            File.WriteAllBytes(src, bytes[..(bytes.Length * 2 / 3)]);
        }
        using (var probe = ImgIo.Read(src)) Assert.True(probe.Empty());   // 전제: 그림으로 못 읽는 원고
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
        {
            runner.PageWaiting += _ => { };   // 페이지별 검토 흐름 — 대기에 들어가거나 사이드카를 남기지 않아야 한다
            await runner.RunAsync([src], log, CancellationToken.None);
        }

        Assert.Equal("이미지를 읽지 못했어요: 01.jpg", log.FinalOf(src).Error);
        Assert.Equal(["01.jpg"], FilesUnder(t.At("vol1")));   // 결과·'원본 그대로' 복사본·마스크 사이드카 어느 것도 생기지 않았다
    }

    [Theory]
    [InlineData(false)]   // 기본 출력 폴더(<원고 폴더>_neko) — 0바이트 복사본이 생기고 지난 결과물은 치워졌다
    [InlineData(true)]    // 출력 폴더 = 원고 폴더 — 복사는 없지만 '원본 그대로' 성공으로 보고됐다
    public async Task RunAsync_EnginesOffZeroByteSource_FailsInsteadOfEmptyUntouchedCopy(bool outputIsSourceFolder)
    {
        // 엔진을 모두 끈 장은 원고를 디코드하지 않고 '원본 그대로' 복사한다 — 쓰다·내려받다 끊긴 0바이트 원고가 0바이트 결과로 성공했다
        using var t = new TempDir();
        var vol = t.At("vol1");
        var src = WriteBytesFile(Path.Combine(vol, "01.jpg"), 0);
        var s = EnginelessSettings(t, outputIsSourceFolder ? vol : "");
        if (!outputIsSourceFolder) WriteImageFile(PipelineRunner.OutputPathFor(s, src), seed: 7);   // 지난번에 만든 결과물
        var before = Snapshot(vol);
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
            await runner.RunAsync([src], log, CancellationToken.None);

        Assert.Equal("이미지를 읽지 못했어요: 01.jpg", log.FinalOf(src).Error);
        AssertUnchanged(before, vol);   // 0바이트 복사본도 없고, 지난 결과물도 그대로다
    }

    // ── 페이지별 검토: 확인한 뒤 마스크를 다시 읽을 때 ──

    /// <summary>원고 두 장(검은 막대가 하나씩)을 페이지별 검토로 돌려 둘 다 '검토 대기'에 들어갈 때까지 기다린다.
    /// 둘째 장은 사용자가 결과 창에서 마스크를 다 지운 것(오탐이었다)으로 해 둔다 — 확인하면 지울 곳이 없어 복원기 없이 '원본 그대로'로 끝난다.</summary>
    private static async Task<Task> StartTwoWaitingPagesAsync(PipelineRunner runner, AppSettings s, string a, string b, IProgress<PageProgress> log, CancellationToken ct = default)
    {
        var waiting = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.PageWaiting += pg => { lock (waiting) { waiting.Add(pg.File); if (waiting.Count == 2) both.TrySetResult(); } };
        var run = runner.RunAsync([a, b], log, ct);
        await Task.WhenAny(both.Task, run).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(both.Task.IsCompleted, "두 장 모두 검토 대기에 들어가야 한다");   // 전제: 규칙 감지기가 두 막대를 찾았다
        ClearMask(s, b);
        return run;
    }

    /// <summary>결과 창의 마스크 탭에서 전부 지우고 저장한 것처럼 그 장의 마스크 PNG를 빈 마스크로 바꾼다.</summary>
    private static void ClearMask(AppSettings s, string source)
    {
        using var cleared = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        Assert.True(ImgIo.Write(MaskSidecar.MaskPathFor(PipelineRunner.OutputPathFor(s, source)), cleared));
    }

    private const string Untouched = "검열 없음 — 원본 그대로";

    [Theory]
    [InlineData("zero")]
    [InlineData("truncated")]
    public async Task RunAsync_PerPageConfirmWithUnreadableMaskPng_FailsOnlyThatPage(string how)
    {
        // 확인한 순간 그 장의 마스크를 못 읽으면 메모리의 자동 감지 마스크로 그냥 지우러 갔다(사용자가 지운 오탐 자리까지) — 그 장만 실패로 두고 나머지는 계속
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        BreakMaskPng(outA, how);
        Assert.True(runner.ConfirmPage(a));
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));   // 작업 전체는 예외 없이 끝난다

        Assert.Contains(".mask.png", log.FinalOf(a).Error);
        Assert.DoesNotContain(log.Items, p => p.File == a && p.Stage.StartsWith("검열 지우는 중"));   // 자동 감지 마스크로 지우러 가지 않았다
        Assert.False(MaskSidecar.Load(outA)!.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(Untouched, log.FinalOf(b).Stage);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmSkipPageWithUnreadableMask_ProceedsWithoutMask()
    {
        // '검열 없음'으로 표시한 장은 마스크를 쓰지 않는다 — 마스크를 못 읽어도 막지 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        (MaskSidecar.Load(outA)! with { Skip = true }).Save(outA);
        BreakMaskPng(outA, "zero");
        Assert.True(runner.ConfirmPage(a));
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(a).Error);
        Assert.Equal(Untouched, log.FinalOf(a).Stage);
        Assert.True(MaskSidecar.Load(outA)!.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmWhileMaskPngStaysLocked_FailsOnlyThatPage()
    {
        // 확인한 순간 다른 프로그램이 마스크를 읽기까지 막고 쥐고 있으면 확인 작업이 깨져 작업 전체가 예외로 끝나고, 그 장은 끝 줄 없이 '검토 대기'로 남았다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        using (new FileStream(MaskSidecar.MaskPathFor(outA), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(runner.ConfirmPage(a));
            Assert.True(runner.ConfirmPage(b));
            await run.WaitAsync(TimeSpan.FromSeconds(60));   // 끝까지 잠겨 있다 — 기다렸다 다시 읽어도 못 읽는다
        }

        var error = log.FinalOf(a).Error;
        Assert.NotNull(error);   // 끝 줄이 하나 있다 — '검토 대기'로 남지 않았다
        Assert.Contains(".mask.png", error);
        Assert.Contains("잠시 뒤 다시", error);   // 영어 OS 문구가 아니라 무엇을 못 읽었고 어떻게 하면 되는지
        Assert.False(MaskSidecar.Load(outA)!.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(Untouched, log.FinalOf(b).Stage);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmWhileMaskPngBrieflyLocked_WaitsAndUsesSavedMask()
    {
        // 결과 창이 막 저장한 마스크를 백신·동기화가 잠깐 쥐고 있는 경우 — 사이드카 저장처럼 기다렸다 다시 읽어 사용자가 저장한 마스크로 간다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);   // 첫 장도 사용자가 다 지웠다
        using (new FileStream(MaskSidecar.MaskPathFor(outA), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(runner.ConfirmPage(a));
            await Task.Delay(300);   // 다시 읽기(0.7초 뒤)보다 먼저 풀린다
        }
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(a).Error);
        Assert.Equal(Untouched, log.FinalOf(a).Stage);   // 다 지운 저장 마스크를 받았다 — 자동 감지 마스크였다면 복원기로 갔다
        Assert.True(MaskSidecar.Load(outA)!.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
    }

    // ── 페이지별 검토: 확인한 장의 상태 글과 2차로 가는 순서 ──

    private const string Queued = "검열 제거 차례 대기";

    /// <summary>파일을 다른 프로그램처럼 쥐었다가 0.15초 뒤에 놓는다 — 다시 읽기(0.7초 뒤)보다 먼저 풀린다. 놓는 스레드를 돌려준다(Join으로 기다린다).
    /// 전용 스레드로 놓는다: Task.Delay(스레드풀)로 놓으면 테스트가 병렬로 도는 느린 CI에서 풀이 밀려 다시 읽기를 넘긴다(v1.9.1 Build Run 77).</summary>
    private static Thread HoldBriefly(string path)
    {
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };
        release.Start();
        return release;
    }

    /// <summary>보고가 들어온 자리(보고한 스레드)에서 끼어드는 수집기 — 적은 뒤에 hook을 부른다(before면 적기 전에).</summary>
    private sealed class HookedLog(Action<PageProgress> hook, bool before = false) : IProgress<PageProgress>
    {
        public ProgressLog Log { get; } = new();

        public void Report(PageProgress value)
        {
            if (before) hook(value);
            Log.Report(value);
            if (!before) hook(value);
        }
    }

    [Fact]
    public async Task RunAsync_PerPage_MaskSavedWhileQueuedForStage2_IsTheOneUsed()
    {
        // [검열 제거 시작] 뒤 차례를 기다리는 장을 결과 창에서 고쳐 저장하면 '저장했어요'라 하고 옛 마스크로 돌았다(2026-11-13 점검 4번) —
        // 러너는 확인 때 한 번만 사이드카를 다시 읽었다. 재생성은 장마다 몇 분이라 기다리는 동안 다음 장을 손보는 것은 흔한 일이다. 2차를 시작할 때 다시 본다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var edited = false;
        // 앞 장(b)이 끝난 자리 = 2차를 도는 스레드. 여기서 붙잡고 있는 동안 뒤 장(a)은 2차 큐에서 차례를 기다린다
        var log = new HookedLog(p =>
        {
            if (!(p.Done && p.File == b)) return;
            var until = DateTime.UtcNow.AddSeconds(30);
            while (MaskSidecar.Load(outA) is not { Confirmed: true } && DateTime.UtcNow < until) Thread.Sleep(10);   // a의 확인 뒤 일(마스크 다시 읽기·확인 표시)이 끝난 뒤에
            var sc = MaskSidecar.Load(outA)!;
            while (DateTime.Now == sc.SavedAt) Thread.Sleep(1);
            using var cleared = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);   // 결과 창에서 오탐이라 전부 지우고 저장한 것처럼
            MaskSidecar.Write(outA, a, cleared, null, null, sc.Summary, skip: false, edited: true, confirmed: sc.Confirmed);
            edited = true;
        });
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        Assert.True(runner.ConfirmPage(b));   // b는 빈 마스크라 복원기 없이 바로 끝난다
        Assert.True(runner.ConfirmPage(a));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(edited);
        Assert.Null(log.Log.FinalOf(a).Error);
        Assert.Equal(Untouched, log.Log.FinalOf(a).Stage);   // 고친(빈) 마스크로 돌았다 — 옛 마스크였으면 복원기로 가 가짜 모델에서 실패한다
        Assert.DoesNotContain(log.Log.Items, p => p.File == a && p.Stage.StartsWith("검열 지우는 중"));
        Assert.False(MaskSidecar.Load(outA)!.Pending);        // 반영됐으니 '고친 장'으로 남지 않는다
    }

    [Fact]
    public async Task RunAsync_PerPage_MaskPaintedOnAPageWithNothingDetected_BeforeItsTurn_IsUsed()
    {
        // 감지가 없어 검토를 기다리지 않고 바로 2차 큐로 간 장 — 자동 감지가 놓친 검열을 결과 창에서 칠해 저장하면, 그 장의 차례가 오기 전이었으면 그 마스크로 돌아야 한다.
        // 러너가 막 써 둔 사이드카의 저장 시각을 기억하지 않으면(새로 감지한 장) 그 뒤의 저장을 알아채지 못한다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var clean = t.At("vol1", "01.png");
        using (var blank = new Mat(H, W, MatType.CV_8UC3, new Scalar(235, 235, 235))) Assert.True(ImgIo.Write(clean, blank));
        var outPath = PipelineRunner.OutputPathFor(s, clean);
        var painted = false;
        // '검열 없음 — 바로 진행'은 1차를 도는 쪽이 2차 큐에 넣기 직전에 보고한다 — 그 자리에서 칠해 저장한다
        var log = new HookedLog(p =>
        {
            if (p.File != clean || !p.Stage.StartsWith("검열 없음 — 바로 진행")) return;
            var sc = MaskSidecar.Load(outPath)!;
            while (DateTime.Now == sc.SavedAt) Thread.Sleep(1);
            using var mask = RectMask(W, H, Bar);
            MaskSidecar.Write(outPath, clean, mask, null, null, sc.Summary, skip: false, edited: true, confirmed: sc.Confirmed);
            painted = true;
        }, before: true);
        using var runner = RuleRunner(s);
        runner.PageWaiting += _ => { };

        await runner.RunAsync([clean], log, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(painted);
        Assert.Contains(log.Log.Items, p => p.File == clean && p.Stage.StartsWith("검열 지우는 중"));   // 칠한 마스크로 복원기까지 갔다(시험의 가짜 모델이라 거기서 실패한다)
        Assert.NotNull(log.Log.FinalOf(clean).Error);
    }

    [Fact]
    public async Task RunAsync_PerPage_FreshlyDetectedPages_AreNotLeftMarkedAsEdited()
    {
        // 러너가 막 써 둔 사이드카의 저장 시각과 끝날 때 다시 읽은 시각이 어긋나면 손대지 않은 장이 전부 '고친 장'(✎)이 된다 — 확인을 거친 장도, 감지가 없어 바로 넘어간 장도
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var clean = t.At("vol1", "03.png");
        using (var blank = new Mat(H, W, MatType.CV_8UC3, new Scalar(235, 235, 235))) Assert.True(ImgIo.Write(clean, blank));   // 막대 없는 장 — 감지 없음
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var waiting = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.PageWaiting += pg => { lock (waiting) { waiting.Add(pg.File); if (waiting.Count == 2) both.TrySetResult(); } };
        var run = runner.RunAsync([a, b, clean], log, CancellationToken.None);
        await Task.WhenAny(both.Task, run).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(both.Task.IsCompleted, "막대가 있는 두 장은 검토 대기에 들어가야 한다");
        foreach (var f in new[] { a, b }) MarkNoCensor(PipelineRunner.OutputPathFor(s, f));   // json만 고쳐 쓴다(저장 시각 그대로) — 복원기 없이 끝나게

        Assert.Equal(2, runner.ConfirmAll());
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        foreach (var f in new[] { a, b, clean })
        {
            Assert.Null(log.FinalOf(f).Error);
            var sc = MaskSidecar.Load(PipelineRunner.OutputPathFor(s, f));
            Assert.NotNull(sc);
            Assert.False(sc.Pending, Path.GetFileName(f));
            Assert.False(sc.Colorized);
        }
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmAll_ReportsEveryPageAsQueuedBeforeItReturns()
    {
        // [검열 제거 시작] 뒤에도 차례를 기다리는 장이 목록에 '검토 대기 (더블클릭해 수정)'로 남았다(실측 2026-11-13: 마지막 장은 6분 반) — 확인을 받은 자리에서 상태 글을 바꾼다.
        // 앞 장의 확인 뒤 일이 오래 걸려도(여기서는 마스크가 잠깐 잠김) 뒤 장의 글은 그것을 기다리지 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);
        var release = HoldBriefly(MaskSidecar.MaskPathFor(outA));
        Assert.Equal(2, runner.ConfirmAll());
        var now = log.Items;
        Assert.Contains(now, p => p.File == a && p.Stage == Queued);
        Assert.Contains(now, p => p.File == b && p.Stage == Queued);
        Assert.Empty(runner.WaitingFiles);   // 메인 창의 [대기 중 N장 전부 진행]은 누른 자리에서 바로 사라진다
        release.Join();
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        foreach (var f in new[] { a, b })
        {
            var lines = log.Items.Where(p => p.File == f).ToList();
            int waiting = lines.FindIndex(p => p.Stage.StartsWith("검토 대기")), queued = lines.FindIndex(p => p.Stage == Queued), done = lines.FindIndex(p => p.Done);
            Assert.True(0 <= waiting && waiting < queued && queued < done, $"{Path.GetFileName(f)}: 검토 대기 {waiting} → 차례 대기 {queued} → 끝 {done}");
            Assert.Null(log.FinalOf(f).Error);
        }
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmAll_RunsPagesInPageOrderEvenWhenAnEarlierOneIsSlowToPickUp()
    {
        // [전부 진행]을 누르면 2차가 쪽 순서로 돌지 않았다(실측 2026-11-13: 4장이 5→4→2→3쪽 순) — 확인 뒤 일(마스크 다시 읽기·확인 표시)이 장마다 따로 돌아
        // 먼저 끝난 장이 먼저 2차 큐에 들어갔다. 앞 장의 마스크가 잠깐 잠겨 있으면 뒤 장이 늘 앞지른다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);
        var release = HoldBriefly(MaskSidecar.MaskPathFor(outA));
        Assert.Equal(2, runner.ConfirmAll());
        release.Join();
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(a).Error);
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal([a, b], log.Items.Where(p => p.Done).Select(p => p.File));
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmPage_RunsPagesInTheOrderTheyWereConfirmed()
    {
        // 하나씩 진행시키면 누른 순서대로 돈다 — 쪽 순서로 다시 줄 세우지 않고, 먼저 누른 장의 확인 뒤 일이 느려도 뒤에 누른 장이 앞지르지 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);
        var release = HoldBriefly(MaskSidecar.MaskPathFor(PipelineRunner.OutputPathFor(s, b)));
        Assert.True(runner.ConfirmPage(b));
        Assert.True(runner.ConfirmPage(a));
        release.Join();
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(a).Error);
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal([b, a], log.Items.Where(p => p.Done).Select(p => p.File));
    }

    [Fact]
    public async Task RunAsync_PerPageConfirm_QueuedLineIsReportedBeforeThePageCanStart()
    {
        // '차례 대기' 보고가 확인 줄에 넣은 뒤에 나가면, 곧바로 돌기 시작한 장의 진행·끝 줄보다 늦게 닿아 도는(끝난) 장의 행이 '차례 대기'로 남는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var log = new HookedLog(p => { if (p.Stage == Queued) Thread.Sleep(200); }, before: true);   // 보고를 받는 쪽이 느리다
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);
        Assert.True(runner.ConfirmPage(a));
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        foreach (var f in new[] { a, b })
        {
            var lines = log.Log.Items.Where(p => p.File == f).ToList();
            int queued = lines.FindIndex(p => p.Stage == Queued), done = lines.FindIndex(p => p.Done);
            Assert.True(0 <= queued && queued < done, $"{Path.GetFileName(f)}: 차례 대기 {queued} → 끝 {done}");
        }
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmAll_QueuedReportThrowing_StillSendsEveryPage()
    {
        // 상태 글 보고가 터져도 대기 목록에서 이미 뺀 장은 확인 줄에 넣는다 — 못 넣으면 그 장을 다시 확인할 길이 없어 작업이 끝나지 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var log = new HookedLog(p => { if (p.Stage == Queued) throw new InvalidOperationException("화면이 닫혔다"); }, before: true);
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        ClearMask(s, a);
        Assert.Equal(2, runner.ConfirmAll());
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.Log.FinalOf(a).Error);
        Assert.Null(log.Log.FinalOf(b).Error);
    }

    // ── 페이지별 검토: 검토 대기·확인 줄에서 [멈추기] ──

    [Fact]
    public async Task RunAsync_PerPageStoppedWhilePagesWait_EndsAsCancellationAndConfirmsNothing()
    {
        // 검토 대기 중에 [멈추기]: 작업은 취소로 끝나고, 기다리던 장은 확인 표시도 끝 줄도 없이 남는다(다시 [시작]하면 또 검토를 기다린다)
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var log = new ProgressLog();
        using var cts = new CancellationTokenSource();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Empty(runner.WaitingFiles);
        Assert.False(runner.ConfirmPage(a));
        Assert.Equal(0, runner.ConfirmAll());
        Assert.DoesNotContain(log.Items, p => p.Done || p.Stage == Queued);
        Assert.False(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, a))!.Confirmed);
        Assert.False(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, b))!.Confirmed);
    }

    [Fact]
    public async Task RunAsync_PerPageStoppedBetweenConfirmedPages_DoesNotMarkTheNextOneConfirmed()
    {
        // 확인 줄에 쌓여 있던 장은 멈춘 뒤에 확인 표시를 남기지 않는다 — 남기면 결과물도 없이 '확인됨'이라, 다시 [시작]했을 때 검토 없이 곧장 지우러 간다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        using var cts = new CancellationTokenSource();
        var log = new HookedLog(p => { if (p.File == a && p.Error != null) cts.Cancel(); });   // 첫 장의 확인 뒤 일이 실패한 자리(확인 줄을 도는 스레드)에서 [멈추기]
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log, cts.Token);

        BreakMaskPng(outA, "zero");
        Assert.InRange(runner.ConfirmAll(), 1, 2);   // 둘째 장을 넣기 전에 멈춤이 먼저 닿으면 1
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.NotNull(log.Log.FinalOf(a).Error);   // 전제: 첫 장은 확인 뒤 일에서 실패했다
        Assert.False(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, b))!.Confirmed);
        Assert.DoesNotContain(log.Log.Items, p => p.File == b && p.Done);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmAfterStopWasPressed_IsRefused()
    {
        // [멈추기]를 누른 뒤에는 확인을 받지 않는다 — 1차가 돌던 감지를 마치고 취소를 알아챌 때까지 확인 줄이 열려 있어,
        // 받으면 '보냈어요'라 하고 결과 창을 닫고 행을 '차례 대기'로 바꾼 채 아무 일도 하지 않는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var log = new ProgressLog();
        using var cts = new CancellationTokenSource();
        using var inSecond = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        int detections = 0;
        using var runner = new PipelineRunner(s)
        {
            DetectorOverride = (img, exclude) =>
            {
                if (Interlocked.Increment(ref detections) == 2) { inSecond.Set(); letGo.Wait(TimeSpan.FromSeconds(30)); }   // 둘째 장의 감지가 도는 중
                return MosaicDetector.BuildMaskForTarget(s, img, exclude, out _);
            },
        };
        runner.PageWaiting += _ => { };
        var run = Task.Run(() => runner.RunAsync([a, b], log, cts.Token));   // 1차는 부른 스레드에서 이어 돈다 — 감지를 붙잡아 두려면 다른 스레드에서
        Assert.True(inSecond.Wait(TimeSpan.FromSeconds(60)), "둘째 장의 감지까지 가야 한다");
        Assert.Equal([a], runner.WaitingFiles);   // 전제: 첫 장은 검토 대기

        cts.Cancel();
        bool one = runner.ConfirmPage(a);
        int all = runner.ConfirmAll();
        letGo.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.False(one);
        Assert.Equal(0, all);
        Assert.DoesNotContain(log.Items, p => p.Stage == Queued);
        Assert.False(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, a))!.Confirmed);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmAll_AfterOnePageWentAhead_StillSendsTheRestInPageOrder()
    {
        // 한 장을 먼저 진행시킨 뒤 새로 대기에 들어온 장이 있으면 대기 목록(Dictionary)의 나열 순서가 넣은 순서와 달라진다(뺀 자리를 다시 쓴다) — 쪽 순서로 골라 보낸다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var files = Enumerable.Range(1, 4).Select(i => WriteBarPage(t.At("vol1", $"0{i}.png"), x: 40 + i * 20)).ToList();
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        int seen = 0; bool firstWentAhead = false;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.PageWaiting += _ =>
        {
            seen++;   // 1차는 한 스레드가 차례로 돈다
            if (seen == 3) { ClearMask(s, files[0]); firstWentAhead = runner.ConfirmPage(files[0]); }   // 셋째 장이 대기에 들어온 순간 첫 장만 진행 — 넷째 장은 그 뒤에 들어온다
            if (seen == 4) all.TrySetResult();
        };
        var run = runner.RunAsync(files, log, CancellationToken.None);
        await Task.WhenAny(all.Task, run).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(all.Task.IsCompleted, "네 장 모두 검토 대기에 들어가야 한다");
        Assert.True(firstWentAhead);
        foreach (var f in files.Skip(1)) ClearMask(s, f);

        Assert.Equal(3, runner.ConfirmAll());
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(files, f => Assert.Null(log.FinalOf(f).Error));
        Assert.Equal(files, log.Items.Where(p => p.Done).Select(p => p.File));
    }

    // ── 페이지별 검토: 확인한 순간 사이드카 json을 못 읽을 때 ──

    /// <summary>결과 창의 마스크 탭에서 '이 페이지는 검열 없음'을 체크해 저장한 것처럼 그 장의 사이드카를 고친다.</summary>
    private static void MarkNoCensor(string outPath) => (MaskSidecar.Load(outPath)! with { Skip = true, Edited = true }).Save(outPath);

    private static bool WentToRepair(ProgressLog log, string file) => log.Items.Any(p => p.File == file && p.Stage.StartsWith("검열 지우는 중"));

    [Theory]
    [InlineData("skip")]      // '검열 없음'으로 표시해 저장했다
    [InlineData("cleared")]   // 오탐을 다 지우고 저장했다
    public async Task RunAsync_PerPageConfirmWhileMaskJsonStaysLocked_FailsOnlyThatPageWithoutErasing(string userEdit)
    {
        // 결과 창이 막 저장한 json을 백신·동기화가 쥐고 있으면 사이드카 읽기가 '사이드카 없음'과 똑같이 null이라, 사용자가 고친 것을 버리고
        // 1차의 자동 감지 마스크로 조용히 지우러 갔다(확인 표시도 경고 없이 건너뛰었다) — 마스크 PNG의 잠금만 기다렸다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        if (userEdit == "skip") MarkNoCensor(outA); else ClearMask(s, a);
        using (new FileStream(MaskSidecar.JsonPathFor(outA), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(runner.ConfirmPage(a));
            Assert.True(runner.ConfirmPage(b));
            await run.WaitAsync(TimeSpan.FromSeconds(60));   // 끝까지 잠겨 있다
        }

        var error = log.FinalOf(a).Error;
        Assert.NotNull(error);
        Assert.Contains(".mask.json", error);
        Assert.Contains("잠시 뒤 다시", error);
        Assert.False(WentToRepair(log, a));   // 자동 감지 마스크로 지우러 가지 않았다
        Assert.False(MaskSidecar.Load(outA)!.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(Untouched, log.FinalOf(b).Stage);
    }

    [Fact]
    public async Task RunAsync_PerPageConfirmWhileMaskJsonBrieflyLocked_WaitsAndUsesSavedDecision()
    {
        // 잠깐 잠긴 json은 마스크 PNG처럼 기다렸다 다시 읽어 사용자가 저장한 '검열 없음'을 받는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        MarkNoCensor(outA);
        using (new FileStream(MaskSidecar.JsonPathFor(outA), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(runner.ConfirmPage(a));
            await Task.Delay(300);   // 다시 읽기(0.7초 뒤)보다 먼저 풀린다
        }
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(a).Error);
        Assert.Equal(Untouched, log.FinalOf(a).Stage);
        Assert.False(WentToRepair(log, a));
        var sc = MaskSidecar.Load(outA)!;
        Assert.True(sc.Skip);
        Assert.True(sc.Confirmed);
        Assert.Null(log.FinalOf(b).Error);
    }

    [Theory]
    [InlineData("zero")]        // 결과 창의 저장이 끊겨 0바이트로 남았다
    [InlineData("truncated")]   // 반쯤 쓰인 json
    public async Task RunAsync_PerPageConfirmWithBrokenMaskJson_FailsOnlyThatPage(string how)
    {
        // 깨진 json도 '사이드카 없음'으로 빠져 자동 감지 마스크로 지우러 갔다 — 사용자가 무엇을 저장했는지 알 수 없으니 그 장만 실패로 둔다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var a = WriteBarPage(t.At("vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("vol1", "02.png"), x: 60);
        var outA = PipelineRunner.OutputPathFor(s, a);
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        var run = await StartTwoWaitingPagesAsync(runner, s, a, b, log);

        var json = MaskSidecar.JsonPathFor(outA);
        var text = File.ReadAllText(json);
        File.WriteAllText(json, how == "zero" ? "" : text[..(text.Length / 2)]);
        var broken = File.ReadAllBytes(json);
        Assert.True(runner.ConfirmPage(a));
        Assert.True(runner.ConfirmPage(b));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains(".mask.json", log.FinalOf(a).Error);
        Assert.False(WentToRepair(log, a));
        Assert.Equal(broken, File.ReadAllBytes(json));   // 사이드카는 읽기만 했다
        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(Untouched, log.FinalOf(b).Stage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // 다시 돌릴 때의 1차 — 깨진 json은 없는 것으로 보고 새로 감지하지만 잠긴 json은 그러면 안 된다
    public void LoadSidecarWaitingOutLock_TellsLockedAndBrokenJsonFromNoSidecar(bool brokenThrows)
    {
        // 사이드카 읽기는 없음·잠김·깨짐을 모두 null로 돌려준다 — 잠김을 '없음'으로 넘기면 다시 돌릴 때 새로 감지해 사용자가 고친 마스크를 덮는다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = PipelineRunner.OutputPathFor(s, src);
        var json = MaskSidecar.JsonPathFor(outPath);

        Assert.Null(PipelineRunner.LoadSidecarWaitingOutLock(outPath, brokenThrows));   // 사이드카가 없다

        WriteRedoSlot(s, src);
        Assert.Equal(src, PipelineRunner.LoadSidecarWaitingOutLock(outPath, brokenThrows)?.Source);

        using (new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => PipelineRunner.LoadSidecarWaitingOutLock(outPath, brokenThrows));
            Assert.Contains(".mask.json", ex.Message);
            Assert.Contains("잠시 뒤 다시", ex.Message);
        }

        File.WriteAllText(json, "");   // 쓰다 끊긴 json
        if (brokenThrows) Assert.Contains(".mask.json", Assert.Throws<InvalidDataException>(() => PipelineRunner.LoadSidecarWaitingOutLock(outPath, true)).Message);
        else Assert.Null(PipelineRunner.LoadSidecarWaitingOutLock(outPath, false));

        File.Delete(MaskSidecar.MaskPathFor(outPath));   // 마스크 PNG가 없으면 사이드카가 없는 것이다 — 깨진 json만으로 막지 않는다
        Assert.Null(PipelineRunner.LoadSidecarWaitingOutLock(outPath, brokenThrows));
    }

    // ── 결과 창의 '채색부터 다시'·다시 돌리기: 저장된 마스크가 잠겨 있을 때 ──

    [Theory]
    [InlineData("json")]   // '저장된 마스크가 없어요 — 먼저 한 번 돌려야 해요'라고 틀리게 안내했다
    [InlineData("png")]    // 영어 OS 문구가 그대로 보였다
    public async Task RedoPageAsync_StoredMaskStaysLocked_SaysRetryLaterAndTouchesNothing(string which)
    {
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src);
        var held = which == "json" ? MaskSidecar.JsonPathFor(outPath) : MaskSidecar.MaskPathFor(outPath);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);

        using (var runner = RuleRunner(s))
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None));
            Assert.Contains(Path.GetFileName(held), ex.Message);
            Assert.Contains("잠시 뒤 다시", ex.Message);
        }

        AssertUnchanged(before, outDir);
        Assert.False(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_MaskJsonBrieflyLocked_WaitsAndRedoes()
    {
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);   // '검열 없음'으로 표시한 장 — 복원기 없이 끝난다
        var log = new ProgressLog();
        using var runner = RuleRunner(s);

        Task redo;
        using (new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            redo = Task.Run(() => runner.RedoPageAsync(src, log, CancellationToken.None));
            await Task.Delay(300);   // 다시 읽기(0.7초 뒤)보다 먼저 풀린다
        }
        await redo.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(src).Error);
        Assert.True(MaskSidecar.Load(outPath)!.Confirmed);
    }

    // ── '확인됨' 표시: 2차가 끝난 뒤 사이드카 json을 못 읽을 때 ──

    /// <summary>보고를 그대로 모으다가 첫 끝 줄(Done)이 오는 자리에서 정해 둔 일을 한 번 한다 — [채색부터 다시]는 2차가 끝 줄을 보낸 바로 뒤에 '확인됨'을 찍으므로,
    /// 그 사이에 다른 프로그램이 json을 쥐는(또는 지우는) 상황을 만든다. 보고는 러너 스레드에서 바로 불려 표시보다 반드시 먼저 끝난다.</summary>
    private sealed class OnFirstDone(ProgressLog log, Action onDone) : IProgress<PageProgress>
    {
        private int _fired;

        public void Report(PageProgress value)
        {
            log.Report(value);
            if (value.Done && Interlocked.Exchange(ref _fired, 1) == 0) onDone();
        }
    }

    [Fact]
    public async Task RedoPageAsync_MaskJsonStaysLockedWhenMarkingConfirmed_WarnsInsteadOfSkippingSilently()
    {
        // 잠긴 json은 읽기가 '사이드카 없음'과 똑같이 null이라, 확인 표시를 경고 없이 건너뛰고 성공으로 끝났다 — 다음 실행이 까닭 없이 검토를 한 번 더 기다렸다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);   // '검열 없음'으로 표시한 장 — 복원기 없이 끝난다
        var log = new ProgressLog();
        FileStream? held = null;
        var progress = new OnFirstDone(log, () => held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None));

        try
        {
            using var runner = RuleRunner(s);
            await runner.RedoPageAsync(src, progress, CancellationToken.None);   // 끝까지 잠겨 있다 — 기다렸다 다시 읽어도 못 읽는다
        }
        finally { held?.Dispose(); }

        var done = log.Items.Where(p => p.Done && p.File == src).ToList();
        Assert.Equal(2, done.Count);   // 2차의 끝 줄, 그리고 경고를 붙여 다시 보낸 끝 줄
        Assert.All(done, p => Assert.Null(p.Error));   // 결과는 나왔다 — 그 장의 실패가 아니다
        Assert.DoesNotContain("⚠", done[0].Stage);
        Assert.Contains("마스크 확인 표시 실패", done[1].Stage);
        Assert.Contains(".mask.json", done[1].Stage);
        Assert.Contains("검토를 한 번 더", done[1].Stage);   // '다시 돌려 주세요'가 아니라 무슨 일이 생기는지
        Assert.True(File.Exists(outPath));
        Assert.False(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_MaskJsonBrieflyLockedWhenMarkingConfirmed_WaitsAndMarks()
    {
        // 잠깐의 잠금(백신 검사·동기화)은 다른 읽기처럼 기다렸다 다시 읽어 표시한다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);
        var log = new ProgressLog();
        Thread? release = null;
        var progress = new OnFirstDone(log, () =>
        {
            var held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
            // 다시 읽기(0.7초 뒤)보다 먼저 풀린다 — 전용 스레드로(스레드풀 Task.Delay는 느린 CI에서 밀려 0.7초를 넘긴다, v1.9.1 Build Run 77)
            release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };
            release.Start();
        });

        using (var runner = RuleRunner(s))
            await runner.RedoPageAsync(src, progress, CancellationToken.None);
        release!.Join();

        Assert.Null(log.FinalOf(src).Error);   // 끝 줄은 하나뿐이다 — 경고로 다시 보내지 않았다
        Assert.DoesNotContain("⚠", log.FinalOf(src).Stage);
        Assert.True(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_MaskJsonGoneWhenMarkingConfirmed_StaysSilent()
    {
        // 사이드카가 정말 없으면 표시할 것이 없다 — 경고하지 않는다(잠김만 '없음'과 가른다)
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);
        var log = new ProgressLog();
        var progress = new OnFirstDone(log, () => File.Delete(MaskSidecar.JsonPathFor(outPath)));

        using (var runner = RuleRunner(s))
            await runner.RedoPageAsync(src, progress, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        Assert.DoesNotContain("⚠", log.FinalOf(src).Stage);
        Assert.DoesNotContain(log.Items, p => p.Stage.Contains("확인 표시 실패"));
        Assert.False(File.Exists(MaskSidecar.JsonPathFor(outPath)));
    }

    [Fact]
    public async Task RedoPageAsync_SidecarAlreadyConfirmedAndJsonLockedAtTheEnd_NeitherWaitsNorWarns()
    {
        // 끝난 장의 [채색부터 다시]는 사이드카가 이미 '확인됨'이라 표시할 것이 없다 — 그런데도 json을 다시 읽으려다 잠겨 있으면 1.4초를 기다리고
        // '다음에 검토를 한 번 더 기다려요'라고 경고했다(이미 확인된 것이라 기다리지 않는다 — 틀린 말이다)
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);
        (MaskSidecar.Load(outPath)! with { Confirmed = true }).Save(outPath);
        var log = new ProgressLog();
        FileStream? held = null;
        var afterDone = new System.Diagnostics.Stopwatch();
        var progress = new OnFirstDone(log, () =>
        {
            held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
            afterDone.Start();
        });

        try
        {
            using var runner = RuleRunner(s);
            await runner.RedoPageAsync(src, progress, CancellationToken.None);
            afterDone.Stop();
        }
        finally { held?.Dispose(); }

        Assert.NotNull(held);   // 전제: 표시할 차례에 json은 잠겨 있었다
        Assert.Null(log.FinalOf(src).Error);   // 끝 줄은 하나뿐이다 — 경고로 다시 보내지 않았다
        Assert.DoesNotContain(log.Items, p => p.Stage.Contains("확인 표시 실패") || p.Stage.Contains("⚠"));
        Assert.True(afterDone.ElapsedMilliseconds < 1000, $"잠금을 기다리지 않아야 한다({afterDone.ElapsedMilliseconds}ms)");   // 기다리면 0.7초씩 두 번(1.4초)이다
        Assert.True(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RunAsync_RerunWhileStoredMaskPngStaysLocked_FailsWithReadableMessageAndKeepsSidecar()
    {
        // 저장된 마스크를 감지 대신 쓰려는데 잠겨 있다 — 그 장은 실패로 두되(새로 감지하면 검토 흐름이 사용자가 고친 마스크를 덮는다) 영어 OS 문구 대신 안내한다
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
        using (new FileStream(MaskSidecar.MaskPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            runner.PageWaiting += _ => { };
            await runner.RunAsync([src], log, CancellationToken.None);
        }

        var error = log.FinalOf(src).Error;
        Assert.NotNull(error);
        Assert.Contains(".mask.png", error);
        Assert.Contains("잠시 뒤 다시", error);
        Assert.DoesNotContain(log.Items, p => p.Stage == "검열 영역 찾는 중");   // 새로 감지하지 않았다
        AssertUnchanged(before, outDir);
    }

    [Fact]
    public async Task RunAsync_RerunWhileStoredMaskPngBrieflyLocked_WaitsAndReusesStoredMask()
    {
        using var t = new TempDir();
        var s = DecensorSettings(t);
        var src = WriteBarPage(t.At("vol1", "01.png"));
        var outPath = WriteRedoSlot(s, src, skip: true);   // '검열 없음'으로 표시한 장 — 저장된 마스크를 받으면 기다리지 않고 복원기 없이 끝난다
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        runner.PageWaiting += _ => { };

        Task run;
        using (new FileStream(MaskSidecar.MaskPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            run = Task.Run(() => runner.RunAsync([src], log, CancellationToken.None));
            await Task.Delay(300);   // 다시 읽기(0.7초 뒤)보다 먼저 풀린다
        }
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(log.FinalOf(src).Error);
        Assert.Contains(log.Items, p => p.Stage.StartsWith("저장된 마스크"));
        Assert.DoesNotContain(log.Items, p => p.Stage == "검열 영역 찾는 중");
    }
}
