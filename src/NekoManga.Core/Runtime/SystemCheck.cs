using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NekoManga.Core.Runtime;

/// <summary>ComfyUI 묶음(채색·재생성·자가 학습)을 설치·실행하기 전에 PC가 조건을 갖췄는지 본다 (2026-10-20).
/// 계기: 다른 PC(GTX 1660, 옛 드라이버)에서 ComfyUI 포터블의 torch(CUDA 13 빌드)가 `c10.dll … WinError 1114`로 죽음 — CUDA 13 런타임은
/// NVIDIA 드라이버 580 이상이 필요하고, VC++ 2015–2022 x64 런타임도 있어야 한다. 설치 창에서 미리 알려 주고 받을 수 있게 안내한다.
/// WMI(System.Management)는 설치본에서 어셈블리 로드 크래시 전력이 있어 쓰지 않는다 — nvidia-smi 프로세스·레지스트리·GC 메모리 정보만 쓴다.</summary>
public static class SystemCheck
{
    public const int MinDriverMajor = 580;          // CUDA 13.x 런타임 최소 드라이버 (R580)
    public const int MinVcBuild = 30;               // VC++ 14.30 이상 (VS 2022 런타임)
    public const string DriverUrl = "https://www.nvidia.com/Download/index.aspx?lang=kr";
    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public sealed record Gpu(string Name, string Driver, int DriverMajor, int MemoryMb);
    public sealed record Item(string Title, bool Ok, bool Blocking, string Detail, string? FixUrl, string? FixLabel);
    /// <summary>GpuTooOld = 막은 까닭이 '어느 묶음으로도 채색 엔진을 못 돌리는 옛 NVIDIA 카드'(<see cref="NvidiaTooOld(IReadOnlyList{Gpu}, IReadOnlyList{string})"/>)다 —
    /// 이때는 "위 항목을 해결한 뒤…" 같은, 따를 수 없는 뒷말을 붙이지 않는다.</summary>
    public sealed record Report(IReadOnlyList<Item> Items, bool GpuTooOld = false)
    {
        public bool HasBlocking => Items.Any(i => !i.Ok && i.Blocking);
        public bool HasWarning => Items.Any(i => !i.Ok && !i.Blocking);
        public string Summary => string.Join("\n", Items.Where(i => !i.Ok).Select(i => (i.Blocking ? "✖ " : "⚠ ") + i.Title + " — " + i.Detail));

        /// <summary>설치 창이 막힌 항목들 아래에 붙이는 말. 너무 오래된 카드는 해결할 항목이 없으니(2026-11-11 검수: 그래도 "위 항목을 해결한 뒤 [다시 확인]…"이 붙었다)
        /// 판정이 틀릴 수 있는 한 가지 경우 — 새 카드를 막 달아 드라이버가 아직 없는 PC — 에 할 일만 적는다.</summary>
        public string BlockedNote => "번역·검열 감지·기본 복원은 영향 없이 설치돼요. " +
            (GpuTooOld ? NewCardHint + " [다시 확인]을 눌러 주세요." : "위 항목을 해결한 뒤 [다시 확인]을 누르면 엔진 묶음도 이어서 받아요.");

        /// <summary>엔진 묶음을 건너뛰고 내장 엔진만 설치했을 때 설치 창 아래 줄에 적는 말.</summary>
        public string LeftoverNote => GpuTooOld
            ? "내장 엔진은 준비됐어요. 이 그래픽카드로는 채색 엔진을 쓸 수 없어 받지 않았어요 — 번역·기본 검열 제거는 바로 쓸 수 있어요."
            : "내장 엔진은 준비됐어요. 채색 엔진은 위 조건을 해결한 뒤 [다시 확인] → [준비 시작!]으로 받아요.";
    }

    /// <summary>'너무 오래된 그래픽카드' 안내 뒤에 붙이는 말의 앞부분 — 뒤는 자리에 따라 다르다(설치 창은 "[다시 확인]을 눌러 주세요", 엔진 로그 진단은 "다시 시도해 주세요").</summary>
    internal const string NewCardHint = "새 그래픽카드를 방금 달았다면 그 카드의 드라이버를 설치한 뒤";

    /// <summary>nvidia-smi로 드라이버·GPU 이름·VRAM을 읽는다. 여러 장이면 엔진이 쓸 카드(<see cref="PickNvidiaGpu"/>). 드라이버가 없으면 null.</summary>
    public static Gpu? QueryNvidia() => PickNvidiaGpu(QueryNvidiaAll());

