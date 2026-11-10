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
        string engine = ComboTag(CmbDecensorEngine, "Aletheia"), target = ComboTag(CmbDecensorTarget, "Auto");
        string assist = ComboTag(CmbLegacyAssist, "White"), mode = ComboTag(CmbDecensorMode, "Basic");
        string assistRepair = ChkAssistRepair.IsChecked == true ? "Aletheia" : "Lama";
        bool ale = DecensorLabels.IsAletheia(engine);
        string models = Path.Combine(App.Settings.RuntimeRoot, "models");
        bool censorNet = File.Exists(Path.Combine(models, NekoManga.Core.Engine.CensorNet.ModelFile));
        bool aleDetect = File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaDetector.ModelFile));
        bool aleRepair = File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaInpainter.BarModelFile))
                      && File.Exists(Path.Combine(models, NekoManga.Core.Engine.AletheiaInpainter.MosaicModelFile));

        // 요약
        TxtFlowFind.Text = DecensorLabels.Find(engine, target, assist, censorNet);
        TxtFlowFix.Text = DecensorLabels.Fix(engine, mode, target, assist, assistRepair);

        // 해당 없는 옵션은 숨긴다: 보완은 Aletheia + 자동에서만, 보완 자리 복원 선택은 기본 방식 + 보완이 있을 때만
        PnlAssist.Visibility = ale && target == "Auto" ? Visibility.Visible : Visibility.Collapsed;
        PnlAssistRepair.Visibility = mode == "Basic" && DecensorLabels.HasAssist(engine, target, assist) ? Visibility.Visible : Visibility.Collapsed;
        ItemModeBasic.Content = DecensorLabels.BasicModeLabel(engine);

        // 고른 제거 방식 하나만 짧게 설명한다 (사용자 요청 10-20: 전부 늘어놓으면 가독성이 떨어짐)
        TxtDecensorModeHint.Text = mode switch
        {
            "Basic" when ale => "Aletheia Lens의 복원 모델이 찾은 자리를 주변 그림에 맞춰 채워요. 막대는 가려진 곳을 비우고 새로 그리고, 모자이크는 흐려진 그림을 힌트 삼아 다듬어요. 빠르고 추가 설치가 없어요.",
            "Basic" => "앱에 내장된 LaMa(만화·애니 학습판)가 검열 자리를 주변과 어울리게 메워요. 빠르고 추가 설치가 없어요. 먹줄·흰줄·가벼운 모자이크에 적합.",
            "ComfyLama" => "ComfyUI의 LaMa 노드(사진 학습 모델, 확장 8·블러 8)로 메워요. 엔진이 필요하고 GPU를 써요.",
            "ComfyRegen" => "내장 레시피: LaMa로 먼저 메운 뒤 SDXL이 선화를 따라 검열 자리를 다시 그려요. 엔진·GPU 필요. 프롬프트 등은 아래 고급 옵션.",
            "ComfyCustom" => "ComfyUI 화면에서 직접 짠 워크플로 파일을 그대로 실행해요. 엔진·GPU 필요. 파일은 엔진 탭에서 만들고 Export (API)로 저장.",
            _ => "",
        };

        // 모델 상태 카드 — 첫 줄은 주 감지 엔진, 둘째 줄은 전용 AI(보완 또는 이전 방식의 주 감지기)
        var ok = System.Windows.Media.Brushes.MediumSeaGreen; var idle = System.Windows.Media.Brushes.Goldenrod; var missing = System.Windows.Media.Brushes.IndianRed;
        bool needInstall;
        if (ale)
        {
            bool needRepair = mode == "Basic";
            bool aleOk = aleDetect && (aleRepair || !needRepair);
            DotCensorAi.Fill = aleOk ? ok : missing;
            TxtCensorAi.Text = !aleDetect ? "Aletheia Lens 감지 모델이 설치되어 있지 않아요 — 검열 제거를 시작할 수 없어요"
                : needRepair && !aleRepair ? "Aletheia Lens 복원 모델이 설치되어 있지 않아요 — '기본' 방식으로 지울 수 없어요"
                : "Aletheia Lens 준비됨 — 막대·모자이크 감지" + (needRepair ? " + 복원" : "");
            bool assistWanted = target == "Auto" && assist != "None";
            RowAssistAi.Visibility = Visibility.Visible;
            DotAssistAi.Fill = !censorNet ? (assistWanted ? missing : idle) : assistWanted ? ok : idle;
            TxtAssistAi.Text = !censorNet ? (assistWanted ? "전용 AI가 설치되어 있지 않아요 — 보완 없이 Aletheia Lens만 찾아요" : "전용 AI 미설치 (지금 설정에서는 쓰이지 않아요)")
                : assistWanted ? "전용 AI 준비됨 — " + assist switch { "WhiteMosaic" => "흰 막대선·모자이크 보완", "All" => "흰·검·모자이크 전부 합치기", _ => "가는 흰 막대선 보완" }
                : "전용 AI 준비됨 — 지금 설정에서는 쉬어요";
            TxtCensorAiHint.Text = "Aletheia Lens는 컬러·흑백 그림의 막대와 모자이크를 찾는 공개 모델이고, 전용 AI는 이 앱용으로 직접 학습한 모델이에요. 잘못 잡힌 곳은 ③의 검토에서 지우면 돼요.";
            needInstall = !aleOk || (assistWanted && !censorNet);
        }
        else
        {
            bool auto = target == "Auto";
            DotCensorAi.Fill = censorNet ? (auto ? ok : idle) : missing;
            TxtCensorAi.Text = !censorNet ? "전용 AI가 설치되어 있지 않아요 — 규칙으로 찾습니다"
                : auto ? "전용 AI 사용 중 — 흰·검·모자이크를 한 번에 찾아요"
                       : "전용 AI는 준비됐지만 규칙 감지 모드예요 — '자동'을 고르면 켜져요";
            RowAssistAi.Visibility = Visibility.Collapsed;
            TxtCensorAiHint.Text = "이전 방식: 이 앱 전용으로 직접 학습한 모델이 흰 막대·흰 박스·단색 밀기·검은 막대·모자이크를 한 번에 찾아요. 자동이 아닌 종류를 고르면 규칙 감지기가 그 종류만 겨냥해요.";
            needInstall = !censorNet;
        }
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
}
