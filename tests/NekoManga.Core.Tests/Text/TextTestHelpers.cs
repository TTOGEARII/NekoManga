using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>번역·식자·말풍선 테스트가 함께 쓰는 합성 원고 도우미.
/// 실제 원고 대신 흰 쪽에 테두리 있는 풍선과 검은 네모(글자 흉내)를 그려 쓴다 — 모델도 글꼴도 필요 없다.</summary>
internal static class TextTestHelpers
{
    /// <summary>단색 쪽 (BGR 8UC3).</summary>
    public static Mat SolidPage(int width, int height, int tone = 255) =>
        new(height, width, MatType.CV_8UC3, new Scalar(tone, tone, tone));

    /// <summary>쪽과 같은 크기의 빈 글자 마스크 (8UC1, 0).</summary>
    public static Mat EmptyMask(Mat page) =>
        new(page.Rows, page.Cols, MatType.CV_8UC1, Scalar.Black);

    /// <summary>테두리 있는 타원 풍선을 그린다 (rx·ry는 흰 안쪽의 반지름, 테두리는 그 바깥에 붙는다).</summary>
    public static void DrawBubble(Mat page, int cx, int cy, int rx, int ry, int fill = 255, int border = 3, int ink = 0)
    {
        Cv2.Ellipse(page, new Point(cx, cy), new Size(rx + border, ry + border), 0, 0, 360, new Scalar(ink, ink, ink), -1);
        Cv2.Ellipse(page, new Point(cx, cy), new Size(rx, ry), 0, 0, 360, new Scalar(fill, fill, fill), -1);
    }

    /// <summary>타원 풍선 여러 개를 한 덩어리(결합 풍선)로 그린다 — 테두리를 먼저 다 그리고 안쪽을 나중에 칠해서
    /// 겹친 곳은 테두리 없이 안쪽이 이어진다(잘록한 목).</summary>
    public static void DrawJoinedBubbles(Mat page, int border, params (int Cx, int Cy, int Rx, int Ry)[] bubbles)
    {
        foreach (var b in bubbles)
            Cv2.Ellipse(page, new Point(b.Cx, b.Cy), new Size(b.Rx + border, b.Ry + border), 0, 0, 360, Scalar.Black, -1);
        foreach (var b in bubbles)
            Cv2.Ellipse(page, new Point(b.Cx, b.Cy), new Size(b.Rx, b.Ry), 0, 0, 360, Scalar.White, -1);
    }

    /// <summary>테두리 있는 네모 풍선(해설 상자)을 그린다. inner가 흰 안쪽이고 테두리는 그 바깥에 붙는다.</summary>
    public static void DrawBox(Mat page, Rect inner, int fill = 255, int border = 3, int ink = 0)
    {
        var outer = new Rect(inner.X - border, inner.Y - border, inner.Width + 2 * border, inner.Height + 2 * border);
        Cv2.Rectangle(page, outer, new Scalar(ink, ink, ink), -1);
        Cv2.Rectangle(page, inner, new Scalar(fill, fill, fill), -1);
    }

