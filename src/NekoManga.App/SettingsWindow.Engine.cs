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

// 엔진 탭 — 엔진 켜고 끄기, 채색 모델 목록·가져오기·내려받기, 이 PC에 맞게 조정, 고급 옵션 패널·기본값 복원,
// ComfyUI 화면 열기, 내장 재생성 레시피 넣기·내보내기, 커스텀 워크플로 고르기(검열 제거 탭의 찾기 버튼도 함께 쓴다)
public partial class SettingsWindow
{
    private void BtnEngineAdv_Click(object sender, RoutedEventArgs e) => ToggleAdv(PnlEngineAdv, BtnEngineAdv);

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
            TxtWfStatus.Text = $"브라우저에서 http://127.0.0.1:{App.Settings.ComfyPort}/ 를 열었어요. 워크플로를 만들어 저장한 파일(그냥 저장한 것이든 Export (API)든)을 커스텀 워크플로로 고르면 돼요.";
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

    private void BtnColorCustomPick_Click(object sender, RoutedEventArgs e) => PickWorkflow(TxtColorCustom);
    private void BtnColorCustomClear_Click(object sender, RoutedEventArgs e) => TxtColorCustom.Text = "";

    /// <summary>워크플로 파일을 고른다. API 포맷과 화면 저장 포맷 둘 다 받는다(2026-11-09 — 화면 포맷은 실행 직전에 변환).
    /// 엔진이 켜져 있으면 그 자리에서 변환·점검까지 해 보고, 없는 노드·모델 파일이 있으면 바로 알려 준다.</summary>
    private async void PickWorkflow(System.Windows.Controls.TextBox target)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ComfyUI 워크플로 (*.json)|*.json",
            InitialDirectory = System.IO.Path.Combine(App.Settings.ComfyRoot, "ComfyUI", "user", "default", "workflows"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var workflow = NekoManga.Core.Comfy.CustomWorkflow.Load(dlg.FileName);   // 규약 검사 (LoadImage·SaveImage)
            bool screenFormat = NekoManga.Core.Comfy.WorkflowConverter.IsUiFormat(workflow);
            string kind = screenFormat ? "화면 저장 포맷이라 실행할 때 자동으로 변환해요" : "API 포맷";
            if (await NekoManga.Core.Comfy.EngineManager.IsAliveAsync(App.Settings))
            {
                var client = new NekoManga.Core.Comfy.ComfyClient($"http://127.0.0.1:{App.Settings.ComfyPort}");
                NekoManga.Core.Comfy.CustomWorkflow.Resolve(workflow, await client.GetObjectInfoAsync(CancellationToken.None));
                TxtWfStatus.Text = $"워크플로를 확인했어요 ({kind} · 노드와 모델 파일이 모두 있어요).";
            }
            else
                TxtWfStatus.Text = $"워크플로를 확인했어요 ({kind}). 엔진이 꺼져 있어 노드·모델 파일은 실행할 때 확인해요.";
            target.Text = dlg.FileName;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "워크플로 파일", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
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
        // 연결해 둔 ComfyUI가 엔진이면 [끄기]가 닫는 것이 그 ComfyUI 자체임을 알린다 — 위 안내는 엔진을 '앱이 알아서 켜고 끄는 뒤쪽 부품'이라고만 해서,
        // 직접 켜 두고 브라우저로 쓰던 ComfyUI가 꺼지는 것을 예상하기 어렵다(EngineManager.Stop은 그 폴더의 python을 누가 띄웠든 죽인다)
        bool linked = !string.IsNullOrWhiteSpace(s.LinkedComfyRoot);
        TxtEngineLinked.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        TxtEngineLinked.Text = linked
            ? $"연결해 둔 ComfyUI({s.LinkedComfyRoot.Trim()})를 엔진으로 쓰고 있어요 — [끄기]는 그 ComfyUI 자체를 닫아요. 직접 켜 두었거나 브라우저에서 쓰던 중이어도 꺼져요."
            : "";
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
        // 이 앱의 작업이 쓰는 중인 엔진을 끄면 그 작업은 도중에 연결 오류로 실패한다 — 끄기 전에 묻는다. 기본 버튼은 '아니요'(끄지 않기)
        if (NekoManga.Core.Comfy.EngineManager.InUse)
        {
            var linked = string.IsNullOrWhiteSpace(App.Settings.LinkedComfyRoot) ? "" : "\n(연결해 둔 ComfyUI 자체가 꺼져요.)";
            var r = MessageBox.Show(this,
                "지금 이 엔진을 쓰는 작업이 있어요 — 진행 중인 작업이거나, 닫은 결과 창에서 아직 돌고 있는 채색·검열 제거, 표지 색 뽑기예요.\n\n" +
                "엔진을 끄면 그 작업은 도중에 실패해요. 작업이 끝난 뒤에 끄면 안전해요. 그래도 지금 끌까요?" + linked,
                "엔진 끄기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }
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
}
