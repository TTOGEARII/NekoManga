using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 결과물 경로 — 이름·폴더 규칙, 결과 자리 충돌 확인과 정리, 옛 이름으로 남은 결과 옮기기
public partial class PipelineRunner
{
    /// <summary>이 장의 결과 자리에 쓰면 안 되는 이유 — 없으면 null이고, 이 장이 그 자리를 차지한다(<see cref="RunContext.TakenSlots"/>).
    /// ① 결과 자리(png·'원본 그대로' 복사본)가 목록에 올린 다른 원고(전에 만든 결과물을 원고로 다시 올린 경우) ② 이번 실행의 앞 장이 이미 차지한 결과 자리
    /// ③ 그 자리에 이미 있는 사이드카가 다른 원고의 것(<see cref="SidecarConflict"/>) — 출력 폴더를 고정하면 A\vol1\01.jpg·B\vol1\01.jpg가
    /// 같은 이름이라, 따로 돌리면 ②가 못 잡아 나중 원고가 앞 원고의 결과·사이드카를 조용히 덮었다. 이름 규칙은 그대로 두고 덮기 전에 멈춘다.
    /// ②는 이 검사를 통과한 장만 자리를 차지한 것으로 본다 — 목록 순서만으로 정하면 [B, A](자리의 사이드카 주인 A가 뒤)에서 B는 ③, A는 ②로 막혀
    /// 아무도 쓰지 않았는데 주인도 못 돌았다.</summary>
    private static string? ResultSlotConflict(RunContext cx, ReviewPage pg)
    {
        var name = Path.GetFileName(pg.OutPath);
        var asIs = AsIsPathFor(pg.OutPath, pg.File);
        bool hitsSource = cx.FileKeys.Contains(FileKey(pg.OutPath))
                          || (!string.Equals(FileKey(asIs), FileKey(pg.File), StringComparison.Ordinal) && cx.FileKeys.Contains(FileKey(asIs)));
        if (hitsSource) return $"결과 파일 이름({name})이 목록의 다른 원고와 같아요 — 그 원고를 덮지 않도록 이 장은 건너뛰어요.";
        var slot = FileKey(pg.OutPath);
        if (cx.TakenSlots.TryGetValue(slot, out var first))
        {
            // 출력 폴더 탓은 고정했고 두 원고의 폴더가 다를 때만 — 같은 폴더의 01.jpg·01_jpg.png는 어느 출력 폴더에서나(기본 _neko에서도) 같은 자리다
            if (!string.IsNullOrWhiteSpace(cx.PathSettings?.OutputRoot) && !SameDir(first, pg.File))
                return $"결과 파일 이름({name})이 목록의 다른 원고({first})와 같아요 — 출력 폴더를 고정해 폴더 이름이 같은 화가 섞였어요. " +
                       "출력 폴더를 바꿔 따로 돌리거나 비워(원고 폴더 옆 _neko) 주세요.";   // 같은 출력 폴더로 따로 돌리면 ③에 막히거나, 사이드카가 없는 장은 덮인다
            return $"결과 파일 이름({name})이 같은 폴더의 다른 원고({Path.GetFileName(first)})와 같아요 — 그 결과를 덮지 않도록 이 장은 건너뛰어요. " +
                   "둘 중 하나의 파일 이름을 바꿔 주세요.";
        }
        if (SidecarConflict(cx, pg) is { } why) return why;
        cx.TakenSlots[slot] = pg.File;
        return null;
    }

