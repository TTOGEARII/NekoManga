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
        if (DetectorOverride != null) { kindUsed = "시험용 감지기"; return DetectorOverride(img, exclude); }   // loc:none
        return Aletheia.Detect(img, exclude, out kindUsed, out kinds);
    }

    /// <summary>검출의 이 비율 이상이 글 블록 안에 있고, 그 블록의 이 비율 이상을 덮으면 글을 지운 자리가 잡힌 것으로 본다.</summary>
    private const double EraseHitInBlock = 0.6, EraseHitCoversBlock = 0.5;

    /// <summary>검열 마스크에서 **글을 지운 자리가 잡힌 것**을 걷어낸다 — 걷어낸 성분 수를 돌려준다(종류 지도 <paramref name="kinds"/>에서도 지운다).
    /// 검열 감지는 글을 지운 뒤의 원고에서 돈다. 흰 풍선의 글은 단색으로 칠해져 깨끗하지만, 톤(망점)으로 채운 풍선·그림 위의 글은 LaMa로 지우고
    /// 그 뭉개진 자리를 Aletheia가 모자이크로 본다(전용 AI 보완은 글 블록을 빼지만 Aletheia 검출에는 그 필터가 없다).
    /// 실측(2026-11-16, 사용자 원고 1쪽): 글을 지우기 전 원고에서는 막대 1곳(2,236px)만 잡히는데, 지운 뒤에는 글 블록 둘이 모자이크로 더 잡혔다(37,257px·36,223px).
    /// 그 둘은 검출의 87%·77%가 글 블록 안이고 블록의 94%·91%를 덮었다 — 검출이 글 블록과 거의 같은 자리·같은 크기다.
    /// 글 옆의 검열, 글을 가로지르는 가는 막대(블록의 일부만 덮는다), 글을 품은 넓은 검출(대부분이 블록 밖)은 남긴다.
    /// <paramref name="textBlocks"/>는 번역 단계가 실제로 지운 블록이어야 한다 — 글을 지우지 않은 원고에는 쓰지 않는다(지우지 않은 글은 검열로 잡히지 않는다).</summary>
    internal static int DropTextEraseHits(Mat mask, Mat? kinds, IReadOnlyList<Rect> textBlocks)
    {
        if (textBlocks.Count == 0 || Cv2.CountNonZero(mask) == 0) return 0;
        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents, PixelConnectivity.Connectivity8);
        var page = new Rect(0, 0, mask.Cols, mask.Rows);
        int dropped = 0;
        for (int i = 1; i < n; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            foreach (var block in textBlocks)
            {
                var b = block.Intersect(page);
                var common = b.Intersect(box);
                if (b.Width <= 0 || b.Height <= 0 || common.Width <= 0 || common.Height <= 0) continue;
                using var part = new Mat(labels, common);
                using var hit = new Mat();
                Cv2.InRange(part, new Scalar(i), new Scalar(i), hit);
                int inside = Cv2.CountNonZero(hit);
                if (inside < EraseHitInBlock * area || inside < EraseHitCoversBlock * b.Width * b.Height) continue;
                using var comp = new Mat();
                Cv2.InRange(labels, new Scalar(i), new Scalar(i), comp);
                mask.SetTo(Scalar.Black, comp);
                kinds?.SetTo(Scalar.Black, comp);
                dropped++;
                break;
            }
        }
        return dropped;
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.
    /// mode: "Basic" | "ComfyRegen" | "ComfyCustom" (옛 "Strong"·"DeepCream"은 2026-10-19, "ComfyLama"는 2026-11-12 폐기 — 모르는 값은 ArgumentException).</summary>
    /// <param name="regenSettings">재생성(ComfyRegen)에서만 쓰는 설정 — 결과 창이 '이 페이지만' 프롬프트·수치를 바꾼 사본(null이면 전역 설정).</param>
    /// <param name="kinds">기본(Basic)에서만 쓰는 종류 지도 — 없으면 손으로 칠한 자리로 보고 전부 막대 모델로 지운다(Aletheia 수동 복원과 같다).</param>
    /// <param name="sexMarks">재생성(ComfyRegen)에서만 — 사용자가 조각에 고른 성별(그림 좌표). 이 길은 사이드카가 없어 결과 창이 들고 있다가 넘긴다.</param>
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct, AppSettings? regenSettings = null, Mat? kinds = null, IReadOnlyList<SexMark>? sexMarks = null)
    {
        // 커스텀은 검열 조각이 덮은 부위 전체(합집합 마스크 하나)를, 재생성 v3는 영역 계획(영역마다 자르기·종류별 태그)을 쓴다 — 파이프라인 Stage2와 같은 규칙
        using var wideMask = mode == "ComfyCustom" ? RegenMaskFor(img, mask, new BodyPartDetector(_onnx), regenSettings) : null;
        var plan = mode == "ComfyRegen" ? RegenPlanFor(img, mask, new BodyPartDetector(_onnx), regenSettings, sexMarks) : null;
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
                status?.Report(Loc.T("커스텀 워크플로로 복원 중..."));
                await ComfyCustomDecensorAsync(client, outDir, wf, img, mask, 902, ct);
                return;
            }
            case "ComfyRegen":
            {
                CheckRegenModels();
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report(Loc.T("검열 자리 재생성 중 (SDXL)..."));
                try { await ComfyRegenDecensorAsync(client, outDir, img, plan!, 901, ct, regenSettings, status); }
                finally { foreach (var rg in plan!) rg.Dispose(); }
                return;
            }
            case "Basic":
            {
                var lama = new LamaInpainter(_onnx);   // 보완으로 찾은 자리(CensorKind.Legacy)만 LaMa로
                if (!Aletheia.InpainterReady) throw new InvalidOperationException(AletheiaDecensor.InpainterMissing);
                status?.Report(Loc.T("복원 중..."));
                Aletheia.Repair(img, mask, kinds, lama);
                return;
            }
            default:
                throw new ArgumentException(Loc.F($"알 수 없는 복원 방식: {mode} (Basic | ComfyRegen | ComfyCustom)"), nameof(mode));
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
    /// 감지기가 없거나 모델이 없으면 확장 경로(Unknown). 창이 겹치는 영역은 RegenRegion.BuildRegions가 합친다(RegenSplitRegions가 켜져 있으면 붙이는 범위가 겹칠 때만). 호출자가 Dispose.
    /// 부위 감지는 임계(RegenRegionConf)보다 낮은 약한 상자(RegenRegion.WeakConf)까지 받아 둔다 — 영역의 모양·종류는 임계 이상만 정하고(전과 같다: 점수순 NMS라
    /// 낮은 상자가 높은 상자를 지우지 못한다), 약한 상자는 조각의 성별(어느 태그를 넣을지)을 가리는 근거로만 쓴다.
    /// <paramref name="marks"/>: 사용자가 조각에 고른 성별(그림 좌표) — 근거보다 앞선다. 영역의 모양은 바꾸지 않는다.</summary>
    /// <param name="cumSource">질내사정의 자동 판단이 볼 그림 — 넘기지 않으면 <paramref name="img"/>. 채색된 장의 다시 렌더링은 img가 이미 한 번 다시 그려진 그림이라
    /// (지난번에 사정을 그렸으면 계속 사정, 안 그렸으면 계속 아님) 검열 제거 전 원고(.pre.png)를 넘긴다. 크기가 달라도 된다(상자를 옮긴다).</param>
    internal List<RegenRegionInfo> RegenPlanFor(Mat img, Mat censorMask, BodyPartDetector? detector, AppSettings? s = null, IReadOnlyList<SexMark>? marks = null, Mat? cumSource = null)
    {
        s ??= settings;
        return RegenRegion.BuildRegions(censorMask, RegenPartsFor(img, detector, s), s.RegenRegionPad, s.RegenWholeRegion ? s.RegenRegionGrow : 0, img.Size(), s.RegenCropSize, s.RegenRegionConf, marks, RegenRegion.SplitReachFor(s),
            cumSource != null ? CumRuleFor(cumSource, s, img.Size()) : CumRuleFor(img, s));
    }

    /// <summary>시험용 이음새 — 질내사정의 자동 판단을 모델 없이 갈아 끼운다(<see cref="DetectorOverride"/>와 같은 쓰임).</summary>
    internal Func<Mat, AppSettings, CumRule?>? CumRuleOverride { get; set; }

    /// <summary>질내사정의 자동 판단(<see cref="CumRule"/>) — 설정이 꺼져 있거나(RegenCumAuto), 사정 태그 칸이 비었거나, 모델이 없으면 null(사용자가 표시한 조각만 사정으로 친다).
    /// 점수는 <paramref name="img"/>에서 조각마다 잰다 — 실패해도 그 장을 실패로 두지 않는다(자동 판단 하나 때문에).
    /// 결과 창도 같은 것을 쓴다(조각의 사정 표시가 2차와 같게) — 돌려받은 규칙은 img를 붙들고 있으니 img를 해제하기 전에만 부른다.
    /// <paramref name="planSize">: 상자가 img가 아닌 다른 크기의 그림(영역 계획을 세운 그림) 좌표일 때 그 크기 — 상자를 img의 좌표로 옮겨서 잰다.
    /// 잴 수 없으면 점수는 -1이다(임계 밑이라 '아님'으로 간다 — 결과 창은 음수를 적어 두지 않는다). 한 번 실패하면 이 러너는 자동 판단을 더 하지 않는다:
    /// 깨진 모델 파일(ComfyUI 노드가 받다 만 것은 해시를 확인한 적이 없다)이면 조각마다 326MB를 다시 읽다 실패하는 일을 되풀이한다(검수).</summary>
    public CumRule? CumRuleFor(Mat img, AppSettings? s = null, Size? planSize = null)
    {
        s ??= settings;
        if (!CumAutoReady(s)) return null;
        CumRule? inner;
        if (CumRuleOverride != null) inner = CumRuleOverride(img, s);
        else
        {
            var detector = new CumDetector(_onnx, s.ComfyRoot);
            inner = new CumRule(box => detector.Score(img, box), s.RegenCumThreshold);
        }
        if (inner == null) return null;
        double fx = planSize is { Width: > 0, Height: > 0 } p ? (double)img.Width / p.Width : 1, fy = planSize is { Width: > 0, Height: > 0 } q ? (double)img.Height / q.Height : 1;
        var whole = new Rect(0, 0, img.Width, img.Height);
        return new CumRule(box =>
        {
            if (_cumBroken) return -1f;
            try
            {
                var at = fx == 1 && fy == 1 ? box : new Rect((int)Math.Round(box.X * fx), (int)Math.Round(box.Y * fy), Math.Max(1, (int)Math.Round(box.Width * fx)), Math.Max(1, (int)Math.Round(box.Height * fy)));
                at &= whole;
                return at.Width > 0 && at.Height > 0 ? inner.Score(at) : 0f;
            }
            catch (Exception ex)
            {
                _cumBroken = true;
                Console.Error.WriteLine($"[cum] 자동 판단을 끕니다 — {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");   // loc:none
                return -1f;
            }
        }, inner.Threshold);
    }

    private volatile bool _cumBroken;

    /// <summary>질내사정의 자동 판단을 쓸 수 있는가 — 설정이 켜져 있고(RegenCumAuto), 사정 태그 칸이 비지 않았고, 모델이 있고, 이 러너에서 실패한 적이 없다.</summary>
    public bool CumAutoReady(AppSettings? s = null)
    {
        s ??= settings;
        return s.RegenCumAuto && s.CumTags.Length > 0 && !_cumBroken && (CumRuleOverride != null || new CumDetector(_onnx, s.ComfyRoot).IsReady);
    }

    /// <summary>재생성의 영역 계획이 보는 부위 상자(penis/pussy, 약한 것까지 — 임계와 <see cref="RegenRegion.WeakConf"/> 중 낮은 쪽 이상). '부위 통째 다시 그리기'가 꺼져 있거나 모델이 없으면 빈 목록.</summary>
    private static IReadOnlyList<BodyPart> RegenPartsFor(Mat img, BodyPartDetector? detector, AppSettings s) =>
        s.RegenWholeRegion && detector is { IsReady: true }
            ? detector.Detect(img, (float)Math.Min(s.RegenRegionConf, RegenRegion.WeakConf)).Where(p => p.Cls is BodyPartDetector.ClsPenis or BodyPartDetector.ClsPussy).ToList()
            : [];

    /// <summary>결과 창의 **페이지별 검출 캐시**용 — 영역 계획(<see cref="RegenPlanFor"/>)이 보는 것과 같은 부위 상자. 창은 그림 사본으로 이것을 한 번 돌려 두고
    /// 마스크를 칠할 때마다 순수 함수(RegenRegion.Build·BuildRegions)만 다시 한다: 점선(다시 그려질 부위)은 임계 이상의 상자로, 조각의 성별 표시는 약한 상자까지로.</summary>
    public IReadOnlyList<BodyPart> DetectRegenParts(Mat img, AppSettings? s = null) => RegenPartsFor(img, new BodyPartDetector(_onnx), s ?? settings);

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
        var up = await client.UploadImageAsync(maskedPng, $"nm_custom_{_tag}_{pageIdx}.png", ct);
        ComfyClient.ComfyResult? r = null;
        var outPrefix = NextOutputPrefix("customout");
        Mat outImg;
        try
        {
            var prepared = CustomWorkflow.Prepare(workflow, up, outPrefix);
            var pid = await client.QueuePromptAsync(prepared.Prompt, ct);
            r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(30), ct);
            if (r.OutputFiles.Count == 0)
                throw new InvalidOperationException(Loc.T("커스텀 워크플로 결과 파일이 없어요 — SaveImage 노드가 결과를 저장하는지 확인해 주세요."));
            outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        }
        finally { WorkFolders.RemoveComfyFiles(settings.ComfyRoot, r?.OutputFiles, outPrefix, up); }   // 읽었으니 ComfyUI 폴더에는 남기지 않는다(실패·멈춤이어도 올린 입력과 그사이 저장된 출력은 지운다)
        using var outImgOwner = outImg;
        if (outImg.Empty()) throw new InvalidDataException(Loc.T("커스텀 워크플로 결과를 읽지 못했어요."));
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
            throw new FileNotFoundException(Loc.F($"재생성 체크포인트가 없어요: {ck}\n설정 → 검열 제거 → 고급 옵션의 '재생성 체크포인트'를 ComfyUI models\\checkpoints 안의 파일 이름으로 맞춰 주세요."));
        if (!File.Exists(cn))
            throw new FileNotFoundException(Loc.F($"재생성 ControlNet이 없어요: {cn}\n설정 → 검열 제거 → 고급 옵션의 '재생성 ControlNet'을 ComfyUI models\\controlnet 안의 파일 이름으로 맞춰 주세요."));
    }

    /// <summary>재생성 v3 검열제거(2026-11-12) — 영역(RegenPlanFor)마다 그 주변을 잘라(RegenCrop.WindowFor, 기본 1024) 두 PNG(알파=영역 / 알파=조각)를 올리고
    /// SDXL 두 단계 그래프(WorkflowBuilder.BuildRegenCrop, LaMa 없음)를 돌린 뒤, 결과를 창 크기로 되돌려 영역을 reach만큼 넓힌 범위 안쪽만 창 자리에 복사한다.
    /// 1단계가 지우는 범위는 기본으로 조각 안의 검은 막대 픽셀만(RegenEraseTight), 태그(RegenTags.For)는 한쪽 성별로 정해진 영역에만 그쪽을, 나머지는 남녀를 같이 넣는다(RegenTagsBySex, 2026-11-15)
    /// — 2026-11-13 사용자 판정. 조각의 성별이 섞인 영역은 설정(RegenTagsPerPiece)이 켜져 있으면 자리마다 그 성별의 태그를 따로 넣는다(RegenSexAreas — 자리 마스크 PNG를 하나 더 올린다).
    /// 영역은 순서대로 돈다(ComfyUI 큐 하나).
    /// 영역마다 쓴 시드를 순서대로 돌려준다(<see cref="RegenSeedFor"/>) — 2차가 사이드카에 적는다. <paramref name="lockedSeeds"/>: 이 장에 고정해 둔 시드.</summary>
    internal async Task<List<long>> ComfyRegenDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, IReadOnlyList<RegenRegionInfo> regions, int pageIdx, CancellationToken ct, AppSettings? regenSettings = null, IProgress<string>? status = null,
        IReadOnlyList<long>? lockedSeeds = null)
    {
        var rs = regenSettings ?? settings;
        int reach = RegenRegion.PasteReach(rs);
        var seeds = new List<long>();
        for (int k = 0; k < regions.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var region = regions[k];
            var w = RegenCrop.WindowFor(region.Bounds, img.Size(), rs.RegenCropSize);
            // 자리별 태그(RegenTagsPerPiece): 남녀 조각이 이 그림에 같이 있으면 자리마다 그 성별의 태그만 — 나눌 것이 없으면 null(영역 하나에 한 줄)
            var areas = RegenSexAreas.For(region, w, rs);
            using var areasOwner = new Disposables(areas);
            status?.Report(Loc.F($"재생성 중 ({k + 1}/{regions.Count}, {(areas != null ? RegenSexAreas.Note(areas, rs) + (RegenTags.CumUsed(RegenKind.Both, false, rs, region.Cum) ? Loc.T(" + 사정") : "") : RegenTags.Note(region.Sex, region.Sure, rs, region.Cum))})"));   // 어느 태그로 그리는지 보이게 — 남성 부위에 여성 태그가 들어갔는지 사용자가 알 수 있다
            using var tight = rs.RegenEraseTight ? RegenRegion.TightenErase(img, region.Erase) : null;   // 막대 픽셀만 지우기(기본)
            var (crop, cropRegion, cropErase) = RegenCrop.CropAndScale(img, region.Region, tight ?? region.Erase, w);
            using (crop) using (cropRegion) using (cropErase)
            {
                string? up = null, eraseUp = null, areaUp = null;
                ComfyClient.ComfyResult? r = null;
                var outPrefix = NextOutputPrefix("regenout");
                Mat outImg;
                try
                {
                    up = await client.UploadImageAsync(MosaicDetector.ToMaskedPng(crop, cropRegion), $"nm_regen_{_tag}_{pageIdx}_{k}.png", ct);
                    eraseUp = await client.UploadImageAsync(MosaicDetector.ToMaskedPng(crop, cropErase), $"nm_regenmask_{_tag}_{pageIdx}_{k}.png", ct);
                    if (areas != null)
                    {
                        using var packed = RegenSexAreas.Pack(areas);
                        Cv2.ImEncode(".png", packed, out var areaPng);
                        areaUp = await client.UploadImageAsync(areaPng, $"nm_regenarea_{_tag}_{pageIdx}_{k}.png", ct);
                    }
                    long seed = RegenSeedFor(rs, lockedSeeds, k);
                    seeds.Add(seed);
                    var graph = WorkflowBuilder.BuildRegenCrop(rs, up, eraseUp, outPrefix, RegenTags.For(region.Sex, region.Sure, rs, region.Cum), w.SendW, w.SendH, seed,
                        areaUp, areas?.Select(a => a.Tags).ToList());
                    var pid = await client.QueuePromptAsync(graph, ct);
                    r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
                    if (r.OutputFiles.Count == 0) throw new InvalidOperationException(Loc.T("재생성 결과 파일이 없어요."));
                    outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
                }
                // 읽었으니 ComfyUI 폴더에는 남기지 않는다 — 영역마다 입력 둘·출력 하나가 쌓였다(실측 2026-11-13: output\nm_* 566MB). 실패·멈춤이어도 올린 입력은 지운다
                finally { WorkFolders.RemoveComfyFiles(settings.ComfyRoot, r?.OutputFiles, outPrefix, up, eraseUp, areaUp); }
                using var outImgOwner = outImg;
                if (outImg.Empty()) throw new InvalidDataException(Loc.T("재생성 결과를 읽지 못했어요."));
                using var back = outImg.Size() == w.Window.Size ? outImg.Clone() : outImg.Resize(w.Window.Size, 0, 0, InterpolationFlags.Lanczos4);
                // 영역(+엣지가 닿는 범위)만 창 자리에 — 나머지 픽셀은 원본 그대로
                using var regionRoi = new Mat(region.Region, w.Window);
                using var take = new Mat();
                Cv2.Dilate(regionRoi, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
                using var dst = new Mat(img, w.Window);
                back.CopyTo(dst, take);
            }
        }
        return seeds;
    }

    /// <summary>재생성 영역 k의 1단계 시드 — 이 장에 고정해 둔 시드가 있으면 그것(영역 순서대로), 없으면 설정의 시드, 그것도 -1이면 새로 뽑는다.
    /// 고정한 것보다 영역이 늘었으면(마스크를 고쳐 조각이 더 생겼다) 남는 영역은 설정대로. 뽑는 범위는 전과 같다(2단계가 +1을 쓴다).</summary>
    internal static long RegenSeedFor(AppSettings rs, IReadOnlyList<long>? locked, int k) =>
        locked != null && k < locked.Count ? locked[k] : rs.RegenSeed >= 0 ? rs.RegenSeed : Random.Shared.NextInt64(0, uint.MaxValue);

    /// <summary>using 한 줄로 목록째 해제 — 없으면(null) 아무것도 안 한다.</summary>
    private readonly struct Disposables(IEnumerable<IDisposable>? items) : IDisposable
    {
        public void Dispose() { if (items != null) foreach (var d in items) d.Dispose(); }
    }
}
