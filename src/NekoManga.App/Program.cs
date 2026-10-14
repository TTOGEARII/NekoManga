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

        // 흰 막대 마스크 단독 테스트: --bartest <이미지...> → <이미지>.barmask.png + bartest.log
        var btIdx = Array.IndexOf(args, "--bartest");
        if (btIdx >= 0)
        {
            foreach (var p in args.Skip(btIdx + 1).Where(File.Exists))
            {
                using var im1 = NekoManga.Core.ImgIo.Read(p);
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                using var mk = NekoManga.Core.Decensor.MosaicDetector.BuildBarMask(
                    im1, white: true, black: false, minAspect: 1.8, minAreaPx: 350, grow: 0);
                int px1 = mk == null ? 0 : OpenCvSharp.Cv2.CountNonZero(mk);
                File.AppendAllLines(Path.Combine(Path.GetDirectoryName(p)!, "bartest.log"),
                    [$"{Path.GetFileName(p)}: {px1}px ({sw1.ElapsedMilliseconds}ms)"]);
                if (mk != null) NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".barmask.png", mk);
            }
            return;
        }

        // 기본 LaMa 수동 복원 테스트: --lamatest <이미지> <x> <y> <w> <h> → <이미지>.lama.png
        var ltIdx = Array.IndexOf(args, "--lamatest");
        if (ltIdx >= 0 && ltIdx + 5 < args.Length)
        {
            var settings0 = NekoManga.Core.AppSettings.Load();
            using var env0 = new NekoManga.Core.Engine.OnnxEnv(settings0);
            var lama = new NekoManga.Core.Engine.LamaInpainter(env0);
            using var im0 = NekoManga.Core.ImgIo.Read(args[ltIdx + 1]);
            using var m0 = new OpenCvSharp.Mat(im0.Rows, im0.Cols, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.Black);
            var r0 = new OpenCvSharp.Rect(int.Parse(args[ltIdx + 2]), int.Parse(args[ltIdx + 3]),
                int.Parse(args[ltIdx + 4]), int.Parse(args[ltIdx + 5]));
            OpenCvSharp.Cv2.Rectangle(m0, r0, OpenCvSharp.Scalar.White, -1);
            OpenCvSharp.Cv2.Dilate(m0, m0, OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Ellipse, new OpenCvSharp.Size(13, 13)));
            lama.Inpaint(im0, m0);
            NekoManga.Core.ImgIo.Write(Path.ChangeExtension(args[ltIdx + 1], null) + ".lama.png", im0);
            return;
        }

        // 흰 스트립 검출 단독 테스트: NekoManga.exe --striptest <이미지...> → <이미지>.strips.png + 콘솔
        var stIdx = Array.IndexOf(args, "--striptest");
        if (stIdx >= 0)
        {
            foreach (var p in args.Skip(stIdx + 1).Where(File.Exists))
            {
                using var img = NekoManga.Core.ImgIo.Read(p);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var strips = NekoManga.Core.Decensor.WhiteStripDetector.Detect(img);
                var lines = new List<string> { $"{Path.GetFileName(p)}: strips={strips.Count} ({sw.ElapsedMilliseconds}ms)" };
                foreach (var st in strips)
                {
                    lines.Add($"  c=({st.Center.X:F0},{st.Center.Y:F0}) {st.Size.Width:F0}x{st.Size.Height:F0} ang={st.AngleDeg:F1}");
                    var rr = new OpenCvSharp.RotatedRect(st.Center, st.Size, st.AngleDeg);
                    OpenCvSharp.Cv2.Polylines(img, new[] { rr.Points().Select(q => new OpenCvSharp.Point((int)q.X, (int)q.Y)) }, true, OpenCvSharp.Scalar.Red, 8);
                }
                NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".strips.png", img);
                File.AppendAllLines(Path.Combine(Path.GetDirectoryName(p)!, "striptest.log"), lines);
            }
            return;
        }

        // 로고 아이콘 재생성 (디자인 변경 시): NekoManga.exe --render-logo <출력.ico>
        var logoIdx = Array.IndexOf(args, "--render-logo");
        if (logoIdx >= 0 && logoIdx + 1 < args.Length)
        {
            LogoArt.WriteIco(args[logoIdx + 1]);
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
                ["lama_decensor"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildLamaDecensor(s, "IN.png", "OUT"),
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
