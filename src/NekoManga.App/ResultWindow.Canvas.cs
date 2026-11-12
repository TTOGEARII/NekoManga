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

// 화면 표시와 그리기 입력 — 창 맞춤·다시 그리기·확대/축소, 마스크 다각형·브러시 커서 오버레이,
// 마우스(마스크 칠하기·패닝)와 키 입력(포토샵식 단축키)
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
        // 마스크 보기 + 재생성/커스텀 모델: 실제 다시 그려질 부위(RegenRegion)의 외곽을 주황 점선으로 (마스크보다 넓다)
        if (MaskMode && _regionContours != null)
        {
            foreach (var c in _regionContours)
            {
                var pl = new System.Windows.Shapes.Polygon { Stroke = Brushes.DarkOrange, StrokeThickness = 2, StrokeDashArray = [6, 4], Fill = null };
                foreach (var p in c) pl.Points.Add(new WinPoint(p.X * _scale, p.Y * _scale));
                Overlay.Children.Add(pl);
            }
        }
        // 마스크 보기: 찍는 중인 다각형 미리보기 (결과 보기에는 오버레이가 없다 — 옛 '점 찍기·브러시 도형'은 탭 통합으로 없어졌다, 2026-11-11)
        if (MaskMode && _maskPoly.Count > 0)
        {
            var pl = new System.Windows.Shapes.Polyline { Stroke = Brushes.Gold, StrokeThickness = 2, StrokeDashArray = [4, 2] };
            foreach (var p in _maskPoly) pl.Points.Add(new WinPoint(p.X * _scale, p.Y * _scale));
            Overlay.Children.Add(pl);
            foreach (var p in _maskPoly) Overlay.Children.Add(MakeDot(p));
        }
        UpdateBrushCursor();   // Clear로 지워진 브러시 커서를 다시 올린다
    }

    private void UpdateBrushCursor()
    {
        bool show = _cursorVisible && !_img.Empty() && MaskMode && _maskMat != null && RbMPoly.IsChecked != true;
        double r = SldMBrush.Value * _scale;
        _cursorInner.Stroke = RbMErase.IsChecked == true ? Brushes.DeepSkyBlue : Brushes.Red;
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

    // ── 좌표 ────────────────────────────────────────────────
    private CvPoint ToImage(WinPoint p)
    {
        var (w, h) = ShownSize;
        return new(Math.Clamp((int)Math.Round(p.X / _scale), 0, Math.Max(0, w - 1)),
                   Math.Clamp((int)Math.Round(p.Y / _scale), 0, Math.Max(0, h - 1)));
    }

    // ── 화면 이동(패닝): 휠(가운데) 버튼 드래그, 또는 Space를 누른 채 왼쪽 드래그(포토샵 손 도구). 맨 좌클릭은 기존 동작 그대로 ──
    private bool _panning;
    private WinPoint _panStartScreen;
    private double _panStartH, _panStartV;

    private void StartPan(MouseButtonEventArgs e)
    {
        _panning = true;
        _panStartScreen = e.GetPosition(Scroller);
        _panStartH = Scroller.HorizontalOffset;
        _panStartV = Scroller.VerticalOffset;
        ImageHost.CaptureMouse();
        e.Handled = true;
    }

    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _img.Empty()) return;
        StartPan(e);
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
        if (!_panning || (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left)) return;
        _panning = false;
        ImageHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Host_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_img.Empty() || _busy) return;
        if (_spaceDown) { StartPan(e); return; }   // 손 도구 — 어느 탭이든 그리지 않고 화면을 끈다

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
        // 결과 보기: 그림을 보기만 한다 — 칠하려면 마스크 보기로
        if (ResultView) TxtStatus.Text = "결과 보기예요 — 지울 곳을 칠하려면 [🎭 마스크 보기]로 바꾸세요";
    }

    private void Host_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (MaskMode && _maskPoly.Count > 0) { _maskPoly.RemoveAt(_maskPoly.Count - 1); DrawShapes(); }
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        // 휠 버튼 드래그(또는 Space+왼쪽 드래그) = 패닝 (확대 상태에서 화면 이동)
        if (_panning && (e.MiddleButton == MouseButtonState.Pressed || e.LeftButton == MouseButtonState.Pressed))
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

        if (LetterMode || _img.Empty()) return;
        UpdateBrushCursor();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 도구·확대·저장 단축키는 Window_PreviewKeyDown(포토샵 맞춤)에 있다 — 여기는 Enter/Esc/페이지 넘김만
        if (MaskMode && Keyboard.FocusedElement is not TextBox)
        {
            switch (e.Key)
            {
                case Key.Enter: MaskClosePolygon(); e.Handled = true; return;
                case Key.Escape: _maskPoly.Clear(); DrawShapes(); e.Handled = true; return;
            }
        }
        switch (e.Key)
        {
            // 텍스트박스에서 타이핑 중일 땐 방향키로 페이지를 넘기지 않는다 (커서 이동용)
            case Key.Left when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index - 1, fit: true); break;
            case Key.Right when !_busy && Keyboard.FocusedElement is not TextBox: LoadPage(_index + 1, fit: true); break;
        }
    }

    // ── 단축키 — 포토샵과 같게 (2026-11-11 사용자 요청: "포토샵대로") ──────────────────────
    // 확대: Ctrl + '+' / '-', 화면 맞춤 Ctrl+0, 100% Ctrl+1, Alt+휠(커서 자리 기준). 맨 휠은 세로 스크롤, Ctrl+휠은 가로 스크롤.
    // 이동: Space를 누른 채 드래그(손 도구) 또는 휠 버튼 드래그. 브러시 크기 [ ]. 도구 B(브러시/칠하기)·E(지우개)·L/P(점 찍기·다각형).
    // 저장 Ctrl+S, 선택 지우기 Ctrl+D, 되돌리기 Ctrl+Z. 텍스트박스에 커서가 있으면(식자 편집·크기 칸) 글자 입력이 우선이라 전부 건너뛴다.
    // Preview 단계에서 받는 이유: 라디오 버튼에 포커스가 있으면 Space가 버튼을 누르고(KeyDown을 버튼이 먹는다), 손 도구가 되지 않는다.
    private const double ZoomStep = 1.25;
    private bool _spaceDown;
    private System.Windows.Input.Cursor? _hostCursor;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (HandleShortcut(key, ctrl, e)) e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) EndSpace();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        EndSpace();   // 창을 떠나며 Space를 놓으면 KeyUp이 안 온다
    }

    private void EndSpace()
    {
        if (!_spaceDown) return;
        _spaceDown = false;
        ImageHost.Cursor = _hostCursor;
    }

    private bool HandleShortcut(Key key, bool ctrl, KeyEventArgs e)
    {
        switch (key)
        {
            case Key.Space:
                if (!_spaceDown && !_img.Empty()) { _spaceDown = true; _hostCursor = ImageHost.Cursor; ImageHost.Cursor = Cursors.Hand; }
                return true;   // 키 반복도 삼킨다 — 포커스가 있는 버튼이 눌리지 않게
            case Key.OemPlus or Key.Add when ctrl: ZoomBy(ZoomStep); return true;
            case Key.OemMinus or Key.Subtract when ctrl: ZoomBy(1 / ZoomStep); return true;
            case Key.D0 or Key.NumPad0 when ctrl: if (!_img.Empty()) FitToWindow(); return true;
            case Key.D1 or Key.NumPad1 when ctrl: SldZoom.Value = 1; return true;
            case Key.OemOpenBrackets when !ctrl: StepBrush(-1); return true;
            case Key.OemCloseBrackets when !ctrl: StepBrush(+1); return true;
            case Key.S when ctrl: if (BtnSave.IsEnabled && BtnSave.Visibility == Visibility.Visible && !_busy && !_loading) BtnSave_Click(this, e); return true;
            case Key.D when ctrl: if (MaskMode && !_busy) BtnMClear_Click(this, e); return true;   // 포토샵 Ctrl+D(선택 해제) ≈ 마스크 전부 지우기 (되돌릴 수 있다)
            case Key.Z when ctrl: if (!_busy) BtnUndo_Click(this, e); return true;
            case Key.B when !ctrl: if (MaskMode) RbMAdd.IsChecked = true; return true;
            case Key.E when !ctrl: if (MaskMode) RbMErase.IsChecked = true; return true;
            case Key.L or Key.P when !ctrl: if (MaskMode) RbMPoly.IsChecked = true; return true;
        }
        return false;
    }

    /// <summary>[ ] — 브러시 반지름을 한 단계(±20%, 최소 1px) 바꾼다 (마스크 보기의 슬라이더).</summary>
    private void StepBrush(int dir)
    {
        static double Step(double v, int d) => d > 0 ? Math.Max(v + 1, Math.Round(v * 1.2)) : Math.Min(v - 1, Math.Round(v / 1.2));
        if (MaskMode) SldMBrush.Value = Math.Clamp(Step(SldMBrush.Value, dir), SldMBrush.Minimum, SldMBrush.Maximum);
    }

    // ── 확대/축소 ───────────────────────────────────────────
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _img.Empty()) return;
        _scale = e.NewValue;
        Redraw();
    }

    /// <summary>배율을 factor배로 바꾸되 anchor(Scroller 안 좌표)가 가리키던 그림 자리가 제자리에 있게 스크롤을 맞춘다 — 포토샵의 커서 기준 확대.
    /// anchor가 없으면 보이는 영역의 가운데.</summary>
    private void ZoomBy(double factor, WinPoint? anchor = null)
    {
        if (_img.Empty()) return;
        double before = SldZoom.Value;
        double after = Math.Clamp(before * factor, SldZoom.Minimum, SldZoom.Maximum);
        if (after == before) return;
        var a = anchor ?? new WinPoint(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        // ImageHost는 가운데 정렬이라 놓인 자리를 직접 잰다: 그림 좌표(배율 전) = (anchor - 놓인 자리) / 배율
        var hostAt = ImageHost.TranslatePoint(new WinPoint(0, 0), Scroller);
        double ix = (a.X - hostAt.X) / before, iy = (a.Y - hostAt.Y) / before;
        SldZoom.Value = after;        // → Zoom_Changed → Redraw(ImageHost 크기가 바뀐다)
        Scroller.UpdateLayout();
        var hostAfter = ImageHost.TranslatePoint(new WinPoint(0, 0), Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + (hostAfter.X + ix * after) - a.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + (hostAfter.Y + iy * after) - a.Y);
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt))
        {
            ZoomBy(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(Scroller));
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
        // 맨 휠은 ScrollViewer가 세로로 스크롤한다 (포토샵과 같다 — 전에는 휠이 확대였다)
    }
}
