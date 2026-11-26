namespace NekoManga.Core.Engine;

/// <summary>번역기에 글과 함께 넘기는 것(2026-11-26, CarrotMangaTranslator 참고 — 사용자 "번역 용어집 + 문맥 이어 보내기"):
/// 용어집(원문→번역, 반드시 이렇게), 앞 쪽의 대사(문맥 — 번역하지 않는다), 효과음 여부(짧은 의성어로 옮긴다).</summary>
public sealed record TranslateOptions(
    IReadOnlyList<(string Source, string Target)> Glossary,
    IReadOnlyList<(string Source, string Translation)> Context,
    bool Sfx)
{
    public static readonly TranslateOptions None = new([], [], false);
}

/// <summary>설정의 용어집 글(한 줄에 하나 "원문=번역" — 탭으로 갈라도 된다, #으로 시작하면 주석) 읽기와, 지시문을 못 주는 번역기(Google·DeepL)를 위한 원문 치환.</summary>
public static class Glossary
{
    public static List<(string Source, string Target)> Parse(string? text)
    {
        var list = new List<(string Source, string Target)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sep = line.IndexOf('\t');
            if (sep < 0) sep = line.IndexOf('=');
            if (sep <= 0) continue;
            var src = line[..sep].Trim();
            var tgt = line[(sep + 1)..].Trim();
            if (src.Length == 0 || tgt.Length == 0) continue;
            list.Add((src, tgt));
        }
        return list;
    }

    /// <summary>원문 속 용어를 번역어로 바꾼다 — 긴 원문부터(짧은 용어가 긴 용어의 일부일 때 긴 쪽이 먼저 맞아야 한다).
    /// 원문 문장에 다른 언어의 낱말이 끼는 셈이라 Google·DeepL이 그 앞뒤를 어색하게 옮길 수 있다 — LLM 번역기는 치환 대신 지시문으로 넘긴다.</summary>
    public static string ApplySource(string text, IReadOnlyList<(string Source, string Target)> glossary)
    {
        if (glossary.Count == 0 || string.IsNullOrEmpty(text)) return text;
        foreach (var (src, tgt) in glossary.OrderByDescending(g => g.Source.Length))
            text = text.Replace(src, tgt, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>번역문 검증(Carrot의 규칙을 따랐다): 비면 안 되고, 목표 언어의 글자가 있어야 하며 원문 글자(가나·한자)가 남으면 안 된다. 효과음은 <see cref="SfxMaxChars"/>자 안.
/// 목표가 일본어면 검사하지 않고, 중국어면 가나만 막는다(한자를 쓴다). 실패한 줄은 호출부가 다시 묻는다(EmbeddedTranslator).</summary>
public static class TranslationCheck
{
    /// <summary>효과음 번역의 글자 수 상한 — 한국 만화 효과음은 2~6자가 보통이고, 넘으면 번역기가 설명을 붙인 것이다("쾅! (문이 닫히는 소리)").</summary>
    public const int SfxMaxChars = 12;

    /// <param name="source">원문(있으면) — 한국어 목표에 한글이 없을 때, 원문도 가나·한자가 두 자 이하인 짧은 글("OK"·"3"·"!?")이면 옮길 것이 없어 그대로가 맞다(검수 2026-11-26: 안 그러면 줄마다 다시 묻고 Google까지 부른다).</param>
    public static bool IsAcceptable(string? translation, string targetLang, bool sfx, string? source = null)
    {
        var t = (translation ?? "").Trim();
        if (t.Length == 0) return false;
        if (sfx && t.Length > SfxMaxChars) return false;
        if (LangCode.IsJapanese(targetLang)) return true;
        if (LangCode.IsChinese(targetLang)) return !t.Any(IsKana);
        if (t.Any(c => IsKana(c) || IsKanji(c))) return false;
        if (LangCode.Primary(targetLang) == "ko" && !t.Any(c => c is >= '가' and <= '힣'))
            return source != null && source.Count(c => IsKana(c) || IsKanji(c)) <= 2;
        return true;
    }

    /// <summary>한자(CJK 통합 한자 + 확장 A) — EmbeddedTranslator.LooksLikeSource와 같은 범위.</summary>
    internal static bool IsKanji(char ch) => ch is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿');
    /// <summary>가나(히라가나·가타카나·반각 가타카나). 장음 기호 ー도 든다 — 한국어·영어 번역문은 PolishTranslation이 먼저 ~로 바꾼다.</summary>
    internal static bool IsKana(char ch) => ch is (>= 'ぁ' and <= 'ヿ') or (>= 'ｦ' and <= 'ﾟ');
}
