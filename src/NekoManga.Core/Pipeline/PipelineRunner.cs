using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

public record PageProgress(string File, string Stage, double Percent, bool Done = false, string? Error = null);

/// <summary>페이지 처리 파이프라인.
///
///   원고 → [번역: 내장 엔진이 글자 감지→OCR→번역→글자 지움]
///        → [검열제거: 검열 감지 → 기본(내장 LaMa 만화판) / 엔진 LaMa(ComfyUI)]
///        → [자동채색: ComfyUI SDXL]
///        → [식자: 번역문을 C#으로 렌더]
///        → 완성본
///
/// 번역·기본 검열제거는 전부 프로세스 안(ONNX)에서 돌고, 채색·엔진 LaMa만 ComfyUI가 필요하다.
/// (SDXL+LoRA 재생성('강력') 경로와 그 후임 DeepCreamPy 모드는 2026-10-19에 폐기 — CLAUDE.md 참조.)</summary>
public class PipelineRunner(AppSettings settings) : IDisposable
{
    private readonly OnnxEnv _onnx = new(settings);
    private string? _refImageName;   // IPAdapter용으로 업로드한 표지 (서버에 없으면 null)
    private readonly ComfyServer _server = new(settings);

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;

        var translator = settings.EnableTranslate ? new EmbeddedTranslator(settings, _onnx) : null;
        var lama = new LamaInpainter(_onnx);

        bool comfyLama = settings.EnableDecensor && settings.DecensorMode == "ComfyLama";
        bool needComfy = settings.EnableAutoColor || comfyLama;

