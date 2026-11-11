using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

/// <param name="File">원고의 **전체 경로** — 메인 창이 행을 찾는 키 (이름만 주면 vol1\01.jpg·vol2\01.jpg가 한 행에 겹친다).</param>
/// <param name="Percent">진행률 0~100. -1은 진행률을 바꾸지 않는 경고 보고.</param>
public record PageProgress(string File, string Stage, double Percent, bool Done = false, string? Error = null);

/// <summary>페이지 처리 파이프라인.
///
///   원고 → [번역: 내장 엔진이 글자 감지→OCR→번역→글자 지움]
///        → [검열제거: 검열 감지(Aletheia Lens + 기존 흰선 보완) → 기본(Aletheia DeepCreamPy, 기존 흰선은 LaMa) / 엔진 LaMa·재생성·커스텀(ComfyUI)]
///        → [자동채색: ComfyUI SDXL]
///        → [식자: 번역문을 C#으로 렌더]
///        → 완성본
///
/// 번역·기본 검열제거는 전부 프로세스 안(ONNX)에서 돌고, 채색·엔진 LaMa만 ComfyUI가 필요하다.
/// 검열 감지·기본 복원은 2026-11-09에 Aletheia Lens 이식(AletheiaDecensor)으로 교체 — 옛 경로(CensorNet/규칙 + LaMa)는
/// settings.json의 DecensorEngine="Legacy"로 남아 있다.</summary>
public partial class PipelineRunner(AppSettings settings) : IDisposable
{
    private readonly OnnxEnv _onnx = new(settings);
    private AletheiaDecensor? _aletheia;
    private AletheiaDecensor Aletheia => _aletheia ??= new AletheiaDecensor(settings, _onnx);
    private bool UseAletheia => settings.DecensorEngine != "Legacy";
    private string? _refImageName;   // IPAdapter용으로 업로드한 표지 (서버에 없으면 null)
    private readonly ComfyServer _server = new(settings);

    /// <summary>시험용 — 프로세스·HTTP 없는 가짜 장부의 ComfyServer를 넣는다.</summary>
    internal PipelineRunner(AppSettings s, ComfyServer server) : this(s) => _server = server;

