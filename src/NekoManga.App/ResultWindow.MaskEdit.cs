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

// 마스크 수정 탭 — 저장된 검열 마스크를 고쳐 저장하고, 검열 제거 진행·다시 돌리기
public partial class ResultWindow
{
    // ── 🎭 마스크 수정 탭 (2026-10-21, 사용자 요청) ───────────────────────────────
    // 검토 대기 페이지: 러너가 .neko에 남긴 마스크(원본 좌표)와 검열 제거 전 원고를 보여 주고, 고친 마스크를 사이드카에 저장한 뒤 러너에 '진행'을 알린다.
    // 끝난 페이지: 같은 마스크를 고쳐 결과물에 바로 다시 제거(빠름)하거나, 보존된 원고에서 2차 전체를 다시 돌린다(채색부터 다시).
    private MaskSidecar? _maskSc;
    private Mat? _maskMat;        // 원본 크기 CV_8UC1, 255=지울 곳 (창이 소유)
    private Mat? _maskPre;        // 검열 제거 전 원고 (원본 크기)
    private Mat? _maskPreDisp;    // 표시용 축소본
    private readonly List<Mat> _maskUndo = [];
    private readonly List<CvPoint> _maskPoly = [];
    private bool _maskDirty, _maskEdited, _maskPainting;
    private bool _maskResetting;   // 페이지를 읽어 체크박스를 맞추는 중 — 이때 Checked/Unchecked는 사용자 수정이 아니다
    private CvPoint _maskLast;

    private bool MaskMode => TabMode.SelectedItem == TabMask;
    /// <summary>러너가 대기·확인에 쓰는 키 — 창이 아는 원본 경로. 사이드카 안의 Source는 폴더를 옮긴 뒤 경로 갱신 저장이 실패하면 옛 경로라 못 믿는다 (--result 모드만 사이드카 값)</summary>
    private string WaitKey => !string.IsNullOrEmpty(_refs[_index].Source) ? _refs[_index].Source : _maskSc?.Source ?? "";
    private bool IsWaitingPage => _maskSc != null && _isWaiting?.Invoke(WaitKey) == true;

    private void ResetMaskState(PageData? data)
    {
        _maskMat?.Dispose(); _maskPre?.Dispose(); _maskPreDisp?.Dispose();
        foreach (var u in _maskUndo) u.Dispose();
        _maskUndo.Clear(); _maskPoly.Clear();
        _maskDirty = _maskEdited = _maskPainting = false;
        _maskSc = data?.MaskSc; _maskMat = data?.Mask; _maskPre = data?.Pre; _maskPreDisp = data?.PreDisp;
        // 체크박스를 사이드카 값으로 맞추는 것은 수정이 아니다 — 가드 없이 바꾸면 ChkMSkip_Changed가 _maskDirty를 켜서
        // 손도 안 댄 페이지를 넘길 때마다 "고친 마스크를 저장할까요?"를 물었다
        if (data != null)
        {
            _maskResetting = true;
            try { ChkMSkip.IsChecked = data.MaskSc?.Skip == true; }
            finally { _maskResetting = false; }
        }
    }

    private void UpdateMaskInfo()
    {
        if (_maskSc == null || _maskMat == null)
        {
            TxtMaskInfo.Text = "이 페이지는 저장된 마스크가 없어요 (검열 제거를 켜고 돌린 페이지만)";
            BtnMRedo.Visibility = Visibility.Collapsed;
            return;
        }
        bool waiting = IsWaitingPage;
        int px = Cv2.CountNonZero(_maskMat);
        TxtMaskInfo.Text = (waiting ? "검토 대기 중 — 고친 뒤 [검열 제거 진행]" : _maskSc.Confirmed ? "제거에 쓴 마스크" : "저장된 마스크")
                         + $" · {px:N0}px" + (_maskEdited || _maskSc.Edited ? " · 수정됨" : "") + (ChkMSkip.IsChecked == true ? " · 검열 없음 표시" : "");
        BtnMRedo.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
    }

