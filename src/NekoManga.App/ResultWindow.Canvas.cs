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

// 화면 표시와 그리기 입력 — 창 맞춤·다시 그리기·확대/축소, 도형·브러시 오버레이,
// 마우스(점 찍기·브러시·패닝)와 키 입력
public partial class ResultWindow
{
    // ── 표시 ────────────────────────────────────────────────
    private void FitToWindow()
    {
        double w = Math.Max(200, Scroller.ActualWidth - 24);
        double h = Math.Max(200, Scroller.ActualHeight - 24);
        var (sw, sh) = ShownSize;
        _scale = Math.Min(Math.Min(w / Math.Max(1, sw), h / Math.Max(1, sh)), 1.0);
        SldZoom.Value = _scale;
    }

    /// <summary>지금 화면에 깔리는 그림의 원본 크기 — 마스크 탭은 검열 제거 전 원고, 식자 편집은 base, 그 외 결과물.</summary>
    private (int W, int H) ShownSize =>
        MaskMode && _maskPre != null ? (_maskPre.Width, _maskPre.Height)
        : LetterMode && !_letterPreview && _baseImg != null ? (_baseImg.Width, _baseImg.Height)
        : (_img.Width, _img.Height);

    private void Redraw()
    {
        if (_img.Empty()) return;
        if (MaskMode && _maskPre != null && _maskMat != null)
        {
            // 마스크 탭: 검열 제거 전 원고(축소본) 위에 빨간 마스크 합성 — 결과물이 아니라 마스크 좌표계(원본 크기)의 그림
            ImgView.Source = RenderMaskComposite();
            ImageHost.Width = ImgView.Width = Overlay.Width = EditLayer.Width = _maskPre.Width * _scale;
            ImageHost.Height = ImgView.Height = Overlay.Height = EditLayer.Height = _maskPre.Height * _scale;
            TxtZoom.Text = $"{_scale * 100:0}%";
            EditLayer.Visibility = Visibility.Collapsed;
            DrawShapes();
            return;
        }
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
        if (MaskMode)
        {
            if (_maskPoly.Count > 0)
            {
                var pl = new System.Windows.Shapes.Polyline { Stroke = Brushes.Gold, StrokeThickness = 2, StrokeDashArray = [4, 2] };
                foreach (var p in _maskPoly) pl.Points.Add(new WinPoint(p.X * _scale, p.Y * _scale));
                Overlay.Children.Add(pl);
                foreach (var p in _maskPoly) Overlay.Children.Add(MakeDot(p));
            }
            UpdateBrushCursor();
            return;
        }

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

    private void UpdateBrushCursor()
    {
        bool show = _cursorVisible && !_img.Empty() && !LetterMode
                    && (MaskMode ? (_maskMat != null && RbMPoly.IsChecked != true) : BrushMode);
        double r = (MaskMode ? SldMBrush.Value : BrushRadius) * _scale;
        _cursorInner.Stroke = MaskMode ? (RbMErase.IsChecked == true ? Brushes.DeepSkyBlue : Brushes.Red) : Brushes.OrangeRed;
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
    private CvPoint ToImage(WinPoint p)
    {
        var (w, h) = ShownSize;
        return new(Math.Clamp((int)Math.Round(p.X / _scale), 0, Math.Max(0, w - 1)),
                   Math.Clamp((int)Math.Round(p.Y / _scale), 0, Math.Max(0, h - 1)));
    }

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
        if (e.ChangedButton == MouseButton.Left && _maskPainting)
        {
            _maskPainting = false;
            ImageHost.ReleaseMouseCapture();
            UpdateMaskInfo();
            e.Handled = true;
            return;
        }
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

        if (MaskMode) { MaskLeftClick(e); return; }

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
        if (MaskMode)
        {
            if (_maskPoly.Count > 0) { _maskPoly.RemoveAt(_maskPoly.Count - 1); DrawShapes(); }
            return;
        }
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

        if (MaskMode)
        {
            if (_maskPainting && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = ToImage(_hover);
                MaskDab(_maskLast, p); _maskLast = p;   // Redraw → DrawShapes가 커서도 다시 올린다
            }
            else if (_maskPoly.Count > 0) DrawShapes();
            else UpdateBrushCursor();
            return;
        }

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
        if (MaskMode && Keyboard.FocusedElement is not TextBox)
        {
            switch (e.Key)
            {
                case Key.Enter: MaskClosePolygon(); e.Handled = true; return;
                case Key.Escape: _maskPoly.Clear(); DrawShapes(); e.Handled = true; return;
                case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: MaskUndo(); e.Handled = true; return;
                case Key.B: RbMAdd.IsChecked = true; return;
                case Key.E: RbMErase.IsChecked = true; return;
                case Key.P: RbMPoly.IsChecked = true; return;
            }
        }
        switch (e.Key)
        {
            case Key.Enter: CloseShape(); break;
            case Key.Escape: _pending.Clear(); DrawShapes(); TxtStatus.Text = "찍던 점을 취소했어요"; break;
            // 텍스트박스에서 타이핑 중일 땐 방향키로 페이지를 넘기지 않는다 (커서 이동용)
            case Key.Left when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index - 1, fit: true); break;
            case Key.Right when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index + 1, fit: true); break;
        }
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
