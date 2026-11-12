using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>AutoTune의 계획 세우기·표시·적용. nvidia-smi를 부르는 Build(s)는 부르지 않는다 —
/// 하드웨어를 묻지 않는 BuildFor(종류·이름·VRAM·RAM을 받아 계획을 세운다)와 직접 만든 계획(Plan)으로 보고,
/// 하드웨어에서 읽은 것을 계획까지 잇는 대목은 nvidia-smi 답·레지스트리 이름·RAM을 지어서 넘기는 Build(s, smi, adapters, ram)로 본다.</summary>
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

    [Fact]
    public void BuildFor_SingleRtx8Gb_SameSuggestionAsBefore()
    {
        // 이 PC(RTX 2070 SUPER 8GB, RAM 40GB)에 기본 설정 — v1.8.2가 내던 한 줄 그대로
        var plan = AutoTune.BuildFor(new AppSettings(), "nvidia", "NVIDIA GeForce RTX 2070 SUPER", 8192, 39.9);

        // 재생성 v3(2026-11-12)부터 쪽 전체 축소(regen-mp)를 제안하지 않는다 — 영역마다 1024로 잘라 보내므로. 8GB 등급은 채색 1.5MP가 기본값이라 바꿀 것이 없다
        Assert.Empty(plan.Changes);
        Assert.Equal(("nvidia", "NVIDIA GeForce RTX 2070 SUPER", 8192), (plan.Vendor, plan.GpuName, plan.VramMb));
    }

    [Fact]
    public void BuildFor_SeveralSupportedCards_TunesForTheCardTheEngineUses()
    {
        // nvidia-smi 첫 줄은 화면용 RTX 2060 6GB, 엔진이 쓸 카드는 RTX 4070 12GB — 예전에는 첫 줄의 6GB로 --lowvram·1.0MP를 권했다
        SystemCheck.Gpu[] smi =
        [
            new("NVIDIA GeForce RTX 2060", "580.88", 580, 6144),
            new("NVIDIA GeForce RTX 4070", "580.88", 580, 12282),
        ];
        var nv = SystemCheck.PickNvidiaGpu(smi)!;

        var plan = AutoTune.BuildFor(new AppSettings(), SystemCheck.JudgeVendor(smi, []).Vendor, nv.Name, nv.MemoryMb, 32);

        Assert.Equal(("nvidia", "NVIDIA GeForce RTX 4070", 12282), (plan.Vendor, plan.GpuName, plan.VramMb));
        var c = Assert.Single(plan.Changes);   // 12GB 등급: 채색 2.0MP(재생성 원본 크기는 기본값이라 목록에 없다)
        Assert.Equal(("color-mp", "2MP"), (c.Key, c.To));
        Assert.DoesNotContain(plan.Changes, x => x.Key == "extra-args");
    }

    [Fact]
    public void BuildFor_CardTooOldForAnyEngineBuild_TurnsGpuFeaturesOffInsteadOfLoweringThem()
    {
        // 케플러(GTX 680 2GB)는 'nvidia-old'로 감지되지만 구형 묶음도 못 돌린다 — 예전에는 쓸 수 있는 카드로 보고 --lowvram·1.0MP를 권했다
        var s = new AppSettings { EnableAutoColor = true, DecensorMode = "ComfyRegen", EnableSelfTraining = true, PrewarmComfyOnLaunch = true, KeepComfyWarm = true };

        var plan = AutoTune.BuildFor(s, "nvidia-old", "NVIDIA GeForce GTX 680", 2048, 16, gpuTooOld: true);

        Assert.Equal(["color-off", "decensor-basic", "selftrain-off", "prewarm-off", "keepwarm-off"], plan.Changes.Select(c => c.Key));
        Assert.All(plan.Changes, c => Assert.Contains("너무 오래", c.Why));
        AutoTune.Apply(s, plan);
        Assert.False(s.EnableAutoColor);
        Assert.Equal("Basic", s.DecensorMode);   // 번역·기본 검열 제거는 CPU로 그대로 쓴다
        Assert.False(s.EnableSelfTraining);

        // 같은 2GB라도 구형 묶음이 도는 카드(맥스웰 GTX 750 Ti)는 끄지 않고 낮춘다
        var maxwell = AutoTune.BuildFor(new AppSettings(), "nvidia-old", "NVIDIA GeForce GTX 750 Ti", 2048, 16);
        Assert.DoesNotContain(maxwell.Changes, c => c.Key == "color-off");
        Assert.Contains(maxwell.Changes, c => c.Key == "extra-args" && c.To == "--lowvram");
    }

    private static SystemCheck.Gpu Card(string name, int vramMb, string driver = "580.88") => new(name, driver, int.Parse(driver.Split('.')[0]), vramMb);

    [Theory]
    // nvidia-smi 첫 줄이 아니라 고른 카드(PickNvidiaGpu)의 이름과 VRAM — 점검 창에 보이는 카드와 같은 카드
    [InlineData("NVIDIA GeForce RTX 2060", 6144, "NVIDIA GeForce RTX 4070", 12282, "NVIDIA GeForce RTX 4070", 12282, "color-mp")]   // 12GB 등급: 채색 2.0MP
    [InlineData("NVIDIA GeForce RTX 4070", 12282, "NVIDIA GeForce RTX 2060", 6144, "NVIDIA GeForce RTX 4070", 12282, "color-mp")]
    // 느린 카드가 메모리만 큰 조합: 엔진이 쓸 3080의 10GB에 맞춘다(8~12GB 등급은 기본값 그대로 — 바꿀 것 없음) — 12GB에 맞추면 채색 2.0MP가 된다(검수 지적).
    // 재생성 v3부터 재생성 축소(regen-mp)는 제안하지 않는다
    [InlineData("NVIDIA GeForce RTX 3080", 10240, "NVIDIA GeForce RTX 3060", 12288, "NVIDIA GeForce RTX 3080", 10240, null)]
    [InlineData("NVIDIA GeForce RTX 3060", 12288, "NVIDIA GeForce RTX 3080", 10240, "NVIDIA GeForce RTX 3080", 10240, null)]
    // 이름으로 못 가르면 작은 VRAM에 맞춘다
    [InlineData("NVIDIA RTX A4000", 16376, "NVIDIA GeForce RTX 3080", 10240, "NVIDIA GeForce RTX 3080", 10240, null)]
    public void Build_SeveralCards_TunesForThePickedCardsVram(string first, int firstMb, string second, int secondMb, string name, int vramMb, string? onlyChange)
    {
        SystemCheck.Gpu[] smi = [Card(first, firstMb), Card(second, secondMb)];

        var plan = AutoTune.Build(new AppSettings(), smi, [first, second], 32);

        Assert.Equal(("nvidia", name, vramMb), (plan.Vendor, plan.GpuName, plan.VramMb));
        Assert.Equal(smi.Single(g => g.Name == name), SystemCheck.PickNvidiaGpu(smi));
        Assert.Equal(onlyChange == null ? [] : new[] { onlyChange }, plan.Changes.Select(c => c.Key));
    }

    [Fact]
    public void Build_GivenSmiList_DoesNotNeedNvidiaSmi()
    {
        // 설치 창·설정 창이 부르는 바로 그 오버로드 — nvidia-smi 답을 받으면 카드와 VRAM은 그 목록에서만 나온다(이 PC의 레지스트리·RAM과 상관없는 것만 본다)
        var plan = AutoTune.Build(new AppSettings(), [Card("NVIDIA GeForce RTX 2060", 6144), Card("NVIDIA GeForce RTX 4070", 12282)]);

        Assert.Equal(("nvidia", "NVIDIA GeForce RTX 4070", 12282), (plan.Vendor, plan.GpuName, plan.VramMb));
        Assert.Contains(plan.Changes, c => c.Key == "color-mp" && c.To == "2MP");
        Assert.DoesNotContain(plan.Changes, c => c.Key is "extra-args" or "color-off");
    }

    [Fact]
    public void Build_CardTooOldForAnyEngineBuild_TurnsGpuFeaturesOff()
    {
        // 케플러 + 47x 드라이버: Build가 '너무 오래됨' 판정을 계획까지 잇는다(BuildFor에 넘기지 않으면 2GB 카드로 보고 --lowvram·1.0MP를 권한다)
        var s = new AppSettings { EnableAutoColor = true, PrewarmComfyOnLaunch = true, KeepComfyWarm = true };
        var plan = AutoTune.Build(s, [Card("GeForce GTX 770", 2048, "474.11")], ["NVIDIA GeForce GTX 770", "Intel(R) HD Graphics 4600"], 32);

        Assert.Equal(("nvidia-old", "GeForce GTX 770", 2048), (plan.Vendor, plan.GpuName, plan.VramMb));
        Assert.Equal(["color-off", "prewarm-off", "keepwarm-off"], plan.Changes.Select(c => c.Key));
        Assert.All(plan.Changes, c => Assert.Contains("너무 오래", c.Why));

        // nvidia-smi가 없는 페르미(39x 드라이버)는 레지스트리 이름으로
        var fermi = AutoTune.Build(new AppSettings(), [], ["NVIDIA GeForce GTX 580 (시험용 이름)"], 32);
        Assert.Contains(fermi.Changes, c => c.Key == "color-off" && c.Why.Contains("너무 오래"));
    }

    [Fact]
    public void Build_OldCardTraceWithUnrecognizedAdapter_DoesNotTurnGpuFeaturesOff()
    {
        // 검수 지적: 카드를 바꾼 직후(옛 카드의 흔적 + 드라이버 없는 새 카드)에 채색을 꺼 버리면 드라이버를 깐 뒤에도 꺼진 채 남는다 — v1.8.2처럼 끄지 않는다
        var noSmi = AutoTune.Build(new AppSettings(), [], ["NVIDIA GeForce GTX 770 (시험용 이름)", "Microsoft Basic Display Adapter"], 32);
        Assert.Equal("nvidia-old", noSmi.Vendor);
        Assert.Empty(noSmi.Changes);   // VRAM을 모르니(레지스트리에 없는 이름) 낮출 것도 없다

        var oldDriver = AutoTune.Build(new AppSettings(), [Card("GeForce GT 710", 2048, "474.11")], ["NVIDIA GeForce GT 710", "Microsoft Basic Display Adapter"], 32);
        Assert.DoesNotContain(oldDriver.Changes, c => c.Key == "color-off");
        Assert.Contains(oldDriver.Changes, c => c.Key == "extra-args" && c.To == "--lowvram");   // 예전처럼 2GB 카드로 낮춘다
    }

    [Fact]
    public void Build_VendorChosenByUser_UsesThatVendorsAdapterName()
    {
        // 'AMD'를 골랐으면 NVIDIA 카드가 같이 있어도 Radeon 이름으로 판정한다 — RX 5700 XT는 ROCm이 지원하지 않는 세대
        var plan = AutoTune.Build(new AppSettings { GpuVendor = "amd" }, [Card("NVIDIA GeForce RTX 3060", 12288)],
            ["NVIDIA GeForce RTX 3060", "AMD Radeon RX 5700 XT (시험용 이름)"], 32);

        Assert.Equal(("amd", "AMD Radeon RX 5700 XT (시험용 이름)"), (plan.Vendor, plan.GpuName));
        Assert.Contains(plan.Changes, c => c.Key == "color-off" && c.Why.Contains("지원하지 않아요"));
    }
}