    /// <summary>③ 결과 자리의 사이드카가 다른 원고의 것이면(<see cref="OtherSlotOwner"/>) 또는 잠겨 있어 주인을 확인하지 못했으면 그 안내 — 아니면 null.
    /// 안내는 상황에 맞춘다: 덮일 것은 실제 결과 파일 이름('원본 그대로' 복사본이면 그 이름)으로, 결과 파일 없이 사이드카만 있으면 '저장된 마스크·식자 자료'로.
    /// '출력 폴더를 바꾸거나 비워'는 출력 폴더를 고정했을 때만 — 기본 _neko에서 걸리는 것은 폴더를 복사한 뒤(원래 폴더가 남아 있다) 원고를 다시 저장했거나 옛 이름 자료라 그 말이 소용없다.
    /// 같은 원고 폴더의 같은 이름 원고(01.jpg·01.png)가 주인이면 v1.5.5 옛 이름(<c>&lt;이름&gt;.png</c>)으로 남은 그 원고의 자료다 — 지우라고 하면 아직 옮기지 않은
    /// 그 원고의 고친 마스크가 사라지므로, 그 원고를 먼저 돌려 새 이름으로 옮기게 한다(<see cref="MigrateLegacyName"/> — 새 이름 자리에 사이드카가 이미 있으면 옮기지 않아 그때는 일반 안내).</summary>
    private static string? SidecarConflict(RunContext cx, ReviewPage pg)
    {
        var owner = OtherSlotOwner(pg, SlotFollowsSource(cx.PathSettings), out var ownerLen, out var locked);
        if (owner == null)
            return locked == null ? null
                : $"결과 자리의 사이드카(.neko 폴더의 {Path.GetFileName(locked)})를 다른 프로그램이 쥐고 있어(백신 검사·동기화 등) 누구의 결과인지 확인하지 못했어요 — " +
                  "덮지 않도록 이 장은 건너뛰어요. 잠시 뒤 다시 돌려 주세요.";
        // 옛 이름 자료의 주인 — v1.5.5 뒤에 폴더를 옮겼으면 적힌 경로는 옛 경로라, 이 폴더의 그 이름 원고를 사이드카처럼 경로 또는 이름+길이로 가린다
        // (옛 이름 옮기기도 같은 판정으로 그 원고의 자료를 옮긴다)
        var sibling = Path.Combine(Path.GetDirectoryName(pg.File) ?? "", Path.GetFileName(owner));
        bool legacySibling = !string.Equals(FileKey(sibling), FileKey(pg.File), StringComparison.Ordinal)
                             && string.Equals(Path.GetFileNameWithoutExtension(sibling), Path.GetFileNameWithoutExtension(pg.File), StringComparison.OrdinalIgnoreCase)
                             && File.Exists(sibling)
                             && (string.Equals(FileKey(sibling), FileKey(owner), StringComparison.Ordinal) || (ownerLen > 0 && MaskSidecar.LengthOf(sibling) == ownerLen));
        if (legacySibling) owner = sibling;
        var result = ResolveResultPath(pg.OutPath, owner);   // 그 원고의 결과물(png → '원본 그대로' 복사본) — 둘 다 없으면 원고 경로가 돌아온다
        bool hasResult = !string.Equals(FileKey(result), FileKey(owner), StringComparison.Ordinal);
        var msg = hasResult
            ? $"결과 파일 이름({Path.GetFileName(result)})이 전에 돌린 다른 원고({owner})의 결과와 같아요 — 그 결과를 덮지 않도록 이 장은 건너뛰어요."
            : $"결과 이름({Path.GetFileNameWithoutExtension(pg.OutPath)})으로 저장된 마스크·식자 자료(.neko 폴더)가 전에 돌린 다른 원고({owner})의 것이에요 — 그 자료를 덮지 않도록 이 장은 건너뛰어요.";
        if (!File.Exists(owner)) msg += " 그 원고가 지금 없어도(지웠거나 드라이브가 끊김) 이것이 하나뿐인 사본일 수 있어요.";
        if (legacySibling && cx.PathSettings != null)
        {
            var ownerOut = OutputPathFor(cx.PathSettings, owner);
            if (!File.Exists(MaskSidecar.JsonPathFor(ownerOut)) && !File.Exists(LetterSidecar.JsonPathFor(ownerOut)))
                return msg + $" 같은 폴더의 {Path.GetFileName(owner)}부터 한 번 돌리면 옛 버전 이름으로 남은 그 자료가 새 이름으로 옮겨져 이 장도 돌릴 수 있어요.";
        }
        if (!string.IsNullOrWhiteSpace(cx.PathSettings?.OutputRoot)) msg += " 출력 폴더를 바꾸거나 비워(원고 폴더 옆 _neko) 주세요.";
        return msg + (hasResult ? " 덮어도 되는 결과면 그것과 .neko 폴더의 같은 이름 파일을 지운 뒤 다시 돌려 주세요."
                                : " 덮어도 되는 자료면 .neko 폴더의 같은 이름 파일을 지운 뒤 다시 돌려 주세요.");
    }

