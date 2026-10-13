using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using NekoManga.Core.Ballons;

namespace NekoManga.Core.Pipeline;

/// <summary>번역문을 완성본(채색된 이미지) 위에 그린다.
/// BT가 알려준 블록 좌표·폰트 크기를 쓰되, 좌표는 원본 크기 기준이므로
/// 채색 과정에서 리사이즈된 비율만큼 환산한다. 넘치면 글자를 줄여서 맞춘다.</summary>
public static class TextRenderer
{
    public static void RenderBlocks(string imagePath, string sourceSizeRefPath,
        IReadOnlyList<TextBlockInfo> blocks, string outPath)
    {
        using var bmp = new Bitmap(imagePath);

        double scaleX = 1, scaleY = 1;
        if (File.Exists(sourceSizeRefPath))
        {
            using var src = new Bitmap(sourceSizeRefPath);
            scaleX = (double)bmp.Width / src.Width;
            scaleY = (double)bmp.Height / src.Height;
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

            var color = ParseColor(b.FontColorHex);
            float fontSize = (float)Math.Max(9, b.FontSize * Math.Min(scaleX, scaleY));

            DrawFitted(g, b.Translation, rect, fontSize, color);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bmp.Save(outPath, ImageFormat.Png);
    }

    private static void DrawFitted(Graphics g, string text, RectangleF rect, float startSize, Color color)
    {
        var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
        };

        // 세로쓰기 원문도 한국어는 가로로 그린다 (국내 식자 관행).
        for (float size = startSize; size >= 9; size -= 1.5f)
        {
            using var font = new Font("Malgun Gothic", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, (int)rect.Width, fmt);
            if (measured.Height <= rect.Height + 2 || size <= 9)
            {
                // 흰 외곽선(halo)으로 어떤 배경에서도 읽히게 한다.
                using var path = new GraphicsPath();
                path.AddString(text, font.FontFamily, (int)font.Style, size, rect, fmt);
                using var halo = new Pen(Color.White, Math.Max(2.5f, size / 7f)) { LineJoin = LineJoin.Round };
                g.DrawPath(halo, path);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
                return;
            }
        }
    }

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
