using System.Diagnostics;
using System.Text.RegularExpressions;
using NekoManga.Core.Comfy;
using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>ComfyUI 서버 공유 — 이 앱 안의 사용자(본 작업 러너·결과 창 러너·표지 색 뽑기·엔진 켜기)가 서버 하나를 같이 쓰고,
/// KeepComfyWarm=false면 이 앱이 띄운 서버를 마지막 사용자가 놓을 때만(놓지 못한 채 앱이 끝나면 그때) 끈다.
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

    // ── 도우미 ──

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
    /// startLimit: 띄운 뒤 응답을 기다리는 한도(기본 4분) — 시간 초과를 볼 때만 줄인다.</summary>
    private sealed class FakeEngine
    {
        private readonly object _gate = new();
        private readonly HashSet<int> _alive = [];
        private readonly List<FakeProcess> _procs = [];
        private readonly List<ProcessStartInfo> _launches = [];
        private int _checks;

        public FakeEngine(TimeSpan? startLimit = null) =>
            Ledger = new ComfyServer.Ledger(IsAliveAsync, Launch, poll: TimeSpan.FromMilliseconds(5), startLimit: startLimit);

        public ComfyServer.Ledger Ledger { get; }
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
                _launches.Add(psi);
                _procs.Add(p);
                if (RespondsAfterLaunch) _alive.Add(port);
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
