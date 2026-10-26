using System.Text.Json;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Pipeline;

/// <summary>식자를 나중에 다시 할 수 있게 남기는 사이드카 — 식자 직전 원고(base)와 번역 블록.
///
/// 식자는 완성본에 글자를 '구워' 넣으므로, 이게 없으면 글꼴 하나 바꾸려고 번역부터
/// 다시 돌려야 한다. 결과 폴더의 <c>.neko\&lt;이름&gt;.json / .base.png</c>에 저장되고,
/// 결과 보기 창의 식자 탭이 읽어 블록 수정·재식자에 쓴다.</summary>
public record LetterSidecar(
    string Source,          // 원본 파일 경로 (블록 좌표의 기준 크기)
    int SrcW, int SrcH,     // 원본 크기 — 원본 파일이 지워져도 좌표 환산이 되도록 박제
    string BasePath,        // 식자 전 원고 (번역 글자 지움 + 검열제거 + 채색까지 끝난 상태)
    List<TextBlockInfo> Blocks,
    long SrcLen = 0)        // 원본 바이트 길이 — 폴더를 옮겨 경로가 달라져도 같은 원고인지 가리는 근거 (옛 사이드카는 0)
{
    /// <summary>이 사이드카가 그 원고의 것인가 — 경로가 같거나, 이름이 같고 길이(옛 사이드카는 원고 크기)가 같으면(폴더를 옮김) 같은 원고.</summary>
    public bool BelongsTo(string file, (int w, int h)? dims = null)
    {
        if (string.Equals(Full(Source), Full(file), StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(Path.GetFileName(Source), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) return false;
        if (SrcLen > 0) return SrcLen == MaskSidecar.LengthOf(file);
        return dims is { } d && d.w == SrcW && d.h == SrcH;
    }

    private static string Full(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

    public static string JsonPathFor(string outPath) =>
        Path.Combine(Path.GetDirectoryName(outPath)!, ".neko",
            Path.GetFileNameWithoutExtension(outPath) + ".json");

    public static string BasePathFor(string outPath) =>
        Path.Combine(Path.GetDirectoryName(outPath)!, ".neko",
            Path.GetFileNameWithoutExtension(outPath) + ".base.png");

    public void Save(string outPath)
    {
        var path = JsonPathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ImgIo.WriteAllTextAtomic(path, JsonSerializer.Serialize(this));
    }

    /// <summary>옛 이름(v1.5.5까지의 <c>&lt;원고 이름&gt;</c>)의 사이드카를 새 이름으로 옮긴다 — 결과 이름 규칙이 바뀐 뒤 한 번만 쓰인다.
    /// 새 이름 자리에 이미 사이드카가 있으면 건드리지 않는다.</summary>
    public static void MoveLegacy(string fromOut, string toOut)
    {
        var sc = Load(fromOut);
        if (sc == null || File.Exists(JsonPathFor(toOut))) return;
        var newBase = BasePathFor(toOut);
        Directory.CreateDirectory(Path.GetDirectoryName(newBase)!);
        // base는 이 .neko 폴더의 것을 옮긴다 — json의 BasePath는 절대 경로라 폴더를 복사해 왔으면 옛 폴더를 가리킨다(그쪽은 복사만)
        var ownBase = BasePathFor(fromOut);
        if (File.Exists(ownBase)) File.Move(ownBase, newBase, overwrite: true);
        else File.Copy(sc.BasePath, newBase, overwrite: true);
        (sc with { BasePath = newBase }).Save(toOut);
        try { File.Delete(JsonPathFor(fromOut)); } catch { }
    }

    public static LetterSidecar? Load(string outPath)
    {
        try
        {
            var path = JsonPathFor(outPath);
            if (!File.Exists(path)) return null;
            var sc = JsonSerializer.Deserialize<LetterSidecar>(File.ReadAllText(path));
            return sc != null && File.Exists(sc.BasePath) ? sc : null;
        }
        catch { return null; }
    }
}
