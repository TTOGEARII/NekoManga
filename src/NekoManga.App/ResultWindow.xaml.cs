using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoManga.Core;
using NekoManga.Core.Engine;
using OpenCvSharp;
// UseWindowsForms가 켜져 있어 Brush·MouseEventArgs 등이 WinForms와 겹친다 — 별칭을 유지할 것.
using Point = System.Windows.Point;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace NekoManga.App;

/// <summary>결과 이미지를 띄워 놓고, 자동 감지가 놓친 검열을 사용자가 직접 네모로 집어 지우는 창.
///
/// 자동 감지는 모양 규칙(직사각형·길쭉함·채움비)으로 판단하므로 그림 선과 맞붙은 막대나
/// 희한한 모양은 끝내 놓친다. 그때 원고를 다시 돌리는 대신 여기서 그 자리만 집어 지운다.
/// 복원은 내장 LaMa로 한다 — 즉시 끝나고(수백 ms) 같은 입력이면 항상 같은 결과라
/// 손으로 다듬는 작업에 맞는다 (SDXL은 시드마다 결과가 달라 되돌리기·재시도가 번거롭다).</summary>
public partial class ResultWindow
{
    private readonly string _path;
    private Mat _img = new();
    private readonly Stack<Mat> _undo = new();
    private readonly List<Rect> _selections = [];

    private bool _dragging;
    private Point _dragStart;
    private double _scale = 1.0;
    private bool _dirty;

    public ResultWindow(string imagePath)
    {
        InitializeComponent();
        _path = imagePath;
        TxtFile.Text = imagePath;

        _img = ImgIo.Read(imagePath, ImreadModes.Color);
        if (_img.Empty())
        {
            TxtStatus.Text = "이미지를 읽지 못했어요: " + Path.GetFileName(imagePath);
            BtnApply.IsEnabled = false;
            return;
        }
        Loaded += (_, _) => { FitToWindow(); Redraw(); };
    }

    // ── 표시 ────────────────────────────────────────────────
    private void FitToWindow()
    {
        double avail = Math.Max(200, Scroller.ActualWidth - 24);
        double availH = Math.Max(200, Scroller.ActualHeight - 24);
        _scale = Math.Min(Math.Min(avail / _img.Width, availH / _img.Height), 1.0);
        SldZoom.Value = _scale;
    }

    private void Redraw()
    {
        ImgView.Source = ToBitmap(_img);
        ImageHost.Width = _img.Width * _scale;
        ImageHost.Height = _img.Height * _scale;
        ImgView.Width = ImageHost.Width;
        ImgView.Height = ImageHost.Height;
        Overlay.Width = ImageHost.Width;
        Overlay.Height = ImageHost.Height;
        TxtZoom.Text = $"{_scale * 100:0}%";
        DrawSelections();
    }

    /// <summary>Mat(BGR) → WPF 비트맵. OpenCvSharp.Extensions 없이 직접 옮긴다.</summary>
    private static BitmapSource ToBitmap(Mat m)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(m, rgb, ColorConversionCodes.BGR2BGRA);
        int stride = rgb.Width * 4;
        var buf = new byte[stride * rgb.Height];
        System.Runtime.InteropServices.Marshal.Copy(rgb.Data, buf, 0, buf.Length);
        var bmp = BitmapSource.Create(rgb.Width, rgb.Height, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }

    private void DrawSelections()
    {
        Overlay.Children.Clear();
        foreach (var r in _selections) Overlay.Children.Add(MakeBox(r, Brushes.OrangeRed));
    }

