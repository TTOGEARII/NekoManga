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

// 검열 지우기 탭 — 검열 마스크를 고쳐 저장하고, 검토 대기면 진행을 알리고, 끝난 장은 이 장만 다시 렌더링(검열 제거 + 식자, 채색 없음)
public partial class ResultWindow
{
    // ── 🙈 검열 지우기 탭 = 옛 '마스크 수정'(2026-10-21) + '검열 지우기'를 합친 것 (2026-11-11, 사용자 요청) ───────────
    // 검토 대기 페이지: 러너가 .neko에 남긴 마스크(원본 좌표)와 검열 제거 전 원고를 보여 주고, 고친 마스크를 사이드카에 저장한 뒤 러너에 '진행'을 알린다.
    // 끝난 페이지: 같은 마스크를 고쳐 [지우기] — 이 장만 검열 제거·식자를 다시 한다(RedoPageAsync, 채색은 돌리지 않는다 — 사용자 결정).
    // 저장 마스크가 없는 장(--result로 연 파일, 검열 제거를 끄고 돌린 장): 결과물 위 빈 마스크에 칠해 결과물 제자리에 지운다. 사이드카는 만들지 않는다.
    private MaskSidecar? _maskSc;
    private Mat? _maskMat;        // 원본 크기 CV_8UC1, 255=지울 곳 (창이 소유)
    private Mat? _maskPre;        // 검열 제거 전 원고 (원본 크기)
    private Mat? _maskPreDisp;    // 표시용 축소본
    private readonly List<Mat> _maskUndo = [];
    private readonly List<CvPoint> _maskPoly = [];
    private bool _maskDirty, _maskEdited, _maskPainting;
    private bool _maskResetting;   // 페이지를 읽어 체크박스를 맞추는 중 — 이때 Checked/Unchecked는 사용자 수정이 아니다
    private CvPoint _maskLast;

    /// <summary>검열 지우기 탭 + 마스크 보기 — 칠하기·다각형을 받는 상태. 결과 보기(ResultView)는 결과물을 보기만 한다.</summary>
    private bool MaskMode => TabMode.SelectedItem == TabDecensor && RbViewMask.IsChecked == true;
    private bool ResultView => TabMode.SelectedItem == TabDecensor && RbViewResult.IsChecked == true;
    /// <summary>저장 마스크(사이드카 + .pre.png)가 있는 장 — [지우기]가 이 장만 다시 렌더링한다. 없으면 결과물 제자리에 지운다.</summary>
    private bool HasStoredMask => _maskSc != null;
    /// <summary>여유(px) — 지울 때 마스크를 부풀리는 양. 기본 0 = 저장 마스크 그대로(파이프라인과 같은 결과).</summary>
    private int GrowPx => int.TryParse(TxtGrow.Text.Trim(), out var g) ? Math.Clamp(g, 0, 40) : 0;
    private enum MaskTone { Dark, White, All }
    /// <summary>지울 대상 — 다각형을 닫을 때 안쪽에서 그 톤의 픽셀만 고른다(브러시는 늘 전체).</summary>
    private MaskTone CurrentTone => RbDarkOnly.IsChecked == true ? MaskTone.Dark : RbWhiteOnly.IsChecked == true ? MaskTone.White : MaskTone.All;
    /// <summary>러너가 대기·확인에 쓰는 키 — 창이 아는 원본 경로. 사이드카 안의 Source는 폴더를 옮긴 뒤 경로 갱신 저장이 실패하면 옛 경로라 못 믿는다 (--result 모드만 사이드카 값)</summary>
    private string WaitKey => !string.IsNullOrEmpty(_refs[_index].Source) ? _refs[_index].Source : _maskSc?.Source ?? "";
    private bool IsWaitingPage => _maskSc != null && _isWaiting?.Invoke(WaitKey) == true;

