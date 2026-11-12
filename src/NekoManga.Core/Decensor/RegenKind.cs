namespace NekoManga.Core.Decensor;

/// <summary>재생성 영역의 부위 종류 — 부위 감지(BodyPartDetector)가 검열 조각과 겹친 상자의 클래스에서. 검출이 없으면 Unknown(확장 경로).</summary>
public enum RegenKind { Unknown, Male, Female, Both }

/// <summary>종류별 신체부위 태그 고르기 (재생성 v3, 2026-11-12) — 한 줄 태그에 여성 태그가 늘 섞여 남성 부위가 이상하게 나오던 것을 종류로 가른다.</summary>
public static class RegenTags
{
    /// <summary>Male→남성, Female→여성, Both·Unknown→"남성, 여성"(비어 있는 쪽은 뺀다). 결과는 기본 프롬프트 뒤에 ", "로 붙는다.</summary>
    public static string For(RegenKind kind, AppSettings s)
    {
        string male = s.RegenBodyTagsMale.Trim(), female = s.RegenBodyTagsFemale.Trim();
        var parts = kind switch
        {
            RegenKind.Male => new[] { male },
            RegenKind.Female => new[] { female },
            _ => new[] { male, female },
        };
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    public static RegenKind Combine(RegenKind a, RegenKind b) =>
        a == RegenKind.Unknown ? b : b == RegenKind.Unknown ? a : a == b ? a : RegenKind.Both;
}
