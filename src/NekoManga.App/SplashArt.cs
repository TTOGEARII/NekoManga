using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;

namespace NekoManga.App;

/// <summary>설치(Setup.exe)·업데이트 적용 스플래시 프레임 — 앱과 같은 만화 원고풍(종이 흰색 + 먹선 + 톤망점 + 선화 고양이).
/// 2026-10-21: 옛 자주빛 둥근 카드 스플래시(tools/gen-splash-*.ps1)를 앱 UI(AppTheme·LogoArt·MangaTextures)와 통일.
/// 생성: `NekoManga.exe --gen-splash <출력폴더>` → update/·setup/ 프레임 PNG → `tools/make-splash-gif.py`가 GIF로 묶는다.
/// 로고·망점은 앱과 같은 코드(LogoArt.Build, 망점 타일)라 디자인을 바꾸면 여기도 같이 따라온다.</summary>
public static class SplashArt
{
    public const int W = 480, H = 300, Frames = 24;
    private static readonly Color InkC = (Color)ColorConverter.ConvertFromString("#111111");
    private static readonly Color HintC = (Color)ColorConverter.ConvertFromString("#595959");
    private static readonly Brush Ink = new SolidColorBrush(InkC);
    private static readonly Brush Paper = Brushes.White;
    private static readonly Brush Hint = new SolidColorBrush(HintC);
    private static readonly FontFamily Font = new("Malgun Gothic");

