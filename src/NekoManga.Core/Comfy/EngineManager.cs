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

    /// <summary>엔진을 켠다 (이미 켜져 있으면 그대로). KeepComfyWarm 설정과 무관하게 살아남는다 —
    /// ComfyServer가 cmd 리다이렉트로 띄워 부모와 분리돼 있기 때문.</summary>
    public static async Task StartAsync(AppSettings settings, IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var server = new ComfyServer(settings);
        await server.EnsureRunningAsync(status, ct);
        // 여기서 Dispose하지 않는다 — KeepComfyWarm=false여도 '엔진 켜기'는 명시적 행동이므로 유지.
    }

    /// <summary>엔진을 끈다 — 이 앱의 ComfyRoot에서 뜬 python(main.py)을 찾아 종료한다.
    /// 사용자가 직접 띄운 다른 ComfyUI는 경로가 다르면 건드리지 않는다.</summary>
    public static int Stop(AppSettings settings)
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
