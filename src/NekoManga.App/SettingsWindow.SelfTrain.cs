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

// 검열 제거 탭의 자가 학습 구역 — 재료 현황·폴더 열기, 학습 시작·중단, 결과 적용·되돌리기
public partial class SettingsWindow
{
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
}
