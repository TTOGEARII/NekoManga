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
                    using var img = Cv2.ImRead(work, ImreadModes.Color);

                    // 말풍선은 흰 막대로 오인되기 쉬우므로 감지된 글자 블록을 제외 영역으로 넘긴다.
                    var exclude = blocks.Select(b =>
                        new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();

                    var mosaicMask = settings.DecensorFindMosaic
                        ? MosaicDetector.BuildMask(img,
                            settings.DecensorBlockSize, settings.DecensorThreshold, settings.DecensorMaskGrow)
                        : null;
                    var barMask = MosaicDetector.BuildBarMask(img,
                        settings.DecensorFindWhiteBar, settings.DecensorFindBlackBar,
                        settings.DecensorBarMinAspect, settings.DecensorBarMinArea,
                        settings.DecensorMaskGrow, exclude);

                    using var mask = MosaicDetector.Combine(mosaicMask, barMask);
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
                            await StrongDecensorAsync(client!, comfyOutputDir, img, mask, i, ct);
                        }
                        work = Path.Combine(workDir, Path.GetFileNameWithoutExtension(file) + ".decensored.png");
                        Directory.CreateDirectory(workDir);
                        Cv2.ImWrite(work, img);
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
                    TextRenderer.RenderBlocks(work, file, blocks, outPath);
                }
                else
                {
                    // 결과는 항상 png로 저장한다 — 원본이 webp/avif여도 확장자만 png가 되지 않도록
                    // 그대로 복사하지 않고 OpenCV로 다시 인코딩한다.
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var done = Cv2.ImRead(work, ImreadModes.Color);
                    if (done.Empty())
                        throw new InvalidDataException($"결과 이미지를 읽지 못했어요: {Path.GetFileName(work)}");
                    Cv2.ImWrite(outPath, done);
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

    /// <summary>강력 검열제거: 검열 ROI를 잘라 SDXL 인페인트(ComfyUI)로 재생성하고 제자리에 붙인다.
    /// 전체 이미지를 통째로 샘플링하면 8GB VRAM이 터지므로 ROI 단위(최대 1024px)로 처리.</summary>
    private async Task StrongDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
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

            var up = await client.UploadImageAsync(maskedPng, $"nm_sdc_{pageIdx}_{ri}.png", ct);
            var pid = await client.QueuePromptAsync(
                WorkflowBuilder.BuildStrongDecensor(settings, up, $"nm_strong_{pageIdx}_{ri}"), ct);
            var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(10), ct);
            if (r.OutputFiles.Count > 0)
            {
                using var outImg = Cv2.ImRead(r.OutputFiles[^1], ImreadModes.Color);
                if (!outImg.Empty())
                {
                    using var resized = outImg.Width == roi.Width && outImg.Height == roi.Height
                        ? outImg.Clone()
                        : outImg.Resize(new OpenCvSharp.Size(roi.Width, roi.Height));
                    resized.CopyTo(crop, mcrop);
                }
            }
            ri++;
        }
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
