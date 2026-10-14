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

/// <summary>결과 이미지를 넘겨 보면서, 자동 감지가 놓친 검열을 직접 집어 지우는 창.
///
/// 자동 감지는 모양 규칙(직사각형·길쭉함·채움비)으로 판단하므로 그림 선과 맞붙은 막대나
/// 희한한 모양은 끝내 놓친다. 그때 원고를 다시 돌리는 대신 여기서 그 자리만 집어 지운다.
///
/// 영역은 드래그가 아니라 **꼭짓점을 클릭**해 만든다 — 기울어진 막대를 네모로 감싸려면
/// 축에 나란한 사각형으로는 주변 그림까지 넣어야 하지만, 네 점을 찍으면 막대만 딱 감싼다.</summary>
public partial class ResultWindow
{
    private readonly List<string> _pages;
    private int _index;

    private Mat _img = new();
    private readonly Stack<Mat> _undo = new();
    private readonly List<CvPoint[]> _shapes = [];   // 완성된 도형들 (이미지 좌표)
    private readonly List<CvPoint> _pending = [];    // 찍는 중인 꼭짓점
    private WinPoint _hover;

    private double _scale = 1.0;
    private bool _dirty;
    private bool _busy;

    public ResultWindow(string imagePath) : this([imagePath], 0) { }

    // ── 식자 탭 상태 ─────────────────────────────────────────
    private LetterSidecar? _sidecar;              // 현재 페이지의 식자 사이드카 (없으면 탭 비활성)
    private List<TextBlockInfo> _letterBlocks = [];
    private bool _syncingBlockUi;                 // 콤보/텍스트박스 동기화 중 재진입 방지
    private Mat? _baseImg;                        // 식자 전 원고 — 편집 모드에서 이 위에 텍스트박스를 띄운다
    private PipelineRunner? _runner;              // 수동 복원용 러너 — 창 수명 동안 ONNX 세션 재사용
    private bool _letterPreview;                  // true면 [다시 식자] 결과(_img)를 보여준다

    public ResultWindow(IReadOnlyList<string> pages, int startIndex)
    {
        InitializeComponent();
        _pages = pages.Count > 0 ? [.. pages] : [""];
        _index = Math.Clamp(startIndex, 0, _pages.Count - 1);

        foreach (var p in _pages) CmbPages.Items.Add(Path.GetFileName(p));

        foreach (var f in System.Drawing.FontFamily.Families.Select(f => f.Name).OrderBy(n => n))
            CmbLFont.Items.Add(f);
        var s = App.Settings;
        CmbLFont.Text = s.LetterFontFamily;
        ChkLBold.IsChecked = s.LetterBold;
        TxtLSize.Text = s.LetterSizeScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TxtLColor.Text = s.LetterColorHex;
        TxtLOutScale.Text = s.LetterOutlineScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TxtLOutColor.Text = s.LetterOutlineColorHex;

        // 스타일 입력이 바뀌면 WYSIWYG 텍스트박스에 즉시 반영
        CmbLFont.SelectionChanged += (s, e) => LetterStyle_Changed(s, e);
        CmbLFont.LostFocus += LetterStyle_Changed;
        ChkLBold.Checked += (s, e) => LetterStyle_Changed(s, e);
        ChkLBold.Unchecked += (s, e) => LetterStyle_Changed(s, e);
        TxtLSize.TextChanged += (s, e) => LetterStyle_Changed(s, e);
        TxtLColor.TextChanged += (s, e) => LetterStyle_Changed(s, e);

        Loaded += (_, _) => LoadPage(_index, fit: true);
    }

