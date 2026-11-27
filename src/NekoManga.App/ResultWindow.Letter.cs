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

// 식자 탭 — 번역 블록 목록, 선택한 풍선의 글자 속성(글꼴·크기 px·외곽선…), 실제 식자 그림 위의 선택 상자(옮기기·여덟 방향 늘리기·돌리기·제자리 글과 줄 고치기),
//           새 글 넣기, 되돌리기·다시 실행, 다시 식자
public partial class ResultWindow
{
    // ── 식자 탭 ─────────────────────────────────────────────
    private bool LetterMode => TabMode.SelectedItem == TabLetter;

    // 편집 화면: 식자 전 원고에 지금 블록·속성으로 **실제 식자한 그림**(최종 렌더와 같은 TextRenderer.Render)을 깔고, 풍선마다 선택 상자를 띄운다.
    // 전에는 풍선마다 WPF 텍스트박스로 글을 흉내 냈다 — 외곽선이 안 보이고 줄 나눔·크기가 최종 식자와 달라 "고쳐도 안 바뀐다"로 보였다(2026-11-17 사용자 보고).
    private Mat? _letterLive;                                     // 편집 화면에 깔린 그림 — 글을 고치는 중인 풍선(_editBlock)만 빼고 다 그렸다
    private List<TextRenderer.BlockLayout> _liveLayouts = [];     // 그 그림의 풍선별 자리·크기
    private float[] _drawnSizes = [];                             // 그 그림의 풍선별 실제 글자 크기(px) — "자동" 크기를 pt로 보여 주는 데 쓴다
    private bool _letterStale = true;                             // 블록·속성이 바뀌어 그림을 다시 그려야 한다
    private int _editBlock = -1;                                  // 제자리에서 글을 고치는 중인 풍선(텍스트박스가 떠 있다) — 없으면 -1
    private bool _rebuildingBoxes;
    private System.Windows.Threading.DispatcherTimer? _letterTimer;

    // ── 되돌리기·다시 실행: 블록 목록의 스냅샷을 쌓는다(블록은 바뀌지 않는 record라 목록만 베끼면 된다). 쪽을 넘기면 비운다.
    // 아래 [↩ 되돌리기]·Ctrl+Z는 식자 탭에서 이것을 먼저 되돌리고, 다 되돌렸으면 예전처럼 결과 그림([다시 식자] 앞의 그림)을 되돌린다.
    private readonly List<(TextBlockInfo[] Blocks, int Selected)> _letterUndo = [], _letterRedo = [];
    private string _undoKey = "";     // 마지막으로 쌓은 편집의 종류 — 같은 칸을 이어서 고치는 것(글자를 치는 중, ↑↓로 숫자를 올리는 중)은 한 걸음으로 모은다
    private DateTime _undoAt;
    private bool _addingBlock;        // [새 글 넣기]를 누르고 그림에서 자리를 고르는 중
    private bool _selectAllOnEdit;    // 새로 넣은 글 — 제자리 텍스트박스가 뜨면 전체를 골라 바로 덮어쓸 수 있게

    /// <summary>블록을 바꾸기 직전에 지금 상태를 쌓는다. key가 앞의 것과 같고 1.2초 안이면 쌓지 않는다(한 걸음으로 모은다).</summary>
    private void PushLetterUndo(string key = "")
    {
        var now = DateTime.UtcNow;
        if (key.Length > 0 && key == _undoKey && _letterUndo.Count > 0 && (now - _undoAt).TotalSeconds < 1.2) { _undoAt = now; return; }
        _letterUndo.Add(([.. _letterBlocks], CmbBlocks.SelectedIndex));
        if (_letterUndo.Count > 200) _letterUndo.RemoveAt(0);
        _letterRedo.Clear();
        (_undoKey, _undoAt) = (key, now);
        UpdateLetterUndoUi();
    }

    /// <summary>식자 탭의 편집을 한 걸음 되돌리거나(redo = false) 다시 한다. 할 것이 없으면 false.</summary>
    private bool LetterUndo(bool redo)
    {
        var (from, to) = redo ? (_letterRedo, _letterUndo) : (_letterUndo, _letterRedo);
        if (from.Count == 0 || _sidecar == null) return false;
        _editBlock = -1; _addingBlock = false;
        to.Add(([.. _letterBlocks], CmbBlocks.SelectedIndex));
        var (blocks, selected) = from[^1];
        from.RemoveAt(from.Count - 1);
        _letterBlocks = [.. blocks];
        _letterDirty = true;
        _undoKey = "";
        RefreshBlockCombo();
        if (_letterBlocks.Count > 0) SelectBlock(Math.Clamp(selected, 0, _letterBlocks.Count - 1));
        RefreshLetter();
        UpdateLetterUndoUi();
        TxtStatus.Text = redo ? Loc.T("다시 실행했다냥") : Loc.T("되돌렸다냥");
        return true;
    }

    private void UpdateLetterUndoUi()
    {
        if (!LetterMode) return;
        BtnUndo.IsEnabled = _letterUndo.Count > 0 || _undo.Count > 0;
        BtnRedo.IsEnabled = _letterRedo.Count > 0;
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e) { if (!_busy && LetterMode && !_letterPreview) LetterUndo(redo: true); }

    /// <summary>식자 탭의 단축키 — 텍스트박스에 커서가 있어도 받는다(창의 Preview 단계). 받았으면 true.
    /// Ctrl+Z 되돌리기 · Ctrl+Y(Ctrl+Shift+Z) 다시 실행 · Esc 글 고치기/새 글 넣기 끝 · Ctrl+Enter 글 고치기 끝 · Delete 고른 풍선 숨기기(새로 넣은 글은 삭제).</summary>
    private bool LetterKey(KeyEventArgs e)
    {
        if (_busy || _loading || _sidecar == null) return false;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var focus = Keyboard.FocusedElement as TextBox;
        bool pageBox = focus != null && focus.Name.StartsWith("TxtL", StringComparison.Ordinal);   // 페이지 전체 기본의 칸 — 그 칸의 글자 되돌리기를 둔다
        switch (e.Key)
        {
            case Key.Z when ctrl && !shift && !pageBox: return LetterUndo(redo: false);   // 쌓인 것이 없으면 false — 예전처럼 결과 그림을 되돌린다
            case Key.Y when ctrl && !pageBox:
            case Key.Z when ctrl && shift && !pageBox: LetterUndo(redo: true); return true;
            case Key.Escape when _addingBlock: CancelAddBlock(); return true;
            case Key.Escape when _editBlock >= 0: EndLetterEdit(); return true;
            case Key.Enter when ctrl && _editBlock >= 0: EndLetterEdit(); return true;
            case Key.Delete when focus == null && CmbBlocks.SelectedIndex >= 0: BtnBlockHide_Click(this, e); return true;
        }
        return false;
    }

