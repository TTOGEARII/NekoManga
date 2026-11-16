using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>재생성(SDXL)·커스텀 ComfyUI 검열 제거가 **다시 그릴 범위** — 검열 조각(막대·흰선·모자이크)만이 아니라 그 조각이 덮은 부위 전체.
/// 사용자 요청(2026-11-12): "검은선·흰선·모자이크가 있는 음부 전체가 마스킹돼서 해당 부위가 전부 재생성됐으면."
///
/// 규칙(조각 = 마스크의 8연결 성분):
/// - 조각과 교차하는 부위 상자(BodyPartDetector의 penis/pussy)가 있으면 그 상자들을 각각 pad×(폭,높이)만큼 사방으로 넓힌 **사각형**을 채운다
///   (가장자리는 ComfyUI 쪽 MaskGrow 블러가 부드럽게 한다). 교차하지 않는 상자는 쓰지 않는다 — 검열이 없는 부위는 다시 그리지 않는다.
/// - 교차하는 상자가 없으면(모델이 없거나 못 찾음 — 흰선은 라벨 평가에서 64%가 이 경우) 조각의 상자를 긴 변 쪽으로 growRatio×길이,
///   짧은 변 쪽으로 max(growRatio×짧은 변, 0.4×긴 변)만큼 넓힌 사각형에 **내접하는 타원**을 채운다 — 납작한 막대가 납작한 띠로만 넓어지지 않게.
/// - 원래 마스크는 늘 OR 한다. 그림 밖은 잘린다. 저장 마스크(.mask.png)·감지 결과는 건드리지 않는다 — 호출자가 재생성 직전에만 쓴다.</summary>
public static class RegenRegion
{
    /// <summary>censorMask: CV_8UC1(255=검열). parts: 부위 상자(그림 좌표). 돌려주는 마스크는 새 Mat(호출자가 해제). (합집합 — 커스텀 워크플로·결과 창 미리보기용)</summary>
    public static Mat Build(Mat censorMask, IReadOnlyList<Rect> parts, double pad, double growRatio)
    {
        var region = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
        foreach (var piece in Pieces(censorMask)) DrawPiece(region, piece, parts.Select(p => (p, RegenKind.Unknown)).ToList(), pad, growRatio);
        Cv2.BitwiseOr(region, censorMask, region);
        return region;
    }

    /// <summary>성별의 **근거로만** 쓰는 약한 부위 상자의 하한. 영역의 모양과 종류는 임계(RegenRegionConf, 기본 0.12) 이상의 상자만 정하고,
    /// 그 아래 ~ 이 값까지의 상자는 조각의 성별(<see cref="PieceSex"/>)을 가릴 때만 본다.
    /// 시험 원고 23쪽(2026-11-15, 그림은 안 봄): 삽입 장면의 가려진 쪽이 0.07·0.12로 잡혔고, 남성 부위만 있는 장에는 여성 상자가 0.03에서도 하나도 없었다.</summary>
    public const double WeakConf = 0.05;

    /// <summary>성별 지도(<see cref="RegenRegionInfo.SexMap"/>)의 값 — 조각 픽셀에 그 조각의 성별. 근거가 양쪽이거나 없는 조각은 BothCode(남녀 태그 자리).</summary>
    public const byte MaleCode = 1, FemaleCode = 2, BothCode = 3;

    /// <summary>티끌의 기준 — 넓이가 그 영역 조각 넓이 합의 이 비율에 못 미치고, **부위 감지의 근거가 없고**, 사용자가 표시하지도 않은 조각은 영역의 성별을 정할 때 세지 않는다.
    /// 사용자 원고 b07(2026-11-15): 남성 0.76이 닿은 조각 넷 + 근거 없는 28px 티끌 하나 때문에 영역이 남녀 태그로 갔다(티끌은 조각 넓이의 0.1%대).
    /// 작아도 근거가 있는 조각은 뺄 수 없다 — 큰 모자이크 옆의 작은 조각이 다른 성별의 부위일 수 있다(검수 2026-11-15).</summary>
    public const double SpeckShare = 0.01;

    /// <summary>표시(<see cref="SexMark"/>)의 점이 조각 밖일 때 그 조각의 것으로 쳐 주는 거리(px) — 표시한 뒤에 마스크를 조금 지워 점이 조각 밖으로 나간 경우.</summary>
    public const int MarkReach = 24;

