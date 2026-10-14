using System.Windows;
using System.Windows.Media;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Application = System.Windows.Application;

namespace NekoManga.App;

/// <summary>만화 원고 느낌을 내는 타일 패턴 — 테마 브러시(먹색)로 그려 다크/화이트를 따라간다.
/// 전부 DrawingBrush(Tile)라 창을 늘려도 끊김 없이 깔린다.</summary>
public static class MangaTextures
{
    /// <summary>원고지 격자 (연한 먹선 정사각). 드롭존·캔버스 배경용.</summary>
    public static DrawingBrush Grid(double cell = 28, double thickness = 1, double opacity = 0.10)
    {
        var ink = (SolidColorBrush)AppTheme.GetBrush("NekoStroke");
        var pen = new Pen(new SolidColorBrush(ink.Color) { Opacity = opacity }, thickness);
        var geo = new GeometryGroup();
        geo.Children.Add(new LineGeometry(new Point(0, 0), new Point(cell, 0)));
        geo.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, cell)));
        var dg = new GeometryDrawing { Geometry = geo, Pen = pen };
        return new DrawingBrush(dg)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }

    /// <summary>스크린톤 도트 (옅은 망점). 큰 빈 배경에 은은한 질감.</summary>
    public static DrawingBrush Dots(double cell = 9, double radius = 1.1, double opacity = 0.08)
    {
        var ink = (SolidColorBrush)AppTheme.GetBrush("NekoStroke");
        var dot = new GeometryDrawing
        {
            Brush = new SolidColorBrush(ink.Color) { Opacity = opacity },
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

    /// <summary>테마가 바뀌면 다시 만들어 리소스 키를 갱신한다 (DrawingBrush는 색 교체가 안 되므로).</summary>
    public static void Refresh()
    {
        var res = Application.Current.Resources;
        res["MangaGrid"] = Grid();
        res["MangaDots"] = Dots();
    }
}