    // ── 새 글 넣기 ──
    private void BtnBlockAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _sidecar == null || _baseImg == null) return;
        if (_letterPreview) { _letterPreview = false; Redraw(); }
        EndLetterEdit();
        _addingBlock = true;
        ImageHost.Cursor = Cursors.Cross;
        TxtStatus.Text = Loc.T("글을 넣을 곳을 클릭하라냥 — Esc로 취소");
    }

    private void CancelAddBlock()
    {
        if (!_addingBlock) return;
        _addingBlock = false;
        ImageHost.Cursor = null;
        TxtStatus.Text = Loc.T("새 글 넣기를 취소했다냥");
    }

    /// <summary>그림의 그 자리(편집 화면 좌표)에 새 글 상자를 넣고 바로 글을 적게 한다. 상자는 쪽 폭의 22% × 높이의 9%로 시작한다 — 손잡이로 늘린다.</summary>
    private void AddBlockAt(System.Windows.Point at)
    {
        _addingBlock = false;
        ImageHost.Cursor = null;
        if (_sidecar == null || _baseImg == null) return;
        var bs = LetterScaleNow(_sidecar, _baseImg);
        double w = _baseImg.Width * 0.22, h = _baseImg.Height * 0.09;
        double x = Math.Clamp(at.X / _scale - w / 2, 0, Math.Max(0, _baseImg.Width - w)), y = Math.Clamp(at.Y / _scale - h / 2, 0, Math.Max(0, _baseImg.Height - h));
        if (bs.CommitBox(default, new LetterBox(x, y, w, h)) is not { } r) return;
        LetterCommon();   // 넣기 전의 블록들로 공통 크기를 붙든다 — 새 글이 다른 풍선의 크기를 바꾸지 않게
        PushLetterUndo();
        _letterBlocks.Add(new TextBlockInfo(r.X1, r.Y1, r.X2, r.Y2, "", Loc.T("새 글"), 20, false, "#000000", ManualRect: true, Added: true));
        _letterDirty = true;
        int i = _letterBlocks.Count - 1;
        RefreshBlockCombo();
        SelectBlock(i);
        _selectAllOnEdit = true;
        BeginLetterEdit(i);
        TxtStatus.Text = Loc.T("새 글을 넣었다냥 — 글을 적고 다른 곳을 클릭하라냥");
    }

    /// <summary>[줄 직접 나누기] — 켜면 지금 그려진 줄들을 글에 그대로 박고(그 뒤로 자동 줄 나눔을 하지 않는다), 끄면 줄바꿈을 지우고 다시 자동으로 나눈다.</summary>
    private void ChkBManualLines_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingBlockUi || !IsLoaded || CmbBlocks.SelectedIndex < 0 || _sidecar == null || _baseImg == null) return;
        int i = CmbBlocks.SelectedIndex;
        var b = _letterBlocks[i];
        bool on = ChkBManualLines.IsChecked == true;
        if (on == b.ManualLines) return;
        string text;
        if (on)
        {
            var bs = LetterScaleNow(_sidecar, _baseImg);
            var lines = TextRenderer.DrawnLines(_baseImg, _letterBlocks, bs.X, bs.Y, LetterStyle(), i, LetterCommon());
            text = lines.Length > 0 ? string.Join("\n", lines) : b.Translation;
        }
        else text = string.Join(" ", b.Translation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        EndLetterEdit();
        SetBlock(i, b with { Translation = text, ManualLines = on });
        _syncingBlockUi = true; TxtBlockText.Text = text; _syncingBlockUi = false;
        RefreshLetter();
        TxtStatus.Text = on ? Loc.T("줄을 지금 모습대로 붙들었다냥 — Enter로 줄을 나누라냥") : Loc.T("줄을 다시 자동으로 나눈다냥");
    }

    /// <summary>페이지 전체 기본(글꼴·크기 배율·색·외곽선)이 바뀌면 편집 화면을 다시 그린다.</summary>
    private void LetterStyle_Changed(object? sender, EventArgs e)
    {
        UpdateSwatches();
        QueueLetterRefresh();
    }

    private double _letterCommon;   // 이 쪽의 붙든 공통 글자 크기(사이드카의 CommonSize, 없으면 처음 그릴 때 구한다) — 0 = 아직 없음

    /// <summary>쪽 공통 글자 크기를 한 번 구해 붙든다 — 그 뒤로는 풍선 하나의 글·자리를 고쳐도 다른 풍선의 자동 크기가 따라 바뀌지 않는다
    /// (전에는 식자할 때마다 풍선들의 중앙값을 다시 구해, 한 풍선을 고치면 손대지 않은 풍선의 글자가 커지거나 작아졌다 — 2026-11-17 사용자 보고).
    /// 처음 부르는 때는 탭을 열어 그림을 처음 그릴 때라 블록은 아직 고치기 전이고, 값은 지금 결과물의 것과 같다.</summary>
    private double LetterCommon()
    {
        if (_letterCommon <= 0 && _sidecar != null && _baseImg != null)
        {
            var bs = LetterScaleNow(_sidecar, _baseImg);
            try { _letterCommon = TextRenderer.CommonSize(_baseImg, _letterBlocks, bs.X, bs.Y, LetterStyle()); }
            catch { }   // 못 구하면 0 — 예전처럼 식자할 때마다 구한다
        }
        return _letterCommon;
    }

    /// <summary>편집 화면에 깔 그림 — 바뀐 것이 있으면 다시 식자해서. 못 그리면(글꼴 오류 등) 지난 그림이나 식자 전 원고.</summary>
    private Mat LetterShown()
    {
        if ((_letterStale || _letterLive == null) && _sidecar != null && _baseImg != null)
        {
            var bs = LetterScaleNow(_sidecar, _baseImg);   // 원본 → 결과 좌표 (축별 — 최종 식자와 같은 비율)
            try
            {
                var live = TextRenderer.Render(_baseImg, _letterBlocks, bs.X, bs.Y, LetterStyle(), out var sizes, out var layouts, _editBlock, LetterCommon());
                _letterLive?.Dispose();
                (_letterLive, _drawnSizes, _liveLayouts, _letterStale) = (live, sizes, layouts, false);
            }
            catch (Exception ex) { TxtStatus.Text = Loc.T("식자 미리보기 실패: ") + ex.Message; }
        }
        return _letterLive ?? _baseImg!;
    }

    /// <summary>편집 화면을 지금 다시 그린다(그림 + 선택 상자).</summary>
    private void RefreshLetter()
    {
        _letterTimer?.Stop();
        _letterStale = true;
        if (!IsLoaded || !LetterMode || _letterPreview || _baseImg == null || _loading) return;
        ImgView.Source = ToBitmapCached(LetterShown());
        RebuildLetterBoxes();
    }

    /// <summary>편집 화면을 조금 뒤에 다시 그린다 — 칸에 숫자를 치는 동안 글자마다 쪽 전체를 다시 식자하지 않게 모은다.</summary>
    private void QueueLetterRefresh()
    {
        _letterStale = true;
        if (!IsLoaded || !LetterMode || _letterPreview) return;
        if (_letterTimer == null)
        {
            _letterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _letterTimer.Tick += (_, _) => RefreshLetter();
        }
        _letterTimer.Stop();
        _letterTimer.Start();
    }

    /// <summary>편집 화면의 그림과 상태를 버린다 — 쪽을 넘기거나 창을 닫을 때.</summary>
    private void DropLetterLive()
    {
        _letterTimer?.Stop();
        _letterLive?.Dispose(); _letterLive = null;
        _liveLayouts = []; _drawnSizes = [];
        _letterStale = true; _editBlock = -1;
        _letterUndo.Clear(); _letterRedo.Clear(); _undoKey = "";
        _addingBlock = false; _selectAllOnEdit = false;
        if (BtnRedo != null) BtnRedo.IsEnabled = false;
    }

    private void RefreshBlockCombo()
    {
        _syncingBlockUi = true;
        CmbBlocks.Items.Clear();
        for (int i = 0; i < _letterBlocks.Count; i++)
        {
            // 글자 수로 자르지 않는다 — 항목은 폭에서 잘리면 XAML(ItemTemplate의 TextTrimming)이 "…"을 붙이고, 온 글은 툴팁에(2026-11-26, 전에는 22자에서 잘랐다).
            // 줄바꿈(직접 나눈 줄의 \n, 번역기가 준 \r\n)과 겹친 빈칸은 빈칸 하나로 — CR이 남으면 TextBlock이 줄을 바꿔 "…"이 둘째 줄에만 붙는다
            var t = string.Join(" ", _letterBlocks[i].Translation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            // ⚠ = 번역을 확인할 줄(다른 번역기로 메움·못 옮김 — 2026-11-27). 목록은 쪽을 열 때 만든다 — 글을 고쳐도 다음에 열 때까지 머리표는 남는다(까닭 줄은 바로 사라진다)
            CmbBlocks.Items.Add($"{i + 1}. {(_letterBlocks[i].NeedsReview() ? "⚠ " : "")}{(_letterBlocks[i].Hidden ? Loc.T("(숨김) ") : "")}{(_letterBlocks[i].Sfx ? Loc.T("[효과음] ") : "")}{t}");
        }
        if (_letterBlocks.Count > 0) CmbBlocks.SelectedIndex = 0;
        TxtBlockText.Text = _letterBlocks.Count > 0 ? _letterBlocks[0].Translation : "";
        if (_letterBlocks.Count > 0) LoadBlockStyleUi(_letterBlocks[0]);
        else ShowBlockSource(null);   // 블록이 없는 장(읽지 못한 장)에서 앞 장의 원문이 남지 않게 — 글 칸을 비우는 윗줄과 짝(검수 2026-11-26)
        _syncingBlockUi = false;
    }

    private void CmbBlocks_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        if (_editBlock >= 0 && _editBlock != i) EndLetterEdit();
        SelectBlock(i);
        DrawShapes();
    }

    /// <summary>풍선 하나를 고른다 — 목록·글 칸·속성 칸을 그 풍선 값으로 채우고 선택 상자를 옮긴다(그림은 다시 그리지 않는다).
    /// 속성 칸도 같이 채워야 한다: 목록만 옮기면 칸에 앞 풍선의 값이 남아, 다음에 칸 하나를 바꿀 때 그 값들이 통째로 이 풍선에 적혔다.</summary>
    private void SelectBlock(int i)
    {
        if (i < 0 || i >= _letterBlocks.Count || i >= CmbBlocks.Items.Count) return;
        bool was = _syncingBlockUi;
        _syncingBlockUi = true;
        CmbBlocks.SelectedIndex = i;
        var b = _letterBlocks[i];
        if (TxtBlockText.Text != b.Translation) TxtBlockText.Text = b.Translation;
        LoadBlockStyleUi(b);
        BtnBlockHide.Content = b.Added ? Loc.T("이 글 삭제") : b.Hidden ? Loc.T("숨김 해제") : Loc.T("이 블록 숨기기");
        ChkBManualLines.IsChecked = b.ManualLines;
        _syncingBlockUi = was;
        StyleBoxes();
    }

    private static string Num(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>#RRGGBB(앞의 #은 없어도 된다)면 그 값, 비었으면 빈 값(= 페이지 설정), 치는 중이라 아직 색이 아니면 fallback.</summary>
    private static string HexOr(string text, string fallback)
    {
        var t = text.Trim();
        if (t.Length == 0) return "";
        if (!t.StartsWith('#')) t = "#" + t;
        return t.Length == 7 && t[1..].All(Uri.IsHexDigit) ? t.ToUpperInvariant() : fallback;
    }

    /// <summary>선택한 풍선의 속성(과 원문 칸)을 칸에 채운다 (_syncingBlockUi 상태에서 호출). 자동인 크기·외곽선은 지금 그려진 값을 보여 준다(ShowAutoValues).</summary>
    private void LoadBlockStyleUi(TextBlockInfo b)
    {
        ShowBlockSource(b);
        ComboBoxItem? font = null;
        foreach (ComboBoxItem it in CmbBFont.Items)
            if (string.Equals((string?)it.Tag, b.FontFamily, StringComparison.OrdinalIgnoreCase)) { font = it; break; }
        if (font == null) CmbBFont.Items.Add(font = new ComboBoxItem { Content = b.FontFamily, Tag = b.FontFamily });   // 이 PC에 없는 글꼴이 적힌 사이드카 — 값은 지킨다
        CmbBFont.SelectedItem = font;
        ChkBSizeAuto.IsChecked = b.SizePx <= 0;
        if (b.SizePx > 0) TxtBSize.Text = Num(b.SizePx, "0.#");
        foreach (ComboBoxItem it in CmbBAlign.Items)
            if ((string?)it.Tag == b.Align) { CmbBAlign.SelectedItem = it; break; }
        foreach (ComboBoxItem it in CmbBBold.Items)
            if ((string?)it.Tag == b.BoldOverride.ToString()) { CmbBBold.SelectedItem = it; break; }
        ChkBItalic.IsChecked = b.Italic;
        ChkBSfx.IsChecked = b.Sfx;
        // 0 = 페이지 기본 따름 — 빈 칸으로 표시
        TxtBLineSp.Text = b.LineSpacing <= 0 ? "" : Num(b.LineSpacing, "0.##");
        TxtBColor.Text = b.ColorOverride;
        ChkBOutAuto.IsChecked = b.OutlinePx < 0;
        if (b.OutlinePx >= 0) TxtBOut.Text = Num(b.OutlinePx, "0.##");
        TxtBOutColor.Text = b.OutlineColor;
        TxtBRot.Text = Num(b.RotationDeg, "0.#");
        ShowAutoValues(force: true);
        UpdateSwatches();
    }

    /// <summary>고른 풍선의 원문(OCR 글)을 번역 글 칸 위에 보여 준다(2026-11-26 사용자 요청 — 번역을 고칠 때 원문을 보며 고치게).
    /// 원문이 없으면([새 글 넣기]로 만든 글, 또는 블록이 없어 null) 칸을 접는다.</summary>
    private void ShowBlockSource(TextBlockInfo? b)
    {
        var src = b?.ShownSource() ?? "";
        TxtBlockSource.Text = src;
        PnlBlockSource.Visibility = src.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowBlockReview(b);
    }

    /// <summary>번역을 확인할 줄이면 그 까닭을 글 칸 위에 보여 준다(2026-11-27 — 고른 번역기가 못 옮겨 다른 번역기로 메운 줄, 끝내 못 옮긴 줄). 전에는 그런 줄도 아무 표시 없이 식자됐다.
    /// 글이 번역기가 낸 그대로일 때만 보인다(TextBlockInfo.NeedsReview) — 고치면 사라지고, 되돌리면 다시 나온다.</summary>
    private void ShowBlockReview(TextBlockInfo? b)
    {
        bool show = b != null && b.NeedsReview();
        TxtBlockReview.Text = !show ? ""
            : b!.Review == ReviewMark.Filled ? Loc.T("⚠ 고른 번역기가 못 옮겨 다른 번역기(Google)로 메운 줄이다냥 — 글을 확인해 줘냥")
            : Loc.T("⚠ 번역하지 못한 줄이다냥 — 글을 고쳐 줘냥");
        TxtBlockReview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>자동인 크기·외곽선 칸에 지금 그려진 값(px)을 적는다 — 그 숫자에서 고쳐 나갈 수 있게. 치는 중인 칸은 건드리지 않는다(force가 아니면).</summary>
    private void ShowAutoValues(bool force = false)
    {
        int i = CmbBlocks.SelectedIndex;
        if (i < 0 || i >= _letterBlocks.Count || _baseImg == null) return;
        var b = _letterBlocks[i];
        float px = i < _drawnSizes.Length ? _drawnSizes[i] : 0;
        bool was = _syncingBlockUi;
        _syncingBlockUi = true;
        if (b.SizePx <= 0 && (force || !TxtBSize.IsKeyboardFocusWithin))
            TxtBSize.Text = px > 0 ? Num(px, "0.#") : "";
        if (b.OutlinePx < 0 && (force || !TxtBOut.IsKeyboardFocusWithin))
            TxtBOut.Text = px > 0 ? Num(TextRenderer.AutoOutlinePx(px, LetterStyle()), "0.#") : "";
        _syncingBlockUi = was;
    }

    /// <summary>"선택한 풍선" 칸이 바뀌면 그 풍선에 저장하고 편집 화면을 다시 그린다.</summary>
    private void BlockStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingBlockUi || !IsLoaded || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        var b = _letterBlocks[i];

        // 크기·외곽선 칸에 직접 적으면 그 값으로 정한다 — "자동"을 끈다
        _syncingBlockUi = true;
        if (ReferenceEquals(sender, TxtBSize)) ChkBSizeAuto.IsChecked = false;
        if (ReferenceEquals(sender, TxtBOut)) ChkBOutAuto.IsChecked = false;
        _syncingBlockUi = false;

        static double D(string t, double fb, double min, double max) =>
            double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, min, max) : fb;

        bool sizeTouched = ReferenceEquals(sender, TxtBSize) || ReferenceEquals(sender, ChkBSizeAuto);
        SetBlock(i, b with
        {
            // 못 읽는 값(치는 중·빈 칸)이면 앞의 값을 둔다
            SizePx = ChkBSizeAuto.IsChecked == true ? 0 : D(TxtBSize.Text, b.SizePx, 4, 1000),
            StyleScale = sizeTouched ? 1.0 : b.StyleScale,   // 옛 "크기×" 배율은 크기를 만지면 푼다 — 자동은 풍선에 맞춘 크기 그대로
            FontFamily = (CmbBFont.SelectedItem as ComboBoxItem)?.Tag as string ?? b.FontFamily,
            Align = (CmbBAlign.SelectedItem as ComboBoxItem)?.Tag as string ?? b.Align,
            BoldOverride = int.TryParse((CmbBBold.SelectedItem as ComboBoxItem)?.Tag as string, out var bo) ? bo : b.BoldOverride,
            Italic = ChkBItalic.IsChecked == true,
            Sfx = ChkBSfx.IsChecked == true,
            LineSpacing = string.IsNullOrWhiteSpace(TxtBLineSp.Text) ? 0 : D(TxtBLineSp.Text, b.LineSpacing, 0.6, 3.0),
            ColorOverride = HexOr(TxtBColor.Text, b.ColorOverride),
            OutlinePx = ChkBOutAuto.IsChecked == true ? -1 : D(TxtBOut.Text, b.OutlinePx, 0, 100),
            OutlineColor = HexOr(TxtBOutColor.Text, b.OutlineColor),
            RotationDeg = D(TxtBRot.Text, b.RotationDeg, -180, 180),
        }, "style:" + ((sender as FrameworkElement)?.Name ?? "") + ":" + i);
        UpdateSwatches();
        QueueLetterRefresh();
        TxtStatus.Text = Loc.T("풍선 속성을 바꿨다냥 — [다시 식자]로 반영하라냥");
    }

    /// <summary>[속성 초기화] — 이 풍선의 글자 속성을 자동·페이지 설정으로 되돌린다(글·자리·숨김은 그대로).</summary>
    private void BtnBlockReset_Click(object sender, RoutedEventArgs e)
    {
        int i = CmbBlocks.SelectedIndex;
        if (i < 0) return;
        SetBlock(i, _letterBlocks[i] with
        {
            SizePx = 0, StyleScale = 1.0, FontFamily = "", Align = "", BoldOverride = -1, Italic = false,
            LineSpacing = 0, ColorOverride = "", OutlinePx = -1, OutlineColor = "", RotationDeg = 0,
        });
        _syncingBlockUi = true;
        LoadBlockStyleUi(_letterBlocks[i]);
        _syncingBlockUi = false;
        RefreshLetter();
        TxtStatus.Text = Loc.T("풍선 속성을 자동·페이지 설정으로 되돌렸다냥");
    }

    /// <summary>숫자 칸에서 ↑↓ — Tag에 적힌 걸음만큼(Shift는 10배) 올리고 내린다 (포토샵의 숫자 칸처럼).</summary>
    private void NumBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || sender is not TextBox box) return;
        NudgeNumBox(box, e.Key == Key.Up ? 1 : -1);
        e.Handled = true;
    }

    /// <summary>커서가 있는 숫자 칸 위에서 휠 — ↑↓와 같다. 커서가 없는 칸은 건드리지 않는다(지나가다 굴린 휠에 값이 바뀌지 않게).</summary>
    private void NumBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocusWithin: true } box) return;
        NudgeNumBox(box, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private static void NudgeNumBox(TextBox box, int dir)
    {
        if (!double.TryParse(box.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var step)) return;
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            v = step < 0.1 ? 1 : 0;   // 빈 줄간격 칸(= 페이지 설정)은 1에서 시작
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 10;
        v = Math.Round(v + dir * step, 2);
        box.Text = Num(v, "0.##");   // → TextChanged → BlockStyle_Changed가 범위를 맞춰 저장한다
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>색 칸 옆의 색 조각을 누르면 색 고르기 창을 띄워 그 칸에 #RRGGBB로 적는다(ColorSwatch — 설정 창과 같이 쓴다).</summary>
    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: TextBox box }) ColorSwatch.Pick(this, box);
    }

    /// <summary>색 조각을 칸의 색으로 칠한다 — 빈 칸(자동·페이지 설정)이나 아직 색이 아닌 글이면 비운다.</summary>
    private void UpdateSwatches()
    {
        foreach (var (sw, box) in new[] { (SwBColor, TxtBColor), (SwBOutColor, TxtBOutColor), (SwLColor, TxtLColor), (SwLOutColor, TxtLOutColor) })
            ColorSwatch.Paint(sw, box);
    }

    private void TxtBlockText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingBlockUi || CmbBlocks.SelectedIndex < 0) return;
        int i = CmbBlocks.SelectedIndex;
        SetBlock(i, _letterBlocks[i] with { Translation = TxtBlockText.Text.Replace("\r\n", "\n") }, "text:" + i);
        ShowBlockReview(_letterBlocks[i]);   // 글을 고치면 '확인할 줄' 까닭이 사라진다
        // 칸은 높이 상한 없이 늘어나므로(2026-11-26) 치는 자리가 패널 아래로 내려가면 바깥 ScrollViewer가 따라오게 — TextBox는 제 안의 스크롤만 맞춘다
        if (TxtBlockText.IsKeyboardFocusWithin)
            try { TxtBlockText.BringIntoView(TxtBlockText.GetRectFromCharacterIndex(TxtBlockText.CaretIndex)); } catch { }
        // 제자리에서 고치는 중이면 그 텍스트박스에도 바로 반영 (박스는 Grid 컨테이너 안에 있다) — 아니면 그림을 다시 그린다
        if (_editBlock == i)
        {
            foreach (var child in EditLayer.Children)
                if (child is Grid host && host.Tag is int idx && idx == i)
                    foreach (var inner in host.Children)
                        if (inner is TextBox tb && tb.Text.Replace("\r\n", "\n") != TxtBlockText.Text.Replace("\r\n", "\n"))
                        { _syncingBlockUi = true; tb.Text = TxtBlockText.Text; _syncingBlockUi = false; }
        }
        else QueueLetterRefresh();
    }

    /// <summary>[이 블록 숨기기] ↔ [숨김 해제] — 지우지 않고 Hidden 플래그만 바꾼다(되살릴 수 있게, 사이드카에도 남는다).</summary>
    private void BtnBlockHide_Click(object sender, RoutedEventArgs e)
    {
        int i = CmbBlocks.SelectedIndex;
        if (i < 0 || i >= _letterBlocks.Count) return;
        if (_letterBlocks[i].Added) { RemoveBlock(i); return; }   // 새로 넣은 글은 원문이 없다 — 숨겨 둘 까닭이 없어 목록에서 뺀다(되돌리기로 되살린다)
        bool hide = !_letterBlocks[i].Hidden;
        SetBlock(i, _letterBlocks[i] with { Hidden = hide });
        if (hide && _editBlock == i) _editBlock = -1;
        RefreshBlockCombo();
        SelectBlock(i);
        RefreshLetter();
        TxtStatus.Text = hide ? Loc.T("블록을 숨겼다냥 — [숨김 해제]로 되살린다냥") : Loc.T("블록을 다시 보인다냥");
    }

    /// <summary>새로 넣은 글을 목록에서 뺀다.</summary>
    private void RemoveBlock(int i)
    {
        if (i < 0 || i >= _letterBlocks.Count) return;
        if (_editBlock >= 0) _editBlock = -1;
        PushLetterUndo();
        _letterBlocks.RemoveAt(i);
        _letterDirty = true;
        RefreshBlockCombo();
        if (_letterBlocks.Count > 0) SelectBlock(Math.Min(i, _letterBlocks.Count - 1));
        RefreshLetter();
        TxtStatus.Text = Loc.T("새 글을 지웠다냥 — Ctrl+Z로 되살린다냥");
    }

    /// <summary>블록 하나를 바꾼다 — 실제로 달라졌을 때만 '고친 것'으로 표시한다(<see cref="_letterDirty"/>). 화면을 맞추느라 같은 값을 다시 넣는 경우(텍스트박스 ↔ 글 칸 동기화)는 세지 않는다.</summary>
    private void SetBlock(int i, TextBlockInfo block, string undoKey = "")
    {
        if (_letterBlocks[i] == block) return;
        PushLetterUndo(undoKey);
        _letterBlocks[i] = block;
        _letterDirty = true;
        _letterStale = true;
    }

    /// <summary>식자 탭의 편집(블록 글·숨김·블록별 스타일)을 사이드카 json에 쓴다 — [다시 식자]와 [지우기] 직전에, 그리고 고친 장(✎)을 떠날 때.
    /// 안 쓰면 다시 렌더링이 원래 블록으로 식자해 편집이 사라진다(v1.8.3까지의 결함). 페이지 전역 스타일(글꼴 등)은 설정값이라 사이드카에 자리가 없다.
    /// 썼거나 쓸 것이 없으면 true — 못 썼으면(읽는 중·쓰기 실패) 상태 줄에 까닭을 남기고 false.</summary>
    private bool SaveLetterSidecar()
    {
        if (_sidecar == null) return true;
        if (_loading) return false;   // 화면의 블록은 떠난 장의 것, _index는 새 장 — 남의 사이드카에 쓰지 않는다
        try
        {
            _sidecar = _sidecar with { Blocks = [.. _letterBlocks], CommonSize = _letterCommon > 0 ? _letterCommon : _sidecar.CommonSize };
            _sidecar.Save(_refs[_index].OutPath);
            _letterDirty = false;
            return true;
        }
        catch (Exception ex) { TxtStatus.Text = Loc.T("식자 편집 저장 실패다냥: ") + ex.Message; return false; }
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
            LetterColorHex = AppSettings.IsAutoColor(TxtLColor.Text) ? AppSettings.LetterColorAuto : Hex(TxtLColor.Text, "#000000"),
            LetterOutlineScale = double.TryParse(TxtLOutScale.Text, out var os) ? Math.Clamp(os, 0, 4) : 1.0,
            LetterOutlineColorHex = Hex(TxtLOutColor.Text, "#FFFFFF"),
            // 페이지 기본 정렬·기울임·줄간격은 설정 전역을 따른다 (블록별 지정이 우선)
            LetterAlign = App.Settings.LetterAlign,
            LetterItalic = App.Settings.LetterItalic,
            LetterLineSpacing = App.Settings.LetterLineSpacing,
            LetterWordSpacing = App.Settings.LetterWordSpacing,
        };
    }

    /// <summary>식자 전 원고(base)에 현재 블록·스타일로 글자를 다시 그린다. 그렸으면 true — 못 그렸으면 상태 줄에 까닭을 남기고 false
    /// (식자 전 원고가 잠겨 있으면 렌더가 던진다 — 전에는 받는 곳이 없어 앱이 꺼졌다, 2026-11-13 점검 9번).</summary>
    private bool ApplyLettering()
    {
        if (_sidecar == null) return false;
        var tmp = Path.Combine(Path.GetTempPath(), $"neko-relett-{Guid.NewGuid():N}.png");
        try
        {
            // 사이드카에 박제한 원본 크기로 환산한다 — 원고를 옮겼거나 지웠어도 편집 화면(LetterShown)처럼 제자리에 그린다
            TextRenderer.RenderBlocks(_sidecar.BasePath, _sidecar.Source, _sidecar.SrcW, _sidecar.SrcH, _letterBlocks, tmp, LetterStyle(), LetterCommon());
            var rendered = ImgIo.Read(tmp);
            if (rendered.Empty()) { rendered.Dispose(); TxtStatus.Text = Loc.T("다시 식자에 실패했다냥"); return false; }
            _undo.Push(_img);
            _img = rendered;
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _letterPreview = true;   // 최종 렌더 결과를 보여준다 — 그림 클릭으로 편집 복귀
            _editBlock = -1; _letterStale = true;
            bool blocksSaved = SaveLetterSidecar();   // 고친 블록이 다음 다시 렌더링에도 쓰이게
            _letterDirty = false;    // 고친 글은 이제 결과 그림(_img)에 들어 있다 — 결과물로 쓰는 것은 [저장]의 몫(_dirty)
            RefreshManualMaskBase(); // 저장 마스크 없는 장의 마스크 보기 바닥도 새 결과물로
            Redraw();
            TxtStatus.Text = Loc.F($"최종 렌더링 미리보기 (블록 {_letterBlocks.Count}개)다냥 — [저장] 또는 그림 클릭으로 다시 편집")
                           + (blocksSaved ? "" : Loc.T(" · ⚠ 고친 글을 저장하지 못했다냥(다시 렌더링하면 옛 글로 돌아간다냥)"));
            return true;
        }
        catch (Exception ex) { TxtStatus.Text = Loc.T("다시 식자 실패다냥: ") + ex.Message; return false; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>풍선 자리마다 선택 상자를 띄운다 (포토샵의 이동 도구처럼). 글은 아래 깔린 그림(LetterShown — 실제 식자)이 보여 준다.
    /// 클릭 = 선택, 끌기 = 옮기기, 오른쪽 아래 손잡이 = 늘리기, 두 번 클릭 = 그 자리에서 글 고치기(그 풍선만 텍스트박스가 뜬다).</summary>
    private void RebuildLetterBoxes()
    {
        _rebuildingBoxes = true;
        try { EditLayer.Children.Clear(); }
        finally { _rebuildingBoxes = false; }
        if (_sidecar == null || _baseImg == null) return;

        var bs = LetterScaleNow(_sidecar, _baseImg);   // 원본 → 결과 좌표 (축별 — 최종 식자와 같은 비율)
        var layouts = _liveLayouts;                    // 깔린 그림과 같은 계산 — 풍선마다의 자리와 글자 크기
        int selected = CmbBlocks.SelectedIndex;

        for (int n = 0; n < _letterBlocks.Count && n < layouts.Count; n++)
        {
            int i = n;
            var b = _letterBlocks[i];
            if (b.Hidden) continue;   // 숨긴 블록은 상자도 없다 (PlanLayout도 Size 0을 준다)

            // 자리(풍선 쪽으로 넓힌 사각형)와 크기는 렌더와 같은 값 — 손으로 잡은 블록은 블록 사각형을 조금 안으로 들인 글 자리(90% × 94%)
            var rect = layouts[i].Rect;
            double w = rect.Width * _scale, h = rect.Height * _scale;
            if (w < 8 || h < 8) continue;

            // 포토샵처럼 옮기고(끌기) 늘리는(오른쪽 아래 모서리) 컨테이너 — 배경이 투명해도 클릭을 받는다
            var host = new Grid { Width = w, Height = h, Tag = i, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            // 이 상자가 놓인 자리(식자할 그림 좌표) — 띄운 자리로 시작해, 옮기거나 늘려서 놓을 때마다 그 자리로 바뀐다.
            // 누르기만 하고 놓았는지를 이것으로 가린다. 확대 배율과 비율은 상자를 띄운 지금 값을 쥐고 있다가 놓을 때 그대로 되돌린다.
            double zoom = _scale;
            LetterBox placed = default;
            void Commit() => placed = CommitBoxRect(host, placed, zoom, bs);
            // 돌린 글은 상자도 같이 돌려 보여 준다(가운데 기준 — 식자도 자리 가운데를 축으로 돌린다). Thumb의 끌기 값은 돌아간 상자 기준이라 화면 방향으로 되돌려 쓴다.
            var turn = new RotateTransform(b.RotationDeg);
            host.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            host.RenderTransform = turn;
            (double X, double Y) ToCanvas(double dx, double dy)
            {
                double r = turn.Angle * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
                return (dx * c - dy * s, dx * s + dy * c);
            }

            host.Children.Add(new System.Windows.Shapes.Rectangle { IsHitTestVisible = false, SnapsToDevicePixels = true });   // 테두리 — StyleBoxes가 칠한다
            // 상자에 마우스를 올리면 원문(OCR 글)이 뜬다 — 풍선을 고르지 않고도 어느 대사인지 본다(2026-11-26 사용자 요청). 원문이 없는 글([새 글 넣기])은 안 띄운다
            if (b.ShownSource() is { Length: > 0 } srcTip)
                host.ToolTip = new System.Windows.Controls.TextBlock
                {
                    Text = srcTip, TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
                    Language = System.Windows.Markup.XmlLanguage.GetLanguage("ja-JP"),   // 원문은 대개 일본어(manga-ocr) — 한국어 화면에서도 한자를 일본 글꼴 자형으로(원문 칸과 같게)
                };

            if (i == _editBlock)
            {
                // 글을 고치는 중인 풍선: 그림에서는 빠져 있고(Render의 skip) 이 텍스트박스가 글을 보여 준다. 줄 나눔은 최종 식자와 조금 다를 수 있다.
                float px = i < _drawnSizes.Length && _drawnSizes[i] > 0 ? _drawnSizes[i] : layouts[i].Size > 0 ? layouts[i].Size : 16;
                var style = LetterStyle();
                bool bold = b.BoldOverride switch { 0 => false, 1 => true, _ => style.LetterBold };
                // 식자(TextRenderer.Render)와 같은 규칙: 블록별 지정 > 쪽 설정(기본 검정, "auto"면 원문 색)
                var fg = !string.IsNullOrWhiteSpace(b.ColorOverride) ? ToMedia(b.ColorOverride, "#000000")
                    : AppSettings.IsAutoColor(style.LetterColorHex) ? ToMedia(b.FontColorHex, "#000000")
                    : ToMedia(style.LetterColorHex, "#000000");
                var alignName = string.IsNullOrEmpty(b.Align) ? style.LetterAlign : b.Align;
                // 텍스트박스에는 지금 그려진 줄들을 그대로 띄운다 — 줄을 직접 나눈 블록은 적은 그대로, 자동인 블록은 식자가 나눈 줄대로.
                // 여기서 글을 건드리면 그 줄들이 블록에 박힌다(ManualLines) — 보이는 줄이 곧 식자되는 줄이다.
                string editText = b.Translation;
                if (!b.ManualLines)
                    try { if (TextRenderer.DrawnLines(_baseImg, _letterBlocks, bs.X, bs.Y, style, i, LetterCommon()) is { Length: > 0 } drawn) editText = string.Join("\n", drawn); }
                    catch { }
                var tb = new TextBox
                {
                    Text = editText,
                    TextWrapping = TextWrapping.NoWrap,
                    TextAlignment = alignName switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = true,
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(b.FontFamily) ? style.LetterFontFamily : b.FontFamily),
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = b.Italic || style.LetterItalic ? FontStyles.Italic : FontStyles.Normal,
                    FontSize = Math.Max(6, px * _scale),
                    Foreground = new SolidColorBrush(fg),
                    Style = null,   // 테마(WPF-UI)의 텍스트박스 틀을 쓰지 않는다 — 어두운 테마에서는 커서가 들어가면 바탕이 짙은 회색이 되어 원고 위의 글이 묻혔다
                    Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    CaretBrush = new SolidColorBrush(fg),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Cursor = Cursors.IBeam,
                    Tag = i,
                };
                tb.TextChanged += LetterBox_TextChanged;
                tb.PreviewKeyDown += (_, a) => { if (a.Key == Key.Escape) { a.Handled = true; EndLetterEdit(); } };
                // 다른 칸으로 커서가 가면 글 고치기를 끝낸다. 상자를 다시 띄우느라(확대 등) 사라지는 것은 끝이 아니다.
                tb.LostKeyboardFocus += (_, _) =>
                {
                    if (_rebuildingBoxes) return;
                    Dispatcher.BeginInvoke(() => { if (_editBlock == i && !tb.IsKeyboardFocusWithin) EndLetterEdit(); });
                };
                host.Children.Add(tb);
                Dispatcher.BeginInvoke(() => { if (_editBlock == i && tb.IsVisible && !tb.IsKeyboardFocusWithin) { tb.Focus(); if (_selectAllOnEdit) { tb.SelectAll(); _selectAllOnEdit = false; } else tb.CaretIndex = tb.Text.Length; } },
                    System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                // 클릭 = 선택, 끌기 = 옮기기, 두 번 클릭 = 글 고치기. 몸통은 보이지 않는 Thumb이 덮어 끌기를 받는다.
                var body = new System.Windows.Controls.Primitives.Thumb { Template = InvisibleThumb, Cursor = Cursors.SizeAll, Tag = "body" };
                double movedX = 0, movedY = 0;
                bool moving = false;
                body.DragStarted += (_, _) => { movedX = movedY = 0; moving = false; };
                body.DragDelta += (_, a) =>
                {
                    movedX += a.HorizontalChange; movedY += a.VerticalChange;
                    if (!moving && Math.Abs(movedX) + Math.Abs(movedY) < 4) return;   // 클릭하다 조금 흔들린 것은 옮기기가 아니다
                    // 처음 움직일 때 그동안 쌓인 만큼을 한꺼번에 옮긴다 — 커서와 상자가 어긋나지 않게
                    var (dx, dy) = ToCanvas(moving ? a.HorizontalChange : movedX, moving ? a.VerticalChange : movedY);
                    moving = true;
                    Canvas.SetLeft(host, Canvas.GetLeft(host) + dx);
                    Canvas.SetTop(host, Canvas.GetTop(host) + dy);
                };
                body.DragCompleted += (_, _) => { if (moving) Commit(); moving = false; };
                host.Children.Add(body);
                // 터널링 단계에서 받는다 — Thumb이 누름을 먹기 전에 선택·두 번 클릭을 본다
                host.PreviewMouseLeftButtonDown += (_, a) =>
                {
                    if (_busy) { a.Handled = true; return; }
                    if (_spaceDown) { a.Handled = true; StartPan(a); return; }   // 손 도구 — 상자 위에서도 화면을 끈다
                    if (_addingBlock) { a.Handled = true; AddBlockAt(a.GetPosition(EditLayer)); return; }   // 새 글 자리를 고르는 중 — 다른 풍선 위여도 그 자리에 넣는다
                    if (_editBlock >= 0)
                    {
                        // 다른 풍선의 글 고치기를 끝낸다 — 상자들이 새로 떠서 이 상자는 사라졌다
                        a.Handled = true;
                        EndLetterEdit();
                        SelectBlock(i);
                        return;
                    }
                    SelectBlock(i);
                    if (a.ClickCount >= 2 && a.OriginalSource is DependencyObject src && IsInside(src, body)) { a.Handled = true; BeginLetterEdit(i); }
                };

                // 여덟 방향 손잡이(포토샵의 변형 상자처럼) — 잡은 쪽만 움직이고 맞은편 변·모서리는 제자리에 있다(돌린 상자에서도).
                foreach (var (hx, hy, tag, cursor) in BoxHandles)
                {
                    var grip = new System.Windows.Controls.Primitives.Thumb
                    {
                        Template = HandleThumb, Width = 11, Height = 11, Tag = tag, Cursor = cursor,
                        HorizontalAlignment = hx < 0 ? HorizontalAlignment.Left : hx > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                        VerticalAlignment = hy < 0 ? VerticalAlignment.Top : hy > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                        Margin = new Thickness(hx < 0 ? -6 : 0, hy < 0 ? -6 : 0, hx > 0 ? -6 : 0, hy > 0 ? -6 : 0),
                    };
                    grip.DragDelta += (_, a) =>
                    {
                        double w0 = host.Width, h0 = host.Height;
                        double w1 = Math.Max(24, w0 + hx * a.HorizontalChange), h1 = Math.Max(20, h0 + hy * a.VerticalChange);
                        var (cx, cy) = ToCanvas(hx * (w1 - w0) / 2, hy * (h1 - h0) / 2);   // 상자 가운데가 옮겨 갈 만큼(화면 방향)
                        Canvas.SetLeft(host, Canvas.GetLeft(host) + cx - (w1 - w0) / 2);
                        Canvas.SetTop(host, Canvas.GetTop(host) + cy - (h1 - h0) / 2);
                        host.Width = w1; host.Height = h1;
                    };
                    grip.DragCompleted += (_, _) => Commit();
                    host.Children.Add(grip);
                }

                // 돌리기 손잡이: 위쪽 가운데에서 조금 떨어진 동그라미. 끌면 상자 가운데를 축으로 돈다 — Shift는 15°씩, 0° 근처는 0°에 붙는다.
                host.Children.Add(new Border
                {
                    Width = 1.5, Height = 16, Background = Brushes.HotPink, IsHitTestVisible = false, Tag = "stem",
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -17, 0, 0),
                });
                var spin = new System.Windows.Controls.Primitives.Thumb
                {
                    Template = RoundThumb, Width = 13, Height = 13, Tag = "rot", Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -29, 0, 0),
                    ToolTip = Loc.T("끌어서 돌린다냥 (Shift: 15°씩)"),
                };
                double armX = 0, armY = 0;   // 상자 가운데에서 손잡이까지(화면 방향)
                spin.DragStarted += (_, _) => (armX, armY) = ToCanvas(0, -(host.Height / 2 + 22));
                spin.DragDelta += (_, a) =>
                {
                    var (dx, dy) = ToCanvas(a.HorizontalChange, a.VerticalChange);
                    armX += dx; armY += dy;
                    double deg = Math.Atan2(armX, -armY) * 180 / Math.PI;   // 위가 0°, 시계 방향이 +
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) deg = Math.Round(deg / 15) * 15;
                    else if (Math.Abs(deg) < 2) deg = 0;
                    turn.Angle = deg;
                };
                spin.DragCompleted += (_, _) =>
                {
                    if (i >= _letterBlocks.Count) return;
                    double deg = Math.Round(turn.Angle, 1);
                    if (Math.Abs(deg - _letterBlocks[i].RotationDeg) < 0.05) return;
                    SetBlock(i, _letterBlocks[i] with { RotationDeg = deg });
                    if (CmbBlocks.SelectedIndex == i) { _syncingBlockUi = true; TxtBRot.Text = Num(deg, "0.#"); _syncingBlockUi = false; }
                    TxtStatus.Text = Loc.T("글을 돌렸다냥 — [다시 식자]로 반영하라냥");
                    Dispatcher.BeginInvoke(RefreshLetter);
                };
                host.Children.Add(spin);
            }

            Canvas.SetLeft(host, rect.X * _scale);
            Canvas.SetTop(host, rect.Y * _scale);
            placed = BoxOf(host, zoom);
            EditLayer.Children.Add(host);
        }
        StyleBoxes();
        ShowAutoValues();
        if (selected >= 0 && selected < _letterBlocks.Count && _editBlock < 0 && TxtBlockText.Text != _letterBlocks[selected].Translation)
        { bool was = _syncingBlockUi; _syncingBlockUi = true; TxtBlockText.Text = _letterBlocks[selected].Translation; _syncingBlockUi = was; }
    }

    /// <summary>상자의 몸통을 덮는 보이지 않는 Thumb의 틀 — 투명한 바탕만 있다(클릭·끌기는 받는다).</summary>
    private static readonly ControlTemplate InvisibleThumb = MakeInvisibleThumb();

    /// <summary>여덟 방향 손잡이 — (가로 쪽, 세로 쪽, 이름, 커서). 오른쪽 아래의 이름 "size"는 손잡이가 하나뿐이던 때부터의 것이다.</summary>
    private static readonly (int X, int Y, string Tag, System.Windows.Input.Cursor Cursor)[] BoxHandles =
    [
        (-1, -1, "nw", Cursors.SizeNWSE), (0, -1, "n", Cursors.SizeNS), (1, -1, "ne", Cursors.SizeNESW),
        (-1, 0, "w", Cursors.SizeWE), (1, 0, "e", Cursors.SizeWE),
        (-1, 1, "sw", Cursors.SizeNESW), (0, 1, "s", Cursors.SizeNS), (1, 1, "size", Cursors.SizeNWSE),
    ];

    /// <summary>손잡이의 틀 — 흰 바탕에 분홍 테두리(어두운 그림·밝은 그림 어디서나 보이게). 돌리기 손잡이는 동그랗다.</summary>
    private static readonly ControlTemplate HandleThumb = MakeHandleThumb(1.5), RoundThumb = MakeHandleThumb(7);

    private static ControlTemplate MakeHandleThumb(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.White);
        border.SetValue(Border.BorderBrushProperty, Brushes.HotPink);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private static ControlTemplate MakeInvisibleThumb()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    /// <summary>선택한 풍선의 상자만 또렷하게(분홍 테두리 + 여덟 방향 손잡이 + 돌리기 손잡이), 나머지는 옅은 점선으로.</summary>
    private void StyleBoxes()
    {
        int selected = CmbBlocks.SelectedIndex;
        foreach (var child in EditLayer.Children)
        {
            if (child is not Grid host || host.Tag is not int i) continue;
            bool on = i == selected;
            foreach (var inner in host.Children)
            {
                if (inner is System.Windows.Shapes.Rectangle frame)
                {
                    frame.Stroke = on ? Brushes.HotPink : Brushes.DeepSkyBlue;
                    frame.StrokeThickness = on ? 2 : 1;
                    frame.StrokeDashArray = on ? null : new DoubleCollection { 4, 3 };
                    frame.Opacity = on ? 1 : 0.55;
                }
                else if (inner is System.Windows.Controls.Primitives.Thumb { Tag: not "body" } grip)
                    grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                else if (inner is Border { Tag: "stem" } stem)
                    stem.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            Canvas.SetZIndex(host, on ? 1 : 0);   // 겹친 상자에서는 고른 것이 위 — 손잡이를 잡을 수 있게
        }
    }

    /// <summary>그 풍선의 글을 그 자리에서 고치기 시작한다 — 그림에서 그 풍선을 빼고 텍스트박스를 띄운다.</summary>
    private void BeginLetterEdit(int i)
    {
        if (i < 0 || i >= _letterBlocks.Count || _letterBlocks[i].Hidden) return;
        _editBlock = i;
        RefreshLetter();
        TxtStatus.Text = Loc.T("글을 고치는 중이다냥 — Enter로 줄 나눔, 다른 곳 클릭이나 Esc로 끝");
    }

    /// <summary>제자리 글 고치기를 끝낸다 — 고친 글로 다시 식자한 그림을 보여 준다.</summary>
    private void EndLetterEdit()
    {
        if (_editBlock < 0) return;
        int i = _editBlock;
        _editBlock = -1;
        _selectAllOnEdit = false;
        // 아무것도 적지 않은 새 글은 남기지 않는다(포토샵이 빈 글 레이어를 지우듯)
        if (i < _letterBlocks.Count && _letterBlocks[i].Added && string.IsNullOrWhiteSpace(_letterBlocks[i].Translation)) { RemoveBlock(i); return; }
        RefreshLetter();
    }

    /// <summary>상자의 화면 좌표를 식자할 그림 좌표로(÷ 확대 배율).</summary>
    private static LetterBox BoxOf(Grid host, double zoom) =>
        new(Canvas.GetLeft(host) / zoom, Canvas.GetTop(host) / zoom, host.Width / zoom, host.Height / zoom);

    /// <summary>상자를 놓았을 때(끌기를 끝냈거나 손잡이의 DragCompleted) — 옮기거나 늘렸으면 그 자리를 원본 좌표로 되돌려 블록에 저장한다.
    /// ManualRect가 켜져 이후 렌더에서 자동 폭 넓히기를 건너뛴다. 돌려주는 값은 상자가 지금 놓인 자리(다음에 놓을 때의 기준).
    /// <para>placed(상자를 띄운 자리, 또는 지난번에 놓은 자리) 그대로면 아무것도 바꾸지 않는다 — 손잡이를 누르기만 하고 놓아도 DragCompleted가 온다.
    /// v1.8.2까지는 그때도 상자 자리를 블록에 저장해서, 자동으로 놓인 블록이 손으로 잡은 블록이 되고 손으로 잡은 블록은 누를 때마다 줄었다.</para>
    /// <para>저장하는 사각형은 상자 그대로가 아니라 상자가 '글 자리'가 되는 블록이다(<see cref="LetterScale.CommitBox"/>) — 렌더가 손으로 잡은 블록을
    /// 안으로 들여 그리는 만큼 넓혀 둬야 다시 띄운 상자와 최종 식자가 놓은 자리에 온다.</para>
    /// <para>shownWith는 이 상자를 띄울 때 쓴 비율이다. 화면 → 결과 좌표(÷ 확대 배율) → 원본 좌표(÷ 축별 비율)를 띄울 때와 같은 값으로 되돌려야
    /// 저장한 블록이 놓은 자리에 그려진다 — 가로 비율 하나로 되돌리면 채색한 쪽에서 세로가 어긋났고, 놓는 순간의 비율을 다시 구하면
    /// 띄울 때 원본이 잠겨 비율 1로 띄운 상자(원본 크기가 0인 사이드카)가 잠금이 풀린 뒤 실제 비율로 되돌려져 쪽 밖 좌표로 저장됐다(글이 알림 없이 빠진다).</para></summary>
    private LetterBox CommitBoxRect(Grid host, LetterBox placed, double zoom, LetterScale shownWith)
    {
        var now = BoxOf(host, zoom);
        if (host.Tag is not int i || i >= _letterBlocks.Count) return now;
        if (shownWith.CommitBox(placed, now) is not { } r) return placed;
        SetBlock(i, _letterBlocks[i] with { X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2, ManualRect = true });
        TxtStatus.Text = Loc.T("블록 자리·크기를 바꿨다냥 — [다시 식자]로 반영하라냥");
        // 놓은 자리에 다시 식자해 보여 준다 — 지금은 손잡이의 이벤트 안이라 상자를 갈아 끼우는 일은 이벤트가 끝난 뒤로 미룬다
        Dispatcher.BeginInvoke(RefreshLetter);
        return now;
    }

    // 같은 쪽(같은 사이드카·같은 식자 전 원고)이면 지난 비율을 다시 쓴다 — 원본 크기(SrcW·SrcH)가 0인 사이드카는 원본 파일을 읽어야
    // 크기를 아는데, 편집 화면은 속성 입력마다 다시 그린다. 쪽이 바뀌면 참조가 달라 저절로 다시 구한다(ToBitmapCached와 같은 방식).
    // 앱이 쓴 식자 사이드카는 처음(v0.6.0)부터 원본 크기를 저장한다 — 0은 손으로 만들거나 고친 사이드카에서만 온다.
    private (LetterSidecar Sidecar, Mat Base, LetterScale Scale)? _letterScale;

    /// <summary>블록 좌표(원본 기준) → 식자 전 원고 좌표의 축별 비율. 최종 식자(TextRenderer.RenderBlocks — [다시 식자], --reletter)와
    /// 같은 계산(<see cref="LetterScale.Resolve"/>)이라 원본 크기가 0인 사이드카(손으로 만든 것)도 최종과 같은 자리에 상자를 띄운다.</summary>
    private LetterScale LetterScaleNow(LetterSidecar sc, Mat baseImg)
    {
        if (_letterScale is { } c && ReferenceEquals(c.Sidecar, sc) && ReferenceEquals(c.Base, baseImg)) return c.Scale;
        LetterScale scale;
        // 원본이 잠겨 있으면 Resolve가 던진다(최종 식자도 그때는 실패한다) — 창을 죽이지 않게 비율 1로 띄우고, 다음에 다시 읽게 기억하지 않는다.
        // 그렇게 띄운 상자를 놓으면 띄울 때의 비율(1)로 되돌린다 — 상자마다 띄울 때의 비율을 쥐고 있다(RebuildLetterBoxes → CommitBoxRect).
        try { scale = LetterScale.Resolve(baseImg.Width, baseImg.Height, sc.SrcW, sc.SrcH, sc.Source); }
        catch { return LetterScale.One; }
        _letterScale = (sc, baseImg, scale);
        return scale;
    }

    private void LetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not int i || i >= _letterBlocks.Count) return;
        if (_syncingBlockUi) return;   // 글 칸에서 친 것을 받아 적는 중 — 블록은 글 칸 쪽이 이미 바꿨다
        // 텍스트박스에 보이는 줄이 곧 식자되는 줄이다 — 여기서 고친 글은 줄을 직접 나눈 것으로 둔다
        SetBlock(i, _letterBlocks[i] with { Translation = tb.Text.Replace("\r\n", "\n"), ManualLines = true }, "text:" + i);
        if (CmbBlocks.SelectedIndex == i)
        { _syncingBlockUi = true; TxtBlockText.Text = tb.Text; ChkBManualLines.IsChecked = true; _syncingBlockUi = false; }
    }

    private static Color ToMedia(string hex, string fallbackHex)
    {
        var h = string.IsNullOrWhiteSpace(hex) ? fallbackHex : hex;
        if (h.StartsWith('#') && h.Length == 7)
            try
            {
                return Color.FromRgb(Convert.ToByte(h[1..3], 16), Convert.ToByte(h[3..5], 16), Convert.ToByte(h[5..7], 16));
            }
            catch { }
        return Colors.Black;
    }
}
