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
            if (!go) throw new OperationCanceledException(Loc.T("사용자가 검토 창에서 취소했어요."));
            if (settings.CollectCorrections)
                try { SaveCorrections(toReview); }
                catch (Exception ex) { cx.Progress.Report(new(toReview[0].File, Loc.F($"학습 재료 저장 실패: {ex.Message}"), 48)); }
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
    // 확인된 장은 '확인 줄'에 확인한 순서대로([전부 진행]은 쪽 순서로) 들어가 한 장씩 확인 뒤 일(마스크 다시 읽기·학습 재료·확인 표시)을 하고 2차 큐로 넘어간다.
    // 전에는 장마다 따로 기다리다 한꺼번에 풀려 확인 뒤 일이 스레드풀에서 겹쳐 돌았고, 먼저 끝난 장이 먼저 2차로 갔다(실측 2026-11-13: 4장을 전부 진행하니 5→4→2→3쪽 순).
    private sealed record WaitingPage(ReviewPage Page, int Index, IProgress<PageProgress> Progress,
        System.Threading.Channels.ChannelWriter<ReviewPage> Confirmed, CancellationToken Ct);
    private readonly Dictionary<string, WaitingPage> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>확인을 받고 2차 차례를 기다리는 장의 상태 글 — 목록의 행이 '검토 대기 (더블클릭해 수정)'로 남지 않게(실측 2026-11-13: 마지막 장은 6분 반 동안 그대로였다).</summary>
    public static string QueuedStage => Loc.T("검열 제거 차례 대기");

    /// <summary>감지된 페이지가 검토를 기다리기 시작할 때 (러너 스레드에서 호출 — UI는 Dispatcher로 옮길 것). PerPage 방식은 이 훅이 있어야 켜진다.</summary>
    public event Action<ReviewPage>? PageWaiting;

    /// <summary>지금 검토를 기다리는 원고 파일 경로들.</summary>
    public IReadOnlyList<string> WaitingFiles { get { lock (_waiting) return _waiting.Keys.ToList(); } }

    /// <summary>검토 대기 중인 페이지를 2차로 보낸다. 결과 창이 .neko 사이드카에 저장한 마스크·건너뜀을 다시 읽어 쓴다. 대기 중이 아니면 false.</summary>
    public bool ConfirmPage(string file)
    {
        WaitingPage? w;
        lock (_waiting)
            if (!_waiting.Remove(file, out w)) return false;
        return SendConfirmed(w);
    }

    /// <summary>검토 대기 중인 페이지를 전부 현재 마스크 그대로 2차로 보낸다 — 쪽 순서대로. 보낸 수를 돌려준다.</summary>
    public int ConfirmAll()
    {
        List<WaitingPage> all;
        lock (_waiting)
        {
            // 대기 목록의 나열 순서는 쪽 순서가 아닐 수 있다 — 한 장을 먼저 진행시킨 뒤 대기에 들어온 장은 그 빈 자리를 쓴다
            all = _waiting.Values.OrderBy(w => w.Index).ToList();
            _waiting.Clear();
        }
        return all.Count(SendConfirmed);
    }

    /// <summary>대기 목록에서 뺀 장을 확인 줄에 넣는다. 멈추는 중이거나 확인 줄이 닫혔으면(오류로 작업이 끝나는 중) false.</summary>
    private static bool SendConfirmed(WaitingPage w)
    {
        // [멈추기] 뒤에는 받지 않는다 — 확인 줄은 1차가 돌던 감지를 마치고 취소를 알아챌 때까지 열려 있어, 받으면 '보냈어요'라 하고 행을 '차례 대기'로 바꾼 채 아무 일도 안 한다
        if (w.Ct.IsCancellationRequested) return false;
        // 보고는 줄에 넣기 전에(잠금 밖에서) — 넣자마자 2차가 시작돼 그 보고보다 늦게 가면 도는 장의 행이 '차례 대기'로 남는다.
        // 보고가 터져도 줄에는 넣는다: 대기 목록에서 이미 뺀 장이라 여기서 잃으면 다시 확인할 길이 없고 작업이 끝나지 않는다
        try { w.Progress.Report(new(w.Page.File, QueuedStage, 49)); }
        catch { }
        return w.Confirmed.TryWrite(w.Page);
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

        // 확인 줄: 확인된 장을 받은 순서대로 하나씩 2차 큐로 넘긴다. open = 생산자 몫 1 + 검토 대기에 들어간 장마다 1 — 0이 되면 더 올 확인이 없으니 줄을 닫는다
        var confirmed = System.Threading.Channels.Channel.CreateUnbounded<ReviewPage>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        int open = 1;
        void CloseOne() { if (Interlocked.Decrement(ref open) == 0) confirmed.Writer.TryComplete(); }
        var confirmer = Task.Run(async () =>
        {
            await foreach (var pg in confirmed.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();   // 줄에 남은 장은 멈춘 뒤에 확인 표시를 남기지 않는다(ReadAllAsync는 쌓인 것을 다 꺼낼 때까지 취소를 보지 않는다)
                await EnqueueConfirmedAsync(cx, pg, queue.Writer, ct);
                CloseOne();
            }
        }, ct);

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
                    if (px == 0 && !pg.Skip) cx.Progress.Report(new(pg.File, Loc.T("검열 없음 — 바로 진행"), 49));
                    await queue.Writer.WriteAsync(pg, ct);
                    continue;
                }
                if (!pg.SidecarSaved)
                {
                    // 사이드카를 못 썼으면(디스크 가득·권한) 결과 창 마스크 탭이 열리지 않아 검토가 불가능하다. 검토를 켠 사용자는 오탐을 거르려는 것이니
                    // 자동 마스크로 그냥 지우지 않고 그 장을 실패로 둔다 — 원인을 고치고 다시 돌리면 된다 (검열 없는 장은 위에서 이미 통과)
                    Fail(cx, pg, new IOException(Loc.T("검열 마스크를 저장하지 못해 검토할 수 없어요 — 출력 폴더의 공간·권한을 확인한 뒤 다시 돌려 주세요") + (pg.SidecarError != null ? " (" + pg.SidecarError + ")" : "")));
                    continue;
                }
                Interlocked.Increment(ref open);
                // '검토 대기' 보고는 대기 목록에 넣기 전에 — 넣자마자 온 확인의 '차례 대기' 보고보다 늦게 가면 행이 다시 '검토 대기'로 남는다
                cx.Progress.Report(new(pg.File, Loc.F($"검토 대기 · {pg.Summary}"), 48, ReviewSummary: pg.Summary ?? ""));   // null이면(손으로 고친 사이드카) 화면이 '검토 대기'인 줄 모른다
                lock (_waiting) _waiting[pg.File] = new(pg, i, cx.Progress, confirmed.Writer, ct);
                PageWaiting?.Invoke(pg);
            }
            CloseOne();
            await confirmer;
        }
        catch
        {
            // 중지·오류: 확인 줄과 소비자가 끝날 때까지 기다린 뒤 던진다 — 둘이 쓰는 Mat을 finally가 먼저 해제하면 안 된다
            confirmed.Writer.TryComplete();
            try { await confirmer; } catch { }
            queue.Writer.TryComplete();
            try { await consumer; } catch { }
            throw;
        }
        queue.Writer.TryComplete();
        await consumer;
    }

    /// <summary>확인된 장 하나를 2차 큐로 — 결과 창이 저장한 마스크를 다시 읽고 학습 재료·확인 표시를 남긴다. 못 하면 그 장만 실패.</summary>
    private async Task EnqueueConfirmedAsync(RunContext cx, ReviewPage pg,
        System.Threading.Channels.ChannelWriter<ReviewPage> writer, CancellationToken ct)
    {
        try
        {
            ReloadMaskFromSidecar(pg);   // 결과 창의 '마스크 수정' 탭이 고쳐 저장한 마스크·건너뜀을 받아 온다
            if (settings.CollectCorrections)
                try { SaveCorrections([pg]); }
                catch (Exception ex) { cx.Progress.Report(new(pg.File, Loc.F($"학습 재료 저장 실패: {ex.Message}"), 48)); }
            MarkSidecarConfirmed(cx, pg);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // 그 장만 실패로 — 밖으로 새면 확인 줄이 깨져 작업 전체가 실패로 끝나고, 이 장은 끝 줄 없이 '차례 대기'로 남는다
        catch (Exception ex) { Fail(cx, pg, ex); return; }
        await writer.WriteAsync(pg, ct);
    }

    private const int RedoPageIndex = 950;

    /// <summary>결과 창에서 '채색부터 다시': 보존해 둔 검열 제거 전 원고(.pre.png)와 저장된 마스크로 이 페이지의 2차(제거→채색→식자→저장)를 다시 돌린다.</summary>
    public Task RedoPageAsync(string file, IProgress<PageProgress> progress, CancellationToken ct, string? outPath = null, RedoPageOptions? options = null) =>
        RedoPageAsync(file, progress, ct, outPath, options, shared: null, seq: 0);

    /// <param name="shared">묶음(RedoPagesAsync)이 미리 한 번 준비해 둔 실행 상태 — 있으면 이 장을 위해 다시 준비하지 않는다. <paramref name="seq"/>는 묶음 안의 순번(작업 파일 이름이 겹치지 않게).</param>
    private async Task RedoPageAsync(string file, IProgress<PageProgress> progress, CancellationToken ct, string? outPath, RedoPageOptions? options, RunContext? shared, int seq)
    {
        options ??= new RedoPageOptions();
        outPath ??= OutputPathFor(file);   // 결과 창은 자기 PageRef의 OutPath를 넘긴다
        if (!string.Equals(Path.GetExtension(outPath), ".png", StringComparison.OrdinalIgnoreCase))
            outPath = Path.ChangeExtension(outPath, ".png");   // --result 모드는 보던 파일(원본 그대로 복사본 jpg)을 OutPath로 넘긴다 — 결과는 항상 png (사이드카 이름은 같다)
        // json이 잠겨 있으면(결과 창이 막 저장한 것을 백신·동기화가 쥠) 기다렸다 다시 읽고, 끝내 못 읽으면 잠김을 알린다 — '없어요'가 아니다
        var sc = LoadSidecarWaitingOutLock(outPath, brokenThrows: false) ?? throw new FileNotFoundException(Loc.T("이 페이지의 저장된 마스크가 없어요 — 먼저 한 번 돌려야 해요."));
        if (!sc.MatchesSource(file)) throw new InvalidOperationException(Loc.T("이 이름의 저장 마스크는 다른 원고의 것이에요 — 먼저 한 번 돌려야 해요."));
        if (!MaskSidecar.HasPre(outPath)) throw new FileNotFoundException(Loc.T("검열 제거 전 원고(.pre.png)가 없어요 — 먼저 한 번 돌려야 해요."));
        // 마스크는 준비(ComfyUI 기동 등) 전에 읽어 본다 — 사이드카가 지울 곳을 약속하는데('검열 없음' 표시가 아닌데) 그림으로 못 읽으면(0바이트·깨진 PNG)
        // 2차가 검열 제거를 건너뛰고 검열 제거 전 원고로 결과물을 덮은 뒤 '확인됨'까지 찍는다. 검열 제거를 끈 다시 돌리기는 마스크를 쓰지 않는다
        var mask = LoadMaskWaitingOutLock(outPath);
        if (mask == null && settings.EnableDecensor && !sc.Skip) throw new InvalidDataException(StoredMaskUnreadable);
        if (mask != null && options.Grow > 0) { var grown = Grown(mask, options.Grow); mask.Dispose(); mask = grown; }   // 결과 창의 여유(px) — 저장 마스크는 그대로 두고 지울 때만 부풀린다
        LetterSidecar? lsc;
        string workPath;
        bool colorized = false;
        try   // 여기서 던지면 마스크를 놓는다 — 페이지가 아직 없어 아래 finally가 놓지 못한다(묶음에서 실패한 장마다 원고 크기의 네이티브 메모리가 남았다)
        {
            // 식자 json은 '있는데 못 읽음'(잠김·깨짐)을 '없음'과 갈라 읽는다 — 못 읽은 것을 식자 없는 장으로 치지 않는다(LoadLetterRecordWaitingOutLock)
            var letterRecord = LoadLetterRecordWaitingOutLock(outPath);
            lsc = letterRecord != null && LetterSidecar.Load(outPath) is { } l && l.BelongsTo(file, (sc.SrcW, sc.SrcH)) ? l : null;
            // 이 원고의 식자 json에 보일 글이 있는데 식자 전 원고(base)를 못 찾았다(지워짐·못 읽는 자리) — 그대로 돌면 번역 글이 빠진 결과물로 덮고 '완료'라고 한다. 멈춘다.
            // (폴더를 옮긴 경우는 LetterSidecar.Load가 그 폴더의 base를 찾는다. 글이 전부 숨김이면 잃을 것이 없어 막지 않는다)
            if (lsc == null && letterRecord != null && letterRecord.BelongsTo(file, (sc.SrcW, sc.SrcH)) && letterRecord.Blocks.Any(b => !b.Hidden))
                throw new FileNotFoundException(Loc.F($"식자 전 원고(.neko 폴더의 {Path.GetFileName(LetterSidecar.BasePathFor(outPath))})를 찾지 못했어요 — ") +
                                                Loc.T("이대로 다시 렌더링하면 번역 글이 빠진 결과물로 덮여서 멈췄어요. [작업 시작]으로 이 장을 처음부터 다시 돌려 주세요."));
            // 결과 창 [지우기]·묶음 다시 렌더링(채색 없이 검열 제거·식자만): 채색된 장은 채색 뒤 그림에서 지워야 색을 잃지 않는다 — 식자한 장은 base(채색 뒤·식자 전),
            // 글자가 없어 식자 사이드카 없이 끝난 장은 결과물 자신이 그 그림이다(번역을 끄고 채색만 쓴 장이 전부 여기 — 전에는 pre에서 시작해 결과물을 흑백으로 덮었다, 2026-11-13 점검 1번).
            // 채색 안 한 장은 pre에서 — 잘못 지운 곳도 되살아난다. 채색했는지는 2차가 사이드카에 적어 둔 것을 따르고, 적힌 것이 없는 옛 결과만 그림으로 가린다(StartsFromColored).
            // 식자 json이 있는데 못 쓰는 경우(남의 것·글이 전부 숨김인데 base가 없음)는 결과물에 글이 구워져 있을 수 있어 후보로 삼지 않는다 — pre에서.
            workPath = MaskSidecar.PrePathFor(outPath);
            if (options.DecensorAndLetterOnly)
            {
                bool letterJson = File.Exists(LetterSidecar.JsonPathFor(outPath));
                var colored = lsc?.BasePath ?? (!letterJson && File.Exists(outPath) ? outPath : null);
                if (colored != null && StartsFromColored(sc, workPath, colored, mask)) { workPath = colored; colorized = true; }
                // 적힌 것은 '채색됨'인데 식자 자료가 있으면서 못 쓴다(남의 것, 글이 전부 숨김인데 base가 없음) — pre에서 시작하면 채색본을 흑백으로 덮는다. 멈춘다.
                // (식자 자료가 아예 없고 결과물만 지워진 경우는 시작할 그림이 pre뿐이라 그대로 간다)
                else if (colored == null && letterJson && sc.Colorized == true)
                    throw new FileNotFoundException(Loc.T("채색된 장인데 채색 뒤 그림(식자 전 원고 .base.png)을 쓸 수 없어요 — 흑백으로 덮지 않도록 멈췄어요. [작업 시작]으로 이 장을 처음부터 다시 돌려 주세요."));
            }
        }
        catch { mask?.Dispose(); throw; }
        RunContext cx;
        if (shared != null) cx = shared;
        else
            try { cx = await PrepareAsync([file], progress, needTranslator: false, ct); }
            catch { mask?.Dispose(); throw; }   // 페이지가 아직 없어 아래 finally가 놓지 못한다
        var pg = new ReviewPage(file)
        {
            OutPath = outPath,
            Stem = Path.GetFileNameWithoutExtension(outPath),
            // 묶음은 작업 폴더 하나를 같이 쓴다 — 다른 폴더의 같은 이름 원고(1화\001.jpg·2화\001.jpg)가 서로 덮지 않게 순번을 넣는다
            WorkKey = shared == null ? $"{RedoPageIndex}_{Path.GetFileNameWithoutExtension(outPath)}" : $"{RedoPageIndex}_{seq:D3}_{Path.GetFileNameWithoutExtension(outPath)}",
            WorkPath = workPath,
            Mask = mask,
            Kinds = MaskSidecar.LoadKinds(outPath),
            Skip = sc.Skip,
            StoredConfirmed = sc.Confirmed,   // 끝난 장을 다시 돌리는 것이면 이미 '확인됨'이다 — 끝에서 표시를 다시 찍지 않는다(MarkSidecarConfirmed)
            MaskSavedAt = sc.SavedAt,
            Summary = sc.Summary,
            Blocks = lsc?.Blocks ?? [],
            LetterCommon = lsc?.CommonSize ?? 0,
            Colorized = colorized,
        };
        // ComfyUI 업로드 이름(nm_cl_<i> 등)은 페이지 번호로 구분된다 — 본 작업이 쓰는 0~N·수동 제거의 900번대와 겹치지 않게 950
        try { await Stage2(cx, pg, RedoPageIndex); }
        finally
        {
            pg.DisposeMats();
            if (shared == null) WorkFolders.Remove(cx.WorkDir);   // 이 장을 위해 만든 작업 폴더(묶음의 것은 묶음이 끝날 때 지운다)
        }
        // 확인 표시는 2차가 **성공한 뒤에만** (먼저 찍으면 OOM 등으로 실패해도 '확인됨'이 남아 다음 실행이 검토를 건너뛴다).
        // 그런데 2차가 이미 끝 줄(100%, Done)을 보낸 뒤라 실패 경고를 48%로 보고하면 행이 되돌아간다 → 2차가 적어 둔 끝 줄을 경고와 함께 다시 보낸다
        if (!MarkSidecarConfirmed(cx, pg)) progress.Report(new(pg.File, pg.FinalStage + WarnSuffix(pg), 100, Done: true));
    }

    /// <summary>채색 없이 다시 렌더링할 때 채색 뒤 그림(<paramref name="coloredPath"/>)에서 시작할까 — 사이드카에 적힌 채색 여부, 없으면 두 그림을 읽어 가린다(<see cref="IsColorized"/>).
    /// 그림을 못 읽으면 pre에서(false). 적힌 것이 '채색됨'인데 그 그림이 깨졌으면 true 그대로 — 2차가 그 그림을 읽다 그 장의 실패로 멈춘다(흑백으로 덮지 않는다).</summary>
    private static bool StartsFromColored(MaskSidecar sc, string prePath, string coloredPath, Mat? mask)
    {
        if (sc.Colorized is { } recorded) return recorded;
        using var preImg = ImgIo.Read(prePath, ImreadModes.Color);
        using var colored = ImgIo.Read(coloredPath, ImreadModes.Color);
        return !preImg.Empty() && !colored.Empty() && LooksColorized(preImg, colored, mask);
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
        /// <summary>1차가 마스크 사이드카를 실제로 썼는지 — 못 썼으면 결과 창 마스크 탭이 열리지 않으므로 페이지별 검토를 기다리지 않는다.</summary>
        public bool SidecarSaved { get; set; }
        /// <summary>마스크 사이드카 저장이 실패한 이유 — 실패 메시지에 그대로 (Warning은 마지막 경고만 남아 엉뚱한 원인을 가리킬 수 있다).</summary>
        public string? SidecarError { get; set; }
        /// <summary>번역(글자 지움) 뒤의 원고 경로 — 검토 창이 보여 주는 그림이자 검열 제거 입력.</summary>
        public string WorkPath { get; set; } = file;
        public IReadOnlyList<TextBlockInfo> Blocks { get; set; } = Array.Empty<TextBlockInfo>();
        /// <summary>결과 창이 붙든 쪽 공통 글자 크기(식자 사이드카의 CommonSize) — 다시 렌더링의 식자도 이 값으로 해야 손대지 않은 풍선의 크기가 그대로다. 0 = 구한다.</summary>
        public double LetterCommon { get; set; }
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
        /// <summary>이 장의 마스크를 읽어 온 사이드카의 저장 시각 — 2차가 결과물을 쓴 뒤 '고친 마스크 대기'(MaskSidecar.Pending) 표시를 지울 때,
        /// 그사이 결과 창이 마스크를 또 저장하지 않았는지 가린다(RefreshMaskIfSavedSince·FinishMaskSidecar). 사이드카 없이 도는 장(검토 흐름이 아닌 새 감지)만 null이다 —
        /// 러너가 사이드카를 새로 쓴 장은 그때 쓴 시각이다.</summary>
        public DateTime? MaskSavedAt { get; set; }
        /// <summary>작업 그림(WorkPath)이 이미 채색된 그림이다 — 채색 없이 다시 렌더링하는 길이 채색 뒤 그림에서 시작했을 때. 2차가 채색을 돌리지 않아도 결과물은 채색된 그림이라
        /// 사이드카에 그렇게 다시 적는다(FinishMaskSidecar).</summary>
        public bool Colorized { get; set; }
        /// <summary>이번 2차에서 재생성(SDXL)이 쓴 시드 — 영역 순서대로. 재생성이 돌지 않았으면 null. 결과물을 쓴 뒤 사이드카에 적는다(FinishMaskSidecar).</summary>
        public IReadOnlyList<long>? RegenSeeds { get; set; }
        public string? Error { get; set; }
        /// <summary>치명적이지 않은 문제(사이드카 저장 실패 등) — '완료' 줄에 함께 보인다.</summary>
        public string? Warning { get; set; }
        /// <summary>2차가 행에 보고한 끝 줄("완료"/"검열 없음 — 원본 그대로", 경고 제외) — 2차 뒤에 경고가 생기면 같은 줄을 다시 보내는 데 쓴다.</summary>
        public string FinalStage { get; set; } = Loc.T("완료");

        public void DisposeMats() { Mask?.Dispose(); Mask = null; AutoMask?.Dispose(); AutoMask = null; Kinds?.Dispose(); Kinds = null; }
    }
}
