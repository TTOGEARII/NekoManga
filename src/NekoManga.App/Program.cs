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

        // 내장 엔진 자가진단: 감지→OCR→번역→글자지움을 창 없이 돌려 결과 파일을 남긴다.
        // 사용: NekoManga.exe --selftest <이미지경로>  (결과: <이미지>.selftest.* + selftest.log)
        var selftestIdx = Array.IndexOf(args, "--selftest");
        if (selftestIdx >= 0 && selftestIdx + 1 < args.Length)
        {
            SelfTest.Run(args[selftestIdx + 1]);
            return;
        }

        // 결과 보기 창만 바로 띄운다 (UI 확인용): NekoManga.exe --result <이미지경로>
        var resultIdx = Array.IndexOf(args, "--result");
        var app = new App
        {
            DemoSettings = args.Contains("--demo-settings"),
            ResultImage = resultIdx >= 0 && resultIdx + 1 < args.Length ? args[resultIdx + 1] : null,
        };
        app.InitializeComponent();
        app.Run();
    }
}
