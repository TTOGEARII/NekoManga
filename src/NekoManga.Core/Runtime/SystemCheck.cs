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
    public sealed record Report(IReadOnlyList<Item> Items)
    {
        public bool HasBlocking => Items.Any(i => !i.Ok && i.Blocking);
        public bool HasWarning => Items.Any(i => !i.Ok && !i.Blocking);
        public string Summary => string.Join("\n", Items.Where(i => !i.Ok).Select(i => (i.Blocking ? "✖ " : "⚠ ") + i.Title + " — " + i.Detail));
    }

    /// <summary>nvidia-smi로 드라이버·GPU 이름·VRAM을 읽는다. 여러 장이면 판정에 쓰는 카드(<see cref="PickNvidia"/> — 지원 세대가 있으면 그 카드). 드라이버가 없으면 null.</summary>
    public static Gpu? QueryNvidia() => PickNvidiaGpu(QueryNvidiaAll());

    /// <summary>nvidia-smi가 보는 NVIDIA 카드 전부(지금 꽂혀 있는 카드). 드라이버가 없거나 nvidia-smi가 답하지 않으면 빈 목록.</summary>
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
    public static string AdapterNameFor(string vendor)
    {
        var list = DisplayAdapters().Where(a => !IsVirtualName(a));
        return (vendor == "amd" ? list.FirstOrDefault(IsAmdName) : list.FirstOrDefault(IsNvidiaName)) ?? "";
    }

    /// <summary>그래픽카드 종류 감지: "nvidia"(RTX 20+) / "nvidia-old"(GTX 10 이하) / "amd" / "none". 판정은 <see cref="JudgeVendor"/>.</summary>
    public static (string Vendor, string Name) DetectVendor() => JudgeVendor(QueryNvidiaAll().Select(g => g.Name).ToList(), DisplayAdapters());

    /// <summary>그래픽카드 종류 판정 — 하드웨어를 묻지 않는다. smiNames = nvidia-smi가 본 카드 이름(지금 꽂혀 있는 NVIDIA 카드, nvidia-smi가 없거나 답하지 않으면 빈 목록),
    /// adapters = 레지스트리 DriverDesc 목록(<see cref="DisplayAdapters"/>). 가상 어댑터(Parsec 등)는 무시.
    /// nvidia-smi가 답하면 그 이름들로만 보고 레지스트리는 nvidia-smi가 없을 때만 쓴다 — 레지스트리 000N 키는 뽑아낸 카드도 남긴다.
    /// 2026-11-11: 레지스트리의 첫 NVIDIA 키 하나로 판정해서, 뽑아낸 옛 카드의 흔적이 자동 감지를 뒤집었다(예: GTX 750 Ti 흔적 + RTX 5070이면 구형 CUDA 12.6 묶음).
    /// 여러 장이면 지원 세대가 한 장이라도 있을 때 "nvidia"(<see cref="PickNvidia"/>). nvidia-smi가 없을 때 NVIDIA 이름이 있어도 Radeon이 있으면 AMD로 본다(카드 교체 PC).</summary>
    internal static (string Vendor, string Name) JudgeVendor(IReadOnlyList<string> smiNames, IReadOnlyList<string> adapters)
    {
        if (smiNames.Count > 0) return NvidiaVendor(smiNames);
        var list = adapters.Where(a => !IsVirtualName(a)).ToList();
        var nv = list.Where(IsNvidiaName).ToList();
        var amd = list.FirstOrDefault(IsAmdName);
        if (nv.Count > 0 && amd == null) return NvidiaVendor(nv);
        if (amd != null) return ("amd", amd);
        return ("none", list.FirstOrDefault() ?? "");
    }

    private static (string Vendor, string Name) NvidiaVendor(IReadOnlyList<string> names)
    {
        var name = PickNvidia(names) ?? "";
        return (LooksUnsupportedByCuda13(name) ? "nvidia-old" : "nvidia", name);
    }

    /// <summary>NVIDIA 카드 여러 장 가운데 판정에 쓸 카드 — CUDA 13 지원 세대(튜링 이후)가 한 장이라도 있으면 그 첫 카드, 모두 옛 세대면 첫 카드. 빈 목록이면 null.
    /// 옛 카드를 화면용·보조로 같이 꽂은 PC가 있다 — 그 카드 하나 때문에 구형 묶음을 고르거나 설치를 막지 않는다.</summary>
    internal static string? PickNvidia(IReadOnlyList<string> names) => names.FirstOrDefault(n => !LooksUnsupportedByCuda13(n)) ?? names.FirstOrDefault();

    private static Gpu? PickNvidiaGpu(IReadOnlyList<Gpu> gpus)
    {
        var name = PickNvidia(gpus.Select(g => g.Name).ToList());
        return gpus.FirstOrDefault(g => g.Name == name);
    }

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
    public static Report CheckForComfy(string vendor)
    {
        var items = new List<Item>();
        // nvidia-smi는 한 번만 부른다 — 감지와 아래 드라이버·세대 점검이 같은 카드 목록을 본다
        var smi = QueryNvidiaAll();
        var smiNames = smi.Select(g => g.Name).ToList();
        var (detected, detName) = JudgeVendor(smiNames, DisplayAdapters());
        if (vendor == "auto") vendor = detected;
        else detName = AdapterNameFor(vendor);   // 사용자가 종류를 직접 골랐으면 그 벤더의 어댑터 이름만 쓴다
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
            var gpu = PickNvidiaGpu(smi);   // 여러 장이면 판정에 쓴 카드의 이름·VRAM을 보인다(드라이버는 모든 카드가 같다)
            bool old = vendor == "nvidia-old";
            int minDrv = old ? MinDriverMajorCu126 : MinDriverMajor;
            if (gpu == null)
                items.Add(new("NVIDIA 드라이버", false, true, "NVIDIA 드라이버를 찾지 못했어요 (nvidia-smi 없음). 드라이버를 설치해 주세요.", DriverUrl, "드라이버 받기"));
            else
            {
                bool drvOk = gpu.DriverMajor >= minDrv;
                items.Add(new("NVIDIA 드라이버", drvOk, true,
                    drvOk ? $"{gpu.Driver} (충분)" : $"{gpu.Driver} — 엔진의 {(old ? "CUDA 12.6" : "CUDA 13")} 런타임은 {minDrv} 이상이 필요해요. 최신 GeForce 드라이버로 올린 뒤 재부팅하세요.",
                    drvOk ? null : DriverUrl, "드라이버 받기"));
                // 사용자가 'RTX 20 이상'을 골랐는데 지금 꽂힌 카드가 모두 CUDA 13이 버린 세대면 막는다 — CUDA 13 빌드가 이 세대를 지원하지 않아 설치해도 채색 엔진이 돌지 않는다
                // (c10.dll 1114 사고는 지원 세대인 GTX 1660에 옛 드라이버를 쓴 경우로, 위 드라이버 항목이 맡는다). 지원 카드가 한 장이라도 있으면 막지 않는다
                bool genBad = !old && BlocksCuda13(smiNames);
                items.Add(new("그래픽카드", !genBad, true, genBad
                    ? $"{gpu.Name} — CUDA 13이 지원하지 않는 세대예요. 그래픽카드 종류를 'NVIDIA GTX 10 이하 (구형)'으로 바꿔 주세요."
                    : gpu.Name + (old ? " — GTX 10 이하용 CUDA 12.6 포터블을 받아요 (느릴 수 있어요)" : " — CUDA 13 포터블"), null, null));
                bool vramOk = gpu.MemoryMb >= 6000;
                items.Add(new("VRAM", vramOk, false,
                    vramOk ? $"{gpu.MemoryMb / 1024.0:0.#}GB" + (gpu.MemoryMb < 8000 ? " — 6GB는 빠듯해요: 엔진 인자 --lowvram, 채색 해상도 1.0MP, 재생성 축소 MP 1.0을 권해요" : "")
                           : $"{gpu.MemoryMb / 1024.0:0.#}GB — 6GB 미만이라 채색·재생성이 메모리 부족으로 실패하기 쉬워요.", null, null));
            }
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
        return new Report(items);
    }

    /// <summary>ComfyUI가 시작 직후 죽었을 때 로그 꼬리에서 흔한 원인을 읽어 사람이 할 일을 알려 준다.</summary>
    public static string? DiagnoseComfyLog(string logPath, string vendor = "auto")
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            var txt = File.ReadAllText(logPath);
            if (txt.Contains("WinError 1114") || txt.Contains("c10.dll") || txt.Contains("DLL load failed") || txt.Contains("WinError 126"))
            {
                var r = CheckForComfy(vendor);
                var why = r.Items.Where(i => !i.Ok && i.Blocking).Select(i => i.Title + ": " + i.Detail).ToList();
                bool amd = (vendor == "auto" ? DetectVendor().Vendor : vendor) == "amd";
                return "엔진의 torch DLL을 올리지 못했어요. " + (why.Count > 0 ? string.Join(" / ", why)
                    : amd ? "최신 AMD Software(Adrenalin)와 VC++ 2015–2022 x64 런타임이 필요해요."
                          : $"NVIDIA 드라이버 {MinDriverMajor} 이상과 VC++ 2015–2022 x64 런타임이 필요해요.")
                    + $" (드라이버: {(amd ? AmdDriverUrl : DriverUrl)}, VC++: {VcRedistUrl})";
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
