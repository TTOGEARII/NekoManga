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

    /// <summary>재생성 v3: 영역마다 따로 — 조각(성분)마다 영역·지우기 마스크·종류를 만들고, 자를 창(RegenCrop.WindowFor)이 겹치는 영역끼리 합친다(마스크 OR, 종류 결합, 경계 합집합).
    /// Region·Erase는 그림 크기 CV_8UC1(호출자가 Dispose).</summary>
    public static List<RegenRegionInfo> BuildRegions(Mat censorMask, IReadOnlyList<BodyPart> parts, double pad, double growRatio, Size img, int cropSize)
    {
        var typed = parts.Select(p => (p.Box, p.Cls == BodyPartDetector.ClsPenis ? RegenKind.Male : p.Cls == BodyPartDetector.ClsPussy ? RegenKind.Female : RegenKind.Unknown)).ToList();
        var result = new List<RegenRegionInfo>();
        foreach (var piece in Pieces(censorMask))
        {
            var region = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
            var kind = DrawPiece(region, piece, typed, pad, growRatio);
            var erase = new Mat(censorMask.Size(), MatType.CV_8UC1, Scalar.Black);
            using (var roiSrc = new Mat(censorMask, piece)) using (var roiDst = new Mat(erase, piece)) roiSrc.CopyTo(roiDst);   // 조각 상자 안의 검열 픽셀만
            Cv2.BitwiseOr(region, erase, region);
            var bounds = BoundingBox(region);
            result.Add(new RegenRegionInfo(bounds, region, erase, kind));
        }
        // 한쪽 영역이 다른 쪽 창 안에 보이면 하나로 — 그 창에 검열 조각이 그대로 비치고, 되돌려 붙이는 범위(영역+reach)도 겹칠 수 있다.
        // 창끼리 겹치기만 해서는 안 합친다(최종 검수): 2000px 쪽의 x≈500·1500 조각은 창이 48px만 겹치는데 합치면 창 1.9k → 0.53배로 둘 다 흐려진다.
        // 순서대로 그리므로 앞 결과가 다음 창의 맥락이 되고, 붙이는 범위만 안 겹치면 서로 덮지 않는다
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < result.Count && !merged; i++)
                for (int j = i + 1; j < result.Count; j++)
                {
                    var wi = RegenCrop.WindowFor(result[i].Bounds, img, cropSize).Window;
                    var wj = RegenCrop.WindowFor(result[j].Bounds, img, cropSize).Window;
                    if (!Overlaps(wi, result[j].Bounds) && !Overlaps(wj, result[i].Bounds)) continue;
                    Cv2.BitwiseOr(result[i].Region, result[j].Region, result[i].Region);
                    Cv2.BitwiseOr(result[i].Erase, result[j].Erase, result[i].Erase);
                    result[i] = result[i] with { Bounds = result[i].Bounds | result[j].Bounds, Kind = RegenTags.Combine(result[i].Kind, result[j].Kind) };
                    result[j].Dispose(); result.RemoveAt(j);
                    merged = true; break;
                }
        }
        return result;
    }

    private static bool Overlaps(Rect a, Rect b) { var i = a & b; return i.Width > 0 && i.Height > 0; }

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

/// <summary>재생성 v3의 영역 하나 — Bounds: 영역 경계(그림 좌표), Region: 다시 그릴 범위, Erase: 지울 검열 조각, Kind: 부위 종류(태그 고르기).</summary>
public sealed record RegenRegionInfo(Rect Bounds, Mat Region, Mat Erase, RegenKind Kind) : IDisposable
{
    public void Dispose() { Region.Dispose(); Erase.Dispose(); }
}
