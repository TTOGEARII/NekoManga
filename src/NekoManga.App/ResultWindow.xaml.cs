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
    // 작업이 도는 동안의 검토(2026-11-12 사용자 요청): [검열 제거 시작]은 대기 중인 장을 전부 보내고 창을 닫는다. 낱장 다시 렌더링은 모든 장이 한 번 끝난 뒤에만
    private readonly Func<int>? _confirmAll;
    private readonly Func<bool>? _isJobRunning;
    private bool JobRunning => _isJobRunning?.Invoke() == true;
    private readonly Func<string, bool>? _isDone;   // 메인 창 행의 Done — 끝나지 않은 장이 지난 작업 결과물을 보여 줄 때 안내한다
    // 고친 장 모두 다시 렌더링(2026-11-13 사용자 요청): 묶음은 메인 창이 돌린다(행이 진행을 보여 준다) — 이 창은 요청만 남기고 닫힌다. --result 모드는 돌려 줄 메인 창이 없어 버튼을 숨긴다
    private readonly bool _canRerenderEdited;
    /// <summary>[🔁 고친 장 모두 다시 렌더링]을 누르고 닫혔다 — 메인 창이 창이 닫힌 뒤 고친 장들을 다시 렌더링한다.</summary>
    public bool RerenderEditedRequested { get; private set; }
    /// <summary>그때 이 창이 알던 고친 장들(원고 경로·결과물 경로) — 메인 창이 자기가 훑은 것과 합친다.</summary>
    public IReadOnlyList<(string Source, string OutPath)> RerenderEditedPages { get; private set; } = [];
    private bool _startOnMask;

    private Mat _img = new();
    private readonly Stack<Mat> _undo = new();
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
    private bool _letterDirty;                    // 식자 탭에서 블록(글·위치·숨김·풍선 스타일)을 고쳤고 아직 사이드카·결과물에 반영하지 않았다 — 넘기거나 닫을 때 말없이 잃지 않게(2026-11-13 점검 16번)

    public ResultWindow(IReadOnlyList<PageRef> pages, int startIndex,
        Func<string, bool>? confirmPage = null, Func<string, bool>? isWaiting = null, bool startOnMask = false, Func<string, bool>? isDone = null,
        Func<int>? confirmAll = null, Func<bool>? isJobRunning = null, bool canRerenderEdited = false)
    {
        InitializeComponent();
        UiText.Apply(this);   // XAML에 적힌 글을 지금 화면 언어로 — 뜨기 전에
        _refs = pages.Count > 0 ? [.. pages] : [new PageRef("", "")];
        _index = Math.Clamp(startIndex, 0, _refs.Count - 1);
        _confirmPage = confirmPage; _isWaiting = isWaiting; _startOnMask = startOnMask; _isDone = isDone;
        _confirmAll = confirmAll; _isJobRunning = isJobRunning; _canRerenderEdited = canRerenderEdited;
        if (_canRerenderEdited) TxtDecensorHint.Text += Loc.T(" 여러 장을 고친 뒤 위의 [고친 장 모두 다시 렌더링]으로 한꺼번에 돌릴 수 있어요.");

        // '기본' 모델은 늘 Aletheia Lens 복원('이전 방식' 엔진은 2026-11-12 삭제) — 손으로 고른 자리는 막대 모델로 지운다. 이름·툴팁은 XAML의 ItemEngineBasic
        foreach (var p in _refs) CmbPages.Items.Add(Path.GetFileName(p.ViewPath));
        SldMBrush.ValueChanged += (_, _) => { TxtMBrush.Text = $"{(int)SldMBrush.Value}px"; if (IsLoaded) UpdateBrushCursor(); };

        foreach (var f in System.Drawing.FontFamily.Families.Select(f => f.Name).OrderBy(n => n))
            CmbLFont.Items.Add(f);
        var s = App.Settings;
        // '모델' 칸은 ⚙ 설정의 검열 제거 방식으로 시작한다(2026-11-13 사용자 결정). 전에는 늘 '기본'이라, 재생성으로 돌린 작업의 장을 낱장 [지우기]로 고치면
        // 직접 바꾸지 않는 한 기본 모델로 지워졌다([고친 장 모두 다시 렌더링]은 설정의 방식으로 돌아 둘이 어긋났다). 설정이 재생성·커스텀인데 ComfyUI가 없으면 바로 쓸 수 있는 '기본'으로 둔다
        var startMode = s.DecensorMode is "ComfyRegen" or "ComfyCustom" && !s.ComfyAvailable ? "Basic" : s.DecensorMode;
        foreach (var it in CmbEngine.Items.OfType<ComboBoxItem>())
            if ((string?)it.Tag == startMode) { CmbEngine.SelectedItem = it; break; }
        PnlSexAll.Visibility = RegenMode && s.RegenTagsBySex ? Visibility.Visible : Visibility.Collapsed;
        PnlRegen.Visibility = RegenMode ? Visibility.Visible : Visibility.Collapsed;   // CmbEngine_Changed는 창이 뜨기 전에는 아무것도 하지 않는다 — 재생성 칸은 여기서 편다(값은 LoadPage가 채운다)
        CmbLFont.Text = s.LetterFontFamily;
        ChkLBold.IsChecked = s.LetterBold;
        TxtLSize.Text = s.LetterSizeScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TxtLColor.Text = s.LetterColorHex;
        TxtLOutScale.Text = s.LetterOutlineScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TxtLOutColor.Text = s.LetterOutlineColorHex;
        // 풍선별 글꼴: 맨 위는 "페이지 설정"(빈 값), 그 아래 설치된 글꼴
        CmbBFont.Items.Add(new ComboBoxItem { Content = Loc.T("페이지 설정"), Tag = "" });
        foreach (var f in CmbLFont.Items.OfType<string>().ToList())
            CmbBFont.Items.Add(new ComboBoxItem { Content = f, Tag = f });
        CmbBFont.SelectedIndex = 0;
        UpdateSwatches();

        // 스타일 입력이 바뀌면 WYSIWYG 텍스트박스에 즉시 반영
        CmbLFont.SelectionChanged += (s, e) => LetterStyle_Changed(s, e);
        CmbLFont.LostFocus += LetterStyle_Changed;
        ChkLBold.Checked += (s, e) => LetterStyle_Changed(s, e);
        ChkLBold.Unchecked += (s, e) => LetterStyle_Changed(s, e);
        TxtLSize.TextChanged += (s, e) => LetterStyle_Changed(s, e);
        TxtLColor.TextChanged += (s, e) => LetterStyle_Changed(s, e);
        TxtLOutScale.TextChanged += (s, e) => LetterStyle_Changed(s, e);   // 외곽선 칸은 전에 화면에 안 이어져 있었다 — [다시 식자]를 눌러야 보였다
        TxtLOutColor.TextChanged += (s, e) => LetterStyle_Changed(s, e);

        Loaded += (_, _) => { LoadPage(_index, fit: true); ScanEditedPages(); };
    }

    /// <summary>이 창에서 저장하거나 다시 돌린 페이지의 원본 경로 — 메인 창이 닫힌 뒤 그 행들만 결과물로 다시 푼다 (시계·mtime에 기대지 않는다).</summary>
    public HashSet<string> ChangedSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    private void TabMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        UpdateModeButtons();
        _maskPoly.Clear();
        _letterPreview = false;      // 탭에 들어오면 항상 편집 모드부터
        _editBlock = -1; _letterStale = true;
        if (!_img.Empty()) FitToWindow();   // 마스크 탭은 검열 제거 전 원고(원본 크기)를, 다른 탭은 결과물을 보여 줘 크기가 다를 수 있다
        Redraw();
        if (LetterMode)
            TxtStatus.Text = _letterBlocks.Count > 0
                ? Loc.F($"번역 블록 {_letterBlocks.Count}개 — 풍선을 클릭해 고르고, 두 번 클릭하면 글을 고쳐요")
                : Loc.T("이 장은 보일 블록이 없어요");
        else if (MaskMode)
            TxtStatus.Text = IsWaitingPage ? Loc.T("검토 대기 중 — 빨간 마스크를 고친 뒤 [검열 제거 시작]을 누르세요")
                           : HasStoredMask ? Loc.T("빨간 마스크를 고친 뒤 [지우기] — 이 장만 검열 제거·식자를 다시 해요")
                           : Loc.T("지울 곳을 칠한 뒤 [지우기] — 결과물에 바로 지워요");
    }

    /// <summary>탭에 따라 아래 버튼 바의 글자·표시를 맞춘다.</summary>
    private void UpdateModeButtons()
    {
        BtnApply.Content = LetterMode ? Loc.T("✍ 다시 식자")
                         : IsWaitingPage ? Loc.T("✔ 검열 제거 시작")
                         : Loc.T("✨ 지우기 (이 장 다시 렌더링)");
        BtnApply.ToolTip = LetterMode ? null
                         : IsWaitingPage ? Loc.T("고친 마스크를 저장하고, 검토 대기 중인 모든 장의 검열 제거를 시작해요 — 이 창은 닫혀요")
                         : JobRunning ? Loc.T("작업이 도는 중이에요 — 모든 장이 한 번 끝난 뒤에 이 장만 다시 지울 수 있어요")
                         : HasStoredMask ? Loc.T("검열 제거 전 원고에 이 마스크로 검열 제거 → 식자를 이 장만 다시 해요 (채색은 안 해요)")
                         : Loc.T("결과물에 이 마스크 자리를 바로 지워요 — 끝나면 [저장]");
        BtnSave.Content = MaskMode ? Loc.T("💾 마스크 저장") : Loc.T("💾 저장");
        BtnSave.Visibility = MaskMode && !HasStoredMask ? Visibility.Collapsed : Visibility.Visible;   // 저장 마스크가 없는 장은 사이드카를 새로 만들지 않는다
        BtnSave.IsEnabled = MaskMode ? _maskDirty : _dirty;
        BtnUndo.IsEnabled = MaskMode ? _maskUndo.Count > 0 : _undo.Count > 0;
        UpdateRerenderButton();
    }

    // ── 로딩 오버레이 ([지우기] 다시 렌더링 중) ───────────────────────────────────
    private CancellationTokenSource? _redoCts;

    /// <summary>캔버스를 덮는 오버레이를 켜고 끈다. 켜는 동안 탭·페이지 넘김·목록을 막는다(_busy와 같이).</summary>
    private void ShowLoading(bool on, string stage = "", double pct = -1)
    {
        if (on)
        {
            TxtLoadStage.Text = stage;
            TxtLoadPct.Text = pct < 0 ? "" : $"{pct:0}%";
            if (PnlLoading.Visibility != Visibility.Visible)
            {
                TxtLoadCancelNote.Visibility = Visibility.Collapsed;
                BtnLoadCancel.IsEnabled = true;
                PnlLoading.Visibility = Visibility.Visible;
            }
            TabMode.IsEnabled = CmbPages.IsEnabled = false;
            BtnPrev.IsEnabled = BtnNext.IsEnabled = false;
            BtnApply.IsEnabled = BtnSave.IsEnabled = BtnUndo.IsEnabled = BtnRerenderEdited.IsEnabled = false;
        }
        else
        {
            PnlLoading.Visibility = Visibility.Collapsed;
            TabMode.IsEnabled = CmbPages.IsEnabled = true;
            BtnApply.IsEnabled = true;
            UpdateRerenderButton();
            if (!_loading) { BtnPrev.IsEnabled = _index > 0; BtnNext.IsEnabled = _index < _refs.Count - 1; }   // 다시 읽는 중이면 LoadPage가 끝날 때 켠다
        }
    }

    private void BtnLoadCancel_Click(object sender, RoutedEventArgs e)
    {
        _redoCts?.Cancel();
        TxtLoadCancelNote.Visibility = Visibility.Visible;
        BtnLoadCancel.IsEnabled = false;
    }

    /// <summary>저장 안 한 수정이 있으면 물어본다. 계속해도 되면 true.
    /// <paramref name="autoSaveMask"/>(장을 넘길 때): 고친 마스크는 묻지 않고 저장한다(2026-11-13 사용자 결정 — 여러 장을 고쳐 한꺼번에 돌리는 흐름에서 장마다 묻지 않게).
    /// 고친 것을 버리려면 넘기기 전에 [되돌리기](Ctrl+Z)나 [자동 감지로]. 창을 닫을 때는 전처럼 묻고, 결과물의 손 편집(_dirty)은 늘 묻는다.</summary>
    /// <para>'예'라고 했는데 저장이 실패하면 머문다(false) — 전에는 실패해도 넘어가거나 닫혀서 고친 것이 사라졌다(2026-11-13 점검 15번).</para>
    private bool ConfirmDiscard(bool autoSaveMask = false)
    {
        if (_loading)
        {
            // 앞선 넘김이 아직 읽는 중 — 화면의 마스크·식자 블록은 이미 떠난 장의 것이라 쓸 자리가 없다(그 장을 떠날 때 이미 물었거나 저장했다). 저장 함수들도 이때는 쓰지 않는다
            _maskDirty = false; _letterDirty = false;
        }
        else SettleMaskBeforeLeaving();
        // 버리기로 한 것의 표시는 떠나기로 확정된 끝에서 지운다 — 앞 물음에 '아니요', 뒤 물음에 '취소'면 이 장에 남는데,
        // 그때 화면의 수정이 표시 없이 남으면 다음에 넘길 때 묻지도 저장하지도 않고 사라진다(2026-11-14 검수)
        bool dropMask = false, dropLetter = false, dropResult = false;
        if (_maskDirty && autoSaveMask && !SaveMaskSidecar()) return false;   // 저장 실패 — 상태 줄에 까닭이 있다. 고친 것을 말없이 잃지 않게 이 장에 머문다
        if (_maskDirty)
        {
            var mr = MessageBox.Show(Loc.T("고친 마스크를 저장할까요?"), Loc.T("마스크 수정"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mr == MessageBoxResult.Cancel) return false;
            if (mr == MessageBoxResult.Yes) { if (!SaveMaskSidecar()) return false; }
            else dropMask = true;
        }
        if (_letterDirty && _sidecar != null)
        {
            // '다시 식자해서 저장할까요?'는 그것이 다른 것을 망가뜨리지 않을 때만 묻는다:
            //  - 고친 장(✎)은 [고친 장 모두 다시 렌더링]이 새 글로 식자한다 — 글을 사이드카에 남겨 두기만 한다(전에는 마스크만 저장되고 글은 사라져 묶음이 옛 글로 돌았다)
            //  - 결과 그림에 저장 안 한 손 편집(_dirty)이 있으면 다시 식자가 식자 전 원고에서 새로 그려 그 편집을 말없이 지운다 — 글은 사이드카에만, 그림은 아래에서 따로 묻는다
            //  - 복원이 돌고 있으면(_busy) 그 그림을 갈아 끼울 수 없다(백그라운드가 쓰는 Mat이 되돌리기 기록으로 가 닫을 때 해제된다 — 2026-11-14 검수)
            bool askReletter = !(_canRerenderEdited && _pendingPages.Contains(_index)) && !_dirty && !_busy;
            if (askReletter)
            {
                var lr = MessageBox.Show(Loc.T("식자 탭에서 고친 글·위치를 아직 결과물에 반영하지 않았어요.\n다시 식자해서 저장할까요?"), Loc.T("식자 수정"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (lr == MessageBoxResult.Cancel) return false;
                if (lr == MessageBoxResult.Yes) { if (!(ApplyLettering() && Save())) return false; }
                else dropLetter = true;
            }
            else if (!SaveLetterSidecar())
            {
                // 못 썼다(상태 줄에 까닭) — 버릴 길을 준다. 머물기만 하면 출력 폴더가 사라졌을 때(드라이브가 빠짐 등) 장을 넘길 수도 창을 닫을 수도 없다
                if (MessageBox.Show(Loc.T("식자 탭에서 고친 내용을 저장하지 못했어요.\n버리고 계속할까요?"), Loc.T("식자 수정"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                dropLetter = true;
            }
        }
        if (_dirty)
        {
            var r = MessageBox.Show(Loc.T("저장하지 않은 수정이 있어요. 저장할까요?"), Loc.T("결과 보기"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) { if (!Save()) return false; }
            else dropResult = true;
        }
        if (dropMask) _maskDirty = false;
        if (dropLetter || _sidecar == null) _letterDirty = false;
        if (dropResult) _dirty = false;
        return true;
    }

    // ── 브러시 커서: 칠해질 범위를 원 테두리로 (검토 창과 같은 모양, 사용자 요청 2026-10-21) ──
    private readonly System.Windows.Shapes.Ellipse _cursorOuter = new() { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Ellipse _cursorInner = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 1.5, IsHitTestVisible = false };
    private bool _cursorVisible;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (MaskMode)
        {
            if (!HasStoredMask) { TxtStatus.Text = Loc.T("이 장은 저장 마스크가 없어 마스크를 저장하지 않아요 — [지우기]로 결과물에 지운 뒤 [저장]하세요"); return; }
            if (!SaveMaskSidecar()) return;   // 저장 실패 — 상태 줄에 까닭이 있다
            TxtStatus.Text = _canRerenderEdited && _maskSc?.Pending == true
                ? Loc.T("마스크를 저장했어요 — [지우기]로 이 장만, [고친 장 모두 다시 렌더링]으로 고친 장을 한꺼번에 결과물에 반영해요")
                : Loc.T("마스크를 저장했어요 — 다음에 다시 돌려도 이 마스크를 써요");
            return;
        }
        Save();
    }

    /// <summary>지금 보이는 결과 그림을 결과물 자리에 쓴다. 썼으면 true — 못 썼으면(읽는 중·원본과 같은 경로·쓰기 실패) 상태 줄에 까닭을 남기고 false.</summary>
    private bool Save()
    {
        // 페이지를 읽는 동안(_index는 새 장, 그림·마스크는 아직 옛 장)에는 저장하지 않는다 — 옛 장의 그림이 새 장의 결과로 들어간다
        if (_loading) { TxtStatus.Text = Loc.T("페이지를 읽는 중이에요 — 잠시 뒤 다시 저장해 주세요"); return false; }
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
                TxtStatus.Text = Loc.T("원본 원고와 같은 경로라 저장하지 않았어요 — 설정에서 출력 폴더를 따로 지정해 주세요");
                return false;
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
                TxtStatus.Text = Loc.F($"결과물로 저장했어요 → {Path.GetFileName(target)} (원본은 그대로)")
                               + (IsWaitingPage ? Loc.T(" — 검열 제거를 진행하면 이 결과물은 다시 만들어져요") : "");
            }
            else TxtStatus.Text = Loc.F($"저장했어요 → {Path.GetFileName(target)}");
            if (ownerWarning != null) TxtStatus.Text += " · ⚠ " + ownerWarning;
            return true;
        }
        catch (Exception ex) { TxtStatus.Text = Loc.T("저장 실패: ") + ex.Message; return false; }
    }

    /// <summary>이 페이지가 보여 주는(=저장하는) 파일을 바꾼다 — 제목·페이지 콤보도 같이.</summary>
    private void SetViewPath(int idx, string path)
    {
        _refs[idx] = _refs[idx] with { ViewPath = path };
        if (idx == _index) { TxtFile.Text = Path.GetFileName(path); TxtFile.ToolTip = path; }
        RefreshPageLabel(idx);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        base.OnClosing(e);
        ++_loadGen;   // 아직 기다리는 LoadPage가 있으면 깨어나도 아무것도 안 하게
        // 복원이 돌고 있으면 그 Mat·러너는 지금 해제하지 않는다 — 쓰는 중인 네이티브 메모리를 해제하면 죽는다.
        // 러너는 그 작업이 끝날 때 정리하고(DisposeRunnerIfClosed — ComfyUI 사용도 그때 놓는다), Mat은 GC가 거둔다
        _closed = true;
        if (!_busy && _previewInFlight == 0) { _runner?.Dispose(); _runner = null; _redoRunner?.Dispose(); _redoRunner = null; }   // 미리보기가 돌고 있으면 그 continuation의 DisposeRunnerIfClosed가 놓는다
        if (!_busy) _img.Dispose();
        _redoCts?.Cancel();   // 다시 렌더링 중 닫으면 러너를 멈춘다 — 취소 없이 Dispose만 하면 닫힌 창 뒤에서 끝까지 돌아 결과물을 덮었다(하네스 실측, 최종 검수)
        _redoCts?.Dispose(); _redoCts = null;
        _baseImg?.Dispose();
        DropLetterLive();
        ResetMaskState(null);
        while (_undo.Count > 0) _undo.Pop().Dispose();
        foreach (var t in _prefetch.Values) t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
        _prefetch.Clear();
    }

    /// <summary>창을 닫을 때 돌고 있던 수동 작업이 끝났다 — 그때 못 한 러너 정리(ONNX 세션 닫기, ComfyUI 사용 놓기)를 이제 한다.
    /// 놓지 않으면 KeepComfyWarm=false여도 이 창의 작업이 띄웠거나 같이 쓰던 서버가 꺼지지 않는다. 수동 작업의 finally에서 부른다.</summary>
    private void DisposeRunnerIfClosed()
    {
        if (!_closed || _busy || _previewInFlight > 0) return;
        _runner?.Dispose();
        _runner = null;
        _redoRunner?.Dispose();
        _redoRunner = null;
    }
}
