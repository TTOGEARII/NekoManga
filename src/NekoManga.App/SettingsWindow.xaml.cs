using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using NekoManga.Core.Engine;
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
        TxtExcludeTags.Text = s.ColorExcludeTags;
        TxtNegative.Text = s.ColorNegative;

        SelectComboByTag(CmbDecensorTarget, s.DecensorTarget);
        TxtBarAspect.Text = s.DecensorBarMinAspect.ToString(CultureInfo.InvariantCulture);
        TxtBarMinArea.Text = s.DecensorBarMinArea.ToString();
        TxtBlockSize.Text = s.DecensorBlockSize.ToString();
        TxtThreshold.Text = s.DecensorThreshold.ToString();
        TxtMaskGrow.Text = s.DecensorMaskGrow.ToString();
        SelectComboByTag(CmbDecensorMode, s.DecensorMode);
        TxtStrongDenoise.Text = s.DecensorStrongDenoise.ToString(CultureInfo.InvariantCulture);
        TxtDecensorSeed.Text = s.DecensorSeed.ToString(CultureInfo.InvariantCulture);

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
        s.ColorExcludeTags = TxtExcludeTags.Text.Trim();
        s.ColorNegative = TxtNegative.Text.Trim();

        s.DecensorTarget = ComboTag(CmbDecensorTarget, s.DecensorTarget);
        s.DecensorBarMinAspect = ParseD(TxtBarAspect.Text, s.DecensorBarMinAspect, 1.0, 10.0);
        s.DecensorBarMinArea = ParseI(TxtBarMinArea.Text, s.DecensorBarMinArea, 50, 500_000);
        s.DecensorBlockSize = ParseI(TxtBlockSize.Text, s.DecensorBlockSize, 2, 64);
        s.DecensorThreshold = ParseI(TxtThreshold.Text, s.DecensorThreshold, 1, 80);
        s.DecensorMaskGrow = ParseI(TxtMaskGrow.Text, s.DecensorMaskGrow, 0, 64);
        s.DecensorMode = ComboTag(CmbDecensorMode, s.DecensorMode);
        s.DecensorStrongDenoise = ParseD(TxtStrongDenoise.Text, s.DecensorStrongDenoise, 0.3, 1.0);
        s.DecensorSeed = long.TryParse(TxtDecensorSeed.Text.Trim(), out var dseed) && dseed >= -1
            ? dseed : s.DecensorSeed;

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

        s.OutputRoot = TxtOutputRoot.Text.Trim();
        s.LinkedComfyRoot = TxtLinkedComfy.Text.Trim();
        s.CivitaiApiKey = TxtCivitaiKey.Text.Trim();
        s.Save();
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