    /// <summary>글 덩어리를 흉내 낸다: glyph 크기의 네모를 cols×rows로 놓는다. mask를 주면 같은 자리를 255로 칠한다
    /// (감지기가 내는 모양으로 만들려면 그 뒤에 <see cref="DilateLikeDetector"/>). 돌려주는 값은 네모들을 꼭 맞게 감싼 사각형.</summary>
    public static Rect DrawGlyphs(Mat page, Mat? mask, int x, int y, int cols, int rows, int glyph = 14, int gap = 4, int ink = 0)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var cell = new Rect(x + c * (glyph + gap), y + r * (glyph + gap), glyph, glyph);
                Cv2.Rectangle(page, cell, new Scalar(ink, ink, ink), -1);
                if (mask != null) Cv2.Rectangle(mask, cell, Scalar.White, -1);
            }
        return new Rect(x, y, cols * glyph + (cols - 1) * gap, rows * glyph + (rows - 1) * gap);
    }

    /// <summary>글자 마스크를 감지기(TextDetector)가 내는 모양으로 만든다 — 획을 7px 타원으로 불린다.</summary>
    public static void DilateLikeDetector(Mat mask)
    {
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.Dilate(mask, mask, k);
    }

    public static DetectedBlock BlockOf(Rect r, int pad = 0, float confidence = 0.9f, bool langJa = true) =>
        new(r.X - pad, r.Y - pad, r.Right + pad, r.Bottom + pad, confidence, langJa);

    public static Rect RectOf(DetectedBlock b) => new(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1);

    /// <summary>PNG로 저장한다 (경로에 한글·일본어가 있어도 되게 파일은 .NET으로 쓴다). 부모 폴더가 없으면 만든다.</summary>
    public static void SavePng(Mat img, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cv2.ImEncode(".png", img, out var buf);
        File.WriteAllBytes(path, buf);
    }

    public static Mat LoadColor(string path) => Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);

    /// <summary>두 그림에서 값이 달라진 픽셀(어느 채널이든 threshold 넘게)을 255로 표시한 마스크 — 식자가 그린 자리.</summary>
    public static Mat ChangedMask(Mat before, Mat after, int threshold = 48)
    {
        using var diff = new Mat();
        Cv2.Absdiff(before, after, diff);
        using var gray = new Mat();
        // 채널 가운데 가장 큰 차이를 쓴다 (색 글자도 잡히게)
        var ch = Cv2.Split(diff);
        try
        {
            Cv2.Max(ch[0], ch[1], gray);
            Cv2.Max(gray, ch[2], gray);
        }
        finally { foreach (var c in ch) c.Dispose(); }
        var mask = new Mat();
        Cv2.Threshold(gray, mask, threshold, 255, ThresholdTypes.Binary);
        return mask;
    }

    /// <summary>마스크에서 0이 아닌 픽셀을 감싸는 사각형. 하나도 없으면 null.</summary>
    public static Rect? Bounds(Mat mask)
    {
        using var nz = new Mat();
        Cv2.FindNonZero(mask, nz);
        return nz.Empty() ? null : Cv2.BoundingRect(nz);
    }

    /// <summary>그려진 글을 줄 단위로 나눈다 — 잉크가 없는 가로줄이 줄 사이다. 줄마다 잉크를 감싼 사각형을 위에서부터 돌려준다.</summary>
    public static List<Rect> InkLines(Mat mask)
    {
        int rows = mask.Rows, cols = mask.Cols;
        var lines = new List<Rect>();
        using var rowMax = new Mat();
        Cv2.Reduce(mask, rowMax, ReduceDimension.Column, ReduceTypes.Max, -1);
        int start = -1;
        for (int y = 0; y <= rows; y++)
        {
            bool ink = y < rows && rowMax.At<byte>(y, 0) != 0;
            if (ink && start < 0) start = y;
            if (!ink && start >= 0)
            {
                using var band = new Mat(mask, new Rect(0, start, cols, y - start));
                var b = Bounds(band)!.Value;
                lines.Add(new Rect(b.X, start + b.Y, b.Width, b.Height));
                start = -1;
            }
        }
        return lines;
    }

    /// <summary>한 줄 안의 잉크를 가로로 나눈다 — 잉크 없는 세로줄이 minGap 넘게 이어지면 다른 덩어리(어절)로 본다.
    /// 덩어리마다 (왼쪽 끝, 오른쪽 끝) 픽셀 열을 왼쪽부터 돌려준다.</summary>
    public static List<(int Left, int Right)> InkRuns(Mat mask, Rect line, int minGap)
    {
        int cols = mask.Cols;
        var runs = new List<(int, int)>();
        using var band = new Mat(mask, new Rect(0, line.Y, cols, line.Height));
        using var colMax = new Mat();
        Cv2.Reduce(band, colMax, ReduceDimension.Row, ReduceTypes.Max, -1);
        int start = -1, last = -1;
        for (int x = 0; x < cols; x++)
        {
            if (colMax.At<byte>(0, x) == 0) continue;
            if (start >= 0 && x - last > minGap) { runs.Add((start, last)); start = -1; }
            if (start < 0) start = x;
            last = x;
        }
        if (start >= 0) runs.Add((start, last));
        return runs;
    }
}
