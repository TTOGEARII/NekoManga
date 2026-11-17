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

// 검열 지우기 탭의 버튼 바 — 재생성 '이 페이지만' 설정, [지우기]·[되돌리기] 분기 (마스크 편집·다시 렌더링은 MaskEdit.cs)
public partial class ResultWindow
{
    // ── 재생성(SDXL) '이 페이지만' 설정 (2026-10-21, 사용자 요청) ─────────────────────
    // 전역 설정의 사본에 이 페이지 값만 덮어 러너에 넘긴다 — settings.json은 건드리지 않는다. 페이지별로 창이 열려 있는 동안만 기억.
    private sealed record RegenPageValues(string Prompt, string BodyMale, string BodyFemale, string Negative, double Denoise, int Steps, double Cfg, long Seed, double CnStr, double CnEnd)
    {
        public static RegenPageValues From(NekoManga.Core.AppSettings s) =>
            new(s.RegenPrompt, s.RegenBodyTagsMale, s.RegenBodyTagsFemale, s.RegenNegative, s.RegenDenoise, s.RegenSteps, s.RegenCfg, s.RegenSeed, s.RegenCnStrength, s.RegenCnEnd);
        public NekoManga.Core.AppSettings ApplyTo(NekoManga.Core.AppSettings s)
        {
            var c = s.ShallowClone();
            c.RegenPrompt = Prompt; c.RegenBodyTagsMale = BodyMale; c.RegenBodyTagsFemale = BodyFemale; c.RegenNegative = Negative;
            c.RegenDenoise = Denoise; c.RegenSteps = Steps; c.RegenCfg = Cfg; c.RegenSeed = Seed; c.RegenCnStrength = CnStr; c.RegenCnEnd = CnEnd;
            return c;
        }
    }
    private readonly Dictionary<int, RegenPageValues> _regenPerPage = [];
    // 지금 칸에 든 값이 어느 장의 것인지 — 장을 읽는 동안에는 _index가 이미 새 장인데 칸은 아직 떠난 장의 값이다.
    // _index로 기억하면 빠르게 넘길 때(← → 누르고 있기) 지나친 장에 앞 장의 값이 복사되거나 그 장의 값이 지워졌다(2026-11-13 점검 13번)
    private int _regenFieldsPage = -1;
    private bool RegenMode => (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string == "ComfyRegen";

    private void CmbEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PnlRegen.Visibility = RegenMode ? Visibility.Visible : Visibility.Collapsed;
        PnlSexAll.Visibility = RegenMode && App.Settings.RegenTagsBySex ? Visibility.Visible : Visibility.Collapsed;
        if (RegenMode) LoadRegenFields();
        UpdateMaskInfo(); RefreshRegionPreview();   // 재생성·커스텀이면 다시 그려질 부위 점선
    }

    /// <summary>이 페이지에 기억된 값이 있으면 그것을, 없으면 ⚙ 설정의 재생성 값을 칸에 채운다.</summary>
    private void LoadRegenFields()
    {
        _regenFieldsPage = _index;
        bool own = _regenPerPage.TryGetValue(_index, out var saved);
        var v = own ? saved! : RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        TxtRgPrompt.Text = v.Prompt; TxtRgBodyM.Text = v.BodyMale; TxtRgBodyF.Text = v.BodyFemale; TxtRgNeg.Text = v.Negative;
        TxtRgDenoise.Text = v.Denoise.ToString(ic); TxtRgSteps.Text = v.Steps.ToString(); TxtRgCfg.Text = v.Cfg.ToString(ic);
        TxtRgSeed.Text = v.Seed.ToString(); TxtRgCnStr.Text = v.CnStr.ToString(ic); TxtRgCnEnd.Text = v.CnEnd.ToString(ic);
        RefreshSeedLockUi();
        UpdateRegenHeader(own);
    }

    private void UpdateRegenHeader(bool own) =>
        TxtRegenHdr.Text = (SeedLocked ? Loc.T("🔒 시드 고정 · ") : "") + (own ? Loc.T("✎ 이 페이지 값 사용 중 — 펼쳐서 확인·수정") : Loc.T("⚙ 설정값 그대로 — 펼쳐서 이 페이지만 바꾸기"));

