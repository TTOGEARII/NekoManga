using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoManga.Core;
using NekoManga.Core.Decensor;
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

// 검열 지우기 탭 — 검열 마스크를 고쳐 저장하고, 검토 대기면 진행을 알리고, 끝난 장은 이 장만 다시 렌더링(검열 제거 + 식자, 채색 없음).
// 여러 장을 고쳐 저장해 두면 [고친 장 모두 다시 렌더링]이 메인 창에 묶음을 맡긴다
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
    // 읽어 온(또는 마지막으로 저장한) 마스크와 '검열 없음' 값 — 만졌지만 달라진 것이 없는 장(칠했다 되돌림, 체크를 켰다 끔, 아무것도 못 고른 다각형)을 고친 장으로 치지 않으려고 든다.
    // 장을 넘길 때 묻지 않고 저장하게 된 뒤로(2026-11-13) 그런 장에도 ✎가 붙어 묶음이 다시 돌렸다 — 재생성이면 마음에 들던 결과가 새 생성으로 바뀐다(점검 5번)
    private Mat? _maskBaseline;
    private bool _skipBaseline;
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
        ResetSexState(_maskSc);   // 조각의 성별 표시도 이 장의 것으로
        _maskBaseline?.Dispose();
        _maskBaseline = _maskSc != null && _maskMat != null ? _maskMat.Clone() : null;   // 저장 마스크가 있는 장만 — 없는 장은 사이드카에 쓸 것이 없다
        _skipBaseline = _maskSc?.Skip == true;
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
    private IReadOnlyList<BodyPart>? _regionParts;    // 페이지별 부위 상자 캐시(마스크 보기 그림 좌표) — 약한 상자까지: 점선은 임계 이상만 쓴다
    private IReadOnlyList<BodyPart>? _regionPartsRun; // 같은 상자를 2차가 그리는 그림(RunImage)의 좌표 그대로 — 조각의 성별 표시가 2차와 같은 영역 계획을 세우는 데 쓴다
    private Size _regionRunSize;                      // 그 그림의 크기(_regionParts와 같이 채운다)
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

    /// <summary>2차(이 장의 다시 렌더링)가 검열 제거를 돌리는 그림 — 채색된 장은 채색 뒤 그림(base, 글자 없는 장은 결과물), 아니면 검열 제거 전 원고.
    /// 채색된 장인지는 Core의 다시 렌더링과 같은 판정으로(PipelineRunner.IsColorized — 사이드카에 적힌 값, 없으면 그림의 채도). 미리보기의 부위 감지와 조각 세기가 이 그림의 크기에서 돈다.</summary>
    private (Mat Img, bool IsBase) RunImage()
    {
        var colored = _baseImg ?? (_sidecar == null && !_img.Empty() && !File.Exists(LetterSidecar.JsonPathFor(_refs[_index].OutPath)) ? _img : null);
        bool useBase = HasStoredMask && colored != null && PipelineRunner.IsColorized(_maskSc, _maskPre!, colored, _maskMat);
        return (useBase ? colored! : _maskPre!, useBase);
    }

    private async void RefreshRegionPreview()
    {
        int gen = ++_regionPreviewGen;
        // 점선(다시 그려질 부위)과 조각의 성별 표시(ResultWindow.Sex.cs)는 같은 부위 상자로 같이 센다 — 둘 다 필요 없으면 지운다
        if (!RegionPreviewWanted && !SexBadgesWanted)
        {
            if (_regionPreview != null) ClearRegionPreview(redraw: true);
            if (_sexPieces != null || _sexTooMany > 0) { ClearSexPieces(); UpdateMaskInfo(); }
            return;
        }
        // 장을 읽는 중에는 세지 않는다 — _index는 이미 새 장인데 그림·마스크는 아직 떠난 장의 것이라, 떠난 장의 부위 상자가 새 장의 캐시(키 = 장 번호)로 들어가
        // 그 장을 보는 내내 엉뚱한 상자로 점선과 성별 표시를 그렸다(검수 2026-11-15). 읽기가 끝나면 LoadPage가 다시 부른다
        if (_loading) return;
        await Task.Delay(300);
        if (gen != _regionPreviewGen || _loading || !(RegionPreviewWanted || SexBadgesWanted) || _closed) return;
        var settings = EngineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : App.Settings;
        var page = _maskPre!;   // 이 호출이 보는 장(장을 읽을 때마다 새 Mat이다) — 기다리는 사이 장이 바뀌었는지 가린다

        // ① 부위 상자 — 페이지·그림(pre/base)마다 한 번. 채색된 장은 2차가 채색 뒤 그림(base, 글자 없는 장은 결과물)에서 돌므로 거기서 검출하고 상자를 pre 좌표로 환산한다.
        var (runImg, useBase) = RunImage();
        string key = $"{_index}|{(useBase ? "base" : "pre")}";
        if (_regionParts == null || _regionPartsKey != key)
        {
            using var imgCopy = runImg.Clone();   // UI 스레드에서 사본 — 백그라운드는 창의 Mat을 만지지 않는다
            double sx = (double)_maskPre!.Width / imgCopy.Width, sy = (double)_maskPre.Height / imgCopy.Height;
            IReadOnlyList<BodyPart> parts;
            _previewInFlight++;
            try
            {
                _runner ??= new PipelineRunner(App.Settings);
                var runner = _runner;
                parts = await Task.Run(() => runner.DetectRegenParts(imgCopy, settings));   // 2차의 영역 계획이 보는 것과 같은 상자(약한 것까지, '부위 통째'가 꺼져 있으면 빈 목록)
            }
            catch { parts = []; }   // 미리보기 실패는 조용히 — 지우기 자체는 영향 없다
            finally { _previewInFlight--; DisposeRunnerIfClosed(); }
            if (_closed) return;
            if (_loading || !ReferenceEquals(_maskPre, page)) return;   // 기다리는 사이 장이 바뀌었다 — 떠난 장의 상자를 새 장의 캐시에 쓰지 않는다
            _regionPartsRun = parts; _regionRunSize = imgCopy.Size();
            _regionParts = parts.Select(p => p with { Box = new OpenCvSharp.Rect((int)Math.Round(p.Box.X * sx), (int)Math.Round(p.Box.Y * sy), (int)Math.Round(p.Box.Width * sx), (int)Math.Round(p.Box.Height * sy)) }).ToList();
            _regionPartsKey = key;
            if (gen != _regionPreviewGen || !(RegionPreviewWanted || SexBadgesWanted)) return;   // 기다리는 사이 또 바뀌었다 — 최신 호출이 ②를 한다
        }

        // ② 영역·조각 — 순수 함수, 마스크 사본으로.
        // 점선은 실제로 다시 그려질 범위라 여유(px)까지 넣은 마스크로, 조각의 성별은 **저장되는 마스크 그대로**로 센다: 고른 성별은 그 조각 안의 점으로 기억하는데
        // 묶음·[시작]은 여유 없이 돌아, 여유로 이어 붙은 조각의 가운데(빈 틈)에 찍힌 점은 어느 조각의 것도 아니게 된다(검수)
        using var maskGrown = PipelineRunner.Grown(_maskMat!, GrowPx);
        using var maskPlain = _maskMat!.Clone();
        var partsPre = _regionParts; var partsRun = _regionPartsRun ?? []; var runSize = _regionRunSize;
        double pad = settings.RegenRegionPad, grow = settings.RegenRegionGrow, conf = settings.RegenRegionConf;
        int cropSize = settings.RegenCropSize;
        bool whole = settings.RegenWholeRegion, wantRegion = RegionPreviewWanted, wantSex = SexBadgesWanted;
        var marks = _sexMarks.ToList();
        var (region, pieces, pieceCount) = await Task.Run(() =>
        {
            // 실패(메모리 부족 등)는 조용히 — 표시 없이 간다. 여기서 던지면 async void 밖으로 나가 전역 처리기까지 간다
            Mat? rg = null; List<(RegenPiece Piece, bool Mixed)>? ps = null; int count = 0;
            try
            {
                // 점선은 임계 이상의 상자로만(전과 같다 — 점수순 NMS라 낮은 임계로 받아도 임계 이상의 상자는 그대로다)
                if (wantRegion) rg = RegenRegion.Build(maskGrown, partsPre.Where(p => p.Conf >= (float)conf).Select(p => p.Box).ToList(), pad, grow);
            }
            catch { rg = null; }
            try
            {
                if (wantSex)
                {
                    count = RegenRegion.PieceAnchors(maskPlain).Count;
                    if (count <= SexBadgeMaxPieces) ps = SexPlan(maskPlain, partsRun, runSize, pad, whole ? grow : 0, cropSize, conf, marks);
                }
            }
            catch { ps = null; count = 0; }
            return (rg, ps, count);
        });
        if (gen != _regionPreviewGen || _closed) { region?.Dispose(); return; }
        _regionPreview?.Dispose();
        _regionPreview = region; _regionContours = null;
        if (region != null)
        {
            using var copy = region.Clone();   // FindContours는 입력을 망가뜨린다
            Cv2.FindContours(copy, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            _regionContours = contours.Where(c => c.Length >= 3).Select(c => c.Select(p => new WinPoint(p.X, p.Y)).ToArray()).ToList();
        }
        _sexPieces = pieces; _sexTooMany = wantSex && pieces == null ? pieceCount : 0;
        DrawShapes();
        DrawSexBadges();
        UpdateMaskInfo();
    }

    private void UpdateMaskInfo()
    {
        if (_maskMat == null) { TxtMaskInfo.Text = ""; return; }
        int px = Cv2.CountNonZero(_maskMat);
        if (_maskSc == null)
        {
            TxtMaskInfo.Text = $"저장된 마스크가 없는 장 — 결과물 위에 칠해 바로 지워요 (사이드카는 안 생겨요) · {px:N0}px" + SexInfo();
            return;
        }
        bool waiting = IsWaitingPage;
        TxtMaskInfo.Text = (waiting ? "검토 대기 중 — 고친 뒤 [검열 제거 시작]" : _maskSc.Pending ? "고친 마스크 — 아직 결과물에 반영 전" : _maskSc.Confirmed ? "제거에 쓴 마스크" : "저장된 마스크")
                         + $" · {px:N0}px" + (_maskEdited || _maskSc.Edited ? " · 수정됨" : "") + (ChkMSkip.IsChecked == true ? " · 검열 없음 표시" : "")
                         + (RegionPreviewWanted ? " · 점선 = 다시 그려질 부위" : "") + SexInfo();
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
        UpdateRerenderButton();
        RefreshRegionPreview();
    }

    /// <summary>만졌지만(<see cref="_maskDirty"/>) 읽어 온 것과 달라진 것이 없으면 고친 것으로 치지 않는다 — 되돌리기·체크·붓질 한 획·다각형 닫기 뒤와, 장을 떠나기 전에 부른다.</summary>
    private void SettleMaskDirty()
    {
        if (!_maskDirty || _maskMat == null || _maskBaseline == null || _maskBaseline.Size() != _maskMat.Size()) return;
        if ((ChkMSkip.IsChecked == true) != _skipBaseline) return;
        if (SexDirty) return;   // 조각의 성별을 바꿨다 — 마스크가 같아도 고친 장이다
        using var diff = new Mat();
        Cv2.BitwiseXor(_maskMat, _maskBaseline, diff);
        if (Cv2.CountNonZero(diff) != 0) return;
        _maskDirty = false; _maskEdited = false;
        if (MaskMode) BtnSave.IsEnabled = false;
        UpdateRerenderButton();
        UpdateMaskInfo();
    }

    /// <summary>장을 떠나기 전(넘김·닫기·묶음 버튼): 찍다 만 다각형(3점 이상)은 닫아서 반영하고 — 저장할 때는 닫아 주면서, 다각형만 있는 장은 고친 것이 없다고 보고 버렸다(점검 17번) —
    /// 그 뒤 달라진 것이 없으면 더티를 끈다. 저장 마스크가 없는 장은 마스크가 사이드카로 가지 않아 할 일이 없다.</summary>
    private void SettleMaskBeforeLeaving()
    {
        if (!HasStoredMask || _maskMat == null) return;
        if (_maskPoly.Count >= 3) MaskClosePolygon();
        SettleMaskDirty();
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
        SettleMaskDirty();   // 아무것도 못 고른 다각형·이미 칠해진 자리만 덮은 다각형은 고친 것이 아니다
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
        SettleMaskDirty();   // 읽어 온 마스크까지 되돌렸으면 고친 것이 없다
        UpdateRerenderButton();
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
        Mat? auto;
        // 자동 감지 사본을 다른 프로그램이 쥐고 있으면(백신 검사·동기화) 읽기가 던진다 — 받는 곳이 없어 앱이 꺼졌다(2026-11-13 점검 9번). '없음'으로 치고 마스크를 비우지도 않는다
        try { auto = MaskSidecar.LoadAuto(_refs[_index].OutPath); }
        catch (Exception ex) { TxtStatus.Text = "자동 감지 사본(.auto.png)을 읽지 못했어요 — 잠시 뒤 다시 눌러 주세요: " + ex.Message; return; }
        using var autoOwner = auto;
        MaskPushUndo();
        if (auto != null && auto.Size() == _maskMat.Size()) auto.CopyTo(_maskMat); else _maskMat.SetTo(Scalar.Black);
        MaskTouched(); SettleMaskDirty(); Redraw(); UpdateMaskInfo();
        TxtStatus.Text = auto != null ? "자동 감지 결과로 되돌렸어요" : "자동 감지 사본이 없어 마스크를 비웠어요";
    }

    private void BtnMClear_Click(object sender, RoutedEventArgs e)
    {
        if (_maskMat == null) return;
        MaskPushUndo(); _maskMat.SetTo(Scalar.Black); _maskPoly.Clear();
        MaskTouched(); SettleMaskDirty(); Redraw(); UpdateMaskInfo();
    }

    private void ChkMSkip_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _maskSc == null || _maskResetting) return;
        _maskDirty = true; BtnSave.IsEnabled = true;   // 저장 마스크가 없는 장은 ChkMSkip이 사이드카에 갈 데가 없다 — 위에서 걸러진다
        SettleMaskDirty();   // 켰다 도로 끈 체크는 고친 것이 아니다
        UpdateRerenderButton();
        UpdateMaskInfo();
    }

    /// <summary>지금 마스크·건너뜀을 .neko 사이드카에 쓴다 (자동 감지 사본·원고 PNG는 그대로). 썼거나 쓸 것이 없으면 true — 못 썼으면 상태 줄에 까닭을 남기고 false
    /// (전에는 돌려주는 값 없이 _maskDirty로 가렸다: 고친 것 없이 다시 저장만 하는 길([지우기])은 실패를 알 수 없어 못 쓴 마스크 대신 옛 마스크로 돌았다 — 2026-11-13 점검 15번).</summary>
    private bool SaveMaskSidecar()
    {
        if (_maskSc == null || _maskMat == null) return true;
        if (_loading) return false;   // 페이지를 넘기는 중 — 옛 장의 마스크를 새 장 사이드카에 쓰지 않는다 (넘기기 전 저장은 ConfirmDiscard가 먼저 한다)
        if (_maskPoly.Count >= 3) MaskClosePolygon();
        try
        {
            // Source는 창이 아는 원본 경로로 — 단 사이드카가 아직 존재하는 다른 경로의 원고 것이면(폴더를 복사해 러너가 빌려 쓰기만 한 마스크) 소유는 그대로 둔다
            var keepSource = !string.IsNullOrEmpty(_refs[_index].Source) && !_maskSc.SourceIs(WaitKey) && _maskSc.SrcLen > 0 && File.Exists(_maskSc.Source);
            // 끝난 장의 마스크를 고쳐 저장하면 '아직 결과물에 반영 전' 표시를 남긴다 — [고친 장 모두 다시 렌더링]이 이 표시로 장을 모으고,
            // 러너가 이 마스크로 결과물을 쓴 뒤에 지운다. 검토 대기 장은 곧 2차로 가니 표시하지 않는다. 한 번도 끝나지 않은 장(검토 대기에서 멈춘 장·2차가 실패한 장)도 표시하지 않는다 —
            // 다시 렌더링하면 식자·채색 없는 결과물이 나온다. 그런 장은 [시작]이 이 마스크로 처음부터 돌린다. 고친 것 없이 다시 저장만 하는 길([지우기]가 먼저 부른다)은 있던 표시 그대로
            _pageFinished = PageFinished(_index);
            // 확인 표시·'고친 장' 표시·채색 여부는 **디스크의 지금 값**에서 옮긴다 — 이 창이 장을 읽은 뒤에 러너가 고쳤을 수 있다(확인을 받음, 2차가 끝나며 채색 여부를 적음).
            // 읽어 둔 값으로 다시 쓰면 그것을 옛 값으로 되돌린다(2026-11-14 검수). Write는 새 레코드라 안 넘기면 채색 여부가 '모름'으로 돌아간다. 못 읽으면 읽어 둔 값으로
            // 같은 원고의 것인지는 경로로, 경로가 다르면 이름·길이로 본다(이 창이 읽은 뒤에 러너가 옮긴 원고의 경로를 고쳐 적었을 수 있다 — 시드 고정의 체크와 같은 규칙)
            var onDisk = MaskSidecar.Load(_refs[_index].OutPath) is { } d && (string.Equals(d.Source, _maskSc.Source, StringComparison.OrdinalIgnoreCase) || d.MatchesSource(WaitKey)) ? d : _maskSc;
            bool pending = !IsWaitingPage && _pageFinished && (_maskDirty || onDisk.Pending);
            // 조각의 성별은 지금 화면의 표시를 적는다("" = 전부 자동 — 넘기지 않으면 Write가 디스크의 옛 표시를 옮겨 와 지운 것이 되살아난다).
            // 지금 마스크의 조각에 맞춰 정리한 꼴로: 지운 조각의 표시는 빠진다(남겨 두면 나중에 그 자리에 칠한 조각에 말없이 붙는다)
            var sexToSave = SexMarksToSave();
            string sexNow = SexMarks.Format(sexToSave) ?? "";
            _maskSc = MaskSidecar.Write(_refs[_index].OutPath, keepSource ? _maskSc.Source : WaitKey, _maskMat, null, null,
                _maskSc.Summary, ChkMSkip.IsChecked == true, _maskEdited || _maskSc.Edited, onDisk.Confirmed, pending: pending, colorized: onDisk.Colorized, regenSex: sexNow);
            _sexMarks = sexToSave; _sexSaved = [.. sexToSave];
            _maskDirty = false;
            _maskBaseline?.Dispose();
            _maskBaseline = _maskMat.Clone();   // 이제 이것이 저장된 마스크다 — 여기서 다시 달라져야 고친 것
            _skipBaseline = ChkMSkip.IsChecked == true;
            BtnSave.IsEnabled = false;
            SyncEditedPage(_index, _maskSc);
            UpdateMaskInfo();
            return true;
        }
        catch (Exception ex)
        {
            // 마스크 PNG는 썼는데 json에서 실패했을 수 있다 — 디스크가 어느 상태인지 모르니 기준을 버린다(기준이 없으면 '달라진 것 없음'으로 더티를 끄지 못한다:
            // 그대로 두면 되돌리기로 옛 마스크에 돌아왔을 때 저장할 것이 없다고 보고, 디스크에는 되돌린 수정이 남는다)
            _maskBaseline?.Dispose(); _maskBaseline = null;
            TxtStatus.Text = "마스크 저장 실패: " + ex.Message;
            return false;
        }
    }

    /// <summary>[✨ 지우기] / [✔ 검열 제거 시작] — 검열 지우기 탭의 주 버튼.
    /// 검토 대기 장: 마스크 저장 → 대기 중인 장을 전부 러너에 보내고 창을 닫는다(진행은 메인 창 목록이 보여 준다 — 창이 남아 있으면 아무 반응이 없어 보였다, 2026-11-12 사용자).
    /// 작업이 도는 동안에는 낱장 다시 렌더링을 하지 않는다 — 모든 장이 한 번 끝난 뒤에. 저장 마스크가 있는 끝난 장: 이 장만 다시 렌더링(RedoThisPageAsync).
    /// 저장 마스크가 없는 장: 결과물 제자리에 지운다(ManualOnResultAsync).</summary>
    private async Task DecensorApplyAsync()
    {
        if (_maskMat == null) return;
        if (_maskPoly.Count >= 3) MaskClosePolygon();   // 찍다 만 다각형도 살려 준다
        if (IsWaitingPage)
        {
            if (!SaveMaskSidecar()) return;   // 저장 실패 — 상태 줄에 까닭이 있다. 못 쓴 마스크 대신 옛 마스크로 지우지 않는다
            int sent = _confirmAll != null ? _confirmAll() : _confirmPage?.Invoke(WaitKey) == true ? 1 : 0;
            if (sent > 0) { Close(); return; }
            TxtStatus.Text = "이 페이지는 지금 검토 대기 중이 아니에요";
            return;
        }
        if (JobRunning)
        {
            TxtStatus.Text = "작업이 도는 중이에요 — 모든 장이 한 번 끝난 뒤에 이 장만 다시 지울 수 있어요";
            return;
        }
        if (HasStoredMask) await RedoThisPageAsync();
        else await ManualOnResultAsync();
    }

    // ── 🔁 고친 장 모두 다시 렌더링 (2026-11-13 사용자 요청: "수정이 된 페이지들을 전체 한번에 다시 재랜더링", 끝나면 또 고쳐 반복) ──────────
    // 끝난 장의 마스크를 고쳐 저장하면 사이드카에 표시(MaskSidecar.Pending)가 남는다 — 창을 닫았다 열어도, 작업 도중 고쳐 둔 장도 모인다.
    // 묶음은 메인 창이 돌린다: 이 창은 요청(RerenderEditedRequested)만 남기고 닫히고, 진행은 목록의 행이 보여 준다([검열 제거 시작]과 같은 흐름).
    // ⚙ 설정의 검열 제거 방식으로 돈다 — 이 탭의 '모델'·'이 페이지만' 재생성 값은 낱장 [지우기] 전용이다(창이 닫히면 사라지는 값).
    private readonly HashSet<int> _pendingPages = [];   // 고친 마스크가 아직 결과물에 반영 전인 장 (페이지 번호)
    private readonly HashSet<int> _pendingSeen = [];    // 이 창이 읽거나 저장해 상태를 아는 장 — 늦게 끝난 첫 훑기가 덮어쓰지 않게

    /// <summary>창을 열 때 한 번, 모든 장의 사이드카를 훑어 고쳐 둔 장을 찾는다(json만 읽는다 — 백그라운드). 메인 창이 연 창에서만.</summary>
    private async void ScanEditedPages()
    {
        if (!_canRerenderEdited) return;
        var refs = _refs.ToList();
        List<int> found;
        try
        {
            found = await Task.Run(() => Enumerable.Range(0, refs.Count)
                .Where(i => !string.IsNullOrEmpty(refs[i].OutPath) && MaskSidecar.IsPendingFor(refs[i].OutPath, SourceOf(refs[i]))).ToList());
        }
        catch { return; }   // 훑기 실패는 조용히 — 장을 열면 그 장의 상태는 다시 맞춰진다(SyncEditedPage)
        if (_closed) return;
        foreach (var i in found)
            if (!_pendingSeen.Contains(i) && _pendingPages.Add(i)) RefreshPageLabel(i);
        UpdateRerenderButton();
    }

    /// <summary>이 장의 '고친 마스크 대기' 상태를 방금 읽거나 쓴 사이드카에 맞춘다 — 페이지 콤보의 ✎ 표시와 버튼의 장 수도 같이.</summary>
    private void SyncEditedPage(int idx, MaskSidecar? sc)
    {
        if (!_canRerenderEdited) return;
        _pendingSeen.Add(idx);
        bool changed = sc?.Pending == true ? _pendingPages.Add(idx) : _pendingPages.Remove(idx);
        if (changed) RefreshPageLabel(idx);
        UpdateRerenderButton();
    }

    /// <summary>페이지 콤보에 보이는 이름 — 고친 마스크가 대기 중인 장은 앞에 ✎.</summary>
    private string PageLabel(int idx) => (_pendingPages.Contains(idx) ? "✎ " : "") + Path.GetFileName(_refs[idx].ViewPath);

    private void RefreshPageLabel(int idx)
    {
        if (idx >= CmbPages.Items.Count) return;
        var label = PageLabel(idx);
        if (Equals(CmbPages.Items[idx], label)) return;
        // 고른 항목을 갈아 끼우면 선택이 풀려 다시 고른다. _index가 아니라 콤보의 선택으로 본다 — 콤보에서 다른 장을 골라 넘어가는 중(떠나는 장의 마스크를 저장하는 중)이면
        // 콤보는 이미 새 장을 가리키고 있어, 떠나는 장을 다시 고르면 안 된다
        bool wasSelected = CmbPages.SelectedIndex == idx;
        CmbPages.Items[idx] = label;
        if (wasSelected) CmbPages.SelectedIndex = idx;
    }

    /// <summary>2차가 한 번이라도 끝난 장인가 — 이번 작업에서 끝났거나(메인 창 행의 Done) 결과물이 그 자리에 있다. --result 모드는 보는 파일이 곧 결과물이다.</summary>
    private bool PageFinished(int idx)
    {
        var r = _refs[idx];
        if (string.IsNullOrEmpty(r.Source)) return true;
        return _isDone?.Invoke(r.Source) == true || PipelineRunner.HasResultFor(r.OutPath, r.Source);
    }
    private bool _pageFinished;   // 지금 장의 PageFinished — 장을 읽을 때와 마스크를 저장할 때 잰다(붓질마다 파일을 보지 않게)

    /// <summary>[고친 장 모두 다시 렌더링]이 돌릴 장 수 — 고쳐 저장해 둔 장 + 지금 장에 저장 안 한 수정이 있으면 그 장(누를 때 저장한다).</summary>
    private int EditedPageCount =>
        _pendingPages.Count + (_maskDirty && HasStoredMask && _pageFinished && !IsWaitingPage && !_pendingPages.Contains(_index) ? 1 : 0);

    private int _rerenderShown = -1;

    private void UpdateRerenderButton()
    {
        if (!_canRerenderEdited) return;   // XAML 기본이 숨김
        int n = EditedPageCount;
        BtnRerenderEdited.Visibility = Visibility.Visible;
        BtnRerenderEdited.IsEnabled = n > 0 && !_busy;   // 작업이 도는 중인지는 누를 때 본다 — 창이 열린 사이 작업이 끝나도 버튼이 꺼진 채 남지 않게
        if (n == _rerenderShown) return;   // 칠하는 동안 붓질마다 불린다
        _rerenderShown = n;
        BtnRerenderEdited.Content = n > 0 ? $"🔁 고친 장 모두 다시 렌더링 ({n}장)" : "🔁 고친 장 모두 다시 렌더링";
        BtnRerenderEdited.ToolTip = n > 0
            ? "마스크를 고친 장(목록의 ✎)을 한꺼번에 다시 렌더링해요 — 검열 제거 + 식자, 채색은 그대로. ⚙ 설정의 검열 제거 방식으로 돌고, 이 창은 닫혀요 (진행은 목록에서 볼 수 있어요)"
            : "마스크를 고친 장이 아직 없어요 — 여러 장의 마스크를 고치면(장을 넘길 때 자동으로 저장돼요) 한꺼번에 다시 렌더링할 수 있어요";
    }

    private void BtnRerenderEdited_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading) return;
        if (JobRunning) { TxtStatus.Text = "작업이 도는 중이에요 — 모든 장이 한 번 끝난 뒤에 고친 장을 다시 렌더링할 수 있어요"; return; }
        SettleMaskBeforeLeaving();   // 찍다 만 다각형도 살려 준다([지우기]와 같다) — 안 닫으면 말없이 버려지고 그 장이 묶음에서 빠진다. 달라진 것이 없으면 고친 장으로 치지 않는다
        if (_maskDirty && HasStoredMask && !IsWaitingPage && !SaveMaskSidecar()) return;   // 지금 장의 저장 안 한 수정도 같이 돌린다(버튼의 장 수에 이미 들어 있다) — 저장 실패면 상태 줄에 까닭이 있다
        if (_pendingPages.Count == 0) { TxtStatus.Text = "마스크를 고쳐 저장한 장이 없어요"; return; }
        // 지금 장도 돌 장이면 식자 탭에서 고친 글·숨김이 다시 렌더링 뒤에도 남게 (낱장 [지우기]와 같다) — 못 쓰면 옛 글로 돌게 두지 않는다
        if (_sidecar != null && _pendingPages.Contains(_index) && !SaveLetterSidecar()) return;
        if (!ConfirmDiscard()) return;               // 결과물에 저장 안 한 손 편집만 묻는다(다시 렌더링이 덮는다)
        // 이 창이 아는 고친 장을 같이 넘긴다 — 메인 창은 사이드카를 다시 훑지만, 방금 저장한 json을 백신·동기화가 잠깐 쥐고 있으면 못 읽어 그 장이 말없이 빠진다
        RerenderEditedPages = [.. _pendingPages.Order().Select(i => (SourceOf(_refs[i]), _refs[i].OutPath))];
        RerenderEditedRequested = true;
        Close();
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
        if (!SaveMaskSidecar()) return;              // 먼저 — [지우기]는 이 마스크를 쓰라는 뜻이라 "고친 마스크를 저장할까요?"를 묻지 않는다(하네스에서 창이 멈춰 잡힘). 못 썼으면 옛 마스크로 돌리지 않는다
        if (_sidecar != null && !SaveLetterSidecar()) return;   // 식자 탭에서 고친 글·숨김이 다시 렌더링 뒤에도 남게 — 못 썼으면 옛 글로 돌리지 않는다
        if (!ConfirmDiscard()) return;               // 결과물에 저장 안 한 손 편집만 묻는다(다시 렌더링이 덮는다)
        var source = WaitKey;
        var outPath = _refs[_index].OutPath;
        var options = new PipelineRunner.RedoPageOptions(DecensorAndLetterOnly: true, Grow: GrowPx);
        _busy = true;
        _redoCts?.Dispose();
        _redoCts = new CancellationTokenSource();
        var ct = _redoCts.Token;
        ShowLoading(true, "준비 중");
        string? warned = null;   // 러너가 '완료' 줄에 붙인 경고(⚠ …) — 오버레이에 잠깐 보이고 사라지지 않게 끝난 뒤 상태 줄에 옮긴다(고정한 시드보다 영역이 늘었다 등)
        var progress = new Progress<PageProgress>(p =>
        {
            ShowLoading(true, p.Error != null ? "실패: " + p.Error : p.Stage, p.Percent);
            int at = p.Done ? p.Stage.IndexOf(" · ⚠ ", StringComparison.Ordinal) : -1;
            if (at >= 0) warned = p.Stage[at..];
        });
        bool regen = EngineMode == "ComfyRegen" && ChkMSkip.IsChecked != true;   // '검열 없음'이면 재생성이 돌지 않는다 — 지난 기록의 시드를 이번에 쓴 것처럼 보여 주지 않는다
        try
        {
            var runner = RedoRunner();
            await Task.Run(() => runner.RedoPageAsync(source, progress, ct, outPath, options), CancellationToken.None);
            _busy = false;
            _dirty = false;
            var after = MaskSidecar.Load(outPath);
            SyncEditedPage(_index, after);   // 러너가 표시를 지웠다 — 아래 LoadPage가 끝나기 전에 장을 넘겨도 ✎·장 수가 남지 않게 여기서 맞춘다
            // 재생성이면 이번에 쓴 시드를 알려 준다 — 마음에 들면 재생성 설정의 '이 장의 시드 고정'으로 붙잡아 둘 수 있다
            string seedNote = regen && after?.RegenSeeds is { Length: > 0 } sd ? $" · 시드 {sd.Replace(",", ", ")}{(after.SeedLock ? " (고정)" : "")}" : "";
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
            _statusAfterLoad = "다시 렌더링했어요 — 결과물이 갱신됐어요 (검열 제거 + 식자, 채색은 그대로)" + seedNote + warned;
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
            // 이 길은 사이드카가 없다 — 조각에 고른 성별은 창이 들고 있다가 넘긴다(마스크 좌표 → 결과물 좌표)
            var sexMarks = SexMarks.Scaled(_sexMarks.ToList(), (double)_img.Width / _maskMat.Width, (double)_img.Height / _maskMat.Height);
            await Task.Run(async () => await runner.ManualDecensorAsync(img, mask, engineMode, status, ct, regenSettings, sexMarks: sexMarks), CancellationToken.None);
            ct.ThrowIfCancellationRequested();   // 기본 모델(Aletheia/LaMa)은 ct를 보지 않고 끝까지 돈다 — 그 사이 [취소]를 눌렀으면 결과를 버린다(아래 catch가 before로 되돌린다)
            _undo.Push(before);
            _dirty = true;
            InvalidateBitmap();
            MaskPushUndo(); _maskMat.SetTo(Scalar.Black); _maskDirty = false;   // 지운 자리는 결과물에 들어갔다 — 마스크는 비운다(되돌릴 수 있게 undo에)
            _sexMarks = []; _sexSaved = []; ClearSexPieces();   // 조각이 없어졌다 — 그 조각들에 고른 성별도 같이
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