    /// <summary>결과 자리에 이미 있는 사이드카의 주인이 이 원고가 아니면 그 원고 경로 — 아니면 null.
    /// 근거는 마스크 사이드카(<see cref="MaskSidecar.MatchesSource"/>)와 식자 사이드카(<see cref="LetterSidecar.BelongsTo"/>)이고, 둘 중 하나라도 남의 것이면 충돌이다(<see cref="IsOtherSource"/>).
    /// 같은 원고(경로가 같거나, 폴더를 옮기거나 복사해 이름+바이트 길이가 같음)면 null — 지금처럼 덮는다.
    /// 식자 사이드카에 원고 크기를 넘기지 않는 것은 1화·2화가 해상도까지 같기 쉬워서다(길이 기록이 없는 옛 사이드카는 경로로만 본다).
    /// 식자 사이드카는 json만 읽는다(<see cref="LetterSidecar.LoadRecord"/>) — 주인을 가리는 데 식자 전 원고는 필요 없고, 그 경로는 절대 경로라
    /// 고정 출력 폴더를 옮기면 옛 폴더를 가리켜 <see cref="LetterSidecar.Load"/>가 null이 된다(식자 사이드카만 있는 자리의 보호가 사라졌다).
    /// 한계: 사이드카를 남기지 않은 결과는 주인을 가릴 수 없어 지금처럼 덮는다 — 검토 흐름 없이 돌린 장 가운데 글자 없는 장 전부다
    /// (마스크 사이드카는 검토 흐름에서만, 식자 사이드카는 글자 블록이 있는 장에만 남는다). 기본 설정(번역+채색, 검열 제거 끔)이면 표지·대사 없는 장이
    /// 모두 여기에 든다 — 같은 출력 폴더에 폴더 이름까지 같은 다른 화를 따로 돌리면 대사 있는 장은 막히고 대사 없는 장은 그 화의 결과로 바뀐다.
    /// 사이드카는 읽기만 한다. 깨졌으면(0바이트·JSON 오류·원고 경로 없음) 근거가 없는 것으로 본다 — 이 검사가 예외로 장을 막지 않는다.
    /// 다만 json이 있는데 잠겨서 못 읽었으면(Load는 이것도 null로 삼킨다) <paramref name="locked"/>에 그 json — 근거 없음으로 덮으면 잠금이 풀린 뒤
    /// 자리에 이 원고의 결과와 남의 사이드카가 섞여 남아, 다음 실행부터 이 원고는 제 결과인데 막히고 그 원고는 이것을 덮는다.
    /// <paramref name="ownerLen"/>은 주인을 알려 준 사이드카가 적은 원고 길이(옛 사이드카는 0).</summary>
    private static string? OtherSlotOwner(ReviewPage pg, bool slotFollowsSource, out long ownerLen, out string? locked)
    {
        ownerLen = 0;
        locked = null;
        try
        {
            var ms = MaskSidecar.Load(pg.OutPath);
            if (ms != null && IsOtherSource(ms.Source, ms.SrcLen, ms.MatchesSource(pg.File), pg.File, slotFollowsSource)) { ownerLen = ms.SrcLen; return ms.Source; }
            if (ms == null && IsLocked(MaskSidecar.JsonPathFor(pg.OutPath))) locked = MaskSidecar.JsonPathFor(pg.OutPath);
            var ls = LetterSidecar.LoadRecord(pg.OutPath);
            if (ls != null && IsOtherSource(ls.Source, ls.SrcLen, ls.BelongsTo(pg.File), pg.File, slotFollowsSource)) { ownerLen = ls.SrcLen; return ls.Source; }
            if (ls == null && IsLocked(LetterSidecar.JsonPathFor(pg.OutPath))) locked ??= LetterSidecar.JsonPathFor(pg.OutPath);
        }
        catch { /* 사이드카를 못 읽으면 근거가 없다 */ }
        return null;
    }

