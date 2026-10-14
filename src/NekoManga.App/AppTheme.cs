using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Application = System.Windows.Application;

namespace NekoManga.App;

/// <summary>일본 흑백 만화(원고/잡지) 풍 테마 — 화이트(기본)/다크 반전.
///
/// 전환이 즉시 전체 창에 먹히는 원리: App.xaml의 브러시 키들을 시작 시
/// **얼리지 않은(SolidColorBrush) 인스턴스로 교체**해 두고, 토글 때는 같은 인스턴스의
/// Color만 바꾼다 — StaticResource는 객체 참조라서 열려 있는 창까지 그대로 따라온다.</summary>
public static class AppTheme
{
    // 만화 원고풍: 종이 흰색 + 먹선 검정. 포인트도 색 대신 굵기/반전으로.
    private static readonly Dictionary<string, (Color Light, Color Dark)> Palette = new()
    {
        ["NekoBg"] = (C("#FFFFFF"), C("#111111")),
        ["NekoCard"] = (C("#FFFFFF"), C("#111111")),
        ["NekoCardHover"] = (C("#EFEFEF"), C("#242424")),
        ["NekoPink"] = (C("#111111"), C("#F5F5F5")),      // 액센트 = 먹색 (다크는 반전)
        ["NekoPinkSoft"] = (C("#111111"), C("#F5F5F5")),
        ["NekoLavender"] = (C("#111111"), C("#F5F5F5")),
        ["NekoText"] = (C("#111111"), C("#F5F5F5")),
        ["NekoHint"] = (C("#595959"), C("#A3A3A3")),
        ["NekoStroke"] = (C("#111111"), C("#F5F5F5")),    // 굵은 먹선 테두리
        ["NekoCanvas"] = (C("#E8E8E8"), C("#1A1A1A")),    // 이미지 뷰 바닥
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static bool IsDark { get; private set; }

    /// <summary>테마를 따라 색이 바뀌는 라이브 브러시 (Apply 이후 유효).
    /// 주의: Window.Icon 같은 freezable 소비처에 주지 말 것 — SnapshotBrush를 쓸 것.</summary>
    public static Brush GetBrush(string key) => Brushes[key];

    /// <summary>현재 테마 색의 스냅샷 브러시 — Freeze돼도 되는 사본.
    /// Window.Icon에 라이브 브러시가 든 DrawingImage를 주면 WPF가 아이콘 변환 때 통째로 얼려
    /// 공유 브러시까지 읽기 전용이 되고, 다음 테마 전환의 .Color 변경이
    /// InvalidOperationException으로 앱을 죽인다 (실사고 — 데모 창에선 아이콘이 없어 멀쩡했다).</summary>
    public static SolidColorBrush SnapshotBrush(string key) => new(Brushes[key].Color);

    /// <summary>로고 리소스를 현재 테마 색 스냅샷으로 (재)생성한다 — 시작 시와 테마 전환 때.</summary>
    public static void RefreshLogo() =>
        Application.Current.Resources["NekoLogo"] = new DrawingImage(
            LogoArt.Build(SnapshotBrush("NekoStroke"), SnapshotBrush("NekoBg")));

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>시작 시 1회 + 토글 때마다. theme: "Light"/"Dark".</summary>
    public static void Apply(string theme)
    {
        IsDark = theme == "Dark";
        var res = Application.Current.Resources;

        foreach (var (key, colors) in Palette)
        {
            var color = IsDark ? colors.Dark : colors.Light;
            // IsFrozen 방어: 누가 얼려 버렸으면(freezable 소비처에 새어 들어간 경우)
            // 색 변경 대신 새 인스턴스로 교체 — 크래시보다 일부 창의 지연 갱신이 낫다.
            if (Brushes.TryGetValue(key, out var brush) && !brush.IsFrozen)
                brush.Color = color;                 // 같은 인스턴스 색만 교체 → 전 창 즉시 반영
            else
            {
                brush = new SolidColorBrush(color);  // 일부러 Freeze하지 않는다
                Brushes[key] = brush;
                res[key] = brush;
            }
        }
        res["NekoBgColor"] = IsDark ? Palette["NekoBg"].Dark : Palette["NekoBg"].Light;

        // WPF-UI 컨트롤(TextBox·ComboBox 등) 테마 + 액센트(버튼 등)도 같이
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None, false);
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            IsDark ? C("#F5F5F5") : C("#111111"),
            IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
    }
}
