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

        // 액센트를 네코핑크로 — 토글·Primary 버튼·포커스 링이 모두 따라온다.
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0xFF, 0x77, 0xA8),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);

        Settings = AppSettings.Load();

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
        // ComfyUI(채색·강력 검열)는 선택 — 마법사에서도 선택으로 표시된다.
        var installer = new ComponentInstaller(Settings);
        if (installer.MissingEngine().Count > 0)
            new SetupWindow().ShowDialog();

        main.Show();

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
