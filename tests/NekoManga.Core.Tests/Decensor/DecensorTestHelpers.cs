using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>검열 감지 테스트용 합성 원고 도우미 — 고정 좌표·고정 색으로만 그린다(난수 없음).</summary>
internal static class DecensorTestHelpers
{
    public static readonly Scalar Black = new(0, 0, 0);
    public static readonly Scalar White = new(255, 255, 255);

    /// <summary>한 가지 회색으로 채운 BGR 페이지.</summary>
    public static Mat SolidPage(int width, int height, byte gray = 255) =>
        new(height, width, MatType.CV_8UC3, new Scalar(gray, gray, gray));

    public static Scalar GrayColor(double v) => new(v, v, v);

    /// <summary>회전 사각형의 네 꼭짓점(반올림한 정수 좌표).</summary>
    public static Point[] Corners(Point2f center, Size2f size, float angleDeg) =>
        new RotatedRect(center, size, angleDeg).Points()
            .Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();

    public static void FillRotated(Mat img, Point2f center, Size2f size, float angleDeg, Scalar color) =>
        Cv2.FillConvexPoly(img, Corners(center, size, angleDeg), color);

    /// <summary>like와 같은 크기의 빈 8UC1 마스크.</summary>
    public static Mat EmptyMask(Mat like) => new(like.Rows, like.Cols, MatType.CV_8UC1, Scalar.Black);

    /// <summary>like와 같은 크기에서 회전 사각형 자리만 255인 마스크 — 검출 결과와 겹침을 잴 기준 영역.</summary>
    public static Mat RotatedRegion(Mat like, Point2f center, Size2f size, float angleDeg)
    {
        var m = EmptyMask(like);
        FillRotated(m, center, size, angleDeg, White);
        return m;
    }

    /// <summary>region(0이 아닌 곳) 가운데 mask가 덮은 비율. mask가 null이면 0.</summary>
    public static double Coverage(Mat? mask, Mat region)
    {
        int total = Cv2.CountNonZero(region);
        if (mask == null || total == 0) return 0;
        using var inter = new Mat();
        Cv2.BitwiseAnd(mask, region, inter);
        return (double)Cv2.CountNonZero(inter) / total;
    }

    /// <summary>축정렬 사각형 r 가운데 mask가 덮은 비율. mask가 null이면 0.</summary>
    public static double Coverage(Mat? mask, Rect r) => mask == null ? 0 : (double)CountIn(mask, r) / (r.Width * r.Height);

    /// <summary>mask가 r 안에서 켠 픽셀 수. mask가 null이면 0.</summary>
    public static int CountIn(Mat? mask, Rect r)
    {
        if (mask == null) return 0;
        using var roi = new Mat(mask, r);
        return Cv2.CountNonZero(roi);
    }

    public static int CountAll(Mat? mask) => mask == null ? 0 : Cv2.CountNonZero(mask);

    /// <summary>망점 스크린톤(밝은 바탕에 4px 간격 2×2 검은 점) — 블록 안 표준편차가 높은 '평탄하지 않은 그림' 대용.</summary>
    public static void DrawScreentone(Mat img, Rect area)
    {
        Cv2.Rectangle(img, area, GrayColor(230), -1);
        for (int y = area.Y; y + 2 <= area.Bottom; y += 4)
            for (int x = area.X; x + 2 <= area.Right; x += 4)
                Cv2.Rectangle(img, new Rect(x, y, 2, 2), Black, -1);
    }

    /// <summary>tile 크기 격자에 맞춘 모자이크 — 칸마다 평탄한 회색(60~200)이고 상하좌우 이웃 칸과는 60 이상 차이 난다.</summary>
    public static void DrawMosaic(Mat img, Rect area, int tile)
    {
        for (int ty = 0; ty < area.Height / tile; ty++)
            for (int tx = 0; tx < area.Width / tile; tx++)
            {
                int v = 60 + 20 * ((tx * 3 + ty * 5) % 8);
                Cv2.Rectangle(img, new Rect(area.X + tx * tile, area.Y + ty * tile, tile, tile), GrayColor(v), -1);
            }
    }

    /// <summary>RuntimeRoot\models 안에 이름만 있는 빈 자리표시 파일을 둔 OnnxEnv.
    /// 준비 여부(IsReady)와 모델을 열기 전의 입력 검사만 보기 위한 것이다 — 세션은 만들지 않는다.</summary>
    public static OnnxEnv EnvWithModelFiles(string runtimeRoot, params string[] modelFiles)
    {
        var models = Path.Combine(runtimeRoot, "models");
        Directory.CreateDirectory(models);
        foreach (var f in modelFiles) File.WriteAllBytes(Path.Combine(models, f), []);
        return new OnnxEnv(new AppSettings { RuntimeRoot = runtimeRoot });
    }
}