        if (translator is { IsReady: false })
            throw new FileNotFoundException("번역 엔진 모델이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (settings.EnableDecensor && !comfyLama && !lama.IsReady)
            throw new FileNotFoundException("검열제거(기본) 모델(LaMa)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (needComfy && !settings.ComfyAvailable)
            throw new FileNotFoundException(
                (settings.EnableAutoColor ? "자동 채색" : "엔진 LaMa 검열제거") +
                " 기능은 ComfyUI가 필요해요 — 설정 → 구성요소에서 ComfyUI를 설치해 주세요.");

        ComfyClient? client = null;
        string comfyOutputDir = "";
        if (needComfy)
        {
            var status = new Progress<string>(m => Report(progress, files, m, 10));
            await _server.EnsureRunningAsync(status, ct);
            client = new ComfyClient(_server.BaseUrl);
            comfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");

            // 표지가 등록돼 있는데 색 태그를 아직 안 뽑았으면 여기서 한 번 뽑아 캐시한다
            if (settings.EnableAutoColor && settings.ColorRefTags.Trim().Length == 0
                && settings.ColorRefImage.Trim().Length > 0 && File.Exists(settings.ColorRefImage))
                try
                {
                    Report(progress, files, "표지에서 캐릭터 색 뽑는 중...", 12);
                    settings.ColorRefTags = await ColorRefHelper.ExtractAsync(settings, settings.ColorRefImage, null, ct);
                    settings.Save();
                }
                catch { /* 표지 분석 실패는 치명적이지 않다 — 참고 색 없이 진행 */ }

            // 표지 정밀 매칭(IPAdapter): 모델이 설치돼 있고 서버가 노드를 알고 있으면 표지를 올려 둔다
            if (settings.EnableAutoColor && settings.ColorRefImage.Trim().Length > 0
                && File.Exists(settings.ColorRefImage) && ColorRefHelper.IpAdapterAvailable(settings))
                try
                {
                    if (await client.HasNodeAsync("IPAdapter", ct))
                    {
                        var refBytes = await File.ReadAllBytesAsync(settings.ColorRefImage, ct);
                        _refImageName = await client.UploadImageAsync(refBytes, "nm_cover_ref.png", ct);
                    }
                    // 노드를 모르면 = 서버가 설치 전부터 떠 있던 것 — 이번 실행은 태그만으로 간다
                }
                catch { _refImageName = null; }
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

                    // 검열 추적: AI 모델(합성 학습 전용 모델)이 있으면 그게 1순위 —
                    // 흰/검/모자이크를 한 번에 찾는다. 없거나 못 찾으면 규칙 기반으로 폴백.
                    using var mask = DetectCensorMask(img, exclude, out var kindUsed);
                    if (settings.DecensorTarget == "Auto")
                        progress.Report(new(name, $"검열 분석: {kindUsed}", 46));
                    if (mask != null)
                    {
                        if (comfyLama)
                        {
                            progress.Report(new(name, "검열 지우는 중 (엔진 LaMa·GPU)", 50));
                            await ComfyLamaDecensorAsync(client!, comfyOutputDir, img, mask, i, ct);
                        }
                        else
                        {
                            progress.Report(new(name, "검열 지우는 중 (기본·LaMa)", 50));
                            await Task.Run(() => lama.Inpaint(img, mask), ct);
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
                        WorkflowBuilder.BuildAutoColor(settings, up, $"nm_color_{i}", _refImageName), ct);
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

    /// <summary>검열 마스크 — AI 전용 모델(CensorNet) 1순위, 규칙 기반 폴백.
    /// AI 모델은 무검열 원고에 검열을 합성해 학습한 세그멘테이션이라 흰/검/모자이크를
    /// 한 번에, 장식 줄무늬·효과음과 구분해 찾는다. 모델 파일이 없으면 기존 규칙으로.</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed)
    {
        var net = new CensorNet(_onnx);
        if (settings.DecensorTarget == "Auto" && net.IsReady)
        {
            // AI 모델이 있으면 AI가 유일한 판정자 — '없음'도 판정이다.
            // 규칙 기반으로 폴백하면 모델이 안 속는 장식 줄무늬 오탐이 도로 살아난다 (실측 22p).
            var aiMask = net.BuildMask(img, exclude, settings.DecensorMaskGrow, out kindUsed);
            if (aiMask == null) kindUsed += " → 검열 없음";
            return aiMask;
        }
        return MosaicDetector.BuildMaskForTarget(settings, img, exclude, out kindUsed);
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.
    /// mode: "Basic" | "ComfyLama" (옛 "Strong"·"DeepCream"은 2026-10-19 폐기 — 모르는 값은 ArgumentException).</summary>
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct)
    {
        switch (mode)
        {
            case "Basic":
            {
                var lama = new LamaInpainter(_onnx);
                if (!lama.IsReady)
                    throw new InvalidOperationException("복원 모델(LaMa)이 없어요 — 설정 → 구성요소에서 설치해 주세요.");
                status?.Report("복원 중...");
                using var m = mask.Clone(); // LaMa 내부 FindContours가 마스크를 파괴한다
                lama.Inpaint(img, m);
                return;
            }
            case "ComfyLama":
            {
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("엔진 LaMa로 복원 중...");
                // 페이지 번호는 파일 이름 충돌만 피하면 되므로 손수정 전용 번호를 쓴다
                await ComfyLamaDecensorAsync(client, outDir, img, mask, 900, ct);
                return;
            }
            default:
                throw new ArgumentException($"알 수 없는 복원 방식: {mode} (Basic | ComfyLama)", nameof(mode));
        }
    }

    /// <summary>엔진 LaMa 검열제거 — 사용자의 원본 ComfyUI 파이프라인(LayerUtility: LaMa, GPU,
    /// 확장 8·블러 8) 그대로. 전체 이미지를 통째로 보내므로 ROI 분할이 필요 없다.</summary>
    private async Task ComfyLamaDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
    {
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_lama_{pageIdx}.png", ct);
        var pid = await client.QueuePromptAsync(
            WorkflowBuilder.BuildLamaDecensor(settings, up, $"nm_lamaout_{pageIdx}"), ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(10), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("엔진 LaMa 결과 파일이 없어요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("엔진 LaMa 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height));
        // 마스크 영역만 받아들인다 — 나머지 픽셀은 원본 그대로 보존
        resized.CopyTo(img, mask);
    }

    public string OutputPathFor(string inputFile)
    {
        var dir = !string.IsNullOrWhiteSpace(settings.OutputRoot)
            ? settings.OutputRoot
            : Path.Combine(Path.GetDirectoryName(inputFile)!,
                Path.GetFileName(Path.GetDirectoryName(inputFile)!) + "_neko");
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(inputFile) + ".png");
    }

    private static void Report(IProgress<PageProgress> p, IReadOnlyList<string> files, string stage, double pct)
    {
        foreach (var f in files) p.Report(new(Path.GetFileName(f), stage, pct));
    }

    public void Dispose() => _onnx.Dispose();
}
