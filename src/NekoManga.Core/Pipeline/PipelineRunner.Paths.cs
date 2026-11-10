using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 결과물 경로 — 이름·폴더 규칙, 결과 자리 충돌 확인과 정리, 옛 이름으로 남은 결과 옮기기
public partial class PipelineRunner
{
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
