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
}
