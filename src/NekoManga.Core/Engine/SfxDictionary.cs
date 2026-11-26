using System.Text;
using System.Text.Json;

namespace NekoManga.Core.Engine;

/// <summary>내장 의성어·의태어 사전(2026-11-26, 효과음 식자 시험 기능 — CarrotMangaTranslator 참고). 흔한 효과음은 번역기에 보내기 전에 여기서 바로 옮긴다:
/// 번역기(특히 Google)는 "ドキドキ"를 설명문으로 옮기거나 원문을 그대로 돌려주기 일쑤다. 표는 <c>Lang\sfx_ja_ko.json</c>(한국어뿐) — 다른 목표 언어는 null(번역기로).
/// 찾기는 <see cref="Normalize"/>로 맞춘다(NFKC·가타카나→히라가나·문장부호와 기호 떼기·장음 하나로·끝 장음 떼기), 되풀이("ドキドキドキ")는 단위로 접어 표의 번역을 그만큼 되풀이한다.</summary>
public static class SfxDictionary
{
    private static readonly Lazy<Dictionary<string, string>> Ko = new(() => Load("sfx_ja_ko.json"));

    public static string? Lookup(string source, string targetLang)
    {
        if (LangCode.Primary(targetLang) != "ko") return null;
        var key = Normalize(source);
        if (key.Length == 0) return null;
        var table = Ko.Value;
        if (table.TryGetValue(key, out var hit)) return hit;

        // 되풀이: 가장 짧은 단위 u를 k번 — 표에 u·u×2·u×3이 있으면 그 번역(의 단위)을 같은 수만큼(최대 6)
        for (int ul = 1; ul <= key.Length / 2; ul++)
        {
            if (key.Length % ul != 0) continue;
            var unit = key[..ul];
            int k = key.Length / ul;
            if (string.Concat(Enumerable.Repeat(unit, k)) != key) continue;
            for (int m = 1; m <= 3 && m < k; m++)
            {
                if (!table.TryGetValue(string.Concat(Enumerable.Repeat(unit, m)), out var ko)) continue;
                var koUnit = UnitOf(ko, m);
                return koUnit == null ? ko : string.Concat(Enumerable.Repeat(koUnit, Math.Min(k, 6)));
            }
            break;
        }
        return null;
    }

    /// <summary>표의 번역 <paramref name="ko"/>가 같은 조각 m개면 그 조각("두근두근", 2 → "두근"). 아니면 null(되풀이하지 않고 그대로 쓴다).</summary>
    private static string? UnitOf(string ko, int m)
    {
        if (m == 1) return ko;
        if (ko.Length % m != 0) return null;
        var u = ko[..(ko.Length / m)];
        return string.Concat(Enumerable.Repeat(u, m)) == ko ? u : null;
    }

    /// <summary>찾기 열쇠: NFKC(반각 가타카나·전각 기호) → 가타카나를 히라가나로 → 빈칸·문장부호·기호(!?…♡)는 떼고 → 장음(ー·〜·～·-)은 하나로 접되 남기고(ガーン ≠ ガン) → 끝의 장음은 뗀다(ドキドキ〜 = ドキドキ).</summary>
    public static string Normalize(string s)
    {
        var t = (s ?? "").Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(t.Length);
        foreach (var raw in t)
        {
            char ch = raw;
            if (ch is 'ー' or '〜' or '～' or '~' or '-' or '―' or '－')   // NFKC가 ～(U+FF5E)를 ~로 바꾸므로 ~도 장음(검수 2026-11-26)
            {
                if (sb.Length > 0 && sb[^1] != 'ー') sb.Append('ー');
                continue;
            }
            if (ch is >= 'ァ' and <= 'ヶ') ch = (char)(ch - 0x60);   // 가타카나 ァ(U+30A1)…ヶ(U+30F6) → 히라가나 ぁ(U+3041)…
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsControl(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        while (sb.Length > 0 && sb[^1] == 'ー') sb.Length--;
        return sb.ToString();
    }

    private static Dictionary<string, string> Load(string file)
    {
        var table = new Dictionary<string, string>();
        using var stream = typeof(SfxDictionary).Assembly.GetManifestResourceStream($"NekoManga.Core.Lang.{file}");
        if (stream == null) return table;
        using var doc = JsonDocument.Parse(stream);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.StartsWith('_') || p.Value.ValueKind != JsonValueKind.String) continue;   // "_"는 설명
            var key = Normalize(p.Name);
            var value = (p.Value.GetString() ?? "").Trim();
            if (key.Length > 0 && value.Length > 0) table.TryAdd(key, value);   // 정규화해 겹치는 열쇠("バーン"·"バーン!")는 먼저 적은 것
        }
        return table;
    }
}
