using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>OnnxEnv 가운데 세션을 만들지 않는 부분 — GPU 사용 설정, 없는 모델의 처리.
/// GpuAllowed가 NEKO_FORCE_CPU 환경변수를 읽으므로 Env 묶음에서 돈다(묶음 정의는 AppSettingsTests.cs).
/// '자동'(Auto)은 레지스트리로 VRAM을 읽어 판정하므로 여기서 다루지 않는다.</summary>
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
}
