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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();

        // 일본 흑백 만화풍 테마 (화이트 기본 / 다크 반전) — 창이 뜨기 전에 적용해야 한다
        AppTheme.Apply(Settings.Theme);
        MangaTextures.Refresh();   // 원고 격자·톤망점 타일 (테마 색 반영)

        // 만화 선화 고양이 로고 — 스냅샷 브러시로 그린다 (라이브 브러시를 주면 Window.Icon이
        // 얼려 버려 테마 전환이 크래시). 전환 때는 Theme_Changed가 RefreshLogo로 다시 만든다.
        AppTheme.RefreshLogo();

        // 기존 설치 자동 감지 — 이미 ComfyUI·BT를 쓰고 있으면 재다운로드 없이 연결한다.
        AutoLinkExistingInstalls();
        Settings.Save();

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

        var main = new MainWindow();
        MainWindow = main;

        // 내장 엔진(번역·기본 검열제거)에 필요한 모델이 빠져 있으면 준비 마법사를 보여준다.
        // ComfyUI(채색·엔진 LaMa 검열)는 선택 — 마법사에서도 선택으로 표시된다.
        var installer = new ComponentInstaller(Settings);
        if (installer.MissingEngine().Count > 0)
            new SetupWindow().ShowDialog();

        main.Show();

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