    private void ResetMaskState(PageData? data)
    {
        _maskMat?.Dispose(); _maskPre?.Dispose(); _maskPreDisp?.Dispose();
        foreach (var u in _maskUndo) u.Dispose();
        _maskUndo.Clear(); _maskPoly.Clear();
        _maskDirty = _maskEdited = _maskPainting = false;
        ClearRegionPreview(redraw: false); _regionParts = null; _regionPartsKey = "";
        _maskSc = data?.MaskSc; _maskMat = data?.Mask; _maskPre = data?.Pre; _maskPreDisp = data?.PreDisp;
        if (data != null && _maskMat == null && !data.Img.Empty())
        {
            // 저장 마스크가 없는 장 — 결과물 위에 빈 마스크를 만들어 같은 도구로 칠한다 (결과물 제자리에 지운다, 사이드카는 안 만든다)
            _maskMat = new Mat(data.Img.Size(), MatType.CV_8UC1, Scalar.Black);
            _maskPre = data.Img.Clone();
            double ds = Math.Min(1.0, (double)MaskDispMax / Math.Max(data.Img.Width, data.Img.Height));
            _maskPreDisp = new Mat();
            Cv2.Resize(_maskPre, _maskPreDisp, new Size((int)Math.Round(_maskPre.Width * ds), (int)Math.Round(_maskPre.Height * ds)), 0, 0, InterpolationFlags.Area);
        }
        // 체크박스를 사이드카 값으로 맞추는 것은 수정이 아니다 — 가드 없이 바꾸면 ChkMSkip_Changed가 _maskDirty를 켜서
        // 손도 안 댄 페이지를 넘길 때마다 "고친 마스크를 저장할까요?"를 물었다
        if (data != null)
        {
            _maskResetting = true;
            try { ChkMSkip.IsChecked = data.MaskSc?.Skip == true; }
            finally { _maskResetting = false; }
        }
    }

    /// <summary>저장 마스크가 없는 장: 마스크 보기의 바닥 그림은 결과물 사본이다 — 결과물이 바뀌면(지움·되돌리기·다시 식자) 다시 뜬다(검수 I-2:
    /// 안 그러면 지운 뒤 마스크 보기로 돌아왔을 때 지우기 전 그림이 보이고, '검은 부분만' 다각형이 옛 그림의 검은 픽셀을 고른다).</summary>
    private void RefreshManualMaskBase()
    {
        if (HasStoredMask || _img.Empty() || _maskMat == null) return;
        _maskPre?.Dispose(); _maskPreDisp?.Dispose();
        _maskPre = _img.Clone();
        double ds = Math.Min(1.0, (double)MaskDispMax / Math.Max(_maskPre.Width, _maskPre.Height));
        _maskPreDisp = new Mat();
        Cv2.Resize(_maskPre, _maskPreDisp, new Size((int)Math.Round(_maskPre.Width * ds), (int)Math.Round(_maskPre.Height * ds)), 0, 0, InterpolationFlags.Area);
        if (_maskMat.Size() != _maskPre.Size())
        {
            _maskMat.Dispose(); _maskMat = new Mat(_maskPre.Size(), MatType.CV_8UC1, Scalar.Black);
            foreach (var u in _maskUndo) u.Dispose();
            _maskUndo.Clear();
        }
        _regionParts = null; _regionPartsKey = "";   // 그림이 바뀌었다 — 부위 상자도 다시 센다
        RefreshRegionPreview();
    }

    // ── 다시 그려질 부위 점선 미리보기 (재생성·커스텀 모델일 때, 2026-11-12) ──
    // 설정의 '부위 통째 다시 그리기'가 켜져 있고 모델이 재생성/커스텀이면 실제 다시 그려질 범위를 마스크 보기에 주황 점선으로 겹친다.
    // 계산은 둘로 나눈다(검수 Critical·Important 뒤 재설계): ① 부위 검출은 페이지(+pre/base 선택)마다 한 번, **그림 사본**으로 백그라운드에서 돌려 상자를 캐시한다
    // — 창의 _maskPre를 백그라운드에 빌려주면 페이지 넘김·되돌리기·닫기가 그 Mat을 해제해 프로세스가 죽는다(세대 번호는 결과를 버릴 뿐 계산을 못 멈춘다).
    // ② 마스크를 칠할 때마다 300ms 뒤 RegenRegion.Build(순수 함수)만 다시 한다. 실제 2차와 같게 여유(GrowPx)를 더하고, 채색된 장은 base 그림에서 검출한다(Core가 그렇게 돈다).
    // 러너가 돌고 있는 동안은 _previewInFlight로 닫기 때의 해제를 미룬다(_busy와 같은 규칙, DisposeRunnerIfClosed).
    private Mat? _regionPreview;
    private List<WinPoint[]>? _regionContours;     // 점선 폴리곤(원본 좌표) — _regionPreview가 바뀔 때만 다시 센다(Redraw마다 FindContours를 돌리지 않는다)
    private IReadOnlyList<OpenCvSharp.Rect>? _regionParts;    // 페이지별 부위 상자 캐시(마스크 보기 그림 좌표)
    private string _regionPartsKey = "";
    private int _regionPreviewGen;
    private int _previewInFlight;
    private bool RegionPreviewWanted => MaskMode && EngineMode is "ComfyRegen" or "ComfyCustom" && App.Settings.RegenWholeRegion && _maskMat != null && _maskPre != null;

