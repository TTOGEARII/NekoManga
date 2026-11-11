using System.Diagnostics;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI portable을 헤드리스(브라우저 없이 API 서버만)로 띄우고 내린다.
/// 이미 다른 ComfyUI가 같은 포트에 떠 있으면 그대로 재사용한다.
///
/// 서버는 포트마다 하나를 이 앱 안의 모든 사용자(본 작업 러너·결과 창 러너·표지 색 뽑기)가 같이 쓴다 — EnsureRunningAsync가 이 인스턴스의
/// 사용을 잡고(인스턴스마다 한 번) Release/DisposeAsync가 놓는다. KeepComfyWarm=false면 이 앱이 띄운 서버를 **마지막 사용자가 놓을 때만** 끈다(Ledger) —
/// 놓지 못한 채 앱이 끝나면 그때 끈다(AppExiting). '엔진 켜기'로 고정한 서버는 어느 때도 끄지 않는다(엔진 끄기 전까지).
/// 예전에는 인스턴스마다 자기가 띄운 프로세스만 쥐고 따로 껐다 — 본 작업이 띄운 서버는 본 작업이 끝나는 순간 꺼져 결과 창에서 돌던
/// [채색부터 다시]가 실패했고, 결과 창이 띄운 서버는 아무도 끄지 않아 남았다.
///
/// 장부는 프로세스 안에만 있다. 다른 NekoManga 프로세스(앱을 한 번 더 켰거나, 미리 켜기 중인 앱 옆에서 돈 --run)와는 띄우기만 맞춘다(StartFile) —
/// 그쪽이 띄워 깨어나는 중인 서버는 하나 더 띄우지 않고 기다렸다가 쓴다. 그 서버는 이 프로세스가 띄운 것이 아니라 끄지 않고, 그쪽이 끄는 것도 막지 못한다.
///
/// 알려진 한계 — 프로세스 사이에서는 누가 쓰는 중인지 세지 않는다(일부러 만들지 않았다): 서버를 띄운 프로세스가 KeepComfyWarm=false면 자기 작업이 끝날 때
/// (작업 중에 앱이 끝나면 그때, AppExiting) 그 서버를 끈다. 합류한 다른 프로세스가 작업 중이어도 알지 못하고 끄므로 그쪽 작업은 도중에 연결 오류로 실패한다.
/// 기본 설정에서는 일어나지 않는다: KeepComfyWarm=true면 놓을 때도 앱이 끝날 때도 끄지 않고, 미리 켜기('엔진 켜기')로 띄운 서버는 고정이라 작업이 끝나도 남는다.</summary>
public class ComfyServer(AppSettings settings) : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Ledger _ledger = Ledger.App;

    /// <summary>시험용 — 프로세스·HTTP 대신 가짜를 넣은 장부로 공유·놓기 규칙만 본다.</summary>
    internal ComfyServer(AppSettings s, Ledger ledger) : this(s) => _ledger = ledger;

    public string BaseUrl => $"http://127.0.0.1:{settings.ComfyPort}";
    /// <summary>지금 포트의 서버를 이 앱이 띄웠고 아직 살아 있는가 — 사용자가 따로 띄운 ComfyUI면 false다(그런 서버는 끄지 않는다).</summary>
    public bool WeStartedIt => _ledger.IsOurs(settings.ComfyPort);

    /// <summary>ComfyUI의 stdout/stderr가 쌓이는 로그 (노드 오류의 파이썬 트레이스백이 여기 남는다).</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoManga", "comfy.log");

    /// <summary>프로세스 사이의 기동 기록(StartFile) — 로그 옆에 둔다. 로그가 포트와 상관없이 하나라 기록도 하나다.</summary>
    internal static string StartRecordPath => Path.Combine(Path.GetDirectoryName(LogPath)!, "comfy.start");

    private const string WakingText = "채색 엔진 깨우는 중... (첫 시작은 1~2분)";

    public Task<bool> IsAliveAsync() => _ledger.IsAliveAsync(BaseUrl);

    /// <summary>서버가 떠 있지 않으면 시작하고 응답할 때까지 기다린다. 이 인스턴스가 서버를 쓰기 시작한 것으로 센다 — 다 쓰면 Release(DisposeAsync).</summary>
    public async Task EnsureRunningAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        int port = settings.ComfyPort;
        var baseUrl = $"http://127.0.0.1:{port}";
        // 살아 있는지 보기 전에 쥔다 — 보고 나서 쥐면 그 사이 마지막 사용자가 놓아 서버가 꺼질 수 있다
        _ledger.Hold(this, port, settings.KeepComfyWarm);
        if (await _ledger.IsAliveAsync(baseUrl)) return;

        var root = settings.ComfyRoot;
        var python = Path.Combine(root, "python_embeded", "python.exe");
        var main = Path.Combine(root, "ComfyUI", "main.py");
        if (!File.Exists(python) || !File.Exists(main))
            throw new FileNotFoundException("ComfyUI가 설치되어 있지 않아요. 설정 → 구성요소에서 설치해 주세요.", main);

        status?.Report(WakingText);

        // 한 포트에 서버는 하나 — 다른 사용자가 띄우는 중이면(앱 시작 때의 미리 켜기 등) 기다렸다가 그것을 쓴다.
        // 둘 다 띄우면 늦은 cmd는 먼저 뜬 cmd가 쥔 comfy.log(포트와 상관없이 하나)를 열지 못해 python을 띄우기도 전에 곧바로 exit 1로 끝나
        // 그 작업이 '시작하자마자 종료'로 실패하고, 장부에 죽은 쪽이 적히면 살아 있는 서버를 아무도 끄지 못한다
        var gate = _ledger.StartGate(port);
        var asked = DateTime.UtcNow;   // 기다리는 한도는 관문 앞에서 보낸 시간까지 센다 — 끝내 응답하지 않는 서버를 여럿이 차례로 한도만큼씩 기다리지 않게
        await gate.WaitAsync(ct);
        try
        {
            if (await _ledger.IsAliveAsync(baseUrl)) return;

            // 띄운 쪽이 기다리기를 그만둔(취소·시간 초과) 우리 서버가 아직 깨어나는 중이면 새로 띄우지 않고 그것을 기다린다 — 하나 더 띄우면 위처럼 곧바로 죽는다.
            // 그 프로세스가 죽었을 때만 새로 띄운다
            var proc = _ledger.LiveProc(port);
            bool ours = proc != null;   // false = 다른 NekoManga 프로세스가 띄운 서버 — 지켜보며 기다리기만 한다(장부에 적지 않으니 이 프로세스는 끄지 않는다)
            var deadline = asked + _ledger.StartLimit;
            bool toldWaitingForOther = false;   // 상태 문구가 '다른 NekoManga가 깨우는 중…'으로 바뀌어 있다
            while (true)
            {
                if (proc == null)
                {
                    // 출력은 cmd로 **로그 파일**에 리다이렉트한다 — RedirectStandardOutput(익명 파이프)을 쓰면 안 된다.
                    // 파이프의 읽는 쪽은 우리 프로세스가 쥐고 있어서, KeepComfyWarm으로 서버를 남긴 채 앱이 종료되면
                    // 고아가 된 ComfyUI의 stdout/stderr가 끊긴 파이프를 가리킨다. 다음 실행이 그 서버를 재사용하면
                    // 진행바(tqdm)를 쓰는 순간 파이썬이 OSError를 던져 **KSampler 노드에서 "[Errno 22] Invalid argument"**로
                    // 터진다 (실제 발생 — 단계마다 출력하는 노드가 KSampler라 늘 거기서 보인다).
                    // 파일 핸들은 부모와 무관하므로 앱을 닫아도 서버가 멀쩡하고, 로그도 남아 진단이 된다.
                    var extraArgs = string.IsNullOrWhiteSpace(settings.ComfyExtraArgs) ? "" : " " + settings.ComfyExtraArgs.Trim();
                    var cmdline = $"\"{python}\" -s \"{main}\" --port {port} --disable-auto-launch{extraArgs}";
                    // 띄우기는 프로세스 사이에서도 한 번에 하나(ClaimAsync) — 다른 NekoManga 프로세스가 띄워 아직 깨어나는 중인 서버가 있으면 띄우지 않고 그것을 기다린다.
                    // 장부가 프로세스 안에만 있어 예전에는 둘째 프로세스가 하나 더 띄웠고, 그 cmd는 위와 같이 comfy.log를 못 열어 곧바로 exit 1로 죽었다
                    // (앱을 켜고 1~2분 안에 한 번 더 켜거나 --run을 돌리면 '시작하자마자 종료 (exit 1)')
                    var claim = await _ledger.ClaimAsync(port, new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{cmdline} > \"{LogPath}\" 2>&1\"",
                        WorkingDirectory = root,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }, ct);
                    // 살아 있는 서버가 다른 포트의 것이면 기다려도 이 포트로는 오지 않고, 하나 더 띄워도 그 서버가 쥔 comfy.log 때문에 곧바로 죽는다 — 까닭을 바로 알린다
                    if (claim.Port != port)
                        throw new InvalidOperationException(
                            $"NekoManga가 포트 {claim.Port}에 띄운 ComfyUI가 돌고 있어서(또는 깨어나는 중이라) 포트 {port}로는 하나 더 띄울 수 없어요 — 앱이 띄우는 ComfyUI는 한 번에 하나예요. " +
                            $"설정 → 엔진 → 고급 옵션의 포트를 {claim.Port}로 맞춰 주세요. 포트를 바꿔 쓰려면 포트가 {claim.Port}인 채로 엔진을 끈 다음에 바꾸면 돼요.");
                    proc = claim.Proc;
                    ours = claim.Ours;
                    if (ours)
                    {
                        _ledger.Started(port, proc);   // 바로 적는다 — 응답을 기다리다 취소·시간 초과로 끝나도 다음 사용자가 그 서버를 기다리고, 마지막 사용자가 놓을 때 끌 수 있게
                        deadline = DateTime.UtcNow + _ledger.StartLimit;   // 새로 띄웠으면 한도도 지금부터
                        // 남의 서버를 기다리다 그것이 죽어 이제 우리가 띄웠다 — 상태 문구를 처음 것으로 되돌린다(그대로 두면 깨어나는 1~2분 내내 없는 '다른 NekoManga'를 기다린다고 한다)
                        if (toldWaitingForOther) status?.Report(WakingText);
                        toldWaitingForOther = false;
                    }
                    else
                    {
                        status?.Report("다른 NekoManga가 깨우는 중인 채색 엔진을 기다리는 중...");   // 한도는 그대로(처음 물은 때부터) — 남의 서버라고 더 오래 기다리지 않는다
                        toldWaitingForOther = true;
                    }
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(ours
                        ? "ComfyUI 서버가 제한 시간 안에 응답하지 않았어요."
                        // 남의 서버를 한도까지 기다리다 끝났다 — 무엇을 기다렸는지와 볼 곳을 알린다(예전에는 이 자리에서 하나 더 띄우다 곧바로 exit 1 + 로그 진단으로 끝났다).
                        // 로그 진단(DiagnoseComfyLog)은 붙이지 못한다: 살아 있는 서버의 cmd가 comfy.log를 쓰기로 쥐고 있어 File.ReadAllText가 공유 위반으로 실패한다
                        // (2026-11-11 실측 — cmd 리다이렉트가 쥔 파일은 FileShare.ReadWrite로 열어야 읽힌다)
                        : "다른 NekoManga가 깨우던 채색 엔진(ComfyUI)이 제한 시간 안에 응답하지 않았어요 — 아직 깨어나는 중이거나 켜지다 멈췄을 수 있어요. " +
                          $"조금 뒤 다시 해 보고, 계속 그러면 설정 → 엔진의 [끄기]로 끈 다음 다시 해 보세요. 로그: {LogPath}");
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited)
                {
                    // 다른 프로세스가 띄우던 서버가 죽었다(기동 실패, 그쪽 작업이 끝나며 끔) — 이제는 띄울 수 있으니 처음부터 다시 본다(같은 프로세스 안의 LiveProc 규칙과 같다)
                    if (!ours) { proc = null; continue; }
                    // 흔한 원인(드라이버·VC++ 런타임·VRAM)은 로그에서 읽어 바로 할 일을 알려 준다 (2026-10-20, 다른 PC의 c10.dll 1114 사고)
                    var hint = Runtime.SystemCheck.DiagnoseComfyLog(LogPath, settings.GpuVendor);
                    throw new InvalidOperationException(
                        $"ComfyUI가 시작하자마자 종료됐어요 (exit {proc.ExitCode}). " + (hint != null ? hint + " " : "") + $"로그: {LogPath}");
                }
                if (await _ledger.IsAliveAsync(baseUrl)) return;
                await Task.Delay(_ledger.Poll, ct);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>'엔진 켜기'(EngineManager.StartAsync) — 이 인스턴스가 쥐는 서버를 KeepComfyWarm과 무관하게 EngineManager.Stop 전까지 둔다.
    /// EnsureRunningAsync 전에 불러 둔다: 응답을 기다리는 동안은 앱이 닫혀도 끄지 않고, 놓을 때(기다리다 실패했어도) 그 서버에 고정이 남는다.</summary>
    internal void Pin() => _ledger.Pin(this);

    /// <summary>앱이 끝난다 — KeepComfyWarm=false면 이 앱이 띄웠고 '엔진 켜기'로 고정하지 않은 서버를, 쓰던 작업이 아직 놓지 않았어도 끈다.
    /// 작업 중에(또는 닫은 결과 창의 작업이 끝나기 전에) 앱을 닫으면 그 작업의 Release가 돌기 전에 프로세스가 끝나는데, 장부는 메모리에만 있고
    /// 서버는 cmd로 떨어져 있어 그대로 남았다. 사용자가 따로 띄운 서버·고정한 서버는 그대로 두고, 끄기를 기다리지 않는다(종료를 늦추지 않게).</summary>
    public static void AppExiting(AppSettings settings) => Ledger.App.AppExiting(settings.KeepComfyWarm);

    /// <summary>이 인스턴스의 사용을 놓는다 — 여러 번 불러도 한 번만 놓고, 놓은 뒤에도 EnsureRunningAsync로 다시 쥘 수 있다.
    /// 이 앱이 띄운 서버를 마지막 사용자가 놓았고 KeepComfyWarm=false이며 '엔진 켜기'로 고정한 것도 아니면 그때 끈다. 사용자가 따로 띄운 서버는 끄지 않는다.</summary>
    public void Release() => _ledger.Release(this, settings.KeepComfyWarm);

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    private static async Task<bool> PingAsync(string baseUrl)
    {
        try
        {
            using var r = await Http.GetAsync($"{baseUrl}/system_stats");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // 로그 폴더를 만들고 cmd를 띄운다 — python 출력은 cmd가 그 파일로 보낸다(EnsureRunningAsync 주석)
    private static IServerProcess StartCmd(ProcessStartInfo psi)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        return new CmdProcess(Process.Start(psi)!);
    }

    /// <summary>이 앱이 띄운 서버 프로세스(cmd → python). 시험에서는 가짜를 넣는다.</summary>
    internal interface IServerProcess
    {
        bool HasExited { get; }
        int ExitCode { get; }
        void Kill();
    }

    internal sealed class CmdProcess(Process p) : IServerProcess
    {
        public bool HasExited => p.HasExited;
        public int ExitCode => p.ExitCode;
        public void Kill() => p.Kill(entireProcessTree: true);
        public int Id => p.Id;
        /// <summary>프로세스가 시작한 시각(UTC ticks) — pid와 함께 적어 두면 다른 프로세스가 '그 프로세스가 아직 살아 있는지'를 pid 재사용에 속지 않고 가린다.</summary>
        public long StartedUtc => p.StartTime.ToUniversalTime().Ticks;
    }

    /// <summary>다른 NekoManga 프로세스가 띄운 서버(cmd) — 살아 있는지만 본다. 이 프로세스가 띄운 것이 아니라 끄지 않는다.</summary>
    private sealed class WatchedProcess(Process p) : IServerProcess
    {
        public bool HasExited { get { try { return p.HasExited; } catch { return true; } } }
        public int ExitCode => 0;
        public void Kill() { }
    }

    /// <summary>ClaimAsync의 답 — 기다릴 서버 프로세스와 그 서버의 포트. Ours=false면 다른 프로세스가 먼저 띄운 것이다(띄우지 않았다).</summary>
    internal readonly record struct Claimed(int Port, IServerProcess Proc, bool Ours);

    /// <summary>프로세스 사이의 기동 조율 — 시험에서는 가짜를 넣는다.</summary>
    internal interface IStartBoard
    {
        /// <summary>프로세스 사이에서 한 번에 하나만: 누가 띄운 서버가 살아 있으면(깨어나는 중이어도) 띄우지 않고 그것을, 없으면 launch로 띄워 적고 그것을 돌려준다.</summary>
        Task<Claimed> ClaimAsync(int port, Func<IServerProcess> launch, CancellationToken ct);
    }

    /// <summary>프로세스 사이의 기동 기록 — 파일 하나(StartRecordPath)에 "포트 cmd의 pid 시작 시각"을 적는다.
    /// 죽은 프로세스가 잠금을 남기지 않게 두 가지로 한다:
    /// ① 관문은 이 파일을 혼자 여는 것(FileShare.None)이다 — 쥔 프로세스가 죽으면 OS가 핸들을 닫는다. 쥐는 것은 읽고·띄우고·적는 몇 ms뿐이라, 서버가 깨어나는 1~2분 동안은 아무도 쥐고 있지 않다.
    /// ② 기록은 적힌 cmd가 **지금 살아 있을 때만** 믿는다(pid + 시작 시각). 띄운 앱이 죽어도 cmd·python은 남아 계속 깨어나므로 기록은 맞고(그 서버를 기다린다),
    ///    서버가 죽으면(기동 실패·엔진 끄기·PC 재시작) 기록은 저절로 낡아 다음 프로세스가 새로 띄운다. 살아만 있고 끝내 응답하지 않으면 기다리는 쪽이 자기 한도에서 시간 초과로 끝난다.
    /// 이 기록을 모르는 옛 버전이 띄운 서버는 알아보지 못한다(예전처럼 하나 더 띄우다 실패한다).
    /// 알려진 한계 — 기록에 ComfyRoot가 없다: 포트는 같고 ComfyRoot가 다른 두 프로세스(설정 파일을 따로 쓰는 개발용 구성에서만 생긴다)는 한 서버에 합류하고,
    /// 그 서버와 폴더가 다른 쪽은 쪽마다 결과 그림을 자기 ComfyRoot의 출력 폴더(ComfyUI\output)에서 찾다가 실패한다.</summary>
    internal sealed class StartFile(string path) : IStartBoard
    {
        public async Task<Claimed> ClaimAsync(int port, Func<IServerProcess> launch, CancellationToken ct)
        {
            using var file = await OpenAloneAsync(ct);
            if (file != null && ReadLive(file) is { } other) return other;
            var proc = launch();
            if (file != null) Write(file, port, proc);
            return new Claimed(port, proc, true);
        }

        // 다른 프로세스가 쥐고 있으면(몇 ms) 놓을 때까지 다시 연다. 5초가 지나도 못 열면 다른 사정(권한·백신)이다 — null을 돌려주고 조율 없이 예전처럼 띄운다
        private async Task<FileStream?> OpenAloneAsync(CancellationToken ct)
        {
            var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (true)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (DateTime.UtcNow >= giveUp) return null;
                }
                await Task.Delay(30, ct);
            }
        }

        // 적힌 서버(cmd)가 살아 있으면 그것 — 읽지 못하거나, 그 pid가 없거나, 시작 시각이 다르면(pid를 물려받은 다른 프로세스) 낡은 기록이라 null
        private static Claimed? ReadLive(FileStream f)
        {
            try
            {
                var buf = new byte[Math.Min(f.Length, 64)];
                f.Position = 0;
                f.ReadExactly(buf);
                var parts = System.Text.Encoding.ASCII.GetString(buf).Split(' ');
                if (parts.Length != 3 || !int.TryParse(parts[0], out var port) || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var started)) return null;
                var p = Process.GetProcessById(pid);   // 없는 pid면 ArgumentException
                if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != started) { p.Dispose(); return null; }
                return new Claimed(port, new WatchedProcess(p), false);
            }
            catch { return null; }   // 권한이 달라(관리자로 띄운 앱) 들여다보지 못하는 프로세스도 여기 — 알아보지 못하면 예전처럼 띄운다
        }

        private static void Write(FileStream f, int port, IServerProcess proc)
        {
            try
            {
                if (proc is not CmdProcess cmd) return;
                f.SetLength(0);
                f.Position = 0;
                f.Write(System.Text.Encoding.ASCII.GetBytes($"{port} {cmd.Id} {cmd.StartedUtc}"));
            }
            catch { /* 적지 못하면 다른 프로세스가 이 서버를 알아보지 못할 뿐이다(예전 동작) */ }
        }
    }

    /// <summary>이 앱 프로세스 안의 ComfyUI 서버 장부 — 포트마다 사용자 수, 이 앱이 띄운 서버 프로세스, '엔진 켜기' 고정 여부.
    /// 러너·도우미마다 ComfyServer 인스턴스가 따로여도 장부는 하나(App)라서 마지막 사용자를 안다. 러너는 저마다 다른 스레드(Task.Run)에서 돌므로 잠금 안에서만 고친다.
    /// 서버가 살아 있는지 보는 법과 띄우는 법도 여기 둔다 — 시험은 프로세스·HTTP 대신 가짜를 넣은 장부로 규칙만 본다.</summary>
    internal sealed class Ledger(Func<string, Task<bool>> isAlive, Func<ProcessStartInfo, IServerProcess> launch, TimeSpan? poll = null, TimeSpan? startLimit = null,
        IStartBoard? board = null)
    {
        public static readonly Ledger App = new(PingAsync, StartCmd, board: new StartFile(StartRecordPath));

        private sealed class Slot
        {
            public int Users;
            public IServerProcess? Proc;   // 이 앱이 띄운 서버 — 사용자가 따로 띄웠거나 지난 실행이 남긴 서버면 null(끄지 않는다)
            public bool Pinned;            // '엔진 켜기'로 켰다 — 사용자가 다 놓아도 EngineManager.Stop 전까지 둔다
            public readonly SemaphoreSlim StartGate = new(1, 1);
        }

        private readonly object _lock = new();
        private readonly Dictionary<int, Slot> _slots = [];
        private readonly Dictionary<object, int> _holders = [];   // 사용을 쥔 인스턴스 → 그 포트 (한 인스턴스는 한 번만 센다)
        private readonly HashSet<object> _pinners = [];           // '엔진 켜기' 인스턴스 — 쥔 동안은 그 포트의 서버를 고정된 것으로 보고, 놓을 때 고정을 남긴다
        private bool _appExited;                                  // 앱이 끝나며 서버를 껐다(KeepComfyWarm=false) — 그 뒤에 적히는 서버도 곧바로 끈다

        /// <summary>서버를 띄운 뒤 응답을 다시 볼 때까지 쉬는 간격.</summary>
        public TimeSpan Poll { get; } = poll ?? TimeSpan.FromMilliseconds(1500);
        /// <summary>서버를 띄운(깨어나는 중인 서버를 기다리기 시작한) 뒤 응답을 기다리는 한도.</summary>
        public TimeSpan StartLimit { get; } = startLimit ?? TimeSpan.FromMinutes(4);

        public Task<bool> IsAliveAsync(string baseUrl) => isAlive(baseUrl);

        /// <summary>port에 서버를 띄운다 — 프로세스 사이에서도 한 번에 하나만(IStartBoard). 다른 프로세스가 띄운 서버가 살아 있으면 띄우지 않고 그것을 돌려준다(Ours=false, Port는 그 서버의 것).
        /// 기동 기록을 넣지 않은 장부(시험)는 그냥 띄운다.</summary>
        public Task<Claimed> ClaimAsync(int port, ProcessStartInfo psi, CancellationToken ct) =>
            board == null ? Task.FromResult(new Claimed(port, launch(psi), true)) : board.ClaimAsync(port, () => launch(psi), ct);

        /// <summary>user가 port의 서버를 쓰기 시작한다. 이미 쥐고 있으면 그대로 — 설정에서 포트를 바꿔 다른 포트를 쥐고 있었으면 그쪽은 놓는다.</summary>
        public void Hold(object user, int port, bool keepWarm)
        {
            lock (_lock)
            {
                if (_holders.TryGetValue(user, out var held))
                {
                    if (held == port) return;
                    Drop(user, held, keepWarm);
                }
                _holders[user] = port;
                SlotOf(port).Users++;
            }
        }

        /// <summary>user의 사용을 놓는다 — 쥐고 있지 않으면 아무 일도 없다(여러 번 불러도 안전).</summary>
        public void Release(object user, bool keepWarm)
        {
            lock (_lock)
            {
                if (_holders.TryGetValue(user, out var port)) Drop(user, port, keepWarm);
                _pinners.Remove(user);
            }
        }

        /// <summary>이 앱이 port에 서버를 띄웠다. 새 프로세스라 '엔진 켜기' 고정은 풀린다 — 고정은 켤 때 떠 있던 그 서버에 붙는다.</summary>
        public void Started(int port, IServerProcess proc)
        {
            lock (_lock)
            {
                var s = SlotOf(port);
                s.Proc = proc;
                s.Pinned = false;
                // 앱이 끝나며 서버를 끈 뒤에 띄웠다(종료와 엇갈린 기동) — 그대로 두면 앱이 끝난 뒤 끌 사람이 없다
                if (_appExited && EndsWithApp(port, s)) Kill(s);
            }
        }

        /// <summary>user를 '엔진 켜기'로 적는다 — 쥐고 있는 동안은 그 포트의 서버(깨어나는 중인 것 포함)를 고정된 것으로 보고(앱이 끝나도 끄지 않는다),
        /// 놓을 때 그 서버에 고정을 남긴다: 사용자가 다 놓아도, KeepComfyWarm=false여도 끄지 않는다. 쥐기 전에 불러 둔다(이미 쥐고 있으면 지금 서버를 바로 고정한다).</summary>
        public void Pin(object user)
        {
            lock (_lock)
            {
                _pinners.Add(user);
                if (_holders.TryGetValue(user, out var port)) SlotOf(port).Pinned = true;
            }
        }

        /// <summary>앱이 끝난다 — keepWarm=false면 이 앱이 띄웠고 고정되지 않은 서버를, 쓰던 사용자가 아직 놓지 않았어도 끈다(앱이 끝나면 놓을 수 없다).
        /// 사용자가 따로 띄운 서버, 고정한 서버와 '엔진 켜기'가 깨우는 중인 서버는 그대로. keepWarm=true면 아무것도 끄지 않는다.</summary>
        public void AppExiting(bool keepWarm)
        {
            lock (_lock)
            {
                if (keepWarm) return;
                _appExited = true;
                foreach (var (port, s) in _slots)
                    if (EndsWithApp(port, s)) Kill(s);
            }
        }

        public SemaphoreSlim StartGate(int port) { lock (_lock) return SlotOf(port).StartGate; }
        public bool IsOurs(int port) { lock (_lock) return _slots.TryGetValue(port, out var s) && s.Proc is { HasExited: false }; }
        /// <summary>이 앱이 port에 띄운 서버 프로세스가 살아 있으면 그것 — 아직 응답하기 전(깨어나는 중)이어도. 없거나 죽었으면 null.</summary>
        public IServerProcess? LiveProc(int port) { lock (_lock) return _slots.TryGetValue(port, out var s) && s.Proc is { HasExited: false } p ? p : null; }
        public int UsersOf(int port) { lock (_lock) return _slots.TryGetValue(port, out var s) ? s.Users : 0; }

        /// <summary>지금 서버를 쥐고 있는 작업 수(본 작업·결과 창·표지 색 뽑기) — '엔진 켜기'가 깨우느라 쥔 것은 작업이 아니라 세지 않는다.
        /// 포트를 가리지 않는다: 엔진 끄기(EngineManager.Stop)는 ComfyRoot의 python을 포트와 상관없이 모두 죽인다.</summary>
        public int JobsUsing { get { lock (_lock) return _holders.Keys.Count(u => !_pinners.Contains(u)); } }

        /// <summary>'엔진 끄기'(EngineManager.Stop)가 ComfyRoot의 python을 죽였다 — 이 앱이 띄운 서버를 모두 내린 것으로 적는다(cmd가 남아 있으면 끈다).
        /// '엔진 켜기' 고정은 따로 풀지 않는다 — 고정은 이 앱이 띄운 서버가 적혀 있을 때만 보고(Drop·EndsWithApp), 다음에 띄우는 서버는 Started가 고정 없이 적는다.
        /// python이 죽으면 cmd도 곧 끝나지만, 그 사이에는(설정에서 ComfyRoot를 바꿔 옛 폴더의 서버가 살아남았으면 계속) 장부가 그 프로세스를 '깨어나는 중인 우리 서버'로 보아
        /// 다음 기동이 새로 띄우지 않고 그것을 기다리다 실패한다. 쥐고 있는 사용자는 그대로 둔다 — 그 작업이 끝나며 놓고(끌 서버가 없으니 놓기만 한다), 그 전에 다시 쓰면 새 서버를 띄운다.</summary>
        public void EngineStopped()
        {
            lock (_lock)
                foreach (var s in _slots.Values) Kill(s);
        }

        private Slot SlotOf(int port) => _slots.TryGetValue(port, out var s) ? s : (_slots[port] = new Slot());

        // _lock 안에서만 부른다
        private void Drop(object user, int port, bool keepWarm)
        {
            _holders.Remove(user);
            var s = SlotOf(port);
            s.Users--;
            // '엔진 켜기'가 놓는다 — 그 서버를 켜 둔다. 응답을 기다리다 실패했어도(시간 초과·취소) 깨어나는 중인 서버는 남긴다(이미 죽었으면 다음에 띄울 때 풀린다)
            if (_pinners.Contains(user)) s.Pinned = true;
            if (s.Users > 0 || keepWarm || s.Pinned || s.Proc == null) return;
            // 마지막 사용자가 놓았다 — 이 앱이 띄운 서버라 끈다. 잠금 안에서 끈다: 끄기로 정한 뒤 실제로 끄기 전에 새 사용자가 쥐고 그 서버를 쓰기 시작하는 틈이 없게
            Kill(s);
        }

        // _lock 안에서만 부른다 — 앱이 끝날 때 끌 서버인가: 이 앱이 띄웠고, 고정되지 않았고, '엔진 켜기'가 쥐고 깨우는 중도 아니다
        private bool EndsWithApp(int port, Slot s) =>
            s.Proc != null && !s.Pinned && !_pinners.Any(u => _holders.TryGetValue(u, out var held) && held == port);

        // _lock 안에서만 부른다 — 끄기를 기다리지 않는다
        private static void Kill(Slot s)
        {
            try { if (s.Proc is { HasExited: false } p) p.Kill(); }
            catch { }
            s.Proc = null;
        }
    }
}
