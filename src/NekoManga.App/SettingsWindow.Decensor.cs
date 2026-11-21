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
using NekoManga.Core;

namespace NekoManga.App;

// 검열 제거 탭 — 지금 고른 값에 맞춘 화면 갱신(요약·옵션 숨기기·모델 상태 카드), 접이 패널,
// 재생성 모델·커스텀 워크플로 파일 고르기, 고급 옵션 채우기·기본값 복원
public partial class SettingsWindow
{
    private void BtnDecensorAdv_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlDecensorAdv, BtnDecensorAdv);
    private void BtnManualDetect_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlManualDetect, BtnManualDetect);

    /// <summary>생성자가 값을 다 채운 뒤에만 화면을 맞춘다 — XAML 초기화·값 채우기 중의 SelectionChanged는 아직 없는 컨트롤을 건드린다.</summary>
    private bool _decensorUiReady;

    /// <summary>검열 제거 탭을 지금 고른 값(아직 저장 전)에 맞춘다 (2026-11-09 — 감지 엔진이 둘이 되면서 고른 값만으로는 실제 동작을 알기 어렵다는 피드백):
    /// '지금 설정' 요약 두 줄, 조합에 해당 없는 옵션 숨기기, '기본' 방식 이름, 모델 상태 카드.</summary>
    private void RefreshDecensorUi()
    {
        if (!_decensorUiReady) return;
        string target = ComboTag(CmbDecensorTarget, "Auto");
        string assist = ComboTag(CmbLegacyAssist, "White"), mode = ComboTag(CmbDecensorMode, "Basic");
        string assistRepair = ChkAssistRepair.IsChecked == true ? "Aletheia" : "Lama";
        string models = Path.Combine(App.Settings.RuntimeRoot, "models");
        bool censorNet = File.Exists(Path.Combine(models, NekoManga.Core.Engine.CensorNet.ModelFile));
        bool aleDetect = File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaDetector.ModelFile));
        bool aleRepair = File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaInpainter.BarModelFile))
                      && File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaInpainter.MosaicModelFile));

        // 요약
        TxtFlowFind.Text = DecensorLabels.Find(target, assist, censorNet);
        TxtFlowFix.Text = DecensorLabels.Fix(mode, target, assist, assistRepair);

        // 해당 없는 옵션은 숨긴다: 보완은 자동에서만, 보완 자리 복원 선택은 기본 방식 + 보완이 있을 때만
        PnlAssist.Visibility = target == "Auto" ? Visibility.Visible : Visibility.Collapsed;
        PnlAssistRepair.Visibility = mode == "Basic" && DecensorLabels.HasAssist(target, assist) ? Visibility.Visible : Visibility.Collapsed;

        // 고른 제거 방식 하나만 짧게 설명한다 (사용자 요청 10-20: 전부 늘어놓으면 가독성이 떨어짐)
        TxtDecensorModeHint.Text = mode switch
        {
            "Basic" => Loc.T("Aletheia Lens가 찾은 자리를 주변에 맞춰 채운다냥.\n· 빠르고 추가 설치가 없다냥."),
            "ComfyRegen" => Loc.T("내장 레시피: 영역마다 1024로 잘라 SDXL 두 단계로 새로 그린다냥.\n· 엔진·GPU가 필요하다냥."),
            "ComfyCustom" => Loc.T("직접 짠 워크플로 파일을 그대로 실행한다냥.\n· 엔진·GPU가 필요하다냥."),
            _ => "",
        };

        // 모델 상태 카드 — 첫 줄은 Aletheia Lens, 둘째 줄은 전용 AI(보완)
        var ok = System.Windows.Media.Brushes.MediumSeaGreen; var idle = System.Windows.Media.Brushes.Goldenrod; var missing = System.Windows.Media.Brushes.IndianRed;
        bool needInstall;
        bool needRepair = mode == "Basic";
        bool aleOk = aleDetect && (aleRepair || !needRepair);
        DotCensorAi.Fill = aleOk ? ok : missing;
        TxtCensorAi.Text = !aleDetect ? Loc.T("Aletheia Lens 감지 모델이 없다냥 — 검열 제거를 시작할 수 없다냥")
            : needRepair && !aleRepair ? Loc.T("Aletheia Lens 복원 모델이 없다냥 — '기본' 방식으로 지울 수 없다냥")
            : Loc.T("Aletheia Lens 준비됨 — 막대·모자이크 감지") + (needRepair ? Loc.T(" + 복원") : "");
        bool whiteOnly = target == "WhiteBar";   // '흰색 막대만'은 보완 설정과 상관없이 전용 AI의 흰 검출을 더한다(없으면 규칙 감지)
        bool assistWanted = whiteOnly || (target == "Auto" && assist != "None");
        RowAssistAi.Visibility = Visibility.Visible;
        DotAssistAi.Fill = !censorNet ? (assistWanted ? missing : idle) : assistWanted ? ok : idle;
        TxtAssistAi.Text = !censorNet ? (whiteOnly ? Loc.T("전용 AI가 없다냥 — 흰 막대는 규칙 감지로 보완한다냥 (오탐이 많다냥)")
                : assistWanted ? Loc.T("전용 AI가 없다냥 — 보완 없이 Aletheia Lens만 찾는다냥") : Loc.T("전용 AI 미설치 (지금 설정에서는 안 쓴다냥)"))
            : assistWanted ? Loc.T("전용 AI 준비됨 — ") + (whiteOnly ? "White" : assist) switch { "WhiteMosaic" => Loc.T("흰 막대선·모자이크 보완"), "All" => Loc.T("흰·검·모자이크 전부 합치기"), _ => Loc.T("가는 흰 막대선 보완") }
            : Loc.T("전용 AI 준비됨 — 지금 설정에서는 쉰다냥");
        TxtCensorAiHint.Text = Loc.T("Aletheia Lens는 공개 모델이고, 전용 AI는 이 앱용으로 학습한 모델이다냥.\n· 잘못 잡힌 곳은 검토에서 지우면 된다냥.");
        needInstall = !aleOk || (assistWanted && !censorNet);
        BtnInstallCensorAi.Visibility = needInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CmbDecensorTarget_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshDecensorUi();
    private void DecensorUi_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshDecensorUi();
    private void DecensorUi_Toggled(object sender, RoutedEventArgs e) => RefreshDecensorUi();

    /// <summary>커스텀 ComfyUI를 골랐을 때만 워크플로 파일 칸을 보여 준다 (사용자 요청 10-20: 다른 방식에서 보이면 헷갈림).</summary>
    private void CmbDecensorMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PnlCustomDecensor == null || TxtDecensorModeHint == null) return;   // XAML 초기화 중 첫 SelectionChanged
        var mode = ComboTag(CmbDecensorMode, "");
        PnlCustomDecensor.Visibility = mode == "ComfyCustom" ? Visibility.Visible : Visibility.Collapsed;
        RefreshDecensorUi();   // 방식 설명·요약·상태 카드는 엔진 조합에 따라 달라져 한곳에서 맞춘다
    }

    private void BtnRegenCkptPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtRegenCkpt, "checkpoints", Loc.T("SDXL 체크포인트"));
    private void BtnRegenCnPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtRegenCn, "controlnet", "ControlNet");

    /// <summary>재생성 레시피의 모델 파일을 파일 대화상자로 고른다 (사용자 요청 10-20: 이름 직접 입력 대신 불러오기, 확장자 제한).
    /// ComfyUI models\&lt;subdir&gt; 안의 파일이면 이름만 쓰고, 밖의 파일이면 그 폴더로 복사한다(EngineManager.ImportModel — 엔진 탭의 '파일 가져오기'와 같은 경로).</summary>
    private void PickModelFile(System.Windows.Controls.TextBox target, string subdir, string label)
    {
        var modelsDir = System.IO.Path.Combine(App.Settings.ComfyRoot, "ComfyUI", "models", subdir);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.F($"{label} 파일 고르기"),
            Filter = Loc.T("모델 파일 (*.safetensors;*.ckpt;*.pt)") + "|*.safetensors;*.ckpt;*.pt",
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
            MessageBox.Show(this, ex.Message, Loc.F($"{label} 파일"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCustomDecensorPick_Click(object sender, RoutedEventArgs e) => PickWorkflow(TxtCustomDecensor);

    private void BtnDecensorAdvReset_Click(object sender, RoutedEventArgs e)
    {
        var d = new NekoManga.Core.AppSettings(); // 기본값 원본
        LoadRegen(d);
    }

    /// <summary>재생성(SDXL) 워크플로 값 → 고급 옵션 칸 (기본값 = 사용자가 ComfyUI에서 짠 워크플로 그대로).</summary>
    private void LoadRegen(NekoManga.Core.AppSettings s)
    {
        TxtRegenCkpt.Text = s.RegenCheckpoint;
        TxtRegenCn.Text = s.RegenControlNet;
        TxtRegenPrompt.Text = s.RegenPrompt;
        TxtRegenBodyM.Text = s.RegenBodyTagsMale;
        TxtRegenBodyF.Text = s.RegenBodyTagsFemale;
        TxtRegenBodyCum.Text = s.CumTags;
        ChkRegenCumAuto.IsChecked = s.RegenCumAuto;
        ChkRegenTagsBySex.IsChecked = s.RegenTagsBySex;
        ChkRegenTagsPerPiece.IsChecked = s.RegenTagsPerPiece;
        ChkRegenSplitRegions.IsChecked = s.RegenSplitRegions;
        TxtRegenNeg.Text = s.RegenNegative;
        TxtRegenDenoise.Text = s.RegenDenoise.ToString(CultureInfo.InvariantCulture);
        TxtRegenSteps.Text = s.RegenSteps.ToString();
        TxtRegenCfg.Text = s.RegenCfg.ToString(CultureInfo.InvariantCulture);
        TxtRegenCnStr.Text = s.RegenCnStrength.ToString(CultureInfo.InvariantCulture);
        TxtRegenCnEnd.Text = s.RegenCnEnd.ToString(CultureInfo.InvariantCulture);
        TxtRegenCrop.Text = s.RegenCropSize.ToString();
        TxtRegenInpaintGrow.Text = s.RegenInpaintGrow.ToString();
        SelectComboByTag(CmbRegenInpaintControl, s.RegenInpaintControl);
        TxtRegenInpaintCnStr.Text = s.RegenInpaintCnStrength.ToString(CultureInfo.InvariantCulture);
        ChkRegenEraseTight.IsChecked = s.RegenEraseTight;
        TxtRegenWhiteRetries.Text = s.RegenWhiteRetries.ToString();
        ChkRegenWhole.IsChecked = s.RegenWholeRegion;
        TxtRegenRegionConf.Text = s.RegenRegionConf.ToString(CultureInfo.InvariantCulture);
        TxtRegenRegionPad.Text = s.RegenRegionPad.ToString(CultureInfo.InvariantCulture);
        TxtRegenRegionGrow.Text = s.RegenRegionGrow.ToString(CultureInfo.InvariantCulture);
    }
}
