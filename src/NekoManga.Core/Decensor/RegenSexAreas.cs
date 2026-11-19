using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>자리별 태그의 자리 하나 — Sex: 이 자리의 성별(태그가 같은 자리를 합쳤으면 앞의 것), Tags: 이 자리에 넣는 부위 태그(기본 프롬프트 뒤에 붙는다, 빈 글이면 기본 프롬프트만),
/// Mask: 자리(보내는 크기 CV_8UC1, 255 = 이 자리). 자리들은 그림을 겹치지 않게 빈틈없이 덮는다.</summary>
public sealed record RegenTagArea(RegenKind Sex, string Tags, Mat Mask) : IDisposable
{
    public void Dispose() => Mask.Dispose();
}

/// <summary>자리별 태그 (2026-11-15) — 남성 조각과 여성 조각이 한 영역(자른 그림 한 장)에 같이 있을 때, 그림을 조각의 성별로 나눠 **자리마다 그 성별의 태그만** 넣는다.
/// 사용자: "앞으로 작업하는 모든 페이지가 남/여 구별되서 들어가야해". 태그가 영역 하나에 한 줄이면 남녀가 같이 있는 영역에는 남녀 태그를 같이 넣을 수밖에 없어 남성 부위 자리에도 여성 태그가 들어간다 —
/// 사용자 원고 44쪽(그림은 안 봄)의 조각 146개 중 97개는 한쪽 근거가 있는데 한쪽 태그를 받는 것은 50개였다(손을 경계로 위는 남성·아래는 여성인 장: 한 조각 남성 0.63, 다른 조각 여성 0.60).
/// 나누는 법: 조각 픽셀에 성별 값을 칠한 지도(RegenRegionInfo.SexMap)에서 모든 픽셀을 **가장 가까운 조각**의 성별로 — 남성 자리 / 여성 자리 / 남녀 자리(근거가 양쪽이거나 없는 조각).
/// 조각 밖까지 나누는 것은 2단계가 영역 전체를 다듬기 때문이고, 그림 전체를 덮는 것은 ComfyUI가 자리 프롬프트들의 결과를 마스크 가중 평균으로 섞기 때문이다(아무 자리도 아닌 곳은 0으로 나뉜다).
/// 그래프 쪽은 WorkflowBuilder.BuildRegenCrop(areaUpload, areaTags). 켜는 설정은 AppSettings.RegenTagsPerPiece.</summary>
public static class RegenSexAreas
{
    /// <summary>자리 마스크를 PNG 한 장에 싣는 채널 — 자리 순서대로(<see cref="Pack"/>). 그래프의 ImageToMask가 같은 이름으로 꺼낸다.</summary>
    public static readonly string[] Channels = ["red", "green", "blue"];

    /// <summary>이 영역을 창 <paramref name="w"/>로 잘라 보낼 때의 자리들 — 나눌 것이 없으면 null(한 줄 프롬프트로 간다):
    /// 설정이 꺼져 있거나(성별 구분·자리별 태그), 조각의 성별이 전부 같거나(지도 없음), 창 안에 보이는 성별이 하나뿐이거나, 태그가 다른 자리가 둘 미만일 때.</summary>
    public static List<RegenTagArea>? For(RegenRegionInfo region, RegenWindow w, AppSettings s)
    {
        if (!s.RegenTagsBySex || !s.RegenTagsPerPiece || region.SexMap == null) return null;
        using var roi = new Mat(region.SexMap, w.Window);
        using var crop = new Mat();
        // 값이 섞이지 않게 Nearest(지도의 값은 성별 번호다) — 마스크를 자를 때와 같다(RegenCrop.CropAndScale)
        Cv2.Resize(roi, crop, new Size(w.SendW, w.SendH), 0, 0, InterpolationFlags.Nearest);
        return Split(crop, s, region.Cum);
    }