    private System.Windows.Shapes.Rectangle MakeBox(Rect r, Brush stroke)
    {
        var box = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, r.Width * _scale),
            Height = Math.Max(1, r.Height * _scale),
            Stroke = stroke,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(48, 255, 80, 60)),
        };
        Canvas.SetLeft(box, r.X * _scale);
        Canvas.SetTop(box, r.Y * _scale);
        return box;
    }

    // ── 선택 ────────────────────────────────────────────────
    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_img.Empty()) return;
        _dragging = true;
        _dragStart = e.GetPosition(ImageHost);
        ImageHost.CaptureMouse();
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(ImageHost);
        DrawSelections();
        Overlay.Children.Add(MakeBox(RectFrom(_dragStart, p), Brushes.Gold));
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ImageHost.ReleaseMouseCapture();
        var r = RectFrom(_dragStart, e.GetPosition(ImageHost));
        if (r.Width >= 3 && r.Height >= 3) _selections.Add(r);
        DrawSelections();
        TxtStatus.Text = _selections.Count == 0 ? "준비됐어요" : $"{_selections.Count}곳 선택됨 — [선택 영역 지우기]를 누르세요";
    }

    /// <summary>화면 좌표 두 점 → 이미지 좌표 사각형 (이미지 밖은 잘라낸다).</summary>
    private Rect RectFrom(Point a, Point b)
    {
        int x1 = (int)Math.Round(Math.Min(a.X, b.X) / _scale), x2 = (int)Math.Round(Math.Max(a.X, b.X) / _scale);
        int y1 = (int)Math.Round(Math.Min(a.Y, b.Y) / _scale), y2 = (int)Math.Round(Math.Max(a.Y, b.Y) / _scale);
        x1 = Math.Clamp(x1, 0, _img.Width - 1);
        x2 = Math.Clamp(x2, 0, _img.Width);
        y1 = Math.Clamp(y1, 0, _img.Height - 1);
        y2 = Math.Clamp(y2, 0, _img.Height);
        return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        _selections.Clear();
        DrawSelections();
        TxtStatus.Text = "선택을 지웠어요";
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty()) return;
        if (_selections.Count == 0)
        {
            TxtStatus.Text = "먼저 지울 곳을 드래그해 주세요";
            return;
        }

        int grow = int.TryParse(TxtGrow.Text.Trim(), out var g) ? Math.Clamp(g, 0, 40) : 6;
        bool darkOnly = RbDarkOnly.IsChecked == true;

        using var mask = BuildMask(darkOnly, grow);
        int px = Cv2.CountNonZero(mask);
        if (px == 0)
        {
            TxtStatus.Text = darkOnly
                ? "네모 안에서 검은 부분을 찾지 못했어요 — [네모 전체]로 바꿔 보세요"
                : "지울 영역이 비어 있어요";
            return;
        }

        BtnApply.IsEnabled = false;
        TxtStatus.Text = $"{px:N0}px 복원 중...";
        var before = _img.Clone();

        try
        {
            await Task.Run(() =>
            {
                using var env = new OnnxEnv(App.Settings);
                var lama = new LamaInpainter(env);
                if (!lama.IsReady)
                    throw new InvalidOperationException("복원 모델(LaMa)이 없어요 — 설정 → 구성요소에서 설치해 주세요.");
                using var forLama = mask.Clone(); // LaMa 내부 FindContours가 마스크를 파괴한다
                lama.Inpaint(_img, forLama);
            });

            _undo.Push(before);
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _selections.Clear();
            Redraw();
            TxtStatus.Text = $"지웠어요 ({px:N0}px) — 마음에 들면 [저장]을 눌러주세요";
        }
        catch (Exception ex)
        {
            before.Dispose();
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally { BtnApply.IsEnabled = true; }
    }

    /// <summary>선택한 네모들로 복원 마스크를 만든다.
    /// '검은 부분만'이면 네모 안에서 어두운 픽셀만 골라 — 네모를 넉넉히 쳐도 주변 그림이 함께
    /// 뭉개지지 않는다. 가장자리 안티에일리어싱까지 덮도록 grow만큼 부풀린다.</summary>
    private Mat BuildMask(bool darkOnly, int grow)
    {
        var mask = new Mat(_img.Rows, _img.Cols, MatType.CV_8UC1, Scalar.Black);
        using var gray = new Mat();
        Cv2.CvtColor(_img, gray, ColorConversionCodes.BGR2GRAY);

        foreach (var r in _selections)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            using var dstRoi = new Mat(mask, r);
            if (!darkOnly)
            {
                dstRoi.SetTo(Scalar.White);
                continue;
            }
            using var grayRoi = new Mat(gray, r);
            using var bin = new Mat();
            // 막대는 거의 순흑이지만 스캔본은 들쑥날쑥하다 — 120까지 넉넉히 잡는다.
            Cv2.Threshold(grayRoi, bin, 120, 255, ThresholdTypes.BinaryInv);
            Cv2.BitwiseOr(dstRoi, bin, dstRoi);
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

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ImgIo.Write(_path, _img);
            _dirty = false;
            BtnSave.IsEnabled = false;
            TxtStatus.Text = "저장했어요 → " + Path.GetFileName(_path);
        }
        catch (Exception ex) { TxtStatus.Text = "저장 실패: " + ex.Message; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty &&
            MessageBox.Show("저장하지 않은 수정이 있어요. 그냥 닫을까요?", "결과 보기",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
        _img.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
    }

    // ── 확대/축소 ───────────────────────────────────────────
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_img.Empty() || !IsLoaded) return;
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
