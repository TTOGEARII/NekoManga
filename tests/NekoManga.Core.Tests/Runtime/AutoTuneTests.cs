using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>AutoTune의 계획 표시와 적용. 계획을 세우는 Build는 그래픽카드를 레지스트리·nvidia-smi로 묻기 때문에 부르지 않고,
/// 계획(Plan)을 직접 만들어 사용자에게 보이는 글과 적용 동작만 본다.</summary>
public class AutoTuneTests
{
    [Theory]
    [InlineData("nvidia", "NVIDIA GeForce RTX 2070 SUPER", 8192, 31.9, "NVIDIA GeForce RTX 2070 SUPER · VRAM 8GB · RAM 32GB")]
    [InlineData("amd", "AMD Radeon RX 7600", 7680, 16.0, "AMD Radeon RX 7600 · VRAM 7.5GB · RAM 16GB")]   // GB는 1024MB 단위, 소수 한 자리까지
    [InlineData("nvidia", "NVIDIA GeForce RTX 3060", 12288, 0.0, "NVIDIA GeForce RTX 3060 · VRAM 12GB")]     // 모르는 RAM은 빼고 보인다
    [InlineData("nvidia-old", "", 6144, 0.0, "NVIDIA (이름 미확인) · VRAM 6GB")]                             // 이름을 못 읽으면 종류로
    [InlineData("amd", "", 0, 15.6, "AMD Radeon (이름 미확인) · RAM 16GB")]
    [InlineData("none", "", 0, 0.0, "전용 그래픽카드 없음")]
    public void HardwareLine_DescribesGpuVramAndRam(string vendor, string name, int vramMb, double ramGb, string expected)
    {
        var plan = new AutoTune.Plan(vendor, name, vramMb, ramGb, []);

        Assert.Equal(expected, RuntimeTestHelpers.InInvariantCulture(() => plan.HardwareLine));
    }

    [Fact]
    public void Summary_OneLinePerChangeOrNothingToDo()
    {
        var nothing = new AutoTune.Plan("nvidia", "NVIDIA GeForce RTX 4090", 24576, 64, []);
        Assert.Contains("바꿀 것이 없어요", nothing.Summary);

        AutoTune.Change[] changes =
        [
            new("prewarm-off", "엔진 미리 켜기", "켬", "끔", "RAM 8GB", x => x.PrewarmComfyOnLaunch = false),
            new("extra-args", "엔진 인자", "(없음)", "--lowvram", "VRAM 6GB — 메모리 절약 모드", x => x.ComfyExtraArgs = "--lowvram"),
        ];
        var lines = new AutoTune.Plan("nvidia", "NVIDIA GeForce GTX 1660", 6144, 8, changes).Summary.Split('\n');

        // 사용자는 줄마다 무엇이 어떻게 왜 바뀌는지 보고 적용 여부를 고른다
        Assert.Equal(changes.Length, lines.Length);
        for (int i = 0; i < changes.Length; i++)
        {
            var (line, c) = (lines[i], changes[i]);
            Assert.StartsWith("• " + c.Label, line);
            Assert.True(line.IndexOf(c.From, StringComparison.Ordinal) < line.IndexOf(c.To, StringComparison.Ordinal), line);   // 바뀌기 전 → 후
            Assert.Contains(c.Why, line);
        }
    }

    [Fact]
    public void Apply_ChangesOnlyWhatThePlanLists()
    {
        var s = new AppSettings { ColorMegapixels = 1.5, ComfyExtraArgs = "", KeepComfyWarm = true };
        var before = s.ShallowClone();

        AutoTune.Apply(s, new AutoTune.Plan("nvidia", "NVIDIA GeForce RTX 4090", 24576, 64, []));
        Assert.Empty(RuntimeTestHelpers.Differences(before, s));   // 빈 계획은 아무것도 바꾸지 않는다

        AutoTune.Apply(s, new AutoTune.Plan("nvidia", "NVIDIA GeForce GTX 1660", 6144, 8,
        [
            new("color-mp", "채색 해상도", "1.5MP", "1MP", "VRAM 6GB", x => x.ColorMegapixels = 1.0),
            new("extra-args", "엔진 인자", "(없음)", "--lowvram", "VRAM 6GB", x => x.ComfyExtraArgs = "--lowvram"),
            new("keepwarm-off", "엔진 꺼두지 않기", "켬", "끔", "RAM 8GB", x => x.KeepComfyWarm = false),
        ]));

        Assert.Equal(1.0, s.ColorMegapixels);
        Assert.Equal("--lowvram", s.ComfyExtraArgs);
        Assert.False(s.KeepComfyWarm);
        // 계획에 없는 설정(사용자가 손댄 값)은 그대로
        Assert.Equal(
            new[] { nameof(AppSettings.ColorMegapixels), nameof(AppSettings.ComfyExtraArgs), nameof(AppSettings.KeepComfyWarm) },
            RuntimeTestHelpers.Differences(before, s).Select(d => d[..d.IndexOf(':')]).Order(StringComparer.Ordinal));
    }
}
