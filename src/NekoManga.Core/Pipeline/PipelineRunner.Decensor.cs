using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 검열 감지와 복원 — 마스크 감지, 결과 창의 수동 복원, ComfyUI 엔진 복원(커스텀 워크플로·재생성·엔진 LaMa)
public partial class PipelineRunner
{
    /// <summary>검열 마스크만 필요할 때 (진단·검토 창 미리보기) — 종류 지도는 버린다.</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, bool autoBubbles = false)
    {
        var mask = DetectCensorMask(img, exclude, out kindUsed, out var kinds, autoBubbles);
        kinds?.Dispose();
        return mask;
    }

    /// <summary>검열 마스크 + 종류 지도. 기본 엔진은 Aletheia Lens(막대·모자이크)에 기존 방식의 흰선 감지를 더한 것(AletheiaDecensor.Detect),
    /// DecensorEngine="Legacy"면 교체 전 경로 — AI 전용 모델(CensorNet) 1순위, 규칙 기반 폴백(종류 지도 없음).</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, out Mat? kinds, bool autoBubbles = false) =>
        DetectCensor(img, exclude, out kindUsed, out kinds, autoBubbles, UseAletheia);

    private Mat? DetectCensor(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, out Mat? kinds, bool autoBubbles, bool aletheia)
    {
        kinds = null;
        // 말풍선 오탐(실측 Confusion 12·21쪽: 흰 말풍선을 흰 검열로 봄)은 BuildMask의 텍스트 블록 필터가 걸러 주는데, 번역을 끄면
        // 블록이 비어 필터가 아예 안 돌았다 → autoBubbles면 글자 감지기(CTD, ~0.5초)만 따로 돌려 블록을 만든다 (2026-10-20).
        if (autoBubbles && (exclude == null || exclude.Count == 0))
        {
            var ctd = new Engine.TextDetector(_onnx);
            if (ctd.IsReady)
            {
                try
                {
                    var textDet = ctd.Detect(img);
                    exclude = textDet.Blocks.Select(b => new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();
                    textDet.TextMask.Dispose();
                }
                catch { /* 글자 감지 실패는 치명적이지 않다 — 필터 없이 진행 */ }
            }
        }
        if (aletheia) return Aletheia.Detect(img, exclude, out kindUsed, out kinds);

        var net = new CensorNet(_onnx);
        if (settings.DecensorTarget == "Auto" && net.IsReady)
        {
            // AI 모델이 있으면 AI가 유일한 판정자 — '없음'도 판정이다.
            // 규칙 기반으로 폴백하면 모델이 안 속는 장식 줄무늬 오탐이 도로 살아난다 (실측 22p).
            var aiMask = net.BuildMask(img, exclude, settings.DecensorMaskGrow, out kindUsed, bubbleFilter: settings.DecensorBubbleFilter);
            if (aiMask == null) kindUsed += " → 검열 없음";
            return aiMask;
        }
        return MosaicDetector.BuildMaskForTarget(settings, img, exclude, out kindUsed);
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.
    /// mode: "Basic" | "ComfyLama" | "ComfyRegen" (옛 "Strong"·"DeepCream"은 2026-10-19 폐기 — 모르는 값은 ArgumentException).</summary>
    /// <param name="regenSettings">재생성(ComfyRegen)에서만 쓰는 설정 — 결과 창이 '이 페이지만' 프롬프트·수치를 바꾼 사본(null이면 전역 설정).</param>
    /// <param name="kinds">기본(Basic)에서만 쓰는 종류 지도 — 없으면 손으로 칠한 자리로 보고 전부 막대 모델로 지운다(Aletheia 수동 복원과 같다).</param>
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct, AppSettings? regenSettings = null, Mat? kinds = null)
    {
        switch (mode)
        {
            case "ComfyCustom":
            {
                var wf = CustomWorkflow.Load(settings.CustomDecensorWorkflow);
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                wf = CustomWorkflow.Resolve(wf, await client.GetObjectInfoAsync(ct));   // 화면 포맷 변환 + 없는 노드·파일 점검
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("커스텀 워크플로로 복원 중...");
                await ComfyCustomDecensorAsync(client, outDir, wf, img, mask, 902, ct);
                return;
            }
            case "ComfyRegen":
            {
                CheckRegenModels();
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("검열 자리 재생성 중 (SDXL)...");
                await ComfyRegenDecensorAsync(client, outDir, img, mask, 901, ct, regenSettings);
                return;
            }
            case "Basic":
            {
                var lama = new LamaInpainter(_onnx);
                if (UseAletheia)
                {
                    if (!Aletheia.InpainterReady) throw new InvalidOperationException(AletheiaDecensor.InpainterMissing);
                    status?.Report("복원 중...");
                    Aletheia.Repair(img, mask, kinds, lama);
                    return;
                }
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
                throw new ArgumentException($"알 수 없는 복원 방식: {mode} (Basic | ComfyLama | ComfyRegen | ComfyCustom)", nameof(mode));
        }
    }

    /// <summary>커스텀 워크플로 검열제거 — 사용자가 ComfyUI 화면에서 짠 API JSON(CustomWorkflow 규약)에 마스크 PNG(알파=마스크)를 넣어 돌리고,
    /// 결과를 마스크 둘레 CustomDecensorReach px 안쪽만(0이면 전체) 원본에 되돌린다. 워크플로 내용은 앱이 손대지 않는다.</summary>
    private async Task ComfyCustomDecensorAsync(ComfyClient client, string comfyOutputDir, JsonObject workflow,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
    {
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_custom_{pageIdx}.png", ct);
        var prepared = CustomWorkflow.Prepare(workflow, up, $"nm_customout_{pageIdx}");
        var pid = await client.QueuePromptAsync(prepared.Prompt, ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(30), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("커스텀 워크플로 결과 파일이 없어요 — SaveImage 노드가 결과를 저장하는지 확인해 주세요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("커스텀 워크플로 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height), 0, 0, InterpolationFlags.Lanczos4);
        int reach = settings.CustomDecensorReach;
        if (reach <= 0) { resized.CopyTo(img); return; }
        using var take = new Mat();
        Cv2.Dilate(mask, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
        resized.CopyTo(img, take);
    }

    /// <summary>재생성 워크플로가 쓰는 SDXL 체크포인트·ControlNet 파일이 ComfyUI models 폴더에 있는지 먼저 본다 —
    /// 없으면 ComfyUI가 큐 단계에서 알아듣기 어려운 오류를 내므로 파일 이름과 폴더를 그대로 알려 준다.
    /// (모델 파일은 사용자가 직접 넣는다 — 앱은 내려받지 않는다.)</summary>
    private void CheckRegenModels()
    {
        var ck = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", "checkpoints", settings.RegenCheckpoint);
        var cnDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", "controlnet");
        var cn = Path.Combine(cnDir, settings.RegenControlNet);
        // 사용자 워크플로의 'diffusion_pytorch_model_promax.safetensors'는 xinsir union promax 그 파일이고, 앱 설치기는 같은 모델을
        // 'controlnet-union-sdxl-promax.safetensors'로 받아 둔다 → 이름만 다르면 설치된 promax를 쓴다 (체크포인트는 대체하지 않는다).
        if (!File.Exists(cn) && settings.RegenControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(cnDir, settings.ColorControlNet)) && settings.ColorControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase))
        {
            settings.RegenControlNet = settings.ColorControlNet;
            cn = Path.Combine(cnDir, settings.RegenControlNet);
        }
        if (!File.Exists(ck))
            throw new FileNotFoundException($"재생성 체크포인트가 없어요: {ck}\n설정 → 검열 제거 → 고급 옵션의 '재생성 체크포인트'를 ComfyUI models\\checkpoints 안의 파일 이름으로 맞춰 주세요.");
        if (!File.Exists(cn))
            throw new FileNotFoundException($"재생성 ControlNet이 없어요: {cn}\n설정 → 검열 제거 → 고급 옵션의 '재생성 ControlNet'을 ComfyUI models\\controlnet 안의 파일 이름으로 맞춰 주세요.");
    }

    /// <summary>재생성 검열제거 — 사용자가 ComfyUI에서 직접 짠 워크플로(2026-10-20, WorkflowBuilder.BuildRegenDecensor)로
    /// 페이지 전체를 보내 LaMa 1차 복원 → SDXL img2img(denoise 0.62, 선화 ControlNet) → 마스크 둘레만 합성해 받는다.
    /// 되가져올 때는 워크플로의 엣지 마스크(확장+블러)가 닿는 범위까지만 받아들이고 나머지 픽셀은 원본 그대로 둔다
    /// (VAE 왕복은 마스크 밖도 미세하게 바꾸지만 워크플로가 LaMa 결과 위에 합성하므로 밖은 LaMa 결과=원본이다).</summary>
    private async Task ComfyRegenDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, CancellationToken ct, AppSettings? regenSettings = null)
    {
        var rs = regenSettings ?? settings;   // 결과 창의 '이 페이지만' 프롬프트·수치 (마스크 확장값은 전역과 같아 되가져오는 범위도 그대로)
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_regen_{pageIdx}.png", ct);
        var pid = await client.QueuePromptAsync(
            WorkflowBuilder.BuildRegenDecensor(rs, up, $"nm_regenout_{pageIdx}"), ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("재생성 결과 파일이 없어요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("재생성 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height), 0, 0, InterpolationFlags.Lanczos4);
        // 엣지 마스크(확장+블러)가 닿는 범위 + 여유 2px 안쪽만 받아들인다
        int reach = Math.Max(settings.RegenEdgeGrow + settings.RegenEdgeBlur, settings.RegenCoreGrow + settings.RegenCoreBlur) + 2;
        using var take = new Mat();
        Cv2.Dilate(mask, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
        resized.CopyTo(img, take);
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
}
