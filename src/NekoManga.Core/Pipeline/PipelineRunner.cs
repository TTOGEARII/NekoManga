using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

public record PageProgress(string File, string Stage, double Percent, bool Done = false, string? Error = null);

/// <summary>페이지 처리 파이프라인.
///
///   원고 → [번역: 내장 엔진이 글자 감지→OCR→번역→글자 지움]
///        → [검열제거: 모자이크 감지 → 기본(내장 LaMa) / 강력(ComfyUI SDXL 인페인트)]
///        → [자동채색: ComfyUI SDXL]
///        → [식자: 번역문을 C#으로 렌더]
///        → 완성본
///
/// 번역·기본 검열제거는 전부 프로세스 안(ONNX)에서 돌고, 채색·강력 검열제거만 ComfyUI가 필요하다.</summary>
public class PipelineRunner(AppSettings settings) : IDisposable
{
    private readonly OnnxEnv _onnx = new(settings);
    private readonly ComfyServer _server = new(settings);

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;

        var translator = settings.EnableTranslate ? new EmbeddedTranslator(settings, _onnx) : null;
        var lama = new LamaInpainter(_onnx);

        bool strongDecensor = settings.EnableDecensor && settings.DecensorMode == "Strong";
        bool needComfy = settings.EnableAutoColor || strongDecensor;

