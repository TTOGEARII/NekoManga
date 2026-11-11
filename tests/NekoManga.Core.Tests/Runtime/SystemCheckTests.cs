using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>SystemCheck 가운데 하드웨어를 묻지 않는 판정 — 그래픽카드 이름으로 보는 세대·종류, nvidia-smi 출력 읽기, 점검 보고서 요약, ComfyUI 로그 진단.
/// nvidia-smi를 실제로 부르는 QueryNvidia·DetectVendor()·CheckForComfy(vendor)는 부르지 않는다 — 그 판정은 떼어 낸 함수
/// (JudgeVendor·PickNvidiaGpu·PickNvidia·BlocksCuda13·NvidiaTooOld·NvidiaItems·ParseNvidiaSmi)로 보고, nvidia-smi 답은 목록으로 지어서 넘긴다.</summary>
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
        // CUDA 13이 버린 세대(맥스웰·파스칼·볼타)는 CUDA 13 포터블로 채색 엔진을 돌릴 수 없다 — 그 카드만 골라내야 한다
        Assert.Equal(dropped, SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 750 Ti")]   // 1세대 맥스웰(GM107)
    [InlineData("NVIDIA Quadro K2200")]         // 1세대 맥스웰(GM107)
    [InlineData("NVIDIA GeForce 940MX")]        // 노트북 맥스웰(GM108) — 이름에 'GTX'도 'MX1~3'도 없다
    [InlineData("NVIDIA GeForce GTX 780")]      // 케플러
    public void LooksUnsupportedByCuda13_FirstGenMaxwellAndKepler_True(string name)
    {
        // 옛 판정 목록이 이 세대를 빠뜨려 자동 감지가 이 카드들에 CUDA 13 포터블을 골랐다 — CUDA 13 빌드가 지원하지 않는 세대라 채색 엔진이 돌지 않는다
        // (어떤 오류로 멈추는지는 기록이 없다. 기록된 c10.dll 1114 사고는 지원 세대인 GTX 1660에 옛 드라이버를 쓴 경우다)
        Assert.True(SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Theory]
    // CUDA 13이 버린 세대(compute capability 7.5 미만) — 레지스트리 DriverDesc·nvidia-smi가 실제로 내는 이름 꼴
    [InlineData("NVIDIA GeForce GTX 780 Ti", true)]         // 케플러
    [InlineData("GeForce GTX 680", true)]                   // 옛 드라이버의 nvidia-smi 이름은 앞에 NVIDIA가 없다
    [InlineData("NVIDIA GeForce GTX 660 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 650", true)]
    [InlineData("NVIDIA GeForce GT 640", true)]
    [InlineData("NVIDIA GeForce GT 710", true)]
    [InlineData("NVIDIA GeForce GTX TITAN Black", true)]
    [InlineData("NVIDIA GeForce GTX 880M", true)]           // 케플러 노트북
    [InlineData("NVIDIA GeForce GT 750M", true)]
    [InlineData("NVIDIA GeForce 710M", true)]
    [InlineData("Quadro K4000", true)]
    [InlineData("Tesla K80", true)]
    [InlineData("NVIDIA GRID K520", true)]                  // 클라우드 가상 GPU
    [InlineData("NVIDIA GeForce GTX 745", true)]            // 1세대 맥스웰
    [InlineData("NVIDIA GeForce GTX 750", true)]
    [InlineData("Quadro K2200", true)]
    [InlineData("Quadro K620", true)]
    [InlineData("NVIDIA GeForce 930MX", true)]              // 노트북 맥스웰 — 시리즈 글자 없이 숫자만
    [InlineData("NVIDIA GeForce 920MX", true)]
    [InlineData("NVIDIA GeForce 940M", true)]
    [InlineData("NVIDIA GeForce 840M", true)]
    [InlineData("NVIDIA GeForce GTX 960M", true)]
    [InlineData("NVIDIA GeForce GTX 860M", true)]
    [InlineData("NVIDIA GeForce MX110", true)]
    [InlineData("NVIDIA GeForce MX130", true)]
    [InlineData("NVIDIA GeForce GTX 980 Ti", true)]         // 2세대 맥스웰
    [InlineData("NVIDIA GeForce GTX TITAN X", true)]
    [InlineData("Quadro M1000M", true)]
    [InlineData("Tesla M60", true)]
    [InlineData("NVIDIA NVS 810", true)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti with Max-Q Design", true)]   // 파스칼
    [InlineData("NVIDIA GeForce GTX 1070 Ti", true)]
    [InlineData("NVIDIA GeForce GT 1010", true)]
    [InlineData("NVIDIA GeForce MX230", true)]
    [InlineData("NVIDIA GeForce MX330", true)]
    [InlineData("Quadro P1000", true)]
    [InlineData("NVIDIA Quadro P620", true)]
    [InlineData("NVIDIA Quadro GP100", true)]
    [InlineData("NVIDIA TITAN X (Pascal)", true)]
    [InlineData("Tesla P40", true)]
    [InlineData("Tesla P100-PCIE-16GB", true)]
    [InlineData("NVIDIA P106-100", true)]                   // 채굴 카드(파스칼)
    [InlineData("NVIDIA Quadro GV100", true)]               // 볼타
    [InlineData("Tesla V100-SXM2-16GB", true)]
    [InlineData("NVIDIA GeForce GTX 580", true)]            // 페르미 — 그보다 옛것도 물론 미지원
    [InlineData("NVIDIA Quadro 4000", true)]
    // 튜링 이후 — CUDA 13 지원. 막으면 멀쩡한 카드에 구형 묶음을 받거나 설치가 막힌다
    [InlineData("GeForce GTX 1650", false)]
    [InlineData("NVIDIA GeForce GTX 1630", false)]
    [InlineData("NVIDIA GeForce GTX 1650 Ti", false)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti with Max-Q Design", false)]
    [InlineData("NVIDIA GeForce MX550", false)]
    [InlineData("NVIDIA GeForce MX570 A", false)]           // 암페어
    [InlineData("NVIDIA T400", false)]                      // 이름에 Quadro가 없는 튜링 워크스테이션
    [InlineData("NVIDIA T400 4GB", false)]
    [InlineData("NVIDIA T600", false)]
    [InlineData("NVIDIA T1000 8GB", false)]
    [InlineData("NVIDIA T550 Laptop GPU", false)]
    [InlineData("Quadro T1000", false)]
    [InlineData("Quadro T2000 with Max-Q Design", false)]
    [InlineData("Quadro RTX 6000", false)]
    [InlineData("NVIDIA Quadro RTX 3000 with Max-Q Design", false)]
    [InlineData("Tesla T4", false)]
    [InlineData("GRID T4-16Q", false)]
    [InlineData("NVIDIA CMP 30HX", false)]
    [InlineData("NVIDIA GeForce RTX 2060", false)]
    [InlineData("NVIDIA GeForce RTX 2050", false)]
    [InlineData("NVIDIA GeForce RTX 3050 Ti Laptop GPU", false)]
    [InlineData("NVIDIA RTX A2000 12GB", false)]
    [InlineData("NVIDIA RTX A500 Laptop GPU", false)]
    [InlineData("NVIDIA A100-SXM4-40GB", false)]
    [InlineData("NVIDIA A10", false)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", false)]
    [InlineData("NVIDIA RTX 6000 Ada Generation", false)]
    [InlineData("NVIDIA L4", false)]
    [InlineData("NVIDIA H100 PCIe", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false)]
    [InlineData("NVIDIA GeForce RTX 5090", false)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", false)]
    [InlineData("NVIDIA", false)]                           // 모델명을 모르면 막지 않는다
    public void LooksUnsupportedByCuda13_RealAdapterNames_OnlyBelowTuringDropped(string name, bool dropped)
    {
        // 옛 세대는 닫힌 목록이라 빠짐없이 골라내고, 튜링 이후는 이름 꼴이 비슷해도(GTX 16xx·MX450 이상·T 시리즈·TITAN RTX) 막지 않는다
        Assert.Equal(dropped, SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Fact]
    public void ParseNvidiaSmi_ReadsEveryCardLine()
    {
        // 카드 한 장에 한 줄 — 첫 줄만 읽으면 앞 순서(PCI 순)의 화면용 옛 카드 하나로 PC 전체를 판정했다
        Assert.Equal([
            new SystemCheck.Gpu("NVIDIA Quadro K620", "580.88", 580, 2048),
            new SystemCheck.Gpu("NVIDIA GeForce RTX 3060", "580.88", 580, 12288),
        ], SystemCheck.ParseNvidiaSmi("580.88, NVIDIA Quadro K620, 2048\r\n580.88, NVIDIA GeForce RTX 3060, 12288\r\n"));
        // 이 PC가 실제로 내는 꼴
        Assert.Equal([new SystemCheck.Gpu("NVIDIA GeForce RTX 2070 SUPER", "591.86", 591, 8192)],
            SystemCheck.ParseNvidiaSmi("591.86, NVIDIA GeForce RTX 2070 SUPER, 8192\r\n"));
        // 드라이버와 통신하지 못할 때의 문구·빈 출력은 카드가 아니다
        Assert.Empty(SystemCheck.ParseNvidiaSmi("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver. Make sure that the latest NVIDIA driver is installed and running.\r\n"));
        Assert.Empty(SystemCheck.ParseNvidiaSmi(""));
        // 쉼표가 든 오류 문구와 값을 못 읽은 카드 줄도 카드가 아니다 — 금지 목록에 안 걸리는 이름이라 세면 '지원 카드'로 뽑힌다
        Assert.Equal([new SystemCheck.Gpu("NVIDIA GeForce GTX 1060 6GB", "580.88", 580, 6144)],
            SystemCheck.ParseNvidiaSmi("580.88, [Unknown Error], [Unknown Error]\r\n580.88, NVIDIA GeForce GTX 1060 6GB, 6144\r\n"));
        Assert.Empty(SystemCheck.ParseNvidiaSmi("ERROR: Option --query-gpu=driver_version,name,memory.total is not recognized. Please run 'nvidia-smi -h'.\r\n"));
    }

    [Theory]
    // 이 PC — nvidia-smi는 RTX 2070 SUPER 한 장, 레지스트리는 0000 같은 이름·0001 Parsec 가상 어댑터
    [InlineData(new[] { "NVIDIA GeForce RTX 2070 SUPER" }, new[] { "NVIDIA GeForce RTX 2070 SUPER", "Parsec Virtual Display Adapter" }, "nvidia", "NVIDIA GeForce RTX 2070 SUPER")]
    // nvidia-smi가 답하면 지금 꽂힌 카드로만 본다 — 레지스트리에는 뽑아낸 카드의 흔적 키가 남는다(첫 NVIDIA 키로 보면 판정이 뒤집힌다)
    [InlineData(new[] { "NVIDIA GeForce RTX 5070" }, new[] { "NVIDIA GeForce GTX 750 Ti", "NVIDIA GeForce RTX 5070" }, "nvidia", "NVIDIA GeForce RTX 5070")]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060" }, new[] { "NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce RTX 3060" }, "nvidia", "NVIDIA GeForce RTX 3060")]   // 가장 흔한 업그레이드
    [InlineData(new[] { "NVIDIA GeForce GTX 1080" }, new[] { "NVIDIA GeForce RTX 4070", "NVIDIA GeForce GTX 1080" }, "nvidia-old", "NVIDIA GeForce GTX 1080")]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060" }, new string[0], "nvidia", "NVIDIA GeForce RTX 3060")]   // 레지스트리를 못 읽어도
    [InlineData(new[] { "NVIDIA GeForce RTX 3060" }, new[] { "NVIDIA GeForce RTX 3060", "AMD Radeon RX 6800 XT" }, "nvidia", "NVIDIA GeForce RTX 3060")]
    // 여러 장이면 지원 카드가 한 장이라도 있을 때 CUDA 13 묶음 — 옛 카드를 화면용으로 같이 꽂은 PC
    [InlineData(new[] { "NVIDIA Quadro K620", "NVIDIA GeForce RTX 3060" }, new[] { "NVIDIA Quadro K620", "NVIDIA GeForce RTX 3060" }, "nvidia", "NVIDIA GeForce RTX 3060")]
    [InlineData(new[] { "NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce GTX 970" }, new[] { "NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce GTX 970" }, "nvidia-old", "NVIDIA GeForce GTX 1060 6GB")]
    // nvidia-smi가 없을 때만 레지스트리로 본다 — 여러 장이면 같은 규칙
    [InlineData(new string[0], new[] { "NVIDIA GeForce RTX 2070 SUPER", "Parsec Virtual Display Adapter" }, "nvidia", "NVIDIA GeForce RTX 2070 SUPER")]
    [InlineData(new string[0], new[] { "Parsec Virtual Display Adapter", "NVIDIA GeForce GTX 1080 Ti" }, "nvidia-old", "NVIDIA GeForce GTX 1080 Ti")]
    [InlineData(new string[0], new[] { "NVIDIA GeForce GTX 750 Ti", "NVIDIA GeForce RTX 5070" }, "nvidia", "NVIDIA GeForce RTX 5070")]
    [InlineData(new string[0], new[] { "NVIDIA GeForce GTX 1060 6GB", "AMD Radeon RX 7800 XT" }, "amd", "AMD Radeon RX 7800 XT")]   // 카드 교체 PC의 유령 NVIDIA 키
    [InlineData(new string[0], new[] { "Parsec Virtual Display Adapter", "Intel(R) UHD Graphics 630" }, "none", "Intel(R) UHD Graphics 630")]
    [InlineData(new string[0], new string[0], "none", "")]
    public void JudgeVendor_InstalledCardsFromNvidiaSmi_RegistryOnlyWithoutIt(string[] smiNames, string[] adapters, string vendor, string name)
    {
        // 자동 감지가 고른 종류로 ComfyUI 묶음(CUDA 13 / CUDA 12.6 / ROCm)을 받는다 — 옛 카드 흔적·보조 카드 하나로 묶음이 바뀌면 안 된다
        // (VRAM을 모두 같게 두어 이름으로만 고르게 한다 — VRAM이 다른 경우는 PickNvidiaGpu 테스트)
        var smi = smiNames.Select(n => new SystemCheck.Gpu(n, "580.88", 580, 8192)).ToList();
        Assert.Equal((vendor, name), SystemCheck.JudgeVendor(smi, adapters));
    }

    /// <summary>"이름:VRAM(MB)" 꼴을 nvidia-smi가 낸 카드 목록으로 (드라이버는 한 PC에서 모든 카드가 같다).</summary>
    private static List<SystemCheck.Gpu> Cards(string[] cards, string driver = "580.88") => cards
        .Select(c => c.Split(':'))
        .Select(p => new SystemCheck.Gpu(p[0], driver, int.Parse(driver.Split('.')[0]), int.Parse(p[1])))
        .ToList();

    [Theory]
    [InlineData(new[] { "NVIDIA GeForce RTX 2070 SUPER:8192" }, "NVIDIA GeForce RTX 2070 SUPER", 8192, "nvidia")]   // 이 PC — 한 장이면 그 카드
    // 지원 카드가 여러 장: nvidia-smi 첫 줄(PCI 순)이 아니라 이름으로 본 빠른 카드 — 엔진의 CUDA 0번은 '가장 빠른 카드'이고 꽂은 슬롯과 상관없다
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 4090:24564" }, "NVIDIA GeForce RTX 4090", 24564, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 2060:6144", "NVIDIA GeForce RTX 4070:12282" }, "NVIDIA GeForce RTX 4070", 12282, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 4070:12282", "NVIDIA GeForce RTX 2060:6144" }, "NVIDIA GeForce RTX 4070", 12282, "nvidia")]   // 순서를 바꿔도 같은 카드
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 4070:12288" }, "NVIDIA GeForce RTX 4070", 12288, "nvidia")]   // VRAM이 같아도 이름으로 가른다
    // 느린 카드가 메모리만 큰 조합 — VRAM이 큰 쪽을 고르면 엔진이 쓰지 않는 12GB에 맞추게 된다(검수 지적). 순서를 바꿔도 같다
    [InlineData(new[] { "NVIDIA GeForce RTX 3080:10240", "NVIDIA GeForce RTX 3060:12288" }, "NVIDIA GeForce RTX 3080", 10240, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 3080:10240" }, "NVIDIA GeForce RTX 3080", 10240, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 4090:24564", "NVIDIA GeForce RTX 3090:24576" }, "NVIDIA GeForce RTX 4090", 24564, "nvidia")]   // 3090이 12MiB 더 크다
    [InlineData(new[] { "NVIDIA GeForce RTX 3090:24576", "NVIDIA GeForce RTX 4090:24564" }, "NVIDIA GeForce RTX 4090", 24564, "nvidia")]
    // 번호까지 같으면 Ti·SUPER가 붙은 쪽 (무인 < SUPER < Ti < Ti SUPER)
    [InlineData(new[] { "NVIDIA GeForce RTX 2080 SUPER:8192", "NVIDIA GeForce RTX 2080 Ti:11264" }, "NVIDIA GeForce RTX 2080 Ti", 11264, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 4070 Ti SUPER:16376", "NVIDIA GeForce RTX 4070 SUPER:12282" }, "NVIDIA GeForce RTX 4070 Ti SUPER", 16376, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 3060 Ti:8192" }, "NVIDIA GeForce RTX 3060 Ti", 8192, "nvidia")]
    // 이름으로 가를 수 없으면 후보 가운데 VRAM이 작은 카드 — 틀려도 해상도가 낮아질 뿐이다. 같은 카드 두 장은 nvidia-smi 순서로 앞 카드
    [InlineData(new[] { "NVIDIA GeForce RTX 3090:24576", "NVIDIA GeForce RTX 3090:24576" }, "NVIDIA GeForce RTX 3090", 24576, "nvidia")]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 3060:8192" }, "NVIDIA GeForce RTX 3060", 8192, "nvidia")]     // 같은 이름의 12GB판·8GB판
    [InlineData(new[] { "NVIDIA RTX A4000:16376", "NVIDIA GeForce RTX 3060:12288" }, "NVIDIA GeForce RTX 3060", 12288, "nvidia")]          // 워크스테이션 카드는 번호 체계가 달라 견주지 못한다
    [InlineData(new[] { "NVIDIA GeForce RTX 4090:24564", "NVIDIA RTX A4000:16376" }, "NVIDIA RTX A4000", 16376, "nvidia")]
    [InlineData(new[] { "NVIDIA RTX 4000 Ada Generation:20475", "NVIDIA GeForce RTX 4090:24564" }, "NVIDIA RTX 4000 Ada Generation", 20475, "nvidia")]   // 'RTX 4000'을 40 시리즈로 읽지 않는다
    [InlineData(new[] { "NVIDIA GeForce RTX 4070:12282", "NVIDIA GeForce RTX 3080:10240" }, "NVIDIA GeForce RTX 3080", 10240, "nvidia")]   // 시리즈는 새롭고 번호는 낮다 — 엇갈리면 작은 쪽
    [InlineData(new[] { "NVIDIA GeForce RTX 4060 Ti:16380", "NVIDIA GeForce RTX 3080:10240" }, "NVIDIA GeForce RTX 3080", 10240, "nvidia")]
    // 세 장: 확실히 밀리는 카드(3060)는 후보에서 빠지고, 남은 둘(4090·A4000) 가운데 작은 쪽
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:8192", "NVIDIA GeForce RTX 4090:24564", "NVIDIA RTX A4000:16376" }, "NVIDIA RTX A4000", 16376, "nvidia")]
    // VRAM을 못 읽은 카드(0)는 작은 쪽으로 치지 않는다
    [InlineData(new[] { "NVIDIA GeForce RTX 3090:0", "NVIDIA GeForce RTX 3090:24576" }, "NVIDIA GeForce RTX 3090", 24576, "nvidia")]
    // 세대가 이름·VRAM보다 먼저 — 옛 카드의 번호가 높고 메모리가 더 커도 지원 세대 카드로 CUDA 13 묶음
    [InlineData(new[] { "NVIDIA GeForce GTX 1080 Ti:11264", "NVIDIA GeForce GTX 1650:4096" }, "NVIDIA GeForce GTX 1650", 4096, "nvidia")]
    [InlineData(new[] { "NVIDIA Quadro K620:2048", "NVIDIA GeForce RTX 3060:12288" }, "NVIDIA GeForce RTX 3060", 12288, "nvidia")]
    // 모두 옛 세대여도 같은 규칙
    [InlineData(new[] { "NVIDIA GeForce GTX 1060 6GB:6144", "NVIDIA GeForce GTX 1080 Ti:11264" }, "NVIDIA GeForce GTX 1080 Ti", 11264, "nvidia-old")]
    [InlineData(new[] { "NVIDIA GeForce GTX 970:4096", "NVIDIA GeForce GTX 1060 6GB:6144" }, "NVIDIA GeForce GTX 970", 4096, "nvidia-old")]   // 10 시리즈의 60번과 9 시리즈의 70번 — 엇갈려서 작은 쪽
    // 구형 묶음도 못 돌리는 세대(케플러)는 번호가 높고 메모리가 커도 맨 뒤 — 엔진이 쓸 수 있는 카드는 750 Ti뿐이다
    [InlineData(new[] { "NVIDIA GeForce GTX 780:6144", "NVIDIA GeForce GTX 750 Ti:2048" }, "NVIDIA GeForce GTX 750 Ti", 2048, "nvidia-old")]
    public void PickNvidiaGpu_OneCardForVendorNameAndVram(string[] cards, string name, int vramMb, string vendor)
    {
        var smi = Cards(cards);

        var picked = SystemCheck.PickNvidiaGpu(smi);

        // 맞춤 설정(AutoTune)은 이 카드의 VRAM으로 조정하고, 묶음 종류와 화면에 보이는 이름도 같은 카드에서 나온다
        Assert.NotNull(picked);
        Assert.Equal((name, vramMb), (picked.Name, picked.MemoryMb));
        Assert.Equal((vendor, name), SystemCheck.JudgeVendor(smi, []));
    }

    [Fact]
    public void PickNvidiaGpu_NoCards_Null()
    {
        Assert.Null(SystemCheck.PickNvidiaGpu([]));
    }

    [Theory]
    [InlineData(new[] { "NVIDIA GeForce RTX 2070 SUPER" }, "NVIDIA GeForce RTX 2070 SUPER", false)]   // 이 PC
    [InlineData(new[] { "NVIDIA GeForce GTX 1080 Ti" }, "NVIDIA GeForce GTX 1080 Ti", true)]
    // nvidia-smi 첫 줄(PCI 순)이 화면용 옛 카드여도 같이 꽂은 지원 카드로 본다 — 'RTX 20 이상' 선택을 막지 않는다
    [InlineData(new[] { "NVIDIA Quadro K620", "NVIDIA GeForce RTX 3060" }, "NVIDIA GeForce RTX 3060", false)]
    [InlineData(new[] { "NVIDIA GeForce GTX 1050 Ti", "NVIDIA GeForce RTX 2060" }, "NVIDIA GeForce RTX 2060", false)]
    [InlineData(new[] { "NVIDIA GeForce RTX 3060", "NVIDIA GeForce GTX 750 Ti" }, "NVIDIA GeForce RTX 3060", false)]
    [InlineData(new[] { "NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce GTX 970" }, "NVIDIA GeForce GTX 1060 6GB", true)]   // 모두 옛 세대면 막는다
    [InlineData(new[] { "NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3060" }, "NVIDIA GeForce RTX 4090", false)]         // 모두 지원 카드면 이름으로 본 빠른 카드 — VRAM을 몰라도
    [InlineData(new[] { "NVIDIA GeForce RTX 3060", "NVIDIA GeForce RTX 4090" }, "NVIDIA GeForce RTX 4090", false)]
    [InlineData(new[] { "NVIDIA RTX A4000", "NVIDIA GeForce RTX 4090" }, "NVIDIA RTX A4000", false)]                        // 이름으로 못 가르면(VRAM도 모른다) 첫 카드
    [InlineData(new[] { "NVIDIA GeForce GTX 680", "NVIDIA GeForce GTX 1060 6GB" }, "NVIDIA GeForce GTX 1060 6GB", true)]   // 케플러 흔적 키가 앞에 있어도 구형 묶음이 도는 카드
    [InlineData(new string[0], null, false)]   // nvidia-smi가 없으면 세대가 아니라 드라이버 항목이 막는다
    public void PickNvidia_BlocksCuda13_LookAtEveryInstalledCard(string[] smiNames, string? picked, bool blocked)
    {
        // 점검 창의 '그래픽카드'·'VRAM' 항목은 고른 카드를 보이고, 꽂힌 카드가 모두 CUDA 13이 버린 세대일 때만 설치를 막는다
        Assert.Equal(picked, SystemCheck.PickNvidia(smiNames));
        Assert.Equal(blocked, SystemCheck.BlocksCuda13(smiNames));
    }

    [Theory]
    // 케플러 — 드라이버가 47x에서 끝나 구형 묶음(CUDA 12.6, 드라이버 528 이상)도 못 돌린다
    [InlineData("NVIDIA GeForce GTX 780 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 770", true)]
    [InlineData("NVIDIA GeForce GTX 760", true)]
    [InlineData("GeForce GTX 680", true)]                   // 옛 드라이버의 이름은 앞에 NVIDIA가 없다
    [InlineData("NVIDIA GeForce GTX 660 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 650 Ti BOOST", true)]
    [InlineData("NVIDIA GeForce GT 640", true)]
    [InlineData("NVIDIA GeForce GT 710", true)]             // 2016년에 나왔지만 케플러(GK208)
    [InlineData("NVIDIA GeForce GT 730", true)]             // 케플러판·페르미판 — 어느 쪽이든 못 돌린다
    [InlineData("NVIDIA GeForce GT 740", true)]
    [InlineData("NVIDIA GeForce GTX TITAN", true)]
    [InlineData("NVIDIA GeForce GTX TITAN Black", true)]
    [InlineData("NVIDIA GeForce GTX TITAN Z", true)]
    [InlineData("NVIDIA GeForce GTX 780M", true)]           // 노트북
    [InlineData("NVIDIA GeForce GTX 765M", true)]
    [InlineData("NVIDIA GeForce GTX 880M", true)]
    [InlineData("NVIDIA GeForce GTX 870M", true)]
    [InlineData("NVIDIA GeForce GT 750M", true)]
    [InlineData("NVIDIA GeForce GT 740M", true)]
    [InlineData("NVIDIA GeForce GTX 675MX", true)]
    [InlineData("NVIDIA GeForce GT 650M", true)]
    [InlineData("NVIDIA GeForce 710M", true)]
    [InlineData("NVIDIA GeForce 825M", true)]
    [InlineData("NVIDIA GeForce 820M", true)]
    [InlineData("Quadro K4000", true)]
    [InlineData("NVIDIA Quadro K600", true)]
    [InlineData("NVIDIA Quadro K420", true)]
    [InlineData("NVIDIA Quadro K4200", true)]
    [InlineData("NVIDIA Quadro K5200", true)]
    [InlineData("NVIDIA Quadro K6000", true)]
    [InlineData("NVIDIA Quadro K2100M", true)]
    [InlineData("NVIDIA Quadro K610M", true)]
    [InlineData("NVIDIA Quadro 410", true)]
    [InlineData("Tesla K80", true)]
    [InlineData("Tesla K40m", true)]
    [InlineData("NVIDIA GRID K520", true)]
    // 페르미와 그 이전
    [InlineData("NVIDIA GeForce GTX 580", true)]
    [InlineData("NVIDIA GeForce GTX 560 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 460", true)]
    [InlineData("NVIDIA GeForce GTS 450", true)]
    [InlineData("NVIDIA GeForce GT 630", true)]
    [InlineData("NVIDIA GeForce GT 610", true)]
    [InlineData("NVIDIA GeForce GT 540M", true)]
    [InlineData("NVIDIA GeForce GTX 285", true)]
    [InlineData("NVIDIA GeForce GT 220", true)]
    [InlineData("NVIDIA GeForce 210", true)]
    [InlineData("NVIDIA GeForce 610M", true)]
    [InlineData("NVIDIA Quadro 4000", true)]
    [InlineData("NVIDIA Quadro 600", true)]
    [InlineData("NVIDIA Quadro 2000D", true)]
    [InlineData("NVIDIA Quadro FX 3800", true)]
    [InlineData("Tesla C2075", true)]
    [InlineData("Tesla M2090", true)]
    // 맥스웰 — 같은 번호대에 섞여 있지만 구형 묶음이 돈다(드라이버만 올리면 된다). 여기서 틀리면 멀쩡한 카드에 "쓸 수 없어요"라고 말한다
    [InlineData("NVIDIA GeForce GTX 750 Ti", false)]
    [InlineData("NVIDIA GeForce GTX 750", false)]
    [InlineData("NVIDIA GeForce GTX 745", false)]
    [InlineData("NVIDIA GeForce GTX TITAN X", false)]
    [InlineData("NVIDIA GeForce GTX 850M", false)]
    [InlineData("NVIDIA GeForce GTX 860M", false)]          // 케플러판·맥스웰판이 같은 이름 — 이름으로는 못 가른다
    [InlineData("NVIDIA GeForce 840M", false)]
    [InlineData("NVIDIA GeForce 830M", false)]
    [InlineData("NVIDIA GeForce 845M", false)]
    [InlineData("NVIDIA GeForce 940MX", false)]
    [InlineData("NVIDIA GeForce 930M", false)]
    [InlineData("NVIDIA GeForce 920M", false)]              // 확실하지 않은 이름은 건드리지 않는다
    [InlineData("NVIDIA GeForce 910M", false)]
    [InlineData("NVIDIA GeForce GTX 950", false)]
    [InlineData("NVIDIA GeForce GTX 960M", false)]
    [InlineData("NVIDIA GeForce GTX 970", false)]
    [InlineData("NVIDIA GeForce GTX 980 Ti", false)]
    [InlineData("NVIDIA Quadro K620", false)]
    [InlineData("NVIDIA Quadro K1200", false)]
    [InlineData("Quadro K2200", false)]
    [InlineData("NVIDIA Quadro K2200M", false)]
    [InlineData("NVIDIA Quadro K620M", false)]
    [InlineData("Quadro M1000M", false)]
    [InlineData("NVIDIA Quadro M4000", false)]
    [InlineData("Tesla M60", false)]
    [InlineData("Tesla M40 24GB", false)]
    [InlineData("Tesla M10", false)]
    [InlineData("NVIDIA NVS 810", false)]
    [InlineData("NVIDIA NVS 510", false)]                   // 케플러지만 NVS는 810(맥스웰)과 섞여 있어 통째로 뺐다
    [InlineData("NVIDIA GRID M60-8Q", false)]
    // 파스칼·볼타·튜링 이후
    [InlineData("NVIDIA GeForce GT 1030", false)]
    [InlineData("NVIDIA GeForce GT 1010", false)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti", false)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", false)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", false)]
    [InlineData("NVIDIA GeForce MX150", false)]
    [InlineData("NVIDIA GeForce MX350", false)]
    [InlineData("NVIDIA TITAN Xp", false)]
    [InlineData("NVIDIA TITAN V", false)]
    [InlineData("Quadro P1000", false)]
    [InlineData("NVIDIA Quadro P620", false)]
    [InlineData("Tesla P40", false)]
    [InlineData("Tesla V100-PCIE-16GB", false)]
    [InlineData("NVIDIA GeForce GTX 1650", false)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", false)]    // 이 PC
    [InlineData("NVIDIA GeForce RTX 3060", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", false)]
    [InlineData("NVIDIA TITAN RTX", false)]
    [InlineData("Quadro RTX 4000", false)]
    [InlineData("NVIDIA Quadro RTX 6000", false)]
    [InlineData("Quadro T1000", false)]
    [InlineData("NVIDIA T600", false)]
    [InlineData("NVIDIA RTX A2000 12GB", false)]
    [InlineData("NVIDIA RTX 6000 Ada Generation", false)]
    [InlineData("Tesla T4", false)]
    [InlineData("NVIDIA A100-SXM4-40GB", false)]
    [InlineData("NVIDIA", false)]
    [InlineData("", false)]
    public void LooksTooOldForGpuEngine_OnlyNamesThatAreSurelyKeplerOrOlder(string name, bool tooOld)
    {
        Assert.Equal(tooOld, SystemCheck.LooksTooOldForGpuEngine(name));
        // 너무 오래된 카드는 CUDA 13이 버린 세대이기도 해야 한다 — 아니면 카드를 고를 때 '지원 세대'로 먼저 뽑힌다
        if (tooOld) Assert.True(SystemCheck.LooksUnsupportedByCuda13(name));
    }

    [Theory]
    // nvidia-smi가 답하면 그 카드들만: 모두 이름이 옛 세대이고 드라이버도 528 미만일 때만 '너무 오래됨'
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048" }, "474.11", new string[0], true)]
    [InlineData(new[] { "NVIDIA GeForce GTX 780:3072", "NVIDIA GeForce GT 710:2048" }, "475.14", new string[0], true)]
    [InlineData(new[] { "GeForce GTX 580:1536" }, "391.35", new string[0], true)]
    // 드라이버 528 이상이 붙어 있으면 케플러·페르미일 수 없다 — 이름이 그렇게 보여도 드라이버 쪽을 믿는다
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048" }, "580.88", new string[0], false)]
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048" }, "528.02", new string[0], false)]   // 경계: 528은 구형 묶음이 도는 드라이버
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048" }, "527.56", new string[0], true)]
    // 맥스웰·파스칼에 옛 드라이버 — 드라이버만 올리면 되는 카드
    [InlineData(new[] { "NVIDIA GeForce GTX 750 Ti:2048" }, "472.12", new string[0], false)]
    [InlineData(new[] { "NVIDIA GeForce GTX 1060 6GB:6144" }, "457.51", new string[0], false)]
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048", "NVIDIA GeForce GTX 1060 6GB:6144" }, "472.12", new string[0], false)]   // 한 장이라도 돌릴 수 있으면 아니다
    [InlineData(new[] { "NVIDIA GeForce RTX 2070 SUPER:8192" }, "591.86", new string[0], false)]   // 이 PC
    // nvidia-smi가 답했으면 레지스트리의 옛 이름(뽑아낸 카드의 흔적)은 보지 않는다
    [InlineData(new[] { "NVIDIA GeForce RTX 3060:12288" }, "580.88", new[] { "NVIDIA GeForce GTX 680" }, false)]
    // nvidia-smi가 없으면(페르미의 39x 드라이버) 레지스트리 이름으로 — 전부 옛 세대일 때만
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 580" }, true)]
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 580", "NVIDIA GeForce GTX 1060 6GB" }, false)]   // 옛 카드는 흔적 키일 수 있다
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 1080 Ti" }, false)]
    [InlineData(new string[0], "", new string[0], false)]
    [InlineData(new string[0], "", new[] { "Intel(R) UHD Graphics 630" }, false)]   // NVIDIA 카드가 없으면 '너무 오래된 NVIDIA'도 아니다
    // 정체를 아는 다른 어댑터(내장 그래픽·가상·원격 화면)는 판정을 바꾸지 않는다 — 옛 카드뿐인 PC는 그대로 '너무 오래됨'
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 770", "Intel(R) UHD Graphics 630" }, true)]
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 770", "Parsec Virtual Display Adapter" }, true)]
    [InlineData(new string[0], "", new[] { "Microsoft Remote Display Adapter", "NVIDIA GeForce GTX 770" }, true)]
    [InlineData(new[] { "GeForce GT 650M:2048" }, "391.35", new[] { "Intel(R) HD Graphics 4000", "NVIDIA GeForce GT 650M" }, true)]   // 옛 노트북
    [InlineData(new[] { "NVIDIA GeForce GTX 680:2048" }, "474.11", new[] { "NVIDIA GeForce GTX 680", "AMD Radeon(TM) Graphics" }, true)]
    // 검수 지적 — 카드를 바꾼 직후: NVIDIA 이름은 뽑아낸 옛 카드의 흔적뿐이고 새 카드는 드라이버가 없어 기본 어댑터로 잡힌다. '너무 오래됨'이 아니다
    [InlineData(new string[0], "", new[] { "NVIDIA GeForce GTX 770", "Microsoft Basic Display Adapter" }, false)]
    [InlineData(new string[0], "", new[] { "Microsoft 기본 디스플레이 어댑터", "NVIDIA GeForce GTX 770" }, false)]   // 한국어 Windows의 이름
    [InlineData(new string[0], "", new[] { "Microsoft Basic Display Adapter", "NVIDIA GeForce GTX 770", "NVIDIA GeForce GT 710" }, false)]
    [InlineData(new string[0], "", new[] { "Microsoft Basic Display Adapter" }, false)]
    // nvidia-smi가 답해도 같다 — 옛 드라이버(47x)는 같이 꽂힌 새 카드를 보지 못해 옛 카드만 적는다
    [InlineData(new[] { "GeForce GT 710:2048" }, "474.11", new[] { "NVIDIA GeForce GT 710", "Microsoft Basic Display Adapter" }, false)]
    [InlineData(new[] { "GeForce GT 710:2048" }, "474.11", new[] { "NVIDIA GeForce GT 710", "NVIDIA GeForce RTX 4070" }, false)]
    public void NvidiaTooOld_NameAndDriverAgree_AllCards(string[] cards, string driver, string[] adapters, bool tooOld)
    {
        // adapters = 레지스트리의 어댑터 이름 전부(다른 벤더·가상 어댑터 포함)
        Assert.Equal(tooOld, SystemCheck.NvidiaTooOld(Cards(cards, driver.Length > 0 ? driver : "0.0"), adapters));
    }

    [Fact]
    public void NvidiaItems_SingleRtxCard_SameThreeLinesAsBefore()
    {
        // 이 PC(RTX 2070 SUPER, 591.86, 8GB)가 v1.8.2에서 보이던 그대로 — 카드 고르기를 바꿔도 한 장짜리 PC의 화면은 달라지지 않는다
        var items = SystemCheck.NvidiaItems(old: false, Cards(["NVIDIA GeForce RTX 2070 SUPER:8192"], "591.86"), ["NVIDIA GeForce RTX 2070 SUPER"]);

        Assert.Equal(
            [
                new SystemCheck.Item("NVIDIA 드라이버", true, true, "591.86 (충분)", null, "드라이버 받기"),
                new SystemCheck.Item("그래픽카드", true, true, "NVIDIA GeForce RTX 2070 SUPER — CUDA 13 포터블", null, null),
                new SystemCheck.Item("VRAM", true, false, "8GB", null, null),
            ], items);
    }

    [Fact]
    public void NvidiaItems_SeveralSupportedCards_ShowTheCardTheEngineUses()
    {
        // nvidia-smi 첫 줄은 화면용 RTX 3060 — 그래도 이름과 VRAM은 엔진이 쓸 카드(RTX 4090)의 것
        var items = SystemCheck.NvidiaItems(old: false, Cards(["NVIDIA GeForce RTX 3060:12288", "NVIDIA GeForce RTX 4090:24576"]), []);

        Assert.StartsWith("NVIDIA GeForce RTX 4090", items.Single(i => i.Title == "그래픽카드").Detail);
        Assert.Equal("24GB", items.Single(i => i.Title == "VRAM").Detail);
        Assert.All(items, i => Assert.True(i.Ok));
    }

    [Theory]
    [InlineData(true)]     // 자동 감지나 'GTX 10 이하'를 골랐을 때
    [InlineData(false)]    // 'RTX 20 이상'을 골랐을 때도 같은 안내
    public void NvidiaItems_KeplerCard_SaysTooOldInsteadOfUpdateDriver(bool old)
    {
        var items = SystemCheck.NvidiaItems(old, Cards(["NVIDIA GeForce GTX 680:2048"], "474.11"), ["NVIDIA GeForce GTX 680"]);

        // 예전에는 "474.11 — CUDA 12.6 런타임은 528 이상이 필요해요. 최신 GeForce 드라이버로 올린 뒤 재부팅하세요" + [드라이버 받기] — 이 카드에는 그런 드라이버가 없다
        var item = Assert.Single(items);
        Assert.Equal("그래픽카드", item.Title);
        Assert.False(item.Ok);
        Assert.True(item.Blocking);                       // 엔진 묶음은 받지 않는다(내장 엔진은 그대로 설치)
        Assert.StartsWith("NVIDIA GeForce GTX 680", item.Detail);
        Assert.Contains("너무 오래된 그래픽카드", item.Detail);
        Assert.Contains("번역과 기본 검열 제거는 CPU로", item.Detail);
        Assert.DoesNotContain("올린 뒤", item.Detail);
        // 드라이버 주소는 남긴다 — 새 카드를 막 달아 아직 어댑터로도 잡히지 않은 PC라면 이 판정이 틀렸고, 그때 할 일이 드라이버 설치다(Report.BlockedNote가 그렇게 적는다)
        Assert.Equal(SystemCheck.DriverUrl, item.FixUrl);
    }

    [Fact]
    public void NvidiaItems_FermiCardWithoutNvidiaSmi_SaysTooOldInsteadOfInstallDriver()
    {
        // 페르미의 마지막 드라이버(39x)는 nvidia-smi를 PATH에 두지 않는다 — "드라이버를 설치해 주세요"가 아니라 레지스트리 이름으로 알려 준다
        var item = Assert.Single(SystemCheck.NvidiaItems(old: true, [], ["NVIDIA GeForce GTX 580"]));

        Assert.Equal(("그래픽카드", false, true), (item.Title, item.Ok, item.Blocking));
        Assert.StartsWith("NVIDIA GeForce GTX 580", item.Detail);
        Assert.Contains("너무 오래된 그래픽카드", item.Detail);
        Assert.DoesNotContain("설치해 주세요", item.Detail);
    }

    [Fact]
    public void NvidiaItems_OldCardTraceWithUnrecognizedAdapter_KeepsDriverGuidance()
    {
        // 검수 지적: GTX 770을 새 카드로 바꾼 직후 — nvidia-smi는 없고 레지스트리에는 옛 카드의 흔적 + 드라이버 없는 새 카드(기본 어댑터).
        // "너무 오래됐어요"가 아니라 v1.8.2처럼 드라이버를 설치하라고 하고 받는 곳을 준다
        var swapped = Assert.Single(SystemCheck.NvidiaItems(old: true, [], ["NVIDIA GeForce GTX 770", "Microsoft Basic Display Adapter"]));
        Assert.Equal(new SystemCheck.Item("NVIDIA 드라이버", false, true, "NVIDIA 드라이버를 찾지 못했어요 (nvidia-smi 없음). 드라이버를 설치해 주세요.", SystemCheck.DriverUrl, "드라이버 받기"), swapped);

        // 옛 드라이버(474)의 nvidia-smi가 GT 710만 적는데 다른 어댑터가 있다 — 새 카드를 같이 꽂았을 수 있다. 드라이버를 올리라는 예전 안내 그대로
        var items = SystemCheck.NvidiaItems(old: true, Cards(["GeForce GT 710:2048"], "474.11"), ["NVIDIA GeForce GT 710", "Microsoft Basic Display Adapter"]);
        var driver = items.Single(i => i.Title == "NVIDIA 드라이버");
        Assert.Contains("528 이상이 필요해요", driver.Detail);
        Assert.Equal(SystemCheck.DriverUrl, driver.FixUrl);
        Assert.DoesNotContain(items, i => i.Detail.Contains("너무 오래된"));
    }

    [Fact]
    public void CheckForComfy_TooOldCard_ReportSaysSoAndDropsUnfollowableNotes()
    {
        var tooOld = SystemCheck.CheckForComfy("auto", Cards(["NVIDIA GeForce GTX 680:2048"], "474.11"), ["NVIDIA GeForce GTX 680"]);

        Assert.True(tooOld.GpuTooOld);
        Assert.True(tooOld.HasBlocking);
        Assert.Contains("너무 오래된 그래픽카드", tooOld.Summary);
        // 해결할 항목이 없는데 "위 항목을 해결한 뒤 [다시 확인]…"·"위 조건을 해결한 뒤…"를 붙였다(검수 지적) — 새 카드를 막 단 경우에 할 일만 남긴다
        Assert.Equal("번역·검열 감지·기본 복원은 영향 없이 설치돼요. 새 그래픽카드를 방금 달았다면 그 카드의 드라이버를 설치한 뒤 [다시 확인]을 눌러 주세요.", tooOld.BlockedNote);
        Assert.DoesNotContain("해결한 뒤", tooOld.LeftoverNote);
        Assert.Contains("채색 엔진을 쓸 수 없어", tooOld.LeftoverNote);

        // 드라이버만 올리면 되는 카드(지원 세대 GTX 1660 + 옛 드라이버)는 v1.8.2의 문구 그대로
        var fixable = SystemCheck.CheckForComfy("auto", Cards(["NVIDIA GeForce GTX 1660 SUPER:6144"], "552.44"), ["NVIDIA GeForce GTX 1660 SUPER"]);
        Assert.False(fixable.GpuTooOld);
        Assert.True(fixable.HasBlocking);
        Assert.Equal("번역·검열 감지·기본 복원은 영향 없이 설치돼요. 위 항목을 해결한 뒤 [다시 확인]을 누르면 엔진 묶음도 이어서 받아요.", fixable.BlockedNote);
        Assert.Equal("내장 엔진은 준비됐어요. 채색 엔진은 위 조건을 해결한 뒤 [다시 확인] → [준비 시작!]으로 받아요.", fixable.LeftoverNote);

        // 카드를 바꾼 직후로 보이면 '너무 오래됨'이 아니다 — 드라이버 안내와 예전 뒷말
        var swapped = SystemCheck.CheckForComfy("auto", [], ["NVIDIA GeForce GTX 770", "Microsoft Basic Display Adapter"]);
        Assert.False(swapped.GpuTooOld);
        Assert.Contains(swapped.Items, i => !i.Ok && i.FixUrl == SystemCheck.DriverUrl && i.Detail.Contains("드라이버를 설치해 주세요"));
        Assert.Equal(fixable.BlockedNote, swapped.BlockedNote);

        // 다른 벤더를 골랐으면 NVIDIA 카드가 옛것이어도 이 표시는 서지 않는다
        Assert.False(SystemCheck.CheckForComfy("amd", Cards(["NVIDIA GeForce GTX 680:2048"], "474.11"), ["NVIDIA GeForce GTX 680"]).GpuTooOld);
    }

    [Fact]
    public void NvidiaItems_OldButRunnableCard_KeepsDriverGuidance()
    {
        // 맥스웰(GTX 750 Ti)에 옛 드라이버 — 드라이버를 올리면 구형 묶음이 돈다. 안내는 예전 그대로
        var items = SystemCheck.NvidiaItems(old: true, Cards(["NVIDIA GeForce GTX 750 Ti:2048"], "472.12"), ["NVIDIA GeForce GTX 750 Ti"]);

        var driver = items.Single(i => i.Title == "NVIDIA 드라이버");
        Assert.False(driver.Ok);
        Assert.Contains("528 이상이 필요해요", driver.Detail);
        Assert.Equal(SystemCheck.DriverUrl, driver.FixUrl);
        Assert.True(items.Single(i => i.Title == "그래픽카드").Ok);
        Assert.DoesNotContain(items, i => i.Detail.Contains("너무 오래된"));

        // nvidia-smi가 답하지 않고 레지스트리 이름도 돌릴 수 있는 세대면 드라이버 안내 그대로
        var noSmi = Assert.Single(SystemCheck.NvidiaItems(old: true, [], ["NVIDIA GeForce GTX 1060 6GB"]));
        Assert.Equal("NVIDIA 드라이버", noSmi.Title);
        Assert.Contains("nvidia-smi 없음", noSmi.Detail);
        Assert.Equal(SystemCheck.DriverUrl, noSmi.FixUrl);
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

    [Theory]
    [InlineData("auto")]      // 예전에는 점검에 한 번, 벤더 감지에 또 한 번 불렀다 — 멈춘 nvidia-smi면 8초씩
    [InlineData("nvidia")]
    public void DiagnoseComfyLog_TorchDllFailure_AsksNvidiaSmiOnce(string vendor)
    {
        using var t = new TempDir();
        var log = t.At("comfy.log");
        File.WriteAllText(log, "OSError: [WinError 1114] Error loading \"torch\\lib\\c10.dll\" or one of its dependencies.\n");
        int asked = 0;
        IReadOnlyList<SystemCheck.Gpu> Smi() { asked++; return Cards(["NVIDIA GeForce GTX 1660 SUPER:6144"], "552.44"); }

        var hint = SystemCheck.DiagnoseComfyLog(log, vendor, Smi);

        Assert.Equal(1, asked);
        // 넘겨받은 목록으로 점검한다 — 기록된 사고 그대로(지원 세대 GTX 1660 + 옛 드라이버)
        Assert.NotNull(hint);
        Assert.Contains("552.44", hint);
        Assert.Contains("580 이상이 필요해요", hint);
        Assert.EndsWith($" (드라이버: {SystemCheck.DriverUrl}, VC++: {SystemCheck.VcRedistUrl})", hint);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nvidia")]
    public void DiagnoseComfyLog_TorchDllFailureOnTooOldCard_NoDriverAndVcLinksToFollow(string vendor)
    {
        using var t = new TempDir();
        var log = t.At("comfy.log");
        File.WriteAllText(log, "OSError: [WinError 1114] Error loading \"torch\\lib\\c10.dll\" or one of its dependencies.\n");

        var hint = SystemCheck.DiagnoseComfyLog(log, vendor, () => Cards(["NVIDIA GeForce GTX 680:2048"], "474.11"), ["NVIDIA GeForce GTX 680"]);

        // 예전에는 "너무 오래된 그래픽카드…" 뒤에 "(드라이버: …, VC++: …)"가 그대로 붙었다 — 받아도 소용없는 것을 받으라는 말(검수 지적)
        Assert.NotNull(hint);
        Assert.Contains("너무 오래된 그래픽카드", hint);
        Assert.DoesNotContain(SystemCheck.VcRedistUrl, hint);
        // 새 카드를 막 단 경우에 쓸 드라이버 주소만 남긴다
        Assert.EndsWith($" 새 그래픽카드를 방금 달았다면 그 카드의 드라이버를 설치한 뒤 다시 시도해 주세요 (드라이버: {SystemCheck.DriverUrl})", hint);
    }

    [Fact]
    public void DiagnoseComfyLog_OtherCauses_DoNotAskNvidiaSmi()
    {
        using var t = new TempDir();
        var oom = t.At("oom.log");
        File.WriteAllText(oom, "torch.OutOfMemoryError: CUDA out of memory.\n");
        int asked = 0;
        IReadOnlyList<SystemCheck.Gpu> Smi() { asked++; return []; }

        // 메모리 부족·모르는 원인·없는 로그에는 하드웨어를 물을 일이 없다
        Assert.Contains("--lowvram", SystemCheck.DiagnoseComfyLog(oom, "auto", Smi));
        Assert.Null(SystemCheck.DiagnoseComfyLog(t.At("없는 로그.txt"), "auto", Smi));
        Assert.Equal(0, asked);
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