    // ── 재생성 시드의 기록·고정 (2026-11-15, 사용자 "이 시드를 저장하는게 따로 있을까?") ──
    // 재생성이 쓴 시드는 러너가 그 장의 마스크 사이드카에 적는다(MaskSidecar.RegenSeeds, 영역 순서대로). 여기서는 보여 주고, 체크하면 고정(SeedLock)을 사이드카에 바로 적는다 —
    // 고정한 장은 [지우기]·묶음·[시작] 모두 그 시드로 돈다(러너가 재생성 직전에 사이드카에서 읽는다: PipelineRunner.LockedSeedsFor).
    // 위의 '이 페이지만' 칸들과 달리 창을 닫아도 남는다. 마스크를 고친 것이 아니라 저장 시각·'고친 장' 표시는 건드리지 않는다.

    /// <summary>지금 장이 시드를 고정한 장인가 — 고정 표시가 있고 적힌 시드도 있다.</summary>
    private bool SeedLocked => _maskSc is { SeedLock: true } sc && MaskSidecar.ParseSeeds(sc.RegenSeeds).Count > 0;

    /// <summary>지금 장의 시드 기록·고정을 칸에 비춘다 — 장을 읽은 뒤, [지우기]가 끝난 뒤(둘 다 LoadRegenFields), 체크를 바꾼 뒤.</summary>
    private void RefreshSeedLockUi()
    {
        bool has = MaskSidecar.ParseSeeds(_maskSc?.RegenSeeds).Count > 0;
        bool locked = SeedLocked;
        ChkRgSeedLock.IsEnabled = has;
        ChkRgSeedLock.IsChecked = locked;
        TxtRgSeed.IsEnabled = !locked;   // 고정 중에는 위 시드 칸을 쓰지 않는다 — 어느 시드로 도는지 헷갈리지 않게
        string shown = has ? _maskSc!.RegenSeeds!.Replace(",", ", ") : "";
        TxtRgLastSeed.Text = _maskSc == null ? Loc.T("이 장에는 저장 마스크가 없어 시드를 적어 둘 곳이 없어요 (검토를 거쳐 돌린 장만 적혀요)")
            : !has ? Loc.T("아직 적힌 시드가 없어요 — 이 장을 재생성으로 한 번 그리면 적혀요")
            : locked ? Loc.F($"🔒 {shown} — 이 장은 늘 이 시드로 그려요 (위 시드 칸은 쓰지 않아요, 창을 닫아도 남아요)")
            : Loc.F($"지난 재생성 시드 {shown} — 체크하면 다음에도 이 시드로 그려요");
    }

    private void ChkRgSeedLock_Click(object sender, RoutedEventArgs e)
    {
        bool want = ChkRgSeedLock.IsChecked == true;
        // 읽는 중이면 칸은 아직 떠난 장의 것이고, 도는 중이면 러너가 이 사이드카를 곧 다시 쓴다 — 받지 않고 체크를 되돌린다
        if (_loading || _busy || _maskSc == null) { RefreshSeedLockUi(); return; }
        try
        {
            var outPath = _refs[_index].OutPath;
            // 디스크의 지금 값에 고정 여부만 바꿔 적는다 — 이 창이 장을 읽은 뒤에 러너가 고친 것(확인·'고친 장' 표시·시드)을 읽어 둔 옛 값으로 되돌리지 않게
            if (MaskSidecar.Load(outPath) is not { } onDisk)
                throw new IOException(Loc.T("마스크 정보(.mask.json)를 읽지 못했어요 — 잠시 뒤 다시 해 주세요"));
            // 같은 원고의 사이드카인가 — 이 창이 읽은 뒤에 러너가 원고 경로를 고쳐 적었을 수 있어(폴더를 옮긴 원고) 경로가 다르면 이름·길이로 본다
            if (!string.Equals(onDisk.Source, _maskSc.Source, StringComparison.OrdinalIgnoreCase) && !onDisk.MatchesSource(WaitKey))
                throw new InvalidOperationException(Loc.T("이 자리의 마스크 정보가 다른 원고의 것으로 바뀌었어요 — 장을 다시 열어 주세요"));
            _maskSc = _maskSc with { RegenSeeds = onDisk.RegenSeeds, SeedLock = onDisk.SeedLock };   // 칸은 디스크의 지금 값을 따른다 — 아래에서 실패해도 꺼진 체크가 켜진 채 남지 않게
            if (want && MaskSidecar.ParseSeeds(onDisk.RegenSeeds).Count == 0) throw new InvalidOperationException(Loc.T("이 장에 적힌 시드가 없어요 (방금 다시 그리는 중이면 끝난 뒤에 해 주세요)"));
            (onDisk with { SeedLock = want }).Save(outPath);
            _maskSc = _maskSc with { SeedLock = want };
            TxtStatus.Text = want ? Loc.F($"이 장의 시드를 고정했어요 ({onDisk.RegenSeeds!.Replace(",", ", ")}) — [지우기]·묶음·[시작] 모두 이 시드로 그려요")
                                  : Loc.T("시드 고정을 풀었어요 — 다음 재생성은 위 시드 칸의 값으로 그려요 (-1 = 랜덤)");
        }
        catch (Exception ex) { TxtStatus.Text = Loc.T("시드 고정을 저장하지 못했어요: ") + ex.Message; }
        RefreshSeedLockUi();
        UpdateRegenHeader(_regenPerPage.ContainsKey(_index));
    }