        if (translator is { IsReady: false })
            throw new FileNotFoundException("번역 엔진 모델이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (settings.EnableDecensor && !strongDecensor && !lama.IsReady)
            throw new FileNotFoundException("검열제거(기본) 모델(LaMa)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (needComfy && !settings.ComfyAvailable)
            throw new FileNotFoundException(
                (settings.EnableAutoColor ? "자동 채색" : "강력 검열제거") +
                " 기능은 ComfyUI가 필요해요 — 설정 → 구성요소에서 ComfyUI를 설치해 주세요.");

        ComfyClient? client = null;
        string comfyOutputDir = "";
        if (needComfy)
        {
            var status = new Progress<string>(m => Report(progress, files, m, 10));
            await _server.EnsureRunningAsync(status, ct);
            client = new ComfyClient(_server.BaseUrl);
            comfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
        }

        var workDir = Path.Combine(AppSettings.DataDir, "work",
            $"job-{DateTime.Now:yyyyMMdd-HHmmss}");

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var name = Path.GetFileName(file);
            try
            {
                var work = file;
                IReadOnlyList<TextBlockInfo> blocks = Array.Empty<TextBlockInfo>();

                // ── 번역 (내장): 글자 지운 원고 + 번역 블록 ──
                if (translator != null)
                {
                    var status = new Progress<string>(m => progress.Report(new(name, m, 25)));
                    var tr = await translator.TranslatePageAsync(work, workDir, status, ct);
                    work = tr.InpaintedPath;
                    blocks = tr.Blocks;
                }

                // ── 검열제거 ──
                if (settings.EnableDecensor)
                {
                    progress.Report(new(name, "검열 영역 찾는 중", 45));
                    using var img = ImgIo.Read(work, ImreadModes.Color);

                    // 말풍선은 흰 막대로 오인되기 쉬우므로 감지된 글자 블록을 제외 영역으로 넘긴다.
                    var exclude = blocks.Select(b =>
                        new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();

                    // 검열 추적은 한 번에 한 종류만 — 여러 경로를 섞으면 서로의 오탐이 결과를 오염시킨다.
                    using var mask = settings.DecensorTarget == "Mosaic"
                        ? MosaicDetector.BuildMask(img,
                            settings.DecensorBlockSize, settings.DecensorThreshold, settings.DecensorMaskGrow)
                        : MosaicDetector.BuildBarMask(img,
                            white: settings.DecensorTarget == "WhiteBar",
                            black: settings.DecensorTarget == "BlackBar",
                            settings.DecensorBarMinAspect, settings.DecensorBarMinArea,
                            settings.DecensorMaskGrow, exclude);
                    if (mask != null)
                    {
                        if (!strongDecensor)
                        {
                            progress.Report(new(name, "검열 지우는 중 (기본·LaMa)", 50));
                            await Task.Run(() => lama.Inpaint(img, mask), ct);
                        }
                        else
                        {
                            progress.Report(new(name, "검열 재생성 중 (강력·SDXL)", 50));
                            await StrongDecensorAsync(client!, comfyOutputDir, img, mask, i, lama, ct);
                        }
                        work = Path.Combine(workDir, Path.GetFileNameWithoutExtension(file) + ".decensored.png");
                        Directory.CreateDirectory(workDir);
                        ImgIo.Write(work, img);
                    }
                }

                // ── 자동채색 (ComfyUI) ──
                if (settings.EnableAutoColor && client != null)
                {
                    progress.Report(new(name, "채색 중 (페이지당 1~2분)", 60));
                    var png = await File.ReadAllBytesAsync(work, ct);
                    var up = await client.UploadImageAsync(png, $"nm_cl_{i}.png", ct);
                    var pid = await client.QueuePromptAsync(
                        WorkflowBuilder.BuildAutoColor(settings, up, $"nm_color_{i}"), ct);
                    var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
                    if (r.OutputFiles.Count == 0)
                        throw new InvalidOperationException("채색 결과 파일이 없어요.");
                    work = r.OutputFiles[^1];
                }

                // ── 식자(번역문 렌더) 및 최종 저장 ──
                var outPath = OutputPathFor(file);
                if (blocks.Count > 0)
                {
                    progress.Report(new(name, "식자 중", 92));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                    // 식자 전 원고와 블록을 남겨 둔다 — 결과 보기 창의 식자 탭이
                    // 글꼴·색·번역문을 바꿔 다시 식자할 때 쓴다 (없으면 번역부터 다시 돌려야 한다).
                    using (var baseImg = ImgIo.Read(work))
                    using (var srcImg = ImgIo.Read(file))
                        if (!baseImg.Empty() && !srcImg.Empty())
                        {
                            ImgIo.Write(LetterSidecar.BasePathFor(outPath), baseImg);
                            new LetterSidecar(file, srcImg.Width, srcImg.Height,
                                LetterSidecar.BasePathFor(outPath), [.. blocks]).Save(outPath);
                        }

                    TextRenderer.RenderBlocks(work, file, blocks, outPath, settings);
                }
                else if (work == file)
                {
                    // 아무것도 감지·수정되지 않았다 (work가 원본 그대로) — 다시 인코딩하지 않고
                    // 원본을 확장자까지 그대로 내보낸다. 손댈 게 없는데 jpg를 png로 재인코딩하면
                    // 파일만 커지고 화질에도 득이 없다.
                    var asIs = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileName(file));
                    Directory.CreateDirectory(Path.GetDirectoryName(asIs)!);
                    if (!string.Equals(asIs, file, StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, asIs, overwrite: true);
                    progress.Report(new(name, "검열 없음 — 원본 그대로", 100, Done: true));
                    continue;
                }
                else
                {
                    // 결과는 항상 png로 저장한다 — 원본이 webp/avif여도 확장자만 png가 되지 않도록
                    // 그대로 복사하지 않고 OpenCV로 다시 인코딩한다.
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var done = ImgIo.Read(work, ImreadModes.Color);
                    if (done.Empty())
                        throw new InvalidDataException($"결과 이미지를 읽지 못했어요: {Path.GetFileName(work)}");
                    ImgIo.Write(outPath, done);
                }

                progress.Report(new(name, "완료", 100, Done: true));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report(new(name, "실패", 100, Done: true, Error: ex.Message));
            }
        }

        await _server.DisposeAsync();
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.</summary>
    public async Task ManualDecensorAsync(Mat img, Mat mask, bool strong,
        IProgress<string>? status, CancellationToken ct)
    {
        var lama = new LamaInpainter(_onnx);

        if (!strong)
        {
            if (!lama.IsReady)
                throw new InvalidOperationException("복원 모델(LaMa)이 없어요 — 설정 → 구성요소에서 설치해 주세요.");
            status?.Report("복원 중...");
            using var m = mask.Clone(); // LaMa 내부 FindContours가 마스크를 파괴한다
            lama.Inpaint(img, m);
            return;
        }

        await _server.EnsureRunningAsync(status, ct);
        var client = new ComfyClient(_server.BaseUrl);
        var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
        status?.Report("SDXL로 다시 그리는 중... (수십 초)");
        // 페이지 번호는 파일 이름 충돌만 피하면 되므로 손수정 전용 번호를 쓴다
        await StrongDecensorAsync(client, outDir, img, mask, 900, lama, ct);
    }

    /// <summary>강력 검열제거: 검열 ROI를 잘라 SDXL 인페인트(ComfyUI)로 재생성하고 제자리에 붙인다.
    /// 전체 이미지를 통째로 샘플링하면 8GB VRAM이 터지므로 ROI 단위(최대 1024px)로 처리.</summary>
    private async Task StrongDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, LamaInpainter lama, CancellationToken ct)
    {
        using var forContours = mask.Clone(); // FindContours가 입력을 파괴하므로 복제본에서 찾는다
        Cv2.FindContours(forContours, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return;
        var rois = LamaInpainter.MergeRois(contours.Select(Cv2.BoundingRect).ToList(), img.Width, img.Height);

        int ri = 0;
        foreach (var roi0 in rois)
        {
            ct.ThrowIfCancellationRequested();
            // SDXL이 다루기 좋은 크기로 (너무 작으면 주변 맥락 부족 → 최소 512, 최대 1024)
            var roi = EnsureSize(roi0, 512, 1024, img.Width, img.Height);
            using var crop = new Mat(img, roi);
            using var mcrop = new Mat(mask, roi);
            var maskedPng = MosaicDetector.ToMaskedPng(crop, mcrop);

            // 시드를 고정해야 같은 원고를 다시 돌렸을 때 같은 그림이 나온다. ROI마다 다른 시드를 쓰되
            // (영역마다 같은 노이즈면 비슷한 무늬가 반복된다) 순서가 정해져 있어 결과는 재현된다.
            long seed = settings.DecensorSeed >= 0
                ? (settings.DecensorSeed + ri) % uint.MaxValue
                : Random.Shared.NextInt64(0, uint.MaxValue);

            var up = await client.UploadImageAsync(maskedPng, $"nm_sdc_{pageIdx}_{ri}.png", ct);
            var pid = await client.QueuePromptAsync(
                WorkflowBuilder.BuildStrongDecensor(settings, up, $"nm_strong_{pageIdx}_{ri}", seed), ct);
            var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(10), ct);
            if (r.OutputFiles.Count > 0)
            {
                using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
                if (!outImg.Empty())
                {
                    using var resized = outImg.Width == roi.Width && outImg.Height == roi.Height
                        ? outImg.Clone()
                        : outImg.Resize(new OpenCvSharp.Size(roi.Width, roi.Height));

                    // SDXL은 시드에 따라 지운 자리를 새카맣게 칠해 놓기도 한다 — 같은 원고를 두 번 돌렸을 때
                    // 한 번은 멀쩡했고 한 번은 검은 타원이 생겼다. 검열을 지운 자리가 주변보다 극단적으로
                    // 어두우면 실패로 보고 LaMa로 대신 메운다 (살짝 어두운 건 정상 — 음영일 수 있다).
                    using var genGray = new Mat();
                    using var srcGray = new Mat();
                    Cv2.CvtColor(resized, genGray, ColorConversionCodes.BGR2GRAY);
                    Cv2.CvtColor(crop, srcGray, ColorConversionCodes.BGR2GRAY);
                    using var notMask = new Mat();
                    Cv2.BitwiseNot(mcrop, notMask);
                    double generated = Cv2.Mean(genGray, mcrop).Val0;
                    double around = Cv2.Mean(srcGray, notMask).Val0;

                    if (generated < around - 90 && lama.IsReady)
                    {
                        using var patched = crop.Clone();
                        using var mclone = mcrop.Clone(); // LaMa 내부 FindContours가 마스크를 파괴한다
                        lama.Inpaint(patched, mclone);
                        patched.CopyTo(crop, mcrop);
                    }
                    else FeatherBlend(crop, resized, mcrop);
                }
            }
            ri++;
        }
    }

    /// <summary>새로 그린 그림을 마스크 경계에서 부드럽게 섞어 넣는다.
    /// 하드 마스크로 그대로 붙이면 경계선이 또렷하게 드러나 '덧붙인 티'가 난다 — 특히 SDXL 결과는
    /// 주변과 밝기·질감이 미묘하게 달라서 네모난 자국으로 보인다. 마스크를 흐려 알파 블렌딩하면
    /// 경계가 사라진다. 마스크 밖은 알파 0이라 원본 픽셀이 그대로 남는다.</summary>
    private static void FeatherBlend(Mat dst, Mat generated, Mat mask)
    {
        using var soft = new Mat();
        Cv2.GaussianBlur(mask, soft, new OpenCvSharp.Size(9, 9), 0);

        using var alpha1 = new Mat();
        soft.ConvertTo(alpha1, MatType.CV_32FC1, 1.0 / 255);
        using var alpha = new Mat();
        Cv2.CvtColor(alpha1, alpha, ColorConversionCodes.GRAY2BGR);

        using var one = new Mat(alpha.Size(), MatType.CV_32FC3, Scalar.All(1));
        using var inv = new Mat();
        Cv2.Subtract(one, alpha, inv);

        using var fDst = new Mat();
        using var fGen = new Mat();
        dst.ConvertTo(fDst, MatType.CV_32FC3);
        generated.ConvertTo(fGen, MatType.CV_32FC3);

        using var keep = new Mat();
        using var add = new Mat();
        Cv2.Multiply(fDst, inv, keep);
        Cv2.Multiply(fGen, alpha, add);
        using var mixed = new Mat();
        Cv2.Add(keep, add, mixed);

        using var result = new Mat();
        mixed.ConvertTo(result, MatType.CV_8UC3);
        result.CopyTo(dst);
    }

    public string OutputPathFor(string inputFile)
    {
        var dir = !string.IsNullOrWhiteSpace(settings.OutputRoot)
            ? settings.OutputRoot
            : Path.Combine(Path.GetDirectoryName(inputFile)!,
                Path.GetFileName(Path.GetDirectoryName(inputFile)!) + "_neko");
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(inputFile) + ".png");
    }

    private static Rect EnsureSize(Rect r, int min, int max, int imW, int imH)
    {
        int w = Math.Clamp(Math.Max(r.Width, min), min, Math.Min(max, imW));
        int h = Math.Clamp(Math.Max(r.Height, min), min, Math.Min(max, imH));
        int x = Math.Clamp(r.X + r.Width / 2 - w / 2, 0, Math.Max(0, imW - w));
        int y = Math.Clamp(r.Y + r.Height / 2 - h / 2, 0, Math.Max(0, imH - h));
        return new Rect(x, y, w, h);
    }

    private static void Report(IProgress<PageProgress> p, IReadOnlyList<string> files, string stage, double pct)
    {
        foreach (var f in files) p.Report(new(Path.GetFileName(f), stage, pct));
    }

    public void Dispose() => _onnx.Dispose();
}
