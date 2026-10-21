using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using WinPoint = System.Windows.Point;
using CvPoint = OpenCvSharp.Point;
using Brushes = System.Windows.Media.Brushes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Path = System.IO.Path;

namespace NekoManga.App;

/// <summary>검열 제거 전 검토 창 (2026-10-20, 사용자 요청 "감지 → 내가 마스크를 더 칠함 → 제거").
/// 러너가 1차(번역+감지)를 끝낸 페이지 목록을 받아, 페이지를 넘기며 감지 마스크(빨강)를 브러시·지우개·다각형으로 고친다.
/// 마스크는 러너의 ReviewPage.Mask(원본 해상도 CV_8UC1)를 제자리에서 바꾼다. 표시는 긴 변 1600px로 줄인 사본 위에 합성해 가볍게 그린다.
/// 확대는 휠, 이동은 휠 버튼 드래그(ResultWindow와 같은 조작). [검열 제거 시작]=DialogResult true, [취소]=false.</summary>
public partial class MaskReviewWindow
{
    private sealed class Row
    {
        public required PipelineRunner.ReviewPage Page { get; init; }
        public string Name => Page.Name;
        public string Status { get; set; } = "";
        public Mat? Original { get; set; }          // 감지 결과 사본 ("자동 감지로" 복원용)
        public List<Mat> Undo { get; } = [];      // 끝이 최신. 깊이 25 제한
    }

    private readonly ObservableCollection<Row> _rows = [];
    private Row? _cur;
    private Mat? _img;          // 현재 페이지 원고 (원본 해상도)
    private Mat? _disp;         // 표시용 축소 원고
    private double _dispScale = 1;   // 표시 px / 원본 px
    private double _zoom = 1;
    private const int DispMax = 1600;

    public MaskReviewWindow(IReadOnlyList<PipelineRunner.ReviewPage> pages)
    {
        InitializeComponent();
        foreach (var p in pages)
            _rows.Add(new Row { Page = p, Original = p.Mask?.Clone() });
        LstPages.ItemsSource = _rows;
        foreach (var r in _rows) UpdateStatus(r);
        SldBrush.ValueChanged += (_, _) => { TxtBrush.Text = $"{(int)SldBrush.Value}px"; UpdateBrushCursor(); };
        foreach (var rb in new[] { RbAdd, RbErase, RbPoly }) rb.Checked += (_, _) => UpdateBrushCursor();
        if (_rows.Count > 0) LstPages.SelectedIndex = 0;
    }

