using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;
using NekoManga.Core.Training;

namespace NekoManga.Core.Tests;

/// <summary>SelfTrainer 가운데 사용자 데이터 폴더(%AppData%\NekoManga\training)를 건드리지 않는 부분 —
/// 런타임 모델·파이썬 경로의 약속, 학습 가능 판정의 첫 관문, 전/후 수치 표시.</summary>
public class SelfTrainerTests
{
    [Fact]
    public void ModelPaths_MatchWhereEngineReadsAndInstallerWrites()
    {
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("런타임 テスト") };
        var trainer = new SelfTrainer(s);
        var installer = new ComponentInstaller(s);
        using var env = new OnnxEnv(s);

        // [적용]이 덮어쓰는 모델 = 검열 감지 엔진이 읽는 모델 — 다르면 학습 결과가 쓰이지 않는다
        Assert.Equal(env.ModelPath(CensorNet.ModelFile), trainer.RuntimeOnnx);
        Assert.Equal(installer.Resolve(ComponentCatalog.ModelCensor.Target), trainer.RuntimeOnnx);
        // 학습의 시작점 가중치 = 설치기가 ComfyUI 묶음으로 받아 두는 파일
        Assert.Equal(installer.Resolve(ComponentCatalog.ModelCensorPt.Target), trainer.RuntimePt);
    }

    [Fact]
    public void PythonExe_IsEmbeddedPythonOfComfyInUse()
    {
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("런타임"), LinkedComfyRoot = t.At("기존 ComfyUI") };
        var trainer = new SelfTrainer(s);

        // 학습은 ComfyUI의 python_embeded로 돈다 — ComfyAvailable이 확인하는 바로 그 파이썬이어야 한다(연결된 설치가 우선)
        RuntimeTestHelpers.Touch(Path.Combine(s.ComfyRoot, "ComfyUI", "main.py"));
        Assert.False(s.ComfyAvailable);
        RuntimeTestHelpers.Touch(trainer.PythonExe);
        Assert.True(s.ComfyAvailable);
        Assert.StartsWith(s.LinkedComfyRoot + Path.DirectorySeparatorChar, trainer.PythonExe);
    }

    [Fact]
    public void CanTrain_WithoutComfyUi_RefusesWithInstallHint()
    {
        using var t = new TempDir();
        var trainer = new SelfTrainer(new AppSettings { RuntimeRoot = t.At("빈 런타임"), EnableSelfTraining = true });

        Assert.False(trainer.CanTrain(out var why));
        Assert.Contains("ComfyUI", why);   // 무엇을 설치하면 되는지 알려 준다
    }

    [Theory]
    [InlineData(0, 0, 0L, 0, 0, "보류 페이지 없음")]
    [InlineData(3, 10, 12345L, 0, 0, "적중 3/10 · 라벨 밖 발화 12,345px")]
    [InlineData(3, 10, 0L, 4, 1, "적중 3/10 · 라벨 밖 발화 0px · 깨끗 4쪽 중 발화 1")]
    [InlineData(0, 0, 0L, 2, 0, "적중 0/0 · 라벨 밖 발화 0px · 깨끗 2쪽 중 발화 0")]   // 깨끗한 페이지만 보류해도 수치는 있다
    public void MetricsText_ShowsHoldoutNumbers(int hits, int total, long fpPx, int cleanPages, int cleanFired, string expected)
    {
        var m = new SelfTrainer.Metrics(hits, total, fpPx, cleanPages, cleanFired);

        Assert.Equal(expected, RuntimeTestHelpers.InInvariantCulture(() => m.Text));
    }

    // ── 스크립트(assets/training/selftrain.py)가 적는 진행 줄·실패 까닭을 화면 글로 ──
    // 스크립트는 한국어로만 적는다. 그 글을 그대로 보이면 다른 언어의 화면에 한국어가 섞여서, 단계와 값만 받아 앱이 짓는다(한국어 화면의 글은 전과 같다)

    [Theory]
    // 설명('|' 뒤)은 일부러 다른 글로 준다 — 앱이 단계와 값으로 지은 글이지 스크립트의 설명을 옮긴 것이 아님을 본다
    [InlineData("scan 12 | 스크립트의 글", "scan 재료 12쪽")]
    [InlineData("split 10 2 | 스크립트의 글", "split 학습 10쪽 · 보류 2쪽")]
    [InlineData("data 55 80 | 스크립트의 글", "data 크롭 55장 · 라벨 80개")]
    [InlineData("train 5 0.0002 10 | 스크립트의 글", "미세조정 시작 (5에폭, lr 0.0002, freeze 10)")]   // train은 단계 이름을 뺀다
    [InlineData("export | 스크립트의 글", "export ONNX로 내보내는 중")]
    [InlineData("eval 2 | 스크립트의 글", "eval 보류 2쪽 전/후 비교")]
    [InlineData("done 3.4 | 스크립트의 글", "done 완료 3.4분")]
    public void ProgressText_BuildsTheLineFromStageAndValues(string fromScript, string shown) =>
        Assert.Equal(shown, SelfTrainer.ProgressText(fromScript));

    [Theory]
    [InlineData("scan 재료 12쪽", "scan 재료 12쪽")]                      // 값이 따로 없는 옛 꼴 — 받은 글 그대로
    [InlineData("train 미세조정 시작 (5에폭)", "미세조정 시작 (5에폭)")]
    [InlineData("warmup 3 | 모르는 단계", "warmup 모르는 단계")]          // 앱이 모르는 단계 — 스크립트의 설명을 보인다
    [InlineData("scan | 재료 ?쪽", "scan 재료 ?쪽")]                      // 값이 빠졌다 — 던지지 않는다
    public void ProgressText_OldOrUnknownLines_FallBackToWhatTheScriptSaid(string fromScript, string shown) =>
        Assert.Equal(shown, SelfTrainer.ProgressText(fromScript));

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\",\"code\":\"few_pages\",\"args\":[3,20]}", "재료가 3쪽이라 아직 부족하다냥 (최소 20쪽)")]
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\",\"code\":\"few_crops\",\"args\":[]}", "학습 크롭이 너무 적다냥 (마스크가 있는 페이지가 더 필요하다냥)")]
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\",\"code\":\"no_weights\"}", "학습 결과 가중치가 없다냥 (로그를 확인해 줘냥)")]
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\"}", "스크립트의 글")]                              // 코드 없는 옛 결과 파일
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\",\"code\":\"something_new\"}", "스크립트의 글")]   // 모르는 코드
    [InlineData("{\"ok\":false,\"error\":\"스크립트의 글\",\"code\":7}", "스크립트의 글")]                    // 글이 아닌 코드
    [InlineData("{\"ok\":false,\"code\":\"few_pages\",\"args\":[3]}", "재료가 3쪽이라 아직 부족하다냥 (최소 ?쪽)")]   // 값이 모자라도 던지지 않는다
    [InlineData("{\"ok\":true}", null)]
    public void ErrorText_KnownCodes_AreBuiltByTheApp_OthersKeepTheScriptText(string resultJson, string? shown) =>
        Assert.Equal(shown, SelfTrainer.ErrorText(System.Text.Json.Nodes.JsonNode.Parse(resultJson)!));

    [Fact]
    public void Script_WritesProgressAndFailuresInTheShapeTheAppReads()
    {
        // 앱과 스크립트는 따로 고쳐진다 — 스크립트가 값을 싣지 않거나 코드를 달지 않으면 화면이 다시 한국어 글(스크립트의 설명)로 돌아간다
        var script = File.ReadAllText(Path.Combine(LocSourceScan.FindRoot(), "assets", "training", "selftrain.py"));

        Assert.Contains("| {msg}", script);   // 진행 줄 = "PROGRESS <단계> <값 …> | <설명>"
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var stage in new[] { "scan", "split", "data", "train", "eval", "done" })
        {
            var calls = lines.Where(l => l.StartsWith($"log(\"{stage}\", ")).ToList();
            Assert.NotEmpty(calls);
            // 값을 쓰는 단계의 줄은 전부 설명 글 뒤에 값이 따라온다 — 값 없이 적으면 앱이 '?'를 끼운 글을 보인다(값 없는 알림은 앱이 모르는 단계 이름으로 적는다)
            Assert.All(calls, l => Assert.Matches(@"\"", [^""]+\)\s*$", l));
        }
        foreach (var code in new[] { "few_pages", "few_crops", "no_weights" })
            Assert.Contains($"code=\"{code}\"", script);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(script, @"dict\(ok=False").Count);   // 실패는 셋 — 더하면 코드도 더한다(SelfTrainer.ErrorText)
    }

    [Fact]
    public void Script_DescriptionsAreTheSameTextTheAppBuilds()
    {
        // 스크립트가 적는 한국어 설명·실패 글과 앱이 짓는 한국어 글은 같은 글이어야 한다 — 앱이 모르는 꼴(옛 앱 + 새 스크립트, 값 없는 줄)로 물러나면 스크립트의 글이 그대로 보이므로.
        // 스크립트 글의 구멍({…})을 {0}·{1}…으로 바꾼 것이 앱의 Loc.F 열쇠다 — 번역표에 그 열쇠가 있는지로 본다(표의 열쇠 = 소스의 글, LocCoverageTests)
        var script = File.ReadAllText(Path.Combine(LocSourceScan.FindRoot(), "assets", "training", "selftrain.py"));
        var en = Loc.Table("en")!;
        const string Quoted = "\"([^\"]+)\"";
        var texts = System.Text.RegularExpressions.Regex.Matches(script, "log\\(\"(?:scan|split|data|train|export|eval|done)\", f?" + Quoted)
            .Concat(System.Text.RegularExpressions.Regex.Matches(script, "error=f?" + Quoted))
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(7 + 3, texts.Count);   // 진행 단계 일곱 + 실패 셋
        foreach (var text in texts)
        {
            int hole = 0;
            string key = System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", _ => "{" + hole++ + "}");
            Assert.True(en.ContainsKey(key), $"selftrain.py의 글 \"{text}\"이 앱이 짓는 글(열쇠 \"{key}\")과 달라요 — SelfTrainer.ProgressText·ErrorText와 같이 고친다");
        }
    }
}
