using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>OnnxEnv 가운데 세션을 만들지 않는 부분 — GPU 사용 설정, 없는 모델의 처리.
/// GpuAllowed가 NEKO_FORCE_CPU 환경변수를 읽으므로 Env 묶음에서 돈다(묶음 정의는 AppSettingsTests.cs).
/// '자동'(Auto)은 레지스트리로 VRAM을 읽어 판정한다 — 그 값은 VramMb 이음새로 바꿔 넣어 본다.</summary>
[Collection("Env")]
public class OnnxEnvTests
{
    [Theory]
    [InlineData("On", true, null, true)]      // On = 항상 GPU — ComfyUI 작업(자동 채색)이 켜져 있어도
    [InlineData("Off", false, null, false)]   // Off = 항상 CPU — VRAM을 다툴 작업이 없어도
    [InlineData("On", false, "1", false)]     // NEKO_FORCE_CPU=1이 설정보다 우선
    [InlineData("On", false, "0", true)]      // 강제는 1일 때만
    public void GpuAllowed_ExplicitChoice_FollowsSettingUnlessCpuForced(string onnxGpu, bool autoColor, string? forceCpu, bool expected)
    {
        using var t = new TempDir();
        using var env = new OnnxEnv(new AppSettings { RuntimeRoot = t.Root, OnnxGpu = onnxGpu, EnableAutoColor = autoColor, EnableDecensor = false });

        Assert.Equal(expected, RuntimeTestHelpers.WithEnv("NEKO_FORCE_CPU", forceCpu, () => env.GpuAllowed));
    }

    [Theory]
    // 설정, CPU 강제, 전용 VRAM(MB), 이 실행이 ComfyUI를 쓰는가 → GPU
    [InlineData("Auto", false, 8000, false, true)]
    [InlineData("Auto", false, 8000, true, false)]       // ComfyUI와 VRAM을 다투지 않게
    [InlineData("Auto", false, 5999, false, false)]      // 6GB 미만은 공유 메모리로 밀려 CPU보다 느려질 수 있다
    [InlineData("Auto", false, 6000, false, true)]
    [InlineData("모르는 값", false, 8000, false, true)]   // 모르는 값은 자동으로
    [InlineData("On", false, 0, true, true)]
    [InlineData("Off", false, 24000, false, false)]
    [InlineData("On", true, 24000, false, false)]
    [InlineData("Auto", true, 24000, false, false)]
    public void Allowed_Rule(string onnxGpu, bool forceCpu, int vramMb, bool usesComfy, bool expected) =>
        Assert.Equal(expected, OnnxEnv.Allowed(onnxGpu, forceCpu, () => vramMb, usesComfy));

    [Fact]
    public void Allowed_ExplicitChoiceOrForcedCpu_NeverAsksForTheVram()
    {
        // VRAM은 레지스트리에서 읽는다 — 물을 필요가 없을 때는 묻지 않는다
        static int Boom() => throw new InvalidOperationException("VRAM을 물었다");
        Assert.True(OnnxEnv.Allowed("On", false, Boom, true));
        Assert.False(OnnxEnv.Allowed("Off", false, Boom, false));
        Assert.False(OnnxEnv.Allowed("Auto", true, Boom, false));
    }

    [Fact]
    public void GpuAllowed_Auto_GoesByThisRun_NotByWhatTheSettingsCouldUse()
    {
        // 글자 자리 다시 그리기를 켜 둔 설정: 본 작업은 ComfyUI를 쓰지만 결과 창의 다시 지우기·다시 식자는 쓰지 않는다 — 러너가 실행마다 알려 준다(JobUsesComfy, 검토 2026-11-26).
        // 설정만 보던 때는 그런 실행까지 내장 AI가 CPU로 돌았다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.Root, OnnxGpu = "Auto", EnableTranslate = true, TextRegen = true, EnableDecensor = false, EnableAutoColor = false };
        using var env = new OnnxEnv(s) { VramMb = () => 8000 };
        bool Gpu() => RuntimeTestHelpers.WithEnv("NEKO_FORCE_CPU", null, () => env.GpuAllowed);

        // 러너가 알려 주기 전(러너를 거치지 않는 길 — 결과 창의 제자리 지우기·부위 감지·진단 진입점): 글자 자리 다시 그리기는 세지 않는다.
        // 그 기능은 러너의 PrepareAsync(번역기를 쓰는 실행)에서만 돌고, 거기서는 늘 알려 준다(검토 2026-11-27 — 세면 그런 길까지 CPU로 돈다)
        Assert.True(Gpu());
        env.JobUsesComfy = true;
        Assert.False(Gpu());
        env.JobUsesComfy = false;
        Assert.True(Gpu());
    }