    // ── 브러시 커서: 칠해질 범위를 원 테두리로 보여 준다 (사용자 요청 2026-10-21) ──
    // 흰 굵은 테두리 위에 색 가는 테두리(칠하기=빨강, 지우개=하늘색) — 원고가 희든 검든 보이게. 오버레이가 지워질 때마다 다시 올린다.
    private readonly Ellipse _cursorOuter = new() { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Ellipse _cursorInner = new() { Stroke = Brushes.Red, StrokeThickness = 1.5, IsHitTestVisible = false };
    private WinPoint _cursorPos; private bool _cursorVisible;

    private void UpdateBrushCursor()
    {
        bool show = _cursorVisible && RbPoly.IsChecked != true && _cur?.Page.Mask != null;
        double r = SldBrush.Value * _dispScale * _zoom;
        foreach (var el in new[] { _cursorOuter, _cursorInner })
        {
            if (!Overlay.Children.Contains(el)) Overlay.Children.Add(el);
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;
            el.Width = el.Height = r * 2;
            Canvas.SetLeft(el, _cursorPos.X - r); Canvas.SetTop(el, _cursorPos.Y - r);
        }
        _cursorInner.Stroke = RbErase.IsChecked == true ? Brushes.DeepSkyBlue : Brushes.Red;
    }

    // ── 페이지 전환 ───────────────────────────────────────────
    private void LstPages_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LstPages.SelectedItem is not Row r) return;
        _cur = r;
        _img?.Dispose(); _disp?.Dispose();
        _img = NekoManga.Core.ImgIo.Read(r.Page.WorkPath, ImreadModes.Color);
        if (_img.Empty()) { TxtStatus.Text = "원고를 읽지 못했어요: " + r.Page.WorkPath; return; }
        _dispScale = Math.Min(1.0, (double)DispMax / Math.Max(_img.Width, _img.Height));
        _disp = new Mat();
        Cv2.Resize(_img, _disp, new OpenCvSharp.Size((int)Math.Round(_img.Width * _dispScale), (int)Math.Round(_img.Height * _dispScale)), 0, 0, InterpolationFlags.Area);
        _poly.Clear(); Overlay.Children.Clear();
        ChkSkip.IsChecked = r.Page.Skip;
        TxtPage.Text = $"{LstPages.SelectedIndex + 1} / {_rows.Count}";
        BtnPrev.IsEnabled = LstPages.SelectedIndex > 0;
        BtnNext.IsEnabled = LstPages.SelectedIndex < _rows.Count - 1;
        Redraw();
        TxtStatus.Text = $"{r.Name} — 감지: {r.Page.Summary}. 빨간 곳이 지워져요. 놓친 검열은 칠하고, 잘못 잡힌 곳은 지우개로 지우세요.";
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) { if (LstPages.SelectedIndex > 0) LstPages.SelectedIndex--; }
    private void BtnNext_Click(object sender, RoutedEventArgs e) { if (LstPages.SelectedIndex < _rows.Count - 1) LstPages.SelectedIndex++; }

    private void UpdateStatus(Row r)
    {
        int px = r.Page.Mask == null ? 0 : Cv2.CountNonZero(r.Page.Mask);
        r.Status = r.Page.Skip ? "건너뜀 (검열 없음)" : px == 0 ? "마스크 없음" : $"마스크 {px:N0}px";
        LstPages.Items.Refresh();
    }

    // ── 그리기 ────────────────────────────────────────────────
    private void Redraw()
    {
        if (_disp == null || _cur?.Page.Mask == null) return;
        using var over = _disp.Clone();
        using var mSmall = new Mat();
        Cv2.Resize(_cur.Page.Mask, mSmall, _disp.Size(), 0, 0, InterpolationFlags.Nearest);
        using var red = new Mat(over.Size(), MatType.CV_8UC3, new Scalar(40, 40, 230));
        using var blended = new Mat();
        Cv2.AddWeighted(over, 0.45, red, 0.55, 0, blended);
        blended.CopyTo(over, mSmall);
        // 마스크 테두리를 진하게 — 어디까지 지워지는지 또렷하게
        Cv2.FindContours(mSmall, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Cv2.DrawContours(over, cs, -1, new Scalar(0, 0, 255), 2);
        ImgView.Source = ToBitmap(over);
        ImageHost.Width = _disp.Width * _zoom; ImageHost.Height = _disp.Height * _zoom;
        ImgView.Width = ImageHost.Width; ImgView.Height = ImageHost.Height;
        Overlay.Width = ImageHost.Width; Overlay.Height = ImageHost.Height;
        DrawPolyPreview();
    }

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

    /// <summary>ImageHost 안 좌표(확대 적용된 화면 px) → 원본 px</summary>
    private CvPoint ToImage(WinPoint p) => new((int)Math.Round(p.X / _zoom / _dispScale), (int)Math.Round(p.Y / _zoom / _dispScale));

    private void PushUndo()
    {
        if (_cur?.Page.Mask == null) return;
        _cur.Undo.Add(_cur.Page.Mask.Clone());
        while (_cur.Undo.Count > 25) { _cur.Undo[0].Dispose(); _cur.Undo.RemoveAt(0); }
    }

    // ── 브러시·지우개 ─────────────────────────────────────────
    private bool _painting; private CvPoint _last;
    private bool _panning; private WinPoint _panStart; private double _panH, _panV;
    private readonly List<CvPoint> _poly = [];

    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_cur?.Page.Mask == null) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true; _panStart = e.GetPosition(Scroller); _panH = Scroller.HorizontalOffset; _panV = Scroller.VerticalOffset;
            ImageHost.CaptureMouse(); return;
        }
        if (e.ChangedButton != MouseButton.Left) { if (e.ChangedButton == MouseButton.Right && RbPoly.IsChecked == true && _poly.Count > 0) { _poly.RemoveAt(_poly.Count - 1); DrawPolyPreview(); } return; }
        var p = ToImage(e.GetPosition(ImageHost));
        if (RbPoly.IsChecked == true)
        {
            if (e.ClickCount >= 2) { ClosePolygon(); return; }
            _poly.Add(p); DrawPolyPreview(); return;
        }
        PushUndo();
        _painting = true; _last = p;
        Dab(p, p);
        ImageHost.CaptureMouse();
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
            Scroller.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
            return;
        }
        _cursorPos = e.GetPosition(ImageHost); _cursorVisible = true;
        if (_painting && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = ToImage(_cursorPos);
            Dab(_last, p); _last = p;   // Dab → Redraw → DrawPolyPreview가 커서도 다시 올린다
        }
        else UpdateBrushCursor();
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { _panning = false; ImageHost.ReleaseMouseCapture(); return; }
        if (e.ChangedButton == MouseButton.Left && _painting) { _painting = false; ImageHost.ReleaseMouseCapture(); if (_cur != null) UpdateStatus(_cur); }
    }

    private void Host_MouseLeave(object sender, MouseEventArgs e) { _cursorVisible = false; UpdateBrushCursor(); }

    private void Dab(CvPoint a, CvPoint b)
    {
        if (_cur?.Page.Mask == null) return;
        int r = (int)SldBrush.Value;
        var color = RbErase.IsChecked == true ? Scalar.Black : Scalar.White;
        Cv2.Line(_cur.Page.Mask, a, b, color, r * 2, LineTypes.Link8);
        Cv2.Circle(_cur.Page.Mask, b, r, color, -1);
        Redraw();
    }

    // ── 다각형 ────────────────────────────────────────────────
    private void DrawPolyPreview()
    {
        Overlay.Children.Clear();
        if (_poly.Count > 0)
        {
            var pl = new Polyline { Stroke = Brushes.Yellow, StrokeThickness = 2, StrokeDashArray = [4, 2] };
            foreach (var p in _poly) pl.Points.Add(new WinPoint(p.X * _dispScale * _zoom, p.Y * _dispScale * _zoom));
            Overlay.Children.Add(pl);
            foreach (var p in _poly)
            {
                var dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Yellow };
                Canvas.SetLeft(dot, p.X * _dispScale * _zoom - 4); Canvas.SetTop(dot, p.Y * _dispScale * _zoom - 4);
                Overlay.Children.Add(dot);
            }
        }
        UpdateBrushCursor();   // Clear로 지워진 커서를 다시 올린다
    }

    private void ClosePolygon()
    {
        if (_cur?.Page.Mask == null || _poly.Count < 3) { _poly.Clear(); DrawPolyPreview(); return; }
        PushUndo();
        Cv2.FillPoly(_cur.Page.Mask, [_poly.ToArray()], RbErase.IsChecked == true ? Scalar.Black : Scalar.White);
        _poly.Clear();
        Redraw(); UpdateStatus(_cur);
    }

    // ── 버튼 ──────────────────────────────────────────────────
    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_cur?.Page.Mask == null) return;
        if (_poly.Count > 0) { _poly.RemoveAt(_poly.Count - 1); DrawPolyPreview(); return; }
        if (_cur.Undo.Count == 0) return;
        var prev = _cur.Undo[^1]; _cur.Undo.RemoveAt(_cur.Undo.Count - 1);
        prev.CopyTo(_cur.Page.Mask); prev.Dispose();
        Redraw(); UpdateStatus(_cur);
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        if (_cur?.Page.Mask == null) return;
        PushUndo();
        if (_cur.Original != null) _cur.Original.CopyTo(_cur.Page.Mask); else _cur.Page.Mask.SetTo(Scalar.Black);
        Redraw(); UpdateStatus(_cur);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_cur?.Page.Mask == null) return;
        PushUndo(); _cur.Page.Mask.SetTo(Scalar.Black); _poly.Clear();
        Redraw(); UpdateStatus(_cur);
    }

    private void ChkSkip_Changed(object sender, RoutedEventArgs e)
    {
        if (_cur == null) return;
        _cur.Page.Skip = ChkSkip.IsChecked == true;
        UpdateStatus(_cur);
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_poly.Count >= 3) ClosePolygon();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RbPoly.IsChecked == true && _poly.Count >= 3) { ClosePolygon(); e.Handled = true; }
        else if (e.Key == Key.Escape && _poly.Count > 0) { _poly.Clear(); DrawPolyPreview(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { BtnUndo_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control) BtnNext_Click(sender, e);
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control) BtnPrev_Click(sender, e);
        else if (e.Key == Key.B) RbAdd.IsChecked = true;
        else if (e.Key == Key.E) RbErase.IsChecked = true;
        else if (e.Key == Key.P) RbPoly.IsChecked = true;
    }

    // ── 확대 ──────────────────────────────────────────────────
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _zoom = e.NewValue; Redraw();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SldZoom.Value = Math.Clamp(SldZoom.Value * (e.Delta > 0 ? 1.15 : 1 / 1.15), SldZoom.Minimum, SldZoom.Maximum);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _img?.Dispose(); _disp?.Dispose();
        foreach (var r in _rows) { r.Original?.Dispose(); foreach (var u in r.Undo) u.Dispose(); r.Undo.Clear(); }
    }
}
