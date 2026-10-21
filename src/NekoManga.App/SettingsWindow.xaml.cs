using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace NekoManga.App;

public partial class SettingsWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = App.Settings;

        TxtMegapixels.Text = s.ColorMegapixels.ToString(CultureInfo.InvariantCulture);
        TxtSteps.Text = s.ColorSteps.ToString();
        TxtCfg.Text = s.ColorCfg.ToString(CultureInfo.InvariantCulture);
        TxtDenoise.Text = s.ColorDenoise.ToString(CultureInfo.InvariantCulture);
        SelectCombo(CmbSampler, s.ColorSampler);
        TxtLineart.Text = s.ColorLineartOpacity.ToString();
        TxtSeed.Text = s.ColorSeed.ToString();
        TxtManualTags.Text = s.ColorManualTags;
        TxtColorRefImage.Text = s.ColorRefImage;
        TxtColorRefTags.Text = s.ColorRefTags;
        TxtRefStrength.Text = s.ColorRefStrength.ToString(CultureInfo.InvariantCulture);
        LoadEngineTab(s);
        if (s.Theme == "Dark") RbThemeDark.IsChecked = true; else RbThemeLight.IsChecked = true;
        TxtExcludeTags.Text = s.ColorExcludeTags;
        TxtNegative.Text = s.ColorNegative;

        SelectComboByTag(CmbDecensorTarget, s.DecensorTarget);
        TxtBarAspect.Text = s.DecensorBarMinAspect.ToString(CultureInfo.InvariantCulture);
        TxtBarMinArea.Text = s.DecensorBarMinArea.ToString();
        TxtBlockSize.Text = s.DecensorBlockSize.ToString();
        TxtThreshold.Text = s.DecensorThreshold.ToString();
        TxtMaskGrow.Text = s.DecensorMaskGrow.ToString();
        ChkBubbleFilter.IsChecked = s.DecensorBubbleFilter;
        ChkReview.IsChecked = s.DecensorReview;
        ChkCollect.IsChecked = s.CollectCorrections;
        ChkSelfTrain.IsChecked = s.EnableSelfTraining;
        TxtSelfEpochs.Text = s.SelfTrainEpochs.ToString();
        TxtSelfLr.Text = s.SelfTrainLr.ToString(CultureInfo.InvariantCulture);
        TxtSelfFreeze.Text = s.SelfTrainFreeze.ToString();
        TxtSelfMinPages.Text = s.SelfTrainMinPages.ToString();
        ChkSelfTrain_Changed(ChkSelfTrain, null!);
        SelectComboByTag(CmbDecensorMode, s.DecensorMode);
        CmbDecensorMode_Changed(CmbDecensorMode, null!);
        TxtCustomDecensor.Text = s.CustomDecensorWorkflow;
        TxtCustomReach.Text = s.CustomDecensorReach.ToString();
        TxtColorCustom.Text = s.ColorCustomWorkflow;
        BindRanges();

        // 고급 옵션 — 검열 제거
        TxtAdvLamaGrow.Text = s.LamaMaskGrow.ToString();
        TxtAdvLamaBlur.Text = s.LamaMaskBlur.ToString();
        LoadRegen(s);

        // 엔진 — 그래픽카드 종류 (ComfyUI 포터블 빌드 선택, 2026-10-21 AMD 지원)
        foreach (ComboBoxItem it in CmbGpuVendor.Items) if ((string)it.Tag == s.GpuVendor) { CmbGpuVendor.SelectedItem = it; break; }
        if (CmbGpuVendor.SelectedItem == null) CmbGpuVendor.SelectedIndex = 0;
        _ = Task.Run(SystemCheck.DetectVendor).ContinueWith(t =>
        {
            var (v, name) = t.Result;
            var label = v switch { "nvidia" => "NVIDIA (RTX 20 이상 → CUDA 13 빌드)", "nvidia-old" => "NVIDIA 구형 (GTX 10 이하 → CUDA 12.6 빌드)", "amd" => "AMD Radeon (→ ROCm 빌드, Windows 11 필요)", _ => "전용 그래픽카드 없음" };
            Dispatcher.Invoke(() => TxtGpuDetected.Text = "감지됨: " + (name.Length > 0 ? name + " — " : "") + label);
        });

        // 고급 옵션 — 엔진·채색 워크플로
        TxtAdvPort.Text = s.ComfyPort.ToString();
        TxtAdvExtraArgs.Text = s.ComfyExtraArgs;
        TxtAdvCnLineStr.Text = s.CnLineartStrength.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnLineEnd.Text = s.CnLineartEnd.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnScrStr.Text = s.CnScribbleStrength.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnScrStart.Text = s.CnScribbleStart.ToString(CultureInfo.InvariantCulture);
        TxtAdvPreRes.Text = s.PreprocResolution.ToString();
        TxtAdvXdog.Text = s.ScribbleXdogThreshold.ToString();
        TxtAdvIpaEnd.Text = s.IpaEndAt.ToString(CultureInfo.InvariantCulture);

        SelectComboByTag(CmbTranslator, s.TranslateEngine);
        SelectComboByTag(CmbSource, s.TranslateSource);
        SelectComboByTag(CmbTarget, s.TranslateTarget);

        // 식자 글꼴: 설치된 글꼴 목록을 채우고 현재 값을 고른다 (편집 가능 — 직접 입력도 됨)
        foreach (var f in System.Drawing.FontFamily.Families.Select(f => f.Name).OrderBy(n => n))
            CmbLetterFont.Items.Add(f);
        CmbLetterFont.Text = s.LetterFontFamily;
        ChkLetterBold.IsChecked = s.LetterBold;
        TxtLetterSize.Text = s.LetterSizeScale.ToString(CultureInfo.InvariantCulture);
        TxtLetterColor.Text = s.LetterColorHex;
        TxtLetterOutline.Text = s.LetterOutlineScale.ToString(CultureInfo.InvariantCulture);
        TxtLetterOutlineColor.Text = s.LetterOutlineColorHex;
        SelectComboByTag(CmbLetterAlign, s.LetterAlign);
        TxtLetterLineSp.Text = s.LetterLineSpacing.ToString(CultureInfo.InvariantCulture);
        ChkLetterItalic.IsChecked = s.LetterItalic;
        TxtDeepLKey.Text = s.DeepLApiKey;
        TxtDeepSeekKey.Text = s.DeepSeekApiKey;
        SetComboText(CmbDeepSeekModel, s.DeepSeekModel, TranslatorFactory.DeepSeekDefaultModel);
        TxtGrokKey.Text = s.GrokApiKey;
        SetComboText(CmbGrokModel, s.GrokModel, TranslatorFactory.GrokDefaultModel);
        UpdateTranslatorPanels();

        TxtRuntimeRoot.Text = s.RuntimeRoot;
        TxtOutputRoot.Text = s.OutputRoot;
        TxtLinkedComfy.Text = s.LinkedComfyRoot;
        TxtCivitaiKey.Text = s.CivitaiApiKey;

        TxtAboutVersion.Text = $"버전 v{new UpdateService().CurrentVersion}";
        RefreshCensorAiCard();

        if ((Application.Current as App)?.DemoSettings == true)
            SettingsTabs.SelectedIndex = 2; // 번역 탭 (UI 데모용)
        Closing += (_, _) => Apply();
    }

    private void Apply()
    {
        var s = App.Settings;
        s.ColorMegapixels = ParseD(TxtMegapixels.Text, s.ColorMegapixels, 0.3, 4.0);
        s.ColorSteps = ParseI(TxtSteps.Text, s.ColorSteps, 5, 100);
        s.ColorCfg = ParseD(TxtCfg.Text, s.ColorCfg, 1, 15);
        s.ColorDenoise = ParseD(TxtDenoise.Text, s.ColorDenoise, 0.1, 1.0);
        s.ColorSampler = ComboText(CmbSampler, s.ColorSampler);
        s.ColorLineartOpacity = ParseI(TxtLineart.Text, s.ColorLineartOpacity, 0, 100);
        s.ColorSeed = long.TryParse(TxtSeed.Text, out var seed) ? seed : -1;
        s.ColorManualTags = TxtManualTags.Text.Trim();
        s.ColorRefImage = TxtColorRefImage.Text.Trim();
        s.ColorRefTags = TxtColorRefTags.Text.Trim();
        s.ColorRefStrength = ParseD(TxtRefStrength.Text, s.ColorRefStrength, 0, 1.5);

        s.PrewarmComfyOnLaunch = ChkPrewarm.IsChecked == true;
        s.KeepComfyWarm = ChkKeepWarm.IsChecked == true;
        if (CmbCheckpoint.SelectedItem is string ckpt && ckpt.Length > 0) s.ColorCheckpoint = ckpt;
        if (CmbControlNet.SelectedItem is string cn && cn.Length > 0) s.ColorControlNet = cn;
        s.ColorExcludeTags = TxtExcludeTags.Text.Trim();
        s.ColorNegative = TxtNegative.Text.Trim();

        s.DecensorTarget = ComboTag(CmbDecensorTarget, s.DecensorTarget);
        s.DecensorBarMinAspect = ParseD(TxtBarAspect.Text, s.DecensorBarMinAspect, 1.0, 10.0);
        s.DecensorBarMinArea = ParseI(TxtBarMinArea.Text, s.DecensorBarMinArea, 50, 500_000);
        s.DecensorBlockSize = ParseI(TxtBlockSize.Text, s.DecensorBlockSize, 2, 64);
        s.DecensorThreshold = ParseI(TxtThreshold.Text, s.DecensorThreshold, 1, 80);
        s.DecensorMaskGrow = ParseI(TxtMaskGrow.Text, s.DecensorMaskGrow, 0, 64);
        s.DecensorBubbleFilter = ChkBubbleFilter.IsChecked == true;
        s.DecensorReview = ChkReview.IsChecked == true;
        s.CollectCorrections = ChkCollect.IsChecked == true;
        s.EnableSelfTraining = ChkSelfTrain.IsChecked == true;
        s.SelfTrainEpochs = ParseI(TxtSelfEpochs.Text, s.SelfTrainEpochs, 1, 50);
        s.SelfTrainLr = ParseD(TxtSelfLr.Text, s.SelfTrainLr, 0.00001, 0.01);
        s.SelfTrainFreeze = ParseI(TxtSelfFreeze.Text, s.SelfTrainFreeze, 0, 22);
        s.SelfTrainMinPages = ParseI(TxtSelfMinPages.Text, s.SelfTrainMinPages, 5, 500);
        s.DecensorMode = ComboTag(CmbDecensorMode, s.DecensorMode);
        s.CustomDecensorWorkflow = TxtCustomDecensor.Text.Trim();
        s.CustomDecensorReach = ParseI(TxtCustomReach.Text, s.CustomDecensorReach, 0, 512);
        s.ColorCustomWorkflow = TxtColorCustom.Text.Trim();

        // 고급 옵션 — 검열 제거
        s.LamaMaskGrow = ParseI(TxtAdvLamaGrow.Text, s.LamaMaskGrow, 0, 64);
        s.LamaMaskBlur = ParseI(TxtAdvLamaBlur.Text, s.LamaMaskBlur, 0, 64);
        s.RegenCheckpoint = string.IsNullOrWhiteSpace(TxtRegenCkpt.Text) ? s.RegenCheckpoint : TxtRegenCkpt.Text.Trim();
        s.RegenControlNet = string.IsNullOrWhiteSpace(TxtRegenCn.Text) ? s.RegenControlNet : TxtRegenCn.Text.Trim();
        s.RegenPrompt = TxtRegenPrompt.Text.Trim();
        s.RegenBodyTags = TxtRegenBody.Text.Trim();
        s.RegenNegative = TxtRegenNeg.Text.Trim();
        s.RegenDenoise = ParseD(TxtRegenDenoise.Text, s.RegenDenoise, 0.05, 1.0);
        s.RegenSteps = ParseI(TxtRegenSteps.Text, s.RegenSteps, 1, 150);
        s.RegenCfg = ParseD(TxtRegenCfg.Text, s.RegenCfg, 0, 30);
        s.RegenCnStrength = ParseD(TxtRegenCnStr.Text, s.RegenCnStrength, 0, 2);
        s.RegenCnEnd = ParseD(TxtRegenCnEnd.Text, s.RegenCnEnd, 0, 1);
        s.RegenMegapixels = ParseD(TxtRegenMp.Text, s.RegenMegapixels, 0, 8);

        s.GpuVendor = CmbGpuVendor.SelectedItem is ComboBoxItem gv ? (string)gv.Tag : "auto";

        // 고급 옵션 — 엔진·채색 워크플로
        s.ComfyPort = ParseI(TxtAdvPort.Text, s.ComfyPort, 1024, 65535);
        s.ComfyExtraArgs = TxtAdvExtraArgs.Text.Trim();
        s.CnLineartStrength = ParseD(TxtAdvCnLineStr.Text, s.CnLineartStrength, 0, 2);
        s.CnLineartEnd = ParseD(TxtAdvCnLineEnd.Text, s.CnLineartEnd, 0, 1);
        s.CnScribbleStrength = ParseD(TxtAdvCnScrStr.Text, s.CnScribbleStrength, 0, 2);
        s.CnScribbleStart = ParseD(TxtAdvCnScrStart.Text, s.CnScribbleStart, 0, 1);
        s.PreprocResolution = ParseI(TxtAdvPreRes.Text, s.PreprocResolution, 256, 4096);
        s.ScribbleXdogThreshold = ParseI(TxtAdvXdog.Text, s.ScribbleXdogThreshold, 1, 128);
        s.IpaEndAt = ParseD(TxtAdvIpaEnd.Text, s.IpaEndAt, 0, 1);

        s.TranslateEngine = ComboTag(CmbTranslator, s.TranslateEngine);
        s.TranslateSource = ComboTag(CmbSource, s.TranslateSource);
        s.TranslateTarget = ComboTag(CmbTarget, s.TranslateTarget);
        s.DeepLApiKey = TxtDeepLKey.Text.Trim();
        s.DeepSeekApiKey = TxtDeepSeekKey.Text.Trim();
        s.DeepSeekModel = (CmbDeepSeekModel.Text ?? "").Trim();
        s.GrokApiKey = TxtGrokKey.Text.Trim();
        s.GrokModel = (CmbGrokModel.Text ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(CmbLetterFont.Text)) s.LetterFontFamily = CmbLetterFont.Text.Trim();
        s.LetterBold = ChkLetterBold.IsChecked == true;
        s.LetterSizeScale = ParseD(TxtLetterSize.Text, s.LetterSizeScale, 0.3, 3.0);
        s.LetterColorHex = NormalizeHex(TxtLetterColor.Text);
        s.LetterOutlineScale = ParseD(TxtLetterOutline.Text, s.LetterOutlineScale, 0, 4.0);
        s.LetterOutlineColorHex = NormalizeHex(TxtLetterOutlineColor.Text, "#FFFFFF");
        s.LetterAlign = ComboTag(CmbLetterAlign, s.LetterAlign);
        s.LetterLineSpacing = ParseD(TxtLetterLineSp.Text, s.LetterLineSpacing, 0.6, 3.0);
        s.LetterItalic = ChkLetterItalic.IsChecked == true;

        s.OutputRoot = TxtOutputRoot.Text.Trim();
        s.LinkedComfyRoot = TxtLinkedComfy.Text.Trim();
        s.CivitaiApiKey = TxtCivitaiKey.Text.Trim();
        s.Save();
    }

    /// <summary>숫자 칸의 범위를 입력 시점에 강제한다 (사용자 요청 10-20: min·max를 못 넘게).
    /// 칸을 벗어날 때(LostFocus) 값을 범위 안으로 잘라 다시 적고, 숫자가 아니면 마지막 유효값으로 되돌린다.
    /// 범위는 저장 코드(ParseI/ParseD)의 것과 같아야 한다 — 여기서 잘린 값이 그대로 저장된다.</summary>
    private void BindRanges()
    {
        void R(System.Windows.Controls.TextBox tb, double min, double max, bool isInt)
        {
            string Fmt(double v) => isInt ? ((int)Math.Round(v)).ToString() : v.ToString(CultureInfo.InvariantCulture);
            tb.ToolTip = $"{Fmt(min)} ~ {Fmt(max)}";
            tb.Tag = tb.Text;
            tb.LostFocus += (_, _) =>
            {
                if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                { tb.Text = tb.Tag as string ?? Fmt(min); return; }
                tb.Text = Fmt(Math.Clamp(v, min, max));
                tb.Tag = tb.Text;
            };
        }
        // 채색
        R(TxtMegapixels, 0.3, 4.0, false); R(TxtSteps, 5, 100, true); R(TxtCfg, 1, 15, false); R(TxtDenoise, 0.1, 1.0, false);
        R(TxtLineart, 0, 100, true); R(TxtRefStrength, 0, 1.5, false);
        // 검열 제거
        R(TxtBarAspect, 1.0, 10.0, false); R(TxtBarMinArea, 50, 500_000, true); R(TxtBlockSize, 2, 64, true); R(TxtThreshold, 1, 80, true);
        R(TxtMaskGrow, 0, 64, true); R(TxtCustomReach, 0, 512, true); R(TxtAdvLamaGrow, 0, 64, true); R(TxtAdvLamaBlur, 0, 64, true);
        R(TxtRegenDenoise, 0.05, 1.0, false); R(TxtRegenSteps, 1, 150, true); R(TxtRegenCfg, 0, 30, false);
        R(TxtRegenCnStr, 0, 2, false); R(TxtRegenCnEnd, 0, 1, false); R(TxtRegenMp, 0, 8, false);
        // 엔진
        R(TxtAdvPort, 1024, 65535, true); R(TxtAdvCnLineStr, 0, 2, false); R(TxtAdvCnLineEnd, 0, 1, false);
        R(TxtAdvCnScrStr, 0, 2, false); R(TxtAdvCnScrStart, 0, 1, false); R(TxtAdvPreRes, 256, 4096, true);
        R(TxtAdvXdog, 1, 128, true); R(TxtAdvIpaEnd, 0, 1, false);
        // 식자
        R(TxtLetterSize, 0.3, 3.0, false); R(TxtLetterOutline, 0, 4.0, false); R(TxtLetterLineSp, 0.6, 3.0, false);
        // 자가 학습
        R(TxtSelfEpochs, 1, 50, true); R(TxtSelfLr, 0.00001, 0.01, false); R(TxtSelfFreeze, 0, 22, true); R(TxtSelfMinPages, 5, 500, true);
    }

    private static double ParseD(string t, double fallback, double min, double max) =>
        double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    private static int ParseI(string t, int fallback, int min, int max) =>
        int.TryParse(t, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static void SelectCombo(ComboBox cmb, string value)
    {
        foreach (ComboBoxItem it in cmb.Items)
            if ((string)it.Content == value) { cmb.SelectedItem = it; return; }
        cmb.Text = value;
    }

    /// <summary>편집 가능 콤보에 값을 넣는다. 저장된 값이 비어 있으면 기본 모델명을 미리 채워 준다
    /// (직접 입력해야 해서 헷갈린다는 피드백 반영).</summary>
    private static void SetComboText(ComboBox cmb, string value, string fallback)
        => cmb.Text = string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static void SelectComboByTag(ComboBox cmb, string tag)
    {
        foreach (ComboBoxItem it in cmb.Items)
            if ((string?)it.Tag == tag) { cmb.SelectedItem = it; return; }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }

    /// <summary>빈 곳 어디를 잡아도 창이 끌리게 — 제목줄이 좁아 '드래그 안 되는 곳'이 많다는
    /// 피드백. 버튼·입력칸·탭은 마우스 다운을 자기들이 처리(Handled)하므로 여기 안 들어온다.</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            try { DragMove(); } catch { /* 버튼을 뗀 뒤 도착한 이벤트 */ }
    }

    private static void ToggleAdv(System.Windows.Controls.Panel panel, Wpf.Ui.Controls.Button btn)
    {
        bool show = panel.Visibility != Visibility.Visible;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        btn.Content = (show ? "▾" : "▸") + ((string)btn.Content)[1..];
    }

    private void BtnDecensorAdv_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlDecensorAdv, BtnDecensorAdv);
    private void BtnManualDetect_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlManualDetect, BtnManualDetect);

    /// <summary>검열 감지 AI 모델 설치 여부·선택 모드에 맞춰 상태 카드를 갱신한다.</summary>
    private void RefreshCensorAiCard()
    {
        bool installed = File.Exists(Path.Combine(App.Settings.RuntimeRoot, "models", NekoManga.Core.Engine.CensorNet.ModelFile));
        bool auto = ((CmbDecensorTarget.SelectedItem as ComboBoxItem)?.Tag as string ?? "") == "Auto";
        DotCensorAi.Fill = installed
            ? (auto ? System.Windows.Media.Brushes.MediumSeaGreen : System.Windows.Media.Brushes.Goldenrod)
            : System.Windows.Media.Brushes.IndianRed;
        TxtCensorAi.Text = !installed
            ? "검열 감지 AI가 설치되어 있지 않아요 — 수동 규칙으로 찾습니다"
            : auto ? "검열 감지 AI 사용 중 — 흰·검·모자이크를 한 번에 찾아요"
                   : "검열 감지 AI는 준비됐지만 수동 모드예요 — '자동 (AI 감지)'을 고르면 켜져요";
        BtnInstallCensorAi.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CmbDecensorTarget_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshCensorAiCard();
    }
    private void BtnEngineAdv_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlEngineAdv, BtnEngineAdv);

    // ── 자가 학습 (2026-10-20) ──
    private CancellationTokenSource? _trainCts;

    private void ChkSelfTrain_Changed(object sender, RoutedEventArgs e)
    {
        if (PnlSelfTrain == null) return;
        PnlSelfTrain.Visibility = ChkSelfTrain.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ChkSelfTrain.IsChecked == true) RefreshTrainStatus();
    }

    private void RefreshTrainStatus()
    {
        var s = App.Settings;
        var tr = new NekoManga.Core.Training.SelfTrainer(s);
        var m = NekoManga.Core.Training.SelfTrainer.CountMaterial();
        TxtTrainMaterial.Text = m.Pages == 0
            ? "모인 재료가 아직 없어요 — 검열 제거를 검토 창과 함께 돌리면 쌓여요."
            : $"모인 재료 {m.Pages}쪽 (마스크를 고친 쪽 {m.Changed}, 검열 없음 {m.Skipped}) · 마지막 {m.Last:MM-dd HH:mm}";
        bool busy = _trainCts != null;
        bool can = tr.CanTrain(out var why);
        BtnSelfTrain.IsEnabled = !busy && can;
        BtnSelfTrainStop.IsEnabled = busy;
        BtnSelfApply.IsEnabled = !busy && File.Exists(tr.CandidateOnnx);
        BtnSelfRollback.IsEnabled = !busy && tr.CanRollback;
        if (!busy)
        {
            var r = tr.LastResult();
            TxtTrainStatus.Text = r == null ? (BtnSelfTrain.IsEnabled ? "학습할 수 있어요." : why)
                : r.Ok ? ResultText(r) : "지난 학습 실패: " + r.Error;
        }
    }

    private static string ResultText(NekoManga.Core.Training.SelfTrainer.Result r) =>
        $"지난 학습: 재료 {r.Pages}쪽 (학습 {r.TrainPages}·보류 {r.HoldoutPages}), 크롭 {r.Crops}장, {r.Minutes}분\n" +
        $"  보류 페이지 전: {r.Before?.Text ?? "-"}\n  보류 페이지 후: {r.After?.Text ?? "-"}\n" +
        "후가 전보다 적중은 같거나 높고 발화가 늘지 않았으면 [적용], 아니면 그대로 두세요.";

    private async void BtnSelfTrain_Click(object sender, RoutedEventArgs e)
    {
        if (_trainCts != null) return;
        Apply();   // 에폭·학습률 등 지금 칸의 값을 설정에 반영하고 저장
        var tr = new NekoManga.Core.Training.SelfTrainer(App.Settings);
        if (!tr.CanTrain(out var why)) { TxtTrainStatus.Text = why; return; }
        _trainCts = new CancellationTokenSource();
        RefreshTrainStatus();
        var status = new Progress<string>(m => TxtTrainStatus.Text = "학습 중 — " + m);
        try
        {
            var r = await tr.TrainAsync(status, _trainCts.Token);
            TxtTrainStatus.Text = r.Ok ? ResultText(r) : "학습 실패: " + r.Error + $"\n(로그: {tr.LogPath})";
        }
        catch (OperationCanceledException) { TxtTrainStatus.Text = "학습을 중단했어요."; }
        catch (Exception ex) { TxtTrainStatus.Text = "학습 실패: " + ex.Message; }
        finally { _trainCts?.Dispose(); _trainCts = null; RefreshTrainStatus(); }
    }

    private void BtnSelfTrainStop_Click(object sender, RoutedEventArgs e) => _trainCts?.Cancel();

    private void BtnSelfApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new NekoManga.Core.Training.SelfTrainer(App.Settings).Apply();
            TxtTrainStatus.Text = "적용했어요 — 다음 검열 제거부터 새 모델을 써요. 이상하면 [이전 모델로 되돌리기].";
        }
        catch (Exception ex) { TxtTrainStatus.Text = "적용 실패: " + ex.Message; }
        RefreshTrainStatus();
    }

    private void BtnSelfRollback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new NekoManga.Core.Training.SelfTrainer(App.Settings).Rollback();
            TxtTrainStatus.Text = "이전 모델로 되돌렸어요.";
        }
        catch (Exception ex) { TxtTrainStatus.Text = "되돌리기 실패: " + ex.Message; }
        RefreshTrainStatus();
    }

    private void BtnOpenCorrections_Click(object sender, RoutedEventArgs e)
    {
        var dir = NekoManga.Core.Pipeline.PipelineRunner.CorrectionsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ── ComfyUI 화면 · 커스텀 워크플로 (2026-10-20) ──
    private async void BtnOpenComfyUi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtWfStatus.Text = "엔진을 켜는 중...";
            var status = new Progress<string>(m => TxtWfStatus.Text = m);
            if (!await NekoManga.Core.Comfy.EngineManager.IsAliveAsync(App.Settings))
                await NekoManga.Core.Comfy.EngineManager.StartAsync(App.Settings, status);
            NekoManga.Core.Comfy.CustomWorkflow.OpenUi(App.Settings);
            TxtWfStatus.Text = $"브라우저에서 http://127.0.0.1:{App.Settings.ComfyPort}/ 를 열었어요. 워크플로를 만든 뒤 메뉴 → Export (API)로 저장하세요.";
        }
        catch (Exception ex) { TxtWfStatus.Text = "열지 못했어요: " + ex.Message; }
    }

    private void BtnInstallBundledWf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "workflows", "NekoManga_decensor_regen.json");
            var dst = NekoManga.Core.Comfy.CustomWorkflow.InstallBundledWorkflow(App.Settings, bundled);
            TxtWfStatus.Text = $"내장 재생성 레시피를 복사했어요 → {dst}\nComfyUI 화면의 워크플로 탐색기(사이드바)에서 'NekoManga_decensor_regen'을 열어 고친 뒤 Export (API)로 저장하면 커스텀 ComfyUI 파일로 쓸 수 있어요.";
        }
        catch (Exception ex) { TxtWfStatus.Text = "복사하지 못했어요: " + ex.Message; }
    }

    private void BtnExportRegenApi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ComfyUI API 워크플로 (*.json)|*.json", FileName = "NekoManga_decensor_regen_api.json",
            InitialDirectory = System.IO.Path.Combine(App.Settings.ComfyRoot, "ComfyUI", "user", "default", "workflows"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // 입력 자리 이름·출력 접두사는 실행 때 앱이 다시 채우므로 자리표시 값으로 둔다
            var wf = NekoManga.Core.Comfy.WorkflowBuilder.BuildRegenDecensor(App.Settings, "nm_regen_input.png", "NekoManga_out");
            System.IO.File.WriteAllText(dlg.FileName, wf.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            TxtWfStatus.Text = $"저장했어요 → {dlg.FileName} (이 파일을 '커스텀 워크플로'로 지정하면 지금 재생성과 같은 결과예요)";
        }
        catch (Exception ex) { TxtWfStatus.Text = "저장하지 못했어요: " + ex.Message; }
    }

    /// <summary>커스텀 ComfyUI를 골랐을 때만 워크플로 파일 칸을 보여 준다 (사용자 요청 10-20: 다른 방식에서 보이면 헷갈림).</summary>
    private void CmbDecensorMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PnlCustomDecensor == null || TxtDecensorModeHint == null) return;   // XAML 초기화 중 첫 SelectionChanged
        var mode = ComboTag(CmbDecensorMode, "");
        PnlCustomDecensor.Visibility = mode == "ComfyCustom" ? Visibility.Visible : Visibility.Collapsed;
        // 고른 방식 하나만 짧게 설명한다 (사용자 요청 10-20: 전부 늘어놓으면 가독성이 떨어짐)
        TxtDecensorModeHint.Text = mode switch
        {
            "Basic" => "앱에 내장된 LaMa(만화·애니 학습판)가 검열 자리를 주변과 어울리게 메워요. 빠르고 추가 설치가 없어요. 먹줄·흰줄·가벼운 모자이크에 적합.",
            "ComfyLama" => "ComfyUI의 LaMa 노드(사진 학습 모델, 확장 8·블러 8)로 메워요. 엔진이 필요하고 GPU를 써요.",
            "ComfyRegen" => "내장 레시피: LaMa로 먼저 메운 뒤 SDXL이 선화를 따라 검열 자리를 다시 그려요. 엔진·GPU 필요. 프롬프트 등은 아래 고급 옵션.",
            "ComfyCustom" => "ComfyUI 화면에서 직접 짠 워크플로 파일을 그대로 실행해요. 엔진·GPU 필요. 파일은 엔진 탭에서 만들고 Export (API)로 저장.",
            _ => "",
        };
    }

    private void BtnRegenCkptPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtRegenCkpt, "checkpoints", "SDXL 체크포인트");
    private void BtnRegenCnPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtRegenCn, "controlnet", "ControlNet");

    /// <summary>재생성 레시피의 모델 파일을 파일 대화상자로 고른다 (사용자 요청 10-20: 이름 직접 입력 대신 불러오기, 확장자 제한).
    /// ComfyUI models\&lt;subdir&gt; 안의 파일이면 이름만 쓰고, 밖의 파일이면 그 폴더로 복사한다(EngineManager.ImportModel — 엔진 탭의 '파일 가져오기'와 같은 경로).</summary>
    private void PickModelFile(System.Windows.Controls.TextBox target, string subdir, string label)
    {
        var modelsDir = System.IO.Path.Combine(App.Settings.ComfyRoot, "ComfyUI", "models", subdir);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"{label} 파일 고르기",
            Filter = "모델 파일 (*.safetensors;*.ckpt;*.pt)|*.safetensors;*.ckpt;*.pt",
            InitialDirectory = Directory.Exists(modelsDir) ? modelsDir : App.Settings.ComfyRoot,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var picked = System.IO.Path.GetFullPath(dlg.FileName);
            var inside = Directory.Exists(modelsDir) &&
                picked.StartsWith(System.IO.Path.GetFullPath(modelsDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            target.Text = inside
                ? System.IO.Path.GetFileName(picked)
                : NekoManga.Core.Comfy.EngineManager.ImportModel(App.Settings, subdir, picked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, $"{label} 파일", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCustomDecensorPick_Click(object sender, RoutedEventArgs e) => PickWorkflow(TxtCustomDecensor);
    private void BtnColorCustomPick_Click(object sender, RoutedEventArgs e) => PickWorkflow(TxtColorCustom);
    private void BtnColorCustomClear_Click(object sender, RoutedEventArgs e) => TxtColorCustom.Text = "";

    private void PickWorkflow(System.Windows.Controls.TextBox target)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ComfyUI API 워크플로 (*.json)|*.json",
            InitialDirectory = System.IO.Path.Combine(App.Settings.ComfyRoot, "ComfyUI", "user", "default", "workflows"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            NekoManga.Core.Comfy.CustomWorkflow.Load(dlg.FileName);   // 규약 검사 — 화면 포맷이면 여기서 알려 준다
            target.Text = dlg.FileName;
            TxtWfStatus.Text = "워크플로를 확인했어요 (LoadImage·SaveImage 규약 OK).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "워크플로 파일", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void BtnDecensorAdvReset_Click(object sender, RoutedEventArgs e)
    {
        var d = new NekoManga.Core.AppSettings(); // 기본값 원본
        TxtAdvLamaGrow.Text = d.LamaMaskGrow.ToString();
        TxtAdvLamaBlur.Text = d.LamaMaskBlur.ToString();
        LoadRegen(d);
    }

    /// <summary>재생성(SDXL) 워크플로 값 → 고급 옵션 칸 (기본값 = 사용자가 ComfyUI에서 짠 워크플로 그대로).</summary>
    private void LoadRegen(NekoManga.Core.AppSettings s)
    {
        TxtRegenCkpt.Text = s.RegenCheckpoint;
        TxtRegenCn.Text = s.RegenControlNet;
        TxtRegenPrompt.Text = s.RegenPrompt;
        TxtRegenBody.Text = s.RegenBodyTags;
        TxtRegenNeg.Text = s.RegenNegative;
        TxtRegenDenoise.Text = s.RegenDenoise.ToString(CultureInfo.InvariantCulture);
        TxtRegenSteps.Text = s.RegenSteps.ToString();
        TxtRegenCfg.Text = s.RegenCfg.ToString(CultureInfo.InvariantCulture);
        TxtRegenCnStr.Text = s.RegenCnStrength.ToString(CultureInfo.InvariantCulture);
        TxtRegenCnEnd.Text = s.RegenCnEnd.ToString(CultureInfo.InvariantCulture);
        TxtRegenMp.Text = s.RegenMegapixels.ToString(CultureInfo.InvariantCulture);
    }

    private void BtnEngineAdvReset_Click(object sender, RoutedEventArgs e)
    {
        var d = new NekoManga.Core.AppSettings();
        TxtAdvPort.Text = d.ComfyPort.ToString();
        TxtAdvExtraArgs.Text = d.ComfyExtraArgs;
        TxtAdvCnLineStr.Text = d.CnLineartStrength.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnLineEnd.Text = d.CnLineartEnd.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnScrStr.Text = d.CnScribbleStrength.ToString(CultureInfo.InvariantCulture);
        TxtAdvCnScrStart.Text = d.CnScribbleStart.ToString(CultureInfo.InvariantCulture);
        TxtAdvPreRes.Text = d.PreprocResolution.ToString();
        TxtAdvXdog.Text = d.ScribbleXdogThreshold.ToString();
        TxtAdvIpaEnd.Text = d.IpaEndAt.ToString(CultureInfo.InvariantCulture);
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var theme = RbThemeDark.IsChecked == true ? "Dark" : "Light";
        App.Settings.Theme = theme;
        App.Settings.Save();
        AppTheme.Apply(theme);       // 열려 있는 모든 창에 즉시 반영
        MangaTextures.Refresh();     // 격자·톤망점도 새 테마 색으로 (DynamicResource라 자동 갱신)
        AppTheme.RefreshLogo();      // 로고는 스냅샷 브러시라 전환 때 새로 그린다
    }

    /// <summary>[이 PC에 맞게 조정] — 화면 값을 설정에 반영한 뒤 AutoTune 계획을 보여 주고, 확인하면 적용해 관련 칸을 다시 채운다.</summary>
    private async void BtnAutoTune_Click(object sender, RoutedEventArgs e)
    {
        Apply();
        var s = App.Settings;
        var plan = await Task.Run(() => AutoTune.Build(s));
        if (plan.Changes.Count == 0)
        {
            MessageBox.Show(this, "감지: " + plan.HardwareLine + "\n\n" + plan.Summary, "이 PC에 맞게 조정", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(this, "감지: " + plan.HardwareLine + "\n\n이렇게 바꿀까요?\n\n" + plan.Summary,
                                 "이 PC에 맞게 조정", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        AutoTune.Apply(s, plan);
        s.Save();
        // 바뀐 칸만 다시 채운다
        TxtMegapixels.Text = s.ColorMegapixels.ToString(CultureInfo.InvariantCulture);
        TxtRegenMp.Text = s.RegenMegapixels.ToString(CultureInfo.InvariantCulture);
        TxtAdvPreRes.Text = s.PreprocResolution.ToString();
        TxtAdvExtraArgs.Text = s.ComfyExtraArgs;
        ChkPrewarm.IsChecked = s.PrewarmComfyOnLaunch;
        ChkKeepWarm.IsChecked = s.KeepComfyWarm;
        ChkSelfTrain.IsChecked = s.EnableSelfTraining; ChkSelfTrain_Changed(ChkSelfTrain, null!);
        SelectComboByTag(CmbDecensorMode, s.DecensorMode); CmbDecensorMode_Changed(CmbDecensorMode, null!);
        TxtGpuDetected.Text = "조정 완료: " + plan.HardwareLine;
    }

    // ── 엔진 탭 ──────────────────────────────────────────────
    private void LoadEngineTab(NekoManga.Core.AppSettings s)
    {
        ChkPrewarm.IsChecked = s.PrewarmComfyOnLaunch;
        ChkKeepWarm.IsChecked = s.KeepComfyWarm;
        RefreshModelCombos(s);
        _ = RefreshEngineStateAsync();
    }

    private void RefreshModelCombos(NekoManga.Core.AppSettings s)
    {
        CmbCheckpoint.Items.Clear();
        foreach (var m in NekoManga.Core.Comfy.EngineManager.ListModels(s, "checkpoints"))
            CmbCheckpoint.Items.Add(m);
        if (!CmbCheckpoint.Items.Contains(s.ColorCheckpoint)) CmbCheckpoint.Items.Add(s.ColorCheckpoint);
        CmbCheckpoint.SelectedItem = s.ColorCheckpoint;

        CmbControlNet.Items.Clear();
        foreach (var m in NekoManga.Core.Comfy.EngineManager.ListModels(s, "controlnet"))
            CmbControlNet.Items.Add(m);
        if (!CmbControlNet.Items.Contains(s.ColorControlNet)) CmbControlNet.Items.Add(s.ColorControlNet);
        CmbControlNet.SelectedItem = s.ColorControlNet;
    }

    private async Task RefreshEngineStateAsync()
    {
        TxtEngineState.Text = "상태 확인 중...";
        DotEngine.Fill = System.Windows.Media.Brushes.Gray;
        bool alive = await NekoManga.Core.Comfy.EngineManager.IsAliveAsync(App.Settings);
        DotEngine.Fill = alive ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.IndianRed;
        TxtEngineState.Text = alive ? "켜져 있어요 — 바로 채색할 수 있어요" : "꺼져 있어요";
        BtnEngineStart.IsEnabled = !alive;
        BtnEngineStop.IsEnabled = alive;
    }

    private async void BtnEngineStart_Click(object sender, RoutedEventArgs e)
    {
        BtnEngineStart.IsEnabled = false;
        try
        {
            var status = new Progress<string>(m => TxtEngineState.Text = m);
            await NekoManga.Core.Comfy.EngineManager.StartAsync(App.Settings, status);
        }
        catch (Exception ex) { TxtEngineState.Text = "켜기 실패: " + ex.Message; }
        await RefreshEngineStateAsync();
    }

    private async void BtnEngineStop_Click(object sender, RoutedEventArgs e)
    {
        BtnEngineStop.IsEnabled = false;
        NekoManga.Core.Comfy.EngineManager.Stop(App.Settings);
        await Task.Delay(800);
        await RefreshEngineStateAsync();
    }

    private void BtnModelsRefresh_Click(object sender, RoutedEventArgs e) => RefreshModelCombos(App.Settings);

    private void BtnCkptImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "그림 모델 고르기 (.safetensors)",
            Filter = "모델|*.safetensors;*.ckpt|모든 파일|*.*",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            TxtCkptStatus.Text = "가져오는 중... (파일이 크면 몇 분 걸려요)";
            var name = NekoManga.Core.Comfy.EngineManager.ImportModel(App.Settings, "checkpoints", dlg.FileName);
            RefreshModelCombos(App.Settings);
            CmbCheckpoint.SelectedItem = name;
            TxtCkptStatus.Text = $"가져왔어요: {name} — 이 모델이 선택됐어요";
        }
        catch (Exception ex) { TxtCkptStatus.Text = "가져오기 실패: " + ex.Message; }
    }

    private async void BtnCkptDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtCkptUrl.Text.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            TxtCkptStatus.Text = "다운로드 주소(https://…)를 붙여넣어 주세요";
            return;
        }
        BtnCkptDownload.IsEnabled = false;
        try
        {
            var status = new Progress<string>(m => TxtCkptStatus.Text = m);
            var name = await NekoManga.Core.Comfy.EngineManager.DownloadModelAsync(
                App.Settings, "checkpoints", url, status);
            RefreshModelCombos(App.Settings);
            CmbCheckpoint.SelectedItem = name;
            TxtCkptStatus.Text = $"받았어요: {name} — 이 모델이 선택됐어요";
        }
        catch (Exception ex) { TxtCkptStatus.Text = "받기 실패: " + ex.Message; }
        finally { BtnCkptDownload.IsEnabled = true; }
    }

    private void RefDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void RefDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            TxtColorRefImage.Text = files[0];
            TxtColorRefTags.Text = "";   // 표지가 바뀌면 캐시된 태그는 무효
        }
    }

    private void TxtColorRefImage_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateRefPreview();

    /// <summary>등록된 표지의 미리보기를 드롭존에 띄운다 (없으면 안내 문구).</summary>
    private void UpdateRefPreview()
    {
        var path = TxtColorRefImage.Text.Trim();
        System.Windows.Media.Imaging.BitmapImage? bmp = null;
        if (File.Exists(path))
            try
            {
                bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 파일 잠금 방지
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelHeight = 256;
                bmp.EndInit();
                bmp.Freeze();
            }
            catch { bmp = null; }
        ImgRefPreview.Source = bmp;
        TxtRefDropHint.Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnRefBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "컬러 표지/참고 일러스트 고르기",
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.webp;*.bmp|모든 파일|*.*",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtColorRefImage.Text = dlg.FileName;
            TxtColorRefTags.Text = "";   // 표지가 바뀌면 캐시된 태그는 무효
        }
    }

    private async void BtnRefExtract_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtColorRefImage.Text.Trim();
        if (!File.Exists(path))
        {
            TxtColorRefTags.Text = "";
            System.Windows.MessageBox.Show("먼저 표지 이미지를 골라주세요.", "색 뽑기");
            return;
        }
        BtnRefExtract.IsEnabled = false;
        var old = BtnRefExtract.Content;
        BtnRefExtract.Content = "뽑는 중...";
        try
        {
            // 현재 입력값으로 뽑는다 (창을 닫기 전이라 settings엔 아직 저장 전일 수 있음)
            var s = App.Settings;
            var tags = await NekoManga.Core.Comfy.ColorRefHelper.ExtractAsync(
                s, path, new Progress<string>(m => BtnRefExtract.Content = m));
            TxtColorRefTags.Text = tags.Length > 0 ? tags : "";
            if (tags.Length == 0)
                System.Windows.MessageBox.Show("색 태그를 찾지 못했어요 — 컬러 이미지가 맞는지 확인해 주세요.", "색 뽑기");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("색 뽑기 실패: " + ex.Message, "색 뽑기");
        }
        finally
        {
            BtnRefExtract.IsEnabled = true;
            BtnRefExtract.Content = old;
        }
    }

    /// <summary>"#RRGGBB"만 통과시키고 아니면 fallback (빈 값 허용 = 자동).</summary>
    private static string NormalizeHex(string text, string fallback = "")
    {
        var t = text.Trim();
        if (t.Length == 0) return fallback == "" ? "" : fallback;
        if (!t.StartsWith('#')) t = "#" + t;
        return t.Length == 7 && t[1..].All(Uri.IsHexDigit) ? t.ToUpperInvariant() : fallback;
    }

    private static string ComboTag(ComboBox cmb, string fallback) =>
        (cmb.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static string ComboText(ComboBox cmb, string fallback) =>
        (cmb.SelectedItem as ComboBoxItem)?.Content as string ?? (string.IsNullOrWhiteSpace(cmb.Text) ? fallback : cmb.Text);

    private void CmbTranslator_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateTranslatorPanels();

    /// <summary>선택한 엔진에 해당하는 키 입력란만 보여준다.</summary>
    private void UpdateTranslatorPanels()
    {
        if (PanelDeepL == null) return; // InitializeComponent 전 호출 방지
        var engine = ComboTag(CmbTranslator, "Google");
        PanelDeepL.Visibility = engine == "DeepL" ? Visibility.Visible : Visibility.Collapsed;
        PanelDeepSeek.Visibility = engine == "DeepSeek" ? Visibility.Visible : Visibility.Collapsed;
        PanelGrok.Visibility = engine == "Grok" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnPickOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "결과물 저장 폴더" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtOutputRoot.Text = dlg.SelectedPath;
    }

    private void BtnOpenSetup_Click(object sender, RoutedEventArgs e)
    {
        Apply();
        new SetupWindow { Owner = this }.ShowDialog();
    }

    private void BtnGitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(UpdateService.RepoUrl) { UseShellExecute = true });

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "NekoManga를 삭제할까요?\n(내려받은 AI 구성요소 폴더는 남아요 — 직접 지워주세요)",
            "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            UpdateService.LaunchUninstaller();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("삭제 실행 실패: " + ex.Message + "\n설치형(Setup.exe)으로 설치했을 때만 쓸 수 있어요.",
                "NekoManga", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
