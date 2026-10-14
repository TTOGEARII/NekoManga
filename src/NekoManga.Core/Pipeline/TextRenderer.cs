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

        foreach (var b in blocks)
        {
            var rect = new RectangleF(
                (float)(b.X1 * scaleX), (float)(b.Y1 * scaleY),
                (float)((b.X2 - b.X1) * scaleX), (float)((b.Y2 - b.Y1) * scaleY));
            if (rect.Width < 4 || rect.Height < 4) continue;

            // 세로쓰기 블록은 CTD가 '글자 기둥'만 잡아 폭이 아주 좁다(실측 26px짜리도 있다).
            // 그 폭 안에서는 한국어를 가로로 눕힐 수 없으므로, 원고에서 말풍선의 실제 흰 폭을
            // 재서 블록을 가로로 넓힌다 — 그래야 "진짜네..."가 세로 한 자씩이 아니라 가로로 읽힌다.
            if (b.Vertical) rect = WidenToBubble(mat, rect);

            // 말풍선은 대개 타원이라 블록(사각형) 가장자리까지 글자를 채우면 테두리에 닿는다 — 약간 안쪽으로.
            rect.Inflate(-rect.Width * 0.05f, -rect.Height * 0.03f);

            // 글자 색: 설정에 지정돼 있으면 그 색, 비어 있으면 원문 색을 따라간다
            var color = string.IsNullOrWhiteSpace(settings.LetterColorHex)
                ? ParseColor(b.FontColorHex)
                : ParseColor(settings.LetterColorHex);
            float scaledOcr = (float)(b.FontSize * Math.Min(scaleX, scaleY)
                * Math.Clamp(settings.LetterSizeScale, 0.3, 3.0));
            DrawFitted(g, b.Translation, rect, StartSize(b, rect, scaledOcr), color, settings);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var outMat = ToMat(bmp);
        if (!ImgIo.Write(outPath, outMat))
            throw new IOException($"결과 저장에 실패했어요: {outPath}");
    }

    /// <summary>세로쓰기 블록을 말풍선의 실제 흰 폭까지 가로로 넓힌다.
    /// 블록 세로 25%/50%/75% 세 줄에서 중심으로부터 밝은 픽셀(≥225)이 이어지는 구간을 재고,
    /// 꼬리 등으로 한 줄만 비정상적으로 넓게 새는 것을 막으려 **셋 중 최소 폭**을 쓴다.
    /// 과확장 방지로 한쪽 최대 2.5배, 테두리 여유로 측정 폭의 86%만 쓴다.</summary>
    public static RectangleF WidenToBubble(Mat img, RectangleF r)
    {
        int cx = Math.Clamp((int)(r.X + r.Width / 2), 0, img.Cols - 1);
        int capL = (int)Math.Max(8, r.Width * 2.5f);

        bool Bright(int x, int y)
        {
            var p = img.At<Vec3b>(y, x);
            return (p.Item0 + p.Item1 + p.Item2) / 3 >= 225;
        }

        float minRun = float.MaxValue;
        foreach (var fy in new[] { 0.25f, 0.5f, 0.75f })
        {
            int y = Math.Clamp((int)(r.Y + r.Height * fy), 0, img.Rows - 1);
            if (!Bright(cx, y)) { minRun = Math.Min(minRun, r.Width); continue; }
            int xl = cx, xr = cx;
            while (xl - 1 >= 0 && cx - xl < capL && Bright(xl - 1, y)) xl--;
            while (xr + 1 < img.Cols && xr - cx < capL && Bright(xr + 1, y)) xr++;
            minRun = Math.Min(minRun, xr - xl);
        }

        float newW = minRun * 0.86f;
        if (newW <= r.Width) return r;
        float nx = Math.Max(0, cx - newW / 2);
        return new RectangleF(nx, r.Y, Math.Min(newW, img.Cols - nx), r.Height);
    }

    /// <summary>말풍선별 시작 글자 크기 — 풍선 모양에 맞춰 각자 정해진다 (DrawFitted가 넘치면 더 줄인다).
    ///
    /// 일본식 세로 말풍선(원문이 세로쓰기)은 좁고 길어서, 원문 크기 그대로 한국어를 가로로 넣으면
    /// 한 줄에 1자씩 세로로 쌓여 가독성이 나쁘다. 그래서 세로 풍선은 **풍선 폭을 상한**으로 쓴다:
    /// 한 줄에 두 글자 이상 들어가는 크기(폭/2.2)를 넘지 않는다 — 짧은 대사든 긴 대사든 가로로 읽힌다.
    /// 가로 풍선은 원문 크기 추정이 믿을 만해 그대로 쓴다. OCR 크기도 풍선 폭도 블록마다 다르므로
    /// 결과적으로 말풍선마다 글자 크기가 제각각으로 맞춰진다.</summary>
    public static float StartSize(TextBlockInfo b, RectangleF rect, float scaledOcrSize)
    {
        if (b.Vertical)
            return Math.Max(9, Math.Min(rect.Width / 2.2f, scaledOcrSize));
        return Math.Max(9, scaledOcrSize);
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
        var family = ResolveFamily(s.LetterFontFamily, style);
        // 일부 글꼴은 Regular/Bold 중 한쪽만 있다 — 없는 스타일로 Font를 만들면 던지므로 보정한다
        if (!family.IsStyleAvailable(style))
            style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;

        // 세로쓰기 원문도 한국어는 가로로 그린다 (국내 식자 관행).
        for (float size = startSize; size >= 9; size -= 1.5f)
        {
            using var font = new Font(family, size, style, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, (int)rect.Width, fmt);
            if (measured.Height <= rect.Height + 2 || size <= 9)
            {
                // 외곽선(halo)으로 어떤 배경에서도 읽히게 한다.
                using var path = new GraphicsPath();
                path.AddString(text, family, (int)style, size, rect, fmt);
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

    /// <summary>설정의 글꼴 이름을 FontFamily로. 미설치 이름이면 Malgun Gothic → GenericSansSerif 순으로 대체.</summary>
    private static FontFamily ResolveFamily(string name, FontStyle style)
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
