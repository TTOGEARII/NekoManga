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

        // 전체 검열제거 경로 테스트: --dctest <모드:Basic|ComfyLama|ComfyRegen|ComfyCustom> <이미지...>
        //   → <이미지>.dc_<모드>.png (AI 감지 + 선택한 복원까지 실제 파이프라인 경로로)
        var dcIdx = Array.IndexOf(args, "--dctest");
        if (dcIdx >= 0 && dcIdx + 2 < args.Length)
        {
            var mode = args[dcIdx + 1];
            var st = NekoManga.Core.AppSettings.Load();
            using var runner0 = new NekoManga.Core.Pipeline.PipelineRunner(st);
            foreach (var p in args.Skip(dcIdx + 2).Where(File.Exists))
            {
                using var im = NekoManga.Core.ImgIo.Read(p);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var mk = runner0.DetectCensorMask(im, null, out var kind);
                if (mk == null) { Console.WriteLine($"{Path.GetFileName(p)}: 검열 없음 ({kind})"); continue; }
                int px = OpenCvSharp.Cv2.CountNonZero(mk);
                // 최종 마스크(검출+보강+grow)를 빨강으로 덮어 저장 — 어떤 막대가 빠졌는지 눈으로 본다
                using (var ov = im.Clone())
                {
                    ov.SetTo(new OpenCvSharp.Scalar(0, 0, 255), mk);
                    NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".mask.png", ov);
                }
                var prog = new Progress<string>(m => Console.WriteLine("   " + m));
                try
                {
                    runner0.ManualDecensorAsync(im, mk, mode, prog, CancellationToken.None).GetAwaiter().GetResult();
                    NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + $".dc_{mode}.png", im);
                    Console.WriteLine($"{Path.GetFileName(p)}: {kind} {px}px, {mode} 복원 {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) { Console.WriteLine($"FAIL {Path.GetFileName(p)}: {ex.Message}"); }   // 옛 모드명(Strong 등)도 죽지 않고 FAIL
            }
            return;
        }

        // 마스크를 직접 주고 복원만: --dcmask <Basic|ComfyLama> <이미지> <마스크.png> [<이미지> <마스크.png> …]
        //   → <이미지>.dcm_<모드>.png — 회귀 하네스(D:\NekoManga-train\bench\dc_batch.py)용: 감지를 건너뛰고 합성 마스크로
        //   복원 경로만 검증한다 (--dctest는 감지를 거쳐 가장자리·타원 같은 합성 마스크를 못 쓴다).
        //   stdout: "RESULT <파일> mask=<px>px <모드> <ms>ms -> <출력>" 또는 "FAIL <파일>: <이유>" (exit code는 0)
        var dmIdx = Array.IndexOf(args, "--dcmask");
        if (dmIdx >= 0 && dmIdx + 3 < args.Length)
        {
            var mode = args[dmIdx + 1];
            var st = NekoManga.Core.AppSettings.Load();
            using var runner1 = new NekoManga.Core.Pipeline.PipelineRunner(st);
            var rest = args.Skip(dmIdx + 2).TakeWhile(a => !a.StartsWith("--")).ToList();
            for (int k = 0; k + 1 < rest.Count; k += 2)
            {
                var (p, mp) = (rest[k], rest[k + 1]);
                if (!File.Exists(p) || !File.Exists(mp)) { Console.WriteLine($"FAIL {Path.GetFileName(p)}: 파일 없음 ({mp})"); continue; }
                using var im = NekoManga.Core.ImgIo.Read(p);
                using var mk = NekoManga.Core.ImgIo.Read(mp, OpenCvSharp.ImreadModes.Grayscale);
                if (im.Empty() || mk.Empty() || mk.Size() != im.Size())
                { Console.WriteLine($"FAIL {Path.GetFileName(p)}: 디코드 실패 또는 마스크 크기 불일치"); continue; }
                OpenCvSharp.Cv2.Threshold(mk, mk, 127, 255, OpenCvSharp.ThresholdTypes.Binary);
                int px = OpenCvSharp.Cv2.CountNonZero(mk);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var prog = new Progress<string>(m => Console.WriteLine("   " + m));
                try
                {
                    runner1.ManualDecensorAsync(im, mk, mode, prog, CancellationToken.None).GetAwaiter().GetResult();
                    var outP = Path.ChangeExtension(p, null) + $".dcm_{mode}.png";
                    NekoManga.Core.ImgIo.Write(outP, im);
                    Console.WriteLine($"RESULT {Path.GetFileName(p)} mask={px}px {mode} {sw.ElapsedMilliseconds}ms -> {outP}");
                }
                catch (Exception ex) { Console.WriteLine($"FAIL {Path.GetFileName(p)}: {ex.Message}"); }
            }
            return;
        }

        // (--loracheck는 2026-10-19 폐기 — SDXL+LoRA 재생성 경로 삭제. git 이력 c4adc65 참조.)

        // AI 검열 모델 단독 테스트: --censortest <이미지...> → <이미지>.censor.png(오버레이) + censortest.log
        var ctIdx = Array.IndexOf(args, "--censortest");
        if (ctIdx >= 0)
        {
            var settingsC = NekoManga.Core.AppSettings.Load();
            using var envC = new NekoManga.Core.Engine.OnnxEnv(settingsC);
            var netC = new NekoManga.Core.Engine.CensorNet(envC);
            // 선택: --conf <값> 으로 검출 임계를 바꿔 잠재 신호를 본다 (기본 0.45)
            float confC = 0.45f;
            var cfI = Array.IndexOf(args, "--conf");
            if (cfI >= 0 && cfI + 1 < args.Length) float.TryParse(args[cfI + 1], System.Globalization.CultureInfo.InvariantCulture, out confC);
            foreach (var p in args.Skip(ctIdx + 1).Where(File.Exists))
            {
                using var imC = NekoManga.Core.ImgIo.Read(p);
                var swC = System.Diagnostics.Stopwatch.StartNew();
                var (wM, bM, mM, dets) = netC.Detect(imC, confC);
                using (wM) using (bM) using (mM)
                {
                    imC.SetTo(new OpenCvSharp.Scalar(0, 0, 255), wM);
                    imC.SetTo(new OpenCvSharp.Scalar(255, 0, 0), bM);
                    imC.SetTo(new OpenCvSharp.Scalar(0, 200, 0), mM);
                    var detLines = dets.OrderByDescending(d => d.Conf).Take(12)
                        .Select(d => $"    cls{d.Cls} conf{d.Conf:F2} {d.Box.Width}x{d.Box.Height}@({d.Box.X},{d.Box.Y})");
                    File.AppendAllLines(Path.Combine(Path.GetDirectoryName(p)!, "censortest.log"),
                        new[] { $"{Path.GetFileName(p)} [conf≥{confC:F2}]: 흰 {dets.Count(d => d.Cls == 0)} 검 {dets.Count(d => d.Cls == 1)} 모자이크 {dets.Count(d => d.Cls == 2)} ({swC.ElapsedMilliseconds}ms)" }.Concat(detLines));
                }
                NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".censor.png", imC);
            }
            return;
        }

        // 검열 마스크만 빠르게 (복원 없음): --masktest <이미지...> → <이미지>.mask.png + masktest.log
        var mtIdx = Array.IndexOf(args, "--masktest");
        if (mtIdx >= 0)
        {
            var stM = NekoManga.Core.AppSettings.Load();
            using var runnerM = new NekoManga.Core.Pipeline.PipelineRunner(stM);
            foreach (var p in args.Skip(mtIdx + 1).Where(File.Exists))
            {
                using var im = NekoManga.Core.ImgIo.Read(p);
                var swM = System.Diagnostics.Stopwatch.StartNew();
                // NEKO_MASKTEST_CTD=1 이면 파이프라인처럼 글자 감지 블록으로 말풍선 필터까지 돌린다 (기본은 꺼짐 — 기존 측정치와 비교 가능하게)
                using var mk = runnerM.DetectCensorMask(im, null, out var kind,
                    autoBubbles: Environment.GetEnvironmentVariable("NEKO_MASKTEST_CTD") == "1");
                int px = mk == null ? 0 : OpenCvSharp.Cv2.CountNonZero(mk);
                File.AppendAllLines(Path.Combine(Path.GetDirectoryName(p)!, "masktest.log"),
                    [$"{Path.GetFileName(p)}: {px}px | {kind} ({swM.ElapsedMilliseconds}ms)"]);
                if (mk != null)
                {
                    // 이진 마스크(정답 대조용) + 사람이 보는 빨강 오버레이, 둘 다 남긴다
                    NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".maskbin.png", mk);
                    im.SetTo(new OpenCvSharp.Scalar(0, 0, 255), mk);
                    NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".mask.png", im);
                }
            }
            return;
        }

        // 색(채도) 검열 검출 테스트: --colortest <이미지...> → <이미지>.colormask.png(오버레이) + colortest.log
        var colIdx = Array.IndexOf(args, "--colortest");
        if (colIdx >= 0)
        {
            foreach (var p in args.Skip(colIdx + 1).Where(File.Exists))
            {
                using var im1 = NekoManga.Core.ImgIo.Read(p);
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                using var mk = NekoManga.Core.Decensor.ColorCensorDetector.BuildMask(
                    im1, null, grow: 0, out var satFrac, out var comps);
                int px1 = mk == null ? 0 : OpenCvSharp.Cv2.CountNonZero(mk);
                File.AppendAllLines(Path.Combine(Path.GetDirectoryName(p)!, "colortest.log"),
                    [$"{Path.GetFileName(p)}: {px1}px comps={comps} satFrac={satFrac:F4} ({sw1.ElapsedMilliseconds}ms)"]);
                if (mk != null)
                {
                    im1.SetTo(new OpenCvSharp.Scalar(0, 0, 255), mk);
                    NekoManga.Core.ImgIo.Write(Path.ChangeExtension(p, null) + ".colormask.png", im1);
                }
            }
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

        // 설치·업데이트 스플래시 프레임 재생성: NekoManga.exe --gen-splash <출력폴더> → tools/make-splash-gif.py로 GIF 조립
        var splashIdx = Array.IndexOf(args, "--gen-splash");
        if (splashIdx >= 0 && splashIdx + 1 < args.Length)
        {
            SplashArt.WriteFrames(args[splashIdx + 1]);
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
                ["regen_decensor"] = NekoManga.Core.Comfy.WorkflowBuilder.BuildRegenDecensor(s, "IN.png", "OUT"),
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
        //   --review <이미지…>     검열 감지만 돌리고 검토 창(MaskReviewWindow)을 띄운다 (검토 흐름 UI 확인용, 제거는 안 함)
        var resultIdx = Array.IndexOf(args, "--result");
        var filesIdx = Array.IndexOf(args, "--demo-files");
        var reviewIdx = Array.IndexOf(args, "--review");
        var app = new App
        {
            ReviewImages = reviewIdx >= 0 ? args.Skip(reviewIdx + 1).TakeWhile(a => !a.StartsWith("--")).ToList() : null,
            DemoSettings = args.Contains("--demo-settings"),
            DemoSetup = args.Contains("--demo-setup"),
            ResultImages = resultIdx >= 0 ? args.Skip(resultIdx + 1).TakeWhile(a => !a.StartsWith("--")).ToList() : null,
            PreloadDir = filesIdx >= 0 && filesIdx + 1 < args.Length ? args[filesIdx + 1] : null,
        };
        app.InitializeComponent();
        app.Run();
    }
}
