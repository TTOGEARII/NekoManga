using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using NekoManga.Core.Engine;
using OpenCvSharp;
using Point = System.Drawing.Point;
using Rect = OpenCvSharp.Rect;

namespace NekoManga.Core.Pipeline;

/// <summary>번역문을 완성본(채색된 이미지) 위에 그린다.
/// 감지 단계가 알려준 블록 좌표·폰트 크기를 쓰되, 좌표는 원본 크기 기준이므로
/// 채색 과정에서 리사이즈된 비율만큼 환산한다. 넘치면 글자를 줄여서 맞춘다.
///
/// 이미지 입출력은 OpenCV로 한다 — System.Drawing(GDI+)은 webp를 못 읽어
/// `new Bitmap(webp)`가 "Parameter is not valid"로 터진다 (만화 파일에 webp가 흔하다).
/// 글자 그리기만 GDI+를 쓰고, 픽셀은 메모리에서 주고받는다.</summary>
public static class TextRenderer
{
    public static void RenderBlocks(string imagePath, string sourceSizeRefPath,
        IReadOnlyList<TextBlockInfo> blocks, string outPath, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        using var mat = ImgIo.Read(imagePath, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidDataException($"이미지를 읽지 못했어요: {Path.GetFileName(imagePath)}");
        using var bmp = ToBitmap(mat);

        double scaleX = 1, scaleY = 1;
        if (File.Exists(sourceSizeRefPath))
        {
            using var src = ImgIo.Read(sourceSizeRefPath, ImreadModes.Color);
            if (!src.Empty())
            {
                scaleX = (double)bmp.Width / src.Width;
                scaleY = (double)bmp.Height / src.Height;
            }
        }

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // 페이지 안 모든 말풍선은 같은 글자 크기를 쓴다 — 풍선마다 제각각이면 지저분해 보인다.
        // 블록별로는 넘칠 때만 DrawFitted가 그 블록에 한해 줄인다.
        float uniformSize = UniformSize(blocks, Math.Min(scaleX, scaleY), settings);

        var allRects = blocks.Select(b => new RectangleF(
            (float)(b.X1 * scaleX), (float)(b.Y1 * scaleY),
            (float)((b.X2 - b.X1) * scaleX), (float)((b.Y2 - b.Y1) * scaleY))).ToList();

        for (int bi = 0; bi < blocks.Count; bi++)
        {
            var b = blocks[bi];
            var rect = allRects[bi];
            if (rect.Width < 4 || rect.Height < 4) continue;

            // 세로쓰기 블록은 CTD가 '글자 기둥'만 잡아 폭이 아주 좁다(실측 26px짜리도 있다).
            // 그 폭 안에서는 한국어를 가로로 눕힐 수 없으므로, 원고에서 말풍선의 실제 흰 폭을
            // 재서 블록을 가로로 넓힌다 — 그래야 "진짜네..."가 세로 한 자씩이 아니라 가로로 읽힌다.
            // 단, 사용자가 결과 보기 창에서 손으로 잡은 블록(ManualRect)은 그 좌표를 존중한다.
            // 다른 블록 영역은 벽으로 — 결합 풍선은 내부가 이어져 흰 폭 측정이 이웃 풍선까지 샌다.
            if (b.Vertical && !b.ManualRect)
                rect = WidenToBubble(mat, rect, allRects.Where((_, i) => i != bi).ToList());

            // 말풍선은 대개 타원이라 블록(사각형) 가장자리까지 글자를 채우면 테두리에 닿는다 — 약간 안쪽으로.
            rect.Inflate(-rect.Width * 0.05f, -rect.Height * 0.03f);

            // 글자 색: 설정에 지정돼 있으면 그 색, 비어 있으면 원문 색을 따라간다
            var color = string.IsNullOrWhiteSpace(settings.LetterColorHex)
                ? ParseColor(b.FontColorHex)
                : ParseColor(settings.LetterColorHex);
            DrawFitted(g, b.Translation, rect, uniformSize, color, settings);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var outMat = ToMat(bmp);
        if (!ImgIo.Write(outPath, outMat))
            throw new IOException($"결과 저장에 실패했어요: {outPath}");
    }

    /// <summary>세로쓰기 블록을 말풍선의 실제 흰 폭까지 가로로 넓힌다.
    /// 블록 세로 25%/50%/75% 세 줄에서 중심으로부터 밝은 픽셀(≥225)이 이어지는 구간을 재고,
    /// 꼬리 등으로 한 줄만 비정상적으로 넓게 새는 것을 막으려 **셋 중 최소 폭**을 쓴다.
    /// 과확장 방지로 한쪽 최대 2.5배, 테두리 여유로 측정 폭의 86%만 쓴다.
    /// avoid(다른 블록 영역)에 들어가면 측정을 멈춘다 — 결합 풍선은 테두리 없이 내부가
    /// 이어져 흰 구간이 이웃 풍선까지 이어지는데, 거기로 넓히면 글이 풍선 밖/남의 풍선에 겹친다
    /// (실측: 07p '집에서 기다려'가 좁은 풍선을 넘어 바지 위까지 식자됨).</summary>
    public static RectangleF WidenToBubble(Mat img, RectangleF r, IReadOnlyList<RectangleF>? avoid = null)
    {
        int cx = Math.Clamp((int)(r.X + r.Width / 2), 0, img.Cols - 1);
        int capL = (int)Math.Max(8, r.Width * 2.5f);

        bool Bright(int x, int y)
        {
            var p = img.At<Vec3b>(y, x);
            return (p.Item0 + p.Item1 + p.Item2) / 3 >= 225;
        }

        bool Blocked(int x, int y)
        {
            if (avoid == null) return false;
            foreach (var a in avoid)
                if (x >= a.Left - 4 && x <= a.Right + 4 && y >= a.Top - 4 && y <= a.Bottom + 4)
                    return true;
            return false;
        }

        float minRun = float.MaxValue;
        foreach (var fy in new[] { 0.25f, 0.5f, 0.75f })
        {
            int y = Math.Clamp((int)(r.Y + r.Height * fy), 0, img.Rows - 1);
            if (!Bright(cx, y)) { minRun = Math.Min(minRun, r.Width); continue; }
            int xl = cx, xr = cx;
            while (xl - 1 >= 0 && cx - xl < capL && Bright(xl - 1, y) && !Blocked(xl - 1, y)) xl--;
            while (xr + 1 < img.Cols && xr - cx < capL && Bright(xr + 1, y) && !Blocked(xr + 1, y)) xr++;
            minRun = Math.Min(minRun, xr - xl);
        }

        float newW = minRun * 0.86f;
        if (newW <= r.Width) return r;
        float nx = Math.Max(0, cx - newW / 2);
        return new RectangleF(nx, r.Y, Math.Min(newW, img.Cols - nx), r.Height);
    }

    /// <summary>페이지 공통 글자 크기 — 블록별 OCR 추정 크기의 **중앙값** × 사용자 배율.
    /// 평균 대신 중앙값: 효과음같이 유난히 큰/작은 블록 하나가 전체 크기를 끌고 가지 않게.</summary>
    public static float UniformSize(IReadOnlyList<TextBlockInfo> blocks, double scale, AppSettings settings)
    {
        if (blocks.Count == 0) return 16;
        var sizes = blocks.Select(b => b.FontSize).OrderBy(s => s).ToList();
        double median = sizes[sizes.Count / 2];
        return (float)Math.Max(9, median * scale * Math.Clamp(settings.LetterSizeScale, 0.3, 3.0));
    }

    /// <summary>OpenCV Mat(BGR 8UC3) → GDI+ Bitmap(24bppRgb). 행 단위로 복사한다 (stride가 다름).</summary>
    private static unsafe Bitmap ToBitmap(Mat mat)
    {
        using var bgr = mat.Channels() == 3 ? mat.Clone() : mat.CvtColor(ColorConversionCodes.GRAY2BGR);
        var bmp = new Bitmap(bgr.Width, bgr.Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            long srcStep = (long)bgr.Step();
            int rowBytes = bgr.Width * 3;
            for (int y = 0; y < bgr.Height; y++)
            {
                var src = bgr.DataPointer + y * srcStep;
                var dst = data.Scan0 + y * data.Stride;
                Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>GDI+ Bitmap → OpenCV Mat(BGR 8UC3).</summary>
    private static unsafe Mat ToMat(Bitmap bmp)
    {
        var mat = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC3);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            long dstStep = (long)mat.Step();
            int rowBytes = bmp.Width * 3;
            for (int y = 0; y < bmp.Height; y++)
            {
                var src = data.Scan0 + y * data.Stride;
                var dst = mat.DataPointer + y * dstStep;
                Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        return mat;
    }

    private static void DrawFitted(Graphics g, string text, RectangleF rect, float startSize, Color color,
        AppSettings s)
    {
        var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
        };

        var style = s.LetterBold ? FontStyle.Bold : FontStyle.Regular;
        var family = ResolveFamily(s.LetterFontFamily);
        // 일부 글꼴은 Regular/Bold 중 한쪽만 있다 — 없는 스타일로 Font를 만들면 던지므로 보정한다
        if (!family.IsStyleAvailable(style))
            style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;

        // 세로쓰기 원문도 한국어는 가로로 그린다 (국내 식자 관행).
        // 줄바꿈은 GDI+ 기본 래핑에 맡기지 않는다 — CJK는 글자 아무 데서나 꺾여
        // "멀긴한/데"처럼 어절(조사 포함 한 덩어리)이 중간에 잘린다. 어절 단위로 직접 줄을 짠다.
        for (float size = startSize; size >= 9; size -= 1.5f)
        {
            using var font = new Font(family, size, style, GraphicsUnit.Pixel);
            // brokeWord = 어절 하나가 줄 폭보다 길어 글자 단위로 쪼개야 했다 →
            // 가독성을 위해 글자를 더 줄여서라도 어절이 한 줄에 들어가게 한다 (9px 바닥까지만).
            string wrapped = WrapByWords(g, text, font, rect.Width, out bool brokeWord);
            var measured = g.MeasureString(wrapped, font, int.MaxValue, fmt);
            bool fits = measured.Height <= rect.Height + 2 && measured.Width <= rect.Width + 2 && !brokeWord;
            if (fits || size <= 9)
            {
                // 외곽선(halo)으로 어떤 배경에서도 읽히게 한다.
                using var path = new GraphicsPath();
                path.AddString(wrapped, family, (int)style, size, rect, fmt);
                if (s.LetterOutlineScale > 0)
                {
                    float w = (float)(Math.Max(2.5f, size / 7f) * Math.Clamp(s.LetterOutlineScale, 0, 4));
                    using var halo = new Pen(ParseColor(s.LetterOutlineColorHex, Color.White), w)
                        { LineJoin = LineJoin.Round };
                    g.DrawPath(halo, path);
                }
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
                return;
            }
        }
    }

    /// <summary>어절(스페이스 단위) 줄바꿈. 한 줄에 다음 어절이 안 들어가면 줄을 넘긴다 —
    /// "좀 / 멀긴한데 / 우리 집에서 / 기다리고 / 있어!!"처럼 어절이 중간에 꺾이지 않는다.
    /// 어절 하나가 줄 폭 자체를 넘으면 그 어절만 어쩔 수 없이 글자 단위로 쪼갠다.</summary>
    private static string WrapByWords(Graphics g, string text, Font font, float maxWidth, out bool brokeWord)
    {
        float W(string t) => t.Length == 0 ? 0 : g.MeasureString(t, font).Width;

        brokeWord = false;
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))   // 사용자가 직접 넣은 줄바꿈은 존중
        {
            string line = "";
            foreach (var word in rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (W(candidate) <= maxWidth) { line = candidate; continue; }

                if (line.Length > 0) { lines.Add(line); line = ""; }
                if (W(word) <= maxWidth) { line = word; continue; }

                // 어절 하나가 줄보다 길다 — 글자 단위로 쪼갠다 (호출부가 이를 보고 크기를 더 줄인다)
                brokeWord = true;
                string piece = "";
                foreach (var ch in word)
                {
                    if (W(piece + ch) > maxWidth && piece.Length > 0) { lines.Add(piece); piece = ""; }
                    piece += ch;
                }
                line = piece;
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>설정의 글꼴 이름을 FontFamily로. 미설치 이름이면 Malgun Gothic → GenericSansSerif 순으로 대체.</summary>
    private static FontFamily ResolveFamily(string name)
    {
        try { return new FontFamily(name); }
        catch
        {
            try { return new FontFamily("Malgun Gothic"); }
            catch { return FontFamily.GenericSansSerif; }
        }
    }

    private static Color ParseColor(string hex, Color fallback) =>
        hex.StartsWith('#') && hex.Length == 7 ? ParseColor(hex) : fallback;

    private static Color ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith('#') && hex.Length == 7)
                return Color.FromArgb(
                    Convert.ToInt32(hex[1..3], 16),
                    Convert.ToInt32(hex[3..5], 16),
                    Convert.ToInt32(hex[5..7], 16));
        }
        catch { }
        return Color.Black;
    }
}