    private BitmapSource RenderMaskComposite()
    {
        using var over = _maskPreDisp!.Clone();
        using var mSmall = new Mat();
        Cv2.Resize(_maskMat!, mSmall, over.Size(), 0, 0, InterpolationFlags.Nearest);
        using var red = new Mat(over.Size(), MatType.CV_8UC3, new Scalar(40, 40, 230));
        using var blended = new Mat();
        Cv2.AddWeighted(over, 0.45, red, 0.55, 0, blended);
        blended.CopyTo(over, mSmall);
        // 마스크 테두리를 진하게 — 어디까지 지워지는지 또렷하게 (FindContours는 입력을 망가뜨리므로 축소본에만)
        Cv2.FindContours(mSmall, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Cv2.DrawContours(over, cs, -1, new Scalar(0, 0, 255), 2);
        return ToBitmap(over);
    }

    private void MaskPushUndo()
    {
        if (_maskMat == null) return;
        _maskUndo.Add(_maskMat.Clone());
        while (_maskUndo.Count > 25) { _maskUndo[0].Dispose(); _maskUndo.RemoveAt(0); }
    }

    private void MaskTouched()
    {
        _maskDirty = _maskEdited = true;
        BtnSave.IsEnabled = true; BtnUndo.IsEnabled = true;
    }

    private void MaskDab(CvPoint a, CvPoint b)
    {
        if (_maskMat == null) return;
        int r = (int)SldMBrush.Value;
        var color = RbMErase.IsChecked == true ? Scalar.Black : Scalar.White;
        Cv2.Line(_maskMat, a, b, color, r * 2, LineTypes.Link8);
        Cv2.Circle(_maskMat, b, r, color, -1);
        MaskTouched();
        Redraw();
    }

    private void MaskLeftClick(MouseButtonEventArgs e)
    {
        if (_maskMat == null) return;
        var p = ToImage(e.GetPosition(ImageHost));
        if (RbMPoly.IsChecked == true)
        {
            if (e.ClickCount >= 2) { MaskClosePolygon(); return; }
            _maskPoly.Add(p); DrawShapes();
            TxtStatus.Text = $"{_maskPoly.Count}점 — 더블클릭(또는 Enter)으로 닫으면 {(RbMErase.IsChecked == true ? "지워져요" : "마스크에 더해져요")}";
            return;
        }
        MaskPushUndo();
        _maskPainting = true; _maskLast = p;
        MaskDab(p, p);
        ImageHost.CaptureMouse();
        e.Handled = true;
    }

    private void MaskClosePolygon()
    {
        if (_maskMat == null || _maskPoly.Count < 3) { _maskPoly.Clear(); DrawShapes(); return; }
        MaskPushUndo();
        Cv2.FillPoly(_maskMat, [_maskPoly.ToArray()], RbMErase.IsChecked == true ? Scalar.Black : Scalar.White);
        _maskPoly.Clear();
        MaskTouched();
        Redraw(); UpdateMaskInfo();
    }

    private void MaskUndo()
    {
        if (_maskMat == null) return;
        if (_maskPoly.Count > 0) { _maskPoly.RemoveAt(_maskPoly.Count - 1); DrawShapes(); return; }
        if (_maskUndo.Count == 0) return;
        var prev = _maskUndo[^1]; _maskUndo.RemoveAt(_maskUndo.Count - 1);
        prev.CopyTo(_maskMat); prev.Dispose();
        _maskDirty = true; BtnSave.IsEnabled = true; BtnUndo.IsEnabled = _maskUndo.Count > 0;
        Redraw(); UpdateMaskInfo();
        TxtStatus.Text = "마스크를 되돌렸어요";
    }

    private void MaskTool_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _maskPoly.Clear(); DrawShapes();
    }

