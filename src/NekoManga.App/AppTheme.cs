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
/// 전환 원리: Apply마다 App.xaml의 브러시 키들을 **새 인스턴스로 교체**하고, XAML은 전부 `DynamicResource`로
/// 받는다. 같은 인스턴스의 Color만 바꿔 StaticResource까지 따라오게 하던 옛 방식은 성립하지 않았다 —
/// Application.Resources(스레드 간 공유 사전)에 든 Freezable은 WPF가 **처음 조회하는 순간 얼려 버려서**
/// (하네스 실측 2026-11-11: 창이 뜨자마자 8개 전부 IsFrozen), 다음 전환은 새 인스턴스 교체로 떨어지고
/// 이미 열린 창의 StaticResource 참조는 옛 색에 묶였다(결과 창 위·아래 바가 흰색으로 남고 글씨 사라짐).
/// 그래서 테마 브러시는 XAML에서 StaticResource로 받지 않는다.</summary>
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

    /// <summary>지금 테마의 브러시 인스턴스 (Apply 이후 유효, 얼려 있다). 전환 때 교체되므로 들고 있지 말고
    /// 그릴 때마다 다시 받는다. Window.Icon 같은 freezable 소비처에는 SnapshotBrush를 쓸 것.</summary>
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
            // 늘 새 인스턴스로 교체한다(어차피 사전이 조회 때 얼린다 — 클래스 주석). DynamicResource 소비처가 전부 따라온다.
            var brush = new SolidColorBrush(IsDark ? colors.Dark : colors.Light);
            brush.Freeze();
            Brushes[key] = brush;
            res[key] = brush;
        }
        res["NekoBgColor"] = IsDark ? Palette["NekoBg"].Dark : Palette["NekoBg"].Light;

        // WPF-UI 컨트롤(TextBox·ComboBox 등) 테마 + 액센트(버튼 등)도 같이.
        // 순서가 중요하다: 액센트를 **먼저** 바꾸고 테마 사전을 읽는다. 테마 사전(Light/Dark.xaml)의 액센트 브러시
        // (강조 버튼 배경 등)는 사전을 읽는 순간의 SystemAccentColor로 만들어져서, 사전을 먼저 읽으면 강조 버튼 배경이
        // 한 번 전 테마의 액센트로 남아 글씨와 같은 색이 됐다 (화이트→다크→화이트: 흰 바탕에 흰 글씨, 하네스 실측 2026-11-11).
        var uiTheme = IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(IsDark ? C("#F5F5F5") : C("#111111"), uiTheme);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(uiTheme, Wpf.Ui.Controls.WindowBackdropType.None, false);
    }
}
