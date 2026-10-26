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

    /// <summary>한 실행의 공유 상태 — PrepareAsync가 채우고 Stage1/Stage2가 같이 쓴다.</summary>
    private sealed class RunContext
    {
        public required IProgress<PageProgress> Progress;
        public EmbeddedTranslator? Translator;
        public required LamaInpainter Lama;
        public bool ComfyLama, ComfyRegen, ComfyCustom;
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

        await _server.DisposeAsync();
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
            await _server.EnsureRunningAsync(status, ct);
            cx.Client = new ComfyClient(_server.BaseUrl);
            cx.ComfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");

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

    // ── 검토 흐름 A: 검토 창 하나로 한꺼번에 (옛 방식, DecensorReviewMode="Window") ──
    private async Task RunWindowReviewAsync(RunContext cx, List<ReviewPage> pages, CancellationToken ct)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { await Stage1(cx, pages[i], i); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Fail(cx, pages[i], ex); }
        }
        var toReview = pages.Where(p => p.Error == null && p.Mask != null && !p.StoredConfirmed).ToList();
        if (toReview.Count > 0)
        {
            bool go = await ReviewMasks!(toReview, ct);
            if (!go) throw new OperationCanceledException("사용자가 검토 창에서 취소했어요.");
            if (settings.CollectCorrections)
                try { SaveCorrections(toReview); }
                catch (Exception ex) { cx.Progress.Report(new(toReview[0].File, $"학습 재료 저장 실패: {ex.Message}", 48)); }
            foreach (var pg in toReview) SaveMaskSidecar(cx, pg, confirmed: true, edited: pg.AutoMask != null && !MasksEqual(pg.AutoMask, pg.Mask!));
        }
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (pages[i].Error != null) continue;
            try { await Stage2(cx, pages[i], i); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Fail(cx, pages[i], ex); }
        }
    }

    // ── 검토 흐름 B: 페이지별 (2026-10-21 사용자 요청) ──
    // 1차(번역+감지)를 돌리는 생산자와 2차(제거+채색+식자)를 돌리는 소비자 큐. 감지 0·건너뜀·이미 확인된 마스크는 곧장 큐로,
    // 감지된 페이지는 사이드카(.neko)를 남기고 PageWaiting을 알린 뒤 ConfirmPage/ConfirmAll을 기다린다 — 다른 페이지 처리는 멈추지 않는다.
    private readonly Dictionary<string, (ReviewPage Page, TaskCompletionSource<bool> Tcs)> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>감지된 페이지가 검토를 기다리기 시작할 때 (러너 스레드에서 호출 — UI는 Dispatcher로 옮길 것). PerPage 방식은 이 훅이 있어야 켜진다.</summary>
    public event Action<ReviewPage>? PageWaiting;

    /// <summary>지금 검토를 기다리는 원고 파일 경로들.</summary>
    public IReadOnlyList<string> WaitingFiles { get { lock (_waiting) return _waiting.Keys.ToList(); } }

    /// <summary>검토 대기 중인 페이지를 2차로 보낸다. 결과 창이 .neko 사이드카에 저장한 마스크·건너뜀을 다시 읽어 쓴다. 대기 중이 아니면 false.</summary>
    public bool ConfirmPage(string file)
    {
        lock (_waiting)
            return _waiting.TryGetValue(file, out var w) && w.Tcs.TrySetResult(true);
    }

    /// <summary>검토 대기 중인 페이지를 전부 현재 마스크 그대로 2차로 보낸다. 보낸 수를 돌려준다.</summary>
    public int ConfirmAll()
    {
        lock (_waiting)
            return _waiting.Values.Count(w => w.Tcs.TrySetResult(true));
    }

    private async Task RunPerPageAsync(RunContext cx, List<ReviewPage> pages, CancellationToken ct)
    {
        var queue = System.Threading.Channels.Channel.CreateUnbounded<ReviewPage>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        var consumer = Task.Run(async () =>
        {
            await foreach (var pg in queue.Reader.ReadAllAsync(ct))
            {
                try { await Stage2(cx, pg, pages.IndexOf(pg)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Fail(cx, pg, ex); }
            }
        }, ct);

        var confirms = new List<Task>();
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pg = pages[i];
                try { await Stage1(cx, pg, i); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Fail(cx, pg, ex); continue; }

                int px = pg.Mask == null ? 0 : Cv2.CountNonZero(pg.Mask);
                if (px == 0 || pg.Skip || pg.StoredConfirmed)
                {
                    // 마스크가 없거나(검열 없음) 이미 확인된 마스크면 기다리지 않고 바로 2차 — 사용자 요청 "마스크 없는 페이지는 바로 넘어가게"
                    if (px == 0 && !pg.Skip) cx.Progress.Report(new(pg.File, "검열 없음 — 바로 진행", 49));
                    await queue.Writer.WriteAsync(pg, ct);
                    continue;
                }
                if (!pg.SidecarSaved)
                {
                    // 사이드카를 못 썼으면(디스크 가득·권한) 결과 창 마스크 탭이 열리지 않아 검토가 불가능하다. 검토를 켠 사용자는 오탐을 거르려는 것이니
                    // 자동 마스크로 그냥 지우지 않고 그 장을 실패로 둔다 — 원인을 고치고 다시 돌리면 된다 (검열 없는 장은 위에서 이미 통과)
                    Fail(cx, pg, new IOException("검열 마스크를 저장하지 못해 검토할 수 없어요 — 출력 폴더의 공간·권한을 확인한 뒤 다시 돌려 주세요" + (pg.SidecarError != null ? " (" + pg.SidecarError + ")" : "")));
                    continue;
                }
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_waiting) _waiting[pg.File] = (pg, tcs);
                cx.Progress.Report(new(pg.File, $"검토 대기 · {pg.Summary}", 48));
                PageWaiting?.Invoke(pg);
                confirms.Add(WaitThenEnqueueAsync(cx, pg, tcs, queue.Writer, ct));
            }
            await Task.WhenAll(confirms);
        }
        catch
        {
            // 중지·오류: 소비자가 끝날 때까지 기다린 뒤 던진다 — 2차가 쓰는 Mat을 finally가 먼저 해제하면 안 된다
            queue.Writer.TryComplete();
            try { await consumer; } catch { }
            throw;
        }
        queue.Writer.TryComplete();
        await consumer;
    }

    private async Task WaitThenEnqueueAsync(RunContext cx, ReviewPage pg, TaskCompletionSource<bool> tcs,
        System.Threading.Channels.ChannelWriter<ReviewPage> writer, CancellationToken ct)
    {
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
        lock (_waiting) _waiting.Remove(pg.File);
        ReloadMaskFromSidecar(pg);   // 결과 창의 '마스크 수정' 탭이 고쳐 저장한 마스크·건너뜀을 받아 온다
        if (settings.CollectCorrections)
            try { SaveCorrections([pg]); }
            catch (Exception ex) { cx.Progress.Report(new(pg.File, $"학습 재료 저장 실패: {ex.Message}", 48)); }
        MarkSidecarConfirmed(cx, pg);
        await writer.WriteAsync(pg, ct);
    }

    private const int RedoPageIndex = 950;

    /// <summary>결과 창에서 '채색부터 다시': 보존해 둔 검열 제거 전 원고(.pre.png)와 저장된 마스크로 이 페이지의 2차(제거→채색→식자→저장)를 다시 돌린다.</summary>
    public async Task RedoPageAsync(string file, IProgress<PageProgress> progress, CancellationToken ct, string? outPath = null)
    {
        outPath ??= OutputPathFor(file);   // 결과 창은 자기 PageRef의 OutPath를 넘긴다
        if (!string.Equals(Path.GetExtension(outPath), ".png", StringComparison.OrdinalIgnoreCase))
            outPath = Path.ChangeExtension(outPath, ".png");   // --result 모드는 보던 파일(원본 그대로 복사본 jpg)을 OutPath로 넘긴다 — 결과는 항상 png (사이드카 이름은 같다)
        var sc = MaskSidecar.Load(outPath) ?? throw new FileNotFoundException("이 페이지의 저장된 마스크가 없어요 — 먼저 한 번 돌려야 해요.");
        if (!sc.MatchesSource(file)) throw new InvalidOperationException("이 이름의 저장 마스크는 다른 원고의 것이에요 — 먼저 한 번 돌려야 해요.");
        if (!MaskSidecar.HasPre(outPath)) throw new FileNotFoundException("검열 제거 전 원고(.pre.png)가 없어요 — 먼저 한 번 돌려야 해요.");
        var cx = await PrepareAsync([file], progress, needTranslator: false, ct);
        var pg = new ReviewPage(file)
        {
            OutPath = outPath,
            Stem = Path.GetFileNameWithoutExtension(outPath),
            WorkKey = $"{RedoPageIndex}_{Path.GetFileNameWithoutExtension(outPath)}",
            WorkPath = MaskSidecar.PrePathFor(outPath),
            Mask = MaskSidecar.LoadMask(outPath),
            Skip = sc.Skip,
            Summary = sc.Summary,
            Blocks = LetterSidecar.Load(outPath) is { } lsc && lsc.BelongsTo(file, (sc.SrcW, sc.SrcH)) ? lsc.Blocks : [],
        };
        // ComfyUI 업로드 이름(nm_cl_<i> 등)은 페이지 번호로 구분된다 — 본 작업이 쓰는 0~N·수동 제거의 900번대와 겹치지 않게 950
        try { await Stage2(cx, pg, RedoPageIndex); }
        finally { pg.DisposeMats(); }
        // 확인 표시는 2차가 **성공한 뒤에만** (먼저 찍으면 OOM 등으로 실패해도 '확인됨'이 남아 다음 실행이 검토를 건너뛴다).
        // 그런데 2차가 이미 끝 줄(100%, Done)을 보낸 뒤라 실패 경고를 48%로 보고하면 행이 되돌아간다 → 2차가 적어 둔 끝 줄을 경고와 함께 다시 보낸다
        if (!MarkSidecarConfirmed(cx, pg)) progress.Report(new(pg.File, pg.FinalStage + WarnSuffix(pg), 100, Done: true));
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
        var mask = DetectCensorMask(img, exclude, out var kindUsed, autoBubbles: settings.DecensorBubbleFilter);
        pg.Summary = kindUsed;
        if (settings.DecensorTarget == "Auto")
            progress.Report(new(name, $"검열 분석: {kindUsed}", 46));
        // 검토 흐름에서는 감지가 비었어도 사용자가 칠할 수 있게 빈 마스크를 준다
        pg.Mask = mask ?? (cx.Review ? new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black) : null);
        if (cx.Review)
        {
            pg.AutoMask = pg.Mask?.Clone();   // 수정 전 사본 — '자동 감지로' 되돌리기 + 학습 재료의 '자동 감지' 쪽
            // 감지가 없어도 사이드카를 남긴다 — 나중에 결과 창의 마스크 탭에서 놓친 검열을 칠해 다시 돌릴 수 있게
            pg.SidecarSaved = SaveMaskSidecar(cx, pg, confirmed: false, edited: false, pre: img);
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

    /// <summary>이 장의 결과 자리에 쓰면 안 되는 이유 — 없으면 null.
    /// ① 결과 자리(png·'원본 그대로' 복사본)가 목록에 올린 다른 원고(전에 만든 결과물을 원고로 다시 올린 경우) ② 앞 장과 같은 결과 자리.</summary>
    private static string? ResultSlotConflict(RunContext cx, ReviewPage pg)
    {
        var name = Path.GetFileName(pg.OutPath);
        var asIs = AsIsPathFor(pg.OutPath, pg.File);
        bool hitsSource = cx.FileKeys.Contains(FileKey(pg.OutPath))
                          || (!string.Equals(FileKey(asIs), FileKey(pg.File), StringComparison.Ordinal) && cx.FileKeys.Contains(FileKey(asIs)));
        if (hitsSource) return $"결과 파일 이름({name})이 목록의 다른 원고와 같아요 — 그 원고를 덮지 않도록 이 장은 건너뛰어요.";
        if (pg.DuplicateOf != null)
            return $"결과 파일 이름({name})이 목록의 다른 원고({pg.DuplicateOf})와 같아요 — 출력 폴더를 고정해 폴더 이름이 같은 화가 섞였어요. " +
                   "따로 돌리거나 출력 폴더를 비워(원고 폴더 옆 _neko) 주세요.";
        return null;
    }

    /// <summary>같은 결과 이름의 다른 형태(png ↔ '원본 그대로' 복사본)를 지운다 — 한 원고의 결과는 둘 중 하나만 남아야 보기 규칙이 헷갈리지 않는다.
    /// 방금 쓴 파일·원본 자신은 절대 아니다.</summary>
    private static void DeleteOther(string other, string written, string source)
    {
        if (string.Equals(FileKey(other), FileKey(written), StringComparison.Ordinal) || string.Equals(FileKey(other), FileKey(source), StringComparison.Ordinal)) return;
        try { if (File.Exists(other)) File.Delete(other); } catch { }
    }

    /// <summary>v1.5.5까지는 결과 이름이 원고 이름 그대로(<c>01.png</c>)라 같은 폴더의 01.jpg·01.png가 결과·사이드카를 서로 덮었다. 지금 규칙(<see cref="OutputStemFor"/>)으로
    /// 이름이 바뀐 원고는 옛 이름 밑의 것을 **근거가 있는 만큼만** 새 이름으로 한 번 옮긴다:
    /// 마스크 사이드카(원고 = 이 원고)는 사이드카만 — 1차가 결과물보다 먼저 쓰므로 그 자리 png가 이 원고 결과라는 근거는 못 된다(검열 없음·검토 중 멈춤이면 png가 없다).
    /// 결과물(png·'원본 그대로' 복사본)은 식자 사이드카(png와 함께 쓰인다)가 이 원고 것이거나, 기본 출력 폴더(<c>&lt;원고 폴더&gt;_neko</c>)이고 원고 폴더에 그 이름의
    /// 원고가 이것 하나뿐일 때만. 출력 폴더가 원고 폴더면 결과물은 절대 옮기지 않는다(그 자리 파일이 사용자 원고일 수 있다).
    /// 새 이름 자리에 이미 있는 것은 덮지 않고, 원고 자신과 목록의 원고는 옮기지 않는다. 근거가 없는 옛 결과는 그대로 둔다(다시 돌리면 새 이름으로 만들어진다).</summary>
    private static void MigrateLegacyName(RunContext cx, ReviewPage pg)
    {
        var dir = Path.GetDirectoryName(pg.OutPath)!;
        var srcDir = Path.GetDirectoryName(pg.File) ?? "";
        var name = Path.GetFileNameWithoutExtension(pg.File);
        var legacy = Path.Combine(dir, name + ".png");
        var legacyAsIs = Path.Combine(dir, Path.GetFileName(pg.File));   // 옛 '원본 그대로' 복사본 = 출력 폴더의 원고 이름 그대로
        var asIs = AsIsPathFor(pg.OutPath, pg.File);
        bool Movable(string f) => File.Exists(f) && !cx.FileKeys.Contains(FileKey(f)) && !string.Equals(FileKey(f), FileKey(pg.File), StringComparison.Ordinal);
        bool legacyPng = !string.Equals(FileKey(legacy), FileKey(pg.OutPath), StringComparison.Ordinal);
        bool legacyCopy = !string.Equals(FileKey(legacyAsIs), FileKey(asIs), StringComparison.Ordinal) && Movable(legacyAsIs);
        if (!legacyPng && !legacyCopy) return;
        bool inSourceDir = SameDir(pg.OutPath, pg.File);
        bool defaultDir = string.Equals(NormDirKey(dir), NormDirKey(Path.Combine(srcDir, Path.GetFileName(srcDir) + "_neko")), StringComparison.Ordinal);
        try
        {
            bool resultsOurs = false;
            if (legacyPng)
            {
                var ms = MaskSidecar.Load(legacy);
                var ls = LetterSidecar.Load(legacy);
                if (ms != null && ms.MatchesSource(pg.File)) MaskSidecar.MoveLegacy(legacy, pg.OutPath);
                bool lettered = ls != null && ls.BelongsTo(pg.File);
                if (lettered) LetterSidecar.MoveLegacy(legacy, pg.OutPath);
                resultsOurs = lettered || (defaultDir && SoleSourceNamed(srcDir, name, pg.File));
                if (resultsOurs && !inSourceDir && Movable(legacy) && !File.Exists(pg.OutPath)) File.Move(legacy, pg.OutPath);
            }
            // '원본 그대로' 복사본은 원고 이름을 확장자까지 그대로 썼다 — 기본 출력 폴더(원고 폴더 하나의 결과만 받는다)에서는 이 원고 것뿐이다
            if (legacyCopy && !inSourceDir && (defaultDir || resultsOurs) && !File.Exists(asIs)) File.Move(legacyAsIs, asIs);
        }
        catch (Exception ex) { Warn(cx, pg, "옛 이름의 결과를 새 이름으로 옮기지 못했어요: " + ex.Message); }
    }

    /// <summary>원고 폴더에 <paramref name="name"/>.* 이미지가 <paramref name="file"/> 하나뿐인가 (01.jpg와 01.png가 같이 있으면 옛 01.png 결과의 주인을 모른다).</summary>
    private static bool SoleSourceNamed(string srcDir, string name, string file)
    {
        try
        {
            var same = Directory.EnumerateFiles(string.IsNullOrEmpty(srcDir) ? "." : srcDir, name + ".*")
                                .Where(f => ImgIo.IsImageFile(f) && string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                                .Take(2).ToList();
            return same.Count == 1 && string.Equals(FileKey(same[0]), FileKey(file), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string WarnSuffix(ReviewPage pg) => pg.Warning == null ? "" : " · ⚠ " + pg.Warning;

    // ── 마스크 사이드카 (.neko) ──
    /// <summary>사이드카는 편의 기능이라 못 써도 파이프라인은 계속 가지만, 실패를 삼키면 "다시 돌려도 마스크가 유지된다"는
    /// 약속이 조용히 깨진다(디스크 가득·권한·OneDrive 잠금) — 행에 바로 알리고 완료 줄에도 남긴다.</summary>
    private bool SaveMaskSidecar(RunContext cx, ReviewPage pg, bool confirmed, bool edited, Mat? pre = null)
    {
        if (pg.Mask == null) return false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (pre != null) MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, pre, pg.Summary, pg.Skip, edited, confirmed);
                else
                {
                    using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
                    MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, img.Empty() ? null : img, pg.Summary, pg.Skip, edited, confirmed);
                }
                if (MaskSidecar.HasPre(pg.OutPath)) return true;
                if (pre == null)
                {
                    // 검토 창 경로(원고를 다시 못 읽은 경우): json+mask는 저장됐고 검토도 이미 끝났다 — 결과 창 마스크 탭만 못 연다고 알리고 성공으로 둔다
                    Warn(cx, pg, "검열 제거 전 원고(.pre.png)를 쓰지 못해 결과 창 마스크 탭은 열리지 않아요");
                    return true;
                }
                // 페이지별 검토 경로: pre.png가 없으면 결과 창 마스크 탭이 열리지 않는다(검토 불가) — '저장됨'으로 치면 그 장이 풀 길 없는 대기에 걸린다
                pg.SidecarError = "검열 제거 전 원고(.pre.png)를 쓰지 못했어요";
                Warn(cx, pg, "마스크 저장 실패: " + pg.SidecarError);
                return false;
            }
            catch (IOException ex) when (attempt < 2 && (ex.HResult & 0xFFFF) is 32 or 33)   // 공유·잠금 위반만 기다릴 가치가 있다 — 디스크 가득·폴더 없음은 곧장 실패
            {
                Thread.Sleep(700);   // 결과 창 미리 읽기가 옛 .pre/.mask를 쥐고 있는 잠깐의 잠금 — 사이드카를 못 쓰면 그 장이 실패로 끝나니 한 번 더 기다려 본다
            }
            catch (Exception ex) { pg.SidecarError = ex.Message; Warn(cx, pg, "마스크 저장 실패: " + ex.Message); return false; }
        }
    }

    /// <summary>치명적이지 않은 문제 — 행 상태에 바로 보이고, 그 장의 '완료' 줄에도 붙는다.</summary>
    private static void Warn(RunContext cx, ReviewPage pg, string message)
    {
        pg.Warning = message;
        cx.Progress.Report(new(pg.File, message, -1));   // 진행률은 그대로(-1 = 바꾸지 않음) — 48%로 되돌리면 92%까지 간 행이 뒤로 간다
    }

    /// <summary>결과 창이 사이드카에 저장한 마스크·건너뜀을 페이지에 되읽는다 (크기가 같을 때만).</summary>
    private static void ReloadMaskFromSidecar(ReviewPage pg)
    {
        var sc = MaskSidecar.Load(pg.OutPath);
        if (sc == null || !sc.MatchesSource(pg.File)) return;   // 그 자리 사이드카가 다른 원고 것이면 받지 않는다
        pg.Skip = sc.Skip;
        using var m = MaskSidecar.LoadMask(pg.OutPath);
        if (m != null && pg.Mask != null && m.Size() == pg.Mask.Size()) m.CopyTo(pg.Mask);
    }

    /// <summary>사이드카에 '확인됨'을 표시한다. 실패하면 경고로 알리고 false.</summary>
    private static bool MarkSidecarConfirmed(RunContext cx, ReviewPage pg)
    {
        try { if (MaskSidecar.Load(pg.OutPath) is { } sc && !sc.Confirmed) (sc with { Confirmed = true, Skip = pg.Skip }).Save(pg.OutPath); return true; }
        catch (Exception ex) { Warn(cx, pg, "마스크 확인 표시 실패: " + ex.Message); return false; }
    }

    private static bool MasksEqual(Mat a, Mat b)
    {
        if (a.Size() != b.Size()) return false;
        using var diff = new Mat();
        Cv2.BitwiseXor(a, b, diff);
        return Cv2.CountNonZero(diff) == 0;
    }

    /// <summary>검토 창의 결과를 학습 재료로 저장한다 — 페이지마다 (원고 png, 자동 감지 마스크, 최종 마스크) 세 파일 + index.jsonl 한 줄.
    /// 자동 감지와 최종이 같고 둘 다 비었으면(검열 없는 페이지를 그냥 지나침) 깨끗 페이지 재료로도 쓸 수 있어 함께 저장한다.
    /// 경로: &lt;DataDir&gt;\training\corrections\&lt;yyyyMMdd&gt;\&lt;원본이름&gt;_&lt;HHmmss&gt;.{page,auto,mask}.png</summary>
    public static string CorrectionsDir => Path.Combine(AppSettings.DataDir, "training", "corrections");

    private void SaveCorrections(IReadOnlyList<ReviewPage> pages)
    {
        var dir = Path.Combine(CorrectionsDir, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("HHmmss");
        var lines = new List<string>();
        foreach (var pg in pages)
        {
            if (pg.Mask == null) continue;
            var stem = pg.WorkKey + "_" + stamp;   // 한 번에 여러 장을 같은 시각으로 저장한다 — 다른 폴더의 같은 이름 원고가 서로 덮지 않게 순번 포함
            using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
            if (img.Empty()) continue;
            ImgIo.Write(Path.Combine(dir, stem + ".page.png"), img);
            ImgIo.Write(Path.Combine(dir, stem + ".mask.png"), pg.Mask);
            if (pg.AutoMask != null) ImgIo.Write(Path.Combine(dir, stem + ".auto.png"), pg.AutoMask);
            int autoPx = pg.AutoMask == null ? 0 : Cv2.CountNonZero(pg.AutoMask);
            int finalPx = Cv2.CountNonZero(pg.Mask);
            int changed = 0;
            if (pg.AutoMask != null) { using var diff = new Mat(); Cv2.BitwiseXor(pg.AutoMask, pg.Mask, diff); changed = Cv2.CountNonZero(diff); }
            lines.Add($"{{\"stem\":\"{stem}\",\"source\":{System.Text.Json.JsonSerializer.Serialize(pg.File)},\"width\":{img.Width},\"height\":{img.Height}," +
                      $"\"auto_px\":{autoPx},\"final_px\":{finalPx},\"changed_px\":{changed},\"skip\":{(pg.Skip ? "true" : "false")}," +
                      $"\"summary\":{System.Text.Json.JsonSerializer.Serialize(pg.Summary)},\"model\":\"{Engine.CensorNet.ModelFile}\"}}");
        }
        if (lines.Count > 0) File.AppendAllLines(Path.Combine(dir, "index.jsonl"), lines);
    }

    /// <summary>검토 창 훅(DecensorReviewMode="Window") — 검열 제거 전에 감지된 마스크를 사용자가 확인·편집한다. null이면(헤드리스) 검토 없이 진행.
    /// 페이지 목록의 Mask(CV_8UC1, 255=지울 곳)를 제자리에서 바꾸고, 진행하면 true·취소면 false를 돌려준다.</summary>
    public Func<IReadOnlyList<ReviewPage>, CancellationToken, Task<bool>>? ReviewMasks { get; set; }

    /// <summary>검토 흐름의 페이지 상태. Mask는 러너가 만들고(감지 결과 또는 빈 마스크) 러너가 해제한다 — 검토 창은 내용만 바꾼다.</summary>
    public sealed class ReviewPage(string file)
    {
        public string File { get; } = file;
        public string Name { get; } = Path.GetFileName(file);
        /// <summary>결과물 경로 — .neko 사이드카(마스크·식자)가 이 옆에 놓인다.</summary>
        public string OutPath { get; set; } = "";
        /// <summary>결과물 이름(확장자 없이, 01.jpg·01.png가 서로 덮지 않게 원고 확장자 포함).</summary>
        public string Stem { get; set; } = Path.GetFileNameWithoutExtension(file);
        /// <summary>작업 폴더 중간 파일·학습 재료 이름 — 이번 실행에서 장마다 다르다(순번_결과 이름). 결과 이름은 출력 폴더 안에서만 유일해
        /// 한 작업 폴더를 같이 쓰는 1화\001.jpg·2화\001.jpg가 겹친다.</summary>
        public string WorkKey { get; set; } = Path.GetFileNameWithoutExtension(file);
        /// <summary>결과 자리가 앞 장과 겹치면 그 앞 장의 원고 — 이 장은 실패로 둔다.</summary>
        public string? DuplicateOf { get; set; }
        /// <summary>1차가 마스크 사이드카를 실제로 썼는지 — 못 썼으면 결과 창 마스크 탭이 열리지 않으므로 페이지별 검토를 기다리지 않는다.</summary>
        public bool SidecarSaved { get; set; }
        /// <summary>마스크 사이드카 저장이 실패한 이유 — 실패 메시지에 그대로 (Warning은 마지막 경고만 남아 엉뚱한 원인을 가리킬 수 있다).</summary>
        public string? SidecarError { get; set; }
        /// <summary>번역(글자 지움) 뒤의 원고 경로 — 검토 창이 보여 주는 그림이자 검열 제거 입력.</summary>
        public string WorkPath { get; set; } = file;
        public IReadOnlyList<TextBlockInfo> Blocks { get; set; } = Array.Empty<TextBlockInfo>();
        public Mat? Mask { get; set; }
        /// <summary>자동 감지 결과 사본(검토 전) — '자동 감지로' 되돌리기 + 학습 재료 저장용. 검토 흐름에서만 채워진다.</summary>
        public Mat? AutoMask { get; set; }
        public string Summary { get; set; } = "";
        /// <summary>사용자가 '이 페이지는 검열 없음'으로 표시 — 제거를 건너뛴다.</summary>
        public bool Skip { get; set; }
        /// <summary>.neko에서 읽은 마스크가 이미 확인된(제거를 돌렸던) 것 — 검토를 다시 기다리지 않는다.</summary>
        public bool StoredConfirmed { get; set; }
        public string? Error { get; set; }
        /// <summary>치명적이지 않은 문제(사이드카 저장 실패 등) — '완료' 줄에 함께 보인다.</summary>
        public string? Warning { get; set; }
        /// <summary>2차가 행에 보고한 끝 줄("완료"/"검열 없음 — 원본 그대로", 경고 제외) — 2차 뒤에 경고가 생기면 같은 줄을 다시 보내는 데 쓴다.</summary>
        public string FinalStage { get; set; } = "완료";

        public void DisposeMats() { Mask?.Dispose(); Mask = null; AutoMask?.Dispose(); AutoMask = null; }
    }

    /// <summary>검열 마스크 — AI 전용 모델(CensorNet) 1순위, 규칙 기반 폴백.
    /// AI 모델은 무검열 원고에 검열을 합성해 학습한 세그멘테이션이라 흰/검/모자이크를
    /// 한 번에, 장식 줄무늬·효과음과 구분해 찾는다. 모델 파일이 없으면 기존 규칙으로.</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, bool autoBubbles = false)
    {
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
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct, AppSettings? regenSettings = null)
    {
        switch (mode)
        {
            case "ComfyCustom":
            {
                var wf = CustomWorkflow.Load(settings.CustomDecensorWorkflow);
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
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

    public string OutputPathFor(string inputFile) => OutputPathFor(settings, inputFile);

    /// <summary>원고 → 결과물 경로 (러너 없이도 계산 가능 — 메인 창·결과 창이 결과물과 .neko 사이드카 위치를 알아야 할 때).
    /// 이름은 **원고 하나만 보고** 정한다(<see cref="OutputStemFor"/>) — 목록에 무엇이 같이 있는지와 무관해서, 장을 넣고 빼도 이름이 바뀌지 않는다.</summary>
    public static string OutputPathFor(AppSettings s, string inputFile) =>
        Path.Combine(OutputDirFor(s, inputFile), OutputStemFor(s, inputFile) + ".png");

    public static string OutputDirFor(AppSettings s, string inputFile) =>
        !string.IsNullOrWhiteSpace(s.OutputRoot)
            ? s.OutputRoot
            : Path.Combine(Path.GetDirectoryName(inputFile)!, Path.GetFileName(Path.GetDirectoryName(inputFile)!) + "_neko");

    /// <summary>결과물 이름(확장자 없이) — 결과는 늘 .png라 원고 확장자를 이름에 남겨 같은 폴더의 01.jpg·01.png·01.webp가 서로 덮지 않게 한다:
    /// png 원고는 이름 그대로(<c>01</c>, 단 출력 폴더가 원고 폴더면 원본을 덮지 않게 <c>01_png</c>), 그 밖의 원고는 <c>이름_확장자</c>(<c>01_jpg</c>),
    /// 출력 폴더를 고정했으면 앞에 원고의 상위 폴더 이름(<c>vol1_01_jpg</c> — 여러 화의 001.jpg가 한 폴더로 모인다).
    /// v1.5.5까지는 원고 이름 그대로였다 — 옛 결과는 1차가 한 번 새 이름으로 옮긴다(<c>MigrateLegacyName</c>).</summary>
    public static string OutputStemFor(AppSettings s, string inputFile)
    {
        var stem = Path.GetFileNameWithoutExtension(inputFile);
        var ext = Path.GetExtension(inputFile).TrimStart('.').ToLowerInvariant();
        bool sameDir = string.Equals(NormDirKey(OutputDirFor(s, inputFile)), NormDirKey(Path.GetDirectoryName(inputFile) ?? ""), StringComparison.Ordinal);
        if (ext != "png" || sameDir) stem += "_" + (ext.Length > 0 ? ext : "src");
        if (!string.IsNullOrWhiteSpace(s.OutputRoot))
        {
            string parent; try { parent = Path.GetFileName(Path.GetFullPath(Path.GetDirectoryName(inputFile) ?? "")); } catch { parent = ""; }
            if (parent.Length > 0) stem = parent + "_" + stem;
        }
        return stem;
    }

    /// <summary>두 파일이 같은 폴더에 있는가 (전체 경로·끝 구분자·대소문자 무시).</summary>
    private static bool SameDir(string a, string b) =>
        string.Equals(NormDirKey(Path.GetDirectoryName(a) ?? ""), NormDirKey(Path.GetDirectoryName(b) ?? ""), StringComparison.Ordinal);

    private static string NormDirKey(string d)
    {
        try { d = Path.GetFullPath(string.IsNullOrEmpty(d) ? "." : d); } catch { }
        return d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    /// <summary>경로 비교 키 — 전체 경로 소문자.</summary>
    public static string FileKey(string file)
    {
        try { return Path.GetFullPath(file).ToLowerInvariant(); } catch { return file.ToLowerInvariant(); }
    }

    /// <summary>이 원고의 결과물로 보여 줄 파일: 결과물(.png) → 원본 그대로 복사본 → 원본. 메인 창·결과 창이 같은 규칙을 쓴다.
    /// 2차는 둘 중 하나를 쓰면 다른 하나를 지우지만, 지우기가 막혀 둘 다 남았으면 더 나중에 쓰인 쪽.</summary>
    public static string ResolveResultPath(string outPath, string source)
    {
        var asIs = AsIsPathFor(outPath, source);
        bool hasOut = File.Exists(outPath);
        bool hasAsIs = !string.Equals(asIs, source, StringComparison.OrdinalIgnoreCase) && !string.Equals(asIs, outPath, StringComparison.OrdinalIgnoreCase) && File.Exists(asIs);
        if (!hasOut && !hasAsIs) return source;
        if (hasOut && hasAsIs) return File.GetLastWriteTimeUtc(asIs) > File.GetLastWriteTimeUtc(outPath) ? asIs : outPath;
        return hasOut ? outPath : asIs;
    }

    /// <summary>검열이 없어 원본을 확장자 그대로 내보낼 때의 경로 — 결과물과 같은 이름에 원본 확장자. 출력 폴더가 원고 폴더면 원본 자신(복사하지 않는다).</summary>
    public static string AsIsPathFor(string outPath, string inputFile)
    {
        if (SameDir(outPath, inputFile)) return inputFile;
        var ext = Path.GetExtension(inputFile);
        return Path.ChangeExtension(outPath, string.IsNullOrEmpty(ext) ? null : ext);   // 확장자 없는 원고는 "01." 이 아니라 "01"
    }

    private static void Report(IProgress<PageProgress> p, IReadOnlyList<string> files, string stage, double pct)
    {
        foreach (var f in files) p.Report(new(f, stage, pct));
    }

    public void Dispose() => _onnx.Dispose();
}
