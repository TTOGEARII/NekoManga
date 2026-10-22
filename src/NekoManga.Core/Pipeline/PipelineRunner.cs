using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

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
        public CancellationToken Ct;
        /// <summary>검토 흐름 — 감지가 비어도 사용자가 칠할 수 있게 빈 마스크를 만들고, 마스크 사이드카를 남긴다.</summary>
        public bool Review;
        /// <summary>페이지별 검토 흐름 (검토 대기 알림은 RunPerPageAsync가 한다).</summary>
        public bool PerPage;
    }

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var cx = await PrepareAsync(files, progress, needTranslator: settings.EnableTranslate, ct);
        var pages = files.Select(f => new ReviewPage(f) { OutPath = OutputPathFor(f) }).ToList();

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
                    catch (OperationCanceledException) { throw; }
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
        cx.Progress.Report(new(pg.Name, "실패", 100, Done: true, Error: ex.Message));
    }

    // ── 검토 흐름 A: 검토 창 하나로 한꺼번에 (옛 방식, DecensorReviewMode="Window") ──
    private async Task RunWindowReviewAsync(RunContext cx, List<ReviewPage> pages, CancellationToken ct)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { await Stage1(cx, pages[i], i); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Fail(cx, pages[i], ex); }
        }
        var toReview = pages.Where(p => p.Error == null && p.Mask != null && !p.StoredConfirmed).ToList();
        if (toReview.Count > 0)
        {
            bool go = await ReviewMasks!(toReview, ct);
            if (!go) throw new OperationCanceledException("사용자가 검토 창에서 취소했어요.");
            if (settings.CollectCorrections)
                try { SaveCorrections(toReview); }
                catch (Exception ex) { cx.Progress.Report(new(toReview[0].Name, $"학습 재료 저장 실패: {ex.Message}", 48)); }
            foreach (var pg in toReview) SaveMaskSidecar(pg, confirmed: true, edited: pg.AutoMask != null && !MasksEqual(pg.AutoMask, pg.Mask!));
        }
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (pages[i].Error != null) continue;
            try { await Stage2(cx, pages[i], i); }
            catch (OperationCanceledException) { throw; }
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
                catch (OperationCanceledException) { throw; }
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
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Fail(cx, pg, ex); continue; }

                int px = pg.Mask == null ? 0 : Cv2.CountNonZero(pg.Mask);
                if (px == 0 || pg.Skip || pg.StoredConfirmed)
                {
                    // 마스크가 없거나(검열 없음) 이미 확인된 마스크면 기다리지 않고 바로 2차 — 사용자 요청 "마스크 없는 페이지는 바로 넘어가게"
                    if (px == 0 && !pg.Skip) cx.Progress.Report(new(pg.Name, "검열 없음 — 바로 진행", 49));
                    await queue.Writer.WriteAsync(pg, ct);
                    continue;
                }
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_waiting) _waiting[pg.File] = (pg, tcs);
                cx.Progress.Report(new(pg.Name, $"검토 대기 · {pg.Summary}", 48));
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
            catch (Exception ex) { cx.Progress.Report(new(pg.Name, $"학습 재료 저장 실패: {ex.Message}", 48)); }
        MarkSidecarConfirmed(pg);
        await writer.WriteAsync(pg, ct);
    }

    /// <summary>결과 창에서 '채색부터 다시': 보존해 둔 검열 제거 전 원고(.pre.png)와 저장된 마스크로 이 페이지의 2차(제거→채색→식자→저장)를 다시 돌린다.</summary>
    public async Task RedoPageAsync(string file, IProgress<PageProgress> progress, CancellationToken ct)
    {
        var outPath = OutputPathFor(file);
        var sc = MaskSidecar.Load(outPath) ?? throw new FileNotFoundException("이 페이지의 저장된 마스크가 없어요 — 먼저 한 번 돌려야 해요.");
        if (!MaskSidecar.HasPre(outPath)) throw new FileNotFoundException("검열 제거 전 원고(.pre.png)가 없어요 — 먼저 한 번 돌려야 해요.");
        var cx = await PrepareAsync([file], progress, needTranslator: false, ct);
        var pg = new ReviewPage(file)
        {
            OutPath = outPath,
            WorkPath = MaskSidecar.PrePathFor(outPath),
            Mask = MaskSidecar.LoadMask(outPath),
            Skip = sc.Skip,
            Summary = sc.Summary,
            Blocks = LetterSidecar.Load(outPath)?.Blocks ?? [],
        };
        try { await Stage2(cx, pg, 0); }
        finally { pg.DisposeMats(); }
        MarkSidecarConfirmed(pg);
    }

    // ── 1차: 번역 + 검열 감지 (마스크는 페이지가 들고 있다 — 검토 창/마스크 탭이 편집한다) ──
    private async Task Stage1(RunContext cx, ReviewPage pg, int i)
    {
        var name = pg.Name;
        var progress = cx.Progress;
        if (cx.Translator != null)
        {
            var status = new Progress<string>(m => progress.Report(new(name, m, 25)));
            var tr = await cx.Translator.TranslatePageAsync(pg.WorkPath, cx.WorkDir, status, cx.Ct);
            pg.WorkPath = tr.InpaintedPath;
            pg.Blocks = tr.Blocks;
        }
        if (!settings.EnableDecensor) return;
        using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);

        // 같은 원고를 다시 돌릴 때: .neko에 남은 마스크(사용자가 고친 것 포함)가 있으면 감지 대신 쓴다 — 고친 마스크가 재실행에도 유지된다
        if (settings.ReuseStoredMasks && MaskSidecar.Load(pg.OutPath) is { } stored
            && stored.SrcW == img.Width && stored.SrcH == img.Height && MaskSidecar.LoadMask(pg.OutPath) is { } storedMask)
        {
            pg.Mask = storedMask;
            pg.AutoMask = MaskSidecar.LoadAuto(pg.OutPath);
            pg.Skip = stored.Skip;
            pg.StoredConfirmed = stored.Confirmed;
            pg.Summary = "저장된 마스크" + (stored.Edited ? " (수정됨)" : "") + $" {Cv2.CountNonZero(storedMask):N0}px";
            progress.Report(new(name, pg.Skip ? "저장된 마스크 — 검열 없음으로 표시됨" : $"저장된 마스크 사용{(stored.Edited ? " (수정됨)" : "")}", 46));
            if (cx.Review) ImgIo.Write(MaskSidecar.PrePathFor(pg.OutPath), img);   // 원고는 이번 번역 결과로 갱신
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
            SaveMaskSidecar(pg, confirmed: false, edited: false, pre: img);
            if (!cx.PerPage) progress.Report(new(name, mask == null ? "검토 대기 (감지 없음)" : "검토 대기", 48));   // 페이지별은 호출부가 대기/진행을 알린다
        }
    }

    // ── 2차: 검열 제거 + 채색 + 식자/저장 ──
    private async Task Stage2(RunContext cx, ReviewPage pg, int i)
    {
        var name = pg.Name; var file = pg.File; var work = pg.WorkPath; var blocks = pg.Blocks;
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
            work = Path.Combine(cx.WorkDir, Path.GetFileNameWithoutExtension(file) + ".decensored.png");
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

        progress.Report(new(name, "완료", 100, Done: true));
    }

    // ── 마스크 사이드카 (.neko) ──
    private void SaveMaskSidecar(ReviewPage pg, bool confirmed, bool edited, Mat? pre = null)
    {
        if (pg.Mask == null) return;
        try
        {
            if (pre != null) { MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, pre, pg.Summary, pg.Skip, edited, confirmed); return; }
            using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
            MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, img.Empty() ? null : img, pg.Summary, pg.Skip, edited, confirmed);
        }
        catch { /* 사이드카는 편의 기능 — 못 써도 파이프라인은 계속 간다 */ }
    }

    /// <summary>결과 창이 사이드카에 저장한 마스크·건너뜀을 페이지에 되읽는다 (크기가 같을 때만).</summary>
    private static void ReloadMaskFromSidecar(ReviewPage pg)
    {
        var sc = MaskSidecar.Load(pg.OutPath);
        if (sc == null) return;
        pg.Skip = sc.Skip;
        using var m = MaskSidecar.LoadMask(pg.OutPath);
        if (m != null && pg.Mask != null && m.Size() == pg.Mask.Size()) m.CopyTo(pg.Mask);
    }

    private static void MarkSidecarConfirmed(ReviewPage pg)
    {
        try { if (MaskSidecar.Load(pg.OutPath) is { } sc && !sc.Confirmed) (sc with { Confirmed = true, Skip = pg.Skip }).Save(pg.OutPath); }
        catch { }
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
            var stem = Path.GetFileNameWithoutExtension(pg.File) + "_" + stamp;
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

    /// <summary>원고 → 결과물 경로 (러너 없이도 계산 가능 — 결과 창이 .neko 사이드카 위치를 알아야 할 때).</summary>
    public static string OutputPathFor(AppSettings s, string inputFile)
    {
        var dir = !string.IsNullOrWhiteSpace(s.OutputRoot)
            ? s.OutputRoot
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