    /// <summary>사이드카(원고 <paramref name="source"/>, 원고 길이 기록 <paramref name="srcLen"/>)가 이 원고(<paramref name="file"/>)의 것이 아니라는 근거가 되는가.
    /// 원고 길이를 적은 사이드카는 '다른 원고' 판정만으로 충분해 그 원고가 지금 디스크에 없어도 충돌이다 — 원고를 지웠거나 압축해 치웠거나
    /// 잠깐 안 보이면(외장 드라이브·NAS) 남은 결과가 유일한 사본이다.
    /// 다만 결과 자리가 원고 폴더에서 정해지면(<paramref name="slotFollowsSource"/>) 그 자리는 그 폴더의 원고만 쓰므로, 주인이 없고 파일 이름이 같은 사이드카는
    /// 폴더를 결과째 옮긴 뒤 다시 저장(재인코딩·최적화)한 같은 장이다 — 충돌로 보지 않는다(여기까지 막으면 옮긴 화의 모든 장이 제 결과에 막힌다).
    /// 이름이 다른 주인(옛 이름으로 남은 형제 원고의 자료)은 거기서도 충돌이다.
    /// 길이 기록이 없는 옛 사이드카(마스크는 v1.5.5 전, 식자는 v1.6.0 전)는 경로로만 판정해 폴더를 옮긴 같은 원고일 수 있으므로 그 원고가 아직 있을 때만 충돌이다.
    /// 원고 경로가 비었으면 깨진 사이드카라 근거가 없다.</summary>
    private static bool IsOtherSource(string? source, long srcLen, bool sameSource, string file, bool slotFollowsSource)
    {
        if (sameSource || string.IsNullOrWhiteSpace(source)) return false;
        if (File.Exists(source)) return true;
        bool movedSamePage = slotFollowsSource && string.Equals(Path.GetFileName(source), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
        return srcLen > 0 && !movedSamePage;
    }

    /// <summary>결과 자리가 원고 폴더에서 정해지는가 — 출력 폴더를 비웠을 때(원고 폴더 안의 _neko).
    /// 출력 폴더를 고정하면 폴더 이름이 같은 여러 화가 한 자리로 모인다 — 출력 폴더가 원고 폴더 자신이어도 다른 곳의 같은 이름 폴더(E:\vol1 → D:\out\vol1)가
    /// 같은 자리로 떨어지므로 고정한 것으로 본다. 시작 때 박제한 설정으로 본다(없으면 고정한 것으로 — 더 조심스러운 쪽).</summary>
    private static bool SlotFollowsSource(AppSettings? s) => s != null && string.IsNullOrWhiteSpace(s.OutputRoot);

    /// <summary>파일이 있는데 다른 프로그램이 쥐고 있어(공유·잠금 위반) 지금 읽을 수 없는가 — 사이드카 Load가 깨진 파일과 똑같이 null로 삼키는 경우를 가른다.
    /// Load(File.ReadAllText)와 같은 공유 모드로 한 번 읽어 본다. 없거나 다른 이유(권한 등)로 못 읽으면 false — 지금처럼 근거 없음이다.</summary>
    private static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try { File.ReadAllBytes(path); return false; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return true; }
        catch { return false; }
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
}
