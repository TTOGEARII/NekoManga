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

        // 내장 엔진(번역·기본 검열제거)에 필요한 모델이 빠져 있으면 준비 마법사를 보여준다.
        // ComfyUI(채색·강력 검열)는 선택 — 마법사에서도 선택으로 표시된다.
        var installer = new ComponentInstaller(Settings);
        if (installer.MissingEngine().Count > 0)
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
    }
}
