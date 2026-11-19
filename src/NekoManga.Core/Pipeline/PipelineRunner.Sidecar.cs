using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 마스크 사이드카(.neko) — 저장·되읽기·확인 표시, 그리고 검토 결과를 학습 재료로 저장
public partial class PipelineRunner
{
    // ── 마스크 사이드카 (.neko) ──
    /// <summary>사이드카는 편의 기능이라 못 써도 파이프라인은 계속 가지만, 실패를 삼키면 "다시 돌려도 마스크가 유지된다"는
    /// 약속이 조용히 깨진다(디스크 가득·권한·OneDrive 잠금) — 행에 바로 알리고 완료 줄에도 남긴다.</summary>
    private bool SaveMaskSidecar(RunContext cx, ReviewPage pg, bool confirmed, bool edited, Mat? pre = null, bool freshDetection = false)
    {
        if (pg.Mask == null) return false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                MaskSidecar written;
                if (pre != null) written = MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, pre, pg.Summary, pg.Skip, edited, confirmed, pg.Kinds, freshDetection);
                else
                {
                    using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
                    written = MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, img.Empty() ? null : img, pg.Summary, pg.Skip, edited, confirmed);
                }
                pg.MaskSavedAt = written.SavedAt;   // 이 장이 쓰는 마스크는 지금 쓴 사이드카의 것 — 그 뒤에 결과 창이 또 저장했는지 2차가 이 시각으로 가린다
                if (MaskSidecar.HasPre(pg.OutPath)) return true;
                if (pre == null)
                {
                    // 검토 창 경로(원고를 다시 못 읽은 경우): json+mask는 저장됐고 검토도 이미 끝났다 — 결과 창 마스크 탭만 못 연다고 알리고 성공으로 둔다
                    Warn(cx, pg, Loc.T("검열 제거 전 원고(.pre.png)를 쓰지 못해 결과 창 마스크 탭은 열리지 않는다냥"));
                    return true;
                }
                // 페이지별 검토 경로: pre.png가 없으면 결과 창 마스크 탭이 열리지 않는다(검토 불가) — '저장됨'으로 치면 그 장이 풀 길 없는 대기에 걸린다
                pg.SidecarError = Loc.T("검열 제거 전 원고(.pre.png)를 쓰지 못했다냥");
                Warn(cx, pg, Loc.T("마스크 저장 실패다냥: ") + pg.SidecarError);
                return false;
            }
            catch (IOException ex) when (attempt < 2 && (ex.HResult & 0xFFFF) is 32 or 33)   // 공유·잠금 위반만 기다릴 가치가 있다 — 디스크 가득·폴더 없음은 곧장 실패
            {
                Thread.Sleep(700);   // 결과 창 미리 읽기가 옛 .pre/.mask를 쥐고 있는 잠깐의 잠금 — 사이드카를 못 쓰면 그 장이 실패로 끝나니 한 번 더 기다려 본다
            }
            catch (Exception ex) { pg.SidecarError = ex.Message; Warn(cx, pg, Loc.T("마스크 저장 실패다냥: ") + ex.Message); return false; }
        }
    }

    /// <summary>사이드카가 지울 곳을 약속하는데('검열 없음' 표시가 아닌데) 마스크 PNG를 그림으로 못 읽었다(0바이트·깨진 PNG) — 결과 창 [채색부터 다시]와 페이지별 검토의 확인이 같이 쓴다.</summary>
    private static string StoredMaskUnreadable => Loc.T("저장된 마스크(.mask.png)를 읽지 못했다냥 — 마스크 탭에서 다시 저장하거나 처음부터 다시 돌려 줘냥.");

    /// <summary>사이드카 json이 있는데 깨져서(0바이트·쓰다 끊김) 못 읽었다 — 사용자가 무엇을 저장했는지('검열 없음' 표시·고친 마스크) 알 수 없다.
    /// 다시 돌리면 1차가 새로 감지해 사이드카를 다시 쓴다.</summary>
    private static string StoredSidecarUnreadable => Loc.T("저장된 마스크 정보(.mask.json)를 읽지 못했다냥 — 처음부터 다시 돌려 줘냥.");

    /// <summary>사이드카 파일을 다른 프로그램이 끝내 쥐고 있어 못 읽었다 — 영어 OS 문구 대신 무엇을 못 읽었고 어떻게 하면 되는지.</summary>
    private static string StoredMaskLocked(string path) =>
        Loc.F($"저장된 마스크(.neko 폴더의 {Path.GetFileName(path)})를 다른 프로그램이 쥐고 있어 읽지 못했다냥 — 잠시 뒤 다시 돌려 줘냥.");

    /// <summary>결과 창이 사이드카에 저장한 마스크·건너뜀을 페이지에 되읽는다 (크기가 같을 때만).
    /// 마스크 PNG가 있는데 그림으로 못 읽으면 던진다 — 메모리의 자동 감지 마스크로 그냥 지우면 사용자가 지운 오탐 자리까지 칠하고 '확인됨'이 찍힌다.
    /// json이 있는데 못 읽어도(잠김·깨짐) 던진다 — '사이드카 없음'으로 넘기면 같은 일이 난다. 사이드카가 정말 없을 때만 메모리의 마스크로 간다.</summary>
    private static void ReloadMaskFromSidecar(ReviewPage pg)
    {
        var sc = LoadSidecarWaitingOutLock(pg.OutPath, brokenThrows: true);
        if (sc == null || !sc.MatchesSource(pg.File)) return;   // 그 자리 사이드카가 다른 원고 것이면 받지 않는다
        pg.Skip = sc.Skip;
        using var m = LoadMaskWaitingOutLock(pg.OutPath);
        // 파일 자체가 없으면(그사이 지워짐) 전처럼 메모리의 마스크로 간다
        if (m == null && !sc.Skip && File.Exists(MaskSidecar.MaskPathFor(pg.OutPath))) throw new InvalidDataException(StoredMaskUnreadable);
        if (m != null && pg.Mask != null && m.Size() == pg.Mask.Size()) m.CopyTo(pg.Mask);
        pg.MaskSavedAt = sc.SavedAt;
    }

    /// <summary>2차를 시작할 때: 이 장의 마스크를 읽은 뒤에 결과 창이 사이드카를 또 저장했으면(저장 시각이 다르다) 다시 읽는다.
    /// 러너는 확인 때 한 번만 사이드카를 읽었다 — [검열 제거 시작] 뒤 차례를 기다리는 장(재생성은 장마다 몇 분)을 결과 창에서 고쳐 저장하면
    /// '저장했어요'라 하고 옛 마스크로 돌았고, 끝난 장이 아니어서 '고친 장' 표시도 없었다(2026-11-13 점검 4번).
    /// 사이드카를 못 읽으면(잠김) 들고 있는 마스크로 간다 — 고친 것이었으면 끝에서 표시가 켜진다(<see cref="FinishMaskSidecar"/>).
    /// 읽었는데 마스크 PNG가 깨졌으면 확인 때처럼 던진다(그 장의 실패).</summary>
    private static void RefreshMaskIfSavedSince(ReviewPage pg)
    {
        if (pg.MaskSavedAt is not { } readAt) return;   // 사이드카 없이 도는 장(검토 흐름이 아니고 저장 마스크도 안 쓴 장)
        if (MaskSidecar.Load(pg.OutPath) is not { } sc || sc.SavedAt == readAt || !sc.MatchesSource(pg.File)) return;
        ReloadMaskFromSidecar(pg);
    }

    /// <summary>2차가 결과물을 쓴 **뒤에** 그 장의 마스크 사이드카를 맞춘다(취소·실패한 장은 건드리지 않는다 — 표시가 남아 [고친 장 모두 다시 렌더링]에 다시 모인다).
    /// ① '고쳤지만 아직 결과물에 반영 전'(<see cref="MaskSidecar.Pending"/>): 이 장이 읽은 마스크가 지금 사이드카의 것이면(저장 시각이 같다) 반영됐으니 지운다.
    ///    저장 시각이 다르면 2차가 도는 사이 결과 창이 마스크를 또 저장한 것이다 — 이번 결과물은 옛 마스크로 만들었으니 표시를 **켠다**(이미 켜져 있으면 그대로).
    ///    결과 창은 끝나지 않은 장을 저장할 때 표시를 켜지 않는다(끝난 장만 다시 렌더링할 수 있어서) — 그 장이 방금 끝났으니 여기서 켠다.
    ///    검열 제거를 끈 실행은 표시를 그대로 둔다 — 결과물을 새로 썼어도 마스크는 쓰지 않았다.
    /// ② 채색 여부(<see cref="MaskSidecar.Colorized"/>): 이 결과물이 채색된 그림인지 — 다시 렌더링이 그림으로 추측하지 않게.
    /// ③ 재생성 시드(<see cref="MaskSidecar.RegenSeeds"/>): 이번에 재생성이 돌았으면 쓴 시드를 영역 순서대로 — 결과 창이 보여 주고, 사용자가 고정하면 다음에도 그 시드로 돈다(2026-11-15).
    /// json이 잠겨 있으면(백신 검사·동기화) 다른 읽기처럼 기다렸다 다시 읽는다. 끝내 못 읽거나 못 쓰면 그 장을 실패로 두지는 않고(결과물은 이미 맞게 쓰였다) 경고로 알린다:
    /// 표시가 남으면 그 장을 한 번 더 렌더링할 뿐이고, 채색 여부·시드는 결과물을 쓰기 전에 '모름'으로 내려 두어(<see cref="ForgetStaleRecords"/>) 틀린 값이 남지 않는다 — 모르면 그림으로 가린다.
    /// 알고 둔 틈: 읽고 쓰는 사이(수 ms)에 결과 창이 마스크를 저장하면 그 저장의 시각·표시가 옛 레코드로 덮인다(마스크 PNG는 새것) — 옛 ClearMaskPending에도 있던 틈이다.</summary>
    private void FinishMaskSidecar(RunContext cx, ReviewPage pg, bool colorized)
    {
        try
        {
            if (LoadSidecarWaitingOutLock(pg.OutPath, brokenThrows: false) is not { } sc || !sc.MatchesSource(pg.File)) return;
            var next = FinishedRecord(sc, colorized, settings.EnableDecensor ? pg.MaskSavedAt : null, pg.RegenSeeds);
            if (next != sc) next.Save(pg.OutPath);
            if (DrawnWithOtherSeeds(sc, pg.RegenSeeds))
                Warn(cx, pg, Loc.T("이 장의 시드 고정은 그리기 시작한 뒤에 켜져 이번에는 안 쓰였다냥 — 다시 렌더링하면 고정한 시드로 그린다냥"));
        }
        catch (Exception ex)
        {
            Warn(cx, pg, Loc.T("마스크 정보(.mask.json)를 갱신하지 못했다냥 — 결과물에는 영향이 없지만 '고친 장' 표시와 시드 기록이 어긋날 수 있다냥: ") + ex.Message);
        }
    }

    /// <summary>2차가 끝난 장의 사이드카 레코드 — <see cref="FinishMaskSidecar"/>의 규칙 그 자체(파일은 건드리지 않는다).
    /// <paramref name="maskReadAt"/>: 이 장이 읽은 마스크의 저장 시각. 검열 제거를 끈 실행·사이드카 없이 돈 장은 null — '고친 장' 표시를 그대로 둔다.
    /// <paramref name="regenSeeds"/>: 이번에 재생성이 쓴 시드(영역 순서). 재생성이 돌지 않았으면 null — 적힌 것은 '지난 재생성의 시드'로 남는다
    /// (고정한 장이 기본 복원으로 한 번 돌았다고 시드를 잃지 않는다).
    /// **고정한 장의 시드는 사용자가 고른 것이라 여기서도 바꾸지 않는다**(2026-11-15 검수): 영역이 늘어 새로 뽑은 시드만 뒤에 덧붙인다(앞이 고정한 시드와 같을 때).
    /// 영역이 줄었으면 남은 시드를 버리지 않고, 앞이 다르면(그리기 시작한 뒤에 고정했다 — <see cref="DrawnWithOtherSeeds"/>) 고른 시드를 그대로 둔다:
    /// 이번에 뽑아 쓴 시드로 덮으면 고정이 아무도 고르지 않은 시드로 옮겨 간다. 시드 없이 고정 표시만 남은 기록은 표시를 지운다.</summary>
    internal static MaskSidecar FinishedRecord(MaskSidecar sc, bool colorized, DateTime? maskReadAt, IReadOnlyList<long>? regenSeeds)
    {
        var next = sc with { Colorized = colorized };
        if (maskReadAt is { } readAt) next = next with { Pending = sc.SavedAt != readAt };
        IReadOnlyList<long> locked = sc.SeedLock ? MaskSidecar.ParseSeeds(sc.RegenSeeds) : [];
        if (sc.SeedLock && locked.Count == 0) next = next with { SeedLock = false };
        if (regenSeeds is { Count: > 0 } && (locked.Count == 0 || (regenSeeds.Count > locked.Count && SamePrefix(locked, regenSeeds))))
            next = next with { RegenSeeds = MaskSidecar.FormatSeeds(regenSeeds) };
        return next;
    }

    /// <summary>이 장은 시드를 고정해 두었는데 이번 결과물은 그 시드로 그려지지 않았는가 — 재생성이 시작한 뒤에(차례가 와서 도는 중에) 결과 창에서 고정을 켠 경우다.
    /// 영역 수만 다른 것(줄었거나 늘었다)은 아니다. 2차가 경고로 알린다: 고정은 남고, 다시 렌더링하면 그 시드로 그린다.</summary>
    internal static bool DrawnWithOtherSeeds(MaskSidecar sc, IReadOnlyList<long>? regenSeeds) =>
        sc.SeedLock && regenSeeds is { Count: > 0 } && MaskSidecar.ParseSeeds(sc.RegenSeeds) is { Count: > 0 } locked && !SamePrefix(locked, regenSeeds);

    /// <summary>두 시드 목록이 짧은 쪽 길이만큼 앞에서부터 같은가.</summary>
    private static bool SamePrefix(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>이 장의 시드를 고정해 두었으면(<see cref="MaskSidecar.SeedLock"/> — 결과 창 '이 장의 시드 고정') 적어 둔 시드를 영역 순서대로, 아니면 null.
    /// 2차가 재생성 직전에 읽는다 — 차례를 기다리는 사이에 고정하거나 풀어도 따라간다. json이 잠겨 있으면(백신 검사·동기화) 다른 읽기처럼 기다렸다 다시 읽고,
    /// 끝내 못 읽으면 던진다(그 장의 실패): '고정 안 함'으로 치면 고정해 둔 장의 결과물을 새 시드의 그림으로 말없이 덮는다. 깨진 json·사이드카 없는 장은 고정 없음.</summary>
    internal static IReadOnlyList<long>? LockedSeedsFor(string outPath, string file) =>
        LoadSidecarWaitingOutLock(outPath, brokenThrows: false) is { SeedLock: true } sc && sc.MatchesSource(file)
            && MaskSidecar.ParseSeeds(sc.RegenSeeds) is { Count: > 0 } seeds ? seeds : null;

    /// <summary>사용자가 이 장의 검열 조각에 고른 성별(<see cref="MaskSidecar.RegenSex"/> — 결과 창의 ♂/♀ 표시)을 지금 그리는 그림의 좌표로. 없으면 빈 목록.
    /// 표시는 마스크(원본) 좌표로 적혀 있다 — 채색된 장의 2차는 더 큰 그림에서 돌아 <paramref name="drawn"/>(그 그림의 크기)으로 옮긴다.
    /// 시드 고정처럼 2차가 재생성 직전에 읽는다(차례를 기다리는 사이에 고른 것도 따라간다). json이 잠겨 있으면 기다렸다 다시 읽고, 끝내 못 읽으면 던진다(그 장의 실패):
    /// '표시 없음'으로 치면 사용자가 남성으로 고른 조각이 말없이 남녀 태그로 그려진다. 깨진 json·사이드카 없는 장·다른 원고의 사이드카는 표시 없음.</summary>
    internal static IReadOnlyList<SexMark> SexMarksFor(string outPath, string file, Size drawn) =>
        LoadSidecarWaitingOutLock(outPath, brokenThrows: false) is { RegenSex: { Length: > 0 } text, SrcW: > 0, SrcH: > 0 } sc && sc.MatchesSource(file)
            ? SexMarks.Scaled(SexMarks.Parse(text), (double)drawn.Width / sc.SrcW, (double)drawn.Height / sc.SrcH) : [];

    /// <summary>결과물을 쓰기 **직전**: 사이드카에 적힌 채색 여부·재생성 시드가 이번 결과와 다르면 '모름'(null)으로 내려 둔다(<see cref="StaleCleared"/>).
    /// 적는 것은 결과물을 쓴 뒤다(<see cref="FinishMaskSidecar"/>) — 그것이 실패하거나(잠김) 그 전에 앱이 꺼지면 지난 실행의 값이 새 결과물 옆에 남는다:
    /// 채색을 끄고 돌린 기록(false)이 채색본 옆에 남으면 [지우기]가 pre에서 시작해 채색본을 흑백으로 덮는다(2026-11-14 검수). 모름이면 그림으로 가린다.
    /// 지난 시드가 남으면 결과 창이 이 그림의 시드가 아닌 것을 보여 주고, 그것을 고정하면 다른 그림이 나온다.</summary>
    private static void ForgetStaleRecords(ReviewPage pg, bool colorized)
    {
        try
        {
            if (MaskSidecar.Load(pg.OutPath) is { } sc && sc.MatchesSource(pg.File) && StaleCleared(sc, colorized, pg.RegenSeeds) is var next && next != sc)
                next.Save(pg.OutPath);
        }
        catch { }
    }

    /// <summary><see cref="ForgetStaleRecords"/>의 규칙 — 적힌 채색 여부가 이번과 다르면 모름으로, 이번에 재생성이 돌았고 적힌 시드가 그 시드가 아니면 기록 없음으로.
    /// 재생성이 돌지 않았으면(<paramref name="regenSeeds"/> null) 시드는 그대로 둔다. **고정한 장의 시드는 내리지 않는다** — 고정은 '이 결과물의 시드'가 아니라 사용자가 고른 시드다.
    /// 내리면 뒤의 기록이 실패했을 때 고정 표시만 남고, 결과 창은 체크를 풀 수도 없으며(시드가 없어 꺼져 있다) 다음에 뽑힌 시드가 고정된다(2026-11-15 검수).</summary>
    internal static MaskSidecar StaleCleared(MaskSidecar sc, bool colorized, IReadOnlyList<long>? regenSeeds)
    {
        var next = sc.Colorized is { } recorded && recorded != colorized ? sc with { Colorized = null } : sc;
        bool locked = sc.SeedLock && MaskSidecar.ParseSeeds(sc.RegenSeeds).Count > 0;
        if (!locked && regenSeeds is { Count: > 0 } && next.RegenSeeds != null && next.RegenSeeds != MaskSidecar.FormatSeeds(regenSeeds)) next = next with { RegenSeeds = null };
        return next;
    }

    /// <summary>식자 json을 읽되 '있는데 못 읽음'을 '없음'과 가른다 — <see cref="LetterSidecar.LoadRecord"/>는 잠김·깨짐도 null로 삼킨다.
    /// 결과 창은 [지우기]·묶음 직전에 식자 json을 새로 쓴다. 그 파일을 백신·동기화가 잠깐 쥐었을 때 '식자 없는 장'으로 치면 번역 글도 색도 없는 그림으로 결과물을 덮는다(2026-11-14 검수).
    /// 잠겨 있으면 마스크 사이드카처럼 두 번까지 기다렸다 다시 읽고, 그래도 잠겨 있거나 깨졌으면 던진다. json이 정말 없으면 null.</summary>
    private static LetterSidecar? LoadLetterRecordWaitingOutLock(string outPath)
    {
        var json = LetterSidecar.JsonPathFor(outPath);
        for (int attempt = 0; ; attempt++)
        {
            if (LetterSidecar.LoadRecord(outPath) is { } rec) return rec;
            if (!File.Exists(json)) return null;
            if (!IsLocked(json))
            {
                // 잠겨 있지 않은데 못 읽었다 — 읽는 사이에 풀렸을 수 있어 한 번 더 읽어 보고, 그래도 안 읽히면 깨진 json이다
                if (LetterSidecar.LoadRecord(outPath) is { } unlocked) return unlocked;
                if (!File.Exists(json)) return null;
                if (!IsLocked(json))
                    throw new InvalidDataException(Loc.F($"식자 정보(.neko 폴더의 {Path.GetFileName(json)})를 읽지 못했다냥(파일이 깨졌다냥) — 번역 글이 빠진 결과물로 덮지 않도록 멈췄다냥. ") +
                                                   Loc.T("[작업 시작]으로 이 장을 처음부터 다시 돌려 줘냥."));
            }
            if (attempt >= 2)
                throw new IOException(Loc.F($"식자 정보(.neko 폴더의 {Path.GetFileName(json)})를 다른 프로그램이 쥐고 있어 읽지 못했다냥 — ") +
                                      Loc.T("번역 글이 빠진 결과물로 덮지 않도록 멈췄다냥. 잠시 뒤 다시 해 줘냥."));
            Thread.Sleep(700);
        }
    }

    /// <summary>마스크 PNG를 읽되 공유·잠금 위반이면 두 번까지 기다렸다 다시 읽는다(<see cref="SaveMaskSidecar"/>와 같은 요령) — 결과 창이 막 저장한 마스크를
    /// 백신·동기화가 잠깐 쥐고 있으면 확인한 장이 그 때문에 실패한다. 그래도 잠겨 있으면 던진다(<see cref="StoredMaskLocked"/>).
    /// 페이지별 검토의 확인, 다시 돌릴 때의 저장 마스크 재사용, 결과 창 [채색부터 다시]가 같이 쓴다.</summary>
    private static Mat? LoadMaskWaitingOutLock(string outPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return MaskSidecar.LoadMask(outPath); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                if (attempt >= 2) throw new IOException(StoredMaskLocked(MaskSidecar.MaskPathFor(outPath)), ex);
                Thread.Sleep(700);
            }
        }
    }

    /// <summary>마스크 사이드카(json)를 읽되 '있는데 못 읽음'을 '사이드카 없음'과 가른다 — <see cref="MaskSidecar.Load"/>는 잠김·깨짐도 null로 삼켜,
    /// 결과 창이 막 저장한 json을 백신·동기화가 잠깐 쥐고 있으면 사용자가 고친 마스크·'검열 없음' 표시를 없는 것으로 치고 자동 감지 마스크로 지우러 갔다.
    /// 잠겨 있으면 마스크 PNG처럼 두 번까지 기다렸다 다시 읽고, 그래도 잠겨 있으면 던진다. 깨진 json은 <paramref name="brokenThrows"/>면 던지고,
    /// 아니면 null이다(다시 돌릴 때의 1차는 그때 새로 감지한다). json이나 마스크 PNG가 정말 없으면 null.</summary>
    internal static MaskSidecar? LoadSidecarWaitingOutLock(string outPath, bool brokenThrows)
    {
        var json = MaskSidecar.JsonPathFor(outPath);
        for (int attempt = 0; ; attempt++)
        {
            if (MaskSidecar.Load(outPath) is { } sc) return sc;
            if (!File.Exists(json) || !File.Exists(MaskSidecar.MaskPathFor(outPath))) return null;
            if (!IsLocked(json))
            {
                // 잠겨 있지 않은데 못 읽었다 — 읽는 사이에 풀렸을 수 있어 한 번 더 읽어 보고, 그래도 안 읽히면 깨진 json이다
                if (MaskSidecar.Load(outPath) is { } unlocked) return unlocked;
                if (!IsLocked(json))
                {
                    if (brokenThrows) throw new InvalidDataException(StoredSidecarUnreadable);
                    return null;
                }
            }
            if (attempt >= 2) throw new IOException(StoredMaskLocked(json));
            Thread.Sleep(700);
        }
    }

    /// <summary>사이드카에 '확인됨'을 표시한다. 실패하면 경고로 알리고 false.
    /// json이 잠겨 있으면(백신 검사·동기화) 읽기가 '사이드카 없음'과 똑같이 null이라 표시를 경고 없이 건너뛰고 true를 돌려줬다 — 다음 실행이 까닭 없이 검토를 한 번 더 기다렸다.
    /// 다른 읽기처럼 두 번까지 기다렸다 다시 읽고(<see cref="LoadSidecarWaitingOutLock"/>), 그래도 잠겨 있으면 경고하고 false.
    /// 그 경고는 '잠시 뒤 다시 돌려 주세요'(<see cref="StoredMaskLocked"/>)가 아니라 무슨 일이 생기는지로 — 이 장은 실패한 것이 아니다.
    /// 사이드카가 정말 없으면(검토 흐름이 아닌 장) 할 일이 없다 — 조용히 true. 깨진 json도 전처럼 조용히 넘긴다(다음 실행의 1차가 새로 감지해 다시 쓴다).
    /// 이 장이 읽어 온 사이드카가 이미 '확인됨'이었으면(<see cref="ReviewPage.StoredConfirmed"/> — 끝난 장의 [채색부터 다시]) 바꿀 것이 없으니 읽지도 않는다:
    /// 그때 json이 잠겨 있으면 1.4초를 기다린 끝에 '다음에 검토를 한 번 더 기다려요'라는 틀린 경고를 냈다(이미 확인된 것이라 기다리지 않는다).</summary>
    private static bool MarkSidecarConfirmed(RunContext cx, ReviewPage pg)
    {
        if (pg.StoredConfirmed) return true;
        try
        {
            MaskSidecar? sc;
            try { sc = LoadSidecarWaitingOutLock(pg.OutPath, brokenThrows: false); }
            catch (IOException)
            {
                Warn(cx, pg, Loc.F($"마스크 확인 표시 실패다냥: 저장된 마스크 정보(.neko 폴더의 {Path.GetFileName(MaskSidecar.JsonPathFor(pg.OutPath))})를 다른 프로그램이 쥐고 있다냥 — ") +
                             Loc.T("이번 결과에는 영향이 없지만, 다음에 다시 돌리면 이 장의 검토를 한 번 더 기다린다냥."));
                return false;
            }
            if (sc is { Confirmed: false }) (sc with { Confirmed = true, Skip = pg.Skip }).Save(pg.OutPath);
            return true;
        }
        catch (Exception ex) { Warn(cx, pg, Loc.T("마스크 확인 표시 실패다냥: ") + ex.Message); return false; }
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
}