    // ── 페이지 ──────────────────────────────────────────────
    private void LoadPage(int idx, bool fit)
    {
        if (!ConfirmDiscard()) return;

        _index = Math.Clamp(idx, 0, _pages.Count - 1);
        _img.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
        _shapes.Clear();
        _pending.Clear();
        _dirty = false;
        BtnSave.IsEnabled = false;
        BtnUndo.IsEnabled = false;

        _img = File.Exists(_pages[_index]) ? ImgIo.Read(_pages[_index], ImreadModes.Color) : new Mat();
        TxtFile.Text = System.IO.Path.GetFileName(_pages[_index]);
        TxtFile.ToolTip = _pages[_index];
        TxtCount.Text = $"{_index + 1} / {_pages.Count}";
        BtnPrev.IsEnabled = _index > 0;
        BtnNext.IsEnabled = _index < _pages.Count - 1;
        if (CmbPages.SelectedIndex != _index) CmbPages.SelectedIndex = _index;

        if (_img.Empty())
        {
            TxtStatus.Text = "이미지를 읽지 못했어요: " + Path.GetFileName(_pages[_index]);
            ImgView.Source = null;
            Overlay.Children.Clear();
            return;
        }
        // 식자 사이드카 로드 — 번역을 돌린 페이지에만 있다
        _sidecar = LetterSidecar.Load(_pages[_index]);
        _letterBlocks = _sidecar?.Blocks.ToList() ?? [];
        _baseImg?.Dispose();
        _baseImg = _sidecar != null ? ImgIo.Read(_sidecar.BasePath) : null;
        if (_baseImg is { } bm && bm.Empty()) { bm.Dispose(); _baseImg = null; }
        _letterPreview = false;
        TabLetter.IsEnabled = _sidecar != null && _baseImg != null;
        if (!TabLetter.IsEnabled && TabMode.SelectedItem == TabLetter) TabMode.SelectedIndex = 0;
        RefreshBlockCombo();

        if (fit) FitToWindow();
        Redraw();
        TxtStatus.Text = "지울 검열의 꼭짓점을 클릭하세요";
    }

    // ── 식자 탭 ─────────────────────────────────────────────
    private bool LetterMode => TabMode.SelectedItem == TabLetter;

