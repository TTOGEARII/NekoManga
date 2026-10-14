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
        TxtFile.Text = _pages[_index];
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
        TabLetter.IsEnabled = _sidecar != null;
        if (_sidecar == null && TabMode.SelectedItem == TabLetter) TabMode.SelectedIndex = 0;
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
        DrawShapes();
        if (LetterMode)
            TxtStatus.Text = _letterBlocks.Count > 0
                ? $"번역 블록 {_letterBlocks.Count}개 — 스타일·번역문을 바꾸고 [다시 식자]를 누르세요"
                : "이 장은 숨기지 않은 블록이 없어요";
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
        _syncingBlockUi = false;
    }

    private void CmbBlocks_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        _syncingBlockUi = true;
        TxtBlockText.Text = _letterBlocks[CmbBlocks.SelectedIndex].Translation;
        _syncingBlockUi = false;
        DrawShapes();
    }

    private void TxtBlockText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        _letterBlocks[i] = _letterBlocks[i] with { Translation = TxtBlockText.Text };
    }

    private void BtnBlockHide_Click(object sender, RoutedEventArgs e)
    {
        if (CmbBlocks.SelectedIndex < 0) return;
        _letterBlocks.RemoveAt(CmbBlocks.SelectedIndex);
        RefreshBlockCombo();
        DrawShapes();
        TxtStatus.Text = $"블록을 숨겼어요 — [다시 식자]를 눌러야 그림에 반영돼요 (남은 {_letterBlocks.Count}개)";
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
            Redraw();
            TxtStatus.Text = $"다시 식자했어요 (블록 {_letterBlocks.Count}개) — 마음에 들면 [저장]을 눌러주세요";
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
        ImgView.Source = ToBitmap(_img);
        ImageHost.Width = ImgView.Width = Overlay.Width = _img.Width * _scale;
        ImageHost.Height = ImgView.Height = Overlay.Height = _img.Height * _scale;
        TxtZoom.Text = $"{_scale * 100:0}%";
        DrawShapes();
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

        // 식자 모드: 번역 블록 위치를 네모로 보여준다 (선택된 블록은 금색)
        if (LetterMode)
        {
            if (_sidecar == null || _img.Empty()) return;
            double bs = (double)_img.Width / Math.Max(1, _sidecar.SrcW);
            for (int i = 0; i < _letterBlocks.Count; i++)
            {
                var b = _letterBlocks[i];
                var box = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(1, (b.X2 - b.X1) * bs * _scale),
                    Height = Math.Max(1, (b.Y2 - b.Y1) * bs * _scale),
                    Stroke = i == CmbBlocks.SelectedIndex ? Brushes.Gold : Brushes.DeepSkyBlue,
                    StrokeThickness = i == CmbBlocks.SelectedIndex ? 3 : 1.5,
                };
                Canvas.SetLeft(box, b.X1 * bs * _scale);
                Canvas.SetTop(box, b.Y1 * bs * _scale);
                Overlay.Children.Add(box);
            }
            return;
        }

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

    private void Host_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_img.Empty() || _busy) return;

        // 식자 모드: 클릭한 자리의 번역 블록을 선택한다
        if (LetterMode)
        {
            if (_sidecar == null) return;
            var p = ToImage(e.GetPosition(ImageHost));
            double bs = (double)_img.Width / Math.Max(1, _sidecar.SrcW);
            int sx = (int)(p.X / bs), sy = (int)(p.Y / bs);
            for (int i = 0; i < _letterBlocks.Count; i++)
            {
                var b = _letterBlocks[i];
                if (sx >= b.X1 && sx <= b.X2 && sy >= b.Y1 && sy <= b.Y2)
                {
                    CmbBlocks.SelectedIndex = i;
                    return;
                }
            }
            return;
        }

        _pending.Add(ToImage(e.GetPosition(ImageHost)));

        // 네 점이면 사각형 완성 — 막대는 사각형이라 네 점이면 충분하다
        if (_pending.Count >= 4) CloseShape();
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
            case Key.Left when !_busy: LoadPage(_index - 1, fit: true); break;
            case Key.Right when !_busy: LoadPage(_index + 1, fit: true); break;
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
        bool strong = (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string == "Strong";

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
            await Task.Run(async () =>
            {
                using var runner = new PipelineRunner(App.Settings);
                await runner.ManualDecensorAsync(img, mask, strong, status, CancellationToken.None);
            });

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
        _img.Dispose();
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
