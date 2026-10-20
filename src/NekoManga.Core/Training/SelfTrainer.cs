using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;

namespace NekoManga.Core.Training;

/// <summary>반자동 자가 학습 (2026-10-20, 사용자 요청 "검토 재료로 감지가 계속 좋아지게"): 검토 창이 모은 재료(PipelineRunner.CorrectionsDir)로
/// ComfyUI의 python_embeded(torch+ultralytics 내장)에서 assets/training/selftrain.py를 돌려 감지 모델을 짧게 미세조정하고, 보류 페이지 전/후 수치를 받아
/// 사용자가 [적용]을 누를 때만 런타임 모델을 바꾼다(이전 모델은 백업 → [되돌리기]). 완전 자동 교체는 일부러 안 한다 — 재학습이 퇴행하는 일이 실측에서 잦았다.
///
/// 파일: &lt;DataDir&gt;\training\selftrain\{work\, candidate.onnx, candidate.pt, result.json, train.log, state.json}. 기준 가중치는 &lt;RuntimeRoot&gt;\models\censor_seg.pt(c11).
/// 학습이 끝나면 candidate.pt가 다음 학습의 시작점이 된다(적용했을 때만 — state.json의 basePt).</summary>
public sealed class SelfTrainer(AppSettings settings)
{
    public static string Dir => Path.Combine(AppSettings.DataDir, "training", "selftrain");
    public string RuntimeOnnx => Path.Combine(settings.RuntimeRoot, "models", Engine.CensorNet.ModelFile);
    public string RuntimePt => Path.Combine(settings.RuntimeRoot, "models", "censor_seg.pt");
    public string CandidateOnnx => Path.Combine(Dir, "candidate.onnx");
    public string CandidatePt => Path.Combine(Dir, "candidate.pt");
    public string BackupOnnx => Path.Combine(Dir, "previous.onnx");
    public string BackupPt => Path.Combine(Dir, "previous.pt");
    public string ResultPath => Path.Combine(Dir, "result.json");
    public string LogPath => Path.Combine(Dir, "train.log");
    public string PythonExe => Path.Combine(settings.ComfyRoot, "python_embeded", "python.exe");
    public string ScriptPath => Path.Combine(AppContext.BaseDirectory, "assets", "training", "selftrain.py");

    public sealed record Material(int Pages, int Changed, int Skipped, DateTime? Last);
    public sealed record Metrics(int Hits, int Total, long FpPx, int CleanPages, int CleanFired)
    {
        public string Text => Total == 0 && CleanPages == 0 ? "보류 페이지 없음"
            : $"적중 {Hits}/{Total} · 라벨 밖 발화 {FpPx:N0}px" + (CleanPages > 0 ? $" · 깨끗 {CleanPages}쪽 중 발화 {CleanFired}" : "");
    }
    public sealed record Result(bool Ok, string? Error, int Pages, int TrainPages, int HoldoutPages, int Crops, Metrics? Before, Metrics? After, double Minutes);

    /// <summary>모인 재료 요약 — 설정 창 상태줄용.</summary>
    public static Material CountMaterial()
    {
        int pages = 0, changed = 0, skipped = 0; DateTime? last = null;
        var root = Pipeline.PipelineRunner.CorrectionsDir;
        if (!Directory.Exists(root)) return new(0, 0, 0, null);
        var seen = new HashSet<string>();
        foreach (var idx in Directory.EnumerateFiles(root, "index.jsonl", SearchOption.AllDirectories))
        {
            last = last == null ? File.GetLastWriteTime(idx) : (File.GetLastWriteTime(idx) > last ? File.GetLastWriteTime(idx) : last);
            foreach (var line in File.ReadLines(idx))
            {
                JsonNode? n; try { n = JsonNode.Parse(line); } catch { continue; }
                var src = n?["source"]?.GetValue<string>() ?? n?["stem"]?.GetValue<string>() ?? "";
                if (!seen.Add(src)) continue;
                pages++;
                if ((n?["changed_px"]?.GetValue<int>() ?? 0) > 0) changed++;
                if (n?["skip"]?.GetValue<bool>() == true) skipped++;
            }
        }
        return new(pages, changed, skipped, last);
    }

    public bool CanTrain(out string why)
    {
        if (!settings.ComfyAvailable || !File.Exists(PythonExe)) { why = "학습은 ComfyUI에 딸린 파이썬으로 돌아요 — 설정 → 구성요소에서 ComfyUI를 설치해 주세요."; return false; }
        if (!File.Exists(RuntimePt) && !File.Exists(CandidatePt)) { why = $"학습 기준 가중치가 없어요: {RuntimePt}"; return false; }
        if (!File.Exists(ScriptPath)) { why = $"학습 스크립트가 없어요: {ScriptPath}"; return false; }
        var m = CountMaterial();
        if (m.Pages < settings.SelfTrainMinPages) { why = $"재료가 {m.Pages}쪽이라 아직 부족해요 (최소 {settings.SelfTrainMinPages}쪽). 검토 창을 거친 페이지가 쌓이면 켜져요."; return false; }
        why = ""; return true;
    }

