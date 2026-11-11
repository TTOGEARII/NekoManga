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
    /// <summary>결과 창 한 페이지: 보여 줄 그림(결과물 또는 원본)과 결과물 경로(.neko 사이드카 위치 기준).</summary>
    /// <param name="ViewPath">지금 보여 주는 파일(결과물이 있으면 결과물, 아직 없으면 원본). 창이 열린 뒤 결과물이 생기면 결과물로 갈아탄다.</param>
    /// <param name="OutPath">결과물 경로 — .neko 사이드카 위치. --result 모드는 ViewPath와 같다.</param>
    /// <param name="Source">원본 원고 경로(메인 창에서 열 때). 원본에는 절대 저장하지 않는다 — 비어 있으면 ViewPath가 곧 결과물.</param>
    public sealed record PageRef(string ViewPath, string OutPath, string Source = "");

    private readonly List<PageRef> _refs;
    private int _index;
    // 페이지별 검토(2026-10-21): 메인 창이 넘겨 주는 러너 훅 — 원고 경로로 '검토 대기 중인지'와 '진행 확인'
    private readonly Func<string, bool>? _confirmPage;
    private readonly Func<string, bool>? _isWaiting;
    private readonly Func<string, bool>? _isDone;   // 메인 창 행의 Done — 끝나지 않은 장이 지난 작업 결과물을 보여 줄 때 안내한다
    private bool _startOnMask;

    private Mat _img = new();
    private readonly Stack<Mat> _undo = new();
    private readonly List<CvPoint[]> _shapes = [];   // 완성된 도형들 (이미지 좌표)
    private readonly List<CvPoint> _pending = [];    // 찍는 중인 꼭짓점
    private WinPoint _hover;

    private double _scale = 1.0;
    private bool _dirty;
    private bool _busy;
    private bool _closed;   // 창이 닫혔다 — 그때 돌던 수동 작업이 끝나면 러너를 정리한다(DisposeRunnerIfClosed)

    public ResultWindow(string imagePath) : this([imagePath], 0) { }

    /// <summary>--result 등 결과물 경로만 있을 때: 사이드카는 그 파일 옆 .neko에서 찾는다.</summary>
    public ResultWindow(IReadOnlyList<string> pages, int startIndex)
        : this(pages.Select(p => new PageRef(p, p)).ToList(), startIndex) { }

    // ── 식자 탭 상태 ─────────────────────────────────────────
    private LetterSidecar? _sidecar;              // 현재 페이지의 식자 사이드카 (없으면 탭 비활성)
    private List<TextBlockInfo> _letterBlocks = [];
    private bool _syncingBlockUi;                 // 콤보/텍스트박스 동기화 중 재진입 방지
    private Mat? _baseImg;                        // 식자 전 원고 — 편집 모드에서 이 위에 텍스트박스를 띄운다
    private PipelineRunner? _runner;              // 수동 복원·다시 돌리기용 러너 — 창 수명 동안 ONNX 세션과 ComfyUI 사용을 쥔다(창이 닫힐 때 놓는다)
    private bool _letterPreview;                  // true면 [다시 식자] 결과(_img)를 보여준다

    public ResultWindow(IReadOnlyList<PageRef> pages, int startIndex,
        Func<string, bool>? confirmPage = null, Func<string, bool>? isWaiting = null, bool startOnMask = false, Func<string, bool>? isDone = null)
    {
        InitializeComponent();
        _refs = pages.Count > 0 ? [.. pages] : [new PageRef("", "")];
        _index = Math.Clamp(startIndex, 0, _refs.Count - 1);
        _confirmPage = confirmPage; _isWaiting = isWaiting; _startOnMask = startOnMask; _isDone = isDone;

        // '기본' 모델 이름은 감지·복원 엔진 설정을 따른다 (Aletheia Lens / 이전 방식 LaMa) — 손으로 고른 자리는 Aletheia에선 막대 모델로 지운다
        ItemEngineBasic.Content = DecensorLabels.BasicModelLabel(App.Settings.DecensorEngine);
        ItemEngineBasic.ToolTip = DecensorLabels.IsAletheia(App.Settings.DecensorEngine)
            ? "Aletheia Lens 복원 모델 — 고른 자리를 비우고 주변 그림에 맞춰 채워요 (내장, 항상 같은 결과)"
            : "내장 LaMa 모델, 항상 같은 결과";

        foreach (var p in _refs) CmbPages.Items.Add(Path.GetFileName(p.ViewPath));
        SldMBrush.ValueChanged += (_, _) => { TxtMBrush.Text = $"{(int)SldMBrush.Value}px"; if (IsLoaded) UpdateBrushCursor(); };

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

    /// <summary>이 창에서 저장하거나 다시 돌린 페이지의 원본 경로 — 메인 창이 닫힌 뒤 그 행들만 결과물로 다시 푼다 (시계·mtime에 기대지 않는다).</summary>
    public HashSet<string> ChangedSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    private void TabMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        UpdateModeButtons();
        _pending.Clear();
        _letterPreview = false;      // 탭에 들어오면 항상 편집 모드부터
        if (!_img.Empty()) FitToWindow();   // 마스크 탭은 검열 제거 전 원고(원본 크기)를, 다른 탭은 결과물을 보여 줘 크기가 다를 수 있다
        Redraw();
        if (LetterMode)
            TxtStatus.Text = _letterBlocks.Count > 0
                ? $"번역 블록 {_letterBlocks.Count}개 — 말풍선의 텍스트박스를 바로 고치세요"
                : "이 장은 보일 블록이 없어요";
        else if (MaskMode)
            TxtStatus.Text = IsWaitingPage ? "검토 대기 중 — 빨간 마스크를 고친 뒤 [검열 제거 진행]을 누르세요"
                                           : "저장된 마스크를 고친 뒤 [저장 마스크로 다시 검열 제거] 또는 [채색부터 다시]";
    }

    /// <summary>탭에 따라 아래 버튼 바의 글자·표시를 맞춘다.</summary>
    private void UpdateModeButtons()
    {
        BtnApply.Content = LetterMode ? "✍ 다시 식자"
                         : MaskMode ? (IsWaitingPage ? "✔ 검열 제거 진행" : "✨ 저장 마스크로 다시 검열 제거")
                         : "✨ 선택 영역 지우기";
        BtnClearSel.Visibility = LetterMode || MaskMode ? Visibility.Collapsed : Visibility.Visible;
        BtnSave.Content = MaskMode ? "💾 마스크 저장" : "💾 저장";
        BtnSave.IsEnabled = MaskMode ? _maskDirty : _dirty;
        BtnUndo.IsEnabled = MaskMode ? _maskUndo.Count > 0 : _undo.Count > 0;
    }

    /// <summary>저장 안 한 수정이 있으면 물어본다. 계속해도 되면 true.</summary>
    private bool ConfirmDiscard()
    {
        if (_maskDirty)
        {
            var mr = MessageBox.Show("고친 마스크를 저장할까요?", "마스크 수정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mr == MessageBoxResult.Cancel) return false;
            if (mr == MessageBoxResult.Yes) SaveMaskSidecar();
            _maskDirty = false;
        }
        if (!_dirty) return true;
        var r = MessageBox.Show("저장하지 않은 수정이 있어요. 저장할까요?", "결과 보기",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) Save();
        _dirty = false;
        return true;
    }

    // ── 브러시 커서: 칠해질 범위를 원 테두리로 (검토 창과 같은 모양, 사용자 요청 2026-10-21) ──
    private readonly System.Windows.Shapes.Ellipse _cursorOuter = new() { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Ellipse _cursorInner = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 1.5, IsHitTestVisible = false };
    private bool _cursorVisible;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (MaskMode) { SaveMaskSidecar(); TxtStatus.Text = "마스크를 저장했어요 — 다음에 다시 돌려도 이 마스크를 써요"; return; }
        Save();
    }

    private void Save()
    {
        // 페이지를 읽는 동안(_index는 새 장, 그림·마스크는 아직 옛 장)에는 저장하지 않는다 — 옛 장의 그림이 새 장의 결과로 들어간다
        if (_loading) { TxtStatus.Text = "페이지를 읽는 중이에요 — 잠시 뒤 다시 저장해 주세요"; return; }
        try
        {
            var r = _refs[_index];
            // 원본 원고에는 절대 쓰지 않는다 — 원본을 아는 페이지는 늘 결과물 png(OutPath)에 (결과 이름은 원고 하나로 정해져 이 원고만 쓰는 자리;
            // '원본 그대로' 복사본 jpg/webp 위에 쓰면 손실 재인코딩된다). --result 모드도 png로, 그 자리에 다른 파일이 있으면 옆 이름으로
            var target = string.IsNullOrEmpty(r.Source) ? Path.ChangeExtension(r.ViewPath, ".png") : r.OutPath;
            if (string.IsNullOrEmpty(r.Source) && !string.Equals(target, r.ViewPath, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                target = Path.Combine(Path.GetDirectoryName(r.ViewPath) ?? "", Path.GetFileNameWithoutExtension(r.ViewPath) + ".edited.png");
            if (!string.IsNullOrEmpty(r.Source) && string.Equals(PipelineRunner.FileKey(target), PipelineRunner.FileKey(r.Source), StringComparison.Ordinal))
            {
                TxtStatus.Text = "원본 원고와 같은 경로라 저장하지 않았어요 — 설정에서 출력 폴더를 따로 지정해 주세요";
                return;
            }
            ImgIo.Write(target, _img);
            // 이 결과가 어느 원고 것인지 러너처럼 적어 둔다(.neko의 주인 기록) — 안 적으면 낡은 남의 기록이 남은 자리에 저장한 결과가 그 원고 것으로 읽혀,
            // 이 원고를 다시 돌리면 막히고 그 원고를 돌리면 방금 저장한 것이 덮인다. 원본을 모르는 --result 모드는 적을 주인이 없다
            var ownerWarning = string.IsNullOrEmpty(r.Source) ? null : PipelineRunner.RecordResultOwner(target, r.Source);
            _dirty = false;
            BtnSave.IsEnabled = false;
            ChangedSources.Add(SourceOf(r));
            // 남아 있던 '원본 그대로' 복사본은 치운다 — 이제 이 장의 결과는 png다 (출력 폴더=원고 폴더면 AsIsOf가 원본 자신이라 건드리지 않는다)
            if (!string.IsNullOrEmpty(r.Source))
            {
                var asIs = AsIsOf(r);
                if (!string.Equals(asIs, r.Source, StringComparison.OrdinalIgnoreCase) && !string.Equals(asIs, target, StringComparison.OrdinalIgnoreCase))
                    try { if (File.Exists(asIs)) File.Delete(asIs); } catch { }
            }
            bool redirect = !string.Equals(target, r.ViewPath, StringComparison.OrdinalIgnoreCase);   // 최종 목적지 기준
            if (redirect)
            {
                SetViewPath(_index, target);
                TxtStatus.Text = "결과물로 저장했어요 → " + Path.GetFileName(target) + " (원본은 그대로)"
                               + (IsWaitingPage ? " — 검열 제거를 진행하면 이 결과물은 다시 만들어져요" : "");
            }
            else TxtStatus.Text = "저장했어요 → " + Path.GetFileName(target);
            if (ownerWarning != null) TxtStatus.Text += " · ⚠ " + ownerWarning;
        }
        catch (Exception ex) { TxtStatus.Text = "저장 실패: " + ex.Message; }
    }

    /// <summary>이 페이지가 보여 주는(=저장하는) 파일을 바꾼다 — 제목·페이지 콤보도 같이.</summary>
    private void SetViewPath(int idx, string path)
    {
        _refs[idx] = _refs[idx] with { ViewPath = path };
        if (idx == _index) { TxtFile.Text = Path.GetFileName(path); TxtFile.ToolTip = path; }
        if (idx < CmbPages.Items.Count) { CmbPages.Items[idx] = Path.GetFileName(path); if (idx == _index) CmbPages.SelectedIndex = idx; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        base.OnClosing(e);
        ++_loadGen;   // 아직 기다리는 LoadPage가 있으면 깨어나도 아무것도 안 하게
        // 복원이 돌고 있으면 그 Mat·러너는 지금 해제하지 않는다 — 쓰는 중인 네이티브 메모리를 해제하면 죽는다.
        // 러너는 그 작업이 끝날 때 정리하고(DisposeRunnerIfClosed — ComfyUI 사용도 그때 놓는다), Mat은 GC가 거둔다
        _closed = true;
        if (!_busy) { _runner?.Dispose(); _img.Dispose(); }
        _baseImg?.Dispose();
        ResetMaskState(null);
        while (_undo.Count > 0) _undo.Pop().Dispose();
        foreach (var t in _prefetch.Values) t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
        _prefetch.Clear();
    }

    /// <summary>창을 닫을 때 돌고 있던 수동 작업이 끝났다 — 그때 못 한 러너 정리(ONNX 세션 닫기, ComfyUI 사용 놓기)를 이제 한다.
    /// 놓지 않으면 KeepComfyWarm=false여도 이 창의 작업이 띄웠거나 같이 쓰던 서버가 꺼지지 않는다. 수동 작업의 finally에서 부른다.</summary>
    private void DisposeRunnerIfClosed()
    {
        if (!_closed || _busy) return;
        _runner?.Dispose();
        _runner = null;
    }
}