    /// <summary>한 실행의 공유 상태 — PrepareAsync가 채우고 Stage1/Stage2가 같이 쓴다.</summary>
    private sealed class RunContext
    {
        public required IProgress<PageProgress> Progress;
        public EmbeddedTranslator? Translator;
        public required LamaInpainter Lama;
        public bool ComfyLama, ComfyRegen, ComfyCustom;
        /// <summary>이번 실행의 감지·기본 복원 엔진(Aletheia / 이전 방식) — 시작할 때 박제한다. 실행 중에 설정 창에서 엔진을 바꿔도
        /// 준비 단계에서 확인하지 않은 모델을 찾다 남은 장이 전부 실패하거나, 한 엔진의 마스크를 다른 엔진이 지우는 일이 없게.</summary>
        public bool Aletheia;
        public JsonObject? CustomDecensor, CustomColor;
        public ComfyClient? Client;
        public string ComfyOutputDir = "";
        public required string WorkDir;
        /// <summary>이번 실행의 원고 전체 경로 키(FileKey) — 결과물·옛 이름 옮기기가 **목록의 원고 파일**을 건드리지 않게.</summary>
        public HashSet<string> FileKeys = new(StringComparer.Ordinal);
        public CancellationToken Ct;
        /// <summary>검토 흐름 — 감지가 비어도 사용자가 칠할 수 있게 빈 마스크를 만들고, 마스크 사이드카를 남긴다.</summary>
        public bool Review;
        /// <summary>페이지별 검토 흐름 (검토 대기 알림은 RunPerPageAsync가 한다).</summary>
        public bool PerPage;
    }

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        // 이 작업의 ComfyUI 사용(PrepareAsync의 EnsureRunningAsync가 잡는다)은 끝나면 놓는다 — 취소·예외로 끝나도.
        // KeepComfyWarm=false여도 다른 사용자(작업 중에 열린 결과 창의 [채색부터 다시] 등)가 쥐고 있으면 서버는 끄지 않는다(ComfyServer)
        try { await RunCoreAsync(files, progress, ct); }
        finally { _server.Release(); }
    }

    private async Task RunCoreAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var pathSettings = settings.ShallowClone();   // 결과 경로는 **지금**의 출력 폴더로 — PrepareAsync(ComfyUI 기동 등 수십 초) 동안 설정 창에서 바꿔도 이 작업은 시작 때 폴더에 쓴다
        var cx = await PrepareAsync(files, progress, needTranslator: settings.EnableTranslate, ct);
        var pages = new List<ReviewPage>(files.Count);
        var firstOf = new Dictionary<string, ReviewPage>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var stem = OutputStemFor(pathSettings, f);
            var pg = new ReviewPage(f) { OutPath = OutputPathFor(pathSettings, f), Stem = stem, WorkKey = $"{pages.Count:D4}_{stem}" };
            // 드물게 둘이 같은 결과 자리로 떨어진다(출력 폴더 고정 + 상위 폴더 이름까지 같은 두 화 — A\ch1\001.jpg·B\ch1\001.jpg) — 뒤 장은 1차에서 실패로
            if (!firstOf.TryAdd(FileKey(pg.OutPath), pg)) pg.DuplicateOf = firstOf[FileKey(pg.OutPath)].File;
            pages.Add(pg);
        }

        // ── 검토 흐름 (2026-10-20 "감지 → 사용자가 마스크 추가 → 제거", 2026-10-21 페이지별 방식 추가)
        //    Window: 모든 페이지 1차 → 검토 창 하나(ReviewMasks) → 모든 페이지 2차 (옛 방식)
        //    PerPage: 1차를 돌리며 감지 0인 페이지는 곧장 2차 큐로, 감지된 페이지는 '검토 대기'(PageWaiting)로 두고 ConfirmPage가 오면 2차 큐로.
        //    둘 다 아니면(설정 꺼짐·헤드리스) 페이지마다 1차·2차를 이어서 돈다.
        bool windowReview = settings.EnableDecensor && settings.DecensorReview && settings.DecensorReviewMode == "Window" && ReviewMasks != null;
        bool perPage = settings.EnableDecensor && settings.DecensorReview && settings.DecensorReviewMode == "PerPage" && PageWaiting != null;
        cx.Review = windowReview || perPage;
        cx.PerPage = perPage;
        try
        {
            if (windowReview) await RunWindowReviewAsync(cx, pages, ct);
            else if (perPage) await RunPerPageAsync(cx, pages, ct);
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Stage1(cx, pages[i], i); await Stage2(cx, pages[i], i); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // 사용자 취소만 전체 중단 — HTTP 타임아웃 같은 취소 예외는 그 장만 실패
                    catch (Exception ex) { Fail(cx, pages[i], ex); }
                }
            }
        }
        finally
        {
            foreach (var pg in pages) pg.DisposeMats();
            lock (_waiting) _waiting.Clear();
        }
    }

    /// <summary>모델·구성요소 확인, ComfyUI 기동, 표지 색 참조 준비. RunAsync와 RedoPageAsync가 공유한다.</summary>
    private async Task<RunContext> PrepareAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, bool needTranslator, CancellationToken ct)
    {
        var translator = needTranslator ? new EmbeddedTranslator(settings, _onnx) : null;
        var lama = new LamaInpainter(_onnx);

        bool comfyLama = settings.EnableDecensor && settings.DecensorMode == "ComfyLama";
        bool comfyRegen = settings.EnableDecensor && settings.DecensorMode == "ComfyRegen";
        bool comfyCustom = settings.EnableDecensor && settings.DecensorMode == "ComfyCustom";
        bool colorCustom = settings.EnableAutoColor && !string.IsNullOrWhiteSpace(settings.ColorCustomWorkflow);
        bool needComfy = settings.EnableAutoColor || comfyLama || comfyRegen || comfyCustom;

        if (translator is { IsReady: false })
            throw new FileNotFoundException("번역 엔진 모델이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (settings.EnableDecensor && !comfyLama && !comfyRegen && !comfyCustom && !lama.IsReady)
            throw new FileNotFoundException("검열제거(기본) 모델(LaMa)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (settings.EnableDecensor && UseAletheia)
        {
            if (!Aletheia.DetectorReady) throw new FileNotFoundException(AletheiaDecensor.DetectorMissing);
            if (!comfyLama && !comfyRegen && !comfyCustom && !Aletheia.InpainterReady) throw new FileNotFoundException(AletheiaDecensor.InpainterMissing);
        }
        if (needComfy && !settings.ComfyAvailable)
            throw new FileNotFoundException(
                (settings.EnableAutoColor ? "자동 채색" : comfyRegen ? "재생성 검열제거" : comfyCustom ? "커스텀 워크플로 검열제거" : "엔진 LaMa 검열제거") +
                " 기능은 ComfyUI가 필요해요 — 설정 → 구성요소에서 ComfyUI를 설치해 주세요.");
        if (comfyRegen) CheckRegenModels();

        var cx = new RunContext
        {
            Progress = progress,
            Translator = translator,
            Lama = lama,
            ComfyLama = comfyLama, ComfyRegen = comfyRegen, ComfyCustom = comfyCustom,
            Aletheia = UseAletheia,
            // 커스텀 워크플로 파일은 시작 전에 한 번 읽어 규약(LoadImage/SaveImage)을 검사한다 — 페이지마다 같은 오류로 실패하지 않게
            CustomDecensor = comfyCustom ? CustomWorkflow.Load(settings.CustomDecensorWorkflow) : null,
            CustomColor = colorCustom ? CustomWorkflow.Load(settings.ColorCustomWorkflow) : null,
            WorkDir = Path.Combine(AppSettings.DataDir, "work", $"job-{DateTime.Now:yyyyMMdd-HHmmss}"),
            FileKeys = files.Select(FileKey).ToHashSet(StringComparer.Ordinal),
            Ct = ct,
        };

        if (needComfy)
        {
            var status = new Progress<string>(m => Report(progress, files, m, 10));
            await _server.EnsureRunningAsync(status, ct);   // 이 러너의 사용을 잡는다 — 놓기는 RunAsync 끝(본 작업)·Dispose(결과 창이 닫힐 때)
            cx.Client = new ComfyClient(_server.BaseUrl);
            cx.ComfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");

            // 커스텀 워크플로: 화면 저장 포맷이면 여기서 API 포맷으로 바꾸고, 서버에 없는 노드·모델 파일을 첫 페이지 전에 알린다
            if (cx.CustomDecensor != null || cx.CustomColor != null)
            {
                var nodeInfo = await cx.Client.GetObjectInfoAsync(ct);
                if (cx.CustomDecensor != null) cx.CustomDecensor = CustomWorkflow.Resolve(cx.CustomDecensor, nodeInfo);
                if (cx.CustomColor != null) cx.CustomColor = CustomWorkflow.Resolve(cx.CustomColor, nodeInfo);
            }

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
                    if (await cx.Client.HasNodeAsync("IPAdapter", ct))
                    {
                        var refBytes = await File.ReadAllBytesAsync(settings.ColorRefImage, ct);
                        _refImageName = await cx.Client.UploadImageAsync(refBytes, "nm_cover_ref.png", ct);
                    }
                    // 노드를 모르면 = 서버가 설치 전부터 떠 있던 것 — 이번 실행은 태그만으로 간다
                }
                catch { _refImageName = null; }
        }
        return cx;
    }

    private static void Fail(RunContext cx, ReviewPage pg, Exception ex)
    {
        pg.Error = ex.Message;
        cx.Progress.Report(new(pg.File, "실패", 100, Done: true, Error: ex.Message));
    }

    // ── 1차: 번역 + 검열 감지 (마스크는 페이지가 들고 있다 — 검토 창/마스크 탭이 편집한다) ──
    private async Task Stage1(RunContext cx, ReviewPage pg, int i)
    {
        var name = pg.File;
        var progress = cx.Progress;
        if (ResultSlotConflict(cx, pg) is { } conflict) throw new IOException(conflict);
        MigrateLegacyName(cx, pg);   // v1.5.5까지의 이름(<원고 이름>.png) 밑에 남은 이 원고의 결과·사이드카를 새 이름으로 한 번 옮긴다
        if (cx.Translator != null)
        {
            var status = new Progress<string>(m => progress.Report(new(name, m, 25)));
            var tr = await cx.Translator.TranslatePageAsync(pg.WorkPath, cx.WorkDir, status, cx.Ct, pg.WorkKey);
            pg.WorkPath = tr.InpaintedPath;
            pg.Blocks = tr.Blocks;
        }
        if (!settings.EnableDecensor) return;
        using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);

        // 같은 원고를 다시 돌릴 때: .neko에 남은 마스크(사용자가 고친 것 포함)가 있으면 감지 대신 쓴다 — 고친 마스크가 재실행에도 유지된다
        // 사이드카는 결과 파일 이름으로만 찾아지므로 **같은 원고인지**(경로 또는 이름+바이트 길이)를 꼭 확인한다 — 출력 폴더를 고정해 두고
        // 1화·2화처럼 파일 이름·해상도가 같은 다른 원고를 돌리면 남의 마스크로 감지·검토를 건너뛰는 사고가 난다
        if (settings.ReuseStoredMasks && MaskSidecar.Load(pg.OutPath) is { } stored && stored.MatchesSource(pg.File)
            && stored.SrcW == img.Width && stored.SrcH == img.Height && MaskSidecar.LoadMask(pg.OutPath) is { } storedMask)
        {
            // 폴더를 옮긴 경우 경로 갱신 (결과 창의 대기 판정이 이 경로를 쓴다). 옛 경로가 아직 있으면(폴더를 복사함) 소유권은 그대로 — 마스크만 빌려 쓴다
            if ((!string.Equals(stored.Source, pg.File, StringComparison.OrdinalIgnoreCase) || stored.SrcLen == 0)
                && (stored.SrcLen == 0 || !File.Exists(stored.Source)))
                try { (stored with { Source = pg.File, SrcLen = MaskSidecar.LengthOf(pg.File) }).Save(pg.OutPath); }
                catch (Exception ex) { Warn(cx, pg, "마스크 경로 갱신 실패: " + ex.Message); }
            pg.Mask = storedMask;
            pg.Kinds = MaskSidecar.LoadKinds(pg.OutPath);   // 지난번 감지가 남긴 종류 지도 — 같은 엔진으로 지운다 (옛 사이드카엔 없다 → 전부 막대 모델)
            pg.AutoMask = MaskSidecar.LoadAuto(pg.OutPath);
            pg.Skip = stored.Skip;
            pg.StoredConfirmed = stored.Confirmed;
            pg.Summary = stored.Summary;   // 감지 요약을 그대로 둔다 — 표시용 문자열로 덮어쓰면 학습 재료 index에 그게 남는다 (상태 줄에만 '저장된 마스크'를 쓴다)
            progress.Report(new(name, pg.Skip ? "저장된 마스크 — 검열 없음으로 표시됨" : $"저장된 마스크 사용{(stored.Edited ? " (수정됨)" : "")}", 46));
            if (cx.Review)
            {
                try { ImgIo.Write(MaskSidecar.PrePathFor(pg.OutPath), img); }   // 원고는 이번 번역 결과로 갱신
                catch (Exception ex) { pg.SidecarError = ex.Message; Warn(cx, pg, "원고 사본(.pre) 갱신 실패: " + ex.Message); }
                pg.SidecarSaved = MaskSidecar.HasPre(pg.OutPath);   // 갱신에 실패해도 옛 pre가 남아 있으면 마스크 탭은 열린다
            }
            return;
        }

        progress.Report(new(name, "검열 영역 찾는 중", 45));
        // 말풍선은 흰 막대로 오인되기 쉬우므로 감지된 글자 블록을 제외 영역으로 넘긴다.
        var exclude = pg.Blocks.Select(b =>
            new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();
        // 검열 추적: AI 모델(합성 학습 전용 모델)이 있으면 그게 1순위 — 흰/검/모자이크를 한 번에 찾는다. 없거나 못 찾으면 규칙 기반으로 폴백.
        // autoBubbles는 말풍선 필터를 켠 경우에만 — 필터가 꺼져 있으면 글자 감지는 검/모자이크의 글자 상자 빼기에만 쓰여 비용 대비 효과가 작다
        var mask = DetectCensor(img, exclude, out var kindUsed, out var kinds, autoBubbles: settings.DecensorBubbleFilter, aletheia: cx.Aletheia);
        pg.Kinds = kinds;
        pg.Summary = kindUsed;
        if (settings.DecensorTarget == "Auto")
            progress.Report(new(name, $"검열 분석: {kindUsed}", 46));
        // 검토 흐름에서는 감지가 비었어도 사용자가 칠할 수 있게 빈 마스크를 준다
        pg.Mask = mask ?? (cx.Review ? new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black) : null);
        if (cx.Review)
        {
            pg.AutoMask = pg.Mask?.Clone();   // 수정 전 사본 — '자동 감지로' 되돌리기 + 학습 재료의 '자동 감지' 쪽
            // 감지가 없어도 사이드카를 남긴다 — 나중에 결과 창의 마스크 탭에서 놓친 검열을 칠해 다시 돌릴 수 있게
            pg.SidecarSaved = SaveMaskSidecar(cx, pg, confirmed: false, edited: false, pre: img, freshDetection: true);
            if (!cx.PerPage) progress.Report(new(name, mask == null ? "검토 대기 (감지 없음)" : "검토 대기", 48));   // 페이지별은 호출부가 대기/진행을 알린다
        }
    }

    // ── 2차: 검열 제거 + 채색 + 식자/저장 ──
    private async Task Stage2(RunContext cx, ReviewPage pg, int i)
    {
        var name = pg.File; var file = pg.File; var work = pg.WorkPath; var blocks = pg.Blocks;
        var progress = cx.Progress;
        if (settings.EnableDecensor && pg.Mask != null && !pg.Skip && Cv2.CountNonZero(pg.Mask) > 0)
        {
            using var img = ImgIo.Read(work, ImreadModes.Color);
            var mask = pg.Mask;
            if (mask.Size() != img.Size())
            {
                var rs = new Mat(); Cv2.Resize(mask, rs, img.Size(), 0, 0, InterpolationFlags.Nearest); mask = rs;
            }
            if (cx.ComfyCustom)
            {
                progress.Report(new(name, "검열 지우는 중 (커스텀 워크플로·GPU)", 50));
                await ComfyCustomDecensorAsync(cx.Client!, cx.ComfyOutputDir, cx.CustomDecensor!, img, mask, i, cx.Ct);
            }
            else if (cx.ComfyRegen)
            {
                progress.Report(new(name, "검열 자리 재생성 중 (SDXL·GPU)", 50));
                await ComfyRegenDecensorAsync(cx.Client!, cx.ComfyOutputDir, img, mask, i, cx.Ct);
            }
            else if (cx.ComfyLama)
            {
                progress.Report(new(name, "검열 지우는 중 (엔진 LaMa·GPU)", 50));
                await ComfyLamaDecensorAsync(cx.Client!, cx.ComfyOutputDir, img, mask, i, cx.Ct);
            }
            else if (cx.Aletheia)
            {
                progress.Report(new(name, "검열 지우는 중 (기본·Aletheia)", 50));
                var kindsMap = pg.Kinds;
                await Task.Run(() => Aletheia.Repair(img, mask, kindsMap, cx.Lama), cx.Ct);
            }
            else
            {
                progress.Report(new(name, "검열 지우는 중 (기본·LaMa)", 50));
                await Task.Run(() => cx.Lama.Inpaint(img, mask), cx.Ct);
            }
            if (!ReferenceEquals(mask, pg.Mask)) mask.Dispose();
            work = Path.Combine(cx.WorkDir, pg.WorkKey + ".decensored.png");   // 한 작업 폴더를 모든 장이 쓴다 — 1화\001.jpg·2화\001.jpg가 서로 덮지 않게 순번 포함 키
            Directory.CreateDirectory(cx.WorkDir);
            ImgIo.Write(work, img);
        }

        // ── 자동채색 (ComfyUI) ──
        if (settings.EnableAutoColor && cx.Client != null)
        {
            progress.Report(new(name, cx.CustomColor != null ? "채색 중 (커스텀 워크플로)" : "채색 중 (페이지당 1~2분)", 60));
            var png = await File.ReadAllBytesAsync(work, cx.Ct);
            var up = await cx.Client.UploadImageAsync(png, $"nm_cl_{i}.png", cx.Ct);
            var pid = await cx.Client.QueuePromptAsync(cx.CustomColor != null
                ? CustomWorkflow.Prepare(cx.CustomColor, up, $"nm_color_{i}").Prompt
                : WorkflowBuilder.BuildAutoColor(settings, up, $"nm_color_{i}", _refImageName), cx.Ct);
            var r = await cx.Client.WaitAsync(pid, cx.ComfyOutputDir, TimeSpan.FromMinutes(20), cx.Ct);
            if (r.OutputFiles.Count == 0)
                throw new InvalidOperationException("채색 결과 파일이 없어요.");
            work = r.OutputFiles[^1];
        }

        // ── 식자(번역문 렌더) 및 최종 저장 ──
        var outPath = pg.OutPath;   // 작업 시작 때 정한 경로 — 다시 계산하지 않는다 (1차의 마스크 사이드카와 같은 이름이어야 한다)
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
                        LetterSidecar.BasePathFor(outPath), [.. blocks], SrcLen: MaskSidecar.LengthOf(file)).Save(outPath);
                }

            TextRenderer.RenderBlocks(work, file, blocks, outPath, settings);
        }
        else if (work == file)
        {
            // 아무것도 감지·수정되지 않았다 (work가 원본 그대로) — 다시 인코딩하지 않고
            // 원본을 확장자까지 그대로 내보낸다. 손댈 게 없는데 jpg를 png로 재인코딩하면
            // 파일만 커지고 화질에도 득이 없다. 출력 폴더가 원고 폴더면 '원본 그대로'는 원본 자신이라 복사하지 않는다.
            var asIs = AsIsPathFor(outPath, file);
            Directory.CreateDirectory(Path.GetDirectoryName(asIs)!);
            if (!string.Equals(asIs, file, StringComparison.OrdinalIgnoreCase))
                ImgIo.CopyAtomic(file, asIs);   // 임시 파일 뒤 교체 + 수정 시각 지금 (결과 창이 복사 중인 파일을 읽지 않게)
            DeleteOther(outPath, asIs, file);   // 지난 실행의 png 결과가 남아 있으면 치운다 — 이 이름은 이 원고만 쓴다
            pg.FinalStage = "검열 없음 — 원본 그대로";
            progress.Report(new(name, pg.FinalStage + WarnSuffix(pg), 100, Done: true));
            return;
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
        DeleteOther(AsIsPathFor(outPath, file), outPath, file);   // 지난 실행의 '원본 그대로' 복사본이 남아 있으면 치운다

        pg.FinalStage = "완료";
        progress.Report(new(name, pg.FinalStage + WarnSuffix(pg), 100, Done: true));
    }

    private static string WarnSuffix(ReviewPage pg) => pg.Warning == null ? "" : " · ⚠ " + pg.Warning;

    /// <summary>치명적이지 않은 문제 — 행 상태에 바로 보이고, 그 장의 '완료' 줄에도 붙는다.</summary>
    private static void Warn(RunContext cx, ReviewPage pg, string message)
    {
        pg.Warning = message;
        cx.Progress.Report(new(pg.File, message, -1));   // 진행률은 그대로(-1 = 바꾸지 않음) — 48%로 되돌리면 92%까지 간 행이 뒤로 간다
    }

    private static void Report(IProgress<PageProgress> p, IReadOnlyList<string> files, string stage, double pct)
    {
        foreach (var f in files) p.Report(new(f, stage, pct));
    }

    /// <summary>ONNX 세션을 닫고 이 러너의 ComfyUI 사용을 놓는다. 결과 창 러너는 수동 작업([채색부터 다시]·Comfy 복원)마다 놓지 않고
    /// 창이 닫힐 때 여기서 놓는다 — 작업마다 놓으면 KeepComfyWarm=false에서 매번 서버를 다시 띄우느라 1~2분씩 기다린다.</summary>
    public void Dispose()
    {
        _server.Release();
        _onnx.Dispose();
    }
}
