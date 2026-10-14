using System.IO;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Application = System.Windows.Application;
using System.Windows.Media.Imaging;

namespace NekoManga.App;

/// <summary>일본 흑백 만화(잡지) 풍 고양이 로고 — 굵은 먹선 + 종이 흰 면.
/// 기하를 여기 한 곳에 두고, 앱 내 표시(테마 브러시로 라이브 반전)와
/// app.ico 생성(--render-logo)이 같은 그림을 공유한다.</summary>
public static class LogoArt
{
    // 좌표계 0..100. 만화 선화 느낌: 각진 귀, 단순한 눈·ω입, 수염 획.
    private static readonly Geometry Head = Geometry.Parse(
        "M 22,42 L 12,8 L 40,22 L 60,22 L 88,8 L 78,42 " +
        "C 88,50 90,66 82,78 C 70,92 30,92 18,78 C 10,66 12,50 22,42 Z");
    private static readonly Geometry EyeL = Geometry.Parse("M 33,50 a 5,7 0 1 0 0.01,0 Z");
    private static readonly Geometry EyeR = Geometry.Parse("M 62,50 a 5,7 0 1 0 0.01,0 Z");
    private static readonly Geometry Mouth = Geometry.Parse(
        "M 50,62 L 50,67 M 41,67 Q 45.5,73 50,67 Q 54.5,73 59,67");
    private static readonly Geometry Whiskers = Geometry.Parse(
        "M 6,52 L 24,56 M 6,64 L 24,62 M 94,52 L 76,56 M 94,64 L 76,62");

    public static Drawing Build(Brush ink, Brush paper)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawGeometry(paper, new Pen(ink, 7) { LineJoin = PenLineJoin.Miter }, Head);
            dc.DrawGeometry(ink, null, EyeL);
            dc.DrawGeometry(ink, null, EyeR);
            dc.DrawGeometry(null, new Pen(ink, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Mouth);
            dc.DrawGeometry(null, new Pen(ink, 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Whiskers);
        }
        return g;
    }

    /// <summary>--render-logo용: 흰 종이에 검정 먹선으로 PNG들을 렌더해 ICO로 묶는다.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [256, 48, 32, 16];
        var pngs = new List<byte[]>();
        foreach (var size in sizes)
        {
            var drawing = Build(Brushes.Black, Brushes.White);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // 테두리 획이 잘리지 않게 안쪽으로 살짝 축소
                double s = size / 112.0;
                dc.PushTransform(new TranslateTransform(size * 0.055, size * 0.055));
                dc.PushTransform(new ScaleTransform(s, s));
                dc.DrawDrawing(drawing);
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngs.Add(ms.ToArray());
        }

        // ICO 컨테이너 (PNG 항목 — Vista 이후 전부 지원)
        using var f = new BinaryWriter(File.Create(path));
        f.Write((ushort)0); f.Write((ushort)1); f.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            f.Write((byte)(sz == 256 ? 0 : sz)); f.Write((byte)(sz == 256 ? 0 : sz));
            f.Write((byte)0); f.Write((byte)0);
            f.Write((ushort)1); f.Write((ushort)32);
            f.Write(pngs[i].Length); f.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) f.Write(p);
    }
}
