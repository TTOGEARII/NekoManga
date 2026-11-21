using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>재생성 결과의 '평평한 흰 조각' 판정과 다시 그리기의 시드 (2026-11-20, 사용자 "0.25로 했을 땐 괜찮았는데 또 흰색으로 칠해진다").
/// 1단계(조각 인페인트)가 막대 자리를 종이처럼 평평한 흰 칠로 메우는 조각이 어느 CN 강도에서든 3분의 1쯤 나온다 — 같은 시드 A/B(11-19·11-20: CN 0 / 0.25 / 0.5에서 5·6·7/22)와
/// 새 시드로 전체를 다시 돌린 결과(5/14)가 같다. 시드 운이라 설정으로는 줄지 않으니, 결과를 재서 흰 조각이 있으면 새 시드로 다시 그리고 덜 흰 것을 고른다(PipelineRunner.ComfyRegenDecensorAsync).
/// 기준은 하네스(regen_stats3.py·regenab Measure)와 같다: 조각(지우기 마스크의 8연결 성분, 300px 이상)마다 새로 그린 자리(막대 픽셀 — 없으면 조각 전체)의 ≥235 픽셀 비율이 0.6 이상.
/// 28쪽 첫 조각처럼 밝은 자리는 시드를 바꿔도 흰 채라(세 시드 모두 0.67~0.76) 재시도 뒤에도 남을 수 있다 — 그래서 '가장 덜 흰 것'을 고르고 끝낸다.</summary>
public static class RegenWhiteCheck
{
    /// <summary>이 밝기(회색) 이상이면 '흰 픽셀'.</summary>
    public const int WhiteLevel = 235;
    /// <summary>새로 그린 자리의 흰 픽셀 비율이 이 이상이면 '평평한 흰 조각'.</summary>
    public const double FlatRatio = 0.6;
    /// <summary>이보다 작은 성분은 티끌로 보고 세지 않는다(하네스와 같음).</summary>
    public const int MinPiecePx = 300;

    /// <summary>조각마다 새로 그린 자리의 흰 픽셀 비율 — 지우기 마스크의 성분 순서(라벨 순).
    /// <paramref name="tight"/>(막대 픽셀만 지운 마스크, RegenEraseTight)가 있으면 조각 안의 그 픽셀만 잰다 — 막대 사이에 남긴 원고의 흰 종이를 흰 칠로 세지 않게.
    /// 조각 안에 그 픽셀이 없으면 조각 전체로 잰다(검은 픽셀이 없는 조각은 TightenErase가 통째로 남기므로 보통은 생기지 않는다 — 안전망). 세 그림은 같은 크기(창 크기)여야 한다.</summary>
    public static List<double> PieceWhiteRatios(Mat result, Mat erase, Mat? tight)
    {
        using var gray = result.Channels() == 1 ? result.Clone() : result.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var bin = erase.Threshold(127, 255, ThresholdTypes.Binary);
        using var white = gray.Threshold(WhiteLevel - 1, 255, ThresholdTypes.Binary);   // > 234 = ≥ 235
        using var labels = new Mat(); using var stats = new Mat(); using var cent = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cent, PixelConnectivity.Connectivity8);
        var ratios = new List<double>();
        for (int i = 1; i < n; i++)
        {
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < MinPiecePx) continue;
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            using var lab = new Mat(labels, box); using var piece = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), piece);
            using var drawn = new Mat();
            if (tight != null)
            {
                using var tb = new Mat(tight, box); using var tbin = tb.Threshold(127, 255, ThresholdTypes.Binary);
                Cv2.BitwiseAnd(piece, tbin, drawn);
            }
            if (tight == null || Cv2.CountNonZero(drawn) == 0) piece.CopyTo(drawn);
            int px = Cv2.CountNonZero(drawn);
            using var w = new Mat(white, box); using var hit = new Mat();
            Cv2.BitwiseAnd(w, drawn, hit);
            ratios.Add(Cv2.CountNonZero(hit) / (double)px);
        }
        return ratios;
    }

    /// <summary>평평한 흰 조각의 수.</summary>
    public static int FlatWhiteCount(IReadOnlyList<double> ratios) => ratios.Count(r => r >= FlatRatio);

    /// <summary>다시 그린 결과가 지금 것보다 나은가 — 흰 조각이 적으면, 같으면 가장 흰 조각부터 차례로 견줘 먼저 덜 흰 쪽(가장 흰 값만 보면 다른 조각이 덜 흰 결과를 버린다 — 검수 2026-11-20). 전부 같으면 바꾸지 않는다(먼저 것을 둔다).</summary>
    public static bool IsBetter(IReadOnlyList<double> candidate, IReadOnlyList<double> best)
    {
        int fc = FlatWhiteCount(candidate), fb = FlatWhiteCount(best);
        if (fc != fb) return fc < fb;
        var c = candidate.OrderByDescending(x => x).ToList(); var b = best.OrderByDescending(x => x).ToList();
        for (int i = 0; i < Math.Min(c.Count, b.Count); i++)
            if (c[i] != b[i]) return c[i] < b[i];
        return false;
    }

    /// <summary>attempt번째 시도의 시드(0 = 첫 시드 그대로) — 첫 시드에서 끌어내므로 설정에서 시드를 고정한 사람도 같은 결과를 다시 얻는다. 범위는 RegenSeedFor와 같다(uint 안, 2단계가 +1을 쓴다).</summary>
    public static long RetrySeed(long first, int attempt) => attempt == 0 ? first : (first + attempt * 7_919_003L) % uint.MaxValue;
}
