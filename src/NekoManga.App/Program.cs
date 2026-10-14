using System.IO;
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
        // 전체 파이프라인 headless 실행 (E2E 테스트용): NekoManga.exe --run <이미지…>
        // 현재 settings.json 그대로 돌리고, 단계별 진행을 <첫이미지>.runlog.txt에 남긴다.
        var runIdx = Array.IndexOf(args, "--run");
        if (runIdx >= 0 && runIdx + 1 < args.Length)
        {
            var files = args.Skip(runIdx + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
            var s = NekoManga.Core.AppSettings.Load();
            var log = new StreamWriter(files[0] + ".runlog.txt", false, System.Text.Encoding.UTF8) { AutoFlush = true };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<NekoManga.Core.Pipeline.PageProgress>(p =>
                log.WriteLine($"[{sw.Elapsed:mm\\:ss}] {p.File} | {p.Stage}" + (p.Error != null ? $" | ERR: {p.Error}" : "")));
            try
            {
                using var runner = new NekoManga.Core.Pipeline.PipelineRunner(s);
                runner.RunAsync(files, progress, CancellationToken.None).GetAwaiter().GetResult();
                log.WriteLine($"[{sw.Elapsed:mm\\:ss}] RUN OK");
            }
            catch (Exception ex) { log.WriteLine($"[{sw.Elapsed:mm\\:ss}] RUN FAIL: {ex}"); }
            log.Dispose();
            return;
        }

        // 워크플로 덤프 (원본 ComfyUI 셋팅과 대조 검증용): NekoManga.exe --dumpwf <출력.json>
        var dumpIdx = Array.IndexOf(args, "--dumpwf");
        if (dumpIdx >= 0 && dumpIdx + 1 < args.Length)
        {
            var s = NekoManga.Core.AppSettings.Load();
            var all = new System.Text.Json.Nodes.JsonObject
            {
                ["autocolor"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildAutoColor(s, "IN.png", "OUT"),
                ["autocolor_with_ref"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildAutoColor(s, "IN.png", "OUT", "REF.png"),
                ["lama_decensor"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildLamaDecensor("IN.png", "OUT"),
                ["strong_decensor"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildStrongDecensor(s, "IN.png", "OUT", 1234),
                ["tag_extract"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildTagExtract(s, "IN.png"),
            };
            File.WriteAllText(args[dumpIdx + 1], all.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        // 표지 색 태그 추출 확인용: NekoManga.exe --colorref <이미지> → <이미지>.colorref.txt
        var colorrefIdx = Array.IndexOf(args, "--colorref");
        if (colorrefIdx >= 0 && colorrefIdx + 1 < args.Length)
        {
            var img = args[colorrefIdx + 1];
            var s = NekoManga.Core.AppSettings.Load();
            var tags = NekoManga.Core.Comfy.ColorRefHelper.ExtractAsync(s, img).GetAwaiter().GetResult();
            File.WriteAllText(img + ".colorref.txt", tags);
            return;
        }

        var selftestIdx = Array.IndexOf(args, "--selftest");
        if (selftestIdx >= 0 && selftestIdx + 1 < args.Length)
        {
            SelfTest.Run(args[selftestIdx + 1]);
            return;
        }

        // UI 확인용 진입점
        //   --result <이미지…>     결과 보기 창만 띄운다 (여러 장을 주면 창 안에서 넘겨볼 수 있다)
        //   --demo-files <폴더>    메인 창 목록에 그 폴더의 이미지를 미리 올려 둔다
        var resultIdx = Array.IndexOf(args, "--result");
        var filesIdx = Array.IndexOf(args, "--demo-files");
        var app = new App
        {
            DemoSettings = args.Contains("--demo-settings"),
            DemoSetup = args.Contains("--demo-setup"),
            ResultImages = resultIdx >= 0 ? args.Skip(resultIdx + 1).TakeWhile(a => !a.StartsWith("--")).ToList() : null,
            PreloadDir = filesIdx >= 0 && filesIdx + 1 < args.Length ? args[filesIdx + 1] : null,
        };
        app.InitializeComponent();
        app.Run();
    }
}
