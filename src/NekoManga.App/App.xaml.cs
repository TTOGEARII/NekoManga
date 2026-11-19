using System.IO;
using System.Windows;
using NekoManga.Core;
using NekoManga.Core.Runtime;
using Application = System.Windows.Application;

namespace NekoManga.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>--demo-settings: 설정 창만 띄워 레이아웃을 확인한다 (UI 작업용).</summary>
    public bool DemoSettings { get; set; }

    /// <summary>--demo-setup: 설치 마법사만 띄운다 (UI 확인용 — 설정 파일 안 건드림).</summary>
    public bool DemoSetup { get; set; }

    /// <summary>--result &lt;경로…&gt;: 결과 보기 창만 띄운다 (UI 확인용).</summary>
    public List<string>? ResultImages { get; set; }

    /// <summary>--demo-files &lt;폴더&gt;: 메인 창 목록을 미리 채운다 (UI 확인용).</summary>
    public string? PreloadDir { get; set; }

    /// <summary>--review &lt;이미지…&gt;: 검열 감지만 돌려 검토 창을 띄운다 (검토 흐름 UI 확인용 — 제거는 안 한다).</summary>
    public List<string>? ReviewImages { get; set; }

    // ── 처리되지 않은 예외 (2026-11-13 점검 9번) ──
    // 전에는 받는 곳이 없어, 화면 처리기에서 샌 예외 하나(잠긴 파일을 읽는 버튼 등)로 앱이 도는 작업째 꺼졌다. 기록하고 알린 뒤 창은 살려 둔다 —
    // 몇 시간짜리 작업이 버튼 하나의 오류로 같이 죽는 것보다 낫다. 연달아 터지면(그리는 중의 오류가 되풀이되는 식) 붙잡지 않고 끝낸다.
    internal static string CrashLogPath => Path.Combine(AppSettings.DataDir, "crash.log");
    private static readonly object CrashLock = new();
    private bool _crashDialogOpen;
    private readonly Queue<DateTime> _recentCrashes = new();

    internal static void LogCrash(string where, Exception? ex)
    {
        try
        {
            lock (CrashLock)
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var path = CrashLogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", overwrite: true);   // 끝없이 자라지 않게
                var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{version} {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash("화면", e.Exception);   // loc:none
            var now = DateTime.UtcNow;
            _recentCrashes.Enqueue(now);
            while (_recentCrashes.Count > 0 && now - _recentCrashes.Peek() > TimeSpan.FromSeconds(10)) _recentCrashes.Dequeue();
            if (_recentCrashes.Count >= 5)
            {
                // 10초에 다섯 번 — 살려 둘 수 없는 상태다. 붙잡지 않고 두면 알림 창이 떠 있을 때는 그 예외가 알림 창의 catch에 먹혀 끝나지도 않는다(2026-11-14 검수): 스스로 끝낸다
                LogCrash("연달아 터져 앱을 끝냅니다", null);   // loc:none
                Environment.Exit(1);
            }
            e.Handled = true;
            if (_crashDialogOpen) return;            // 알림 창이 떠 있는 동안 또 난 것은 기록만
            _crashDialogOpen = true;
            try
            {
                System.Windows.MessageBox.Show(
                    Loc.T("예상 못 한 오류가 났다냥 — 방금 동작은 끝나지 않았을 수 있다냥. 도는 작업은 계속된다냥.\n\n") +
                    Loc.F($"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n자세한 기록: {CrashLogPath}"),
                    "NekoManga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            finally { _crashDialogOpen = false; }
            // 창이 하나도 뜨기 전이었으면(시작 도중) 살려 둘 것이 없다 — 창 없는 프로세스로 남지 않게 끝낸다
            if (!Windows.OfType<Window>().Any(w => w.IsVisible)) Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("백그라운드 작업", e.Exception); e.SetObserved(); };   // loc:none
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("치명적", e.ExceptionObject as Exception);   // loc:none
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookUnhandledExceptions();

        // 화면 언어 — 설정을 읽기 전에는 Windows의 언어로(설정을 못 읽었다는 알림도 그 언어로 나온다), 읽은 뒤에는 고른 언어로.
        // 창을 만들기 전에 정한다: 창은 생성자에서 XAML의 글을 이 언어로 바꾼다(UiText)
        var windowsLanguage = System.Globalization.CultureInfo.CurrentUICulture;
        Loc.SetLanguage(Loc.Resolve(null, windowsLanguage));
        Settings = AppSettings.Load();
        Loc.SetLanguage(Loc.Resolve(Settings.UiLanguage, windowsLanguage));
        UiText.Init();
        // 설정 파일을 못 읽었으면(백업에서 되살렸거나 기본값으로 시작) 말없이 넘어가지 않는다 — 못 읽은 파일을 어디에 남겼는지도 알려 준다
        if (AppSettings.LoadProblem is { } settingsProblem)
            System.Windows.MessageBox.Show(settingsProblem, Loc.T("NekoManga 설정"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // 앱이 끝날 때 KeepComfyWarm=false면 이 앱이 띄운 채색 엔진을 끈다('엔진 켜기'로 고정한 서버·사용자가 띄운 서버는 그대로) —
        // 작업 중에 닫으면 작업의 정리(사용 놓기)가 돌기 전에 프로세스가 끝나 서버가 남았다. Exit 이벤트가 아니라 ProcessExit에 건다:
        // 업데이트 적용(Velopack ApplyUpdatesAndRestart)은 Environment.Exit로 끝나 Exit 이벤트가 오지 않는다. 끄기를 기다리지 않아 종료가 늦어지지 않는다
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { NekoManga.Core.Comfy.ComfyServer.AppExiting(Settings); }
            catch { }
        };

        // 일본 흑백 만화풍 테마 (화이트 기본 / 다크 반전) — 창이 뜨기 전에 적용해야 한다
        AppTheme.Apply(Settings.Theme);
        MangaTextures.Refresh();   // 원고 격자·톤망점 타일 (테마 색 반영)

        // 만화 선화 고양이 로고 — 스냅샷 브러시로 그린다 (라이브 브러시를 주면 Window.Icon이
        // 얼려 버려 테마 전환이 크래시). 전환 때는 Theme_Changed가 RefreshLogo로 다시 만든다.
        AppTheme.RefreshLogo();

        // 기존 설치 자동 감지 — 이미 ComfyUI·BT를 쓰고 있으면 재다운로드 없이 연결한다.
        AutoLinkExistingInstalls();
        try { Settings.Save(); }
        catch (Exception ex) { LogCrash("시작할 때 설정 저장", ex); }   // 못 써도(잠김·디스크 가득) 앱은 뜬다 — 다음에 설정을 바꿀 때 다시 저장한다 · loc:none

        if (DemoSettings)
        {
            new SettingsWindow().Show();
            return;
        }

        if (DemoSetup)
        {
            new SetupWindow(demo: true).Show();
            return;
        }

        if (ResultImages is { Count: > 0 })
        {
            new ResultWindow(ResultImages, 0).Show();
            return;
        }

        if (ReviewImages is { Count: > 0 })
        {
            // 감지만 돌려 검토 창을 띄운다 — 마스크는 창을 닫을 때 같이 버린다
            using var runner = new NekoManga.Core.Pipeline.PipelineRunner(Settings);
            var pages = new List<NekoManga.Core.Pipeline.PipelineRunner.ReviewPage>();
            foreach (var f in ReviewImages.Where(File.Exists))
            {
                var pg = new NekoManga.Core.Pipeline.PipelineRunner.ReviewPage(f);
                using var img = NekoManga.Core.ImgIo.Read(f, OpenCvSharp.ImreadModes.Color);
                var m = runner.DetectCensorMask(img, null, out var kind);
                pg.Summary = kind;
                pg.Mask = m ?? new OpenCvSharp.Mat(img.Rows, img.Cols, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.Black);
                pages.Add(pg);
            }
            var w = new MaskReviewWindow(pages);
            w.Closed += (_, _) => { foreach (var p in pages) p.Mask?.Dispose(); };
            w.Show();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;

        // 내장 엔진(번역·기본 검열제거)에 필요한 모델이 빠져 있으면 준비 마법사를 보여준다.
        // ComfyUI(채색·재생성·커스텀 검열)는 선택 — 마법사에서도 선택으로 표시된다.
        var installer = new ComponentInstaller(Settings);
        // 파일만 받고 파이썬 패키지가 안 깔린 노드(v1.13.0까지의 설치)가 있어도 띄운다 — 그대로 두면 채색·재생성이 '워크플로 검증 실패'로 멈춘다
        if (installer.MissingEngine().Count > 0 || installer.NeedsRepair().Count > 0)
        {
            new SetupWindow().ShowDialog();
            main.SyncFromSettings();   // 메인 창은 위에서 이미 만들어졌다 — 준비 창이 바꾼 설정(PC 맞춤 설정이 끈 자동 채색 등)을 스위치에 다시 채운다
        }

        main.Show();

        // 옛 작업 폴더 치우기 — 끝난 작업은 제 폴더를 지우지만, 꺼지거나 죽어서 남은 것과 옛 버전이 쌓아 둔 것은 여기서(7일 넘은 것만, 백그라운드)
        _ = Task.Run(() => { try { NekoManga.Core.Pipeline.WorkFolders.SweepOld(); } catch { } });

        // --theme-test: 메인 창(아이콘 포함)이 뜬 뒤 테마를 자동 순환 — 아이콘이 로고 브러시를
        // 얼려 전환이 크래시하던 회귀의 자동 검증용. 결과를 파일로 남기고 종료한다.
        var cliArgs = Environment.GetCommandLineArgs();
        var themeTestIdx = Array.IndexOf(cliArgs, "--theme-test");
        if (themeTestIdx >= 0 && themeTestIdx + 1 < cliArgs.Length)
        {
            var outPath = cliArgs[themeTestIdx + 1];
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    foreach (var t in new[] { "Dark", "Light", "Dark", "Light" })
                    {
                        AppTheme.Apply(t);
                        MangaTextures.Refresh();
                        AppTheme.RefreshLogo();
                    }
                    System.IO.File.WriteAllText(outPath, "THEME-CYCLE OK");
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(outPath, "THEME-CYCLE FAIL: " + ex);
                }
                Shutdown();
            };
            timer.Start();
        }

        // 채색 엔진 미리 켜기 — 첫 채색에서 1~2분 기다리지 않게 백그라운드로 데워 둔다.
        // 실패해도 조용히 넘어간다 (채색을 쓸 때 어차피 다시 켠다).
        if (Settings.PrewarmComfyOnLaunch && Settings.ComfyAvailable)
            _ = Task.Run(async () =>
            {
                try { await NekoManga.Core.Comfy.EngineManager.StartAsync(Settings); }
                catch { }
            });
    }

    private static void AutoLinkExistingInstalls()
    {
        if (string.IsNullOrWhiteSpace(App.Settings.LinkedComfyRoot))
        {
            foreach (var cand in new[]
            {
                @"D:\ComfyUI\ComfyUI_windows_portable",
                @"C:\ComfyUI\ComfyUI_windows_portable",
            })
                if (System.IO.File.Exists(System.IO.Path.Combine(cand, "ComfyUI", "main.py")))
                {
                    App.Settings.LinkedComfyRoot = cand;
                    break;
                }
        }
    }
}
