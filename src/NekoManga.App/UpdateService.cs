using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Velopack;
using Velopack.Sources;

namespace NekoManga.App;

/// <summary>GitHub Releases에서 새 버전을 확인하고 적용한다 (Velopack).
/// FanboxUpdater에서 검증된 패턴: 백신 선적재 → 재시작 감시자 → 자체 스플래시 → silent 적용.</summary>
public class UpdateService
{
    public const string RepoUrl = "https://github.com/TTOGEARII/NekoManga";
    private const string ExeName = "NekoManga";
    private const string StubName = "NekoManga.exe";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));

    /// <summary>설치형(Setup.exe)으로 설치된 경우에만 업데이트 가능</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int>? progress = null, Action? beforeRestart = null)
    {
        await _manager.DownloadUpdatesAsync(info, progress);
        await PrewarmAntivirusScanAsync(info);
        beforeRestart?.Invoke();
        var version = info.TargetFullRelease.Version.ToString();
        StartRestartWatchdog(version);
        StartApplySplash(version);
        // Velopack 기본 적용 다이얼로그는 이미지 커스텀이 불가능해(--splashImage는 Setup.exe 전용)
        // silent로 적용하고, 그동안 위에서 띄운 자체 스플래시 창을 보여준다.
        _manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true);
        Environment.Exit(0);
    }

    /// <summary>--demo-splash 실행 인자용: 적용 스플래시를 10초간 미리 본다.</summary>
    public static void ShowApplySplashDemo() => StartApplySplash("0.0.0-demo", timeoutSeconds: 10);

    /// <summary>Velopack 언인스톨러(Update.exe --uninstall)를 실행한다. 설치형에서만 유효.</summary>
    public static void LaunchUninstaller()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(root, "Update.exe"),
            Arguments = "--uninstall",
            UseShellExecute = false,
            // current 폴더를 잠그지 않도록 작업 폴더를 밖으로 뺀다
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>업데이트 적용 동안 보여줄 자체 스플래시 창(PowerShell WinForms, 움직이는 GIF).
    /// 앱 폴더(current)가 통째로 교체되므로 이미지·스크립트를 임시 폴더로 복사해 실행한다.</summary>
    private static void StartApplySplash(string targetVersion, int timeoutSeconds = 180)
    {
        try
        {
            var src = Path.Combine(AppContext.BaseDirectory, "assets", "splash.gif");
            if (!File.Exists(src)) return;
            var tmp = Path.Combine(Path.GetTempPath(), "NekoManga-splash");
            Directory.CreateDirectory(tmp);
            var img = Path.Combine(tmp, "splash.gif");
            File.Copy(src, img, overwrite: true);

            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var sqVersion = Path.Combine(root, "current", "sq.version");
            var script = Path.Combine(tmp, "splash.ps1");
            File.WriteAllText(script, $$"""
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                [System.Windows.Forms.Application]::EnableVisualStyles()

                # WPF Image는 GIF 애니메이션을 재생하지 못해 WinForms PictureBox를 쓴다
                $gif = [System.Drawing.Image]::FromFile('{{img}}')

                $pb = New-Object System.Windows.Forms.PictureBox
                $pb.Image = $gif
                $pb.SizeMode = 'AutoSize'

                $f = New-Object System.Windows.Forms.Form
                $f.FormBorderStyle = 'None'
                $f.StartPosition = 'CenterScreen'
                $f.ClientSize = New-Object System.Drawing.Size($gif.Width, $gif.Height)
                $f.TopMost = $true
                $f.ShowInTaskbar = $false
                $f.BackColor = [System.Drawing.Color]::FromArgb(14, 14, 18)
                $f.Controls.Add($pb)

                $sq = '{{sqVersion}}'
                $deadline = (Get-Date).AddSeconds({{timeoutSeconds}})
                $timer = New-Object System.Windows.Forms.Timer
                $timer.Interval = 500
                $timer.Add_Tick({
                    $applied = $false
                    try { $applied = (Test-Path $sq) -and (Select-String -Path $sq -Pattern '<version>{{targetVersion}}' -SimpleMatch -Quiet) } catch {}
                    $running = [bool](Get-Process {{ExeName}} -ErrorAction SilentlyContinue)
                    if (($applied -and $running) -or ((Get-Date) -gt $deadline)) { $timer.Stop(); $f.Close() }
                })
                $timer.Start()
                [void]$f.ShowDialog()
                """,
                // PowerShell 5.1은 BOM 없는 UTF-8 스크립트를 ANSI로 읽어 비ASCII 경로가 깨진다
                new System.Text.UTF8Encoding(true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                // 감시자와 마찬가지로 current 폴더를 잠그지 않도록 작업 폴더를 밖으로 뺀다
                WorkingDirectory = tmp,
            });
        }
        catch { /* 스플래시 실패는 치명적이지 않음 — silent 적용은 그대로 진행된다 */ }
    }

    /// <summary>백신(V3 Lite 등)이 업데이트로 교체된 "처음 보는" 실행 파일의 실행을 몇 분씩 차단하는 문제가 있어,
    /// 재시작 전에 새 버전 파일들을 임시 폴더에 미리 풀어 백신이 먼저 검사하도록 유도한다 (해시 캐시 선적재).</summary>
    private static async Task PrewarmAntivirusScanAsync(UpdateInfo info)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var pkg = Path.Combine(root, "packages", info.TargetFullRelease.FileName);
            if (!File.Exists(pkg)) return;

            var dest = Path.Combine(Path.GetTempPath(), "NekoManga-prewarm");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            await Task.Run(() =>
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(pkg);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith("lib/app/", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Length == 0) continue;
                    var target = Path.Combine(dest, name["lib/app/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            });

            // 백신이 방금 쓴 파일들을 검사할 시간을 준다.
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        catch { /* 사전 검사 유도는 실패해도 업데이트 자체에는 지장 없음 */ }
    }

    /// <summary>Update.exe의 자동 재시작이 "액세스 거부"로 실패하는 환경 대비 (velopack#198):
    /// 외부 cmd 감시자를 먼저 띄워 새 버전 적용 후 앱이 안 떠 있으면 대신 실행한다.</summary>
    private static void StartRestartWatchdog(string targetVersion)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var stub = Path.Combine(root, StubName);
            var sqVersion = Path.Combine(root, "current", "sq.version");
            if (!File.Exists(stub)) return;

            // timeout은 콘솔 없는 환경에서 실패할 수 있어 ping으로 1초씩 대기한다.
            // find/findstr는 PATH에 GNU find가 앞서는 환경(Git Bash 등)이 있어 절대경로로 고정.
            var script = Path.Combine(Path.GetTempPath(), "NekoManga-restart.cmd");
            File.WriteAllText(script, $"""
                @echo off
                set "sys=%SystemRoot%\System32"
                for /l %%i in (1,1,60) do (
                  "%sys%\findstr.exe" /c:"<version>{targetVersion}" "{sqVersion}" >nul 2>&1 && goto applied
                  "%sys%\ping.exe" -n 2 127.0.0.1 >nul
                )
                :applied
                "%sys%\ping.exe" -n 5 127.0.0.1 >nul
                rem 교체 직후 백신이 새 실행 파일을 몇 분간 차단할 수 있어
                rem 앱이 뜰 때까지 10초 간격으로 최대 6분간 재시도한다.
                for /l %%j in (1,1,36) do (
                  "%sys%\tasklist.exe" /fi "imagename eq {ExeName}.exe" | "%sys%\find.exe" /i "{ExeName}.exe" >nul && goto end
                  start "" "{stub}"
                  "%sys%\ping.exe" -n 11 127.0.0.1 >nul
                )
                :end
                """);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                // 앱의 CWD(…\current)를 물려받으면 감시자가 그 폴더를 잠가
                // Update.exe의 current 교체를 막아버린다 — 반드시 앱 폴더 밖으로.
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch { /* 감시자 실패는 치명적이지 않음 — 기존 Velopack 재시작 경로는 그대로 시도된다 */ }
    }
}
