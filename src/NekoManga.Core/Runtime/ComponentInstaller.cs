using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace NekoManga.Core.Runtime;

public record InstallProgress(string ComponentId, string Message, double Percent, long BytesDone, long BytesTotal);

/// <summary>구성요소(ComfyUI·커스텀노드·모델·BallonsTranslator)를 내려받아 RuntimeRoot에 설치한다.
/// 대용량 파일은 .part 파일로 받다가 완료 시 이름을 바꿔, 중단해도 이어받을 수 있다.</summary>
public class ComponentInstaller(AppSettings settings)
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NekoManga/1.0");
        return c;
    }

    public string Resolve(string relative) => Path.GetFullPath(Path.Combine(settings.RuntimeRoot, relative));

    public bool IsInstalled(Component c)
    {
        // 기존 설치가 연결돼 있으면 연결 경로에서 존재를 확인한다.
        if (!string.IsNullOrWhiteSpace(settings.LinkedComfyRoot) &&
            c.Probe.StartsWith(ComponentCatalog.ComfyDirName, StringComparison.Ordinal))
            return File.Exists(Path.Combine(settings.LinkedComfyRoot,
                c.Probe[(ComponentCatalog.ComfyDirName.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(settings.LinkedBallonsRoot) && c.Id == "ballons")
            return File.Exists(Path.Combine(settings.LinkedBallonsRoot, "ballontranslator", "launch.py"));
        return File.Exists(Resolve(c.Probe));
    }

    public IReadOnlyList<Component> Missing() =>
        ComponentCatalog.All.Where(c => !IsInstalled(c)).ToList();

    public async Task InstallAsync(Component c, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(settings.RuntimeRoot);
        switch (c.Kind)
        {
            case ComponentKind.File:
                await DownloadAsync(c, Resolve(c.Target), progress, ct);
                break;

            case ComponentKind.ArchiveZip:
            {
                var tmp = Resolve($"tmp/{c.Id}.zip");
                await DownloadAsync(c, tmp, progress, ct);
                progress.Report(new(c.Id, "압축 푸는 중...", 99, 0, 0));
                ExtractZip(tmp, Resolve(c.Target), c.StripRootFolder);
                File.Delete(tmp);
                break;
            }

            case ComponentKind.Archive7z:
            {
                var tmp = Resolve($"tmp/{c.Id}.7z");
                await DownloadAsync(c, tmp, progress, ct);
                progress.Report(new(c.Id, "압축 푸는 중... (수 분 걸려요)", 99, 0, 0));
                await Extract7zAsync(tmp, Resolve(c.Target), ct);
                File.Delete(tmp);
                break;
            }
        }
        progress.Report(new(c.Id, "완료", 100, c.ApproxBytes, c.ApproxBytes));
    }

    private async Task DownloadAsync(Component c, string dest, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, c.Url);
        if (c.UseCivitaiKey && !string.IsNullOrWhiteSpace(settings.CivitaiApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.CivitaiApiKey);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            existing = 0; // 서버가 이어받기를 지원하지 않음 — 처음부터
        }
        if (!resp.IsSuccessStatusCode)
        {
            var hint = c.UseCivitaiKey && (int)resp.StatusCode is 401 or 403
                ? " — civitai 로그인 필요 모델일 수 있어요. 설정에서 civitai API 키를 넣어주세요."
                : "";
            throw new HttpRequestException($"{c.DisplayName} 다운로드 실패 (HTTP {(int)resp.StatusCode}){hint}");
        }

        long total = (resp.Content.Headers.ContentLength ?? 0) + existing;
        if (total <= 0) total = c.ApproxBytes;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create,
                                              FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = existing;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new(c.Id, $"{done / 1048576.0:n0} / {total / 1048576.0:n0} MB",
                                        total > 0 ? Math.Min(98, done * 100.0 / total) : 0, done, total));
                }
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    private static void ExtractZip(string zipPath, string destDir, bool stripRoot)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (stripRoot)
            {
                var slash = name.IndexOf('/');
                if (slash < 0) continue; // 루트 폴더 자체
                name = name[(slash + 1)..];
            }
            if (name.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(destDir, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                continue; // zip-slip 방지
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private async Task Extract7zAsync(string archive, string destDir, CancellationToken ct)
    {
        var sevenZip = Resolve(ComponentCatalog.SevenZip.Target);
        if (!File.Exists(sevenZip))
            throw new FileNotFoundException("7zr.exe가 없습니다. 구성요소 설치 순서를 확인하세요.", sevenZip);
        Directory.CreateDirectory(destDir);
        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = $"x -y -o\"{destDir}\" \"{archive}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"7z 압축 해제 실패 (exit {p.ExitCode}): {await p.StandardError.ReadToEndAsync(ct)}");
    }
}
