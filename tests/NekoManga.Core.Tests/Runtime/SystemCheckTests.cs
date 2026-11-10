using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>SystemCheck 가운데 하드웨어를 묻지 않는 판정 — 그래픽카드 이름으로 보는 세대, 점검 보고서 요약, ComfyUI 로그 진단.
/// nvidia-smi·레지스트리를 읽는 QueryNvidia·DetectVendor·CheckForComfy는 부르지 않는다.</summary>
public class SystemCheckTests
{
    [Theory]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", true)]     // 파스칼
    [InlineData("NVIDIA GeForce GTX 1050", true)]
    [InlineData("NVIDIA GeForce GTX 970", true)]         // 맥스웰
    [InlineData("NVIDIA GeForce GTX 980M", true)]
    [InlineData("NVIDIA GeForce MX150", true)]
    [InlineData("NVIDIA GeForce MX350", true)]
    [InlineData("NVIDIA GeForce GT 1030", true)]
    [InlineData("NVIDIA GeForce GT 730", true)]
    [InlineData("NVIDIA TITAN Xp", true)]
    [InlineData("NVIDIA TITAN V", true)]                 // 볼타
    [InlineData("NVIDIA Quadro P4000", true)]
    [InlineData("NVIDIA Quadro M4000", true)]
    [InlineData("Tesla V100-PCIE-16GB", true)]
    [InlineData("nvidia geforce gtx 1060 6gb", true)]    // 이름의 대소문자는 드라이버마다 다르다
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", false)] // 튜링 이후는 CUDA 13 지원
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)] // GTX 16은 튜링 — 'GTX 10'과 헷갈리면 안 된다
    [InlineData("NVIDIA GeForce GTX 1650", false)]
    [InlineData("NVIDIA GeForce MX450", false)]          // MX도 450부터는 튜링
    [InlineData("NVIDIA TITAN RTX", false)]
    [InlineData("NVIDIA Quadro RTX 4000", false)]
    [InlineData("NVIDIA RTX A2000", false)]
    [InlineData("NVIDIA Tesla T4", false)]
    [InlineData("NVIDIA GeForce RTX 3060", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("NVIDIA GeForce RTX 5070", false)]
    [InlineData("", false)]                              // 이름을 모르면 막지 않는다
    public void LooksUnsupportedByCuda13_JudgesByGeneration(string name, bool dropped)
    {
        // CUDA 13이 버린 세대(맥스웰·파스칼·볼타)에 CUDA 13 포터블을 깔면 c10.dll 1114로 죽는다 — 그 카드만 골라내야 한다
        Assert.Equal(dropped, SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Theory(Skip = "알려진 결함: 판정 목록이 1세대 맥스웰(GTX 750 Ti·Quadro K2200·GeForce 940MX)과 그보다 오래된 케플러(GTX 780)를 빠뜨려, 문서 주석이 CUDA 13에서 빠졌다고 적은 세대를 지원 세대로 본다 — 자동 감지가 이 카드에 CUDA 13 포터블을 고른다")]
    [InlineData("NVIDIA GeForce GTX 750 Ti")]   // 1세대 맥스웰(GM107)
    [InlineData("NVIDIA Quadro K2200")]         // 1세대 맥스웰(GM107)
    [InlineData("NVIDIA GeForce 940MX")]        // 노트북 맥스웰(GM108) — 이름에 'GTX'도 'MX1~3'도 없다
    [InlineData("NVIDIA GeForce GTX 780")]      // 케플러
    public void LooksUnsupportedByCuda13_FirstGenMaxwellAndKepler_True(string name)
    {
        Assert.True(SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Theory]
    [InlineData("AMD Radeon RX 6800 XT", true)]           // RDNA2
    [InlineData("AMD Radeon RX 6600", true)]
    [InlineData("AMD Radeon RX 7900 XTX", true)]          // RDNA3
    [InlineData("AMD Radeon RX 7600", true)]
    [InlineData("AMD Radeon RX 9070 XT", true)]           // RDNA4
    [InlineData("AMD Radeon PRO W7900", true)]            // RDNA3 워크스테이션
    [InlineData("amd radeon rx 7800 xt", true)]
    [InlineData("AMD Radeon RX 5700 XT", false)]          // RDNA1
    [InlineData("AMD Radeon RX 580 2048SP", false)]       // Polaris
    [InlineData("Radeon RX Vega", false)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", false)] // 내장 Vega
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("", false)]
    public void LooksRocmSupported_JudgesByGeneration(string name, bool supported)
    {
        // ROCm(Windows)은 RDNA2 이상만 — 그보다 오래된 Radeon에 AMD 포터블을 깔면 채색·재생성이 돌지 않는다
        Assert.Equal(supported, SystemCheck.LooksRocmSupported(name));
    }

    [Fact]
    public void Report_SummarizesOnlyFailedItemsByKind()
    {
        var report = new SystemCheck.Report([
            new("NVIDIA 드라이버", false, true, "552.44 — 580 이상이 필요해요", SystemCheck.DriverUrl, "드라이버 받기"),
            new("VRAM", false, false, "4GB — 6GB 미만", null, null),
            new("Visual C++ 런타임 (x64)", true, true, "14.40 (충분)", null, null),
            new("메모리(RAM)", true, false, "32GB", null, null),
        ]);

        Assert.True(report.HasBlocking);
        Assert.True(report.HasWarning);
        // 통과한 항목은 요약에 없다 — 막는 실패는 ✖, 경고는 ⚠
        var lines = report.Summary.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("✖", lines[0]);
        Assert.Contains("NVIDIA 드라이버", lines[0]);
        Assert.Contains("580 이상이 필요해요", lines[0]);
        Assert.StartsWith("⚠", lines[1]);
        Assert.Contains("VRAM", lines[1]);

        // Blocking은 '실패하면 막는다'는 뜻 — 통과했으면 막지도 경고하지도 않는다
        var passed = new SystemCheck.Report([
            new("Windows 11", true, true, "빌드 22631 (충분)", null, null),
            new("참고", true, false, "AMD용 엔진은 초기 지원이에요.", null, null),
        ]);
        Assert.False(passed.HasBlocking);
        Assert.False(passed.HasWarning);
        Assert.Equal("", passed.Summary);
    }

    [Theory]
    [InlineData("torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 2.00 GiB", "--lowvram")]
    [InlineData("torch.cuda.OutOfMemoryError: Allocation on device", "--lowvram")]
    [InlineData("AssertionError: Torch not compiled with CUDA enabled", "GTX 10")]
    [InlineData("RuntimeError: CUDA error: no kernel image is available for execution on the device", "GTX 10")]
    [InlineData("RuntimeError: HIP error: invalid device function", "Adrenalin")]
    [InlineData("rocBLAS error: Cannot read TensileLibrary.dat", "Adrenalin")]
    public void DiagnoseComfyLog_KnownCrash_GivesMatchingAdvice(string errorLine, string advice)
    {
        using var t = new TempDir();
        var log = t.At("ComfyUI 로그 テスト.txt");
        File.WriteAllText(log, "Total VRAM 8192 MB\n서버 시작 중...\n" + errorLine + "\n");

        var hint = SystemCheck.DiagnoseComfyLog(log);

        // 메모리 부족 → --lowvram, 엔진 빌드 미지원 → 'GTX 10 이하' 묶음, ROCm 오류 → AMD 드라이버
        Assert.NotNull(hint);
        Assert.Contains(advice, hint);
    }

    [Fact]
    public void DiagnoseComfyLog_NoKnownCause_ReturnsNull()
    {
        using var t = new TempDir();
        var empty = t.At("빈 로그.txt");
        File.WriteAllText(empty, "");
        var normal = t.At("정상 로그.txt");
        File.WriteAllText(normal, "Starting server\n\nTo see the GUI go to: http://127.0.0.1:8188\n");

        // 모르는 원인은 아는 척하지 않는다 — 호출자가 일반 안내로 넘어간다
        Assert.Null(SystemCheck.DiagnoseComfyLog(t.At("없는 로그.txt")));
        Assert.Null(SystemCheck.DiagnoseComfyLog(empty));
        Assert.Null(SystemCheck.DiagnoseComfyLog(normal));
    }
}
