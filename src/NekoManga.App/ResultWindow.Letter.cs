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
            CmbBlocks.Items.Add($"{i + 1}. {(_letterBlocks[i].Hidden ? "(숨김) " : "")}{(t.Length > 22 ? t[..22] + "…" : t)}");
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
        BtnBlockHide.Content = b.Hidden ? "숨김 해제" : "이 블록 숨기기";
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

    /// <summary>[이 블록 숨기기] ↔ [숨김 해제] — 지우지 않고 Hidden 플래그만 바꾼다(되살릴 수 있게, 사이드카에도 남는다).</summary>
    private void BtnBlockHide_Click(object sender, RoutedEventArgs e)
    {
        int i = CmbBlocks.SelectedIndex;
        if (i < 0) return;
        bool hide = !_letterBlocks[i].Hidden;
        _letterBlocks[i] = _letterBlocks[i] with { Hidden = hide };
        RefreshBlockCombo();
        CmbBlocks.SelectedIndex = i;
        if (LetterMode && !_letterPreview) RebuildLetterBoxes();
        TxtStatus.Text = hide ? "블록을 숨겼어요 — [숨김 해제]로 되살릴 수 있어요 (다시 식자·지우기 때 저장돼요)" : "블록을 다시 보여요";
    }

    /// <summary>식자 탭의 편집(블록 글·숨김·블록별 스타일)을 사이드카 json에 쓴다 — [다시 식자]와 [지우기] 직전에.
    /// 안 쓰면 다시 렌더링이 원래 블록으로 식자해 편집이 사라진다(v1.8.3까지의 결함). 페이지 전역 스타일(글꼴 등)은 설정값이라 사이드카에 자리가 없다.</summary>
    private void SaveLetterSidecar()
    {
        if (_sidecar == null || _loading) return;
        try
        {
            _sidecar = _sidecar with { Blocks = [.. _letterBlocks] };
            _sidecar.Save(_refs[_index].OutPath);
        }
        catch (Exception ex) { TxtStatus.Text = "식자 편집 저장 실패: " + ex.Message; }
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
            SaveLetterSidecar();     // 고친 블록이 다음 다시 렌더링에도 쓰이게
            RefreshManualMaskBase(); // 저장 마스크 없는 장의 마스크 보기 바닥도 새 결과물로
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

        var bs = LetterScaleNow(_sidecar, _baseImg);   // 원본 → 결과 좌표 (축별 — 최종 식자와 같은 비율)
        var style = LetterStyle();
        var family = new FontFamily(style.LetterFontFamily);
        // 파이프라인과 동일: 말풍선마다의 자리와 글자 크기를 같은 계산으로 구한다
        var layouts = TextRenderer.PlanLayout(_baseImg, _letterBlocks, bs.X, bs.Y, style);

        for (int i = 0; i < _letterBlocks.Count; i++)
        {
            var b = _letterBlocks[i];
            if (b.Hidden) continue;   // 숨긴 블록은 텍스트박스도 없다 (PlanLayout도 Size 0을 준다)

            // 자리(풍선 쪽으로 넓힌 사각형)와 크기는 렌더와 같은 값 — 손으로 잡은 블록은 블록 사각형을 조금 안으로 들인 글 자리(90% × 94%)
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
            // 이 상자가 놓인 자리(식자할 그림 좌표) — 띄운 자리로 시작해, 옮기거나 늘려서 놓을 때마다 그 자리로 바뀐다.
            // 손잡이를 누르기만 하고 놓았는지를 이것으로 가린다. 확대 배율과 비율은 상자를 띄운 지금 값을 쥐고 있다가 놓을 때 그대로 되돌린다.
            double zoom = _scale;
            LetterBox placed = default;
            void Commit() => placed = CommitBoxRect(host, placed, zoom, bs);
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
            move.DragCompleted += (s, a) => Commit();
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
            size.DragCompleted += (s, a) => Commit();
            host.Children.Add(size);

            tb.Tag = i;
            tb.GotKeyboardFocus += (s, _) =>
            {
                if (s is TextBox box && box.Tag is int idx && idx < CmbBlocks.Items.Count)
                { _syncingBlockUi = true; CmbBlocks.SelectedIndex = idx; TxtBlockText.Text = box.Text; _syncingBlockUi = false; }
            };

            Canvas.SetLeft(host, rect.X * _scale);
            Canvas.SetTop(host, rect.Y * _scale);
            placed = BoxOf(host, zoom);
            EditLayer.Children.Add(host);
        }
    }

    /// <summary>상자의 화면 좌표를 식자할 그림 좌표로(÷ 확대 배율).</summary>
    private static LetterBox BoxOf(Grid host, double zoom) =>
        new(Canvas.GetLeft(host) / zoom, Canvas.GetTop(host) / zoom, host.Width / zoom, host.Height / zoom);

    /// <summary>상자를 놓았을 때(손잡이의 DragCompleted) — 옮기거나 늘렸으면 그 자리를 원본 좌표로 되돌려 블록에 저장한다.
    /// ManualRect가 켜져 이후 렌더에서 자동 폭 넓히기를 건너뛴다. 돌려주는 값은 상자가 지금 놓인 자리(다음에 놓을 때의 기준).
    /// <para>placed(상자를 띄운 자리, 또는 지난번에 놓은 자리) 그대로면 아무것도 바꾸지 않는다 — 손잡이를 누르기만 하고 놓아도 DragCompleted가 온다.
    /// v1.8.2까지는 그때도 상자 자리를 블록에 저장해서, 자동으로 놓인 블록이 손으로 잡은 블록이 되고 손으로 잡은 블록은 누를 때마다 줄었다.</para>
    /// <para>저장하는 사각형은 상자 그대로가 아니라 상자가 '글 자리'가 되는 블록이다(<see cref="LetterScale.CommitBox"/>) — 렌더가 손으로 잡은 블록을
    /// 안으로 들여 그리는 만큼 넓혀 둬야 다시 띄운 상자와 최종 식자가 놓은 자리에 온다.</para>
    /// <para>shownWith는 이 상자를 띄울 때 쓴 비율이다. 화면 → 결과 좌표(÷ 확대 배율) → 원본 좌표(÷ 축별 비율)를 띄울 때와 같은 값으로 되돌려야
    /// 저장한 블록이 놓은 자리에 그려진다 — 가로 비율 하나로 되돌리면 채색한 쪽에서 세로가 어긋났고, 놓는 순간의 비율을 다시 구하면
    /// 띄울 때 원본이 잠겨 비율 1로 띄운 상자(원본 크기가 0인 사이드카)가 잠금이 풀린 뒤 실제 비율로 되돌려져 쪽 밖 좌표로 저장됐다(글이 알림 없이 빠진다).</para></summary>
    private LetterBox CommitBoxRect(Grid host, LetterBox placed, double zoom, LetterScale shownWith)
    {
        var now = BoxOf(host, zoom);
        if (host.Tag is not int i || i >= _letterBlocks.Count) return now;
        if (shownWith.CommitBox(placed, now) is not { } r) return placed;
        _letterBlocks[i] = _letterBlocks[i] with { X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2, ManualRect = true };
        TxtStatus.Text = "블록 위치·크기를 조정했어요 — [다시 식자]로 반영하세요";
        return now;
    }

    // 같은 쪽(같은 사이드카·같은 식자 전 원고)이면 지난 비율을 다시 쓴다 — 원본 크기(SrcW·SrcH)가 0인 사이드카는 원본 파일을 읽어야
    // 크기를 아는데, RebuildLetterBoxes는 휠 확대·스타일 입력마다 불린다. 쪽이 바뀌면 참조가 달라 저절로 다시 구한다(ToBitmapCached와 같은 방식).
    // 앱이 쓴 식자 사이드카는 처음(v0.6.0)부터 원본 크기를 저장한다 — 0은 손으로 만들거나 고친 사이드카에서만 온다.
    private (LetterSidecar Sidecar, Mat Base, LetterScale Scale)? _letterScale;

    /// <summary>블록 좌표(원본 기준) → 식자 전 원고 좌표의 축별 비율. 최종 식자(TextRenderer.RenderBlocks — [다시 식자], --reletter)와
    /// 같은 계산(<see cref="LetterScale.Resolve"/>)이라 원본 크기가 0인 사이드카(손으로 만든 것)도 최종과 같은 자리에 상자를 띄운다.</summary>
    private LetterScale LetterScaleNow(LetterSidecar sc, Mat baseImg)
    {
        if (_letterScale is { } c && ReferenceEquals(c.Sidecar, sc) && ReferenceEquals(c.Base, baseImg)) return c.Scale;
        LetterScale scale;
        // 원본이 잠겨 있으면 Resolve가 던진다(최종 식자도 그때는 실패한다) — 창을 죽이지 않게 비율 1로 띄우고, 다음에 다시 읽게 기억하지 않는다.
        // 그렇게 띄운 상자를 놓으면 띄울 때의 비율(1)로 되돌린다 — 상자마다 띄울 때의 비율을 쥐고 있다(RebuildLetterBoxes → CommitBoxRect).
        try { scale = LetterScale.Resolve(baseImg.Width, baseImg.Height, sc.SrcW, sc.SrcH, sc.Source); }
        catch { return LetterScale.One; }
        _letterScale = (sc, baseImg, scale);
        return scale;
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
