using OpenCvSharp;

namespace NekoManga.Core;

/// <summary>유니코드 경로 안전 이미지 I/O.
///
/// <see cref="Cv2.ImRead(string, ImreadModes)"/>·<see cref="Cv2.ImWrite(string, Mat, int[])"/>는
/// 파일 경로를 ANSI(현재 코드페이지)로 마샬링한다. 만화 경로에는 일본어·한글이 흔해서
/// 코드페이지로 못 옮기는 문자가 있으면 "cannot marshal: Encountered unmappable character"로 터진다.
///
/// 그래서 파일 자체는 .NET(유니코드 경로 OK)으로 읽고/쓰고, OpenCV는 메모리 버퍼만
/// 디코드/인코드한다 — webp 등 OpenCV 코덱은 그대로 유지된다
/// (System.Drawing은 이 포맷들을 못 읽으므로 OpenCV 디코드는 반드시 거쳐야 한다).</summary>
public static class ImgIo
{
    /// <summary>OpenCV(ImDecode)가 읽을 수 있는 만화 이미지 확장자 (소문자, 점 포함). 메인 창 파일 선택·끌어놓기 필터가 쓴다.
    /// .avif는 뺐다 — 번들된 OpenCV 런타임(OpenCvSharp4.runtime.win)이 AVIF 코덱 없이 빌드돼 있어(Cv2.GetBuildInformation()의 'AVIF: NO')
    /// 받아 놓고 작업 도중에 읽지 못해 실패했다. 런타임을 바꿔 다시 넣을 때는 그 줄부터 확인한다.</summary>
    public static readonly string[] ImageExts =
        [".png", ".jpg", ".jpeg", ".jpe", ".webp", ".bmp", ".tif", ".tiff", ".jp2", ".ppm", ".pgm", ".pbm", ".pnm"];

    public static bool IsImageFile(string path) =>
        ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>그림이지만 번들된 OpenCV에 코덱이 없어 받지 않는 확장자 — 메인 창이 목록에서 뺀 수를 알린다(그림이 아닌 파일은 알리지 않는다).
    /// 만화 원고로 올 만한 것만 짧게 둔다. .gif는 읽히지만(빌드 정보 'GIF: YES') 받는 목록에 없을 뿐이라 여기 넣지 않는다.</summary>
    public static readonly string[] UnreadableImageExts = [".avif", ".heic", ".heif", ".jxl"];

    /// <summary>목록에 넣지 않은 파일 가운데 <see cref="UnreadableImageExts"/>를 형식별로 세어 알리는 한 줄 (예: "AVIF 2장은 읽을 수 없어 뺐어요"). 없으면 null.</summary>
    public static string? UnreadableNotice(IEnumerable<string> skipped)
    {
        var exts = skipped.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToList();
        var parts = UnreadableImageExts.Select(e => (Name: e[1..].ToUpperInvariant(), Count: exts.Count(x => x == e)))
                                       .Where(p => p.Count > 0).Select(p => $"{p.Name} {p.Count}장").ToList();
        return parts.Count == 0 ? null : string.Join("·", parts) + "은 읽을 수 없어 뺐어요";
    }

    /// <summary>경로의 이미지를 읽는다. 디코드하지 못하면(0바이트·깨진 파일·코덱 없는 형식) 빈 Mat을 돌려준다(Cv2.ImRead와 동일 계약).
    /// 파일이 없거나 열리지 않으면(잠김·권한) 예외를 그대로 던진다 — 이 점은 빈 Mat을 주는 Cv2.ImRead와 다르다.</summary>
    public static Mat Read(string path, ImreadModes mode = ImreadModes.Color)
    {
        var bytes = File.ReadAllBytes(path);
        // 쓰다 끊기거나 내려받다 끊긴 0바이트 파일 — Cv2.ImDecode는 빈 버퍼를 ArgumentException(Empty span)으로 거부한다(Cv2.ImRead는 빈 Mat).
        // 던지면 '못 읽음'을 Empty()로 가르는 호출부를 지나쳐 나간다(0바이트 마스크 사이드카가 재실행마다 그 장을 실패시켰다)
        if (bytes.Length == 0) return new Mat();
        return Cv2.ImDecode(bytes, mode);
    }

