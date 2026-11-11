using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoManga.Core;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
// UseWindowsForms가 켜져 있어 Brush·MouseEventArgs 등이 WinForms와 겹친다 — 별칭을 유지할 것.
using WinPoint = System.Windows.Point;
using CvPoint = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextRenderer = NekoManga.Core.Pipeline.TextRenderer;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace NekoManga.App;

// 식자 탭 — 번역 블록 목록·스타일 편집, 말풍선 위 텍스트박스(WYSIWYG), 다시 식자
public partial class ResultWindow
{
    // ── 식자 탭 ─────────────────────────────────────────────
    private bool LetterMode => TabMode.SelectedItem == TabLetter;

    /// <summary>스타일 입력(글꼴·크기·색 등)이 바뀌면 편집 중인 텍스트박스에 즉시 반영한다.</summary>
    private void LetterStyle_Changed(object? sender, EventArgs e)
    {
        if (IsLoaded && LetterMode && !_letterPreview) RebuildLetterBoxes();
    }

    private void RefreshBlockCombo()
    {
        _syncingBlockUi = true;
        CmbBlocks.Items.Clear();
        for (int i = 0; i < _letterBlocks.Count; i++)
        {
            var t = _letterBlocks[i].Translation;
            CmbBlocks.Items.Add($"{i + 1}. {(t.Length > 22 ? t[..22] + "…" : t)}");
        }
        if (_letterBlocks.Count > 0) CmbBlocks.SelectedIndex = 0;
        TxtBlockText.Text = _letterBlocks.Count > 0 ? _letterBlocks[0].Translation : "";
        if (_letterBlocks.Count > 0) LoadBlockStyleUi(_letterBlocks[0]);
        _syncingBlockUi = false;
    }

