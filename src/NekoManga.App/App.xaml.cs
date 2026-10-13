using System.Windows;
using NekoManga.Core;
using NekoManga.Core.Runtime;
using Application = System.Windows.Application;

namespace NekoManga.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;

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

        var main = new MainWindow();
        MainWindow = main;

        // 빠진 구성요소가 있으면 먼저 준비 마법사를 보여준다.
        // 건너뛰어도 창은 열어준다 — 설정 → 구성요소에서 언제든 다시 설치할 수 있다.
        var installer = new ComponentInstaller(Settings);
        if (installer.Missing().Count > 0)
            new SetupWindow().ShowDialog();

        main.Show();
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
        if (string.IsNullOrWhiteSpace(App.Settings.LinkedBallonsRoot))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var cand = System.IO.Path.Combine(desktop, "Ballonstranslator_win_minium");
            if (System.IO.File.Exists(System.IO.Path.Combine(cand, "ballontranslator", "launch.py")))
                App.Settings.LinkedBallonsRoot = cand;
        }
    }
}
