using System.Diagnostics;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI portable을 헤드리스(브라우저 없이 API 서버만)로 띄우고 내린다.
/// 이미 다른 ComfyUI가 같은 포트에 떠 있으면 그대로 재사용한다.</summary>
public class ComfyServer(AppSettings settings) : IAsyncDisposable
{
    private Process? _proc;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string BaseUrl => $"http://127.0.0.1:{settings.ComfyPort}";
    public bool WeStartedIt => _proc != null;

    /// <summary>ComfyUI의 stdout/stderr가 쌓이는 로그 (노드 오류의 파이썬 트레이스백이 여기 남는다).</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoManga", "comfy.log");

    public async Task<bool> IsAliveAsync()
    {
        try
        {
            using var r = await Http.GetAsync($"{BaseUrl}/system_stats");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>서버가 떠 있지 않으면 시작하고 응답할 때까지 기다린다.</summary>
    public async Task EnsureRunningAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (await IsAliveAsync()) return;

        var root = settings.ComfyRoot;
        var python = Path.Combine(root, "python_embeded", "python.exe");
        var main = Path.Combine(root, "ComfyUI", "main.py");
        if (!File.Exists(python) || !File.Exists(main))
            throw new FileNotFoundException("ComfyUI가 설치되어 있지 않아요. 설정 → 구성요소에서 설치해 주세요.", main);

        status?.Report("채색 엔진 깨우는 중... (첫 시작은 1~2분)");

        // 출력은 cmd로 **로그 파일**에 리다이렉트한다 — RedirectStandardOutput(익명 파이프)을 쓰면 안 된다.
        // 파이프의 읽는 쪽은 우리 프로세스가 쥐고 있어서, KeepComfyWarm으로 서버를 남긴 채 앱이 종료되면
        // 고아가 된 ComfyUI의 stdout/stderr가 끊긴 파이프를 가리킨다. 다음 실행이 그 서버를 재사용하면
        // 진행바(tqdm)를 쓰는 순간 파이썬이 OSError를 던져 **KSampler 노드에서 "[Errno 22] Invalid argument"**로
        // 터진다 (실제 발생 — 단계마다 출력하는 노드가 KSampler라 늘 거기서 보인다).
        // 파일 핸들은 부모와 무관하므로 앱을 닫아도 서버가 멀쩡하고, 로그도 남아 진단이 된다.
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var extraArgs = string.IsNullOrWhiteSpace(settings.ComfyExtraArgs) ? "" : " " + settings.ComfyExtraArgs.Trim();
        var cmdline = $"\"{python}\" -s \"{main}\" --port {settings.ComfyPort} --disable-auto-launch{extraArgs}";
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdline} > \"{LogPath}\" 2>&1\"",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited)
            {
                // 흔한 원인(드라이버·VC++ 런타임·VRAM)은 로그에서 읽어 바로 할 일을 알려 준다 (2026-10-20, 다른 PC의 c10.dll 1114 사고)
                var hint = Runtime.SystemCheck.DiagnoseComfyLog(LogPath);
                throw new InvalidOperationException(
                    $"ComfyUI가 시작하자마자 종료됐어요 (exit {_proc.ExitCode}). " + (hint != null ? hint + " " : "") + $"로그: {LogPath}");
            }
            if (await IsAliveAsync()) return;
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("ComfyUI 서버가 제한 시간 안에 응답하지 않았어요.");
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!settings.KeepComfyWarm) Stop();
        return ValueTask.CompletedTask;
    }
}
