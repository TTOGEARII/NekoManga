using System.Text.Json;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary>검열 마스크 사이드카 (2026-10-21, 사용자 요청 "마스크 수정이 재실행에도 유지되게").
///
/// 결과 폴더의 <c>.neko\&lt;이름&gt;.mask.json / .mask.png / .auto.png / .pre.png</c>에 저장된다.
/// - mask.png: 지울 곳(255) — 원본 해상도 CV_8UC1. 사용자가 결과 창의 '마스크 수정' 탭에서 고치면 여기에 덮어쓴다.
/// - auto.png: 자동 감지 결과 사본 ('자동 감지로' 되돌리기용 + 학습 재료의 '자동' 쪽).
/// - pre.png: 검열 제거 직전 원고 (번역 글자만 지운 상태) — 마스크 탭이 보여 주는 그림이자 '채색부터 다시'의 입력.
/// 같은 원고를 다시 돌리면 러너가 감지 대신 이 마스크를 쓴다(ReuseStoredMasks).</summary>
public record MaskSidecar(
    string Source,          // 원본 파일 경로
    int SrcW, int SrcH,     // 원본 크기 (마스크 크기와 같다)
    string Summary,         // 감지 요약 (예: "AI(흰 0·검 4·모자이크 0 → 24705px)")
    bool Skip,              // 사용자가 '이 페이지는 검열 없음'으로 표시
    bool Edited,            // 사용자가 마스크를 손으로 고쳤는지
    bool Confirmed,         // 검열 제거를 진행한(또는 진행하라고 확인한) 마스크인지 — 재실행 때 검토를 다시 기다리지 않는다
    DateTime SavedAt,
    long SrcLen = 0,        // 원본 파일 바이트 길이 — 폴더를 옮겨 경로가 달라져도 같은 원고인지 가리는 근거 (옛 사이드카는 0)
    bool Pending = false,   // 결과 창이 끝난 장의 마스크를 고쳐 저장했고, 아직 이 마스크로 결과물을 다시 만들지 않았다 — [고친 장 모두 다시 렌더링]이 모으는 장 (2026-11-13)
    bool? Colorized = null, // 이 장의 결과물이 채색된 그림인가 — 2차가 결과물을 쓴 뒤 적는다. null = 모름(v1.9.1까지의 사이드카, 아직 2차 전): 그때는 그림으로 가린다(PipelineRunner.IsColorized)
    string? RegenSeeds = null,  // 지난 재생성(SDXL)이 쓴 시드 — 영역 순서대로 쉼표로("460327666,1277331254"). 2차가 결과물을 쓴 뒤 적는다. null = 기록 없음. 글로 두는 것은 레코드 비교가 값으로 되게(배열은 참조로 견준다)
    bool SeedLock = false,      // 이 장은 적어 둔 시드로 고정 — 다음 재생성([지우기]·묶음·[시작])도 그 시드로 돈다 (2026-11-15 사용자 "이 시드를 저장하는게 따로 있을까?")
    string? RegenSex = null)    // 사용자가 검열 조각에 고른 성별 — "x,y,M;x,y,F;x,y,B"(조각 안의 한 점, 마스크 좌표 — Decensor.SexMarks). 재생성이 그 조각의 자리에 그 성별의 태그를 넣는다. null = 전부 자동 (2026-11-15 사용자 "모든 페이지가 남/여 구별되서")
{
    /// <summary>재생성이 쓴 시드 목록 → 사이드카에 적는 글. 적을 것이 없으면 null(기록 없음).</summary>
    public static string? FormatSeeds(IReadOnlyList<long> seeds) => seeds.Count == 0 ? null : string.Join(",", seeds);

    /// <summary>사이드카에 적힌 시드 글 → 목록(영역 순서). 비었거나 한 칸이라도 숫자(0 이상)가 아니면 빈 목록 — 손으로 망가뜨린 기록의 반만 맞는 시드로 돌지 않는다.
    /// long의 끝 값도 받지 않는다(2단계가 시드+1을 쓴다 — 넘치면 음수가 되어 ComfyUI가 거부한다).</summary>
    public static IReadOnlyList<long> ParseSeeds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var seeds = new List<long>();
        foreach (var part in text.Split(','))
        {
            if (!long.TryParse(part.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seed) || seed == long.MaxValue) return [];
            seeds.Add(seed);
        }
        return seeds;
    }

    /// <summary>그 결과 자리의 저장 마스크가 '고쳤지만 아직 결과물에 반영 전'(<see cref="Pending"/>)이고, 그 원고의 것이며, 다시 렌더링할 재료(검열 제거 전 원고)가 있는가.
    /// 메인 창·결과 창이 [고친 장 모두 다시 렌더링]의 대상을 셀 때 쓴다. json을 못 읽으면(잠김·깨짐) false — 그 장은 이번 묶음에서 빠질 뿐이다.</summary>
    public static bool IsPendingFor(string outPath, string source) =>
        Load(outPath) is { Pending: true } sc && sc.MatchesSource(source) && HasPre(outPath);

    /// <summary>이 사이드카가 그 원고의 것인가 — 경로가 같거나, 파일 이름과 바이트 길이가 같으면(폴더를 옮긴 경우) 같은 원고.
    /// 이름만 같은 다른 원고(1화·2화의 001.jpg)는 길이가 달라 걸러진다. 길이를 모르는 옛 사이드카는 경로로만 본다.</summary>
    public bool MatchesSource(string file) => SameSource(Source, SrcLen, file);

    /// <summary>원고 경로 <paramref name="source"/>와 원고 길이 <paramref name="srcLen"/>을 적은 기록이 그 원고(<paramref name="file"/>)의 것인가 — <see cref="MatchesSource"/>의 규칙 그 자체.
    /// 결과 주인 기록(<see cref="ResultOwner"/>)도 같은 규칙으로 가려야 해서 한 곳에 둔다(둘이 어긋나면 한쪽은 같은 원고, 한쪽은 다른 원고라고 한다).</summary>
    internal static bool SameSource(string source, long srcLen, string file)
    {
        if (string.Equals(Norm(source), Norm(file), StringComparison.OrdinalIgnoreCase)) return true;
        if (srcLen <= 0 || !string.Equals(Path.GetFileName(source), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) return false;
        return LengthOf(file) == srcLen;
    }

    /// <summary>경로가 정확히 같은 원고의 것인가 — 이름+길이 폴백 없이. 다른 이름의 사이드카를 **옮겨 올** 때는 이걸로 본다
    /// (이름·길이만 같은 다른 폴더의 복제 원고가 남의 사이드카를 가져가 지우면 안 된다).</summary>
    public bool SourceIs(string file) => string.Equals(Norm(Source), Norm(file), StringComparison.OrdinalIgnoreCase);

    public static long LengthOf(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); } catch { return p; }
    }

    private static string Base(string outPath) =>
        Path.Combine(Path.GetDirectoryName(outPath)!, ".neko", Path.GetFileNameWithoutExtension(outPath));

    public static string JsonPathFor(string outPath) => Base(outPath) + ".mask.json";
    public static string MaskPathFor(string outPath) => Base(outPath) + ".mask.png";
    public static string AutoPathFor(string outPath) => Base(outPath) + ".auto.png";
    public static string PrePathFor(string outPath) => Base(outPath) + ".pre.png";
    /// <summary>종류 지도(Decensor.CensorKind 값 그대로의 8비트 PNG) — 자동 감지가 어느 엔진으로 그 자리를 찾았는지. 다시 돌릴 때도 같은 엔진으로 지우게 남긴다.</summary>
    public static string KindPathFor(string outPath) => Base(outPath) + ".kind.png";

    /// <summary>json만 다시 쓴다 (마스크·원고 PNG는 그대로).</summary>
    public void Save(string outPath)
    {
        var path = JsonPathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ImgIo.WriteAllTextAtomic(path, JsonSerializer.Serialize(this));
    }

    /// <summary>마스크(+자동 감지 사본, 검열 제거 전 원고)를 PNG로 쓰고 json을 저장한다. 넘기지 않은 PNG는 있던 그대로 둔다.
    /// 재생성 시드의 기록·고정(<see cref="RegenSeeds"/>·<see cref="SeedLock"/>)은 그 자리에 있던 **같은 원고의** 사이드카에서 옮긴다 — 마스크와 따로 가는 그 장의 값이라
    /// 마스크를 다시 쓰는 길(결과 창 저장·검토 확인·새 감지)마다 지워지면 고정해 둔 장이 말없이 풀린다. 다른 원고가 그 자리에 왔으면 버린다.
    /// 그 사이드카는 **아무것도 쓰기 전에** 읽는다: json이 잠겨 있으면(백신 검사·동기화) 기다렸다 다시 읽고, 끝내 못 읽으면 던진다(IOException) —
    /// '기록 없음'으로 넘기면 고정이 풀린 채 새로 쓰이고(쓰기는 0.5초쯤 다시 시도해 성공한다) 바로 다음 [지우기]가 새 시드로 돈다(2026-11-15 검수).
    /// 시드 없이 고정 표시만 남은 기록은 옮기지 않는다 — 다음에 뽑힌 시드가 말없이 고정된다.
    /// 조각의 성별 표시(<see cref="RegenSex"/>)도 같은 길로 옮긴다. <paramref name="regenSex"/>를 넘기면(결과 창의 저장 — 지금 화면의 표시) 그것으로 바꾼다: null = 옮김, "" = 지움(전부 자동).
    /// <paramref name="pending"/>은 결과 창이 끝난 장의 마스크를 고쳐 저장할 때만 켠다 — 러너가 쓰는 마스크(새 감지·검토 결과)는 곧 결과물이 되므로 늘 꺼져 있다.</summary>
    /// <param name="kinds">자동 감지의 종류 지도. <paramref name="freshDetection"/>이면(1차가 새로 감지한 결과) null일 때 옛 지도를 지운다 —
    /// 남겨 두면 지난번 감지의 표시가 이번 마스크에 엉뚱하게 적용된다. 결과 창의 마스크 저장은 둘 다 넘기지 않아 지도가 그대로 남는다.</param>
    public static MaskSidecar Write(string outPath, string source, Mat mask, Mat? autoMask, Mat? pre,
        string summary, bool skip, bool edited, bool confirmed, Mat? kinds = null, bool freshDetection = false, bool pending = false, bool? colorized = null, string? regenSex = null)
    {
        var old = PipelineRunner.LoadSidecarWaitingOutLock(outPath, brokenThrows: false);   // 깨진 json·없는 사이드카는 null — 옮길 것이 없다
        bool same = old != null && old.MatchesSource(source);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(outPath)!, ".neko"));
        ImgIo.Write(MaskPathFor(outPath), mask);
        if (autoMask != null) ImgIo.Write(AutoPathFor(outPath), autoMask);
        if (pre != null) ImgIo.Write(PrePathFor(outPath), pre);
        if (kinds != null) ImgIo.Write(KindPathFor(outPath), kinds);
        else if (freshDetection) File.Delete(KindPathFor(outPath));
        // PNG를 쓰는 사이(수십~수백 ms)에 러너가 시드를 적었거나 결과 창이 고정을 바꿨을 수 있다 — 지금 읽히는 같은 원고의 레코드가 있으면 그쪽이 최신이다.
        // 못 읽으면(그사이 잠김) 맨 앞에서 기다려 읽어 둔 것으로 간다
        if (Load(outPath) is { } latest && latest.MatchesSource(source)) { old = latest; same = true; }
        var sc = new MaskSidecar(source, mask.Width, mask.Height, summary, skip, edited, confirmed, DateTime.Now, LengthOf(source), pending, colorized,
            same ? old!.RegenSeeds : null, same && old!.SeedLock && ParseSeeds(old.RegenSeeds).Count > 0,
            regenSex != null ? (regenSex.Length > 0 ? regenSex : null) : same ? old!.RegenSex : null);
        sc.Save(outPath);
        return sc;
    }

    /// <summary>json·마스크 PNG가 모두 있을 때만 돌려준다.</summary>
    public static MaskSidecar? Load(string outPath)
    {
        try
        {
            var path = JsonPathFor(outPath);
            if (!File.Exists(path) || !File.Exists(MaskPathFor(outPath))) return null;
            return JsonSerializer.Deserialize<MaskSidecar>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static bool HasPre(string outPath) => File.Exists(PrePathFor(outPath));

    /// <summary>옛 이름(v1.5.5까지의 <c>&lt;원고 이름&gt;</c>)의 사이드카 네 파일을 새 이름으로 옮긴다 — 결과 이름 규칙이 바뀐 뒤 한 번만 쓰인다.
    /// 새 이름 자리에 이미 마스크 사이드카가 있으면 건드리지 않는다(json을 마지막에 옮겨, 도중에 멈추면 다음 실행이 이어서 옮긴다).</summary>
    public static void MoveLegacy(string fromOut, string toOut)
    {
        if (File.Exists(JsonPathFor(toOut))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(JsonPathFor(toOut))!);
        foreach (var (a, b) in new[] { (MaskPathFor(fromOut), MaskPathFor(toOut)), (AutoPathFor(fromOut), AutoPathFor(toOut)), (PrePathFor(fromOut), PrePathFor(toOut)),
                                       (JsonPathFor(fromOut), JsonPathFor(toOut)) })
            if (File.Exists(a)) File.Move(a, b, overwrite: true);
    }

    /// <summary>마스크 PNG → CV_8UC1 (255=지울 곳). 없거나 그림으로 읽히지 않으면(0바이트·깨진 PNG) null — 재실행은 이때 새로 감지한다.
    /// 잠김·권한 같은 입출력 오류는 종류 지도(<see cref="LoadKinds"/>)와 달리 삼키지 않고 던진다 — null로 넘기면 사용자가 고친 마스크가 멀쩡히 있는데도
    /// 재실행이 새로 감지하고, 검토 흐름에서는 그 감지로 사이드카를 덮는다.</summary>
    public static Mat? LoadMask(string outPath) => LoadGray(MaskPathFor(outPath));
    public static Mat? LoadAuto(string outPath) => LoadGray(AutoPathFor(outPath));

    /// <summary>종류 지도 → CV_8UC1 (값은 CensorKind). 없거나 못 읽으면 null — 그때는 제거 단계가 전부 막대 모델로 본다.</summary>
    public static Mat? LoadKinds(string outPath)
    {
        var path = KindPathFor(outPath);
        if (!File.Exists(path)) return null;
        try
        {
            var m = ImgIo.Read(path, ImreadModes.Grayscale);
            if (m.Empty()) { m.Dispose(); return null; }
            return m;
        }
        catch { return null; }
    }

    private static Mat? LoadGray(string path)
    {
        if (!File.Exists(path)) return null;
        var m = ImgIo.Read(path, ImreadModes.Grayscale);
        if (m.Empty()) { m.Dispose(); return null; }
        Cv2.Threshold(m, m, 127, 255, ThresholdTypes.Binary);
        return m;
    }
}