    /// <summary>칸의 값을 읽어(범위 밖은 설정값으로) 이 페이지 값으로 기억하고 돌려준다. 설정과 같으면 기억에서 뺀다.</summary>
    private RegenPageValues ReadRegenFields()
    {
        var d = RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        double D(string t, double def, double lo, double hi) => double.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, ic, out var x) ? Math.Clamp(x, lo, hi) : def;
        int I(string t, int def, int lo, int hi) => int.TryParse(t.Trim(), out var x) ? Math.Clamp(x, lo, hi) : def;
        var v = new RegenPageValues(TxtRgPrompt.Text.Trim(), TxtRgBodyM.Text.Trim(), TxtRgBodyF.Text.Trim(), TxtRgNeg.Text.Trim(),
            D(TxtRgDenoise.Text, d.Denoise, 0.05, 1.0), I(TxtRgSteps.Text, d.Steps, 1, 150), D(TxtRgCfg.Text, d.Cfg, 0, 30),
            long.TryParse(TxtRgSeed.Text.Trim(), out var seed) ? seed : -1, D(TxtRgCnStr.Text, d.CnStr, 0, 2), D(TxtRgCnEnd.Text, d.CnEnd, 0, 1));
        int page = _regenFieldsPage >= 0 ? _regenFieldsPage : _index;   // 칸의 값이 속한 장
        if (v == d) _regenPerPage.Remove(page); else _regenPerPage[page] = v;
        UpdateRegenHeader(v != d);
        return v;
    }

    /// <summary>페이지를 떠나기 전에 칸 값을 그 페이지 몫으로 기억해 둔다.</summary>
    private void StashRegenFields()
    {
        if (IsLoaded && PnlRegen.Visibility == Visibility.Visible && !_img.Empty() && _regenFieldsPage >= 0) ReadRegenFields();
    }

    private void BtnRegenReset_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;   // 읽는 중 — 칸은 아직 떠난 장의 값이다
        _regenPerPage.Remove(_index);
        LoadRegenFields();
        TxtStatus.Text = Loc.T("이 페이지의 재생성 값을 설정값으로 되돌렸어요");
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty() || _busy || _loading) return;

        if (LetterMode) { ApplyLettering(); return; }   // 실패하면 상태 줄에 까닭이 있다
        await DecensorApplyAsync();   // 검열 지우기 탭 — 검토 대기면 진행 알림, 아니면 이 장만 다시 렌더링
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (MaskMode) { MaskUndo(); return; }
        if (LetterMode && !_letterPreview && LetterUndo(redo: false)) return;   // 식자 탭: 풍선 편집을 먼저 되돌린다 — 다 되돌렸으면 아래(결과 그림)
        if (_undo.Count == 0) return;
        _img.Dispose();
        _img = _undo.Pop();
        BtnUndo.IsEnabled = _undo.Count > 0 || (LetterMode && _letterUndo.Count > 0);
        RefreshManualMaskBase();   // 저장 마스크 없는 장의 마스크 보기 바닥도 되돌린 결과물로
        Redraw();
        TxtStatus.Text = Loc.T("되돌렸어요");
    }
}
