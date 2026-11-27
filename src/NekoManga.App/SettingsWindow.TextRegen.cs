using System.Globalization;
using System.Windows;
using NekoManga.Core;

namespace NekoManga.App;

// 번역 탭의 '지운 자리 다시 그리기' 카드(TextRegen, 2026-11-26) — 글자를 지운 자리 가운데 그림 위의 것만 ComfyUI SDXL로 다시 그리는 기능의 칸.
// 사용자 결정: "검열 제거와 별도로 특화된 모델을 따로", "인페인팅과 컨트롤넷도 글자용으로 따로" — 그래서 검열 재생성의 칸(SettingsWindow.Decensor.cs의 LoadRegen)과 값을 하나도 나누지 않는다.
// 체크포인트·ControlNet 칸만 비워 둘 수 있다(비면 재생성의 모델을 쓴다 — AppSettings.TextRegenCheckpointInUse).
public partial class SettingsWindow
{
    /// <summary>설정 → 카드의 칸. <paramref name="withToggle"/>가 false면 켬/끔 체크는 그대로 둔다([기본값 복원]은 고급 옵션의 값만 되돌린다).</summary>
    private void LoadTextRegen(AppSettings s, bool withToggle = true)
    {
        if (withToggle) ChkTextRegen.IsChecked = s.TextRegen;
        TxtTextRegenCkpt.Text = s.TextRegenCheckpoint;
        TxtTextRegenCn.Text = s.TextRegenControlNet;
        TxtTextRegenPrompt.Text = s.TextRegenPrompt;
        TxtTextRegenNeg.Text = s.TextRegenNegative;
        TxtTextRegenSteps.Text = s.TextRegenSteps.ToString();
        TxtTextRegenCfg.Text = s.TextRegenCfg.ToString(CultureInfo.InvariantCulture);
        TxtTextRegenSampler.Text = s.TextRegenSampler;
        TxtTextRegenScheduler.Text = s.TextRegenScheduler;
        TxtTextRegenSeed.Text = s.TextRegenSeed.ToString();
        TxtTextRegenGrow.Text = s.TextRegenInpaintGrow.ToString();
        TxtTextRegenCnStr.Text = s.TextRegenCnStrength.ToString(CultureInfo.InvariantCulture);
        TxtTextRegenCnEnd.Text = s.TextRegenCnEnd.ToString(CultureInfo.InvariantCulture);
        TxtTextRegenDenoise.Text = s.TextRegenDenoise.ToString(CultureInfo.InvariantCulture);
        TxtTextRegenCrop.Text = s.TextRegenCropSize.ToString();
        TxtTextRegenRetries.Text = s.TextRegenWhiteRetries.ToString();
        SelectComboByTag(CmbTextRegenControl, s.TextRegenControl);
    }

    /// <summary>카드의 칸 → 설정. 범위 밖 숫자는 잘리고, 못 읽는 글은 전 값 그대로(재생성 칸과 같은 규칙).</summary>
    private void SaveTextRegen(AppSettings s)
    {
        s.TextRegen = ChkTextRegen.IsChecked == true;
        s.TextRegenCheckpoint = TxtTextRegenCkpt.Text.Trim();   // 비워 둘 수 있다 — 비면 재생성의 체크포인트
        s.TextRegenControlNet = TxtTextRegenCn.Text.Trim();
        s.TextRegenPrompt = TxtTextRegenPrompt.Text.Trim();
        s.TextRegenNegative = TxtTextRegenNeg.Text.Trim();
        s.TextRegenSteps = ParseI(TxtTextRegenSteps.Text, s.TextRegenSteps, 1, 150);
        s.TextRegenCfg = ParseD(TxtTextRegenCfg.Text, s.TextRegenCfg, 0, 30);
        if (!string.IsNullOrWhiteSpace(TxtTextRegenSampler.Text)) s.TextRegenSampler = TxtTextRegenSampler.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TxtTextRegenScheduler.Text)) s.TextRegenScheduler = TxtTextRegenScheduler.Text.Trim();
        if (long.TryParse(TxtTextRegenSeed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) s.TextRegenSeed = Math.Max(-1, seed);
        s.TextRegenInpaintGrow = ParseI(TxtTextRegenGrow.Text, s.TextRegenInpaintGrow, 0, 64);
        s.TextRegenCnStrength = ParseD(TxtTextRegenCnStr.Text, s.TextRegenCnStrength, 0, 2);
        s.TextRegenCnEnd = ParseD(TxtTextRegenCnEnd.Text, s.TextRegenCnEnd, 0, 1);
        s.TextRegenDenoise = ParseD(TxtTextRegenDenoise.Text, s.TextRegenDenoise, 0.05, 1);
        s.TextRegenCropSize = ParseI(TxtTextRegenCrop.Text, s.TextRegenCropSize, 512, 2048);
        s.TextRegenWhiteRetries = ParseI(TxtTextRegenRetries.Text, s.TextRegenWhiteRetries, 0, 5);
        s.TextRegenControl = ComboTag(CmbTextRegenControl, s.TextRegenControl);
    }

    private void BtnTextRegenCkptPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtTextRegenCkpt, "checkpoints", Loc.T("SDXL 체크포인트"));
    private void BtnTextRegenCnPick_Click(object sender, RoutedEventArgs e) => PickModelFile(TxtTextRegenCn, "controlnet", "ControlNet");
    private void BtnTextRegenCkptClear_Click(object sender, RoutedEventArgs e) => TxtTextRegenCkpt.Text = "";
    private void BtnTextRegenCnClear_Click(object sender, RoutedEventArgs e) => TxtTextRegenCn.Text = "";

    private void BtnTextRegenReset_Click(object sender, RoutedEventArgs e) => LoadTextRegen(new AppSettings(), withToggle: false);
}
