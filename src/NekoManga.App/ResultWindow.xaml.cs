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
    /// <summary>디스크에서 읽은 한 페이지 묶음(결과물 + 식자 사이드카 + 식자 전 원고). 읽기는 백그라운드.</summary>
    private sealed record PageData(Mat Img, LetterSidecar? Sidecar, Mat? Base)
    {
        public void Dispose() { Img.Dispose(); Base?.Dispose(); }
    }
    private readonly Dictionary<int, Task<PageData>> _prefetch = [];   // 이웃 페이지 미리 읽기 (2026-10-21: 페이지 넘김 지연 해소)
    private int _loadGen;

    private static PageData ReadPage(string path)
    {
        var img = File.Exists(path) ? ImgIo.Read(path, ImreadModes.Color) : new Mat();
        if (img.Empty()) return new PageData(img, null, null);
        var sc = LetterSidecar.Load(path);                       // 번역을 돌린 페이지에만 있다
        var b = sc != null ? ImgIo.Read(sc.BasePath) : null;
        if (b is { } bm && bm.Empty()) { bm.Dispose(); b = null; }
        return new PageData(img, sc, b);
    }

    private Task<PageData> FetchPage(int i)
    {
        if (!_prefetch.TryGetValue(i, out var t)) { var p = _pages[i]; t = Task.Run(() => ReadPage(p)); _prefetch[i] = t; }
        return t;
    }

    /// <summary>현재 페이지의 양옆을 미리 읽어 둔다 — ◀ ▶로 넘길 때 디스크·PNG 디코딩을 기다리지 않게. 멀어진 것은 버린다.</summary>
    private void PrefetchNeighbors()
    {
        foreach (var k in _prefetch.Keys.Where(k => Math.Abs(k - _index) > 1).ToList())
        {
            var t = _prefetch[k]; _prefetch.Remove(k);
            t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
        }
        if (_index > 0) FetchPage(_index - 1);
        if (_index < _pages.Count - 1) FetchPage(_index + 1);
    }

    /// <summary>페이지 전환. 읽기는 백그라운드(미리 읽어 둔 것이 있으면 즉시) — 읽는 동안은 넘김 버튼만 잠근다.
    /// 2026-10-21 이전엔 UI 스레드에서 PNG 두 장(결과물·식자 전 원고)을 동기로 읽어 큰 페이지에서 0.5초쯤 멈췄다.</summary>
    private bool _loading;   // 페이지 읽기 대기 중 — 그동안 [지우기] 적용은 막는다 (옛 _img를 지우는 사이 복원이 그 위에 쓰면 안 된다)

    private async void LoadPage(int idx, bool fit)
    {
        if (_busy) return;                 // 복원이 돌고 있으면 _img를 건드리지 않는다
        if (!ConfirmDiscard()) return;
        StashRegenFields();                // 떠나는 페이지의 재생성 값을 기억
        int gen = ++_loadGen;

        idx = Math.Clamp(idx, 0, _pages.Count - 1);
        _index = idx;
        _loading = true;
        TxtFile.Text = System.IO.Path.GetFileName(_pages[idx]);
        TxtFile.ToolTip = _pages[idx];
        TxtCount.Text = $"{idx + 1} / {_pages.Count}";
        BtnPrev.IsEnabled = BtnNext.IsEnabled = false;
        if (CmbPages.SelectedIndex != idx) CmbPages.SelectedIndex = idx;

        var task = FetchPage(idx);
        if (!task.IsCompleted) TxtStatus.Text = "읽는 중...";
        PageData data; string? readError = null;
        try { data = await task; }
        catch (Exception ex) { data = new PageData(new Mat(), null, null); readError = ex.Message; }
        // 기다리는 사이 또 넘겼거나 창이 닫혔다 — 결과는 사전(_prefetch)이 계속 소유하므로 여기서 해제·제거하지 않는다
        // (지는 쪽이 이기는 쪽 키를 지우고 Mat을 해제해 ObjectDisposedException으로 죽던 경로)
        if (gen != _loadGen) return;
        _loading = false;
        _prefetch.Remove(idx);

        _img.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
        _shapes.Clear();
        _pending.Clear();
        _brushStrokes.Clear();
        _dirty = false;
        BtnSave.IsEnabled = false;
        BtnUndo.IsEnabled = false;
        InvalidateBitmap();

        _img = data.Img;
        BtnPrev.IsEnabled = _index > 0;
        BtnNext.IsEnabled = _index < _pages.Count - 1;
        PrefetchNeighbors();

        if (_img.Empty())
        {
            TxtStatus.Text = "이미지를 읽지 못했어요: " + Path.GetFileName(_pages[_index]) + (readError != null ? " — " + readError : "");
            ImgView.Source = null;
            Overlay.Children.Clear();
            return;
        }
        _sidecar = data.Sidecar;
        _letterBlocks = _sidecar?.Blocks.ToList() ?? [];
        _baseImg?.Dispose();
        _baseImg = data.Base;
        _letterPreview = false;
        TabLetter.IsEnabled = _sidecar != null && _baseImg != null;
        if (!TabLetter.IsEnabled && TabMode.SelectedItem == TabLetter) TabMode.SelectedIndex = 0;
        RefreshBlockCombo();

        if (fit) FitToWindow();
        Redraw();
        if (RegenMode) LoadRegenFields();   // 새 페이지의 재생성 값(기억된 것 또는 설정값)
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
        ImgView.Source = ToBitmapCached(shown);
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

    /// <summary>같은 Mat이면 지난 변환을 다시 쓴다 — Redraw는 휠 확대마다 불리는데, 2000×3000 페이지를 매번
    /// BGRA로 복사해 비트맵을 새로 만들면(23MB) 확대·페이지 전환이 굼떴다 (2026-10-21). 제자리에서 픽셀을 바꾼
    /// 뒤(수동 복원)에는 InvalidateBitmap()으로 버린다; 새 Mat을 대입한 경우는 참조가 달라 자동으로 다시 만든다.</summary>
    private Mat? _bmpKey; private BitmapSource? _bmpVal;
    private BitmapSource ToBitmapCached(Mat m)
    {
        if (ReferenceEquals(m, _bmpKey) && _bmpVal != null) return _bmpVal;
        _bmpVal = ToBitmap(m); _bmpKey = m;
        return _bmpVal;
    }
    private void InvalidateBitmap() { _bmpKey = null; _bmpVal = null; }

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
        foreach (var (pts, r) in _brushStrokes) Overlay.Children.Add(MakeStroke(pts, r));

        if (_pending.Count > 0)
        {
            // 찍는 중: 지금까지의 점 + 마우스 위치까지 미리보기
            var preview = new List<CvPoint>(_pending);
            var hoverImg = ToImage(_hover);
            if (_pending.Count >= 1) preview.Add(hoverImg);
            if (preview.Count >= 2) Overlay.Children.Add(MakePolygon([.. preview], finished: false));
            foreach (var p in _pending) Overlay.Children.Add(MakeDot(p));
        }
        UpdateBrushCursor();   // Clear로 지워진 브러시 커서를 다시 올린다
    }

    // ── 브러시 커서: 칠해질 범위를 원 테두리로 (검토 창과 같은 모양, 사용자 요청 2026-10-21) ──
    private readonly System.Windows.Shapes.Ellipse _cursorOuter = new() { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Ellipse _cursorInner = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 1.5, IsHitTestVisible = false };
    private bool _cursorVisible;

    private void UpdateBrushCursor()
    {
        bool show = _cursorVisible && BrushMode && !LetterMode && !_img.Empty();
        double r = BrushRadius * _scale;
        foreach (var el in new[] { _cursorOuter, _cursorInner })
        {
            if (!Overlay.Children.Contains(el)) Overlay.Children.Add(el);
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;
            el.Width = el.Height = r * 2;
            Canvas.SetLeft(el, _hover.X - r); Canvas.SetTop(el, _hover.Y - r);
        }
    }

    private void Host_MouseLeave(object sender, MouseEventArgs e) { _cursorVisible = false; UpdateBrushCursor(); }

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

    /// <summary>브러시 획 미리보기 — 실제 마스크와 같은 두께의 반투명 선.</summary>
    private System.Windows.Shapes.Shape MakeStroke(List<CvPoint> pts, int r)
    {
        var brush = new SolidColorBrush(Color.FromArgb(80, 255, 70, 40));
        if (pts.Count == 1)
        {
            var dot = new System.Windows.Shapes.Ellipse
            { Width = r * 2 * _scale, Height = r * 2 * _scale, Fill = brush };
            Canvas.SetLeft(dot, (pts[0].X - r) * _scale);
            Canvas.SetTop(dot, (pts[0].Y - r) * _scale);
            return dot;
        }
        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = brush,
            StrokeThickness = r * 2 * _scale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var p in pts) line.Points.Add(new WinPoint(p.X * _scale, p.Y * _scale));
        return line;
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
        if (e.ChangedButton == MouseButton.Left && _brushing)
        {
            _brushing = false;
            ImageHost.ReleaseMouseCapture();
            TxtStatus.Text = $"획 {_brushStrokes.Count}개 — [선택 영역 지우기]를 누르거나 계속 칠하세요";
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;
        _panning = false;
        ImageHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ── 브러시 도구 ─────────────────────────────────────────
    private readonly List<(List<CvPoint> Pts, int R)> _brushStrokes = [];
    private bool _brushing;
    private bool BrushMode => RbToolBrush.IsChecked == true;
    private int BrushRadius => int.TryParse(TxtBrushSize.Text.Trim(), out var r) ? Math.Clamp(r, 3, 200) : 24;

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateBrushCursor();
        if (!IsLoaded) return;
        _pending.Clear();
        DrawShapes();
        TxtStatus.Text = BrushMode
            ? "브러시 — 지울 곳을 누른 채 칠하세요 (우클릭=마지막 획 취소)"
            : "점 찍기 — 꼭짓점을 클릭하고 더블클릭으로 닫으세요";
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

        if (BrushMode)
        {
            _brushing = true;
            _brushStrokes.Add(([ToImage(e.GetPosition(ImageHost))], BrushRadius));
            ImageHost.CaptureMouse();
            DrawShapes();
            e.Handled = true;
            return;
        }

        // 더블클릭 = 도형 닫기 (첫 클릭이 이미 점을 추가했으므로 여기선 닫기만)
        if (e.ClickCount >= 2) { CloseShape(); return; }

        _pending.Add(ToImage(e.GetPosition(ImageHost)));
        DrawShapes();
        TxtStatus.Text = $"{_pending.Count}점 — 계속 찍고, 더블클릭(또는 Enter)으로 도형을 닫으세요";
    }

    private void Host_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (LetterMode) return;
        if (BrushMode && _brushStrokes.Count > 0)
        {
            _brushStrokes.RemoveAt(_brushStrokes.Count - 1);
            DrawShapes();
            TxtStatus.Text = $"획을 취소했어요 — 남은 획 {_brushStrokes.Count}개";
            return;
        }
        if (_pending.Count > 0)
        {
            _pending.RemoveAt(_pending.Count - 1);
            DrawShapes();
            TxtStatus.Text = _pending.Count == 0 ? "점을 취소했어요" : $"{_pending.Count}점";
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

        _hover = e.GetPosition(ImageHost); _cursorVisible = true;

        // 브러시 칠하는 중 — 2px 이상 움직였을 때만 점 추가 (과밀 방지)
        if (_brushing && e.LeftButton == MouseButtonState.Pressed && _brushStrokes.Count > 0)
        {
            var p = ToImage(_hover);
            var pts = _brushStrokes[^1].Pts;
            var last = pts[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2) { pts.Add(p); DrawShapes(); }
            else UpdateBrushCursor();
            return;
        }

        if (LetterMode || _img.Empty()) return;
        if (_pending.Count > 0) DrawShapes(); else UpdateBrushCursor();
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

    // ── 재생성(SDXL) '이 페이지만' 설정 (2026-10-21, 사용자 요청) ─────────────────────
    // 전역 설정의 사본에 이 페이지 값만 덮어 러너에 넘긴다 — settings.json은 건드리지 않는다. 페이지별로 창이 열려 있는 동안만 기억.
    private sealed record RegenPageValues(string Prompt, string Body, string Negative, double Denoise, int Steps, double Cfg, long Seed, double CnStr, double CnEnd)
    {
        public static RegenPageValues From(NekoManga.Core.AppSettings s) =>
            new(s.RegenPrompt, s.RegenBodyTags, s.RegenNegative, s.RegenDenoise, s.RegenSteps, s.RegenCfg, s.RegenSeed, s.RegenCnStrength, s.RegenCnEnd);
        public NekoManga.Core.AppSettings ApplyTo(NekoManga.Core.AppSettings s)
        {
            var c = s.ShallowClone();
            c.RegenPrompt = Prompt; c.RegenBodyTags = Body; c.RegenNegative = Negative;
            c.RegenDenoise = Denoise; c.RegenSteps = Steps; c.RegenCfg = Cfg; c.RegenSeed = Seed; c.RegenCnStrength = CnStr; c.RegenCnEnd = CnEnd;
            return c;
        }
    }
    private readonly Dictionary<int, RegenPageValues> _regenPerPage = [];
    private bool RegenMode => (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string == "ComfyRegen";

    private void CmbEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PnlRegen.Visibility = RegenMode ? Visibility.Visible : Visibility.Collapsed;
        if (RegenMode) LoadRegenFields();
    }

    /// <summary>이 페이지에 기억된 값이 있으면 그것을, 없으면 ⚙ 설정의 재생성 값을 칸에 채운다.</summary>
    private void LoadRegenFields()
    {
        bool own = _regenPerPage.TryGetValue(_index, out var saved);
        var v = own ? saved! : RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        TxtRgPrompt.Text = v.Prompt; TxtRgBody.Text = v.Body; TxtRgNeg.Text = v.Negative;
        TxtRgDenoise.Text = v.Denoise.ToString(ic); TxtRgSteps.Text = v.Steps.ToString(); TxtRgCfg.Text = v.Cfg.ToString(ic);
        TxtRgSeed.Text = v.Seed.ToString(); TxtRgCnStr.Text = v.CnStr.ToString(ic); TxtRgCnEnd.Text = v.CnEnd.ToString(ic);
        UpdateRegenHeader(own);
    }

    private void UpdateRegenHeader(bool own) =>
        TxtRegenHdr.Text = own ? "✎ 이 페이지 값 사용 중 — 펼쳐서 확인·수정" : "⚙ 설정값 그대로 — 펼쳐서 이 페이지만 바꾸기";

    /// <summary>칸의 값을 읽어(범위 밖은 설정값으로) 이 페이지 값으로 기억하고 돌려준다. 설정과 같으면 기억에서 뺀다.</summary>
    private RegenPageValues ReadRegenFields()
    {
        var d = RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        double D(string t, double def, double lo, double hi) => double.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, ic, out var x) ? Math.Clamp(x, lo, hi) : def;
        int I(string t, int def, int lo, int hi) => int.TryParse(t.Trim(), out var x) ? Math.Clamp(x, lo, hi) : def;
        var v = new RegenPageValues(TxtRgPrompt.Text.Trim(), TxtRgBody.Text.Trim(), TxtRgNeg.Text.Trim(),
            D(TxtRgDenoise.Text, d.Denoise, 0.05, 1.0), I(TxtRgSteps.Text, d.Steps, 1, 150), D(TxtRgCfg.Text, d.Cfg, 0, 30),
            long.TryParse(TxtRgSeed.Text.Trim(), out var seed) ? seed : -1, D(TxtRgCnStr.Text, d.CnStr, 0, 2), D(TxtRgCnEnd.Text, d.CnEnd, 0, 1));
        if (v == d) _regenPerPage.Remove(_index); else _regenPerPage[_index] = v;
        UpdateRegenHeader(v != d);
        return v;
    }

    /// <summary>페이지를 떠나기 전에 칸 값을 그 페이지 몫으로 기억해 둔다.</summary>
    private void StashRegenFields()
    {
        if (IsLoaded && PnlRegen.Visibility == Visibility.Visible && !_img.Empty()) ReadRegenFields();
    }

    private void BtnRegenReset_Click(object sender, RoutedEventArgs e)
    {
        _regenPerPage.Remove(_index);
        LoadRegenFields();
        TxtStatus.Text = "이 페이지의 재생성 값을 설정값으로 되돌렸어요";
    }

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        _shapes.Clear();
        _pending.Clear();
        _brushStrokes.Clear();
        DrawShapes();
        TxtStatus.Text = "선택을 지웠어요";
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty() || _busy || _loading) return;

        if (LetterMode) { ApplyLettering(); return; }

        if (_pending.Count >= 3) CloseShape();   // 찍다 만 도형도 살려 준다
        if (_shapes.Count == 0 && _brushStrokes.Count == 0)
        {
            TxtStatus.Text = "먼저 지울 곳을 점으로 찍거나 브러시로 칠해 주세요";
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
            // 러너(ONNX 세션 포함)는 창 수명 동안 재사용 — 매번 만들면 LaMa 모델(211MB)을
            // 적용할 때마다 다시 로딩해 수 초씩 낭비된다.
            _runner ??= new PipelineRunner(App.Settings);
            var runner = _runner;
            // 재생성이면 이 페이지 전용 프롬프트·수치를 담은 설정 사본을 넘긴다 (전역 설정은 그대로)
            var regenSettings = engineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : null;
            await Task.Run(async () =>
                await runner.ManualDecensorAsync(img, mask, engineMode, status, CancellationToken.None, regenSettings));

            _undo.Push(before);
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _shapes.Clear();
            _brushStrokes.Clear();
            InvalidateBitmap();   // _img를 제자리에서 고쳤다
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
        // 브러시 획: 두꺼운 선 + 각 점에 원(끝/꺾임이 둥글게) — 미리보기와 같은 모양으로 래스터
        foreach (var (pts, r) in _brushStrokes)
        {
            for (int i = 1; i < pts.Count; i++)
                Cv2.Line(mask, pts[i - 1], pts[i], Scalar.White, r * 2, LineTypes.Link8);
            foreach (var p in pts) Cv2.Circle(mask, p, r, Scalar.White, -1);
        }

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
        ++_loadGen;   // 아직 기다리는 LoadPage가 있으면 깨어나도 아무것도 안 하게
        // 복원이 돌고 있으면 그 Mat·러너는 해제하지 않는다(작업이 끝나면 GC가 거둔다) — 쓰는 중인 네이티브 메모리를 해제하면 죽는다
        if (!_busy) { _runner?.Dispose(); _img.Dispose(); }
        _baseImg?.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
        foreach (var t in _prefetch.Values) t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
        _prefetch.Clear();
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
