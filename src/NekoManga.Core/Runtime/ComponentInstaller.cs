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
        // 기존 ComfyUI 설치가 연결돼 있으면 연결 경로에서 존재를 확인한다.
        if (!string.IsNullOrWhiteSpace(settings.LinkedComfyRoot) &&
            c.Probe.StartsWith(ComponentCatalog.ComfyDirName, StringComparison.Ordinal))
            return File.Exists(Path.Combine(settings.LinkedComfyRoot,
                c.Probe[(ComponentCatalog.ComfyDirName.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(Resolve(c.Probe));
    }

    public IReadOnlyList<Component> Missing() =>
        ComponentCatalog.All.Where(c => !IsInstalled(c)).ToList();

    /// <summary>내장 기능(번역·기본 검열제거)에 필요한 것 중 빠진 것.</summary>
    public IReadOnlyList<Component> MissingEngine() =>
        ComponentCatalog.EngineComponents.Where(c => !IsInstalled(c)).ToList();

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
                // ComfyUI 포터블은 그래픽카드 종류(설정 GpuVendor, auto면 감지)에 맞는 묶음을 받는다 (2026-10-21 AMD·구형 NVIDIA 지원)
                bool isComfy = c.Id == ComponentCatalog.ComfyPortable.Id;
                string vendor = "nvidia";
                if (isComfy)
                {
                    vendor = settings.GpuVendor == "auto" ? SystemCheck.DetectVendor().Vendor : settings.GpuVendor;
                    c = c with { Url = ComponentCatalog.ComfyPortableUrl(vendor) };
                }
                var amdDir = Resolve(ComponentCatalog.ComfyAmdDirName); var stdDir = Resolve(ComponentCatalog.ComfyDirName);
                // 지난번에 풀어 두고 이름만 못 바꾼 AMD 폴더가 있으면(백신이 파일을 잡고 있던 경우 등) 1.8GB를 다시 받지 않고 이름 변경부터 한다
                bool amdExtracted = isComfy && File.Exists(Path.Combine(amdDir, "ComfyUI", "main.py"));
                if (!amdExtracted)
                {
                    // 임시 파일 이름에 벤더를 넣는다 — 받다 만 NVIDIA .part에 AMD 묶음을 이어 붙이면 깨진 7z가 된다
                    var tmp = Resolve(isComfy ? $"tmp/{c.Id}-{vendor}.7z" : $"tmp/{c.Id}.7z");
                    await DownloadAsync(c, tmp, progress, ct);
                    progress.Report(new(c.Id, "압축 푸는 중... (수 분 걸려요)", 99, 0, 0));
                    await Extract7zAsync(tmp, Resolve(c.Target), ct);
                    File.Delete(tmp);
                }
                // AMD 묶음은 `ComfyUI_windows_portable_amd`로 풀린다 → 나머지 구성요소·실행 경로가 기대하는 이름으로 맞춘다.
                // 판정은 폴더 유무가 아니라 Probe(main.py)로 — 받다 만 표준 폴더가 남아 있으면 그게 설치를 영원히 '필요'로 묶는다.
                if (isComfy && Directory.Exists(amdDir) && !File.Exists(Resolve(c.Probe)))
                {
                    if (Directory.Exists(stdDir)) Directory.Delete(stdDir, true);
                    for (int attempt = 0; ; attempt++)
                    {
                        try { Directory.Move(amdDir, stdDir); break; }
                        catch (IOException) when (attempt < 5) { await Task.Delay(1000, ct); }   // 백신이 갓 풀린 파일을 잡고 있는 동안
                    }
                }
                break;
            }
        }
        RemoveObsolete(c);
        progress.Report(new(c.Id, "완료", 100, c.ApproxBytes, c.ApproxBytes));
    }

    /// <summary>이 구성요소가 대체한 옛 파일을 지운다 (예: 만화판 LaMa 설치 뒤 Carve lama_fp32.onnx 208MB — 업그레이드 설치에 죽은 파일로 남는다).
    /// 설치가 끝난 뒤에만 부르므로 실패해도 새 파일은 이미 있다. A/B용 `NEKO_LAMA_MODEL`이 가리키는 파일은 남긴다. 삭제 실패는 무시(잠김 등).</summary>
    private void RemoveObsolete(Component c)
    {
        var keep = Environment.GetEnvironmentVariable("NEKO_LAMA_MODEL");
        foreach (var rel in c.Obsolete ?? [])
        {
            var path = Resolve(rel);
            if (!string.IsNullOrEmpty(keep) && string.Equals(Path.GetFileName(path), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 다음 설치 때 다시 시도된다 */ }
        }
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