    /// <summary>확장자에 맞춰 인코딩해 경로에 쓴다. 인코딩 실패 시 false(Cv2.ImWrite와 동일 계약).
    /// 부모 폴더가 없으면 만들어 준다 (사이드카 폴더 등 — 없어서 실제로 터진 적 있음). 임시 파일에 다 쓴 뒤 교체한다(<see cref="WriteViaTemp"/>).</summary>
    public static bool Write(string path, Mat img)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!Cv2.ImEncode(ext, img, out var buf)) return false;
        WriteViaTemp(path, p => File.WriteAllBytes(p, buf));
        return true;
    }

    /// <summary>작은 텍스트(사이드카 json)도 임시 파일 뒤 교체로 쓴다 — 결과 창 미리 읽기가 반쯤 쓰인 json을 읽으면 역직렬화가 실패해 마스크 탭이 꺼진다.</summary>
    public static void WriteAllTextAtomic(string path, string text) =>
        WriteViaTemp(path, p => File.WriteAllText(p, text));

    /// <summary>원본 파일을 바이트 그대로 복사하되 임시 파일에 쓴 뒤 교체한다. 수정 시각은 지금으로
    /// (Copy는 원본 시각을 물려줘 결과 보기의 '더 나중에 쓰인 쪽' 규칙·결과 창 캐시 도장이 못 본다).</summary>
    public static void CopyAtomic(string src, string dst)
    {
        // Copy는 읽기 전용 속성까지 물려준다(압축 해제한 원고) — 우리가 만든 복사본이니 풀어 둔다(안 풀면 시각 설정·다음 덮어쓰기·임시 파일 삭제가 막힌다)
        WriteViaTemp(dst, p => { File.Copy(src, p, overwrite: true); ClearReadOnly(p); });
        try { File.SetLastWriteTimeUtc(dst, DateTime.UtcNow); }
        catch { Thread.Sleep(20); try { File.SetLastWriteTimeUtc(dst, DateTime.UtcNow); } catch { } }   // 막 닫은 파일을 누가 쥐고 있으면 한 번 더
    }

    /// <summary>임시 파일(<c>&lt;경로&gt;.xxxxxxxx.part</c>)에 다 쓴 뒤 제자리로 교체한다 — 결과 창·메인 창이 러너가 쓰는 중인 파일을 읽어 잘린 그림을 디코드하지 않게.
    /// 같은 볼륨 안의 Move는 원자적이라 읽는 쪽은 옛 파일 아니면 완성된 새 파일만 본다. 임시 이름은 호출마다 다르고(본 작업과 결과 창이 동시에 쓸 수 있다)
    /// 짧다(긴 동인지 경로에서 MAX_PATH를 넘기지 않게).
    /// 임시 파일을 **만들 수 없는** 경우(경로 길이·폴더·'만들기'만 막힌 권한 — 제자리 파일이 없을 때)만 제자리에 바로 쓴다. 디스크 가득 같은 I/O 오류는 그대로
    /// 던진다 — 제자리에 다시 쓰면 멀쩡하던 옛 파일을 0바이트로 비운 채 실패한다.</summary>
    private static void WriteViaTemp(string path, Action<string> write)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.part";
        try
        {
            try { write(tmp); }
            catch (Exception ex) when (ex is PathTooLongException or DirectoryNotFoundException or NotSupportedException || (ex is UnauthorizedAccessException && !File.Exists(path)))
            { write(path); return; }
            MoveOrFallback(tmp, path, () => write(path));
        }
        finally { try { if (File.Exists(tmp)) { ClearReadOnly(tmp); File.Delete(tmp); } } catch { } }
    }

    private static void ClearReadOnly(string path)
    {
        try { var a = File.GetAttributes(path); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly); } catch { }
    }

    private static readonly int[] MoveRetryMs = [30, 80, 150, 300];

    /// <summary>임시 파일을 제자리로 교체한다. OneDrive·백신·결과 창 미리 읽기가 옛 파일을 잠깐 쥐고 있으면 rename이 공유 위반이나 접근 거부로 튕긴다 —
    /// 네 번 재시도하고(합쳐서 0.5초 남짓), 끝내 안 되면 제자리에 바로 쓴다(예전 동작; 잠겨 있으면 열기 자체가 거부돼 옛 파일이 비워지지 않는다).
    /// 읽기 전용인 대상은 풀지 않는다 — 사용자가 보호하려고 둔 것일 수 있어 예전처럼 실패로 알린다.</summary>
    private static void MoveOrFallback(string tmp, string path, Action direct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < MoveRetryMs.Length) { Thread.Sleep(MoveRetryMs[attempt]); continue; }
                direct();
                return;
            }
        }
    }
}
