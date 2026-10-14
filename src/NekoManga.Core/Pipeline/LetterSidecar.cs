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
    List<TextBlockInfo> Blocks)
{
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
        File.WriteAllText(path, JsonSerializer.Serialize(this));
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