    private void TabMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        BtnApply.Content = LetterMode ? "✍ 다시 식자" : "✨ 선택 영역 지우기";
        BtnClearSel.Visibility = LetterMode ? Visibility.Collapsed : Visibility.Visible;
        _pending.Clear();
        _letterPreview = false;      // 탭에 들어오면 항상 편집 모드부터
        Redraw();
        if (LetterMode)
            TxtStatus.Text = _letterBlocks.Count > 0
                ? $"번역 블록 {_letterBlocks.Count}개 — 말풍선의 텍스트박스를 바로 고치세요"
                : "이 장은 보일 블록이 없어요";
    }

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
        TxtBLineSp.Text = b.LineSpacing.ToString("0.##", CultureInfo.InvariantCulture);
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
            LineSpacing = D(TxtBLineSp.Text, b.LineSpacing, 0.6, 3.0),
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
        };
    }

    /// <summary>식자 전 원고(base)에 현재 블록·스타일로 글자를 다시 그린다.</summary>
    private void ApplyLettering()
    {
        if (_sidecar == null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"neko-relett-{Guid.NewGuid():N}.png");
        try
        {
            TextRenderer.RenderBlocks(_sidecar.BasePath, _sidecar.Source, _letterBlocks, tmp, LetterStyle());
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

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => LoadPage(_index - 1, fit: true);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => LoadPage(_index + 1, fit: true);

    private void CmbPages_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CmbPages.SelectedIndex < 0 || CmbPages.SelectedIndex == _index) return;
        LoadPage(CmbPages.SelectedIndex, fit: true);
    }

    /// <summary>저장 안 한 수정이 있으면 물어본다. 계속해도 되면 true.</summary>
    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("저장하지 않은 수정이 있어요. 저장할까요?", "결과 보기",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) Save();
        _dirty = false;
        return true;
    }

    // ── 표시 ────────────────────────────────────────────────
    private void FitToWindow()
    {
        double w = Math.Max(200, Scroller.ActualWidth - 24);
        double h = Math.Max(200, Scroller.ActualHeight - 24);
        _scale = Math.Min(Math.Min(w / _img.Width, h / _img.Height), 1.0);
        SldZoom.Value = _scale;
    }

    private void Redraw()
    {
        if (_img.Empty()) return;
        // 식자 편집 모드에서는 글자가 안 구워진 원고(base)를 깔고 그 위에 텍스트박스를 띄운다
        bool editView = LetterMode && !_letterPreview && _baseImg != null;
        var shown = editView ? _baseImg! : _img;
        ImgView.Source = ToBitmap(shown);
        ImageHost.Width = ImgView.Width = Overlay.Width = EditLayer.Width = shown.Width * _scale;
        ImageHost.Height = ImgView.Height = Overlay.Height = EditLayer.Height = shown.Height * _scale;
        TxtZoom.Text = $"{_scale * 100:0}%";
        EditLayer.Visibility = editView ? Visibility.Visible : Visibility.Collapsed;
        if (editView) RebuildLetterBoxes();
        DrawShapes();
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
        // 파이프라인과 동일: 페이지 공통 글자 크기 (중앙값)
        float px = TextRenderer.UniformSize(_letterBlocks, bs, style);

        var allRects = _letterBlocks.Select(lb => new System.Drawing.RectangleF(
            (float)(lb.X1 * bs), (float)(lb.Y1 * bs),
            (float)((lb.X2 - lb.X1) * bs), (float)((lb.Y2 - lb.Y1) * bs))).ToList();

        for (int i = 0; i < _letterBlocks.Count; i++)
        {
            var b = _letterBlocks[i];

            // 자동 폭·높이 넓히기(세로 블록)는 렌더와 같은 규칙으로 — 손으로 잡은 블록은 그대로
            var rect = allRects[i];
            if (b.Vertical && !b.ManualRect)
            {
                var others = allRects.Where((_, j) => j != i).ToList();
                rect = TextRenderer.WidenToBubble(_baseImg, rect, others);
                rect = TextRenderer.HeightenToBubble(_baseImg, rect, others);
            }
            double w = rect.Width * _scale, h = rect.Height * _scale;
            if (w < 8 || h < 8) continue;

            // 블록별 스타일 오버라이드 반영 (줄간격은 TextBox 미리보기 한계로 생략 — 최종 렌더엔 적용)
            bool bold = b.BoldOverride switch { 0 => false, 1 => true, _ => style.LetterBold };
            var fg = !string.IsNullOrWhiteSpace(b.ColorOverride)
                ? ToMedia(b.ColorOverride, b.FontColorHex)
                : ToMedia(style.LetterColorHex, b.FontColorHex);
            var tb = new TextBox
            {
                Text = b.Translation,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = b.Align switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
                VerticalContentAlignment = VerticalAlignment.Center,
                AcceptsReturn = true,
                FontFamily = family,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = b.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontSize = Math.Max(6, px * Math.Clamp(b.StyleScale, 0.3, 3.0) * _scale),
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

    /// <summary>Mat(BGR) → WPF 비트맵. OpenCvSharp.Extensions 없이 직접 옮긴다.</summary>
    private static BitmapSource ToBitmap(Mat m)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(m, bgra, ColorConversionCodes.BGR2BGRA);
        int stride = bgra.Width * 4;
        var buf = new byte[stride * bgra.Height];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, buf, 0, buf.Length);
        var bmp = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }

    private void DrawShapes()
    {
        Overlay.Children.Clear();

        // 식자 모드의 블록 표시는 EditLayer의 텍스트박스 자체가 담당한다
        if (LetterMode) return;

        foreach (var s in _shapes) Overlay.Children.Add(MakePolygon(s, finished: true));

        if (_pending.Count > 0)
        {
            // 찍는 중: 지금까지의 점 + 마우스 위치까지 미리보기
            var preview = new List<CvPoint>(_pending);
            var hoverImg = ToImage(_hover);
            if (_pending.Count >= 1) preview.Add(hoverImg);
            if (preview.Count >= 2) Overlay.Children.Add(MakePolygon([.. preview], finished: false));
            foreach (var p in _pending) Overlay.Children.Add(MakeDot(p));
        }
    }

    private System.Windows.Shapes.Polygon MakePolygon(CvPoint[] pts, bool finished)
    {
        var poly = new System.Windows.Shapes.Polygon
        {
            Stroke = finished ? Brushes.OrangeRed : Brushes.Gold,
            StrokeThickness = 2,
            StrokeDashArray = finished ? null : [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(finished ? (byte)56 : (byte)28, 255, 90, 60)),
        };
        foreach (var p in pts) poly.Points.Add(new WinPoint(p.X * _scale, p.Y * _scale));
        return poly;
    }

    private System.Windows.Shapes.Ellipse MakeDot(CvPoint p)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = Brushes.Gold, Stroke = Brushes.Black, StrokeThickness = 1,
        };
        Canvas.SetLeft(dot, p.X * _scale - 4.5);
        Canvas.SetTop(dot, p.Y * _scale - 4.5);
        return dot;
    }

    // ── 점 찍기 ─────────────────────────────────────────────
    private CvPoint ToImage(WinPoint p) => new(
        Math.Clamp((int)Math.Round(p.X / _scale), 0, Math.Max(0, _img.Width - 1)),
        Math.Clamp((int)Math.Round(p.Y / _scale), 0, Math.Max(0, _img.Height - 1)));

    // ── 휠(가운데) 버튼 드래그 = 화면 이동(패닝). 좌클릭은 기존 동작 그대로 ──
    private bool _panning;
    private WinPoint _panStartScreen;
    private double _panStartH, _panStartV;

    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _img.Empty()) return;
        _panning = true;
        _panStartScreen = e.GetPosition(Scroller);
        _panStartH = Scroller.HorizontalOffset;
        _panStartV = Scroller.VerticalOffset;
        ImageHost.CaptureMouse();
        e.Handled = true;
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;
        _panning = false;
        ImageHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Host_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_img.Empty() || _busy) return;

        if (LetterMode)
        {
            // 미리보기 중이면 편집 모드로 (편집 모드의 클릭은 텍스트박스가 직접 받는다)
            if (_letterPreview)
            {
                _letterPreview = false;
                Redraw();
                TxtStatus.Text = "편집 모드 — 텍스트박스를 바로 고치세요";
            }
            return;
        }

        _pending.Add(ToImage(e.GetPosition(ImageHost)));
        if (_pending.Count >= 4) CloseShape();   // 막대는 사각형이라 네 점이면 충분하다
        else
        {
            DrawShapes();
            TxtStatus.Text = $"{_pending.Count}/4 점 — 꼭짓점을 더 찍어주세요 (Enter로 3점에서 닫기)";
        }
    }

    private void Host_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (LetterMode) return;
        if (_pending.Count > 0)
        {
            _pending.RemoveAt(_pending.Count - 1);
            DrawShapes();
            TxtStatus.Text = _pending.Count == 0 ? "점을 취소했어요" : $"{_pending.Count}/4 점";
        }
        else if (_shapes.Count > 0)
        {
            _shapes.RemoveAt(_shapes.Count - 1);
            DrawShapes();
            TxtStatus.Text = $"도형을 하나 지웠어요 — 남은 도형 {_shapes.Count}개";
        }
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        // 휠 버튼 드래그 = 패닝 (확대 상태에서 화면 이동)
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(_panStartH - (p.X - _panStartScreen.X));
            Scroller.ScrollToVerticalOffset(_panStartV - (p.Y - _panStartScreen.Y));
            return;
        }

        if (LetterMode || _pending.Count == 0 || _img.Empty()) return;
        _hover = e.GetPosition(ImageHost);
        DrawShapes();
    }

    private void CloseShape()
    {
        if (_pending.Count >= 3)
        {
            _shapes.Add([.. _pending]);
            TxtStatus.Text = $"도형 {_shapes.Count}개 — [선택 영역 지우기]를 누르거나 계속 찍으세요";
        }
        _pending.Clear();
        DrawShapes();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: CloseShape(); break;
            case Key.Escape: _pending.Clear(); DrawShapes(); TxtStatus.Text = "찍던 점을 취소했어요"; break;
            // 텍스트박스에서 타이핑 중일 땐 방향키로 페이지를 넘기지 않는다 (커서 이동용)
            case Key.Left when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index - 1, fit: true); break;
            case Key.Right when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index + 1, fit: true); break;
        }
    }

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        _shapes.Clear();
        _pending.Clear();
        DrawShapes();
        TxtStatus.Text = "선택을 지웠어요";
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty() || _busy) return;

        if (LetterMode) { ApplyLettering(); return; }

        if (_pending.Count >= 3) CloseShape();   // 찍다 만 도형도 살려 준다
        if (_shapes.Count == 0)
        {
            TxtStatus.Text = "먼저 지울 곳의 꼭짓점을 클릭해 주세요";
            return;
        }

        int grow = int.TryParse(TxtGrow.Text.Trim(), out var g) ? Math.Clamp(g, 0, 40) : 6;
        var tone = RbDarkOnly.IsChecked == true ? MaskTone.Dark
                 : RbWhiteOnly.IsChecked == true ? MaskTone.White
                 : MaskTone.All;
        string engineMode = (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? "Basic";

        using var mask = BuildMask(tone, grow);
        int px = Cv2.CountNonZero(mask);
        if (px == 0)
        {
            TxtStatus.Text = tone switch
            {
                MaskTone.Dark => "도형 안에서 검은 부분을 찾지 못했어요 — [흰 부분만]이나 [도형 전체]로 바꿔 보세요",
                MaskTone.White => "도형 안에서 흰 부분을 찾지 못했어요 — [도형 전체]로 바꿔 보세요",
                _ => "지울 영역이 비어 있어요",
            };
            return;
        }

        _busy = true;
        BtnApply.IsEnabled = false;
        TxtStatus.Text = $"{px:N0}px 복원 중...";
        var before = _img.Clone();
        var status = new Progress<string>(m => TxtStatus.Text = m);

        try
        {
            var img = _img;
            // 러너(ONNX 세션 포함)는 창 수명 동안 재사용 — 매번 만들면 LaMa 모델(208MB)을
            // 적용할 때마다 다시 로딩해 수 초씩 낭비된다.
            _runner ??= new PipelineRunner(App.Settings);
            var runner = _runner;
            await Task.Run(async () =>
                await runner.ManualDecensorAsync(img, mask, engineMode, status, CancellationToken.None));

            _undo.Push(before);
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _shapes.Clear();
            Redraw();
            TxtStatus.Text = $"지웠어요 ({px:N0}px) — 마음에 들면 [저장]을 눌러주세요";
        }
        catch (Exception ex)
        {
            before.Dispose();
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnApply.IsEnabled = true;
        }
    }

    private enum MaskTone { Dark, White, All }

    /// <summary>찍은 도형들로 복원 마스크를 만든다.
    /// '검은/흰 부분만'이면 도형 안에서 그 톤의 픽셀만 골라 — 넉넉히 감싸도 주변 그림이 함께
    /// 뭉개지지 않는다. 가장자리 안티에일리어싱까지 덮도록 grow만큼 부풀린다.</summary>
    private Mat BuildMask(MaskTone tone, int grow)
    {
        var mask = new Mat(_img.Rows, _img.Cols, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(mask, _shapes, Scalar.White);

        if (tone != MaskTone.All)
        {
            using var gray = new Mat();
            Cv2.CvtColor(_img, gray, ColorConversionCodes.BGR2GRAY);
            using var picked = new Mat();
            // 막대는 거의 순흑/순백이지만 스캔본은 들쑥날쑥하다 — 양쪽 다 넉넉히 잡는다.
            if (tone == MaskTone.Dark) Cv2.Threshold(gray, picked, 120, 255, ThresholdTypes.BinaryInv);
            else Cv2.Threshold(gray, picked, 200, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(mask, picked, mask);
        }

        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _img.Dispose();
        _img = _undo.Pop();
        BtnUndo.IsEnabled = _undo.Count > 0;
        Redraw();
        TxtStatus.Text = "되돌렸어요";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        try
        {
            ImgIo.Write(_pages[_index], _img);
            _dirty = false;
            BtnSave.IsEnabled = false;
            TxtStatus.Text = "저장했어요 → " + Path.GetFileName(_pages[_index]);
        }
        catch (Exception ex) { TxtStatus.Text = "저장 실패: " + ex.Message; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        base.OnClosing(e);
        _runner?.Dispose();
        _img.Dispose();
        _baseImg?.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
    }

    // ── 확대/축소 ───────────────────────────────────────────
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _img.Empty()) return;
        _scale = e.NewValue;
        Redraw();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SldZoom.Value = Math.Clamp(SldZoom.Value * (e.Delta > 0 ? 1.15 : 1 / 1.15),
            SldZoom.Minimum, SldZoom.Maximum);
        e.Handled = true;
    }
}