    /// <summary>성별 지도(0 = 조각 아님, 그 밖은 RegenRegion의 MaleCode·FemaleCode·BothCode)를 자리로 나눈다. 태그가 같은 자리는 합친다
    /// (이 장의 여성 칸을 비웠으면 남성 자리와 남녀 자리가 같은 글이다). 태그가 다른 자리가 둘 미만이면 null.
    /// <paramref name="cum"/>: 이 영역에 질내사정인 조각이 있다 — 여성 자리·남녀 자리에 사정 태그를 더한다(남성 자리에는 넣지 않는다).</summary>
    public static List<RegenTagArea>? Split(Mat sexMap, AppSettings s, bool cum = false)
    {
        (byte Code, RegenKind Sex)[] kinds = [(RegenRegion.MaleCode, RegenKind.Male), (RegenRegion.FemaleCode, RegenKind.Female), (RegenRegion.BothCode, RegenKind.Both)];
        // 값마다 '그 값의 조각까지의 거리'를 재고, 픽셀마다 가장 가까운 값을 고른다
        using var owner = new Mat(sexMap.Size(), MatType.CV_8UC1, Scalar.Black);
        using var best = new Mat();
        int present = 0;
        foreach (var (code, _) in kinds)
        {
            using var away = new Mat();
            Cv2.Compare(sexMap, new Scalar(code), away, CmpTypes.NE);   // 그 값의 픽셀만 0 — DistanceTransform은 0까지의 거리를 잰다
            if (Cv2.CountNonZero(away) == away.Rows * away.Cols) continue;   // 이 그림에는 그 값의 조각이 없다
            using var dist = new Mat();
            Cv2.DistanceTransform(away, dist, DistanceTypes.L2, DistanceTransformMasks.Mask5);
            if (present++ == 0) { dist.CopyTo(best); owner.SetTo(new Scalar(code)); continue; }
            using var closer = new Mat();
            Cv2.Compare(dist, best, closer, CmpTypes.LT);
            dist.CopyTo(best, closer);
            owner.SetTo(new Scalar(code), closer);
        }
        if (present < 2) return null;

        var areas = new List<RegenTagArea>();
        foreach (var (code, sex) in kinds)
        {
            var mask = new Mat();
            Cv2.Compare(owner, new Scalar(code), mask, CmpTypes.EQ);
            if (Cv2.CountNonZero(mask) == 0) { mask.Dispose(); continue; }
            string tags = RegenTags.For(sex, sex != RegenKind.Both, s, cum);
            if (areas.FirstOrDefault(a => a.Tags == tags) is { } same) { Cv2.BitwiseOr(same.Mask, mask, same.Mask); mask.Dispose(); }
            else areas.Add(new RegenTagArea(sex, tags, mask));
        }
        if (areas.Count >= 2) return areas;
        foreach (var a in areas) a.Dispose();
        return null;
    }

    /// <summary>자리 마스크들을 그림 한 장의 채널로 — 첫 자리 = red, 둘째 = green, 셋째 = blue(<see cref="Channels"/>). 올릴 PNG의 재료(호출자가 해제).</summary>
    public static Mat Pack(IReadOnlyList<RegenTagArea> areas)
    {
        if (areas.Count is < 1 or > 3) throw new ArgumentException(Loc.T("자리는 하나에서 셋까지다냥."), nameof(areas));
        using var none = new Mat(areas[0].Mask.Size(), MatType.CV_8UC1, Scalar.Black);
        Mat At(int i) => i < areas.Count ? areas[i].Mask : none;
        var packed = new Mat();
        Cv2.Merge([At(2), At(1), At(0)], packed);   // OpenCV는 B·G·R 순서
        return packed;
    }

    /// <summary>진행 줄에 보이는 말 — "자리별 태그(남성·여성)". 자리마다 실제로 들어가는 태그를 말한다(RegenTags.Note와 같은 낱말).</summary>
    public static string Note(IReadOnlyList<RegenTagArea> areas, AppSettings s)
    {
        return Loc.F($"자리별 태그({string.Join("·", areas.Select(a => RegenTags.ShortNote(a.Sex, a.Sex != RegenKind.Both, s)))})");
    }
}
