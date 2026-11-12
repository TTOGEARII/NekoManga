using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 검열 감지와 복원 — 마스크 감지, 결과 창의 수동 복원, ComfyUI 엔진 복원(커스텀 워크플로·재생성)
public partial class PipelineRunner
{
    /// <summary>검열 마스크만 필요할 때 (진단·검토 창 미리보기) — 종류 지도는 버린다.</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, bool autoBubbles = false)
    {
        var mask = DetectCensorMask(img, exclude, out kindUsed, out var kinds, autoBubbles);
        kinds?.Dispose();
        return mask;
    }

    /// <summary>검열 마스크 + 종류 지도 — Aletheia Lens(막대·모자이크)에 기존 방식의 흰선 보완을 더한 것(AletheiaDecensor.Detect).
    /// 시험용 DetectorOverride가 있으면 그것이 감지한다(종류 지도 없음). ('이전 방식' CensorNet 1순위·규칙 폴백 경로는 2026-11-12 삭제.)</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, out Mat? kinds, bool autoBubbles = false) =>
        DetectCensor(img, exclude, out kindUsed, out kinds, autoBubbles);

    private Mat? DetectCensor(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, out Mat? kinds, bool autoBubbles)
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
        if (DetectorOverride != null) { kindUsed = "시험용 감지기"; return DetectorOverride(img, exclude); }
        return Aletheia.Detect(img, exclude, out kindUsed, out kinds);
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.
    /// mode: "Basic" | "ComfyRegen" | "ComfyCustom" (옛 "Strong"·"DeepCream"은 2026-10-19, "ComfyLama"는 2026-11-12 폐기 — 모르는 값은 ArgumentException).</summary>
    /// <param name="regenSettings">재생성(ComfyRegen)에서만 쓰는 설정 — 결과 창이 '이 페이지만' 프롬프트·수치를 바꾼 사본(null이면 전역 설정).</param>
    /// <param name="kinds">기본(Basic)에서만 쓰는 종류 지도 — 없으면 손으로 칠한 자리로 보고 전부 막대 모델로 지운다(Aletheia 수동 복원과 같다).</param>
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct, AppSettings? regenSettings = null, Mat? kinds = null)
    {
        // 커스텀은 검열 조각이 덮은 부위 전체(합집합 마스크 하나)를, 재생성 v3는 영역 계획(영역마다 자르기·종류별 태그)을 쓴다 — 파이프라인 Stage2와 같은 규칙
        using var wideMask = mode == "ComfyCustom" ? RegenMaskFor(img, mask, new BodyPartDetector(_onnx), regenSettings) : null;
        var plan = mode == "ComfyRegen" ? RegenPlanFor(img, mask, new BodyPartDetector(_onnx), regenSettings) : null;
        if (wideMask != null) mask = wideMask;
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
                try { await ComfyRegenDecensorAsync(client, outDir, img, plan!, 901, ct, regenSettings, status); }
                finally { foreach (var rg in plan!) rg.Dispose(); }
                return;
            }
            case "Basic":
            {
                var lama = new LamaInpainter(_onnx);   // 보완으로 찾은 자리(CensorKind.Legacy)만 LaMa로
                if (!Aletheia.InpainterReady) throw new InvalidOperationException(AletheiaDecensor.InpainterMissing);
                status?.Report("복원 중...");
                Aletheia.Repair(img, mask, kinds, lama);
                return;
            }
            default:
                throw new ArgumentException($"알 수 없는 복원 방식: {mode} (Basic | ComfyRegen | ComfyCustom)", nameof(mode));
        }
    }

    /// <summary>커스텀 워크플로 검열제거 — 사용자가 ComfyUI 화면에서 짠 API JSON(CustomWorkflow 규약)에 마스크 PNG(알파=마스크)를 넣어 돌리고,
    /// 결과를 마스크 둘레 CustomDecensorReach px 안쪽만(0이면 전체) 원본에 되돌린다. 워크플로 내용은 앱이 손대지 않는다.</summary>
    /// <summary>재생성·커스텀이 다시 그릴 마스크 — 설정(또는 결과 창의 페이지 설정 사본 <paramref name="s"/>)의 '부위 통째 다시 그리기'가 꺼져 있으면 사본 그대로.
    /// 켜져 있으면 부위 감지기(penis/pussy, 임계 RegenRegionConf)가 찾은 상자 중 검열 조각과 겹치는 것을 여백만큼 넓혀 합치고, 검출이 없는 조각은 배율로 넓힌다
    /// (<see cref="RegenRegion.Build"/>). 감지기가 없거나(모델 미설치) null이면 전부 확장 경로 — 기능은 돌아간다. 돌려주는 Mat은 호출자가 해제한다.</summary>
    public Mat RegenMaskFor(Mat img, Mat mask, BodyPartDetector? detector, AppSettings? s = null)
    {
        s ??= settings;
        if (!s.RegenWholeRegion) return mask.Clone();
        IReadOnlyList<Rect> parts = detector is { IsReady: true }
            ? detector.Detect(img, (float)s.RegenRegionConf).Where(p => p.Cls is BodyPartDetector.ClsPenis or BodyPartDetector.ClsPussy).Select(p => p.Box).ToList()
            : [];
        return RegenRegion.Build(mask, parts, s.RegenRegionPad, s.RegenRegionGrow);
    }

    /// <summary>재생성 v3의 영역 계획 — 검열 조각마다 (넓힌 영역, 지우기 마스크, 부위 종류). 설정이 꺼져 있으면 영역 = 조각 그대로(Unknown).
    /// 감지기가 없거나 모델이 없으면 확장 경로(Unknown). 창이 겹치는 영역은 RegenRegion.BuildRegions가 합친다. 호출자가 Dispose.</summary>
    internal List<RegenRegionInfo> RegenPlanFor(Mat img, Mat censorMask, BodyPartDetector? detector, AppSettings? s = null)
    {
        s ??= settings;
        IReadOnlyList<BodyPart> parts = s.RegenWholeRegion && detector is { IsReady: true }
            ? detector.Detect(img, (float)s.RegenRegionConf).Where(p => p.Cls is BodyPartDetector.ClsPenis or BodyPartDetector.ClsPussy).ToList()
            : [];
        return RegenRegion.BuildRegions(censorMask, parts, s.RegenRegionPad, s.RegenWholeRegion ? s.RegenRegionGrow : 0, img.Size(), s.RegenCropSize);
    }

    /// <summary>부위 상자(penis/pussy, 그림 좌표)만 — 결과 창 미리보기의 **페이지별 검출 캐시**용. 모델이 없으면 빈 목록.
    /// 미리보기는 그림 사본으로 이것을 한 번 돌려 두고, 마스크를 칠할 때마다 <see cref="RegenRegion.Build"/>(순수 함수)만 다시 한다 —
    /// 창의 그림을 백그라운드에서 빌려 쓰다 해제되는 사고(검수 Critical)를 피하고 획마다 YOLO를 돌리지 않는다.</summary>
    public IReadOnlyList<Rect> DetectBodyParts(Mat img, AppSettings? s = null)
    {
        s ??= settings;
        var detector = new BodyPartDetector(_onnx);
        if (!detector.IsReady) return [];
        return detector.Detect(img, (float)s.RegenRegionConf).Where(p => p.Cls is BodyPartDetector.ClsPenis or BodyPartDetector.ClsPussy).Select(p => p.Box).ToList();
    }

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

    /// <summary>재생성 v3 검열제거(2026-11-12) — 영역(RegenPlanFor)마다 그 주변을 잘라(RegenCrop.WindowFor, 기본 1024) 두 PNG(알파=영역 / 알파=조각)를 올리고
    /// SDXL 두 단계 그래프(WorkflowBuilder.BuildRegenCrop, LaMa 없음)를 돌린 뒤, 결과를 창 크기로 되돌려 영역을 reach만큼 넓힌 범위 안쪽만 창 자리에 복사한다.
    /// 종류별 태그(RegenTags.For)는 영역의 부위 종류(penis/pussy 검출)로 고른다. 영역은 순서대로 돈다(ComfyUI 큐 하나).</summary>
    private async Task ComfyRegenDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, IReadOnlyList<RegenRegionInfo> regions, int pageIdx, CancellationToken ct, AppSettings? regenSettings = null, IProgress<string>? status = null)
    {
        var rs = regenSettings ?? settings;
        int reach = Math.Max(rs.RegenEdgeGrow + rs.RegenEdgeBlur, rs.RegenCoreGrow + rs.RegenCoreBlur) + 2;
        for (int k = 0; k < regions.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var region = regions[k];
            status?.Report($"재생성 중 ({k + 1}/{regions.Count})");
            var w = RegenCrop.WindowFor(region.Bounds, img.Size(), rs.RegenCropSize);
            var (crop, cropRegion, cropErase) = RegenCrop.CropAndScale(img, region.Region, region.Erase, w);
            using (crop) using (cropRegion) using (cropErase)
            {
                var up = await client.UploadImageAsync(MosaicDetector.ToMaskedPng(crop, cropRegion), $"nm_regen_{pageIdx}_{k}.png", ct);
                var eraseUp = await client.UploadImageAsync(MosaicDetector.ToMaskedPng(crop, cropErase), $"nm_regenmask_{pageIdx}_{k}.png", ct);
                var graph = WorkflowBuilder.BuildRegenCrop(rs, up, eraseUp, $"nm_regenout_{pageIdx}_{k}", RegenTags.For(region.Kind, rs), w.SendW, w.SendH);
                var pid = await client.QueuePromptAsync(graph, ct);
                var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
                if (r.OutputFiles.Count == 0) throw new InvalidOperationException("재생성 결과 파일이 없어요.");
                using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
                if (outImg.Empty()) throw new InvalidDataException("재생성 결과를 읽지 못했어요.");
                using var back = outImg.Size() == w.Window.Size ? outImg.Clone() : outImg.Resize(w.Window.Size, 0, 0, InterpolationFlags.Lanczos4);
                // 영역(+엣지가 닿는 범위)만 창 자리에 — 나머지 픽셀은 원본 그대로
                using var regionRoi = new Mat(region.Region, w.Window);
                using var take = new Mat();
                Cv2.Dilate(regionRoi, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
                using var dst = new Mat(img, w.Window);
                back.CopyTo(dst, take);
            }
        }
    }
}
