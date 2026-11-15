using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>사용자가 검열 조각에 찍은 성별 표시 — At: 그 조각 안의 한 점(마스크 좌표), Sex: Male·Female·Both(남녀가 같이 있는 조각).
/// 부위 감지가 못 잡는 조각(사용자 원고 146조각 중 23개, 2026-11-15)과 잘못 잡은 조각을 사용자가 결과 창에서 직접 고른다 — "앞으로 작업하는 모든 페이지가 남/여 구별되서 들어가야해".
/// 조각을 번호가 아니라 점으로 기억하는 것은 마스크를 고치면 조각의 수·순서가 바뀌기 때문이다: 점은 그 자리에 있는 조각을 따라간다(RegenRegion.BuildRegions가 맞춘다).</summary>
public readonly record struct SexMark(Point At, RegenKind Sex);

/// <summary>표시 목록 ↔ 마스크 사이드카에 적는 글(MaskSidecar.RegenSex) — "x,y,M;x,y,F;x,y,B". 글로 두는 것은 시드와 같다(레코드 비교가 값으로 되게).</summary>
public static class SexMarks
{
    /// <summary>적을 것이 없으면 null(기록 없음). '자동'(Unknown)은 표시가 없는 것이라 적지 않는다.</summary>
    public static string? Format(IReadOnlyList<SexMark> marks)
    {
        var parts = marks.Where(m => Letter(m.Sex) != null).Select(m => $"{m.At.X},{m.At.Y},{Letter(m.Sex)}").ToList();
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    /// <summary>망가진 칸(숫자가 아님·음수·모르는 글자·칸 수가 다름)만 버리고 나머지는 읽는다 — 시드와 달리 칸끼리 순서로 묶여 있지 않다.</summary>
    public static IReadOnlyList<SexMark> Parse(string? text)
    {
        var marks = new List<SexMark>();
        if (string.IsNullOrWhiteSpace(text)) return marks;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var entry in text.Split(';'))
        {
            var f = entry.Split(',');
            if (f.Length != 3) continue;
            if (!int.TryParse(f[0].Trim(), System.Globalization.NumberStyles.None, inv, out int x)) continue;
            if (!int.TryParse(f[1].Trim(), System.Globalization.NumberStyles.None, inv, out int y)) continue;
            RegenKind? sex = f[2].Trim().ToUpperInvariant() switch { "M" => RegenKind.Male, "F" => RegenKind.Female, "B" => RegenKind.Both, _ => null };
            if (sex != null) marks.Add(new SexMark(new Point(x, y), sex.Value));
        }
        return marks;
    }

    /// <summary>다른 크기의 그림으로 옮긴다 — 표시는 마스크(원본) 좌표인데 채색된 장의 2차는 더 큰 그림에서 돈다.</summary>
    public static IReadOnlyList<SexMark> Scaled(IReadOnlyList<SexMark> marks, double sx, double sy) =>
        sx == 1.0 && sy == 1.0 ? marks : marks.Select(m => m with { At = new Point((int)Math.Round(m.At.X * sx), (int)Math.Round(m.At.Y * sy)) }).ToList();

    private static string? Letter(RegenKind sex) => sex switch { RegenKind.Male => "M", RegenKind.Female => "F", RegenKind.Both => "B", _ => null };
}
