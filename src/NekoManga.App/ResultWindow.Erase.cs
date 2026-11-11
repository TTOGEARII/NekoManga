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

// 검열 지우기 — 재생성 '이 페이지만' 설정, 고른 영역으로 마스크를 만들어 복원, 되돌리기
public partial class ResultWindow
{
    // ── 재생성(SDXL) '이 페이지만' 설정 (2026-10-21, 사용자 요청) ─────────────────────
    // 전역 설정의 사본에 이 페이지 값만 덮어 러너에 넘긴다 — settings.json은 건드리지 않는다. 페이지별로 창이 열려 있는 동안만 기억.
    private sealed record RegenPageValues(string Prompt, string Body, string Negative, double Denoise, int Steps, double Cfg, long Seed, double CnStr, double CnEnd)
    {
        public static RegenPageValues From(NekoManga.Core.AppSettings s) =>
            new(s.RegenPrompt, s.RegenBodyTags, s.RegenNegative, s.RegenDenoise, s.RegenSteps, s.RegenCfg, s.RegenSeed, s.RegenCnStrength, s.RegenCnEnd);
        public NekoManga.Core.AppSettings ApplyTo(NekoManga.Core.AppSettings s)
        {
            var c = s.ShallowClone();
            c.RegenPrompt = Prompt; c.RegenBodyTags = Body; c.RegenNegative = Negative;
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
    }

    /// <summary>이 페이지에 기억된 값이 있으면 그것을, 없으면 ⚙ 설정의 재생성 값을 칸에 채운다.</summary>
    private void LoadRegenFields()
    {
        bool own = _regenPerPage.TryGetValue(_index, out var saved);
        var v = own ? saved! : RegenPageValues.From(App.Settings);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        TxtRgPrompt.Text = v.Prompt; TxtRgBody.Text = v.Body; TxtRgNeg.Text = v.Negative;
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
        var v = new RegenPageValues(TxtRgPrompt.Text.Trim(), TxtRgBody.Text.Trim(), TxtRgNeg.Text.Trim(),
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

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        _shapes.Clear();
        _pending.Clear();
        _brushStrokes.Clear();
        DrawShapes();
        TxtStatus.Text = "선택을 지웠어요";
    }

    // ── 지우기 ──────────────────────────────────────────────
    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_img.Empty() || _busy || _loading) return;

        if (LetterMode) { ApplyLettering(); return; }
        if (MaskMode) { await MaskApplyAsync(); return; }

        if (_pending.Count >= 3) CloseShape();   // 찍다 만 도형도 살려 준다
        if (_shapes.Count == 0 && _brushStrokes.Count == 0)
        {
            TxtStatus.Text = "먼저 지울 곳을 점으로 찍거나 브러시로 칠해 주세요";
            return;
        }

        int grow = int.TryParse(TxtGrow.Text.Trim(), out var g) ? Math.Clamp(g, 0, 40) : 6;
        var tone = RbDarkOnly.IsChecked == true ? MaskTone.Dark
                 : RbWhiteOnly.IsChecked == true ? MaskTone.White
                 : MaskTone.All;
        string engineMode = (CmbEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? "Basic";

        using var mask = BuildMask(tone, grow);
        int px = Cv2.CountNonZero(mask);
        if (px == 0)
        {
            TxtStatus.Text = tone switch
            {
                MaskTone.Dark => "도형 안에서 검은 부분을 찾지 못했어요 — [흰 부분만]이나 [도형 전체]로 바꿔 보세요",
                MaskTone.White => "도형 안에서 흰 부분을 찾지 못했어요 — [도형 전체]로 바꿔 보세요",
                _ => "지울 영역이 비어 있어요",
            };
            return;
        }

        _busy = true;
        BtnApply.IsEnabled = false;
        TxtStatus.Text = $"{px:N0}px 복원 중...";
        var before = _img.Clone();
        var status = new Progress<string>(m => TxtStatus.Text = m);

        try
        {
            var img = _img;
            // 러너(ONNX 세션 포함)는 창 수명 동안 재사용 — 매번 만들면 LaMa 모델(211MB)을
            // 적용할 때마다 다시 로딩해 수 초씩 낭비된다.
            _runner ??= new PipelineRunner(App.Settings);
            var runner = _runner;
            // 재생성이면 이 페이지 전용 프롬프트·수치를 담은 설정 사본을 넘긴다 (전역 설정은 그대로)
            var regenSettings = engineMode == "ComfyRegen" ? ReadRegenFields().ApplyTo(App.Settings) : null;
            await Task.Run(async () =>
                await runner.ManualDecensorAsync(img, mask, engineMode, status, CancellationToken.None, regenSettings));

            _undo.Push(before);
            BtnUndo.IsEnabled = true;
            _dirty = true;
            BtnSave.IsEnabled = true;
            _shapes.Clear();
            _brushStrokes.Clear();
            InvalidateBitmap();   // _img를 제자리에서 고쳤다
            Redraw();
            TxtStatus.Text = $"지웠어요 ({px:N0}px) — 마음에 들면 [저장]을 눌러주세요";
        }
        catch (Exception ex)
        {
            // 복원은 그림을 제자리에서 단계별로 고친다 — 도중에 실패하면 반쯤 고친 그림이 남아 저장될 수 있으므로 되돌린다
            try { if (before.Size() == _img.Size()) { before.CopyTo(_img); InvalidateBitmap(); Redraw(); } } catch { }
            before.Dispose();
            TxtStatus.Text = "실패: " + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnApply.IsEnabled = true;
            DisposeRunnerIfClosed();
        }
    }

    private enum MaskTone { Dark, White, All }

    /// <summary>찍은 도형들로 복원 마스크를 만든다.
    /// '검은/흰 부분만'이면 도형 안에서 그 톤의 픽셀만 골라 — 넉넉히 감싸도 주변 그림이 함께
    /// 뭉개지지 않는다. 가장자리 안티에일리어싱까지 덮도록 grow만큼 부풀린다.</summary>
    private Mat BuildMask(MaskTone tone, int grow)
    {
        var mask = new Mat(_img.Rows, _img.Cols, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(mask, _shapes, Scalar.White);
        // 브러시 획: 두꺼운 선 + 각 점에 원(끝/꺾임이 둥글게) — 미리보기와 같은 모양으로 래스터
        foreach (var (pts, r) in _brushStrokes)
        {
            for (int i = 1; i < pts.Count; i++)
                Cv2.Line(mask, pts[i - 1], pts[i], Scalar.White, r * 2, LineTypes.Link8);
            foreach (var p in pts) Cv2.Circle(mask, p, r, Scalar.White, -1);
        }

        if (tone != MaskTone.All)
        {
            using var gray = new Mat();
            Cv2.CvtColor(_img, gray, ColorConversionCodes.BGR2GRAY);
            using var picked = new Mat();
            // 막대는 거의 순흑/순백이지만 스캔본은 들쑥날쑥하다 — 양쪽 다 넉넉히 잡는다.
            if (tone == MaskTone.Dark) Cv2.Threshold(gray, picked, 120, 255, ThresholdTypes.BinaryInv);
            else Cv2.Threshold(gray, picked, 200, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(mask, picked, mask);
        }

        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (MaskMode) { MaskUndo(); return; }
        if (_undo.Count == 0) return;
        _img.Dispose();
        _img = _undo.Pop();
        BtnUndo.IsEnabled = _undo.Count > 0;
        Redraw();
        TxtStatus.Text = "되돌렸어요";
    }
}