    /// <summary>nvidia-smi가 보는 NVIDIA 카드 전부(지금 꽂혀 있는 카드). 드라이버가 없거나 nvidia-smi가 답하지 않으면 빈 목록.
    /// 멈춘 nvidia-smi는 부를 때마다 8초를 기다리게 한다 — 한 흐름(점검 + 맞춤 설정, 로그 진단)에서는 한 번만 부르고 그 목록을 받는 오버로드
    /// (<see cref="DetectVendor(IReadOnlyList{Gpu})"/>·<see cref="CheckForComfy(string, IReadOnlyList{Gpu})"/>·AutoTune.Build)로 넘겨 쓴다.
    /// 2026-11-11까지 AutoTune.Build와 DiagnoseComfyLog가 두 번씩, 설치 창은 열릴 때 세 번 불렀다.
    /// 결과를 붙잡아 두지는 않는다(캐시 없음) — 앱을 켜 둔 채 드라이버를 올리거나 카드를 바꾼 뒤의 [다시 확인]은 새로 물어야 한다.</summary>
    public static IReadOnlyList<Gpu> QueryNvidiaAll()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version,name,memory.total --format=csv,noheader,nounits")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return [];
            // 출력은 카드 한 장에 한 줄뿐이라 파이프가 차지 않는다 — 먼저 기다려야 nvidia-smi가 멈춘 경우(드라이버 크래시 뒤 흔함) 8초 타임아웃이 먹는다
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return []; }
            return ParseNvidiaSmi(p.StandardOutput.ReadToEnd());
        }
        catch { return []; }
    }

    /// <summary>nvidia-smi `--query-gpu=driver_version,name,memory.total --format=csv,noheader,nounits` 출력을 카드마다 읽는다. 꼴이 맞지 않는 줄(오류 문구 등)은 건너뛴다.
    /// 2026-11-11: 첫 줄만 읽어서, 화면용 옛 카드가 앞 순서(PCI 순)에 있으면 같이 꽂은 새 카드를 보지 못했다.</summary>
    internal static IReadOnlyList<Gpu> ParseNvidiaSmi(string output)
    {
        var list = new List<Gpu>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 3) continue;
            int major = int.TryParse(parts[0].Split('.')[0], out var m) ? m : 0;
            // 쉼표가 든 오류 문구나 값을 못 읽은 카드('[Unknown Error]')는 카드로 세지 않는다 — 금지 목록에 안 걸리는 이름이라 '지원 카드'로 뽑힌다
            if (major == 0 || parts[1].Length == 0 || parts[1].StartsWith('[')) continue;
            int mem = int.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? mm : 0;
            list.Add(new Gpu(parts[1], parts[0], major, mem));
        }
        return list;
    }

    /// <summary>VC++ 2015–2022 x64 런타임 버전 (설치돼 있지 않으면 null). 레지스트리 HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64.</summary>
    public static Version? VcRuntime()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64")
                        ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (k == null) return null;
            if ((k.GetValue("Installed") as int?) != 1) return null;
            int major = k.GetValue("Major") as int? ?? 0, minor = k.GetValue("Minor") as int? ?? 0, bld = k.GetValue("Bld") as int? ?? 0;
            return new Version(major, minor, bld);
        }
        catch { return null; }
    }

    public static double RamGb()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0; } catch { return 0; }
    }

    /// <summary>CUDA 13이 버린 세대(compute capability 7.5 미만 — 케플러·맥스웰·파스칼·볼타와 그 이전)인지 이름으로 거칠게 판정.
    /// 옛 세대는 더 늘지 않는 닫힌 목록이라 그 이름 꼴을 빠짐없이 적고, 어느 꼴에도 걸리지 않는 이름(모르는 이름·앞으로 나올 카드)은 지원으로 본다 —
    /// 지원 카드를 막는 쪽이 더 해롭다(멀쩡한 카드에 구형 묶음을 받거나 설치가 막힌다).</summary>
    public static bool LooksUnsupportedByCuda13(string name) => Cuda13Dropped.IsMatch(name);

    // 튜링 이후(GTX 16xx, RTX 전부, MX450·MX550·MX570, NVIDIA T400·T600·T1000, Quadro T·RTX, TITAN RTX, Tesla T4, A100·H100 …)는 어느 꼴에도 걸리지 않아야 한다.
    // 2026-11-10: 옛 목록('GTX 9'·'GTX 10'·'GT 7'·'MX1~3'·'QUADRO P/M' …)이 1세대 맥스웰(GTX 750 Ti·Quadro K2200·GeForce 940MX)과
    // 케플러(GTX 780)를 빠뜨려, 자동 감지가 이 카드들에 CUDA 13 포터블을 고르고 'RTX 20 이상' 선택도 막지 못했다.
    private static readonly Regex Cuda13Dropped = new(string.Join("|",
        @"\bGTX ?(?:\d{3}|10\d\d)(?!\d)",           // 세 자리 GTX 전부(GTX 6xx·7xx·9xx, 노트북 860M·980M 포함)와 GTX 10xx — GTX 16xx(튜링)는 걸리지 않는다
        @"\bGTX TITAN|\bTITAN [XV]",                // GTX TITAN·Black·Z·X(케플러·맥스웰), TITAN X·Xp(파스칼), TITAN V(볼타) — TITAN RTX는 지원
        @"\bGTS? ?\d",                              // GT 710~740·GT 1030 등 — GT·GTS는 튜링부터 없다
        @"\bGEFORCE \d",                            // 노트북 940MX·930MX·920MX·9xxM·8xxM처럼 시리즈 글자 없이 숫자가 붙는 이름
        @"\bMX ?[1-3]\d\d(?!\d)",                   // MX110~MX350 — MX450·MX550·MX570은 지원
        @"\bQUADRO (?:[KMP]\d|G[PV]100|\d|FX|NVS)", // Quadro K·M·P 시리즈, GP100·GV100, 페르미 Quadro 4000류, FX·NVS — Quadro T·RTX는 지원
        @"\bNVS \d",                                // NVS 310·510·810
        @"\bTESLA [KMPVCS]",                        // Tesla K80·M60·P40·V100·PG503(볼타)과 페르미 C2050·M2090 — Tesla T4(튜링)·A100 등은 지원
        @"\bGRID [KMPV]\d",                         // 가상 GPU GRID K520·M60·P40·V100 — GRID T4는 지원
        @"\bP10[246]-\d"),                          // 채굴 카드 P102-100·P104-100·P106-100(파스칼)
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>구형 묶음(CUDA 12.6)으로도 채색 엔진을 돌릴 수 없는 세대(케플러·페르미와 그 이전)인지 이름으로 판정 (2026-11-11).
    /// 이 세대의 드라이버는 47x(케플러)·39x(페르미)에서 끝났는데 CUDA 12.6 런타임은 드라이버 <see cref="MinDriverMajorCu126"/> 이상이 필요하고 torch 빌드도 이 세대를 뺐다.
    /// 예전에는 'GTX 10 이하'로 묶여 "최신 드라이버로 올리세요"라는, 따를 수 없는 안내를 받았다.
    /// <see cref="LooksUnsupportedByCuda13"/>와 반대로 **확실한 이름만** 적는다 — 여기서 틀리면 드라이버만 올리면 되는 카드(맥스웰·파스칼)에 "쓸 수 없어요"라고 말하게 된다.
    /// 그래서 같은 번호대에 맥스웰이 섞인 이름은 뺐다: GTX 745·750·750 Ti, GTX 860M(케플러판·맥스웰판이 같은 이름), GeForce 830M~845M·910M~940M,
    /// Quadro K620·K1200·K2200(M), NVS(810이 맥스웰). 이런 카드는 지금처럼 드라이버 버전 점검이 맡는다.
    /// 이름만으로 끝내지도 않는다 — <see cref="NvidiaTooOld(IReadOnlyList{Gpu}, IReadOnlyList{string})"/>가 드라이버 버전과 함께 본다.</summary>
    public static bool LooksTooOldForGpuEngine(string name) => TooOldForCu126.IsMatch(name);

    // 여기 걸리는 이름은 모두 Cuda13Dropped에도 걸려야 한다(테스트가 확인) — 아니면 PickNvidiaGpu가 '지원 세대'로 먼저 뽑는다.
    private static readonly Regex TooOldForCu126 = new(string.Join("|",
        @"\bGT[XS]? ?[1-6]\d\d(?!\d)",              // GT·GTS·GTX 1xx~6xx(노트북 M·MX 포함) — 전부 케플러 이전. 네 자리(GT 1030·GTX 1060·GTX 1650)는 걸리지 않는다
        @"\bGT ?7\d\d",                             // GT 705~740, GT 720M~755M — 케플러이거나 페르미 재탕(GT 710·730 포함)
        @"\bGTX ?7[6-8]\d",                         // GTX 760·770·780(Ti), 760M~780M — 케플러. GTX 745·750(Ti)는 맥스웰이라 뺀다
        @"\bGTX ?8[78]0M",                          // GTX 870M·880M — 케플러. 850M은 맥스웰, 860M은 두 가지가 같은 이름
        @"\bGTX TITAN(?! ?X)",                      // GTX TITAN·Black·Z — 케플러. GTX TITAN X는 맥스웰
        @"\bGEFORCE (?:[2-7]\d\d(?!\d)|8[0-2][05]M)", // 시리즈 글자 없는 GeForce 210·605·705M·710M, 800M~825M — 페르미·케플러 이전
        @"\bQUADRO K(?:420|600|2000D?|4[02]00|5[02]00|6000|500M|[56]10M|[1-5][01]00M)(?!\d)",   // 케플러 Quadro K — K620·K1200·K2200(M)은 맥스웰
        @"\bQUADRO (?:\d|FX)",                      // 페르미 Quadro 600·2000·4000·5000·6000, Quadro 410(케플러), FX
        @"\bTESLA (?:K\d|[CMS][12]0\d\d)",          // Tesla K10~K80(케플러), 페르미 C2050·M2090·S2050 — Tesla M4·M10·M40·M60(맥스웰)은 숫자가 짧아 걸리지 않는다
        @"\bGRID K\d"),                             // GRID K1·K2·K340·K520 — 케플러
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>꽂힌 NVIDIA 카드가 모두 채색 엔진을 돌릴 수 없는 옛 세대인지. smi = <see cref="QueryNvidiaAll"/>가 낸 목록 — 레지스트리의 어댑터 이름도 같이 본다.</summary>
    public static bool NvidiaTooOld(IReadOnlyList<Gpu> smi) => NvidiaTooOld(smi, DisplayAdapters());

    /// <summary>판정 — 하드웨어를 묻지 않는다. adapters = 레지스트리 DriverDesc 전부(<see cref="DisplayAdapters"/>).
    /// nvidia-smi가 답했으면 그 카드들이 모두 이름이 옛 세대이고 드라이버도 <see cref="MinDriverMajorCu126"/> 미만일 때만.
    /// 드라이버가 528 이상이면 그 드라이버가 붙은 카드는 케플러·페르미일 수 없으니(그 세대용 드라이버는 47x에서 끝났다) 이름이 어떻게 보이든 드라이버 쪽을 믿는다.
    /// nvidia-smi가 답하지 않으면(페르미의 39x 드라이버는 nvidia-smi를 PATH에 두지 않는다) 레지스트리에 NVIDIA 이름이 있을 때만.
    /// 어느 쪽이든 **옛 NVIDIA·인텔·AMD·가상 어댑터로 알아보지 못한 어댑터가 레지스트리에 하나라도 있으면 '너무 오래됨'이라고 하지 않는다**(<see cref="MayBeAnotherCard"/>).
    /// 2026-11-11 검수: 옛 카드를 새 카드로 바꾼 직후에는 새 카드가 드라이버 없이 'Microsoft 기본 디스플레이 어댑터'로 잡히고 NVIDIA 이름은 뽑아낸 옛 카드의 흔적뿐이라
    /// (GTX 770 흔적 + 기본 어댑터) "너무 오래됐어요"라고 잘못 말하고 [드라이버 받기]도 감췄다. nvidia-smi가 답해도 같다 — 옛 드라이버(47x)는 같이 꽂힌 새 카드를 보지 못한다.
    /// 그럴 때는 예전(v1.8.2)처럼 드라이버 안내를 보인다: 새 카드라면 드라이버를 깔면 풀리고, 정말 옛 카드뿐이었다면 예전과 같은 안내를 볼 뿐이다.
    /// 남는 한계: 뽑아낸 카드의 흔적 키와 꽂혀 있는 카드를 레지스트리로는 가를 수 없다 — 흔적 키만 남은 PC(새 카드를 옛 카드로 되돌린 경우, 목록에 없는 원격 화면 프로그램)는
    /// 옛 카드뿐이어도 드라이버 안내를 본다. 반대로 새 카드가 아예 어댑터로 잡히지 않은 PC는 여전히 '너무 오래됨'을 본다(그래서 안내 끝에 <see cref="NewCardHint"/>와 드라이버 주소를 남긴다).</summary>
    internal static bool NvidiaTooOld(IReadOnlyList<Gpu> smi, IReadOnlyList<string> adapters)
    {
        if (adapters.Any(MayBeAnotherCard)) return false;
        return smi.Count > 0 ? smi.All(g => g.DriverMajor < MinDriverMajorCu126 && LooksTooOldForGpuEngine(g.Name))
                             : NvidiaAdapters(adapters).Count > 0;   // 옛 세대가 아닌 NVIDIA 이름은 위에서 이미 걸렀다 — 남은 NVIDIA 이름은 전부 옛 세대
    }

    /// <summary>레지스트리의 이 어댑터가 '옛 NVIDIA 카드 말고 다른 그래픽카드'일 수 있는지 — 정체를 아는 것(너무 오래된 NVIDIA, 드라이버가 깔린 인텔·AMD, 가상·원격 화면)만 빼고 전부 그렇다고 본다.
    /// 드라이버 없는 카드의 이름은 Windows 언어마다 달라('Microsoft Basic Display Adapter'·'Microsoft 기본 디스플레이 어댑터') 그 이름을 찾지 않고 아는 이름을 뺀다.
    /// 옛 세대가 아닌 NVIDIA 이름(RTX 4070 등)도 여기 걸린다 — 꽂혀 있는데 지금 드라이버가 못 보는 카드일 수 있다.</summary>
    private static bool MayBeAnotherCard(string a) =>
        !IsVirtualName(a) && !IsRemoteDisplayName(a) && !IsAmdName(a) && !a.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !LooksTooOldForGpuEngine(a);

    // 그래픽카드가 아닌 화면 어댑터 가운데 이름에 'Virtual'이 없는 흔한 것: 원격 데스크톱(Microsoft Remote Display Adapter), Hyper-V 화면, Citrix 등의 Indirect Display, USB 독(DisplayLink), spacedesk.
    // IsVirtualName에 넣지 않은 까닭: 그쪽은 자동 감지·내장 AI의 VRAM 기준이 같이 쓴다 — '너무 오래됨' 판정에만 쓴다.
    private static bool IsRemoteDisplayName(string a) =>
        new[] { "Remote Display", "Hyper-V", "Indirect Display", "DisplayLink", "spacedesk" }.Any(k => a.Contains(k, StringComparison.OrdinalIgnoreCase));

    public const string AmdDriverUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const int MinDriverMajorCu126 = 528;      // CUDA 12.6 런타임(마이너 버전 호환)의 사실상 최소 드라이버

    /// <summary>레지스트리의 디스플레이 어댑터(DriverDesc) 목록 — WMI 없이 그래픽카드 이름·벤더를 본다.</summary>
    public static IReadOnlyList<string> DisplayAdapters()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string d && d.Length > 0) list.Add(d);
            }
        }
        catch { }
        return list;
    }

    /// <summary>레지스트리 `HardwareInformation.qwMemorySize`로 어댑터 VRAM(MB)을 읽는다 — nvidia-smi가 없는 AMD도 된다. 모르면 0.</summary>
    public static int AdapterVramMb(string adapterName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return 0;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string d || d != adapterName) continue;
                // 같은 이름의 000N 키가 여럿(드라이버 재설치 흔적)일 수 있다 — 값이 없는 유령 키는 건너뛰고 계속 찾는다
                var v = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                long bytes = v switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length == 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0,
                };
                if (bytes > 0) return (int)(bytes / 1048576);
            }
        }
        catch { }
        return 0;
    }

    /// <summary>가상 어댑터를 뺀 그래픽카드 중 전용 VRAM이 가장 큰 것(MB) — 레지스트리만 읽어 빠르다. 모르면 0.
    /// 내장 그래픽은 전용 메모리가 작게(수백 MB~1GB) 잡히므로 '내장 AI를 GPU에 올려도 되는가'의 기준으로 쓴다(Engine.OnnxEnv.GpuAllowed).</summary>
    public static int BestAdapterVramMb()
    {
        int best = 0;
        foreach (var a in DisplayAdapters().Where(a => !IsVirtualName(a)).Distinct()) best = Math.Max(best, AdapterVramMb(a));
        return best;
    }

    private static bool IsNvidiaName(string a) => a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || a.Contains("GeForce", StringComparison.OrdinalIgnoreCase);
    private static bool IsAmdName(string a) => a.Contains("AMD", StringComparison.OrdinalIgnoreCase) || a.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
    private static bool IsVirtualName(string a) => a.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || a.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    /// <summary>고른 벤더("nvidia"/"nvidia-old"/"amd")에 해당하는 어댑터 이름 — 없으면 "". 사용자가 종류를 직접 골랐을 때 다른 벤더 카드 이름으로 판정하지 않게.</summary>
    public static string AdapterNameFor(string vendor) => AdapterNameFor(vendor, DisplayAdapters());

    /// <summary>이미 읽어 둔 레지스트리 목록(<see cref="DisplayAdapters"/>)에서 고른다.</summary>
    internal static string AdapterNameFor(string vendor, IReadOnlyList<string> adapters)
    {
        var list = adapters.Where(a => !IsVirtualName(a));
        return (vendor == "amd" ? list.FirstOrDefault(IsAmdName) : list.FirstOrDefault(IsNvidiaName)) ?? "";
    }

    private static IReadOnlyList<string> NvidiaAdapters(IReadOnlyList<string> adapters) => adapters.Where(a => !IsVirtualName(a) && IsNvidiaName(a)).ToList();

    /// <summary>그래픽카드 종류 감지: "nvidia"(RTX 20+) / "nvidia-old"(GTX 10 이하) / "amd" / "none". 판정은 <see cref="JudgeVendor"/>.</summary>
    public static (string Vendor, string Name) DetectVendor() => DetectVendor(QueryNvidiaAll());

    /// <summary>이미 물어 둔 nvidia-smi 목록(<see cref="QueryNvidiaAll"/>)으로 감지 — 한 흐름에서 nvidia-smi를 다시 부르지 않게.</summary>
    public static (string Vendor, string Name) DetectVendor(IReadOnlyList<Gpu> smi) => JudgeVendor(smi, DisplayAdapters());

    /// <summary>그래픽카드 종류 판정 — 하드웨어를 묻지 않는다. smi = nvidia-smi가 본 카드(지금 꽂혀 있는 NVIDIA 카드, nvidia-smi가 없거나 답하지 않으면 빈 목록),
    /// adapters = 레지스트리 DriverDesc 목록(<see cref="DisplayAdapters"/>). 가상 어댑터(Parsec 등)는 무시.
    /// nvidia-smi가 답하면 그 카드들로만 보고 레지스트리는 nvidia-smi가 없을 때만 쓴다 — 레지스트리 000N 키는 뽑아낸 카드도 남긴다.
    /// 2026-11-11: 레지스트리의 첫 NVIDIA 키 하나로 판정해서, 뽑아낸 옛 카드의 흔적이 자동 감지를 뒤집었다(예: GTX 750 Ti 흔적 + RTX 5070이면 구형 CUDA 12.6 묶음).
    /// 여러 장이면 엔진이 쓸 카드 한 장(<see cref="PickNvidiaGpu"/>)의 세대와 이름 — 지원 세대가 한 장이라도 있으면 "nvidia".
    /// nvidia-smi가 없을 때 NVIDIA 이름이 있어도 Radeon이 있으면 AMD로 본다(카드 교체 PC).</summary>
    internal static (string Vendor, string Name) JudgeVendor(IReadOnlyList<Gpu> smi, IReadOnlyList<string> adapters)
    {
        if (smi.Count > 0) return NvidiaVendor(PickNvidiaGpu(smi)!.Name);
        var list = adapters.Where(a => !IsVirtualName(a)).ToList();
        var nv = list.Where(IsNvidiaName).ToList();
        var amd = list.FirstOrDefault(IsAmdName);
        if (nv.Count > 0 && amd == null) return NvidiaVendor(PickNvidia(nv)!);
        if (amd != null) return ("amd", amd);
        return ("none", list.FirstOrDefault() ?? "");
    }

    private static (string Vendor, string Name) NvidiaVendor(string name) => (LooksUnsupportedByCuda13(name) ? "nvidia-old" : "nvidia", name);

    /// <summary>NVIDIA 카드 여러 장 가운데 엔진(ComfyUI의 torch)이 쓸 카드 한 장 — 묶음 종류, 점검 창의 이름·VRAM, 맞춤 설정(AutoTune)의 VRAM이 모두 이 카드를 본다. 빈 목록이면 null.
    /// 엔진은 장치를 고르지 않고 CUDA 0번을 쓴다: ComfyUI의 get_torch_device가 torch.cuda.current_device()이고, Windows에서는 main.py가 CUDA_VISIBLE_DEVICES=0까지 건다(0.39.0에서 확인).
    /// 그런데 CUDA 0번은 nvidia-smi의 첫 줄이 아니다 — nvidia-smi는 PCI 버스 순으로 적고, CUDA는 기본값 CUDA_DEVICE_ORDER=FASTEST_FIRST로
    /// "단순한 휴리스틱으로 가장 빠른 카드부터" 센다(NVIDIA CUDA 환경변수 문서. 기준은 공개돼 있지 않다 — torch 포럼에 nvidia-smi 0번 TITAN Xp가 cuda:1로 잡힌 사례).
    /// nvidia-smi 출력으로 그 휴리스틱을 그대로 따라 할 수는 없어서 '가장 빠른 카드'를 이렇게 어림한다:
    ///   ① 세대 등급 — CUDA 13 지원 세대(튜링 이후) → 구형 묶음(CUDA 12.6)이 도는 세대 → 어느 묶음도 못 돌리는 세대(<see cref="LooksTooOldForGpuEngine"/>) 순으로 앞세운다.
    ///      옛 카드를 화면용·보조로 같이 꽂은 PC에서 그 카드 하나 때문에 구형 묶음을 고르거나 설치를 막지 않으려는 것.
    ///   ② 같은 등급 안에서는 이름으로 빠르기를 견준다(<see cref="GeForceModel"/>): GeForce RTX/GTX의 시리즈(40·30·20·16…)와 등급 번호(90·80·70·60…)를 읽어
    ///      시리즈가 같거나 새롭고 번호도 같거나 높은 카드가 이긴다(4070 > 2060, 3080 > 3060, 4090 > 3090. 둘 다 같으면 Ti·SUPER가 붙은 쪽).
    ///   ③ 그래도 한 장으로 좁혀지지 않으면 — 워크스테이션·모르는 이름(RTX A4000 등)이 섞였거나, 같은 카드 두 장이거나, 시리즈와 번호가 엇갈릴 때(4070과 3080, 4060 Ti 16GB와 3080 10GB) —
    ///      남은 후보 가운데 **VRAM이 작은 카드**. 어느 쪽이 빠른지 모를 때는 틀려도 해상도가 낮아질 뿐인 쪽으로 틀린다(큰 쪽으로 틀리면 메모리 부족으로 작업이 죽는다).
    ///      VRAM도 같으면 nvidia-smi 순서로 앞 카드. VRAM을 못 읽은 카드(0)는 맨 뒤.
    /// 2026-11-11까지는 지원 세대 가운데 nvidia-smi의 첫 줄을 골라 꽂은 슬롯에 따라 달라졌고, 그 뒤 한동안은 같은 등급에서 VRAM이 큰 카드를 골라
    /// 느린 카드가 메모리만 큰 조합(RTX 3080 10GB + RTX 3060 12GB)에서 엔진이 쓰지 않는 12GB에 맞췄다(검수 지적).
    /// 남는 한계: 이름으로 본 빠르기는 어림이다 — CUDA의 실제 기준과 다를 수 있고(같은 번호의 노트북용·데스크톱용, 세대를 건너뛴 조합), 엔진이 쓰는 카드를 확인하는 것은 아니다.
    /// 그래서 화면에 보이는 카드와 엔진이 쓰는 카드가 다를 수 있다 — ③ 덕분에 그때도 조정은 대개 낮은 쪽이다. 장치를 고르는 기능은 두지 않는다.</summary>
    internal static Gpu? PickNvidiaGpu(IReadOnlyList<Gpu> gpus)
    {
        if (gpus.Count == 0) return null;
        int top = gpus.Max(g => EngineRank(g.Name));
        var tier = gpus.Where(g => EngineRank(g.Name) == top).Select(g => (Gpu: g, Key: GeForceKey(g.Name))).ToList();
        // 후보 = 이름을 읽지 못한 카드 + 읽은 카드 가운데 다른 카드에 확실히 밀리지 않는 카드
        var candidates = tier.Where(c => c.Key is not { } k || !tier.Any(o => o.Key is { } ok && Beats(ok, k))).Select(c => c.Gpu);
        return candidates.OrderBy(g => g.MemoryMb > 0 ? g.MemoryMb : int.MaxValue).First();   // 정렬이 안정적이라 VRAM이 같으면 nvidia-smi 순서가 남는다. 후보가 한 장이면 그 카드
    }

    private static int EngineRank(string name) => !LooksUnsupportedByCuda13(name) ? 2 : LooksTooOldForGpuEngine(name) ? 0 : 1;

    // 'GeForce RTX 4070 Ti SUPER'·'GeForce GTX 1660 Ti'·'GeForce GTX 980M'·'GeForce GTX 1060 6GB' 꼴만 읽는다. 번호 체계가 다른 이름(RTX A4000, RTX 4000 Ada, Quadro RTX 4000, TITAN RTX, Tesla T4)은
    // 'GeForce'가 없어 걸리지 않는다 — 걸리면 'RTX 4000 Ada'를 40 시리즈의 00번으로 읽는다.
    private static readonly Regex GeForceModel = new(@"\bGEFORCE (?:RTX|GTX) ?(\d{3,4})(?!\d)(?:MX?\b)?( TI\b)?( SUPER\b)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>GeForce 이름에서 (시리즈, 등급 번호, Ti·SUPER) — 4070 Ti SUPER = (40, 70, 3), GTX 980 = (9, 80, 0). 읽을 수 없으면 null.</summary>
    private static (int Series, int Model, int Suffix)? GeForceKey(string name)
    {
        var m = GeForceModel.Match(name);
        if (!m.Success) return null;
        int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return (n / 100, n % 100, (m.Groups[2].Success ? 2 : 0) + (m.Groups[3].Success ? 1 : 0));   // 무인 < SUPER < Ti < Ti SUPER
    }

    /// <summary>a가 b보다 확실히 빠르다고 볼 수 있는지 — 시리즈도 번호도 뒤지지 않고 하나는 앞설 때만. 엇갈리면(4070과 3080) 어느 쪽도 이기지 않는다.</summary>
    private static bool Beats((int Series, int Model, int Suffix) a, (int Series, int Model, int Suffix) b) =>
        a.Series >= b.Series && a.Model >= b.Model && (a.Series > b.Series || a.Model > b.Model || a.Suffix > b.Suffix);

    /// <summary>이름만 아는 목록(nvidia-smi가 없어 레지스트리로 볼 때)에서 고른다 — <see cref="PickNvidiaGpu"/>와 같은 규칙, VRAM을 몰라 이름으로 못 가르면 첫 이름. 빈 목록이면 null.</summary>
    internal static string? PickNvidia(IReadOnlyList<string> names) => PickNvidiaGpu(names.Select(n => new Gpu(n, "", 0, 0)).ToList())?.Name;

    /// <summary>'RTX 20 이상'(CUDA 13 묶음)을 막을지 — nvidia-smi가 본 카드가 모두 CUDA 13이 버린 세대일 때만. 지원 카드가 한 장이라도 있으면 막지 않는다.</summary>
    internal static bool BlocksCuda13(IReadOnlyList<string> smiNames) => smiNames.Count > 0 && smiNames.All(LooksUnsupportedByCuda13);

    /// <summary>ROCm(Windows)이 지원하는 Radeon 세대인지 이름으로 거칠게 판정: RX 6000(RDNA2)·7000(RDNA3)·9000(RDNA4)·Ryzen AI Max 내장. RX 5000·Vega·내장 Vega는 미지원.</summary>
    public static bool LooksRocmSupported(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("RX 6") || n.Contains("RX 7") || n.Contains("RX 9") || n.Contains("AI MAX") || n.Contains("W7") || n.Contains("PRO R9")) return true;
        return false;
    }

    public static Report CheckForComfy() => CheckForComfy("auto");

    /// <summary>ComfyUI 묶음 조건 점검. vendor = 설정 GpuVendor("auto"면 감지).</summary>
    public static Report CheckForComfy(string vendor) => CheckForComfy(vendor, QueryNvidiaAll());

    /// <summary>이미 물어 둔 nvidia-smi 목록(<see cref="QueryNvidiaAll"/>)으로 점검 — 감지와 드라이버·세대 점검이 같은 카드 목록을 보고, 같은 흐름의 맞춤 설정(AutoTune.Build)도 그 목록을 쓴다.</summary>
    public static Report CheckForComfy(string vendor, IReadOnlyList<Gpu> smi) => CheckForComfy(vendor, smi, DisplayAdapters());

    /// <summary>adapters = 레지스트리 DriverDesc 목록(<see cref="DisplayAdapters"/>) — 그래픽카드 쪽 판정은 넘겨받은 두 목록으로만 한다(VC++·RAM·AMD의 VRAM은 여기서 읽는다).</summary>
    internal static Report CheckForComfy(string vendor, IReadOnlyList<Gpu> smi, IReadOnlyList<string> adapters)
    {
        var items = new List<Item>();
        bool tooOld = false;
        var (detected, detName) = JudgeVendor(smi, adapters);
        if (vendor == "auto") vendor = detected;
        else detName = AdapterNameFor(vendor, adapters);   // 사용자가 종류를 직접 골랐으면 그 벤더의 어댑터 이름만 쓴다
        if (vendor == "amd")
        {
            bool win11 = Environment.OSVersion.Version.Build >= 22000;
            bool found = detName.Length > 0;
            items.Add(new("그래픽카드", found, false, found ? detName + " (AMD — ROCm 포터블)" : "AMD Radeon을 찾지 못했어요 — 그래도 AMD용(ROCm) 포터블을 받아요. 종류를 잘못 고른 건 아닌지 확인하세요.", null, null));
            items.Add(new("Windows 11", win11, true,
                win11 ? $"빌드 {Environment.OSVersion.Version.Build} (충분)" : $"빌드 {Environment.OSVersion.Version.Build} — AMD용 엔진(ROCm)은 Windows 11이 필요해요.", null, null));
            bool genOk = !found || LooksRocmSupported(detName);
            items.Add(new("Radeon 세대", genOk, true,
                genOk ? "RDNA2 이상(RX 6000/7000/9000)으로 보여요. AMD Software(Adrenalin)를 최신으로 유지하세요." : $"{detName} — ROCm(Windows)이 지원하지 않는 세대예요(RX 5000·Vega 등). 번역·검열제거는 되지만 채색·재생성은 돌릴 수 없어요.",
                genOk ? AmdDriverUrl : null, "AMD 드라이버 받기"));
            int vram = found ? AdapterVramMb(detName) : 0;
            if (vram > 0)
                items.Add(new("VRAM", vram >= 8000, false, $"{vram / 1024.0:0.#}GB" + (vram < 8000 ? " — ROCm은 메모리를 넉넉히 써서 8GB 미만이면 채색·재생성이 실패하기 쉬워요 (--lowvram·1.0MP 권장)" : ""), null, null));
            items.Add(new("참고", true, false, "AMD용 엔진은 ComfyUI 공식 ROCm 포터블(초기 지원)이라 NVIDIA보다 느리거나 일부 노드가 안 될 수 있어요. VRAM 8GB 이상 권장.", null, null));
        }
        else if (vendor == "none")
        {
            items.Add(new("그래픽카드", false, true, "NVIDIA나 AMD 그래픽카드를 찾지 못했어요. 채색·재생성·자가 학습은 전용 GPU가 필요해요 (번역·검열제거는 CPU로 돼요).", null, null));
        }
        else
        {
            items.AddRange(NvidiaItems(vendor == "nvidia-old", smi, adapters));
            tooOld = NvidiaTooOld(smi, adapters);
        }
        var vc = VcRuntime();
        bool vcOk = vc != null && (vc.Major > 14 || (vc.Major == 14 && vc.Minor >= MinVcBuild));
        items.Add(new("Visual C++ 런타임 (x64)", vcOk, true,
            vcOk ? $"{vc} (충분)" : (vc == null ? "설치돼 있지 않아요" : $"{vc} — 너무 오래됐어요") + ". 엔진의 torch가 VC++ 2015–2022 x64 런타임을 요구해요 — 받아서 설치한 뒤 다시 확인하세요.",
            vcOk ? null : VcRedistUrl, "VC++ 런타임 받기"));
        double ram = RamGb();
        bool ramOk = ram >= 15.5;
        items.Add(new("메모리(RAM)", ramOk, false,
            ramOk ? $"{ram:0}GB" + (ram < 30 ? " — 16GB는 채색 중 다른 프로그램을 닫아 두는 편이 안전해요" : "") : $"{ram:0}GB — 채색 엔진(SDXL)은 16GB 이상을 권해요.", null, null));
        return new Report(items, tooOld);
    }

    /// <summary>NVIDIA 쪽 점검 항목(드라이버·그래픽카드·VRAM) — 하드웨어를 묻지 않는다. old = 구형 묶음(CUDA 12.6)을 받는 경우,
    /// smi = nvidia-smi가 본 카드, adapters = 레지스트리 DriverDesc 전부(카드 이름은 nvidia-smi가 답하지 않을 때만 여기서 고르고, '너무 오래됨' 판정은 늘 같이 본다).</summary>
    internal static IReadOnlyList<Item> NvidiaItems(bool old, IReadOnlyList<Gpu> smi, IReadOnlyList<string> adapters)
    {
        var items = new List<Item>();
        var gpu = PickNvidiaGpu(smi);   // 여러 장이면 엔진이 쓸 카드의 이름·VRAM을 보인다(드라이버는 모든 카드가 같다)
        // 케플러·페르미와 그 이전: 어느 묶음으로도 채색 엔진이 돌지 않는다 — 드라이버·VRAM 항목을 늘어놓으면 "드라이버를 올리세요"라는, 따를 수 없는 안내가 된다(2026-11-11까지 그랬다).
        // 할 수 없다는 것과 그래도 되는 것만 한 줄로 말한다. 드라이버 주소는 남긴다 — 새 카드를 막 달아 아직 어댑터로도 잡히지 않은 PC라면 이 판정이 틀렸고 드라이버를 깔아야 풀린다(NewCardHint)
        if (NvidiaTooOld(smi, adapters))
        {
            var name = gpu?.Name ?? PickNvidia(NvidiaAdapters(adapters)) ?? "";
            items.Add(new("그래픽카드", false, true,
                $"{name} — 너무 오래된 그래픽카드라 GPU 채색(채색·재생성·자가 학습)은 쓸 수 없어요. 이 세대는 새 드라이버가 더 나오지 않아 드라이버를 올려도 해결되지 않아요. 번역과 기본 검열 제거는 CPU로 그대로 쓸 수 있어요.",
                DriverUrl, "드라이버 받기"));
            return items;
        }
        int minDrv = old ? MinDriverMajorCu126 : MinDriverMajor;
        if (gpu == null)
        {
            items.Add(new("NVIDIA 드라이버", false, true, "NVIDIA 드라이버를 찾지 못했어요 (nvidia-smi 없음). 드라이버를 설치해 주세요.", DriverUrl, "드라이버 받기"));
            return items;
        }
        bool drvOk = gpu.DriverMajor >= minDrv;
        items.Add(new("NVIDIA 드라이버", drvOk, true,
            drvOk ? $"{gpu.Driver} (충분)" : $"{gpu.Driver} — 엔진의 {(old ? "CUDA 12.6" : "CUDA 13")} 런타임은 {minDrv} 이상이 필요해요. 최신 GeForce 드라이버로 올린 뒤 재부팅하세요.",
            drvOk ? null : DriverUrl, "드라이버 받기"));
        // 사용자가 'RTX 20 이상'을 골랐는데 지금 꽂힌 카드가 모두 CUDA 13이 버린 세대면 막는다 — CUDA 13 빌드가 이 세대를 지원하지 않아 설치해도 채색 엔진이 돌지 않는다
        // (c10.dll 1114 사고는 지원 세대인 GTX 1660에 옛 드라이버를 쓴 경우로, 위 드라이버 항목이 맡는다). 지원 카드가 한 장이라도 있으면 막지 않는다
        bool genBad = !old && BlocksCuda13(smi.Select(g => g.Name).ToList());
        items.Add(new("그래픽카드", !genBad, true, genBad
            ? $"{gpu.Name} — CUDA 13이 지원하지 않는 세대예요. 그래픽카드 종류를 'NVIDIA GTX 10 이하 (구형)'으로 바꿔 주세요."
            : gpu.Name + (old ? " — GTX 10 이하용 CUDA 12.6 포터블을 받아요 (느릴 수 있어요)" : " — CUDA 13 포터블"), null, null));
        bool vramOk = gpu.MemoryMb >= 6000;
        items.Add(new("VRAM", vramOk, false,
            vramOk ? $"{gpu.MemoryMb / 1024.0:0.#}GB" + (gpu.MemoryMb < 8000 ? " — 6GB는 빠듯해요: 엔진 인자 --lowvram, 채색 해상도 1.0MP, 재생성 축소 MP 1.0을 권해요" : "")
                   : $"{gpu.MemoryMb / 1024.0:0.#}GB — 6GB 미만이라 채색·재생성이 메모리 부족으로 실패하기 쉬워요.", null, null));
        return items;
    }

    /// <summary>ComfyUI가 시작 직후 죽었을 때 로그 꼬리에서 흔한 원인을 읽어 사람이 할 일을 알려 준다.</summary>
    public static string? DiagnoseComfyLog(string logPath, string vendor = "auto") => DiagnoseComfyLog(logPath, vendor, QueryNvidiaAll);

    /// <summary>querySmi = nvidia-smi를 묻는 함수(<see cref="QueryNvidiaAll"/>) — torch DLL 오류일 때만, 한 번만 부른다(점검과 벤더 판정이 같은 목록을 쓴다).
    /// adapters = 레지스트리 DriverDesc 목록(null이면 여기서 읽는다).</summary>
    internal static string? DiagnoseComfyLog(string logPath, string vendor, Func<IReadOnlyList<Gpu>> querySmi, IReadOnlyList<string>? adapters = null)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            var txt = File.ReadAllText(logPath);
            if (txt.Contains("WinError 1114") || txt.Contains("c10.dll") || txt.Contains("DLL load failed") || txt.Contains("WinError 126"))
            {
                var smi = querySmi();
                adapters ??= DisplayAdapters();
                var r = CheckForComfy(vendor, smi, adapters);
                var why = r.Items.Where(i => !i.Ok && i.Blocking).Select(i => i.Title + ": " + i.Detail).ToList();
                bool amd = (vendor == "auto" ? JudgeVendor(smi, adapters).Vendor : vendor) == "amd";
                // 너무 오래된 카드에 "(드라이버: …, VC++: …)"를 붙이면 받아도 소용없는 것을 받으라는 말이 된다 — 새 카드를 막 단 경우에 쓸 드라이버 주소만 남긴다
                return "엔진의 torch DLL을 올리지 못했어요. " + (why.Count > 0 ? string.Join(" / ", why)
                    : amd ? "최신 AMD Software(Adrenalin)와 VC++ 2015–2022 x64 런타임이 필요해요."
                          : $"NVIDIA 드라이버 {MinDriverMajor} 이상과 VC++ 2015–2022 x64 런타임이 필요해요.")
                    + (r.GpuTooOld ? $" {NewCardHint} 다시 시도해 주세요 (드라이버: {DriverUrl})"
                                   : $" (드라이버: {(amd ? AmdDriverUrl : DriverUrl)}, VC++: {VcRedistUrl})");
            }
            if (txt.Contains("CUDA out of memory") || txt.Contains("OutOfMemoryError"))
                return "GPU 메모리가 부족했어요 — 설정 → 엔진 → 고급 옵션의 추가 실행 인자에 --lowvram 을 넣고, 채색 해상도 상한·재생성 축소 MP를 1.0으로 낮춰 보세요.";
            if (txt.Contains("Torch not compiled with CUDA") || txt.Contains("no kernel image is available"))
                return "이 그래픽카드는 엔진 빌드가 지원하지 않아요. NVIDIA GTX 10 이하면 설정 → 엔진의 '그래픽카드 종류'를 'GTX 10 이하'로 바꾸고 ComfyUI 포터블 폴더를 지운 뒤 구성요소를 다시 설치하세요 (AMD는 RX 6000 이상 필요). 번역·검열제거는 돌아가요.";
            if (txt.Contains("HIP error") || txt.Contains("hipErrorNoBinaryForGpu") || txt.Contains("rocBLAS"))
                return "AMD 엔진(ROCm)이 이 Radeon을 지원하지 않거나 드라이버가 오래됐어요. AMD Software(Adrenalin)를 최신으로 올리고, RX 6000/7000/9000 계열인지 확인하세요.";
            return null;
        }
        catch { return null; }
    }
}
