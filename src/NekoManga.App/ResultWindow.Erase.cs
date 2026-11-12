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
    private bool RegenMode => (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string == "ComfyRegen";

    private void CmbEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PnlRegen.Visibility = RegenMode ? Visibility.Visible : Visibility.Collapsed;
        if (RegenMode) LoadRegenFields();
        UpdateMaskInfo(); RefreshRegionPreview();   // 재생성·커스텀이면 다시 그려질 부위 점선
    }

    /// <summary>이 페이지에 기억된 값이 있으면 그것을, 없으면 ⚙ 설정의 재생성 값을 칸에 채운다.</summary>
    private void LoadRegenFields()
    {
        bool own = _regenPerPage.TryGetValue(_index, out var saved);
        var v = own ? saved! : RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        TxtRgPrompt.Text = v.Prompt; TxtRgBodyM.Text = v.BodyMale; TxtRgBodyF.Text = v.BodyFemale; TxtRgNeg.Text = v.Negative;
        TxtRgDenoise.Text = v.Denoise.ToString(ic); TxtRgSteps.Text = v.Steps.ToString(); TxtRgCfg.Text = v.Cfg.ToString(ic);
        TxtRgSeed.Text = v.Seed.ToString(); TxtRgCnStr.Text = v.CnStr.ToString(ic); TxtRgCnEnd.Text = v.CnEnd.ToString(ic);
        UpdateRegenHeader(own);
    }

    private void UpdateRegenHeader(bool own) =>
        TxtRegenHdr.Text = own ? "✎ 이 페이지 값 사용 중 — 펼쳐서 확인·수정" : "⚙ 설정값 그대로 — 펼쳐서 이 페이지만 바꾸기";

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
        if (v == d) _regenPerPage.Remove(_index); else _regenPerPage[_index] = v;
        UpdateRegenHeader(v != d);
        return v;
    }

    /// <summary>페이지를 떠나기 전에 칸 값을 그 페이지 몫으로 기억해 둔다.</summary>
    private void StashRegenFields()
    {
        if (IsLoaded && PnlRegen.Visibility == Visibility.Visible && !_img.Empty()) ReadRegenFields();
    }

    private void BtnRegenReset_Click(object sender, RoutedEventArgs e)
    {
        _regenPerPage.Remove(_index);
        LoadRegenFields();
        TxtStatus.Text = "이 페이지의 재생성 값을 설정값으로 되돌렸어요";
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty() || _busy || _loading) return;

        if (LetterMode) { ApplyLettering(); return; }
        await DecensorApplyAsync();   // 검열 지우기 탭 — 검토 대기면 진행 알림, 아니면 이 장만 다시 렌더링
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (MaskMode) { MaskUndo(); return; }
        if (_undo.Count == 0) return;
        _img.Dispose();
        _img = _undo.Pop();
        BtnUndo.IsEnabled = _undo.Count > 0;
        RefreshManualMaskBase();   // 저장 마스크 없는 장의 마스크 보기 바닥도 되돌린 결과물로
        Redraw();
        TxtStatus.Text = "되돌렸어요";
    }
}
