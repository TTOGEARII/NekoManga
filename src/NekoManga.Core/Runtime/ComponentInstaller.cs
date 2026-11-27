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

    /// <summary>구성요소의 상대 경로가 가리키는 실제 자리. ComfyUI 폴더(<see cref="ComponentCatalog.ComfyDirName"/>) 아래의 것은 연결해 둔 ComfyUI가 있으면
    /// 그 폴더로 간다 — 앱이 돌리는 ComfyUI(AppSettings.ComfyRoot)와 같은 곳이다. 나머지(엔진 모델·7zr·tmp)는 늘 RuntimeRoot.
    /// 2026-11-18까지는 설치 여부만 연결된 폴더에서 보고 까는 곳은 RuntimeRoot여서, 연결한 ComfyUI에 빠진 모델을 받으면 쓰이지 않는 사본이 생기고
    /// 준비 창에는 다시 '필요'로 나왔다(체크포인트면 6.5GB를 받을 때마다 헛받는다).</summary>
    public string Resolve(string relative)
    {
        const string comfy = ComponentCatalog.ComfyDirName;
        if (!OwnsPython && (relative == comfy || relative.StartsWith(comfy + "/", StringComparison.Ordinal)))
            return Path.GetFullPath(Path.Combine(settings.LinkedComfyRoot.Trim(), relative[comfy.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        return Path.GetFullPath(Path.Combine(settings.RuntimeRoot, relative));
    }

    public bool IsInstalled(Component c)
    {
        // 질내사정 판단 모델은 ComfyUI의 WD14 노드가 같은 파일을 받아 뒀으면 그것을 쓴다(Engine.CumDetector) — 326MB를 다시 받으라고 하지 않는다
        if (c.Id == ComponentCatalog.ModelWdTagger.Id && Engine.CumDetector.ComfyNodeCopy(settings.ComfyRoot) != null) return true;
        // 다 받은 포터블 압축 파일이 tmp에 남아 있으면 풀기가 끝나지 않은 것이다(풀고 나서야 지운다) — 풀다가 꺼지면 main.py는 있는데
        // python_embeded가 덜 풀려 있어, 그것을 '설치됨'으로 보면 고칠 길이 없다. 다시 설치하면 남은 압축 파일로 마저 푼다.
        if (c.Id == ComponentCatalog.ComfyPortable.Id && OwnsPython && Directory.Exists(Resolve("tmp"))
            && Directory.EnumerateFiles(Resolve("tmp"), $"{c.Id}-*.7z").Any()) return false;
        // 파이썬 패키지가 필요한 노드는 그 설치까지 끝나야 설치된 것이다 — 파일만 있으면 ComfyUI가 노드를 못 읽는다.
        // 연결한 ComfyUI에 이미 있던 것은 파일만 본다(사용자가 넣어 쓰던 것 — 아래 OwnsPython)
        return File.Exists(Resolve(c.Probe)) && (!c.HasDeps || !OwnsPython || File.Exists(DepsMarker(c)));
    }

    /// <summary>파이썬 패키지 설치가 끝났다는 표식 파일 — 폴더로 푸는 구성요소는 그 폴더 안, 파일 하나짜리는 그 파일 옆.</summary>
    internal string DepsMarker(Component c) =>
        c.Kind == ComponentKind.File ? Resolve(c.Target) + ".neko-deps" : Path.Combine(Resolve(c.Target), ".neko-deps");

    /// <summary>ComfyUI가 앱이 깐 것인가(연결해 둔 ComfyUI가 없다). 연결한 ComfyUI는 사용자 것이다 — 거기 이미 있는 노드는 패키지가 깔렸는지 따지지 않고
    /// 고치지도 않는다. 빠진 것을 앱이 새로 넣을 때만 그 구성요소의 패키지를 같이 넣는다(이미 깔린 패키지는 버전을 묶어 두므로 바뀌지 않는다).</summary>
    private bool OwnsPython => string.IsNullOrWhiteSpace(settings.LinkedComfyRoot);

    /// <summary>파일은 받았는데 파이썬 패키지가 안 깔린 노드 — v1.13.0까지의 설치가 이 상태다. 앱이 시작할 때 이것이 있으면 준비 창을 띄운다.
    /// 연결된 ComfyUI는 사용자 것이라 보지 않는다(거기 있던 노드는 고치지 않는다).</summary>
    public IReadOnlyList<Component> NeedsRepair() =>
        !OwnsPython ? []
        : ComponentCatalog.All.Where(c => c.HasDeps && File.Exists(Resolve(c.Probe)) && !File.Exists(DepsMarker(c))).ToList();

    /// <summary>받을 대상인가 — 채색에만 쓰는 구성요소는 설정에서 채색을 뺐으면 받지 않고, 글자 읽기 모델은 고른 엔진의 것만 받는다(다른 엔진의 수백 MB를 받지 않게). 나머지는 늘 받는다.</summary>
    public bool IsWanted(Component c) => (!c.ColorOnly || settings.InstallColor) && (c.Ocr == null || c.Ocr == settings.OcrEngine);

    public IReadOnlyList<Component> Missing() =>
        ComponentCatalog.All.Where(c => !IsInstalled(c)).ToList();

    /// <summary>내장 기능(번역·기본 검열제거)에 필요한 것 중 빠진 것 — 고르지 않은 글자 읽기 엔진의 모델은 필요한 것이 아니다.</summary>
    public IReadOnlyList<Component> MissingEngine() =>
        ComponentCatalog.EngineComponents.Where(c => IsWanted(c) && !IsInstalled(c)).ToList();

    /// <summary>자동 감지(GpuVendor=auto)일 때 그래픽카드 종류를 묻는 함수 — 시험에서 nvidia-smi 대신 넣는다.</summary>
    internal Func<string> DetectVendor = () => SystemCheck.DetectVendor().Vendor;

    /// <summary>ComfyUI 포터블을 어느 그래픽카드용 묶음으로 받을지: 설정에서 고른 종류, 자동이면 호출자가 넘긴 감지 결과, 그것도 없으면 여기서 감지.</summary>
    internal string ComfyVendor(string? detectedVendor) =>
        settings.GpuVendor != "auto" ? settings.GpuVendor : detectedVendor ?? DetectVendor();

    /// <summary>detectedVendor = 같은 흐름의 PC 점검이 이미 감지한 그래픽카드 종류(<see cref="SystemCheck.DetectVendor(IReadOnlyList{SystemCheck.Gpu})"/>).
    /// 설치 창은 [준비 시작!] 때 점검에 쓴 nvidia-smi 답으로 정한 종류를 넘긴다 — 2026-11-11까지는 점검이 한 번, 여기서 또 한 번 물어
    /// 멈춘 nvidia-smi면 8초를 두 번 기다렸고 점검이 본 카드와 받는 묶음이 어긋날 수 있었다. null이면(설치기만 따로 쓸 때) 여기서 새로 묻는다.</summary>
    public async Task InstallAsync(Component c, IProgress<InstallProgress> progress, CancellationToken ct, string? detectedVendor = null)
    {
        Directory.CreateDirectory(settings.RuntimeRoot);
        // 연결한 폴더에 ComfyUI가 없으면 연결이 틀린 것이다 — RuntimeRoot에 하나 더 받아도 앱은 연결한 쪽을 돌리므로 쓰이지 않는다
        if (c.Id == ComponentCatalog.ComfyPortable.Id && !OwnsPython)
            throw new InvalidOperationException(Loc.F($"연결한 폴더에 ComfyUI가 없다냥: {settings.LinkedComfyRoot}\n설정 → 구성요소 → 기존 설치 연결의 경로를 고치거나 비워 줘냥."));
        // 이미 있던 것인가 — 패키지가 딸린 구성요소는 파일이 있으면 다시 받지 않고(v1.13.0까지의 설치: 패키지만 빠졌다),
        // 연결한 ComfyUI에서는 앱이 지금 새로 넣는 것에만 패키지를 깐다
        bool had = c.HasDeps && File.Exists(Resolve(c.Probe));
        bool installDeps = c.HasDeps && (OwnsPython || !had);
        switch (c.Kind)
        {
            case ComponentKind.File:
                if (!had) await DownloadAsync(c, Resolve(c.Target), progress, ct);
                if (installDeps) await InstallDepsAsync(c, progress, ct);
                break;

            case ComponentKind.ArchiveZip:
            {
                if (!had)
                {
                    var tmp = Resolve($"tmp/{c.Id}.zip");
                    await DownloadAsync(c, tmp, progress, ct);
                    progress.Report(new(c.Id, Loc.T("압축 푸는 중이다냥..."), 99, 0, 0));
                    ExtractZip(tmp, Resolve(c.Target), c.StripRootFolder);
                    File.Delete(tmp);
                }
                if (installDeps) await InstallDepsAsync(c, progress, ct);
                break;
            }

            case ComponentKind.Archive7z:
            {
                // ComfyUI 포터블은 그래픽카드 종류(설정 GpuVendor, auto면 감지)에 맞는 묶음을 받는다 (2026-10-21 AMD·구형 NVIDIA 지원)
                bool isComfy = c.Id == ComponentCatalog.ComfyPortable.Id;
                string vendor = "nvidia";
                if (isComfy)
                {
                    vendor = ComfyVendor(detectedVendor);
                    c = c with { Url = ComponentCatalog.ComfyPortableUrl(vendor) };
                }
                var amdDir = Resolve(ComponentCatalog.ComfyAmdDirName); var stdDir = Resolve(ComponentCatalog.ComfyDirName);
                // 지난번에 풀어 두고 이름만 못 바꾼 AMD 폴더가 있으면(백신이 파일을 잡고 있던 경우 등) 1.8GB를 다시 받지 않고 이름 변경부터 한다
                bool amdExtracted = isComfy && File.Exists(Path.Combine(amdDir, "ComfyUI", "main.py"));
                if (!amdExtracted)
                {
                    // 임시 파일 이름에 벤더를 넣는다 — 받다 만 NVIDIA .part에 AMD 묶음을 이어 붙이면 깨진 7z가 된다
                    var tmp = Resolve(isComfy ? $"tmp/{c.Id}-{vendor}.7z" : $"tmp/{c.Id}.7z");
                    if (!File.Exists(tmp)) await DownloadAsync(c, tmp, progress, ct);   // 지난번에 다 받고 풀다가 멈췄으면 다시 받지 않는다
                    progress.Report(new(c.Id, Loc.T("압축 푸는 중이다냥... (수 분 걸린다냥)"), 99, 0, 0));
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
        progress.Report(new(c.Id, Loc.T("완료"), 100, c.ApproxBytes, c.ApproxBytes));
    }

    /// <summary>프로그램을 돌려 끝날 때까지 기다리고 종료 코드와 출력(표준 출력 + 오류)을 돌려준다 — 시험에서 pip 대신 넣는다.</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>> RunProcess = RunProcessAsync;

    private static async Task<(int Exit, string Output)> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var p = Process.Start(psi)!;
        try
        {
            // 두 흐름을 같이 읽는다 — 하나만 읽으면 다른 쪽 버퍼가 차서 pip가 멈춘다
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>구성요소의 requirements.txt·패키지를 ComfyUI의 내장 파이썬에 설치하고 표식을 남긴다.
    /// 이미 깔린 패키지는 지금 버전으로 묶는다(-c) — 노드의 요구가 ComfyUI가 가져온 torch·numpy를 바꾸면 ComfyUI가 깨진다.
    /// 2026-11-18 실측(포터블 cu130, 파이썬 3.13): 세 노드 합쳐 1분쯤, 0.9GB, 기존 패키지 변경 0, 그 뒤 쓰는 노드 11종 전부 로드.</summary>
    private async Task InstallDepsAsync(Component c, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var python = Resolve($"{ComponentCatalog.ComfyDirName}/python_embeded/python.exe");
        if (!File.Exists(python))
            throw new FileNotFoundException(Loc.T("ComfyUI의 파이썬(python.exe)이 없다냥. 구성요소 설치 순서를 확인하라냥."), python);
        progress.Report(new(c.Id, Loc.T("파이썬 패키지 설치 중이다냥... (1~2분 걸린다냥)"), 99, 0, 0));

        string[] pip = ["-s", "-m", "pip"];
        var (listExit, listed) = await RunProcess(python, [.. pip, "list", "--format=freeze", "--disable-pip-version-check"], ct);
        if (listExit != 0) throw DepsFailed(c, listExit, listed);
        var constraints = Resolve($"tmp/{c.Id}-constraints.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(constraints)!);
        // "이름==버전" 꼴만 쓴다 — 경고 줄이나 "이름 @ file://…" 꼴은 제약 파일에서 오류가 난다
        await File.WriteAllLinesAsync(constraints,
            listed.Split('\n').Select(l => l.Trim()).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[A-Za-z0-9._-]+==\S+$")), ct);

        string[] what = [.. c.Requirements != null ? new[] { "-r", Resolve(c.Requirements) } : [], .. c.PipPackages ?? []];
        var (exit, output) = await RunProcess(python,
            [.. pip, "install", "--disable-pip-version-check", "--no-warn-script-location", "-c", constraints, .. what], ct);
        if (exit != 0) throw DepsFailed(c, exit, output);
        File.Delete(constraints);

        // opencv-python(-headless)과 opencv-contrib-python은 같은 cv2 폴더에 풀린다 — 나중에 깔린 쪽이 덮어, opencv-python이 뒤면 contrib의
        // cv2.ximgproc이 사라지고 LayerStyle이 로드되지 않는다(2026-11-18 실측: LayerStyle → controlnet_aux 순서). 둘 다 있으면 contrib를 맨 뒤에 다시 깐다.
        var (afterExit, after) = await RunProcess(python, [.. pip, "list", "--format=freeze", "--disable-pip-version-check"], ct);
        if (afterExit != 0) throw DepsFailed(c, afterExit, after);
        var installed = after.Split('\n').Select(l => l.Trim()).ToList();
        var contrib = installed.FirstOrDefault(l => l.StartsWith("opencv-contrib-python==", StringComparison.OrdinalIgnoreCase));
        if (contrib != null && installed.Any(l => l.StartsWith("opencv-python==", StringComparison.OrdinalIgnoreCase)
                                               || l.StartsWith("opencv-python-headless==", StringComparison.OrdinalIgnoreCase)))
        {
            var (fixExit, fixOut) = await RunProcess(python,
                [.. pip, "install", "--disable-pip-version-check", "--no-warn-script-location", "--force-reinstall", "--no-deps", contrib], ct);
            if (fixExit != 0) throw DepsFailed(c, fixExit, fixOut);
        }
        await File.WriteAllTextAsync(DepsMarker(c), DateTime.UtcNow.ToString("o"), ct);
    }

    private static InvalidOperationException DepsFailed(Component c, int exit, string output)
    {
        var tail = output.Trim();
        if (tail.Length > 600) tail = "…" + tail[^600..];
        return new InvalidOperationException(Loc.F($"{Loc.T(c.DisplayName)} 파이썬 패키지 설치 실패다냥 (exit {exit}): {tail}"));
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

    /// <summary>요청을 보내고 응답 머리까지 받는다 — 시험에서 가짜 응답을 넣는다.</summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send =
        (req, ct) => Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

    /// <summary>이 시간 동안 한 바이트도 오지 않으면 멈춘 연결로 보고 끊는다 — HttpClient의 Timeout은 전체 시간이라 6.5GB 파일에는 못 쓴다(무한).
    /// 2026-11-18까지는 받는 도중 와이파이가 끊기면 읽기가 돌아오지 않아 앱을 끌 때까지 그 줄에서 멈춰 있었다.</summary>
    internal TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    /// <summary>끊긴 뒤 이어받기까지 기다리는 시간의 단위(실패가 이어지면 그 횟수만큼 곱한다).</summary>
    internal TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    /// <summary>한 바이트도 더 받지 못한 채 이어받기를 되풀이하는 횟수 — 조금이라도 받았으면 다시 센다(느리게라도 오는 회선은 끝까지 받는다).</summary>
    internal const int MaxResumes = 5;

    /// <summary>서버가 주지 않겠다고 한 것(HTTP 오류·파일 대신 웹 페이지) — 다시 보내도 같으므로 이어받기를 하지 않는다.</summary>
    private sealed class DownloadRejectedException(string message) : HttpRequestException(message);

    private async Task DownloadAsync(Component c, string dest, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        // 받다가 끊기거나 멈추면 받은 데서부터 스스로 이어받는다 — 예전에는 한 번 끊기면 설치 전체가 멈추고 사용자가 다시 눌러야 했다
        for (int stuck = 0; ; )
        {
            long before = Size(part);
            try { await FetchAsync(c, part, progress, ct); break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or HttpRequestException and not DownloadRejectedException)
            {
                stuck = Size(part) > before ? 0 : stuck + 1;
                if (stuck > MaxResumes) throw;
                progress.Report(new(c.Id, Loc.F($"연결이 끊겼다냥 — 이어받는 중 ({Math.Max(1, stuck)}/{MaxResumes})"), 0, 0, 0));
                await Task.Delay(RetryDelay * Math.Max(1, stuck), ct);
            }
        }

        // 해시가 적힌 구성요소는 받은 파일을 확인한다 — 남의 저장소에서 받는 모델은 주소가 그대로여도 내용이 바뀌거나 중간에 깨질 수 있다
        if (c.Sha256 is { Length: > 0 } expected)
        {
            progress.Report(new(c.Id, Loc.T("받은 파일 확인 중이다냥..."), 99, 0, 0));
            string actual;
            await using (var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException(Loc.F($"{Loc.T(c.DisplayName)} 파일이 깨졌거나 배포본이 바뀌었다냥 (해시 불일치) — 다시 시도해 줘냥."));
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    /// <summary>한 번의 요청으로 .part에 이어 받는다. 끝까지 받으면 돌아오고, 끊기면 IOException·HttpRequestException, 멈추면 TimeoutException을 던진다.</summary>
    private async Task FetchAsync(Component c, string part, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // 게이트웨이 오류(502·503·504)는 잠깐 뒤 다시 보낸다 — 2026-11-09 실측: GitHub이 Aletheia Lens 저장소의 릴리스 자산 주소에
            // 간헐적으로 504를 돌려줬다(같은 시각 다른 저장소 자산은 정상, 몇 분 전에는 200). 한 번 실패로 엔진 설치 전체가 멈추지 않게.
            HttpResponseMessage resp;
            for (int attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, c.Url);
                if (c.UseCivitaiKey && !string.IsNullOrWhiteSpace(settings.CivitaiApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.CivitaiApiKey);
                if (existing > 0)
                    req.Headers.Range = new RangeHeaderValue(existing, null);

                idle.CancelAfter(IdleTimeout);
                resp = await Send(req, idle.Token);
                int code = (int)resp.StatusCode;
                if (code == 416 && existing > 0)
                {
                    // 받다 만 파일이 사실은 다 받은 것(옮기기 직전에 앱이 꺼짐)이거나 서버 파일보다 크다 — 이어받기 범위가 비어 416이 난다.
                    // 지우고 처음부터 다시 받는다 (그대로 두면 이 구성요소가 영원히 설치되지 않는다).
                    resp.Dispose();
                    try { File.Delete(part); } catch (IOException) { }
                    existing = 0; attempt--;
                    continue;
                }
                if (code is not (502 or 503 or 504) || attempt >= 3) break;
                resp.Dispose();
                progress.Report(new(c.Id, Loc.F($"서버가 잠깐 응답하지 않는다냥 (HTTP {code}) — 다시 시도 {attempt + 1}/3"), 0, 0, 0));
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct);
            }
            using var respDispose = resp;
            if (existing > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                existing = 0; // 서버가 이어받기를 지원하지 않음 — 처음부터
            }
            var civitaiHint = c.UseCivitaiKey ? Loc.T(" — civitai 로그인이 필요한 모델일 수 있다냥. 설정에서 civitai API 키를 넣어 줘냥.") : "";
            if (!resp.IsSuccessStatusCode)
            {
                var hint = (int)resp.StatusCode is 401 or 403 ? civitaiHint : "";
                throw new DownloadRejectedException(Loc.F($"{Loc.T(c.DisplayName)} 다운로드 실패다냥 (HTTP {(int)resp.StatusCode}){hint}"));
            }
            // 로그인이 필요한 모델은 200으로 로그인 쪽을 주기도 한다 — 그것을 모델 이름으로 저장하면 '설치됨'인데 쓸 수 없다
            if (string.Equals(resp.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                throw new DownloadRejectedException(Loc.F($"{Loc.T(c.DisplayName)} 다운로드 실패다냥 — 파일 대신 웹 페이지가 왔다냥{civitaiHint}"));

            long? length = resp.Content.Headers.ContentLength;
            long total = (length ?? 0) + existing;
            if (total <= 0) total = c.ApproxBytes;

            long done = existing;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create,
                                                  FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                var lastReport = DateTime.MinValue;
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);   // 읽을 때마다 다시 잰다 — 오는 동안에는 끊지 않는다
                    int n = await src.ReadAsync(buf, idle.Token);
                    if (n <= 0) break;
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
            // 서버가 알린 길이보다 덜 오고 끝났으면 끊긴 것이다 — 덜 받은 파일을 다 받은 것으로 옮기지 않는다
            if (length is > 0 && done < existing + length.Value)
                throw new IOException(Loc.F($"{Loc.T(c.DisplayName)} — 받는 도중 연결이 끊겼다냥."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.F($"{Loc.T(c.DisplayName)} — 서버가 {(int)IdleTimeout.TotalSeconds}초 동안 응답하지 않았다냥."));
        }
    }

    /// <summary>zip을 destDir에 푼다. stripRoot면 최상위 폴더 한 겹(GitHub 소스 zip의 `저장소-main/`)을 벗긴다.
    /// destDir 밖으로 풀리는 항목(`../`, 절대 경로)과 destDir 자신으로 풀리는 파일 항목(`.`)은 건너뛴다(zip-slip 방지).</summary>
    internal static void ExtractZip(string zipPath, string destDir, bool stripRoot)
    {
        Directory.CreateDirectory(destDir);
        // 끝에 구분자를 붙여 비교한다 — 그냥 StartsWith(destDir)면 `../ComfyUI-WD14-Tagger-evil/x.py`·`../ComfyUI-WD14-Tagger.py`처럼
        // destDir 이름으로 시작하는 형제 경로도 안쪽으로 통과했다
        var root = Path.GetFullPath(destDir);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
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
            // zip-slip 방지. destDir 자신의 폴더 항목('./')은 GetFullPath가 끝 구분자를 남겨 root와 같아지므로 그대로 받는다.
            // destDir 자신으로 풀리는 파일 항목('.'·'sub/..')은 탈출 항목처럼 건너뛴다 — 2026-11-11: 이것까지 받아 ExtractToFile(destDir)이
            // UnauthorizedAccessException을 던지면서 그 구성요소 설치가 통째로 멈추고 뒤의 항목도 풀리지 않았다
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            if (Directory.Exists(target)) continue;   // 이미 있는 폴더로 풀리는 파일 항목('js/.' 등)도 같은 이유로 건너뛴다
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private async Task Extract7zAsync(string archive, string destDir, CancellationToken ct)
    {
        var sevenZip = Resolve(ComponentCatalog.SevenZip.Target);
        if (!File.Exists(sevenZip))
            throw new FileNotFoundException(Loc.T("7zr.exe가 없다냥. 구성요소 설치 순서를 확인하라냥."), sevenZip);
        Directory.CreateDirectory(destDir);
        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 인자는 하나씩 넘긴다 — 한 줄로 이으면 드라이브 뿌리(E:\)를 고른 경우 `-o"E:\"`의 끝 역슬래시가 따옴표를 먹어 7zr이 실패한다
        foreach (var a in new[] { "x", "-y", "-o" + destDir, archive }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(Loc.F($"7z 압축 해제 실패다냥 (exit {p.ExitCode}): {await p.StandardError.ReadToEndAsync(ct)}"));
    }
}