    [Fact]
    public void GpuAllowed_Auto_BeforeTheRunnerSaysAnything_ColorAndComfyDecensorStillCount()
    {
        // 채색·재생성은 러너 밖의 길(결과 창의 제자리 지우기가 재생성을 부른다)에서도 ComfyUI를 쓴다 — 전과 같이 CPU
        using var t = new TempDir();
        using var color = new OnnxEnv(new AppSettings { RuntimeRoot = t.Root, OnnxGpu = "Auto", EnableAutoColor = true, EnableDecensor = false }) { VramMb = () => 8000 };
        using var regen = new OnnxEnv(new AppSettings { RuntimeRoot = t.Root, OnnxGpu = "Auto", EnableAutoColor = false, EnableDecensor = true, DecensorMode = "ComfyRegen" }) { VramMb = () => 8000 };
        using var none = new OnnxEnv(new AppSettings { RuntimeRoot = t.Root, OnnxGpu = "Auto", EnableAutoColor = false, EnableDecensor = true, DecensorMode = "Basic" }) { VramMb = () => 8000 };

        Assert.False(RuntimeTestHelpers.WithEnv("NEKO_FORCE_CPU", null, () => color.GpuAllowed));
        Assert.False(RuntimeTestHelpers.WithEnv("NEKO_FORCE_CPU", null, () => regen.GpuAllowed));
        Assert.True(RuntimeTestHelpers.WithEnv("NEKO_FORCE_CPU", null, () => none.GpuAllowed));
    }

    [Fact]
    public void MissingModel_ThrowsFileNotFoundBeforeRunningAnything()
    {
        using var t = new TempDir();
        using var env = new OnnxEnv(new AppSettings { RuntimeRoot = t.At("빈 런타임 폴더") });
        const string file = "없는 모델 テスト.onnx";

        // 어떤 파일이 없는지 알려 줘야 설치 안내로 이어진다
        var ex = Assert.Throws<FileNotFoundException>(() => env.Get(file));
        Assert.Equal(env.ModelPath(file), ex.FileName);
        Assert.Throws<FileNotFoundException>(() => env.Get(file, cpuOnly: true));
        Assert.Throws<FileNotFoundException>(() => env.UsesGpu(file));

        // Guard는 세션부터 확인한다 — 모델이 없으면 본문을 한 번도 돌리지 않는다
        bool ran = false;
        Assert.Throws<FileNotFoundException>(() => env.Guard(() => ran = true, file));
        Assert.False(ran);
    }

    [Fact]
    public void Unfused_IsAnotherSessionNameOfTheSameModelFile()
    {
        // DirectML의 그래프 융합(모델을 한 덩어리로 묶어 실행)이 감지기·LaMa에서 GPU를 멈추게 한다(RTX 2070 SUPER, 2026-11-27 — 융합을 끄면 55ms·107ms로 돈다).
        // 융합을 끈 세션은 같은 파일의 다른 이름으로 따로 둔다 — 이름을 바꾸지 않은 쪽(검열 제거가 쓰는 세션)은 그대로다
        using var t = new TempDir();
        using var env = new OnnxEnv(new AppSettings { RuntimeRoot = t.At("런타임") });
        string name = OnnxEnv.Unfused("ctd.onnx");

        Assert.NotEqual("ctd.onnx", name);
        Assert.True(OnnxEnv.IsUnfused(name));
        Assert.False(OnnxEnv.IsUnfused("ctd.onnx"));
        Assert.Equal(env.ModelPath("ctd.onnx"), env.ModelPath(name));
        Assert.Equal(name, OnnxEnv.Unfused(name));   // 두 번 붙이지 않는다
    }

    [Fact]
    public void DmlSessionConfig_TurnsGraphFusionOff_OnlyForUnfusedNames()
    {
        Assert.Equal("1", OnnxEnv.DmlSessionConfig(OnnxEnv.Unfused("lama_manga.onnx"))["ep.dml.disable_graph_fusion"]);
        Assert.Empty(OnnxEnv.DmlSessionConfig("lama_manga.onnx"));
    }

    [Fact]
    public void Unfused_WhenTheGpuIsNotUsed_IsTheSameCpuSessionAsThePlainName()
    {
        // CPU로 도는 실행에서는 융합이 뜻이 없다 — 같은 모델을 이름만 달리해 두 번 올리지 않는다(모델이 없으면 원래 파일 이름으로 알린다)
        using var t = new TempDir();
        using var env = new OnnxEnv(new AppSettings { RuntimeRoot = t.At("런타임"), OnnxGpu = "Off" });

        var ex = Assert.Throws<FileNotFoundException>(() => env.Get(OnnxEnv.Unfused("ctd.onnx")));

        Assert.Equal(env.ModelPath("ctd.onnx"), ex.FileName);
        string unfused = OnnxEnv.Unfused("ctd.onnx");
        Assert.Equal(("ctd.onnx", false), OnnxEnv.ResolveSession(unfused, cpuOnly: false, gpuWanted: false, demoted: false));
        Assert.Equal(("ctd.onnx", true), OnnxEnv.ResolveSession(unfused, cpuOnly: true, gpuWanted: true, demoted: false));
        Assert.Equal((unfused, false), OnnxEnv.ResolveSession(unfused, cpuOnly: false, gpuWanted: true, demoted: false));
        // GPU에서 실패해 내려간 뒤: 원래 이름의 CPU 전용 세션 — 원래 이름의 보통 세션(융합한 GPU 세션일 수 있다)으로 가지 않는다
        Assert.Equal(("ctd.onnx", true), OnnxEnv.ResolveSession(unfused, cpuOnly: false, gpuWanted: true, demoted: true));
        // 융합을 끄지 않은 이름(검열 제거가 쓰는 세션들)은 손대지 않는다
        Assert.Equal(("lama_manga.onnx", false), OnnxEnv.ResolveSession("lama_manga.onnx", cpuOnly: false, gpuWanted: false, demoted: true));
    }
}