    /// <summary>update/ (적용 중 — 먹색 marquee 바 포함)와 setup/ (Setup.exe — 아래 54px는 Velopack이 진행바를 그리므로 비움) 두 세트.</summary>
    public static void WriteFrames(string outDir)
    {
        foreach (var setup in new[] { false, true })
        {
            var dir = Path.Combine(outDir, setup ? "setup" : "update");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < Frames; i++)
            {
                var rtb = Render(i / (double)Frames, setup);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var f = File.Create(Path.Combine(dir, $"frame{i:d2}.png"));
                enc.Save(f);
            }
        }
    }

    private static RenderTargetBitmap Render(double t, bool setup)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 종이 + 톤망점 (앱 MangaTextures.Dots와 같은 규격: 9px 격자, 반지름 1.1, 8%)
            dc.DrawRectangle(Paper, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(Dots(), null, new Rect(0, 0, W, H));

            // 트림 마크 (원고 재단선) — 네 귀퉁이 ㄱ자 먹선
            var thin = new Pen(Ink, 1);
            foreach (var (x, y, sx, sy) in new[] { (10, 10, 1, 1), (W - 10, 10, -1, 1), (10, H - 10, 1, -1), (W - 10, H - 10, -1, -1) })
            {
                dc.DrawLine(thin, new Point(x, y + sy * 4), new Point(x, y + sy * 22));
                dc.DrawLine(thin, new Point(x + sx * 4, y), new Point(x + sx * 22, y));
            }

            // 원고 패널: 각진 먹선 3px (설치 스플래시는 아래 진행바 자리를 비워 둔다)
            var panel = new Rect(22, 22, W - 44, (setup ? H - 54 : H - 56) - 22);
            dc.DrawRectangle(Paper, new Pen(Ink, 3), panel);

            // 고양이 로고 — 둥실거림 (한 루프에 한 번 위아래)
            double bob = -4 * Math.Sin(2 * Math.PI * t);
            const double logoSize = 78;
            dc.PushTransform(new TranslateTransform(W / 2.0 - logoSize / 2, 44 + bob));
            dc.PushTransform(new ScaleTransform(logoSize / 112.0, logoSize / 112.0));
            dc.PushTransform(new TranslateTransform(6, 6));
            dc.DrawDrawing(LogoArt.Build(Ink, Paper));
            dc.Pop(); dc.Pop(); dc.Pop();

            // 글귀
            string title = setup ? "NekoManga 설치하는 중이에요..." : "새 버전으로 갈아입는 중...";
            string sub = setup ? "☆ 만화에 마법을 부릴 준비 ☆" : "☆ 금방 끝나요, 잠시만 기다려 주세요 ☆";
            DrawText(dc, title, 17, FontWeights.Bold, Ink, W / 2.0, 138);
            DrawText(dc, sub, 12, FontWeights.Normal, Hint, W / 2.0, 166);

            // 먹색 라벨 태그 (앱의 MangaTag) — "NekoManga"
            var tag = new FormattedText("NekoManga", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 11.5, Paper, 1.0);
            double tagW = tag.Width + 18, tagH = tag.Height + 6, tagY = setup ? 200 : 198;
            dc.DrawRectangle(Ink, null, new Rect(W / 2.0 - tagW / 2, tagY, tagW, tagH));
            dc.DrawText(tag, new Point(W / 2.0 - tag.Width / 2, tagY + 3));

            // 먹색 반짝이 (위상차 깜빡임) — 색 대신 농담으로
            // 글귀 줄(138~180)과 겹치지 않게 위·아래 귀퉁이에만
            var sparks = new[] { (62.0, 60.0, 18.0, 0.0), (396.0, 52.0, 14.0, 0.2), (54.0, 184.0, 12.0, 0.45), (426.0, 186.0, 16.0, 0.65), (236.0, 30.0, 12.0, 0.85) };
            foreach (var (x, y, size, ph) in sparks)
            {
                double op = 0.12 + 0.88 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (t + ph)));
                DrawSpark(dc, x, y, size, op);
            }

            // 집중선 (패널 안쪽 귀퉁이에 짧은 먹선 몇 가닥 — 만화 효과선)
            var sl = new Pen(new SolidColorBrush(InkC) { Opacity = 0.35 }, 1.2);
            for (int k = 0; k < 5; k++)
            {
                double o = 8 + k * 7;
                dc.DrawLine(sl, new Point(panel.Left + 6, panel.Top + o + 20), new Point(panel.Left + 6 + 18 - k * 2, panel.Top + o));
                dc.DrawLine(sl, new Point(panel.Right - 6, panel.Top + o + 20), new Point(panel.Right - 6 - 18 + k * 2, panel.Top + o));
            }

            if (!setup)
            {
                // marquee 진행바: 먹선 트랙 + 흐르는 먹색 토막 (각진 모양)
                var track = new Rect(22, H - 34, W - 44, 10);
                dc.DrawRectangle(new SolidColorBrush(InkC) { Opacity = 0.08 }, new Pen(Ink, 1.5), track);
                dc.PushClip(new RectangleGeometry(track));
                double segW = 110, x0 = track.Left - segW + (track.Width + segW) * t;
                dc.DrawRectangle(Ink, null, new Rect(x0, track.Top, segW, track.Height));
                dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static void DrawText(DrawingContext dc, string text, double size, FontWeight weight, Brush brush, double cx, double y)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y));
    }

    /// <summary>네 꼭짓점 별 모양 반짝이 (✦) — 먹색, 농담만 바뀐다.</summary>
    private static void DrawSpark(DrawingContext dc, double cx, double cy, double size, double opacity)
    {
        double r = size / 2, k = size / 7;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r), true, true);
            c.LineTo(new Point(cx + k, cy - k), true, false);
            c.LineTo(new Point(cx + r, cy), true, false);
            c.LineTo(new Point(cx + k, cy + k), true, false);
            c.LineTo(new Point(cx, cy + r), true, false);
            c.LineTo(new Point(cx - k, cy + k), true, false);
            c.LineTo(new Point(cx - r, cy), true, false);
            c.LineTo(new Point(cx - k, cy - k), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(InkC) { Opacity = opacity }, null, g);
    }

    private static DrawingBrush Dots()
    {
        const double cell = 9, radius = 1.1;
        var dot = new GeometryDrawing
        {
            Brush = new SolidColorBrush(InkC) { Opacity = 0.08 },
            Geometry = new EllipseGeometry(new Point(cell / 2, cell / 2), radius, radius),
        };
        return new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }
}