    private void CmbBlocks_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        _syncingBlockUi = true;
        var b = _letterBlocks[CmbBlocks.SelectedIndex];
        TxtBlockText.Text = b.Translation;
        LoadBlockStyleUi(b);
        _syncingBlockUi = false;
        DrawShapes();
    }

    /// <summary>선택한 블록의 '이 풍선만' 스타일을 컨트롤에 채운다 (_syncingBlockUi 상태에서 호출).</summary>
    private void LoadBlockStyleUi(TextBlockInfo b)
    {
        TxtBScale.Text = b.StyleScale.ToString("0.##", CultureInfo.InvariantCulture);
        foreach (ComboBoxItem it in CmbBAlign.Items)
            if ((string?)it.Tag == b.Align) { CmbBAlign.SelectedItem = it; break; }
        foreach (ComboBoxItem it in CmbBBold.Items)
            if ((string?)it.Tag == b.BoldOverride.ToString()) { CmbBBold.SelectedItem = it; break; }
        ChkBItalic.IsChecked = b.Italic;
        // 0 = 페이지 기본 따름 — 빈 칸으로 표시
        TxtBLineSp.Text = b.LineSpacing <= 0 ? "" : b.LineSpacing.ToString("0.##", CultureInfo.InvariantCulture);
        TxtBColor.Text = b.ColorOverride;
        TxtBRot.Text = b.RotationDeg.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>'이 풍선만' 컨트롤 변경 → 선택 블록에 저장하고 미리보기 갱신.</summary>
    private void BlockStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingBlockUi || !IsLoaded || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        var b = _letterBlocks[i];

        static double D(string t, double fb, double min, double max) =>
            double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, min, max) : fb;

        _letterBlocks[i] = b with
        {
            StyleScale = D(TxtBScale.Text, b.StyleScale, 0.3, 3.0),
            Align = (CmbBAlign.SelectedItem as ComboBoxItem)?.Tag as string ?? b.Align,
            BoldOverride = int.TryParse((CmbBBold.SelectedItem as ComboBoxItem)?.Tag as string, out var bo) ? bo : b.BoldOverride,
            Italic = ChkBItalic.IsChecked == true,
            LineSpacing = string.IsNullOrWhiteSpace(TxtBLineSp.Text) ? 0 : D(TxtBLineSp.Text, b.LineSpacing, 0.6, 3.0),
            ColorOverride = TxtBColor.Text.Trim(),
            RotationDeg = D(TxtBRot.Text, b.RotationDeg, -45, 45),
        };
        if (LetterMode && !_letterPreview) RebuildLetterBoxes();
        TxtStatus.Text = "풍선 스타일을 바꿨어요 — [다시 식자]가 최종 화질로 반영해요";
    }

    private void TxtBlockText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        _letterBlocks[i] = _letterBlocks[i] with { Translation = TxtBlockText.Text };
        // 캔버스 위 텍스트박스에도 즉시 반영 (박스는 Grid 컨테이너 안에 있다)
        foreach (var child in EditLayer.Children)
            if (child is Grid host && host.Tag is int idx && idx == i)
                foreach (var inner in host.Children)
                    if (inner is TextBox tb && tb.Text != TxtBlockText.Text)
                    { _syncingBlockUi = true; tb.Text = TxtBlockText.Text; _syncingBlockUi = false; }
    }

    private void BtnBlockHide_Click(object sender, RoutedEventArgs e)
    {
        if (CmbBlocks.SelectedIndex < 0) return;
        _letterBlocks.RemoveAt(CmbBlocks.SelectedIndex);
        RefreshBlockCombo();
        if (LetterMode && !_letterPreview) RebuildLetterBoxes();
        TxtStatus.Text = $"블록을 숨겼어요 (남은 {_letterBlocks.Count}개) — 창을 다시 열면 복구돼요";
    }

    /// <summary>식자 탭의 스타일 값들로 재식자 전용 설정을 만든다 (전역 설정은 건드리지 않는다).</summary>
    private AppSettings LetterStyle()
    {
        static string Hex(string t, string fb)
        {
            t = t.Trim();
            if (t.Length == 0) return fb;
            if (!t.StartsWith('#')) t = "#" + t;
            return t.Length == 7 && t[1..].All(Uri.IsHexDigit) ? t : fb;
        }
        return new AppSettings
        {
            LetterFontFamily = string.IsNullOrWhiteSpace(CmbLFont.Text) ? "Malgun Gothic" : CmbLFont.Text.Trim(),
            LetterBold = ChkLBold.IsChecked == true,
            LetterSizeScale = double.TryParse(TxtLSize.Text, out var sz) ? Math.Clamp(sz, 0.3, 3.0) : 1.0,
            LetterColorHex = Hex(TxtLColor.Text, ""),
            LetterOutlineScale = double.TryParse(TxtLOutScale.Text, out var os) ? Math.Clamp(os, 0, 4) : 1.0,
            LetterOutlineColorHex = Hex(TxtLOutColor.Text, "#FFFFFF"),
            // 페이지 기본 정렬·기울임·줄간격은 설정 전역을 따른다 (블록별 지정이 우선)
            LetterAlign = App.Settings.LetterAlign,
            LetterItalic = App.Settings.LetterItalic,
            LetterLineSpacing = App.Settings.LetterLineSpacing,
            LetterWordSpacing = App.Settings.LetterWordSpacing,
        };
    }

    /// <summary>식자 전 원고(base)에 현재 블록·스타일로 글자를 다시 그린다.</summary>
    private void ApplyLettering()
    {
        if (_sidecar == null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"neko-relett-{Guid.NewGuid():N}.png");
        try
        {
            // 사이드카에 박제한 원본 크기로 환산한다 — 원고를 옮겼거나 지웠어도 편집 미리보기(RebuildLetterBoxes)처럼 제자리에 그린다
            TextRenderer.RenderBlocks(_sidecar.BasePath, _sidecar.Source, _sidecar.SrcW, _sidecar.SrcH, _letterBlocks, tmp, LetterStyle());
            var rendered = ImgIo.Read(tmp);
            if (rendered.Empty()) { rendered.Dispose(); TxtStatus.Text = "다시 식자에 실패했어요"; return; }
            _undo.Push(_img);
            _img = rendered;
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _letterPreview = true;   // 최종 렌더 결과를 보여준다 — 그림 클릭으로 편집 복귀
            Redraw();
            TxtStatus.Text = $"최종 렌더링 미리보기 (블록 {_letterBlocks.Count}개) — [저장] 또는 그림 클릭으로 다시 편집";
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>말풍선 자리마다 편집 가능한 텍스트박스를 띄운다 (포토샵식 WYSIWYG).
    /// 글꼴·크기·색은 현재 스타일 값을 따르므로 타이핑·스타일 변경이 즉시 보인다.
    /// 최종 화질(외곽선 포함)은 [다시 식자]의 GDI+ 렌더가 책임진다.</summary>
    private void RebuildLetterBoxes()
    {
        EditLayer.Children.Clear();
        if (_sidecar == null || _baseImg == null) return;

        double bs = (double)_baseImg.Width / Math.Max(1, _sidecar.SrcW);   // 원본 → 결과 좌표
        var style = LetterStyle();
        var family = new FontFamily(style.LetterFontFamily);
        // 파이프라인과 동일: 말풍선마다의 자리와 글자 크기를 같은 계산으로 구한다
        var layouts = TextRenderer.PlanLayout(_baseImg, _letterBlocks, bs, bs, style);

        for (int i = 0; i < _letterBlocks.Count; i++)
        {
            var b = _letterBlocks[i];

            // 자리(풍선 쪽으로 넓힌 사각형)와 크기는 렌더와 같은 값 — 손으로 잡은 블록은 그 좌표 그대로
            var rect = layouts[i].Rect;
            float px = layouts[i].Size > 0 ? layouts[i].Size : 16;
            double w = rect.Width * _scale, h = rect.Height * _scale;
            if (w < 8 || h < 8) continue;

            // 블록별 스타일 오버라이드 반영 (줄간격은 TextBox 미리보기 한계로 생략 — 최종 렌더엔 적용)
            bool bold = b.BoldOverride switch { 0 => false, 1 => true, _ => style.LetterBold };
            var fg = !string.IsNullOrWhiteSpace(b.ColorOverride)
                ? ToMedia(b.ColorOverride, b.FontColorHex)
                : ToMedia(style.LetterColorHex, b.FontColorHex);
            var alignName = string.IsNullOrEmpty(b.Align) ? style.LetterAlign : b.Align;
            var tb = new TextBox
            {
                Text = b.Translation,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = alignName switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
                VerticalContentAlignment = VerticalAlignment.Center,
                AcceptsReturn = true,
                FontFamily = family,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = b.Italic || style.LetterItalic ? FontStyles.Italic : FontStyles.Normal,
                FontSize = Math.Max(6, px * _scale),
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderBrush = Brushes.DeepSkyBlue,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
            };
            tb.TextChanged += LetterBox_TextChanged;

            // 포토샵처럼 옮기고(위 손잡이) 늘리는(오른쪽 아래 모서리) 컨테이너
            var host = new Grid { Width = w, Height = h, Tag = i };
            if (Math.Abs(b.RotationDeg) > 0.05)
            {
                host.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                host.RenderTransform = new RotateTransform(b.RotationDeg);
            }
            host.Children.Add(tb);

            var move = new System.Windows.Controls.Primitives.Thumb
            {
                Height = 10, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.SizeAll, Opacity = 0.55,
                Background = Brushes.DeepSkyBlue,
            };
            move.DragDelta += (s, a) =>
            {
                Canvas.SetLeft(host, Canvas.GetLeft(host) + a.HorizontalChange);
                Canvas.SetTop(host, Canvas.GetTop(host) + a.VerticalChange);
            };
            move.DragCompleted += (s, a) => CommitBoxRect(host);
            host.Children.Add(move);

            var size = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 12, Height = 12,
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.SizeNWSE, Opacity = 0.75,
                Background = Brushes.HotPink,
            };
            size.DragDelta += (s, a) =>
            {
                host.Width = Math.Max(24, host.Width + a.HorizontalChange);
                host.Height = Math.Max(20, host.Height + a.VerticalChange);
            };
            size.DragCompleted += (s, a) => CommitBoxRect(host);
            host.Children.Add(size);

            tb.Tag = i;
            tb.GotKeyboardFocus += (s, _) =>
            {
                if (s is TextBox box && box.Tag is int idx && idx < CmbBlocks.Items.Count)
                { _syncingBlockUi = true; CmbBlocks.SelectedIndex = idx; TxtBlockText.Text = box.Text; _syncingBlockUi = false; }
            };

            Canvas.SetLeft(host, rect.X * _scale);
            Canvas.SetTop(host, rect.Y * _scale);
            EditLayer.Children.Add(host);
        }
    }

    /// <summary>옮기거나 늘린 박스의 화면 좌표를 원본 좌표로 되돌려 블록에 저장한다.
    /// ManualRect가 켜져 이후 렌더에서 자동 폭 넓히기를 건너뛰고 이 좌표 그대로 식자된다.</summary>
    private void CommitBoxRect(Grid host)
    {
        if (_sidecar == null || _baseImg == null || host.Tag is not int i || i >= _letterBlocks.Count) return;
        double bs = (double)_baseImg.Width / Math.Max(1, _sidecar.SrcW);
        double toSrc = 1.0 / (bs * _scale);
        int x1 = (int)Math.Round(Canvas.GetLeft(host) * toSrc);
        int y1 = (int)Math.Round(Canvas.GetTop(host) * toSrc);
        int x2 = x1 + (int)Math.Round(host.Width * toSrc);
        int y2 = y1 + (int)Math.Round(host.Height * toSrc);
        _letterBlocks[i] = _letterBlocks[i] with { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, ManualRect = true };
        TxtStatus.Text = "블록 위치·크기를 조정했어요 — [다시 식자]로 반영하세요";
    }

    private void LetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not int i || i >= _letterBlocks.Count) return;
        _letterBlocks[i] = _letterBlocks[i] with { Translation = tb.Text };
        if (!_syncingBlockUi && CmbBlocks.SelectedIndex == i)
        { _syncingBlockUi = true; TxtBlockText.Text = tb.Text; _syncingBlockUi = false; }
    }

    private static Color ToMedia(string hex, string fallbackHex)
    {
        var h = string.IsNullOrWhiteSpace(hex) ? fallbackHex : hex;
        if (h.StartsWith('#') && h.Length == 7)
            try
            {
                return Color.FromRgb(Convert.ToByte(h[1..3], 16), Convert.ToByte(h[3..5], 16), Convert.ToByte(h[5..7], 16));
            }
            catch { }
        return Colors.Black;
    }
}
