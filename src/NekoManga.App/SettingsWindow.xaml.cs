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

        SelectComboByTag(CmbDecensorEngine, s.DecensorEngine);
        SelectComboByTag(CmbLegacyAssist, s.DecensorLegacyAssist);
        ChkAssistRepair.IsChecked = s.DecensorAssistRepair == "Aletheia";
        SelectComboByTag(CmbDecensorTarget, s.DecensorTarget);
        TxtBarAspect.Text = s.DecensorBarMinAspect.ToString(CultureInfo.InvariantCulture);
        TxtBarMinArea.Text = s.DecensorBarMinArea.ToString();
        TxtBlockSize.Text = s.DecensorBlockSize.ToString();
        TxtThreshold.Text = s.DecensorThreshold.ToString();
        TxtMaskGrow.Text = s.DecensorMaskGrow.ToString();
        ChkBubbleFilter.IsChecked = s.DecensorBubbleFilter;
        ChkReview.IsChecked = s.DecensorReview;
        SelectComboByTag(CmbReviewMode, s.DecensorReviewMode);
        ChkReuseMasks.IsChecked = s.ReuseStoredMasks;
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
        SelectComboByTag(CmbOnnxGpu, s.OnnxGpu);
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
        TxtLetterWordSp.Text = s.LetterWordSpacing.ToString(CultureInfo.InvariantCulture);
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
        _decensorUiReady = true;
        RefreshDecensorUi();

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

        s.DecensorEngine = ComboTag(CmbDecensorEngine, s.DecensorEngine);
        s.DecensorLegacyAssist = ComboTag(CmbLegacyAssist, s.DecensorLegacyAssist);
        s.DecensorAssistRepair = ChkAssistRepair.IsChecked == true ? "Aletheia" : "Lama";
        s.DecensorTarget = ComboTag(CmbDecensorTarget, s.DecensorTarget);
        s.DecensorBarMinAspect = ParseD(TxtBarAspect.Text, s.DecensorBarMinAspect, 1.0, 10.0);
        s.DecensorBarMinArea = ParseI(TxtBarMinArea.Text, s.DecensorBarMinArea, 50, 500_000);
        s.DecensorBlockSize = ParseI(TxtBlockSize.Text, s.DecensorBlockSize, 2, 64);
        s.DecensorThreshold = ParseI(TxtThreshold.Text, s.DecensorThreshold, 1, 80);
        s.DecensorMaskGrow = ParseI(TxtMaskGrow.Text, s.DecensorMaskGrow, 0, 64);
        s.DecensorBubbleFilter = ChkBubbleFilter.IsChecked == true;
        s.DecensorReview = ChkReview.IsChecked == true;
        s.DecensorReviewMode = ComboTag(CmbReviewMode, s.DecensorReviewMode);
        s.ReuseStoredMasks = ChkReuseMasks.IsChecked == true;
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
        s.OnnxGpu = ComboTag(CmbOnnxGpu, s.OnnxGpu);

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
        s.LetterWordSpacing = ParseD(TxtLetterWordSp.Text, s.LetterWordSpacing, 0.3, 2.0);
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
        R(TxtLetterSize, 0.3, 3.0, false); R(TxtLetterOutline, 0, 4.0, false); R(TxtLetterLineSp, 0.6, 3.0, false); R(TxtLetterWordSp, 0.3, 2.0, false);
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