    public Result? LastResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(ResultPath))!;
            static Metrics? M(JsonNode? x) => x == null ? null : new(x["hits"]!.GetValue<int>(), x["total"]!.GetValue<int>(), x["fp_px"]!.GetValue<long>(),
                x["clean_pages"]?.GetValue<int>() ?? 0, x["clean_fired"]?.GetValue<int>() ?? 0);
            return new(n["ok"]?.GetValue<bool>() ?? false, n["error"]?.GetValue<string>(), n["pages"]?.GetValue<int>() ?? 0, n["train_pages"]?.GetValue<int>() ?? 0,
                n["holdout_pages"]?.GetValue<int>() ?? 0, n["crops"]?.GetValue<int>() ?? 0, M(n["before"]), M(n["after"]), n["minutes"]?.GetValue<double>() ?? 0);
        }
        catch { return null; }
    }

    /// <summary>학습을 돌린다 (수 분~수십 분). GPU를 비우려고 ComfyUI 엔진을 먼저 끈다. 진행 줄("PROGRESS …")은 status로, 전체 출력은 train.log로.</summary>
    public async Task<Result> TrainAsync(IProgress<string>? status, CancellationToken ct)
    {
        if (!CanTrain(out var why)) throw new InvalidOperationException(why);
        Directory.CreateDirectory(Dir);
        var work = Path.Combine(Dir, "work"); Directory.CreateDirectory(work);
        foreach (var f in new[] { ResultPath, CandidateOnnx, CandidatePt }) if (File.Exists(f)) File.Delete(f);
        // VRAM: 엔진이 모델을 들고 있으면 학습이 메모리 부족으로 죽는다 → 끄고 시작
        if (await EngineManager.IsAliveAsync(settings)) { status?.Report("GPU를 비우려고 채색 엔진을 끄는 중..."); EngineManager.Stop(settings); }
        var init = File.Exists(RuntimePt) ? RuntimePt : CandidatePt;
        var psi = new ProcessStartInfo(PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = work, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-X", "utf8", ScriptPath, "--corrections", Pipeline.PipelineRunner.CorrectionsDir, "--work", work, "--init", init,
                     "--out-onnx", CandidateOnnx, "--epochs", settings.SelfTrainEpochs.ToString(), "--lr0", settings.SelfTrainLr.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--freeze", settings.SelfTrainFreeze.ToString(), "--min-pages", settings.SelfTrainMinPages.ToString() })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var log = new StreamWriter(LogPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        using var p = Process.Start(psi)!;
        var readOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                log.WriteLine(line);
                if (line.StartsWith("PROGRESS ")) status?.Report(line[9..].Replace("train ", "").Trim());
            }
        });
        var readErr = Task.Run(async () =>
        {
            string? line; int n = 0;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                log.WriteLine(line);
                // ultralytics 진행바는 stderr로 온다 — 에폭 줄만 간추려 보여 준다
                if (++n % 20 == 0 && line.Contains('/') && line.Contains("it/s")) status?.Report("학습 중... " + line.Trim()[..Math.Min(60, line.Trim().Length)]);
            }
        });
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        await Task.WhenAll(readOut, readErr);
        var r = LastResult() ?? new Result(false, $"결과 파일이 없어요 (exit {p.ExitCode}) — {LogPath} 를 확인해 주세요.", 0, 0, 0, 0, null, null, 0);
        return r;
    }

    /// <summary>후보 모델을 런타임에 적용한다 — 이전 모델은 previous.onnx/.pt로 보관해 되돌릴 수 있다. 후보 .pt는 다음 학습의 기준이 된다.</summary>
    public void Apply()
    {
        if (!File.Exists(CandidateOnnx)) throw new FileNotFoundException("적용할 후보 모델이 없어요 — 먼저 학습을 돌려 주세요.");
        File.Copy(RuntimeOnnx, BackupOnnx, overwrite: true);
        if (File.Exists(RuntimePt)) File.Copy(RuntimePt, BackupPt, overwrite: true);
        File.Copy(CandidateOnnx, RuntimeOnnx, overwrite: true);
        if (File.Exists(CandidatePt)) File.Copy(CandidatePt, RuntimePt, overwrite: true);
        File.WriteAllText(Path.Combine(Dir, "state.json"), JsonSerializer.Serialize(new { applied = DateTime.Now, onnx = CandidateOnnx }));
    }

    public bool CanRollback => File.Exists(BackupOnnx);

    public void Rollback()
    {
        if (!CanRollback) throw new FileNotFoundException("되돌릴 이전 모델이 없어요.");
        File.Copy(BackupOnnx, RuntimeOnnx, overwrite: true);
        if (File.Exists(BackupPt)) File.Copy(BackupPt, RuntimePt, overwrite: true);
        File.Delete(BackupOnnx); if (File.Exists(BackupPt)) File.Delete(BackupPt);
    }
}