    private void BtnMReset_Click(object sender, RoutedEventArgs e)
    {
        if (_maskMat == null || _maskSc == null) return;
        using var auto = MaskSidecar.LoadAuto(_refs[_index].OutPath);
        MaskPushUndo();
        if (auto != null && auto.Size() == _maskMat.Size()) auto.CopyTo(_maskMat); else _maskMat.SetTo(Scalar.Black);
        MaskTouched(); Redraw(); UpdateMaskInfo();
        TxtStatus.Text = auto != null ? "자동 감지 결과로 되돌렸어요" : "자동 감지 사본이 없어 마스크를 비웠어요";
    }

    private void BtnMClear_Click(object sender, RoutedEventArgs e)
    {
        if (_maskMat == null) return;
        MaskPushUndo(); _maskMat.SetTo(Scalar.Black); _maskPoly.Clear();
        MaskTouched(); Redraw(); UpdateMaskInfo();
    }

    private void ChkMSkip_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _maskSc == null || _maskResetting) return;
        _maskDirty = true; BtnSave.IsEnabled = true;
        UpdateMaskInfo();
    }

    /// <summary>지금 마스크·건너뜀을 .neko 사이드카에 쓴다 (자동 감지 사본·원고 PNG는 그대로).</summary>
    private void SaveMaskSidecar()
    {
        if (_maskSc == null || _maskMat == null) return;
        if (_loading) return;   // 페이지를 넘기는 중 — 옛 장의 마스크를 새 장 사이드카에 쓰지 않는다 (넘기기 전 저장은 ConfirmDiscard가 먼저 한다)
        if (_maskPoly.Count >= 3) MaskClosePolygon();
        try
        {
            // Source는 창이 아는 원본 경로로 — 단 사이드카가 아직 존재하는 다른 경로의 원고 것이면(폴더를 복사해 러너가 빌려 쓰기만 한 마스크) 소유는 그대로 둔다
            var keepSource = !string.IsNullOrEmpty(_refs[_index].Source) && !_maskSc.SourceIs(WaitKey) && _maskSc.SrcLen > 0 && File.Exists(_maskSc.Source);
            _maskSc = MaskSidecar.Write(_refs[_index].OutPath, keepSource ? _maskSc.Source : WaitKey, _maskMat, null, null,
                _maskSc.Summary, ChkMSkip.IsChecked == true, _maskEdited || _maskSc.Edited, _maskSc.Confirmed);
            _maskDirty = false;
            BtnSave.IsEnabled = false;
            UpdateMaskInfo();
        }
        catch (Exception ex) { TxtStatus.Text = "마스크 저장 실패: " + ex.Message; }
    }

    /// <summary>[검열 제거 진행](대기 페이지) 또는 [저장 마스크로 다시 검열 제거](끝난 페이지).</summary>
    private async Task MaskApplyAsync()
    {
        if (_maskSc == null || _maskMat == null) return;
        SaveMaskSidecar();
        if (IsWaitingPage)
        {
            if (_confirmPage?.Invoke(WaitKey) == true)
            {
                TxtStatus.Text = "검열 제거를 시작했어요 — 메인 창 목록에서 진행을 확인하세요. 다음 장으로 넘겨 계속 검토할 수 있어요";
                UpdateMaskInfo(); UpdateModeButtons();
            }
            else TxtStatus.Text = "이 페이지는 지금 검토 대기 중이 아니에요";
            return;
        }
        if (ChkMSkip.IsChecked == true) { TxtStatus.Text = "'검열 없음'으로 표시된 페이지라 지울 곳이 없어요"; return; }
        int px = Cv2.CountNonZero(_maskMat);
        if (px == 0) { TxtStatus.Text = "마스크가 비어 있어요 — 지울 곳을 칠해 주세요"; return; }

        // 결과물에 바로 적용 (채색은 그대로) — 마스크는 원본 좌표라 결과물 크기로 맞춘다
        string engineMode = (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? "Basic";
        using var mask = new Mat();
        if (_maskMat.Size() == _img.Size()) _maskMat.CopyTo(mask);
        else Cv2.Resize(_maskMat, mask, _img.Size(), 0, 0, InterpolationFlags.Nearest);

        _busy = true;
        BtnApply.IsEnabled = false;
        TxtStatus.Text = $"{px:N0}px 복원 중...";
        var before = _img.Clone();
        var status = new Progress<string>(m => TxtStatus.Text = m);
        try
        {
            var img = _img;
            _runner ??= new PipelineRunner(App.Settings);
            var runner = _runner;
            var regenSettings = engineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : null;
            // 저장 마스크에는 자동 감지의 종류 지도(.kind.png)가 딸려 있다 — 기본 방식이 파이프라인과 같은 모델(막대/모자이크/보완)로 지우게 넘긴다
            using var kinds = engineMode == "Basic" ? MaskSidecar.LoadKinds(_refs[_index].OutPath) : null;
            await Task.Run(async () => await runner.ManualDecensorAsync(img, mask, engineMode, status, CancellationToken.None, regenSettings, kinds));
            _undo.Push(before);
            _dirty = true;
            InvalidateBitmap();
            try { _maskSc = _maskSc with { Confirmed = true }; _maskSc.Save(_refs[_index].OutPath); } catch { }
            TabMode.SelectedIndex = 0;   // 결과를 보여 준다 (마스크 탭은 제거 전 원고를 보여 주므로)
            TxtStatus.Text = $"저장된 마스크로 지웠어요 ({px:N0}px) — 마음에 들면 [저장]을 눌러주세요";
        }
        catch (Exception ex)
        {
            try { if (before.Size() == _img.Size()) { before.CopyTo(_img); InvalidateBitmap(); } } catch { }   // 반쯤 고친 그림을 남기지 않는다
            before.Dispose();
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally { _busy = false; BtnApply.IsEnabled = true; UpdateModeButtons(); }
    }

    /// <summary>[채색부터 다시]: 보존된 검열 제거 전 원고에 이 마스크를 적용해 채색·식자까지 다시 돌리고 결과물을 다시 읽는다.</summary>
    private async void BtnMRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_maskSc == null || _maskMat == null || _busy || _loading) return;
        if (!ConfirmDiscard()) return;
        SaveMaskSidecar();
        var source = WaitKey;
        _busy = true;
        BtnApply.IsEnabled = BtnMRedo.IsEnabled = false;
        BtnPrev.IsEnabled = BtnNext.IsEnabled = false;
        var progress = new Progress<PageProgress>(p => TxtStatus.Text = p.Error != null ? "실패: " + p.Error : p.Percent < 0 ? p.Stage : $"{p.Stage} ({p.Percent:0}%)");
        try
        {
            _runner ??= new PipelineRunner(App.Settings);
            var runner = _runner;
            var outPath = _refs[_index].OutPath;
            await Task.Run(() => runner.RedoPageAsync(source, progress, CancellationToken.None, outPath));
            _busy = false;
            _dirty = false;
            ChangedSources.Add(SourceOf(_refs[_index]));
            if (string.IsNullOrEmpty(_refs[_index].Source) && !string.Equals(Path.GetExtension(outPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                // --result 모드에서 jpg를 보고 있었다면 결과는 png로 새로 생겼다 — 그걸 보고 저장하게 (사이드카 이름은 같다)
                var png = Path.ChangeExtension(outPath, ".png");
                _refs[_index] = _refs[_index] with { OutPath = png };
                SetViewPath(_index, png);
            }
            InvalidatePrefetch(_index);
            _statusAfterLoad = "다시 돌렸어요 — 결과물이 갱신됐어요";
            LoadPage(_index, fit: false);   // 상태 줄은 LoadPage가 읽기를 마친 뒤 _statusAfterLoad로 바꾼다 — 여기서 덧쓰면 바로 지워진다
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnApply.IsEnabled = BtnMRedo.IsEnabled = true;   // 적용 자체는 _loading 중 거부되므로 켜 둬도 안전
            if (!_loading) { BtnPrev.IsEnabled = _index > 0; BtnNext.IsEnabled = _index < _refs.Count - 1; }   // 다시 읽는 중이면 LoadPage가 끝날 때 켠다
        }
    }
}
