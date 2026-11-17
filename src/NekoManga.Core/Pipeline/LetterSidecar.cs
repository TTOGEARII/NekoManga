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
    long SrcLen = 0,        // 원본 바이트 길이 — 폴더를 옮겨 경로가 달라져도 같은 원고인지 가리는 근거 (옛 사이드카는 0)
    double CommonSize = 0)  // 쪽 공통 글자 크기(px, 식자 전 원고 기준 — TextRenderer.CommonSize). 결과 창에서 고치기 시작한 쪽에만 적힌다:
                            // 풍선 하나의 글·자리를 고쳐도 다른 풍선의 자동 크기가 따라 바뀌지 않게 붙든 값. 0 = 식자할 때마다 구한다(옛 사이드카·자동 식자)
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

    /// <summary>json과 식자 전 원고(base)가 모두 있을 때만 돌려준다 — 다시 식자하려면 둘 다 필요하다.
    /// base는 **이 결과의 .neko 폴더**(<see cref="BasePathFor"/>)에서 먼저 찾고, 없으면 json에 적힌 경로에서 찾는다. 돌려주는 BasePath는 찾은 자리다.
    /// json의 BasePath는 절대 경로라 화 폴더를 옮기거나 드라이브 글자가 바뀌면 없는 곳을 가리킨다 — 적힌 경로만 보던 때는 그런 장을 다시 렌더링하면
    /// 식자 블록 없이 돌아 번역 글이 빠진 결과물로 덮었다(2026-11-13 점검에서 재현). 폴더를 복사해 온 경우에도 이 폴더의 것이 이 결과의 base다.</summary>
    public static LetterSidecar? Load(string outPath)
    {
        try
        {
            var sc = LoadRecord(outPath);
            if (sc == null) return null;
            var own = BasePathFor(outPath);
            if (File.Exists(own)) return string.Equals(sc.BasePath, own, StringComparison.OrdinalIgnoreCase) ? sc : sc with { BasePath = own };
            return File.Exists(sc.BasePath) ? sc : null;
        }
        catch { return null; }
    }

    /// <summary>json만 읽는다 — 식자 전 원고(base)가 있는지는 보지 않는다. 그 결과 자리가 누구의 것인지(Source·SrcLen)만 볼 때 쓴다:
    /// BasePath는 절대 경로라 출력 폴더를 옮기면 옛 폴더를 가리켜 <see cref="Load"/>는 null이 된다. 없거나 못 읽으면 null.</summary>
    internal static LetterSidecar? LoadRecord(string outPath)
    {
        try
        {
            var path = JsonPathFor(outPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<LetterSidecar>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
