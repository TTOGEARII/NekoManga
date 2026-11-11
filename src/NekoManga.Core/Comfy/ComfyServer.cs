using System.Diagnostics;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI portable을 헤드리스(브라우저 없이 API 서버만)로 띄우고 내린다.
/// 이미 다른 ComfyUI가 같은 포트에 떠 있으면 그대로 재사용한다.
///
/// 서버는 포트마다 하나를 이 앱 안의 모든 사용자(본 작업 러너·결과 창 러너·표지 색 뽑기)가 같이 쓴다 — EnsureRunningAsync가 이 인스턴스의
/// 사용을 잡고(인스턴스마다 한 번) Release/DisposeAsync가 놓는다. KeepComfyWarm=false면 이 앱이 띄운 서버를 **마지막 사용자가 놓을 때만** 끈다(Ledger) —
/// 놓지 못한 채 앱이 끝나면 그때 끈다(AppExiting). '엔진 켜기'로 고정한 서버는 어느 때도 끄지 않는다(엔진 끄기 전까지).
/// 예전에는 인스턴스마다 자기가 띄운 프로세스만 쥐고 따로 껐다 — 본 작업이 띄운 서버는 본 작업이 끝나는 순간 꺼져 결과 창에서 돌던
/// [채색부터 다시]가 실패했고, 결과 창이 띄운 서버는 아무도 끄지 않아 남았다.</summary>
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

        status?.Report("채색 엔진 깨우는 중... (첫 시작은 1~2분)");

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
                proc = _ledger.Launch(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{cmdline} > \"{LogPath}\" 2>&1\"",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                _ledger.Started(port, proc);   // 바로 적는다 — 응답을 기다리다 취소·시간 초과로 끝나도 다음 사용자가 그 서버를 기다리고, 마지막 사용자가 놓을 때 끌 수 있게
                asked = DateTime.UtcNow;       // 새로 띄웠으면 한도도 지금부터
            }

            var deadline = asked + _ledger.StartLimit;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited)
                {
                    // 흔한 원인(드라이버·VC++ 런타임·VRAM)은 로그에서 읽어 바로 할 일을 알려 준다 (2026-10-20, 다른 PC의 c10.dll 1114 사고)
                    var hint = Runtime.SystemCheck.DiagnoseComfyLog(LogPath, settings.GpuVendor);
                    throw new InvalidOperationException(
                        $"ComfyUI가 시작하자마자 종료됐어요 (exit {proc.ExitCode}). " + (hint != null ? hint + " " : "") + $"로그: {LogPath}");
                }
                if (await _ledger.IsAliveAsync(baseUrl)) return;
                await Task.Delay(_ledger.Poll, ct);
            }
            throw new TimeoutException("ComfyUI 서버가 제한 시간 안에 응답하지 않았어요.");
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

    private sealed class CmdProcess(Process p) : IServerProcess
    {
        public bool HasExited => p.HasExited;
        public int ExitCode => p.ExitCode;
        public void Kill() => p.Kill(entireProcessTree: true);
    }

    /// <summary>이 앱 프로세스 안의 ComfyUI 서버 장부 — 포트마다 사용자 수, 이 앱이 띄운 서버 프로세스, '엔진 켜기' 고정 여부.
    /// 러너·도우미마다 ComfyServer 인스턴스가 따로여도 장부는 하나(App)라서 마지막 사용자를 안다. 러너는 저마다 다른 스레드(Task.Run)에서 돌므로 잠금 안에서만 고친다.
    /// 서버가 살아 있는지 보는 법과 띄우는 법도 여기 둔다 — 시험은 프로세스·HTTP 대신 가짜를 넣은 장부로 규칙만 본다.</summary>
    internal sealed class Ledger(Func<string, Task<bool>> isAlive, Func<ProcessStartInfo, IServerProcess> launch, TimeSpan? poll = null, TimeSpan? startLimit = null)
    {
        public static readonly Ledger App = new(PingAsync, StartCmd);

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
        public IServerProcess Launch(ProcessStartInfo psi) => launch(psi);

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
