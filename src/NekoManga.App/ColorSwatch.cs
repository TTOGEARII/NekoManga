using System.Windows;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;      // UseWindowsForms의 전역 using과 겹친다 — WPF 쪽으로 못 박는다
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace NekoManga.App;

/// <summary>색 칸(#RRGGBB를 적는 TextBox) 옆의 색 조각 — 누르면 Windows 색 팔레트 창(ColorDialog: 기본색 48 + 사용자 지정 색)을 띄워 고른 색을 그 칸에 적고, 칸의 글에 맞춰 조각을 칠한다.
/// 결과 창의 식자 탭이 쓰던 것(2026-11-17)을 설정 창의 글자 색·외곽선 색도 쓰게 뺐다(2026-11-26 사용자 "글자색 설정할 때 색 팔레트로 설정할 수 있게").
/// UseWindowsForms가 켜져 있어 Brush·Color가 WinForms와 겹친다 — 이 파일은 WPF 쪽을 using으로 두고 WinForms는 전부 이름을 다 적는다.</summary>
internal static class ColorSwatch
{
    /// <summary>"#RRGGBB"(# 없이 적어도 된다) → 색. 빈 글·auto·그 밖의 글은 false.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Black;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        if (!t.StartsWith('#')) t = "#" + t;
        if (t.Length != 7 || !t[1..].All(Uri.IsHexDigit)) return false;
        color = Color.FromRgb(Convert.ToByte(t[1..3], 16), Convert.ToByte(t[3..5], 16), Convert.ToByte(t[5..7], 16));
        return true;
    }

    /// <summary>색 고르기 창을 띄워 <paramref name="box"/>에 #RRGGBB로 적는다 — 지금 칸의 색에서 시작한다(색이 아니면 검정). 취소하면 그대로.</summary>
    public static void Pick(Window owner, TextBox box)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        var cur = TryParse(box.Text, out var c) ? c : Colors.Black;
        dlg.Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B);
        var handle = new System.Windows.Forms.NativeWindow();
        handle.AssignHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        try
        {
            if (dlg.ShowDialog(handle) == System.Windows.Forms.DialogResult.OK)
                box.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        }
        finally { handle.ReleaseHandle(); }
    }

    /// <summary>조각을 칸의 색으로 칠한다 — 빈 칸(자동·페이지 설정)이나 아직 색이 아닌 글이면 비운다.</summary>
    public static void Paint(Border swatch, TextBox box) =>
        swatch.Background = TryParse(box.Text, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
}