    /// <summary>재생성 v3: 영역마다 따로 — 조각(성분)마다 영역·지우기 마스크·종류를 만들고, 자를 창(RegenCrop.WindowFor)이 겹치는 영역끼리 합친다(마스크 OR, 종류 결합, 경계 합집합).
    /// Region·Erase는 그림 크기 CV_8UC1(호출자가 Dispose).
    /// <paramref name="parts"/>에는 약한 상자(<see cref="WeakConf"/> 이상)까지 넘긴다 — <paramref name="strongConf"/> 미만은 영역을 넓히거나 종류를 바꾸지 않고,
    /// 합친 뒤 조각마다 성별을 가리는 데만 쓴다(<see cref="RegenRegionInfo.Pieces"/>·<see cref="RegenRegionInfo.Sex"/>). <paramref name="marks"/>(사용자가 조각에 찍은 표시)도 태그만 정한다.
    /// strongConf에는 기본값을 두지 않는다 — 빠뜨리면 약한 상자가 말없이 영역을 넓힌다(복원 결과가 바뀐다).
    /// <paramref name="splitReach"/>: 0 이상이면 떨어진 자리를 따로 둔다 — 붙이는 범위(영역 상자 + 이 px)가 겹칠 때만 합친다(<see cref="SplitReachFor"/>, 설정 RegenSplitRegions). 음수면 옛 규칙(상대 창 안에 보이면 합친다).
    /// <paramref name="cum"/>: 질내사정의 자동 판단(<see cref="CumRule"/>) — 남성이 아닌 조각 가운데 사용자가 표시하지 않은 것만 묻는다. null이면 표시한 조각만 사정으로 친다. 영역의 모양은 바꾸지 않는다.</summary>
    public static List<RegenRegionInfo> BuildRegions(Mat censorMask, IReadOnlyList<BodyPart> parts, double pad, double growRatio, Size img, int cropSize, double strongConf,
        IReadOnlyList<SexMark>? marks = null, int splitReach = -1, CumRule? cum = null)
    {
        // 점수는 float, 설정의 임계는 double — float로 맞춰 견준다((float)0.12 < 0.12라 double로 견주면 감지기가 받던 0.12f짜리 상자가 약한 상자로 떨어진다)
        var typed = parts.Where(p => p.Conf >= (float)strongConf)
            .Select(p => (p.Box, p.Cls == BodyPartDetector.ClsPenis ? RegenKind.Male : p.Cls == BodyPartDetector.ClsPussy ? RegenKind.Female : RegenKind.Unknown)).ToList();
        var result = new List<RegenRegionInfo>();
        var pieces = new List<List<int>>();   // result와 나란히 — 그 영역에 든 조각들의 성분 번호(성별은 조각마다 본다)
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(censorMask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        for (int c = 1; c < n; c++)
        {
            var piece = BoxOf(stats, c);
            var region = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
            var kind = DrawPiece(region, piece, typed, pad, growRatio);
            var erase = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
            using (var roiSrc = new Mat(censorMask, piece)) using (var roiDst = new Mat(erase, piece)) roiSrc.CopyTo(roiDst);   // 조각 상자 안의 검열 픽셀만
            Cv2.BitwiseOr(region, erase, region);
            var bounds = BoundingBox(region);
            result.Add(new RegenRegionInfo(bounds, region, erase, kind));
            pieces.Add([c]);
        }
        // 한쪽 영역이 다른 쪽 창 안에 보이면 하나로 — 그 창에 검열 조각이 그대로 비치고, 되돌려 붙이는 범위(영역+reach)도 겹칠 수 있다.
        // 창끼리 겹치기만 해서는 안 합친다(최종 검수): 2000px 쪽의 x≈500·1500 조각은 창이 48px만 겹치는데 합치면 창 1.9k → 0.53배로 둘 다 흐려진다.
        // 순서대로 그리므로 앞 결과가 다음 창의 맥락이 되고, 붙이는 범위만 안 겹치면 서로 덮지 않는다
        // 떨어진 자리는 따로(splitReach ≥ 0, 2026-11-16): 위 규칙은 창이 최소 cropSize라 1813px 쪽에서 떨어진 자리끼리 줄줄이 합쳐 창이 쪽 전체가 된다 —
        // 사용자 원고 18쪽·영역 28개 중 12개를 줄여 보냈고(6개는 0.40~0.44배) 조각 밖의 선명도가 원고의 0.08(1.0배인 영역은 0.83), 선 유지 73%(94%)였다.
        // 붙이는 범위(상자 + reach)가 겹칠 때만 합치면 같은 원고에서 영역 28 → 46개, 1.0배가 16 → 39개(계획만 흉내 낸 수치). 생성 횟수가 그만큼 는다.
        // 상대의 검열 조각이 창에 그대로 비치는 것은 받아들인다(다시 그리는 것은 제 영역뿐이고, 뒤 영역의 창에는 앞 영역의 결과가 비친다)
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < result.Count && !merged; i++)
                for (int j = i + 1; j < result.Count; j++)
                {
                    if (splitReach >= 0)
                    {
                        if (!Overlaps(Grown(result[i].Bounds, splitReach), Grown(result[j].Bounds, splitReach))) continue;
                    }
                    else
                    {
                        var wi = RegenCrop.WindowFor(result[i].Bounds, img, cropSize).Window;
                        var wj = RegenCrop.WindowFor(result[j].Bounds, img, cropSize).Window;
                        if (!Overlaps(wi, result[j].Bounds) && !Overlaps(wj, result[i].Bounds)) continue;
                    }
                    Cv2.BitwiseOr(result[i].Region, result[j].Region, result[i].Region);
                    Cv2.BitwiseOr(result[i].Erase, result[j].Erase, result[i].Erase);
                    result[i] = result[i] with { Bounds = result[i].Bounds | result[j].Bounds, Kind = RegenTags.Combine(result[i].Kind, result[j].Kind) };
                    result[j].Dispose(); result.RemoveAt(j);
                    pieces[i].AddRange(pieces[j]); pieces.RemoveAt(j);
                    merged = true; break;
                }
        }
        var marked = MarkedSexes(labels, marks);
        var cumMarked = MarkedCums(labels, marks);
        for (int i = 0; i < result.Count; i++)
        {
            var list = pieces[i].Select(c => (C: c, Box: BoxOf(stats, c), Area: stats.At<int>(c, (int)ConnectedComponentsTypes.Area), ByUser: marked.ContainsKey(c))).ToList();
            long total = list.Sum(p => (long)p.Area);
            bool Tiny((int C, Rect Box, int Area, bool ByUser) p) => p.Area < SpeckShare * total;
            // 자잘한 조각뿐인 영역은 전부 센다(판단할 조각이 하나도 없으면 안 된다) — 넓이로만 본다: 표시 하나가 있다고 나머지가 전부 티끌로 빠지면 그 하나가 영역을 정해 버린다
            bool allTiny = list.All(Tiny);
            var info = list.Select(p =>
            {
                var auto = PieceSex(p.Box, parts, pad);
                // 티끌 = 작고, 근거가 없고, 사용자가 고르지도 않은 조각. 작아도 다른 성별로 잡힌 조각은 진짜 부위일 수 있어 뺄 수 없다
                bool speck = !allTiny && Tiny(p) && !p.ByUser && auto == RegenKind.Unknown && !cumMarked.ContainsKey(p.C);   // 사정을 고른 조각도 사용자가 고른 조각이다 — 티끌로 빠지면 그 표시가 말없이 버려진다(검수)
                return new RegenPiece(p.Box, p.Area, p.ByUser ? marked[p.C] : auto, p.ByUser, speck, AnchorOf(labels, p.C, p.Box), auto);
            }).ToList();
            var counted = Enumerable.Range(0, info.Count).Where(k => !info[k].Speck).ToList();
            var codes = counted.Select(k => CodeOf(info[k].Sex)).Distinct().ToList();
            // 티끌 아닌 조각이 전부 같은 한쪽이면 그쪽, 아니면 남녀를 같이(섞였거나 모른다)
            var sex = codes.Count == 1 && codes[0] != BothCode ? (codes[0] == MaleCode ? RegenKind.Male : RegenKind.Female) : RegenKind.Both;
            Mat? map = null;
            if (codes.Count > 1)   // 조각마다 성별이 다르다 — 자리별 태그(RegenSexAreas)가 쓸 지도: 조각 픽셀에 그 조각의 값. 티끌은 칠하지 않아 가까운 조각의 자리에 든다
            {
                map = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
                foreach (int k in counted)
                {
                    using var lab = new Mat(labels, info[k].Box); using var own = new Mat();
                    Cv2.InRange(lab, new Scalar(list[k].C), new Scalar(list[k].C), own);
                    using var dst = new Mat(map, info[k].Box);
                    dst.SetTo(new Scalar(CodeOf(info[k].Sex)), own);
                }
            }
            // 질내사정(2026-11-16): 여성 태그에 늘 들어 있던 'cum in pussy'를 떼어 조각마다 가린다 — 사용자가 표시한 조각은 그대로, 나머지는 자동 판단(없으면 아님).
            // 남성으로 정해진 조각과 티끌은 묻지 않는다(그 태그가 들어갈 자리가 아니다)
            var withCum = info.Select((p, k) =>
            {
                if (p.Speck || p.Sex == RegenKind.Male) return p;
                if (cumMarked.TryGetValue(list[k].C, out bool on)) return p with { Cum = on, CumByUser = true };
                bool auto = cum != null && cum.Score(p.Box) >= cum.Threshold;
                return p with { Cum = auto, CumAuto = auto };
            }).ToList();
            result[i] = result[i] with { Sex = sex, PieceList = withCum, SexMap = map, Cum = withCum.Any(p => p.Cum) };
        }
        return result;
    }

    /// <summary>조각 하나의 성별을 부위 감지의 근거로 — 조각의 상자에 닿는(상자를 여백 pad만큼 넓혀 겹친다) <see cref="WeakConf"/> 이상의 부위 상자가
    /// 남성뿐이면 Male, 여성뿐이면 Female, 둘 다면 Both, 없으면 Unknown.
    /// 사용자(2026-11-15): "남자거에 계속 여자거가 그려지는데 검열부분을 보고 남/여 구분을 확실하게 해서 재생성". 남녀 태그를 늘 같이 넣으면 남성 부위만 있는 장에 여성 부위가 그려지고,
    /// 감지한 쪽만 넣으면(v1.9.1) 삽입 장면의 가려진 쪽이 안 그려진다 — 그래서 **검열 조각마다** 근거를 본다. 한 조각에 양쪽 근거가 있거나 아무 근거도 없으면 가려진 다른 쪽이 있을 수 있다:
    /// 시험 원고에서 삽입 장면은 남성 0.59 + 같은 조각에 여성 0.07, 여성 0.27 + 옆 조각에는 아무 상자 없음(그 장은 '여자 것만 그려진다'는 보고가 있던 장이다)으로 나타났다.
    /// 임계(0.12) 이상의 상자가 하나도 없어도 약한 근거가 한쪽뿐이면 그쪽이다 — 사용자 원고 05·06쪽은 남성이 0.11·0.06으로만 잡혔다(전에는 그런 영역을 남녀 태그로 보냈다).
    /// 조각을 여백까지 넓혀 보는 것은 다시 그리는 사각형(상자 + 여백)과 같은 범위라서다 — 상자 바로 밖에 걸친 조각도 그 부위의 것이다.</summary>
    internal static RegenKind PieceSex(Rect piece, IReadOnlyList<BodyPart> parts, double pad)
    {
        bool Near(int cls) => parts.Any(p => p.Cls == cls && p.Conf >= WeakConf && Overlaps(Padded(p.Box, pad), piece));
        bool male = Near(BodyPartDetector.ClsPenis), female = Near(BodyPartDetector.ClsPussy);
        return male && female ? RegenKind.Both : male ? RegenKind.Male : female ? RegenKind.Female : RegenKind.Unknown;
    }

    /// <summary>결과 창에서 조각의 표시를 눌러 성별을 바꿀 때의 새 표시 목록 — <paramref name="pieceAnchor"/>가 가리키는 조각에 걸린 표시를 전부 걷고,
    /// <paramref name="sex"/>가 Male·Female·Both면 그 점에 새 표시를 둔다(null·Unknown = 자동으로 되돌림). 다른 조각의 표시와 어느 조각의 것도 아닌 표시는 그대로 둔다.
    /// 표시가 어느 조각의 것인지는 <see cref="BuildRegions"/>와 같은 규칙으로 가린다(점이 조각 위이거나 <see cref="MarkReach"/>px 안). 그 점에 조각이 없으면 아무것도 바꾸지 않고
    /// <paramref name="onPiece"/>가 false다(누르는 사이 마스크가 바뀌었다 — 결과 창이 '골랐어요'라고 말하지 않게).</summary>
    /// <summary>표시를 이 마스크의 조각에 맞춰 정리한다 — 표시가 걸린 조각마다 하나, 그 조각의 점(<see cref="RegenPiece.Anchor"/>)에, 조각 순서로. 어느 조각의 것도 아닌 표시는 버리고,
    /// 한 조각에 다른 표시가 둘이면 Both. 결과 창이 사이드카에 적는 꼴이자 '고른 것이 달라졌는가'를 견주는 꼴이다:
    /// 찍은 순서나 점의 자리가 달라도 같은 조각·같은 성별이면 같은 글이 된다(표시를 한 바퀴 돌려 제자리로 오면 고친 것이 아니다),
    /// 지운 조각의 표시가 남아 나중에 그 자리에 칠한 조각에 말없이 붙지 않는다.</summary>
    public static List<SexMark> ResolveMarks(Mat censorMask, IReadOnlyList<SexMark>? marks)
    {
        var resolved = new List<SexMark>();
        if (marks == null || marks.Count == 0) return resolved;
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(censorMask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var chosen = MarkedSexes(labels, marks);
        var cums = MarkedCums(labels, marks);
        for (int c = 1; c < n; c++)
        {
            if (!chosen.ContainsKey(c) && !cums.ContainsKey(c)) continue;
            var at = AnchorOf(labels, c, BoxOf(stats, c));
            if (chosen.TryGetValue(c, out var sex)) resolved.Add(new SexMark(at, sex));
            if (cums.TryGetValue(c, out bool on)) resolved.Add(new SexMark(at, RegenKind.Unknown, on));   // 사정 표시는 성별 표시 뒤에
        }
        return resolved;
    }

    public static List<SexMark> WithSex(Mat censorMask, IReadOnlyList<SexMark> marks, Point pieceAnchor, RegenKind? sex, out bool onPiece)
    {
        using var labels = new Mat();
        Cv2.ConnectedComponents(censorMask, labels, PixelConnectivity.Connectivity8);
        int c = LabelNear(labels, pieceAnchor);
        onPiece = c > 0;
        if (c <= 0) return [.. marks];
        var next = marks.Where(m => m.Cum != null || LabelNear(labels, m.At) != c).ToList();   // 그 조각의 사정 표시는 그대로 둔다
        if (sex is RegenKind.Male or RegenKind.Female or RegenKind.Both) next.Add(new SexMark(pieceAnchor, sex.Value));
        return next;
    }

    /// <summary><see cref="WithSex"/>의 사정판 — <paramref name="pieceAnchor"/>가 가리키는 조각의 사정 표시를 걷고, <paramref name="on"/>이 null이 아니면 그 점에 새 표시를 둔다(null = 자동으로 되돌림).
    /// 그 조각의 성별 표시와 다른 조각의 표시는 그대로 둔다.</summary>
    public static List<SexMark> WithCum(Mat censorMask, IReadOnlyList<SexMark> marks, Point pieceAnchor, bool? on, out bool onPiece)
    {
        using var labels = new Mat();
        Cv2.ConnectedComponents(censorMask, labels, PixelConnectivity.Connectivity8);
        int c = LabelNear(labels, pieceAnchor);
        onPiece = c > 0;
        if (c <= 0) return [.. marks];
        var next = marks.Where(m => m.Cum == null || LabelNear(labels, m.At) != c).ToList();
        if (on != null) next.Add(new SexMark(pieceAnchor, RegenKind.Unknown, on));
        return next;
    }

    /// <summary>조각(성분)마다 그 조각 안의 한 점(<see cref="RegenPiece.Anchor"/>와 같은 점) — 영역 계획 없이 성분만 센다. 결과 창이 조각 수를 세고 [모두 남성]·[모두 여성]의 표시를 찍는 데 쓴다.</summary>
    public static List<Point> PieceAnchors(Mat censorMask)
    {
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(censorMask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var anchors = new List<Point>(Math.Max(0, n - 1));
        for (int c = 1; c < n; c++) anchors.Add(AnchorOf(labels, c, BoxOf(stats, c)));
        return anchors;
    }

    /// <summary>성별 → 지도의 값. 근거가 양쪽이거나 없는 조각은 같은 자리(남녀 태그)다.</summary>
    internal static byte CodeOf(RegenKind sex) => sex switch { RegenKind.Male => MaleCode, RegenKind.Female => FemaleCode, _ => BothCode };

    /// <summary>표시가 가리키는 조각(성분 번호)마다 사용자가 고른 성별. 점이 조각 위면 그 조각, 아니면 <see cref="MarkReach"/>px 안의 가장 가까운 조각, 그것도 없으면 버린다.
    /// 한 조각에 다른 표시가 둘이면(따로 표시한 조각들을 칠해서 이었다) 남녀가 같이 있는 조각으로 본다.</summary>
    private static Dictionary<int, RegenKind> MarkedSexes(Mat labels, IReadOnlyList<SexMark>? marks)
    {
        var chosen = new Dictionary<int, RegenKind>();
        if (marks == null) return chosen;
        foreach (var m in marks)
        {
            if (m.Sex == RegenKind.Unknown || m.Cum != null) continue;
            int c = LabelNear(labels, m.At);
            if (c <= 0) continue;
            chosen[c] = chosen.TryGetValue(c, out var before) && before != m.Sex ? RegenKind.Both : m.Sex;
        }
        return chosen;
    }

    /// <summary>표시가 가리키는 조각(성분 번호)마다 사용자가 고른 사정 여부 — 조각을 찾는 법은 <see cref="MarkedSexes"/>와 같다.
    /// 한 조각에 '넣는다'와 '넣지 않는다'가 같이 걸리면(따로 표시한 조각들을 칠해서 이었다) 넣는다.</summary>
    private static Dictionary<int, bool> MarkedCums(Mat labels, IReadOnlyList<SexMark>? marks)
    {
        var chosen = new Dictionary<int, bool>();
        if (marks == null) return chosen;
        foreach (var m in marks)
        {
            if (m.Cum is not bool on) continue;
            int c = LabelNear(labels, m.At);
            if (c <= 0) continue;
            chosen[c] = on || (chosen.TryGetValue(c, out bool before) && before);
        }
        return chosen;
    }

    private static int LabelNear(Mat labels, Point at)
    {
        if (at.X >= 0 && at.Y >= 0 && at.X < labels.Cols && at.Y < labels.Rows && labels.At<int>(at.Y, at.X) is > 0 and var on) return on;
        int best = 0; long bestD = (long)MarkReach * MarkReach + 1;
        long y0 = Math.Max(0, (long)at.Y - MarkReach), y1 = Math.Min(labels.Rows - 1, (long)at.Y + MarkReach);
        long x0 = Math.Max(0, (long)at.X - MarkReach), x1 = Math.Min(labels.Cols - 1, (long)at.X + MarkReach);
        for (long y = y0; y <= y1; y++)
            for (long x = x0; x <= x1; x++)
            {
                int c = labels.At<int>((int)y, (int)x);
                if (c <= 0) continue;
                long d = (x - at.X) * (x - at.X) + (y - at.Y) * (y - at.Y);
                if (d < bestD) { bestD = d; best = c; }
            }
        return best;
    }

    /// <summary>조각 안의 한 점 — 결과 창이 표시를 그리고, 사용자가 고르면 이 점으로 기억한다. 상자의 가운데 줄에서 그 조각의 가장 긴 구간의 가운데
    /// (8연결 성분은 상자의 모든 줄을 지나므로 가운데 줄에 그 조각의 픽셀이 반드시 있다 — 상자 가운데는 고리·ㄱ자 모양에서 조각 밖일 수 있다).</summary>
    private static Point AnchorOf(Mat labels, int c, Rect box)
    {
        int y = box.Y + box.Height / 2, end = box.X + box.Width, bestX = box.X, bestLen = 0, run = 0;
        for (int x = box.X; x <= end; x++)
        {
            bool on = x < end && labels.At<int>(y, x) == c;
            if (on) { run++; continue; }
            if (run > bestLen) { bestLen = run; bestX = x - run + run / 2; }
            run = 0;
        }
        return new Point(bestX, y);
    }

    private static Rect BoxOf(Mat stats, int c) => new(
        stats.At<int>(c, (int)ConnectedComponentsTypes.Left), stats.At<int>(c, (int)ConnectedComponentsTypes.Top),
        stats.At<int>(c, (int)ConnectedComponentsTypes.Width), stats.At<int>(c, (int)ConnectedComponentsTypes.Height));

    private static Rect Padded(Rect box, double pad)
    {
        int dx = (int)Math.Round(box.Width * pad), dy = (int)Math.Round(box.Height * pad);
        return new Rect(box.X - dx, box.Y - dy, box.Width + 2 * dx, box.Height + 2 * dy);
    }

    /// <summary>1단계가 지울 조각을 **검은 막대 픽셀만**으로 줄인다(새 Mat, 호출자가 해제). 감지 마스크는 막대보다 넓다 — 사용자 원고 6쪽 실측(2026-11-12)에서
    /// 조각 픽셀의 40~70%만 검었다. 통째로 지우면(1단계 denoise 1.0 + 조각 자리 선화 지움) 막대 사이로 보이던 윤곽·쥔 손까지 사라져 모델이 모양을 지어낸다.
    /// 조각(8연결 성분)마다: 어두운(gray ≤ darkBelow) 픽셀을 7px 타원으로 열어 가는 선(그림)을 빼고 2px 넓힌 것이 조각의 minShare 이상이면 그것만,
    /// 아니면(흰 막대·모자이크·손으로 칠한 자리 — 검은 덩어리가 없다) 조각을 그대로 둔다.</summary>
    public static Mat TightenErase(Mat img, Mat erase, int darkBelow = 48, double minShare = 0.15)
    {
        var result = new Mat(erase.Size(), MatType.CV_8UC1, Scalar.Black);
        using var gray = img.Channels() == 1 ? img.Clone() : img.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, darkBelow, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(dark, erase, dark);
        using (var open = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7))) Cv2.MorphologyEx(dark, dark, MorphTypes.Open, open);
        using (var grow = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5))) Cv2.Dilate(dark, dark, grow);   // 압축(webp·jpg)으로 번진 막대 테두리
        Cv2.BitwiseAnd(dark, erase, dark);
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(erase, labels, stats, centroids, PixelConnectivity.Connectivity8);
        for (int i = 1; i < n; i++)
        {
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            using var lab = new Mat(labels, box); using var piece = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), piece);
            using var bar = new Mat(); using var darkRoi = new Mat(dark, box);
            Cv2.BitwiseAnd(darkRoi, piece, bar);
            using var dst = new Mat(result, box);
            Cv2.BitwiseOr(dst, Cv2.CountNonZero(bar) >= minShare * area ? bar : piece, dst);
        }
        return result;
    }

    private static bool Overlaps(Rect a, Rect b) { var i = a & b; return i.Width > 0 && i.Height > 0; }

    private static Rect Grown(Rect r, int px) => new(r.X - px, r.Y - px, r.Width + px * 2, r.Height + px * 2);

    /// <summary>재생성 결과를 되돌려 붙일 때 영역 밖으로 더 가져오는 폭(px) — 가장자리를 섞는 마스크(넓힘 + 흐림)가 닿는 데까지 + 2. 러너(ComfyRegenDecensorAsync)가 붙이는 범위와 같은 값이어야 한다.</summary>
    public static int PasteReach(AppSettings s) => Math.Max(s.RegenEdgeGrow + s.RegenEdgeBlur, s.RegenCoreGrow + s.RegenCoreBlur) + 2;

    /// <summary><see cref="BuildRegions"/>의 splitReach — '떨어진 자리는 따로'(RegenSplitRegions)가 켜져 있으면 붙이는 폭, 꺼져 있으면 -1(옛 규칙).
    /// 영역 계획을 만드는 곳(러너·결과 창의 조각 표시)은 전부 이것을 넘긴다 — 한쪽만 빠뜨리면 창에 보이는 묶음과 실제로 그리는 묶음이 달라진다.</summary>
    public static int SplitReachFor(AppSettings s) => s.RegenSplitRegions ? PasteReach(s) : -1;

    private static IEnumerable<Rect> Pieces(Mat censorMask)
    {
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(censorMask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        for (int i = 1; i < n; i++)
            yield return new Rect(
                stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
    }

    /// <summary>조각 하나의 영역을 region에 그린다 — 교차하는 부위 상자(여백)로, 없으면 확장 타원으로. 돌려주는 값은 교차한 상자들의 종류(없으면 Unknown).</summary>
    private static RegenKind DrawPiece(Mat region, Rect piece, IReadOnlyList<(Rect Box, RegenKind Kind)> parts, double pad, double growRatio)
    {
        var bounds = new Rect(0, 0, region.Cols, region.Rows);
        var kind = RegenKind.Unknown; bool any = false;
        foreach (var (p, k) in parts)
        {
            var inter = piece & p;
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            any = true; kind = RegenTags.Combine(kind, k);
            int dx = (int)Math.Round(p.Width * pad), dy = (int)Math.Round(p.Height * pad);
            var r = new Rect(p.X - dx, p.Y - dy, p.Width + 2 * dx, p.Height + 2 * dy) & bounds;
            if (r.Width > 0 && r.Height > 0) region.Rectangle(r, Scalar.White, -1);
        }
        if (!any && growRatio > 0)
        {
            bool wide = piece.Width >= piece.Height;
            int lng = wide ? piece.Width : piece.Height, srt = wide ? piece.Height : piece.Width;
            int growLong = (int)Math.Round(lng * growRatio);
            int growShort = (int)Math.Round(Math.Max(srt * growRatio, 0.4 * lng));
            int gx = wide ? growLong : growShort, gy = wide ? growShort : growLong;
            var box = new Rect(piece.X - gx, piece.Y - gy, piece.Width + 2 * gx, piece.Height + 2 * gy);
            Cv2.Ellipse(region, new Point(box.X + box.Width / 2.0, box.Y + box.Height / 2.0),
                new Size(box.Width / 2.0, box.Height / 2.0), 0, 0, 360, Scalar.White, -1, LineTypes.Link8);   // 그림 밖은 OpenCV가 자른다
        }
        return kind;
    }

    private static Rect BoundingBox(Mat mask)
    {
        using var pts = new Mat();
        Cv2.FindNonZero(mask, pts);   // 비어 있지 않다(조각이 들어 있다)
        return Cv2.BoundingRect(pts);
    }
}

/// <summary>재생성 v3의 영역 하나 — Bounds: 영역 경계(그림 좌표), Region: 다시 그릴 범위, Erase: 지울 검열 조각, Kind: 부위 종류(임계 이상의 부위 상자에서 — 영역의 모양과 같이 정해진다),
/// Sex: 태그를 고르는 성별 — 티끌 아닌 조각이 전부 같은 한쪽이면 Male·Female(그쪽 태그만), 아니면 Both(남녀 같이). 조각의 성별은 약한 근거와 사용자의 표시까지 본 것이라 Kind와 다를 수 있다.
/// PieceList: 영역에 든 조각들. SexMap: 조각마다 성별이 다를 때만 — 그림 크기 CV_8UC1, 조각 픽셀에 RegenRegion.MaleCode·FemaleCode·BothCode(자리별 태그의 재료).</summary>
public sealed record RegenRegionInfo(Rect Bounds, Mat Region, Mat Erase, RegenKind Kind, RegenKind Sex = RegenKind.Both, IReadOnlyList<RegenPiece>? PieceList = null, Mat? SexMap = null,
    bool Cum = false) : IDisposable   // Cum: 이 영역의 조각 가운데 질내사정인 것이 있다 — 여성 태그가 들어가는 자리에 사정 태그를 더한다(RegenTags.For)
{
    /// <summary>한쪽 성별로 정해졌다 — 그쪽 태그만 넣는다(RegenTags.For).</summary>
    public bool Sure => Sex is RegenKind.Male or RegenKind.Female;
    public IReadOnlyList<RegenPiece> Pieces => PieceList ?? [];
    public void Dispose() { Region.Dispose(); Erase.Dispose(); SexMap?.Dispose(); }
}

/// <summary>영역 안의 검열 조각 하나(마스크의 8연결 성분) — Box: 상자, Area: 픽셀 수, Sex: 성별(사용자가 고른 것이 있으면 그것, 없으면 부위 감지의 근거 — 근거가 없으면 Unknown),
/// ByUser: 사용자가 고른 것, Speck: 티끌(영역의 성별을 정할 때 세지 않는다 — RegenRegion.SpeckShare), Anchor: 조각 안의 한 점(표시를 그리고 기억하는 자리),
/// Auto: 부위 감지의 근거만으로 본 성별(사용자가 고른 것이 없으면 Sex와 같다 — 결과 창이 '자동이면 무엇'을 알려 준다).</summary>
/// <param name="Cum">질내사정으로 친다(사정 태그를 넣는다).</param><param name="CumByUser">사용자가 고른 것.</param>
/// <param name="CumAuto">자동 판단의 결과 — 사용자가 고른 조각은 묻지 않아 false다(결과 창은 '자동'으로 되돌린 뒤에 다시 센다).</param>
public readonly record struct RegenPiece(Rect Box, int Area, RegenKind Sex, bool ByUser, bool Speck, Point Anchor, RegenKind Auto = RegenKind.Unknown,
    bool Cum = false, bool CumByUser = false, bool CumAuto = false);

/// <summary>질내사정의 자동 판단 — Score: 조각 상자(그림 좌표)를 받아 0~1의 점수(Engine.CumDetector), Threshold: 이 점수 이상이면 사정으로 친다(AppSettings.RegenCumThreshold).</summary>
public sealed record CumRule(Func<Rect, float> Score, double Threshold);
