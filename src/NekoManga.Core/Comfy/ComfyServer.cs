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
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-s \"{main}\" --port {settings.ComfyPort} --disable-auto-launch",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        // 출력 버퍼가 차서 프로세스가 멈추지 않게 흘려보낸다.
        _proc.OutputDataReceived += (_, _) => { };
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited)
                throw new InvalidOperationException($"ComfyUI가 시작하자마자 종료됐어요 (exit {_proc.ExitCode}). 로그를 확인해 주세요.");
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
