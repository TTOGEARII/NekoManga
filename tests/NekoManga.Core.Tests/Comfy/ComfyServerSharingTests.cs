using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NekoManga.Core.Comfy;
using NekoManga.Core.Pipeline;
using NekoManga.Core.Training;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>ComfyUI 서버 공유 — 이 앱 안의 사용자(본 작업 러너·결과 창 러너·표지 색 뽑기·엔진 켜기)가 서버 하나를 같이 쓰고,
/// KeepComfyWarm=false면 이 앱이 띄운 서버를 마지막 사용자가 놓을 때만(놓지 못한 채 앱이 끝나면 그때) 끈다.
/// 다른 NekoManga 프로세스가 띄워 깨어나는 중인 서버는 하나 더 띄우지 않고 기다린다(끄지는 않는다). 엔진 끄기는 쓰는 작업이 있는지 알 수 있고, 끈 뒤 장부가 맞는다.
/// 실제 ComfyUI는 띄우지 않는다 — 프로세스·HTTP 대신 가짜 엔진을 넣은 장부(ComfyServer.Ledger)로 규칙만 본다.</summary>
public class ComfyServerSharingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // ── 같이 쓰고 마지막에 끈다 ──

    [Fact]
    public async Task TwoUsers_ServerWeStarted_StopsOnlyWhenTheLastOneReleases()
    {
        // 본 작업과 결과 창이 같은 서버를 쓴다 — 먼저 끝난 쪽이 놓아도 서버는 남고, 마지막 사용자가 놓을 때 꺼진다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var main = new ComfyServer(s, engine.Ledger);
        var window = new ComfyServer(s, engine.Ledger);

        await main.EnsureRunningAsync();
        await window.EnsureRunningAsync();
        var proc = Assert.Single(engine.Procs);   // 둘째는 떠 있는 서버를 그대로 쓴다
        Assert.True(main.WeStartedIt);
        Assert.True(window.WeStartedIt);          // 이 앱이 띄운 서버다 — 어느 인스턴스가 띄웠는지는 상관없다

        main.Release();
        Assert.False(proc.Killed);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));

        window.Release();
        Assert.Equal(1, proc.Kills);
        Assert.False(main.WeStartedIt);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));
    }

    [Fact]
    public async Task KeepComfyWarm_LastRelease_LeavesTheServerRunning()
    {
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine();
        await using (var user = new ComfyServer(s, engine.Ledger)) await user.EnsureRunningAsync();

        Assert.False(Assert.Single(engine.Procs).Killed);
        Assert.True(new ComfyServer(s, engine.Ledger).WeStartedIt);   // 다음 작업이 그대로 쓴다
    }

    [Fact]
    public async Task ServerWeDidNotStart_IsNeverStopped()
    {
        // 사용자가 같은 포트에 따로 띄운 ComfyUI는 KeepComfyWarm=false여도 끄지 않는다 — 쓰기만 한다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        engine.ExternalServerOn(s.ComfyPort);
        var user = new ComfyServer(s, engine.Ledger);

        await user.EnsureRunningAsync();
        Assert.False(user.WeStartedIt);
        user.Release();

        Assert.Empty(engine.Procs);
        Assert.True(await user.IsAliveAsync());
    }

    [Fact]
    public async Task EngineStart_KeepsTheServerAfterTheLastRelease_UntilItIsStoppedAndAJobStartsANewOne()
    {
        // '엔진 켜기'(EngineManager.StartAsync)는 KeepComfyWarm=false여도 엔진 끄기 전까지 유지한다 — 작업이 쓰고 놓아도 그대로
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        await EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, CancellationToken.None);
        var pinned = Assert.Single(engine.Procs);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));   // 켜기 자신은 사용을 남기지 않는다

        await using (var job = new ComfyServer(s, engine.Ledger)) await job.EnsureRunningAsync();
        Assert.False(pinned.Killed);

        // 엔진 끄기(EngineManager.Stop)로 죽은 뒤 작업이 새로 띄운 서버는 고정이 아니다 — 작업이 끝나면 KeepComfyWarm 규칙대로 꺼진다
        pinned.Exit();
        await using (var job = new ComfyServer(s, engine.Ledger)) await job.EnsureRunningAsync();
        Assert.Equal(2, engine.Procs.Count);
        Assert.True(engine.Procs[1].Killed);
    }

    [Fact]
    public async Task EngineStart_OnAServerAJobStarted_KeepsItAfterTheJob()
    {
        // 작업이 띄워 이미 떠 있는 서버에 엔진 켜기를 눌러도(미리 켜기와 작업 시작이 겹칠 때 등) 켜 두라는 뜻으로 본다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();

        await EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, CancellationToken.None);
        job.Release();

        Assert.False(Assert.Single(engine.Procs).Killed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineStart_FailsWhileTheServerWakes_LeavesThatServerPinned(bool timedOut)
    {
        // '엔진 켜기'·미리 켜기가 응답을 기다리다 실패해도(느린 PC의 4분 시간 초과·취소) KeepComfyWarm=false에서 깨어나는 중인 서버를 끄지 않는다 —
        // 명시적으로 켜라고 한 서버다. 예전 StartAsync도 남겨 두어 결국 뜬 서버를 다음 작업이 썼고, 작업이 끝나도 엔진 끄기 전까지 두었다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine(startLimit: timedOut ? TimeSpan.FromMilliseconds(50) : null) { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        var start = EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, stop.Token);
        var waking = Assert.Single(engine.Procs);
        if (timedOut) await Assert.ThrowsAsync<TimeoutException>(() => start.WaitAsync(Limit));
        else
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Limit));
        }
        Assert.True(start.IsCompleted);   // 켜기 자신이 실패로 끝났다(시험의 기다림 한도가 아니라)
        Assert.False(waking.Killed);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));   // 켜기 자신은 사용을 남기지 않는다

        engine.Respond(s.ComfyPort);   // 그 서버가 다 깨어난다
        await using (var job = new ComfyServer(s, engine.Ledger)) await job.EnsureRunningAsync();
        Assert.Single(engine.Procs);
        Assert.False(waking.Killed);   // 고정된 서버 — 작업이 끝나도 그대로
    }

    // ── 놓기는 멱등, 놓은 뒤 다시 쥐기 ──

    [Fact]
    public async Task Release_CalledTwice_DoesNotTakeAwayAnotherUsersHold()
    {
        // 본 작업은 RunAsync 끝과 Dispose에서 두 번 놓는다 — 그래도 결과 창이 쓰는 서버를 끄지 않는다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var main = new ComfyServer(s, engine.Ledger);
        var window = new ComfyServer(s, engine.Ledger);
        await main.EnsureRunningAsync();
        await window.EnsureRunningAsync();
        await main.EnsureRunningAsync();   // 같은 인스턴스는 몇 번 불러도 한 사용자
        Assert.Equal(2, engine.Ledger.UsersOf(s.ComfyPort));

        main.Release();
        main.Release();
        await main.DisposeAsync();

        Assert.False(Assert.Single(engine.Procs).Killed);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        window.Release();
        Assert.Equal(1, engine.Procs[0].Kills);
    }

    [Fact]
    public async Task ReleasedUser_CanHoldAgain()
    {
        // 놓은 뒤에도 같은 인스턴스로 다시 쓸 수 있다 — 서버가 꺼졌으면 다시 띄우고, 다시 놓으면 다시 끈다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var user = new ComfyServer(s, engine.Ledger);

        await user.EnsureRunningAsync();
        user.Release();
        await user.EnsureRunningAsync();
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        user.Release();

        Assert.Equal(2, engine.Procs.Count);
        Assert.All(engine.Procs, p => Assert.Equal(1, p.Kills));
    }

    [Fact]
    public async Task AfterTheServerDies_TheNextEnsureRunningStartsAFreshOne()
    {
        // 엔진 끄기(EngineManager.Stop — 자가 학습이 GPU를 비울 때도)나 충돌로 서버가 죽어도 다음 EnsureRunningAsync가 새로 띄운다 — 사용자는 한 번만 센다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var user = new ComfyServer(s, engine.Ledger);
        await user.EnsureRunningAsync();

        engine.Procs[0].Exit();
        Assert.False(user.WeStartedIt);
        await user.EnsureRunningAsync();

        Assert.Equal(2, engine.Procs.Count);
        Assert.True(user.WeStartedIt);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        user.Release();
        Assert.True(engine.Procs[1].Killed);
        Assert.Equal(0, engine.Procs[0].Kills);   // 이미 죽은 것은 다시 죽이지 않는다
    }

    [Fact]
    public async Task SameUserOnAnotherPort_LetsGoOfTheOldPort()
    {
        // 작업 중에 설정에서 포트를 바꾼 뒤 같은 인스턴스가 다시 쓰면 옛 포트의 사용은 놓는다 — 아무도 안 쓰는 옛 서버가 남지 않게
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var user = new ComfyServer(s, engine.Ledger);
        await user.EnsureRunningAsync();
        int oldPort = s.ComfyPort;

        s.ComfyPort = oldPort + 1;
        await user.EnsureRunningAsync();

        Assert.True(engine.Procs[0].Killed);
        Assert.Equal(0, engine.Ledger.UsersOf(oldPort));
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        Assert.False(engine.Procs[1].Killed);
    }

    // ── 여러 스레드에서 동시에 ──

    [Fact]
    public async Task TwoUsersStartingAtOnce_LaunchOneServer()
    {
        // 앱 시작 때 미리 켜기가 서버를 띄우는 동안 작업을 시작한 경우 — 둘이 따로 띄우면 늦은 cmd는 먼저 뜬 cmd가 쥔 comfy.log를 열지 못해 곧바로 exit 1로 끝나
        // 그 작업이 실패하고, 장부에 죽은 쪽이 적히면 살아 있는 서버를 아무도 끄지 못한다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine { RespondsAfterLaunch = false };
        var first = new ComfyServer(s, engine.Ledger);
        var second = new ComfyServer(s, engine.Ledger);

        var firstTask = first.EnsureRunningAsync();   // 띄우고 응답을 기다리는 중
        Assert.Single(engine.Procs);
        var secondSawItDown = new TaskCompletionSource();
        var secondTask = second.EnsureRunningAsync(new SyncProgress(_ => secondSawItDown.TrySetResult()));
        await secondSawItDown.Task.WaitAsync(Limit);   // 둘째도 꺼진 서버를 보고 띄우려는 참이다
        engine.Respond(s.ComfyPort);                   // 첫째가 띄운 서버가 응답하기 시작한다

        await Task.WhenAll(firstTask, secondTask).WaitAsync(Limit);
        Assert.Single(engine.Procs);
        Assert.Equal(2, engine.Ledger.UsersOf(s.ComfyPort));
    }

    [Fact]
    public async Task HoldsAndReleasesFromManyThreads_KeepAnExactCount()
    {
        // 러너들은 저마다 다른 스레드(Task.Run)에서 쥐고 놓는다 — 세기가 어긋나면 남이 쓰는 서버를 끄거나 아무도 안 쓰는 서버를 남긴다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        var keeper = new ComfyServer(s, engine.Ledger);   // 내내 쥐고 있는 사용자 (오래 도는 본 작업)
        await keeper.EnsureRunningAsync();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            for (int k = 0; k < 20; k++)
            {
                var user = new ComfyServer(s, engine.Ledger);
                await user.EnsureRunningAsync();
                user.Release();
                await user.DisposeAsync();   // 두 번 놓아도 한 번만
            }
        }))).WaitAsync(Limit);

        Assert.False(Assert.Single(engine.Procs).Killed);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        keeper.Release();
        Assert.Equal(1, engine.Procs[0].Kills);
    }

    // ── 그만둔 기동: 깨어나는 중인 우리 서버를 기다린다 ──

    [Fact]
    public async Task CancelledStart_ThenStartAgain_WaitsForTheServerStillWakingUp_InsteadOfLaunchingAnother()
    {
        // 엔진이 깨어나는 중에 [중지]하고 곧바로 다시 [작업 시작]한 경우 — KeepComfyWarm=true(기본)라 놓아도 서버는 계속 깨어난다.
        // 예전에는 둘째 작업이 cmd를 하나 더 띄웠다: 그 cmd는 첫째가 쥔 comfy.log를 못 열어 곧바로 exit 1로 죽어 작업이 '시작하자마자 종료'로 실패했고,
        // 장부는 살아 있는 첫째를 잃어 나중에 KeepComfyWarm을 꺼도 그 서버를 끄지 못했다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        var job1 = new ComfyServer(s, engine.Ledger);
        var boot = job1.EnsureRunningAsync(null, stop.Token);
        var waking = Assert.Single(engine.Procs);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boot.WaitAsync(Limit));
        job1.Release();   // RunAsync의 finally
        Assert.False(waking.HasExited);

        using var limit = new CancellationTokenSource(Limit);
        var job2 = new ComfyServer(s, engine.Ledger);
        int checks = engine.AliveChecks;
        var second = job2.EnsureRunningAsync(null, limit.Token);
        await WaitUntil(() => engine.AliveChecks >= checks + 3);   // 관문 앞·안에서 꺼진 것을 보고, 깨어나기를 기다리며 다시 볼 때까지
        Assert.Single(engine.Procs);                                // 하나 더 띄우지 않고 깨어나는 중인 서버를 기다린다

        engine.Respond(s.ComfyPort);   // 그 서버가 다 깨어난다
        await second.WaitAsync(Limit);
        Assert.Single(engine.Procs);
        Assert.True(job2.WeStartedIt);   // 장부도 그 서버를 이 앱이 띄운 것으로 안다

        s.KeepComfyWarm = false;   // 나중에 꺼 두기로 바꾸면 마지막 사용자가 놓을 때 그 서버를 끈다
        job2.Release();
        Assert.Equal(1, waking.Kills);
    }

    [Fact]
    public async Task StarterCancelled_WhileAnotherWaitsAtTheGate_TheWaiterWaitsForTheSameServer_AndTheLastReleaseStopsIt()
    {
        // KeepComfyWarm=false — 띄우던 사용자가 그만두고(취소) 다른 사용자가 관문에서 기다리던 경우.
        // 예전에는 기다리던 쪽이 하나 더 띄워 곧바로 죽었고(그 작업 실패), 마지막 사용자가 놓아도 살아 있는 첫째 서버는 꺼지지 않는 고아로 남았다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        using var limit = new CancellationTokenSource(Limit);
        var starter = new ComfyServer(s, engine.Ledger);
        var waiter = new ComfyServer(s, engine.Ledger);
        var starting = starter.EnsureRunningAsync(null, stop.Token);
        var waking = Assert.Single(engine.Procs);
        var waiterSawItDown = new TaskCompletionSource();
        var waiting = waiter.EnsureRunningAsync(new SyncProgress(_ => waiterSawItDown.TrySetResult()), limit.Token);
        await waiterSawItDown.Task.WaitAsync(Limit);   // 기다리는 쪽은 관문 앞에 있다

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Limit));
        starter.Release();   // 기다리는 쪽이 쥐고 있어 끄지 않는다
        Assert.False(waking.HasExited);
        int checks = engine.AliveChecks;
        await WaitUntil(() => engine.AliveChecks >= checks + 2);   // 기다리던 쪽이 관문을 지나 깨어나기를 기다릴 때까지
        Assert.Single(engine.Procs);

        engine.Respond(s.ComfyPort);
        await waiting.WaitAsync(Limit);
        waiter.Release();   // 마지막 사용자 — 이 앱이 띄운 서버라 끈다
        Assert.Single(engine.Procs);
        Assert.Equal(1, waking.Kills);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));
    }

    [Fact]
    public async Task TimedOutStart_TheNextUserWaitsForTheSameServerAgain()
    {
        // 느린 PC에서 제한 시간(4분) 안에 응답하지 않아 그만둔 기동 — 서버는 아직 깨어나는 중이다(KeepComfyWarm=true라 놓아도 남는다).
        // 다음 사용자도 하나 더 띄우지 않고 그 서버를 다시 기다리고, 장부는 끝까지 그 서버를 이 앱이 띄운 것으로 안다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine(startLimit: TimeSpan.FromMilliseconds(50)) { RespondsAfterLaunch = false };
        var job1 = new ComfyServer(s, engine.Ledger);
        var first = job1.EnsureRunningAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(Limit));
        Assert.True(first.IsFaulted);   // 기동 자신의 시간 초과다(시험의 기다림 한도가 아니라)
        job1.Release();
        var waking = Assert.Single(engine.Procs);

        var job2 = new ComfyServer(s, engine.Ledger);
        var second = job2.EnsureRunningAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Limit));   // 여전히 응답이 없어 또 시간 초과 — 그래도 새로 띄우지 않았다
        Assert.True(second.IsFaulted);
        Assert.Single(engine.Procs);
        Assert.False(waking.HasExited);
        Assert.True(job2.WeStartedIt);

        s.KeepComfyWarm = false;
        job2.Release();
        Assert.Equal(1, waking.Kills);
    }

    [Fact]
    public async Task Launch_SendsComfyOutputThroughCmdToTheLogFile_NotAPipe()
    {
        // ComfyUI 출력을 익명 파이프로 받으면, 꺼두지 않은(KeepComfyWarm) 서버가 앱이 닫힌 뒤 끊긴 파이프에 쓰다 다음 실행의 KSampler에서 [Errno 22]로 터진다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        s.ComfyPort = 8190;
        s.ComfyExtraArgs = "  --lowvram ";
        var engine = new FakeEngine();
        await using var user = new ComfyServer(s, engine.Ledger);

        await user.EnsureRunningAsync();

        var psi = Assert.Single(engine.Launches);
        var python = Path.Combine(s.ComfyRoot, "python_embeded", "python.exe");
        var main = Path.Combine(s.ComfyRoot, "ComfyUI", "main.py");
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal($"/c \"\"{python}\" -s \"{main}\" --port 8190 --disable-auto-launch --lowvram > \"{ComfyServer.LogPath}\" 2>&1\"", psi.Arguments);
        Assert.Equal(s.ComfyRoot, psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.False(psi.RedirectStandardOutput);
        Assert.False(psi.RedirectStandardError);
    }

    // ── 러너: 본 작업과 결과 창 ──

    [Fact]
    public async Task MainRunEnds_WhileTheResultWindowUsesTheServer_ServerStaysUntilTheWindowCloses()
    {
        // 원래 결함: 본 작업이 띄운 서버를 본 작업이 끝나며 꺼서(KeepComfyWarm=false), 작업 중에 열린 결과 창의 [채색부터 다시]가 도중에 실패했다
        using var t = new TempDir();
        var s = ColorRunSettings(t);
        var engine = new FakeEngine();
        using var main = new PipelineRunner(s, new ComfyServer(s, engine.Ledger));
        var windowServer = new ComfyServer(s, engine.Ledger);
        var window = new PipelineRunner(s, windowServer);   // 결과 창의 러너 — 창이 닫힐 때 Dispose

        // 본 작업이 아직 도는 동안(장 끝 줄을 보낼 때) 결과 창이 같은 서버를 쓰기 시작한다 — RedoPageAsync의 PrepareAsync가 EnsureRunningAsync로 쥐는 것과 같다
        Task? windowUse = null;
        var progress = new HookedProgress(p => { if (p.Done) windowUse ??= windowServer.EnsureRunningAsync(); });
        await main.RunAsync([MissingPage(t)], progress, CancellationToken.None);
        Assert.NotNull(windowUse);
        await windowUse.WaitAsync(Limit);

        var proc = Assert.Single(engine.Procs);   // 본 작업이 띄운 서버 하나를 같이 썼다
        Assert.False(proc.Killed);                // 본 작업이 끝나도 결과 창이 쓰는 중이라 그대로
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));

        window.Dispose();                         // 결과 창이 닫힌다 — 마지막 사용자
        Assert.True(proc.Killed);
    }

    [Fact]
    public async Task ResultWindowRunner_HoldsAcrossManualJobs_AndLetsGoWhenDisposed()
    {
        // 결과 창이 띄운 서버는 아무도 끄지 않아 KeepComfyWarm=false여도 남았다 — 이제 창이 닫힐 때(Dispose) 놓아 꺼진다.
        // 창이 열려 있는 동안은 수동 작업마다 놓지 않는다(작업마다 1~2분씩 다시 띄우지 않게)
        using var t = new TempDir();
        var s = ColorRunSettings(t);
        var engine = new FakeEngine();
        var windowServer = new ComfyServer(s, engine.Ledger);
        var window = new PipelineRunner(s, windowServer);

        await windowServer.EnsureRunningAsync();   // 첫 수동 작업
        await windowServer.EnsureRunningAsync();   // 다음 수동 작업
        var proc = Assert.Single(engine.Procs);
        Assert.False(proc.Killed);

        window.Dispose();
        window.Dispose();   // 두 번 정리해도 한 번만 놓는다
        Assert.Equal(1, proc.Kills);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));
    }

    [Fact]
    public async Task RunCancelledWhileTheEngineStarts_StillLetsGoAndStopsTheServerItStarted()
    {
        // 취소·예외로 끝난 작업도 사용을 놓는다 — 예전에는 RunAsync 끝의 정리가 try 밖이라 [중지]하면 KeepComfyWarm=false여도 서버가 남았다
        using var t = new TempDir();
        var s = ColorRunSettings(t);
        using var cts = new CancellationTokenSource();
        var engine = new FakeEngine { OnLaunch = cts.Cancel };   // 서버를 띄우자마자 [중지]
        using var runner = new PipelineRunner(s, new ComfyServer(s, engine.Ledger));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync([MissingPage(t)], new ProgressLog(), cts.Token));

        Assert.True(Assert.Single(engine.Procs).Killed);
        Assert.Equal(0, engine.Ledger.UsersOf(s.ComfyPort));
    }

    // ── 앱이 끝날 때 ──

    [Fact]
    public async Task AppExit_StopsOurServerAJobStillHolds_ButLeavesPinnedAndOutsideServers()
    {
        // 작업 중에(또는 닫은 결과 창의 작업이 끝나기 전에) 앱을 닫으면 그 작업이 사용을 놓지 못한 채 프로세스가 끝난다 — KeepComfyWarm=false인데 이 앱이 띄운 서버가 남았다.
        // 앱이 끝날 때 이 앱이 띄웠고 고정하지 않은 서버만 끈다: '엔진 켜기'로 켠 서버와 사용자가 따로 띄운 서버는 그대로
        using var t = new TempDir();
        var engine = new FakeEngine();
        var jobSettings = Settings(t, keepWarm: false);
        var pinnedSettings = Settings(t, keepWarm: false);
        pinnedSettings.ComfyPort = jobSettings.ComfyPort + 1;
        var outsideSettings = Settings(t, keepWarm: false);
        outsideSettings.ComfyPort = jobSettings.ComfyPort + 2;
        var job = new ComfyServer(jobSettings, engine.Ledger);
        await job.EnsureRunningAsync();   // 아직 쓰는 중 — 놓기 전에 앱이 끝난다
        await EngineManager.StartAsync(new ComfyServer(pinnedSettings, engine.Ledger), null, CancellationToken.None);
        engine.ExternalServerOn(outsideSettings.ComfyPort);
        var outsideUser = new ComfyServer(outsideSettings, engine.Ledger);
        await outsideUser.EnsureRunningAsync();
        Assert.Equal(2, engine.Procs.Count);
        var (jobProc, pinnedProc) = (engine.Procs[0], engine.Procs[1]);

        engine.Ledger.AppExiting(keepWarm: false);

        Assert.Equal(1, jobProc.Kills);
        Assert.False(pinnedProc.Killed);
        Assert.True(await outsideUser.IsAliveAsync());
        job.Release();   // 늦게 돈 정리가 있어도 다시 끄지 않는다
        Assert.Equal(1, jobProc.Kills);
    }

    [Fact]
    public async Task AppExit_KeepComfyWarm_StopsNothing()
    {
        // KeepComfyWarm=true면 앱이 끝나도 끄지 않는다 — 다음 실행이 그 서버를 그대로 쓴다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine();
        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();

        engine.Ledger.AppExiting(keepWarm: true);

        Assert.False(Assert.Single(engine.Procs).Killed);
    }

    [Fact]
    public async Task AppExit_WhileEngineStartIsStillWakingTheServer_LeavesIt()
    {
        // '엔진 켜기'·미리 켜기가 서버를 깨우는 중에 앱을 닫아도 그 서버는 끄지 않는다 — 다 깨어난 뒤에 닫은 것과 같게(명시적으로 켜라고 한 서버)
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        var start = EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, stop.Token);
        var waking = Assert.Single(engine.Procs);

        engine.Ledger.AppExiting(keepWarm: false);

        Assert.False(waking.Killed);
        stop.Cancel();   // 시험 정리 — 그만둬도 고정은 남는다
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Limit));
        Assert.False(waking.Killed);
    }

    [Fact]
    public void AppExit_ThenAServerIsLaunchedAnyway_ItIsStoppedRightAway()
    {
        // 앱이 끝나며 서버를 끈 바로 그때 다른 스레드의 작업이 서버를 띄웠다(종료와 엇갈린 기동) — 앱이 끝나면 끌 사람이 없으니 적히는 대로 끈다
        var engine = new FakeEngine();
        engine.Ledger.AppExiting(keepWarm: false);

        var late = new FakeProcess(engine, 8188);
        engine.Ledger.Started(8188, late);

        Assert.True(late.Killed);
        Assert.False(engine.Ledger.IsOurs(8188));
    }

    // ── 다른 NekoManga 프로세스가 깨우는 중인 서버 ──

    [Fact]
    public async Task AnotherProcessIsWakingTheServer_WeWaitForIt_InsteadOfLaunchingOneThatDiesAtOnce()
    {
        // 앱을 켜고(미리 켜기) 서버가 깨어나는 1~2분 안에 앱을 한 번 더 켜거나 --run을 돌린 경우 — 장부는 프로세스 안에만 있어 둘째 프로세스는 '꺼져 있다'고만 보고
        // 하나 더 띄웠다. 그 cmd는 첫째가 쥔 comfy.log를 못 열어 곧바로 exit 1로 죽어 작업이 '시작하자마자 종료 (exit 1)'로 실패했다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine(crossProcess: true) { RespondsAfterLaunch = false };
        using var limit = new CancellationTokenSource(Limit);
        var first = new ComfyServer(s, engine.Ledger);             // 먼저 켠 앱
        var second = new ComfyServer(s, engine.OtherProcess());    // 다른 프로세스 — 장부가 따로다
        var firstStart = first.EnsureRunningAsync(null, limit.Token);
        var waking = Assert.Single(engine.Procs);

        var waitingForTheOther = new TaskCompletionSource();
        var secondStart = second.EnsureRunningAsync(new SyncProgress(m => { if (m.Contains("다른 NekoManga")) waitingForTheOther.TrySetResult(); }), limit.Token);
        await waitingForTheOther.Task.WaitAsync(Limit);   // 둘째가 깨어나는 중인 서버를 알아보고 기다린다
        Assert.Single(engine.Procs);
        Assert.False(secondStart.IsCompleted);

        engine.Respond(s.ComfyPort);   // 첫째가 띄운 서버가 다 깨어난다
        await Task.WhenAll(firstStart, secondStart).WaitAsync(Limit);
        Assert.Single(engine.Procs);
        Assert.True(first.WeStartedIt);
        Assert.False(second.WeStartedIt);   // 둘째 프로세스가 띄운 서버가 아니다

        second.Release();                   // KeepComfyWarm=false여도 이 프로세스가 띄우지 않은 서버는 끄지 않는다
        Assert.False(waking.Killed);
        first.Release();                    // 띄운 쪽은 자기 규칙대로 — 마지막 사용자가 놓으면 끈다
        Assert.Equal(1, waking.Kills);
    }

    [Fact]
    public async Task ProcessThatLaunchedTheServerIsGone_TheServerItLeftWakingIsStillWaitedFor()
    {
        // 서버를 띄운 앱이 죽거나 닫혀도 cmd·python은 남아 계속 깨어난다(그리고 comfy.log를 쥐고 있다) — 기동 기록은 띄운 앱이 아니라 **그 서버가 살아 있는 동안** 유효해야 한다.
        // 띄운 앱이 쥐는 잠금으로 만들었다면 앱이 죽는 순간 풀려, 다음 프로세스가 하나 더 띄우고 곧바로 죽는다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine(crossProcess: true) { RespondsAfterLaunch = false };
        using var limit = new CancellationTokenSource(Limit);
        using var gone = new CancellationTokenSource();
        var starting = new ComfyServer(s, engine.Ledger).EnsureRunningAsync(null, gone.Token);
        var waking = Assert.Single(engine.Procs);
        gone.Cancel();   // 그 프로세스는 여기서 끝난다 — 놓지도 끄지도 못한 채
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Limit));
        Assert.False(waking.HasExited);

        var later = new ComfyServer(s, engine.OtherProcess());
        var waitingForTheOther = new TaskCompletionSource();
        var laterStart = later.EnsureRunningAsync(new SyncProgress(m => { if (m.Contains("다른 NekoManga")) waitingForTheOther.TrySetResult(); }), limit.Token);
        await waitingForTheOther.Task.WaitAsync(Limit);
        Assert.Single(engine.Procs);

        engine.Respond(s.ComfyPort);
        await laterStart.WaitAsync(Limit);
        Assert.Single(engine.Procs);
        later.Release();
        Assert.False(waking.Killed);
    }

    [Fact]
    public async Task ServerAnotherProcessWasWakingDies_WeLaunchOurOwn()
    {
        // 다른 프로세스가 띄우던 서버가 깨어나지 못하고 죽으면(기동 실패, 그쪽이 끔) 낡은 기록에 묶이지 않는다 — 기다리던 쪽이 자기 서버를 띄운다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine(crossProcess: true) { RespondsAfterLaunch = false };
        using var limit = new CancellationTokenSource(Limit);
        using var gaveUp = new CancellationTokenSource();
        var firstStart = new ComfyServer(s, engine.Ledger).EnsureRunningAsync(null, gaveUp.Token);
        var dying = Assert.Single(engine.Procs);
        var second = new ComfyServer(s, engine.OtherProcess());
        var waitingForTheOther = new TaskCompletionSource();
        var said = new List<string>();
        var secondStart = second.EnsureRunningAsync(new SyncProgress(m =>
        {
            lock (said) said.Add(m);
            if (m.Contains("다른 NekoManga")) waitingForTheOther.TrySetResult();
        }), limit.Token);
        await waitingForTheOther.Task.WaitAsync(Limit);
        gaveUp.Cancel();   // 띄운 쪽은 물러났다
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStart.WaitAsync(Limit));

        dying.Exit();
        await WaitUntil(() => engine.Procs.Count == 2);
        var ours = engine.Procs[1];
        Assert.False(ours.HasExited);   // 먼저 뜬 서버가 죽은 뒤라 로그를 열 수 있다
        engine.Respond(s.ComfyPort);
        await secondStart.WaitAsync(Limit);

        Assert.True(second.WeStartedIt);
        // 상태 문구도 따라온다: 깨우는 중 → 다른 NekoManga의 것을 기다리는 중 → (그것이 죽어 우리가 띄웠으니) 다시 깨우는 중.
        // 되돌리지 않으면 자기 서버가 깨어나는 1~2분 내내 '다른 NekoManga가 깨우는 중…'이 남는다
        Assert.Equal(3, said.Count);
        Assert.Contains("채색 엔진 깨우는 중", said[0]);
        Assert.Contains("다른 NekoManga", said[1]);
        Assert.Equal(said[0], said[2]);
        second.Release();
        Assert.Equal(1, ours.Kills);    // 자기가 띄운 서버는 KeepComfyWarm=false 규칙대로 끈다
        Assert.Equal(0, dying.Kills);
    }

    [Fact]
    public async Task ServerAnotherProcessIsWakingNeverResponds_WeTimeOutOnOurOwnLimit_WithoutLaunchingOrStoppingAnything()
    {
        // 살아만 있고 끝내 응답하지 않는 서버 — 기다리는 쪽은 자기 한도에서 시간 초과로 끝난다(무한정 기다리지 않는다).
        // 하나 더 띄우지도 않는다: 그 서버가 comfy.log를 쥐고 있어 띄워도 곧바로 죽는다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine(startLimit: TimeSpan.FromMilliseconds(80), crossProcess: true) { RespondsAfterLaunch = false };
        var starting = new ComfyServer(s, engine.Ledger).EnsureRunningAsync();
        var stuck = Assert.Single(engine.Procs);

        var second = new ComfyServer(s, engine.OtherProcess());
        var secondStart = second.EnsureRunningAsync();
        var waited = await Assert.ThrowsAsync<TimeoutException>(() => secondStart.WaitAsync(Limit));
        Assert.True(secondStart.IsFaulted);   // 기동 자신의 시간 초과다(시험의 기다림 한도가 아니라)
        second.Release();
        // 한도까지 기다린 까닭을 알린다 — 남이 깨우던 엔진이 응답하지 않았다는 것과 볼 로그. 예전에는 이 자리에서 하나 더 띄우다 곧바로 exit 1 + 로그 진단으로 끝났다
        Assert.Contains("다른 NekoManga", waited.Message);
        Assert.Contains(ComfyServer.LogPath, waited.Message);

        Assert.Single(engine.Procs);
        Assert.Equal(0, stuck.Kills);
        Assert.False(stuck.HasExited);
        var launched = await Assert.ThrowsAsync<TimeoutException>(() => starting.WaitAsync(Limit));   // 띄운 쪽도 자기 한도에서 끝난다
        Assert.DoesNotContain("다른 NekoManga", launched.Message);   // 자기가 띄운 서버의 시간 초과는 예전 문구 그대로
    }

    [Fact]
    public async Task WaitingForAnotherProcessesServer_CountsFromWhenWeFirstAsked_NotFromWhenWeFoundIt()
    {
        // 남의 서버를 기다리는 한도는 처음 물은 때부터 센다 — 관문 앞에서(이 프로세스의 앞선 기동이 같은 서버를 한도까지 기다리는 동안) 보낸 시간도 든다.
        // 남의 서버임을 알아본 때부터 새로 세면, 끝내 응답하지 않는 서버를 이 프로세스의 사용자들이 차례로 한도만큼씩(4분씩) 기다린다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var startLimit = TimeSpan.FromMilliseconds(300);
        var engine = new FakeEngine(startLimit: startLimit, crossProcess: true) { RespondsAfterLaunch = false };
        using var gone = new CancellationTokenSource();
        var launching = new ComfyServer(s, engine.Ledger).EnsureRunningAsync(null, gone.Token);   // 다른 프로세스가 띄웠다
        var stuck = Assert.Single(engine.Procs);
        gone.Cancel();   // 그 프로세스는 물러났다(더는 응답을 묻지 않는다) — 서버는 살아만 있다
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launching.WaitAsync(Limit));

        var mine = engine.OtherProcess();
        var gate = mine.StartGate(s.ComfyPort);
        await gate.WaitAsync();   // 이 프로세스의 앞선 기동이 관문을 쥐고 있다
        var user = new ComfyServer(s, mine);
        var sinceAsked = Stopwatch.StartNew();
        var start = user.EnsureRunningAsync();   // 여기서 '처음 물은 때'가 정해지고 관문 앞에서 멈춘다
        Assert.False(start.IsCompleted);
        while (sinceAsked.Elapsed < startLimit + TimeSpan.FromMilliseconds(100)) await Task.Delay(20);   // 관문 앞에서 한도를 다 보낸다
        int checksBefore = engine.AliveChecks;
        gate.Release();

        await Assert.ThrowsAsync<TimeoutException>(() => start.WaitAsync(Limit));
        // 관문을 지나며 한 번 묻고, 남의 서버임을 알아본 그 자리에서 끝난다 — 한도를 새로 받았다면 그 한도 내내 다시 묻는다(5ms마다)
        Assert.Equal(1, engine.AliveChecks - checksBefore);
        Assert.Single(engine.Procs);
        Assert.Equal(0, stuck.Kills);
        user.Release();
    }

    [Fact]
    public async Task ServerAnotherProcessLaunchedIsOnAnotherPort_FailsRightAwayWithTheReason()
    {
        // 두 프로세스의 포트가 다르면 — comfy.log가 포트와 상관없이 하나라 앱이 띄우는 서버도 한 번에 하나뿐이다. 다른 포트의 서버가 살아 있으면 기다려도 우리 포트로는 오지 않고,
        // 하나 더 띄워도 곧바로 죽는다(exit 1 — 그러면 남의 서버 로그를 읽고 엉뚱한 원인을 짚는다). 기다리지도 띄우지도 않고 까닭을 알린다
        using var t = new TempDir();
        var a = Settings(t, keepWarm: false);
        var b = Settings(t, keepWarm: false);
        b.ComfyPort = a.ComfyPort + 1;
        var engine = new FakeEngine(crossProcess: true) { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        var starting = new ComfyServer(a, engine.Ledger).EnsureRunningAsync(null, stop.Token);
        var other = Assert.Single(engine.Procs);

        var second = new ComfyServer(b, engine.OtherProcess());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second.EnsureRunningAsync().WaitAsync(Limit));
        second.Release();

        Assert.Contains($"포트 {a.ComfyPort}", ex.Message);
        Assert.Contains($"포트 {b.ComfyPort}", ex.Message);
        Assert.Single(engine.Procs);
        Assert.False(other.Killed);
        stop.Cancel();   // 시험 정리
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Limit));
    }

    // ── 진짜 기동 기록 파일 (ComfyServer.StartFile) ──

    [Fact]
    public async Task StartRecordFile_ALiveLaunchIsSeenThroughTheFile_AndForgottenOnceThatProcessIsGone()
    {
        // 서버 대신 가만히 있는 cmd 하나를 띄워 본다(ComfyUI는 띄우지 않는다). 기록은 적힌 프로세스가 살아 있는 동안만 믿는다 — 죽은 프로세스가 남긴 기록이 다음 기동을 막지 않는다
        using var t = new TempDir();
        var path = t.At("state", "comfy.start");            // 폴더가 없으면 만든다
        var mine = new ComfyServer.StartFile(path);
        var theirs = new ComfyServer.StartFile(path);       // 다른 프로세스 — 같은 파일을 본다
        ComfyServer.CmdProcess? cmd = null;
        try
        {
            var first = await mine.ClaimAsync(8188, () => cmd = IdleCmd(), CancellationToken.None);
            Assert.True(first.Ours);
            Assert.NotNull(cmd);

            var seen = await theirs.ClaimAsync(8190, () => throw new InvalidOperationException("살아 있는 기록이 있는데 띄웠다"), CancellationToken.None);
            Assert.False(seen.Ours);
            Assert.Equal(8188, seen.Port);                  // 포트는 띄운 쪽의 것 — 다르면 부른 쪽이 까닭을 알린다
            Assert.False(seen.Proc.HasExited);
            seen.Proc.Kill();                               // 다른 프로세스가 띄운 서버는 끄라고 해도 끄지 않는다
            await Task.Delay(200);
            Assert.False(cmd.HasExited);

            cmd.Kill();
            await WaitUntil(() => cmd.HasExited);
            Assert.True(seen.Proc.HasExited);               // 지켜보던 쪽도 죽은 것을 안다
            var fresh = new FakeProcess(new FakeEngine(), 8190);
            var next = await theirs.ClaimAsync(8190, () => fresh, CancellationToken.None);
            Assert.True(next.Ours);                         // 낡은 기록 — 새로 띄운다
            Assert.Same(fresh, next.Proc);
        }
        finally
        {
            try { if (cmd is { HasExited: false }) cmd.Kill(); }
            catch { }
        }
    }

    [Theory]
    [InlineData("")]                        // 빈 파일
    [InlineData("깨진 기록")]
    [InlineData("8188 2147483647 1")]       // 없는 pid — 띄운 서버가 죽었다(PC를 다시 켰다)
    [InlineData("8188 {self} 1")]           // 살아 있는 pid지만 시작 시각이 다르다 — 그 pid를 물려받은 다른 프로세스
    public async Task StartRecordFile_StaleOrBrokenRecord_DoesNotBlockTheLaunch(string record)
    {
        using var t = new TempDir();
        var path = t.At("comfy.start");
        File.WriteAllText(path, record.Replace("{self}", Environment.ProcessId.ToString()));
        var fresh = new FakeProcess(new FakeEngine(), 8188);

        var claim = await new ComfyServer.StartFile(path).ClaimAsync(8188, () => fresh, CancellationToken.None);

        Assert.True(claim.Ours);
        Assert.Same(fresh, claim.Proc);
    }

    [Fact]
    public async Task StartRecordFile_WhileAnotherProcessHoldsIt_WaitsForItsTurn()
    {
        // 관문은 기록 파일을 혼자 여는 것이다 — 다른 프로세스가 보고·띄우고·적는 동안에는 기다린다. 쥔 프로세스가 죽으면 OS가 핸들을 닫으니 남는 잠금이 없다
        using var t = new TempDir();
        var path = t.At("comfy.start");
        var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var fresh = new FakeProcess(new FakeEngine(), 8188);
        int launches = 0;

        var claim = new ComfyServer.StartFile(path).ClaimAsync(8188, () => { launches++; return fresh; }, CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(0, launches);
        holder.Dispose();

        Assert.True((await claim.WaitAsync(Limit)).Ours);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task StartRecordFile_WhileOneProcessIsLaunching_AnotherCannotLookOrLaunch()
    {
        // 관문은 읽고·띄우고·적는 내내 닫혀 있어야 한다 — 띄우는 중(기록은 아직 비어 있다)에 온 다른 프로세스가 파일을 같이 열 수 있으면 빈 기록을 보고 하나 더 띄운다
        using var t = new TempDir();
        var path = t.At("comfy.start");
        ComfyServer.CmdProcess? cmd = null;
        int otherLaunches = 0;
        Task<ComfyServer.Claimed>? other = null;
        try
        {
            var first = await new ComfyServer.StartFile(path).ClaimAsync(8188, () =>
            {
                other = Task.Run(() => new ComfyServer.StartFile(path).ClaimAsync(8188, () =>
                {
                    Interlocked.Increment(ref otherLaunches);
                    return new FakeProcess(new FakeEngine(), 8188);
                }, CancellationToken.None));
                Assert.False(other.Wait(200));   // 관문 앞에서 기다린다
                Assert.Equal(0, Volatile.Read(ref otherLaunches));
                return cmd = IdleCmd();
            }, CancellationToken.None);

            Assert.True(first.Ours);
            var seen = await other!.WaitAsync(Limit);
            Assert.False(seen.Ours);             // 첫째가 적고 나간 기록을 보고 띄우지 않는다
            Assert.Equal(0, Volatile.Read(ref otherLaunches));
        }
        finally
        {
            try { if (cmd is { HasExited: false }) cmd.Kill(); }
            catch { }
        }
    }

    [Fact]
    public async Task StartRecordFile_ManyProcessesClaimAtOnce_ExactlyOneLaunches()
    {
        // 진짜 파일에 16곳이 한꺼번에, 5번 — 관문(혼자 열기)이 없으면 여럿이 빈 기록을 보고 저마다 띄운다(검수 실측: FileShare.ReadWrite로 바꾸면 16곳 중 12곳이 띄웠다)
        using var t = new TempDir();
        for (int round = 0; round < 5; round++)
        {
            var path = t.At("round" + round, "comfy.start");
            var cmds = new ConcurrentBag<ComfyServer.CmdProcess>();
            try
            {
                var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var claiming = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
                {
                    var board = new ComfyServer.StartFile(path);   // 프로세스마다 따로 — 같은 파일만 같이 본다
                    await go.Task;
                    return await board.ClaimAsync(8188 + i % 2, () => { var c = IdleCmd(); cmds.Add(c); return c; }, CancellationToken.None);
                })).ToArray();
                go.SetResult();
                var claims = await Task.WhenAll(claiming).WaitAsync(Limit);

                Assert.Single(cmds);
                var winner = Assert.Single(claims, c => c.Ours);
                Assert.All(claims, c => Assert.Equal(winner.Port, c.Port));   // 나머지는 모두 그 서버(와 그 포트)를 본다
            }
            finally
            {
                foreach (var c in cmds)
                    try { if (!c.HasExited) c.Kill(); }
                    catch { }
            }
        }
    }

    // ── 엔진 끄기: 쓰는 작업이 있는가, 끈 뒤의 장부 ──

    [Fact]
    public async Task JobsUsing_CountsJobsHoldingAServerOnAnyPort()
    {
        // 설정 창의 [끄기]와 자가 학습이 끄기 전에 보는 값 — 엔진 끄기는 ComfyRoot의 python을 포트와 상관없이 죽이므로 포트를 가리지 않고 센다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var moved = Settings(t, keepWarm: true);
        moved.ComfyPort = s.ComfyPort + 1;   // 작업 중에 설정의 포트를 바꾼 뒤 시작한 다른 작업
        var engine = new FakeEngine();
        Assert.Equal(0, engine.Ledger.JobsUsing);

        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();
        await job.EnsureRunningAsync();   // 같은 작업은 한 번만
        Assert.Equal(1, engine.Ledger.JobsUsing);
        var window = new ComfyServer(moved, engine.Ledger);
        await window.EnsureRunningAsync();
        Assert.Equal(2, engine.Ledger.JobsUsing);

        job.Release();
        Assert.Equal(1, engine.Ledger.JobsUsing);
        window.Release();
        window.Release();
        Assert.Equal(0, engine.Ledger.JobsUsing);
    }

    [Fact]
    public async Task JobsUsing_DoesNotCountEngineStart()
    {
        // '엔진 켜기'·미리 켜기는 작업이 아니다 — 켜 둔 엔진도, 깨우느라 쥐고 있는 동안도 '쓰는 작업 있음'으로 보지 않는다(끄기를 막거나 묻지 않는다)
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        await EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, CancellationToken.None);
        Assert.Equal(0, engine.Ledger.JobsUsing);

        var waking = new FakeEngine { RespondsAfterLaunch = false };
        using var stop = new CancellationTokenSource();
        var start = EngineManager.StartAsync(new ComfyServer(s, waking.Ledger), null, stop.Token);
        Assert.Equal(1, waking.Ledger.UsersOf(s.ComfyPort));   // 켜기가 쥐고 깨우는 중
        Assert.Equal(0, waking.Ledger.JobsUsing);
        stop.Cancel();   // 시험 정리
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Limit));
    }

    [Fact]
    public async Task EngineStop_ForgetsTheServer_SoTheNextUseStartsAFreshOne_WhichIsNotPinned()
    {
        // 엔진 끄기는 ComfyRoot의 python을 죽인다 — cmd는 곧 뒤따라 끝나지만 그 사이에는 아직 살아 있다. 장부가 그 cmd를 '깨어나는 중인 우리 서버'로 남겨 두면
        // 곧바로 다시 쓰는 쪽이 새로 띄우지 않고 죽어 가는 것을 기다린다. '엔진 켜기' 고정은 꺼진 서버의 것이었다 — 새로 띄운 서버는 고정 없이 시작한다(Ledger.Started)
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        await EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, CancellationToken.None);   // '엔진 켜기'로 고정한 서버
        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();   // 작업(결과 창)이 쓰는 중에 — 사용자가 확인하고 — 끈다
        var stopped = Assert.Single(engine.Procs);

        int killed = EngineManager.Stop(s, engine.Ledger, _ => { engine.Silence(s.ComfyPort); return 1; });   // python이 죽어 응답이 끊겼다. cmd는 아직 끝나기 전

        Assert.Equal(1, killed);
        Assert.True(stopped.HasExited);                          // 장부가 남은 cmd도 내린다
        Assert.False(job.WeStartedIt);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));     // 쥔 작업은 그대로 센다 — 끝나며 놓는다
        Assert.Equal(1, engine.Ledger.JobsUsing);

        await job.EnsureRunningAsync().WaitAsync(Limit);         // 그 작업이 다시 쓰면 새 서버를 띄운다
        Assert.Equal(2, engine.Procs.Count);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        job.Release();
        Assert.Equal(1, engine.Procs[1].Kills);                  // 고정은 꺼진 서버의 것이었다 — 새 서버는 KeepComfyWarm=false 규칙대로 꺼진다
        Assert.Equal(0, engine.Ledger.JobsUsing);
    }

    [Fact]
    public async Task EngineStop_WithNoServerOfOurs_OnlyReportsWhatWasKilled()
    {
        // 장부는 이 앱이 띄운 서버만 내린다 — 사용자가 따로 띄운 서버를 죽일지는 EngineManager.Stop의 ComfyRoot 검사(그 폴더의 python인가)가 정한다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: false);
        var engine = new FakeEngine();
        engine.ExternalServerOn(s.ComfyPort);
        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();

        Assert.Equal(0, EngineManager.Stop(s, engine.Ledger, _ => 0));   // 다른 폴더의 ComfyUI라 죽인 것이 없다

        Assert.Empty(engine.Procs);
        Assert.True(await job.IsAliveAsync());
        Assert.Equal(1, engine.Ledger.JobsUsing);
        job.Release();
        Assert.True(await job.IsAliveAsync());
    }

    [Fact]
    public async Task SelfTraining_WhileAJobUsesTheEngine_RefusesToStart_AndLeavesTheServerRunning()
    {
        // 자가 학습은 GPU를 비우려고 엔진을 끈다 — 예전에는 쓰는 작업이 있어도 조용히 꺼서 그 작업이 도중에 연결 오류로 실패했다.
        // 켜져만 있고 쓰는 작업이 없는 엔진은 예전처럼 끄고 학습한다(EngineBusy=false)
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine();
        // 학습기의 설정에는 ComfyUI를 두지 않는다 — 이 검사가 빠지더라도 학습이 실제로 돌지 않고 CanTrain에서 멈추게(쓰는 작업은 포트·폴더를 가리지 않고 장부로 센다).
        // 엔진 끄기도 가짜다(실제 python을 건드리지 않는다)
        int stops = 0;
        var trainer = new SelfTrainer(new AppSettings { RuntimeRoot = t.At("no-comfy") }, engine.Ledger, _ => { stops++; return 0; });
        Assert.Null(await trainer.EngineBusyAsync());
        await EngineManager.StartAsync(new ComfyServer(s, engine.Ledger), null, CancellationToken.None);
        Assert.Null(await trainer.EngineBusyAsync());

        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();
        var why = await trainer.EngineBusyAsync();
        Assert.NotNull(why);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(null, CancellationToken.None));   // 재료·파일을 보기도 전에 그만둔다

        Assert.Equal(why, ex.Message);
        Assert.Contains("엔진을 쓰는 작업", why);
        // 결과 창과 설정 창은 둘 다 모달이라 같이 떠 있지 못한다 — '결과 창을 닫은 뒤'는 할 수 없는 안내라 '작업이 끝난 뒤'라고 한다
        Assert.Contains("작업이 끝난 뒤", why);
        Assert.DoesNotContain("결과 창", why);
        Assert.Equal(0, stops);
        Assert.False(Assert.Single(engine.Procs).Killed);
        Assert.Equal(1, engine.Ledger.UsersOf(s.ComfyPort));
        job.Release();
        Assert.Null(await trainer.EngineBusyAsync());
    }

    [Fact]
    public async Task SelfTraining_AJobStillHoldsAnEngineThatIsNoLongerAlive_IsNotRefused()
    {
        // 엔진이 꺼진 뒤에도(사용자가 확인하고 끔·충돌·직접 닫음) 그것을 쓰던 작업은 끝날 때까지 사용을 쥐고 있다. 학습이 끄는 것은 살아 있는 엔진뿐이라
        // 이때는 끌 것이 없다 — '지금 끄면 그 작업이 도중에 실패해요'라며 그만두면 틀린 말이고, v1.8.2는 그대로 학습을 시작했다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        var engine = new FakeEngine();
        int stops = 0;
        var trainer = new SelfTrainer(new AppSettings { RuntimeRoot = t.At("no-comfy") }, engine.Ledger, _ => { stops++; return 0; });
        var job = new ComfyServer(s, engine.Ledger);
        await job.EnsureRunningAsync();
        Assert.NotNull(await trainer.EngineBusyAsync());

        Assert.Single(engine.Procs).Exit();   // 엔진이 죽었다 — 작업은 아직 놓지 않았다
        Assert.Equal(1, engine.Ledger.JobsUsing);

        Assert.Null(await trainer.EngineBusyAsync());
        await trainer.StopEngineAsync(null);   // 끌 것이 없다 — 그만두지도 끄지도 않는다
        Assert.Equal(0, stops);
        // 학습 전체로도 엔진 때문에 그만두지 않는다 — 이 시험의 학습기에는 ComfyUI가 없어 그다음 검사(CanTrain)에서 멈춘다
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(null, CancellationToken.None));
        Assert.DoesNotContain("엔진을 쓰는 작업", ex.Message);
        job.Release();
    }

    [Fact]
    public async Task SelfTraining_AJobTakesTheEngineWhileWeAskWhetherItIsAlive_StopsNothing()
    {
        // 학습 버튼을 누를 때는 쓰는 작업이 없었는데, 엔진이 살아 있는지 묻는 사이(HTTP)에 작업이 엔진을 쥐었다 — 끄기 바로 앞에서 다시 보고 끄지 않는다
        using var t = new TempDir();
        var s = Settings(t, keepWarm: true);
        ComfyServer.Ledger ledger = null!;
        object? job = null;
        ledger = new ComfyServer.Ledger(
            _ =>
            {
                if (job != null) ledger.Hold(job, s.ComfyPort, keepWarm: true);
                return Task.FromResult(true);   // 사용자가 따로 띄워 둔 엔진 — 늘 응답한다
            },
            _ => throw new InvalidOperationException("살아 있는 엔진이 있는데 띄웠다"));
        int stops = 0;
        var trainer = new SelfTrainer(s, ledger, _ => { stops++; return 1; });

        await trainer.StopEngineAsync(null);   // 켜져만 있고 쓰는 작업이 없는 엔진은 예전처럼 끄고 시작한다
        Assert.Equal(1, stops);

        job = new object();   // 이제부터는 살아 있는지 묻는 바로 그때 작업이 쥔다
        Assert.Null(await trainer.EngineBusyAsync());   // 버튼을 누를 때는 쓰는 작업이 없다
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.StopEngineAsync(null));

        Assert.Contains("엔진을 쓰는 작업", ex.Message);
        Assert.Equal(1, stops);
        Assert.Equal(1, ledger.JobsUsing);
    }

    // ── 도우미 ──

    /// <summary>가만히 살아 있는 진짜 프로세스 — 입력을 기다리는 cmd(창 없음). 입력 파이프를 이 시험이 쥐고 있는 동안 끝나지 않는다. 다 쓰면 Kill.</summary>
    private static ComfyServer.CmdProcess IdleCmd() => new(Process.Start(new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = "/k",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!);

    /// <summary>임시 폴더에 가짜 ComfyUI 설치(빈 python.exe·main.py)를 둔 설정 — EnsureRunningAsync는 띄우기 전에 두 파일이 있는지 본다.</summary>
    private static AppSettings Settings(TempDir t, bool keepWarm)
    {
        var s = new AppSettings { RuntimeRoot = t.At("runtime"), KeepComfyWarm = keepWarm };
        FakeComfyInstall(s);
        return s;
    }

    /// <summary>채색만 켠 러너 설정(KeepComfyWarm=false) — 번역·검열 제거 모델 없이 ComfyUI만 쓴다.</summary>
    private static AppSettings ColorRunSettings(TempDir t)
    {
        var s = EnginelessSettings(t);
        s.EnableAutoColor = true;
        s.KeepComfyWarm = false;
        FakeComfyInstall(s);
        return s;
    }

    private static void FakeComfyInstall(AppSettings s)
    {
        foreach (var f in new[] { Path.Combine(s.ComfyRoot, "python_embeded", "python.exe"), Path.Combine(s.ComfyRoot, "ComfyUI", "main.py") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, []);
        }
    }

    /// <summary>없는 원고 — 그 장은 채색 업로드 전에(원고를 읽다가) 실패하므로 HTTP 없이 작업이 끝까지 돈다.</summary>
    private static string MissingPage(TempDir t) => t.At("01.png");

    /// <summary>다른 스레드에서 도는 기동이 cond에 이를 때까지 기다린다 — Limit 안에 이르지 않으면 실패.</summary>
    private static async Task WaitUntil(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > Limit) throw new TimeoutException("기다리던 상태가 오지 않았어요");
            await Task.Delay(5);
        }
    }

    /// <summary>프로세스·HTTP 없는 가짜 엔진. 띄운 서버는 곧바로 응답하고(RespondsAfterLaunch), 끄거나(Kill) 죽으면(Exit) 응답하지 않는다.
    /// startLimit: 띄운 뒤 응답을 기다리는 한도(기본 4분) — 시간 초과를 볼 때만 줄인다.
    /// crossProcess: 한 PC의 NekoManga 프로세스 여럿을 본다 — 장부는 프로세스마다 따로(Ledger, OtherProcess()), 서버·기동 기록(FakeBoard)·comfy.log는 같이.
    /// 이때는 진짜처럼 먼저 뜬 서버가 살아 있는 동안 하나 더 띄운 cmd가 곧바로 죽는다(comfy.log 잠김).</summary>
    private sealed class FakeEngine
    {
        private readonly object _gate = new();
        private readonly HashSet<int> _alive = [];
        private readonly List<FakeProcess> _procs = [];
        private readonly List<ProcessStartInfo> _launches = [];
        private readonly TimeSpan? _startLimit;
        private readonly FakeBoard? _board;
        private int _checks;

        public FakeEngine(TimeSpan? startLimit = null, bool crossProcess = false)
        {
            _startLimit = startLimit;
            _board = crossProcess ? new FakeBoard() : null;
            Ledger = NewLedger();
        }

        public ComfyServer.Ledger Ledger { get; }
        /// <summary>같은 PC의 다른 NekoManga 프로세스의 장부 — 누가 서버를 쓰는지·띄웠는지는 서로 모르고, 서버와 기동 기록만 같이 본다.</summary>
        public ComfyServer.Ledger OtherProcess() => NewLedger();
        private ComfyServer.Ledger NewLedger() =>
            new(IsAliveAsync, Launch, poll: TimeSpan.FromMilliseconds(5), startLimit: _startLimit, board: _board);
        public bool RespondsAfterLaunch { get; init; } = true;
        public Action? OnLaunch { get; init; }
        public IReadOnlyList<FakeProcess> Procs { get { lock (_gate) return _procs.ToList(); } }
        public IReadOnlyList<ProcessStartInfo> Launches { get { lock (_gate) return _launches.ToList(); } }
        /// <summary>서버가 살아 있는지 물어본 횟수 — 다른 스레드의 기동이 어디까지 왔는지 볼 때 쓴다.</summary>
        public int AliveChecks { get { lock (_gate) return _checks; } }

        /// <summary>사용자가 이 포트에 따로 띄운 ComfyUI — 이 앱이 띄운 것이 아니다.</summary>
        public void ExternalServerOn(int port) => Respond(port);

        public void Respond(int port) { lock (_gate) _alive.Add(port); }

        public void Silence(int port) { lock (_gate) _alive.Remove(port); }

        private Task<bool> IsAliveAsync(string baseUrl)
        {
            lock (_gate)
            {
                _checks++;
                return Task.FromResult(_alive.Contains(new Uri(baseUrl).Port));
            }
        }

        private ComfyServer.IServerProcess Launch(ProcessStartInfo psi)
        {
            var port = int.Parse(Regex.Match(psi.Arguments, @"--port (\d+)").Groups[1].Value);
            var p = new FakeProcess(this, port);
            lock (_gate)
            {
                // 진짜처럼: 먼저 뜬 서버가 살아 있으면(comfy.log를 쥐고 있다 — 포트가 달라도) 늦은 cmd는 그 파일을 못 열어 python을 띄우기도 전에 exit 1로 끝난다
                bool logHeld = _board != null && _procs.Any(q => !q.HasExited);
                _launches.Add(psi);
                _procs.Add(p);
                if (logHeld) p.DieAtOnce();
                else if (RespondsAfterLaunch) _alive.Add(port);
            }
            OnLaunch?.Invoke();
            return p;
        }
    }

    private sealed class FakeProcess(FakeEngine engine, int port) : ComfyServer.IServerProcess
    {
        public int Kills { get; private set; }
        public bool Killed => Kills > 0;
        public bool HasExited { get; private set; }
        public int ExitCode => 1;

        public void Kill()
        {
            Kills++;
            Exit();
        }

        /// <summary>밖에서 죽었다 — 엔진 끄기(EngineManager.Stop)·충돌.</summary>
        public void Exit()
        {
            HasExited = true;
            engine.Silence(port);
        }

        /// <summary>뜨지도 못하고 끝났다(comfy.log 잠김) — 그 포트에서 응답하던 다른 서버는 그대로다.</summary>
        public void DieAtOnce() => HasExited = true;
    }

    /// <summary>프로세스 사이의 기동 기록 가짜 — 진짜(ComfyServer.StartFile)와 같은 규칙: 적힌 서버가 살아 있으면 띄우지 않고 그것을, 아니면 띄워 적는다. 한 번에 한 프로세스만.
    /// 남이 띄운 서버도 가짜 프로세스를 그대로 돌려준다 — 기다리던 쪽이 실수로 끄면 Kills로 드러난다.</summary>
    private sealed class FakeBoard : ComfyServer.IStartBoard
    {
        private readonly object _gate = new();
        private (int Port, ComfyServer.IServerProcess Proc)? _record;

        public Task<ComfyServer.Claimed> ClaimAsync(int port, Func<ComfyServer.IServerProcess> launch, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_record is { } r && !r.Proc.HasExited) return Task.FromResult(new ComfyServer.Claimed(r.Port, r.Proc, false));
                var p = launch();
                _record = (port, p);
                return Task.FromResult(new ComfyServer.Claimed(port, p, true));
            }
        }
    }

    private sealed class SyncProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }

    private sealed class HookedProgress(Action<PageProgress> onReport) : IProgress<PageProgress>
    {
        public void Report(PageProgress value) => onReport(value);
    }
}
