using System.Security.Cryptography;
using System.Text.Json;

namespace NekoManga.Core.Pipeline;

/// <summary>결과물의 주인 기록 — 그 결과 자리의 결과물(png 또는 '원본 그대로' 복사본)이 어느 원고에서 나왔는지. 결과 폴더의 <c>.neko\&lt;이름&gt;.owner</c>(내용은 json).
///
/// 출력 폴더를 고정하면 A\vol1\01.png·B\vol1\01.png가 같은 결과 이름(<c>vol1_01.png</c>)이라, 따로 돌리면 나중 원고가 앞 원고의 결과를 조용히 덮었다.
/// 그 자리가 누구 것인지 알려 주는 것이 마스크 사이드카(검토 흐름에서만 남는다)와 식자 사이드카(글자 블록이 있는 장에만 남는다)뿐이어서, 둘 다 없는 장
/// (검토 없이 돌린 글자 없는 장 — 기본 설정이면 표지·대사 없는 장 전부)은 가릴 근거가 없었다(v1.8.2에서 합성 그림으로 재현).
/// 그래서 결과물을 쓰는 곳(러너, 결과 창 [저장])이 쓸 때마다 이 기록을 같이 남기고(<c>PipelineRunner.RecordResultOwner</c>), 결과 자리 검사(<c>PipelineRunner.OtherSlotOwner</c>)가 세 번째 근거로 읽는다.
///
/// 일부러 작게 둔다: 읽기만 하는 근거이고, 옮기거나 따라다니게 하지 않는다 — 결과 이름이 원고 하나로 정해지므로(v1.6.0) 그럴 일이 없다
/// (목록에 따라 이름이 바뀌던 때의 소유 표식 <c>.src.txt</c>는 그것을 따라 옮기는 장치가 끝없이 붙어 걷어냈다).
/// 식자 사이드카를 글자 없는 장에도 쓰는 방법은 쓰지 않았다 — 결과 창이 그것을 '다시 식자할 수 있는 장'으로 읽는다.
/// 확장자가 .json·.png가 아닌 것은 다른 사이드카와 이름이 겹치지 않게 하려는 것이다(<c>01.png</c>의 <c>01.owner.json</c>이었다면 <c>01.owner.png</c> 원고의 식자 사이드카와 같은 파일이다).
/// 이 기록이 없는 결과(이전 버전이 만든 것)는 예전처럼 근거가 없다.</summary>
/// <param name="Source">원고 파일의 전체 경로.</param>
/// <param name="SrcLen">원고 파일 바이트 길이 — 폴더를 옮기거나 복사해 경로가 달라져도 같은 원고인지 가리는 근거(사이드카의 SrcLen과 같은 뜻).</param>
/// <param name="SavedAt">결과물을 쓴 때.</param>
/// <param name="SrcHash">원고 파일 내용의 지문(<see cref="HashOf"/>) — 경로가 달라졌는데 이름·길이가 같을 때 정말 같은 원고인지 가린다.
/// 비어 있으면(원고를 읽지 못했거나 이 필드가 생기기 전의 기록) 이름+길이까지만 본다.</param>
internal record ResultOwner(string Source, long SrcLen, DateTime SavedAt, string SrcHash = "")
{
    /// <summary>이 기록이 그 원고의 것인가.
    /// 경로가 같으면 같은 원고다 — 제자리에서 고쳐 다시 저장한 원고도(길이·내용이 달라졌어도) 그 자리의 주인이라 지문을 보지 않는다.
    /// 경로가 다르면 마스크 사이드카처럼 파일 이름과 바이트 길이가 같아야 하고(<see cref="MaskSidecar.SameSource"/> — 폴더를 옮기거나 복사한 원고), 거기에 **내용 지문도 같아야** 한다:
    /// 압축하지 않는 포맷(bmp·ppm·pgm, 압축 없는 tif)은 해상도가 같으면 길이가 늘 같아서, 이름+길이만 보면 A\vol1\01.bmp와 B\vol1\01.bmp가 같은 원고로 읽혀
    /// 나중 것이 앞의 결과를 조용히 덮었다(합성 그림으로 재현 — bmp·ppm·tif 모두).
    /// 지문이 없는 기록이거나 지금 원고의 지문을 내지 못하면(잠김) 이름+길이까지만 본다 — 지문이 생기기 전의 규칙 그대로.</summary>
    public bool MatchesSource(string file)
    {
        if (MaskSidecar.SameSource(Source, 0, file)) return true;          // 길이 0 = 경로로만 본다
        if (!MaskSidecar.SameSource(Source, SrcLen, file)) return false;   // 이름 또는 길이가 다르다
        if (string.IsNullOrEmpty(SrcHash)) return true;
        var now = HashOf(file);
        return now.Length == 0 || string.Equals(now, SrcHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>원고 파일 내용의 지문 — 파일 전체 SHA-256의 앞 16바이트(16진수 32자). 못 읽으면 빈 문자열(그 기록은 이름+길이 규칙으로 돌아간다).
    /// 파일을 **통째로** 읽는다. 머리 몇 KB만 보는 것으로는 안 된다 — 해상도가 같은 bmp는 머리(54바이트)가 같고, 아래 줄부터 저장해 첫 부분이 두 쪽 모두 흰 아래 여백이다.
    /// 띄엄띄엄 표본을 뜨는 방법도 쓰지 않았다: 여백이 넓은 쪽(속표지·후기)은 표본이 전부 흰 줄에 떨어질 수 있고, 식은 디스크에서는 표본마다 탐색이 붙어 한 번에 죽 읽는 것보다 빠르지도 않다.
    /// 비용(실측, Ryzen 5 3600 — 파일이 OS 캐시에 있을 때 약 1.3GB/s): 0.5MB 0.4ms · 2MB 1.3ms · 8MB 6ms · 36MB(3000×4000 bmp) 27ms · 77MB 58ms.
    /// SHA 명령이 없는 CPU는 서너 배. 디스크를 더 읽는 일은 거의 없다 — 기록은 결과물을 쓴 뒤에 적으므로 그때 원고는 러너가 막 읽은(디코드했거나 복사한) 파일이고,
    /// 결과 자리 검사는 경로가 달라졌는데 이름·길이가 같을 때만(옮기거나 복사한 폴더) 지문을 낸다 — 그 파일도 곧 러너가 읽는다.
    /// 식은 파일을 읽게 되는 것은 결과 창 [저장]·[채색부터 다시]처럼 한 장씩 손으로 하는 일뿐이다.</summary>
    internal static string HashOf(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs), 0, 16);
        }
        catch { return ""; }
    }

    public static string PathFor(string outPath) =>
        Path.Combine(Path.GetDirectoryName(outPath)!, ".neko", Path.GetFileNameWithoutExtension(outPath) + ".owner");

    /// <summary>이 결과 자리의 주인을 <paramref name="source"/>로 적는다(있던 기록은 무조건 바꿔 쓴다). 못 쓰면 던진다.
    /// 결과물을 쓴 쪽은 이것 대신 <see cref="Record"/>를 부른다(있던 기록의 주인을 넘겨받을지부터 따진다).</summary>
    public static ResultOwner Write(string outPath, string source) => Describe(source).Save(outPath);

    /// <summary>방금 쓴 결과물의 주인을 적는다 — 러너와 결과 창이 결과물을 쓴 **뒤에** 부른다. 못 쓰면 던진다.
    /// 있던 기록이 같은 장(이름·길이·지문이 같다)을 다른 경로로 가리키고 그 경로의 원고가 아직 그대로 있으면(길이가 기록과 같다) 기록을 건드리지 않는다 —
    /// 마스크 사이드카의 규칙과 같다(폴더를 복사해 온 원고는 그 자리를 빌려 쓸 뿐이다). 늘 지금 경로로 바꿔 쓰면 같은 파일을 다른 표기(정션·subst·매핑 드라이브)로
    /// 한 번 올리거나 복사본을 한 번 돌린 것만으로 주인이 넘어가, 원래 원고를 다시 저장한 뒤 돌리면 제 결과에 막혔다.
    /// 그 경로의 원고가 없어졌거나 길이가 달라졌으면(폴더를 옮겼다, 그 자리에 다른 것이 왔다) 지금 경로가 주인이 된다.</summary>
    public static ResultOwner Record(string outPath, string source)
    {
        var now = Describe(source);
        if (Load(outPath) is { } prev && prev.KeepsOwnershipOver(now)) return prev;
        return now.Save(outPath);
    }

    private static ResultOwner Describe(string source)
    {
        string full; try { full = Path.GetFullPath(source); } catch { full = source; }   // 상대 경로로 돌린 원고(--run)도 다음 실행의 현재 폴더와 무관하게 가려지도록
        return new ResultOwner(full, MaskSidecar.LengthOf(source), DateTime.Now, HashOf(source));
    }

    private ResultOwner Save(string outPath)
    {
        ImgIo.WriteAllTextAtomic(PathFor(outPath), JsonSerializer.Serialize(this));   // 임시 파일 뒤 교체 — .neko 폴더가 없으면 만들고, 잠깐의 잠금은 다시 시도한다
        return this;
    }

    /// <summary>이 기록(먼저 있던 것)이 같은 장을 다른 경로로 들고 온 <paramref name="now"/>에게 주인 자리를 넘기지 않는가 — <see cref="Record"/>의 규칙.
    /// 옛 경로의 원고는 길이까지만 확인한다(지문까지 내려면 복사해 온 폴더를 돌릴 때마다 원래 폴더의 원고를 한 번씩 더 읽는다).</summary>
    private bool KeepsOwnershipOver(ResultOwner now) =>
        !string.IsNullOrWhiteSpace(Source) && SrcLen > 0 && SrcLen == now.SrcLen
        && !MaskSidecar.SameSource(Source, 0, now.Source)
        && string.Equals(Path.GetFileName(Source), Path.GetFileName(now.Source), StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(SrcHash) || now.SrcHash.Length == 0 || string.Equals(SrcHash, now.SrcHash, StringComparison.OrdinalIgnoreCase))
        && MaskSidecar.LengthOf(Source) == SrcLen;

    /// <summary>없거나 못 읽으면(깨짐·잠김) null — 잠김을 '없음'과 갈라야 하는 쪽은 파일이 있는지·잠겼는지를 따로 본다(사이드카 Load와 같은 계약).</summary>
    public static ResultOwner? Load(string outPath)
    {
        try
        {
            var path = PathFor(outPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<ResultOwner>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
