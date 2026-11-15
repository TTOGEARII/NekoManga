namespace NekoManga.Core.Decensor;

/// <summary>재생성 영역의 부위 종류 — 부위 감지(BodyPartDetector)가 검열 조각과 겹친 상자의 클래스에서. 검출이 없으면 Unknown(확장 경로).</summary>
public enum RegenKind { Unknown, Male, Female, Both }

/// <summary>신체부위 태그 고르기 (재생성 v3, 2026-11-12). 종류로 가르는 것은 한 줄 태그에 여성 태그가 늘 섞여 남성 부위가 이상하게 나오던 데서 나왔지만,
/// 삽입·손으로 쥔 장면에서는 부위 감지가 한쪽만 잡아 가려진 쪽이 안 그려졌고(v1.9.1), 그래서 남녀를 늘 같이 넣자(2026-11-13) 남성 부위만 있는 장에 여성 부위가 그려졌다.
/// 지금(2026-11-15 사용자 "남/여 구분을 확실하게" → "모든 페이지가 남/여 구별되서"): 검열 조각마다 성별을 가려(RegenRegion.PieceSex + 사용자가 고른 것)
/// **티끌 아닌 조각이 전부 같은 한쪽인 영역**(RegenRegionInfo.Sex)에만 그쪽 태그를, 나머지는 남녀를 같이. 끄는 설정은 AppSettings.RegenTagsBySex.
/// 남녀 조각이 섞인 영역을 자리마다 나눠 넣는 것은 RegenSexAreas(설정 RegenTagsPerPiece) — 그때도 자리마다의 태그는 여기 For가 고른다.</summary>
public static class RegenTags
{
    /// <summary>확정된 남성 영역 → 남성 태그, 확정된 여성 영역 → 여성 태그, 그 밖(확정 아님·둘 다·검출 없음·설정 끔) → "남성, 여성".
    /// 비어 있는 쪽은 뺀다. 결과는 기본 프롬프트 뒤에 ", "로 붙는다.</summary>
    public static string For(RegenKind kind, bool sure, AppSettings s)
    {
        string male = s.RegenBodyTagsMale.Trim(), female = s.RegenBodyTagsFemale.Trim();
        var parts = Chosen(kind, sure, s) switch
        {
            RegenKind.Male => new[] { male },
            RegenKind.Female => new[] { female },
            _ => new[] { male, female },
        };
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    /// <summary>진행 줄에 보이는 말 — 이 영역에 실제로 들어가는 태그("남성 태그"·"여성 태그"·"남녀 태그", 들어갈 것이 없으면 "부위 태그 없음").
    /// 비워 둔 칸은 치지 않는다: 결과 창에서 이 장의 여성 칸을 비우고 [지우기] 하면 "남성 태그"로 보인다.</summary>
    public static string Note(RegenKind kind, bool sure, AppSettings s)
    {
        var chosen = Chosen(kind, sure, s);
        bool male = chosen != RegenKind.Female && s.RegenBodyTagsMale.Trim().Length > 0;
        bool female = chosen != RegenKind.Male && s.RegenBodyTagsFemale.Trim().Length > 0;
        return male && female ? "남녀 태그" : male ? "남성 태그" : female ? "여성 태그" : "부위 태그 없음";
    }

    private static RegenKind Chosen(RegenKind kind, bool sure, AppSettings s) =>
        s.RegenTagsBySex && sure && kind is RegenKind.Male or RegenKind.Female ? kind : RegenKind.Both;

    public static RegenKind Combine(RegenKind a, RegenKind b) =>
        a == RegenKind.Unknown ? b : b == RegenKind.Unknown ? a : a == b ? a : RegenKind.Both;
}
