using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 검토 흐름 — 검토 창 방식, 페이지별 방식(검토 대기·확인), 결과 창의 '채색부터 다시'
// 검토 창 훅(ReviewMasks)과 페이지 상태(ReviewPage)도 여기 둔다
public partial class PipelineRunner
{
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
            Kinds = MaskSidecar.LoadKinds(outPath),
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
        /// <summary>종류 지도(CensorKind, CV_8UC1) — 자동 감지가 어느 엔진으로 그 자리를 찾았는지. 기본 제거가 성분마다 맞는 모델을 고르는 데 쓴다. 러너가 해제한다.</summary>
        public Mat? Kinds { get; set; }
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

        public void DisposeMats() { Mask?.Dispose(); Mask = null; AutoMask?.Dispose(); AutoMask = null; Kinds?.Dispose(); Kinds = null; }
    }
}
