using Velopack;

namespace NekoManga.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 설치/업데이트 훅 — 반드시 앱 시작 최우선으로 실행.
        // 바로가기는 패커의 --shortcuts Desktop,StartMenuRoot 설정에 따라 자동 생성된다.
        VelopackApp.Build().Run();

        if (args.Contains("--demo-splash"))
        {
            // 업데이트 적용 스플래시를 10초간 미리보기 (UI 작업용)
            UpdateService.ShowApplySplashDemo();
            Thread.Sleep(11000);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
