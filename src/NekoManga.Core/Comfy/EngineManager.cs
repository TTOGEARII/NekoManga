using System.Diagnostics;

namespace NekoManga.Core.Comfy;

/// <summary>채색 엔진(ComfyUI)의 수명을 앱이 책임진다 — 사용자는 켜짐/꺼짐만 보면 된다.
/// 엔진은 headless 서버일 뿐이고 UI는 전부 이 앱이 제공한다 (BT를 내장 엔진으로 바꾼 것과 같은 방향).</summary>
public static class EngineManager
{
    /// <summary>엔진이 응답하는가 (포트 기준 — 앱이 띄웠든 사용자가 띄웠든 무관).</summary>
    public static async Task<bool> IsAliveAsync(AppSettings settings)
    {
        await using var server = new ComfyServer(settings);
        return await server.IsAliveAsync();
    }

    /// <summary>엔진을 켠다 (이미 켜져 있으면 그대로). KeepComfyWarm 설정과 무관하게 Stop 전까지 살아남는다 —
    /// 작업들이 사용을 다 놓아도 끄지 않게 지금 서버를 고정하고(ComfyServer.Pin), cmd 리다이렉트로 띄워 부모와 분리돼 있어 앱을 닫아도 남는다.
    /// 응답을 기다리다 실패해도(4분 시간 초과·취소) 깨어나는 중인 서버는 고정해 남긴다.</summary>
    public static Task StartAsync(AppSettings settings, IProgress<string>? status = null,
        CancellationToken ct = default) => StartAsync(new ComfyServer(settings), status, ct);

    /// <summary>StartAsync 본체 — 시험이 가짜 장부의 서버를 넘긴다.</summary>
    internal static async Task StartAsync(ComfyServer server, IProgress<string>? status, CancellationToken ct)
    {
        await using (server)
        {
            // KeepComfyWarm=false여도 '엔진 켜기'는 명시적 행동이므로 유지 — 쥐기 전에 고정을 걸어 두면 이 사용을 놓을 때 그 서버에 고정이 남는다
            // (고정된 서버는 마지막 사용자가 놓아도 끄지 않는다). 작업이 띄워 이미 떠 있던 서버에 켜기를 눌러도(미리 켜기와 작업 시작이 겹칠 때 등) 같다 — 켜 두라는 뜻이다.
            // 응답을 기다리다 실패해도(느린 PC의 4분 시간 초과·취소) 깨어나는 중인 서버는 끄지 않는다 — 예전에도 남겨 두어 결국 뜬 서버를 다음 작업이 썼다.
            // 기다리는 사이 앱이 닫혀도 끄지 않는다(ComfyServer.AppExiting)
            server.Pin();
            await server.EnsureRunningAsync(status, ct);
        }
    }

    /// <summary>이 앱(프로세스)의 작업이 지금 엔진을 쓰고 있는가 — 본 작업, 채색·검열 제거를 다시 돌린 결과 창(닫을 때까지 쥔다), 표지 색 뽑기.
    /// 쓰는 중에 Stop으로 끄면 그 작업은 도중에 연결 오류로 실패한다 — 끄기 전에 이것을 보고 묻거나(설정 창의 끄기) 그만둔다(자가 학습).
    /// 다른 NekoManga 프로세스의 작업과 브라우저에서 ComfyUI를 직접 쓰는 것은 알지 못한다(장부는 프로세스 안에만 있다).</summary>
    public static bool InUse => ComfyServer.Ledger.App.JobsUsing > 0;

    /// <summary>엔진을 끈다 — 이 앱의 ComfyRoot에서 뜬 python(main.py)을 찾아 종료한다.
    /// 사용자가 직접 띄운 다른 ComfyUI는 경로가 다르면 건드리지 않는다. ComfyRoot가 연결해 둔 사용자의 ComfyUI면 그 ComfyUI가 꺼진다(누가 띄웠든).
    /// 쓰는 작업이 있어도 끈다(명시적 행동) — 물을지 그만둘지는 부르는 쪽이 InUse를 보고 정한다.</summary>
    public static int Stop(AppSettings settings) => Stop(settings, ComfyServer.Ledger.App, KillPythons);

    /// <summary>Stop 본체 — 시험이 가짜 장부와 가짜 '죽이기'를 넘긴다(실제 python을 건드리지 않게).</summary>
    internal static int Stop(AppSettings settings, ComfyServer.Ledger ledger, Func<AppSettings, int> killPythons)
    {
        int killed = killPythons(settings);
        // 장부도 맞춘다 — 죽어 가는 cmd를 '깨어나는 중인 우리 서버'로 남기지 않는다(Ledger.EngineStopped). 다음에 띄우는 서버는 '엔진 켜기' 고정 없이 시작한다
        ledger.EngineStopped();
        return killed;
    }

    private static int KillPythons(AppSettings settings)
    {
        int killed = 0;
        var rootPython = Path.GetFullPath(Path.Combine(settings.ComfyRoot, "python_embeded"));
        foreach (var p in Process.GetProcessesByName("python"))
        {
            try
            {
                var exe = p.MainModule?.FileName;
                if (exe != null && exe.StartsWith(rootPython, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { /* 다른 사용자 프로세스 등 접근 불가는 무시 */ }
            finally { p.Dispose(); }
        }
        return killed;
    }

    /// <summary>설치된 모델 파일명 나열 (엔진 탭 드롭다운용). 폴더가 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> ListModels(AppSettings settings, string subdir)
    {
        var dir = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", subdir);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>로컬 모델 파일을 엔진의 모델 폴더로 복사해 들여온다. 돌려주는 값은 파일명.</summary>
    public static string ImportModel(AppSettings settings, string subdir, string sourcePath)
    {
        var dir = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", subdir);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(sourcePath);
        var dest = Path.Combine(dir, name);
        if (!File.Exists(dest)) File.Copy(sourcePath, dest);
        return name;
    }

    /// <summary>URL에서 모델을 내려받아 모델 폴더에 넣는다 (.part로 받다가 완료 시 교체).
    /// civitai URL이면 설정의 API 키를 Bearer로 보낸다. 돌려주는 값은 파일명.</summary>
    public static async Task<string> DownloadModelAsync(AppSettings settings, string subdir, string url,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", subdir);
        Directory.CreateDirectory(dir);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Contains("civitai.com", StringComparison.OrdinalIgnoreCase)
            && settings.CivitaiApiKey.Trim().Length > 0)
            req.Headers.Authorization = new("Bearer", settings.CivitaiApiKey.Trim());

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // 파일명: Content-Disposition 우선, 없으면 URL 마지막 조각
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar
                   ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) name = "model.safetensors";

        var dest = Path.Combine(dir, name);
        var part = dest + ".part";
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) status?.Report($"받는 중... {done * 100 / total}% ({done / 1048576}MB)");
                else status?.Report($"받는 중... {done / 1048576}MB");
            }
        }
        File.Move(part, dest, overwrite: true);
        return name;
    }
}