    private void ClearRegionPreview(bool redraw)
    {
        ++_regionPreviewGen;
        _regionPreview?.Dispose(); _regionPreview = null; _regionContours = null;
        if (redraw && !_closed) DrawShapes();
    }

    private async void RefreshRegionPreview()
    {
        int gen = ++_regionPreviewGen;
        if (!RegionPreviewWanted) { if (_regionPreview != null) ClearRegionPreview(redraw: true); return; }
        await Task.Delay(300);
        if (gen != _regionPreviewGen || !RegionPreviewWanted || _closed) return;
        var settings = EngineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : App.Settings;

        // ① 부위 상자 — 페이지·그림(pre/base)마다 한 번. 채색된 장은 2차가 base에서 돌므로 base에서 검출하고 상자를 pre 좌표로 환산한다
        bool useBase = HasStoredMask && _baseImg != null && PipelineRunner.LooksColorized(_maskPre!, _baseImg, _maskMat);
        string key = $"{_index}|{(useBase ? "base" : "pre")}";
        if (_regionParts == null || _regionPartsKey != key)
        {
            var srcImg = useBase ? _baseImg! : _maskPre!;
            using var imgCopy = srcImg.Clone();   // UI 스레드에서 사본 — 백그라운드는 창의 Mat을 만지지 않는다
            double sx = (double)_maskPre!.Width / imgCopy.Width, sy = (double)_maskPre.Height / imgCopy.Height;
            IReadOnlyList<OpenCvSharp.Rect> parts;
            _previewInFlight++;
            try
            {
                _runner ??= new PipelineRunner(App.Settings);
                var runner = _runner;
                parts = await Task.Run(() => runner.DetectBodyParts(imgCopy, settings));
            }
            catch { parts = []; }   // 미리보기 실패는 조용히 — 지우기 자체는 영향 없다
            finally { _previewInFlight--; DisposeRunnerIfClosed(); }
            if (_closed) return;
            _regionParts = parts.Select(r => new OpenCvSharp.Rect((int)Math.Round(r.X * sx), (int)Math.Round(r.Y * sy), (int)Math.Round(r.Width * sx), (int)Math.Round(r.Height * sy))).ToList();
            _regionPartsKey = key;
            if (gen != _regionPreviewGen || !RegionPreviewWanted) return;   // 기다리는 사이 또 바뀌었다 — 최신 호출이 ②를 한다
        }

        // ② 영역 — 순수 함수, 마스크 사본으로 (여유 px까지 실제 2차와 같게)
        using var maskCopy = PipelineRunner.Grown(_maskMat!, GrowPx);
        var parts2 = _regionParts;
        double pad = settings.RegenRegionPad, grow = settings.RegenRegionGrow;
        var region = await Task.Run(() => NekoManga.Core.Decensor.RegenRegion.Build(maskCopy, parts2, pad, grow));
        if (gen != _regionPreviewGen || _closed) { region.Dispose(); return; }
        _regionPreview?.Dispose();
        _regionPreview = region;
        using (var copy = region.Clone())   // FindContours는 입력을 망가뜨린다
        {
            Cv2.FindContours(copy, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            _regionContours = contours.Where(c => c.Length >= 3).Select(c => c.Select(p => new WinPoint(p.X, p.Y)).ToArray()).ToList();
        }
        DrawShapes();
    }

    private void UpdateMaskInfo()
    {
        if (_maskMat == null) { TxtMaskInfo.Text = ""; return; }
        int px = Cv2.CountNonZero(_maskMat);
        if (_maskSc == null)
        {
            TxtMaskInfo.Text = $"저장된 마스크가 없는 장 — 결과물 위에 칠해 바로 지워요 (사이드카는 안 생겨요) · {px:N0}px";
            return;
        }
        bool waiting = IsWaitingPage;
        TxtMaskInfo.Text = (waiting ? "검토 대기 중 — 고친 뒤 [검열 제거 진행]" : _maskSc.Confirmed ? "제거에 쓴 마스크" : "저장된 마스크")
                         + $" · {px:N0}px" + (_maskEdited || _maskSc.Edited ? " · 수정됨" : "") + (ChkMSkip.IsChecked == true ? " · 검열 없음 표시" : "")
                         + (RegionPreviewWanted ? " · 점선 = 다시 그려질 부위" : "");
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
        _maskEdited = true;
        _maskDirty = HasStoredMask;   // 저장 마스크가 없는 장은 사이드카가 없다 — "마스크를 저장할까요?"를 묻지 않는다(검수 I-1)
        BtnSave.IsEnabled = HasStoredMask; BtnUndo.IsEnabled = true;
        RefreshRegionPreview();
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

    /// <summary>다각형을 닫아 마스크에 더한다(지우개면 뺀다). '지울 대상'이 검은/흰 부분만이면 안쪽에서 그 톤의 픽셀만 —
    /// 막대를 넉넉히 감싸도 주변 그림이 같이 뭉개지지 않는다(임계 120/200은 옛 검열 지우기 탭 BuildMask의 값: 막대는 거의 순흑/순백이지만 스캔본은 들쑥날쑥하다).</summary>
    private void MaskClosePolygon()
    {
        if (_maskMat == null || _maskPre == null || _maskPoly.Count < 3) { _maskPoly.Clear(); DrawShapes(); return; }
        MaskPushUndo();
        using var area = new Mat(_maskMat.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(area, [_maskPoly.ToArray()], Scalar.White);
        var tone = CurrentTone;
        if (tone != MaskTone.All)
        {
            using var gray = new Mat();
            Cv2.CvtColor(_maskPre, gray, ColorConversionCodes.BGR2GRAY);
            using var picked = new Mat();
            if (tone == MaskTone.Dark) Cv2.Threshold(gray, picked, 120, 255, ThresholdTypes.BinaryInv);
            else Cv2.Threshold(gray, picked, 200, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(area, picked, area);
        }
        if (RbMErase.IsChecked == true) _maskMat.SetTo(Scalar.Black, area); else _maskMat.SetTo(Scalar.White, area);
        int added = Cv2.CountNonZero(area);
        _maskPoly.Clear();
        MaskTouched();
        Redraw(); UpdateMaskInfo();
        if (added == 0 && tone != MaskTone.All)
            TxtStatus.Text = tone == MaskTone.Dark ? "다각형 안에서 검은 부분을 찾지 못했어요 — [흰 부분만]이나 [도형 전체]로 바꿔 보세요"
                                                   : "다각형 안에서 흰 부분을 찾지 못했어요 — [도형 전체]로 바꿔 보세요";
    }

    private void MaskUndo()
    {
        if (_maskMat == null) return;
        if (_maskPoly.Count > 0) { _maskPoly.RemoveAt(_maskPoly.Count - 1); DrawShapes(); return; }
        if (_maskUndo.Count == 0) return;
        var prev = _maskUndo[^1]; _maskUndo.RemoveAt(_maskUndo.Count - 1);
        prev.CopyTo(_maskMat); prev.Dispose();
        _maskDirty = HasStoredMask; BtnSave.IsEnabled = HasStoredMask; BtnUndo.IsEnabled = _maskUndo.Count > 0;
        Redraw(); UpdateMaskInfo(); RefreshRegionPreview();
        TxtStatus.Text = "마스크를 되돌렸어요";
    }

    private void MaskTool_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _maskPoly.Clear(); DrawShapes();
    }

    /// <summary>마스크 보기 ↔ 결과 보기 — 그림 크기가 다를 수 있어(마스크는 원본 크기, 결과물은 채색으로 달라졌을 수 있다) 맞춤부터.</summary>
    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading || _img.Empty()) return;
        _maskPoly.Clear();
        FitToWindow();
        Redraw();
        UpdateModeButtons();
        RefreshRegionPreview();
        TxtStatus.Text = MaskMode ? "마스크 보기 — 빨간 곳이 지워져요" : "결과 보기 — 지금 결과물이에요";
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
        _maskDirty = true; BtnSave.IsEnabled = true;   // 저장 마스크가 없는 장은 ChkMSkip이 사이드카에 갈 데가 없다 — 위에서 걸러진다
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

    /// <summary>[✨ 지우기] / [✔ 검열 제거 진행] — 검열 지우기 탭의 주 버튼.
    /// 검토 대기 장: 마스크 저장 → 러너에 진행 알림. 저장 마스크가 있는 끝난 장: 이 장만 다시 렌더링(RedoThisPageAsync).
    /// 저장 마스크가 없는 장: 결과물 제자리에 지운다(ManualOnResultAsync).</summary>
    private async Task DecensorApplyAsync()
    {
        if (_maskMat == null) return;
        if (_maskPoly.Count >= 3) MaskClosePolygon();   // 찍다 만 다각형도 살려 준다
        if (IsWaitingPage)
        {
            SaveMaskSidecar();
            if (_confirmPage?.Invoke(WaitKey) == true)
            {
                TxtStatus.Text = "검열 제거를 시작했어요 — 메인 창 목록에서 진행을 확인하세요. 다음 장으로 넘겨 계속 검토할 수 있어요";
                UpdateMaskInfo(); UpdateModeButtons();
            }
            else TxtStatus.Text = "이 페이지는 지금 검토 대기 중이 아니에요";
            return;
        }
        if (HasStoredMask) await RedoThisPageAsync();
        else await ManualOnResultAsync();
    }

    private string EngineMode => (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? "Basic";

    /// <summary>[지우기]용 설정 사본 — 채색은 끄고(사용자 결정: 수정 창은 채색을 돌리지 않는다), 검열 제거는 이 탭에서 고른 모델로.
    /// 러너는 만들 때의 설정을 박제하므로 _runner(전역 설정, 결과물 제자리 지우기용)와 따로 둔다.</summary>
    private AppSettings RedoSettings()
    {
        var c = EngineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : App.Settings.ShallowClone();
        c.EnableAutoColor = false;
        c.EnableDecensor = true;
        c.DecensorMode = EngineMode;
        return c;
    }

    // [지우기] 러너는 모델·재생성 값이 같으면 재사용한다 — 매번 만들면 복원 모델(LaMa 211MB)을 다시 읽어 수 초씩 늦고, Comfy 계열이면 Dispose가
    // ComfyUI 사용을 놓아 KeepComfyWarm=false인 사용자의 서버가 꺼졌다 다시 뜬다(검수 I-3). 키가 바뀌거나 창이 닫히면 놓는다.
    private PipelineRunner? _redoRunner;
    private string _redoKey = "";

    private PipelineRunner RedoRunner()
    {
        var key = EngineMode + "|" + (EngineMode == "ComfyRegen" ? ReadRegenFields().ToString() : "");
        if (_redoRunner != null && key == _redoKey) return _redoRunner;
        _redoRunner?.Dispose();
        _redoRunner = new PipelineRunner(RedoSettings());
        _redoKey = key;
        return _redoRunner;
    }

    /// <summary>저장 마스크가 있는 장: 마스크(와 식자 편집)를 저장하고 이 장만 검열 제거 → 식자를 다시 돌려 결과물을 갱신한다. 채색은 하지 않는다 —
    /// 채색된 장은 Core가 base(채색 뒤·식자 전)에서 지운다(RedoPageOptions.DecensorAndLetterOnly). 돌아가는 동안 오버레이, [취소]는 단계 경계에서.</summary>
    private async Task RedoThisPageAsync()
    {
        if (_maskSc == null || _maskMat == null || _busy || _loading) return;
        if (ChkMSkip.IsChecked != true && Cv2.CountNonZero(_maskMat) == 0) { TxtStatus.Text = "마스크가 비어 있어요 — 지울 곳을 칠하거나 '검열 없음'으로 표시해 주세요"; return; }
        SaveMaskSidecar();                           // 먼저 — [지우기]는 이 마스크를 쓰라는 뜻이라 "고친 마스크를 저장할까요?"를 묻지 않는다(하네스에서 창이 멈춰 잡힘)
        if (_sidecar != null) SaveLetterSidecar();   // 식자 탭에서 고친 글·숨김이 다시 렌더링 뒤에도 남게
        if (!ConfirmDiscard()) return;               // 결과물에 저장 안 한 손 편집만 묻는다(다시 렌더링이 덮는다)
        var source = WaitKey;
        var outPath = _refs[_index].OutPath;
        var options = new PipelineRunner.RedoPageOptions(DecensorAndLetterOnly: true, Grow: GrowPx);
        _busy = true;
        _redoCts?.Dispose();
        _redoCts = new CancellationTokenSource();
        var ct = _redoCts.Token;
        ShowLoading(true, "준비 중");
        var progress = new Progress<PageProgress>(p => ShowLoading(true, p.Error != null ? "실패: " + p.Error : p.Stage, p.Percent));
        try
        {
            var runner = RedoRunner();
            await Task.Run(() => runner.RedoPageAsync(source, progress, ct, outPath, options), CancellationToken.None);
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
            RbViewResult.IsChecked = true;   // 끝나면 결과 보기
            _statusAfterLoad = "다시 렌더링했어요 — 결과물이 갱신됐어요 (검열 제거 + 식자, 채색은 그대로)";
            LoadPage(_index, fit: false);   // 상태 줄은 LoadPage가 읽기를 마친 뒤 _statusAfterLoad로 바꾼다
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TxtStatus.Text = "취소했어요 — 결과물은 그대로예요";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally
        {
            _busy = false;
            ShowLoading(false);
            UpdateModeButtons();
            DisposeRunnerIfClosed();   // 창이 닫혔으면 [지우기] 러너도 여기서 놓는다
        }
    }

    /// <summary>저장 마스크가 없는 장: 결과물 제자리에 마스크 자리를 지운다(옛 검열 지우기 탭의 길). 끝나면 [저장]으로 결과물에 쓴다.</summary>
    private async Task ManualOnResultAsync()
    {
        if (_maskMat == null || _busy || _loading) return;
        int px = Cv2.CountNonZero(_maskMat);
        if (px == 0) { TxtStatus.Text = "마스크가 비어 있어요 — 지울 곳을 칠해 주세요"; return; }
        string engineMode = EngineMode;
        using var mask = PipelineRunner.Grown(_maskMat, GrowPx);
        if (mask.Size() != _img.Size()) Cv2.Resize(mask, mask, _img.Size(), 0, 0, InterpolationFlags.Nearest);   // 마스크는 결과물 사본 위에 칠했으니 보통 같은 크기

        _busy = true;
        _redoCts?.Dispose();
        _redoCts = new CancellationTokenSource();
        var ct = _redoCts.Token;
        ShowLoading(true, $"{px:N0}px 지우는 중...");
        var before = _img.Clone();
        var status = new Progress<string>(m => ShowLoading(true, m));
        try
        {
            var img = _img;
            _runner ??= new PipelineRunner(App.Settings);   // 러너(ONNX 세션 포함)는 창 수명 동안 재사용 — 매번 만들면 모델을 다시 읽는다
            var runner = _runner;
            var regenSettings = engineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : null;
            await Task.Run(async () => await runner.ManualDecensorAsync(img, mask, engineMode, status, ct, regenSettings), CancellationToken.None);
            ct.ThrowIfCancellationRequested();   // 기본 모델(Aletheia/LaMa)은 ct를 보지 않고 끝까지 돈다 — 그 사이 [취소]를 눌렀으면 결과를 버린다(아래 catch가 before로 되돌린다)
            _undo.Push(before);
            _dirty = true;
            InvalidateBitmap();
            MaskPushUndo(); _maskMat.SetTo(Scalar.Black); _maskDirty = false;   // 지운 자리는 결과물에 들어갔다 — 마스크는 비운다(되돌릴 수 있게 undo에)
            RefreshManualMaskBase();
            RbViewResult.IsChecked = true;
            TxtStatus.Text = $"지웠어요 ({px:N0}px) — 마음에 들면 [저장]을 눌러주세요";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (before.Size() == _img.Size()) { before.CopyTo(_img); InvalidateBitmap(); } } catch { }
            before.Dispose();
            TxtStatus.Text = "취소했어요 — 결과물은 그대로예요";
        }
        catch (Exception ex)
        {
            try { if (before.Size() == _img.Size()) { before.CopyTo(_img); InvalidateBitmap(); } } catch { }   // 반쯤 고친 그림을 남기지 않는다
            before.Dispose();
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally { _busy = false; ShowLoading(false); Redraw(); UpdateModeButtons(); DisposeRunnerIfClosed(); }
    }

}
