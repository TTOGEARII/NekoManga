using System.Diagnostics;
using System.Globalization;
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

    /// <summary>nvidia-smi로 드라이버·GPU 이름·VRAM을 읽는다. 드라이버가 없으면 null.</summary>
    public static Gpu? QueryNvidia()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version,name,memory.total --format=csv,noheader,nounits")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            if (string.IsNullOrWhiteSpace(line)) return null;
            var parts = line.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 3) return null;
            int major = int.TryParse(parts[0].Split('.')[0], out var m) ? m : 0;
            int mem = int.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? mm : 0;
            return new Gpu(parts[1], parts[0], major, mem);
        }
        catch { return null; }
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

    /// <summary>CUDA 13이 버린 세대(맥스웰·파스칼·볼타 — GTX 9xx/10xx, MX, TITAN V 등)인지 이름으로 거칠게 판정.</summary>
    public static bool LooksUnsupportedByCuda13(string name)
    {
        var n = name.ToUpperInvariant();
        string[] old = ["GTX 9", "GTX 10", "GT 7", "GT 10", "MX1", "MX2", "MX3", "TITAN X", "TITAN V", "QUADRO P", "QUADRO M", "TESLA P", "TESLA V", "TESLA M"];
        return old.Any(n.Contains);
    }

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
                var v = k.GetValue("HardwareInformation.qwMemorySize");
                long bytes = v switch { long l => l, int i => (uint)i, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => 0 };
                return (int)(bytes / 1048576);
            }
        }
        catch { }
        return 0;
    }

    /// <summary>그래픽카드 종류 감지: "nvidia"(RTX 20+) / "nvidia-old"(GTX 10 이하) / "amd" / "none". 가상 어댑터(Parsec 등)는 무시.</summary>
    public static (string Vendor, string Name) DetectVendor()
    {
        var adapters = DisplayAdapters().Where(a => !a.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !a.Contains("Parsec", StringComparison.OrdinalIgnoreCase)).ToList();
        var nv = adapters.FirstOrDefault(a => a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || a.Contains("GeForce", StringComparison.OrdinalIgnoreCase));
        if (nv != null) return (LooksUnsupportedByCuda13(nv) ? "nvidia-old" : "nvidia", nv);
        var amd = adapters.FirstOrDefault(a => a.Contains("AMD", StringComparison.OrdinalIgnoreCase) || a.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
        if (amd != null) return ("amd", amd);
        return ("none", adapters.FirstOrDefault() ?? "");
    }

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
        var (detected, detName) = DetectVendor();
        if (vendor == "auto") vendor = detected;
        else
        {
            // 사용자가 종류를 직접 골랐으면 그 벤더의 어댑터 이름만 쓴다 (NVIDIA PC에서 AMD를 골라도 NVIDIA 이름으로 판정하지 않게)
            bool wantAmd = vendor == "amd";
            detName = DisplayAdapters().FirstOrDefault(a => wantAmd
                ? a.Contains("AMD", StringComparison.OrdinalIgnoreCase) || a.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                : a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || a.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) ?? "";
        }
        if (vendor == "amd")
        {
            bool win11 = Environment.OSVersion.Version.Build >= 22000;
            items.Add(new("그래픽카드", true, false, detName.Length > 0 ? detName + " (AMD — ROCm 포터블)" : "AMD Radeon을 찾지 못했지만 AMD용(ROCm) 포터블을 받아요", null, null));
            items.Add(new("Windows 11", win11, true,
                win11 ? $"빌드 {Environment.OSVersion.Version.Build} (충분)" : $"빌드 {Environment.OSVersion.Version.Build} — AMD용 엔진(ROCm)은 Windows 11이 필요해요.", null, null));
            bool genOk = detName.Length == 0 || LooksRocmSupported(detName);
            items.Add(new("Radeon 세대", genOk, true,
                genOk ? "RDNA2 이상(RX 6000/7000/9000)으로 보여요. AMD Software(Adrenalin)를 최신으로 유지하세요." : $"{detName} — ROCm(Windows)이 지원하지 않는 세대예요(RX 5000·Vega 등). 번역·검열제거는 되지만 채색·재생성은 돌릴 수 없어요.",
                genOk ? AmdDriverUrl : null, "AMD 드라이버 받기"));
            items.Add(new("참고", true, false, "AMD용 엔진은 ComfyUI 공식 ROCm 포터블(초기 지원)이라 NVIDIA보다 느리거나 일부 노드가 안 될 수 있어요. VRAM 8GB 이상 권장.", null, null));
        }
        else if (vendor == "none")
        {
            items.Add(new("그래픽카드", false, true, "NVIDIA나 AMD 그래픽카드를 찾지 못했어요. 채색·재생성·자가 학습은 전용 GPU가 필요해요 (번역·검열제거는 CPU로 돼요).", null, null));
        }
        else
        {
            var gpu = QueryNvidia();
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
                items.Add(new("그래픽카드", true, false, gpu.Name + (old ? " — GTX 10 이하용 CUDA 12.6 포터블을 받아요 (느릴 수 있어요)" : " — CUDA 13 포터블"), null, null));
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
    public static string? DiagnoseComfyLog(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            var txt = File.ReadAllText(logPath);
            if (txt.Contains("WinError 1114") || txt.Contains("c10.dll") || txt.Contains("DLL load failed") || txt.Contains("WinError 126"))
            {
                var r = CheckForComfy();
                var why = r.Items.Where(i => !i.Ok && i.Blocking).Select(i => i.Title + ": " + i.Detail).ToList();
                return "엔진의 torch DLL을 올리지 못했어요. " + (why.Count > 0 ? string.Join(" / ", why)
                    : $"NVIDIA 드라이버 {MinDriverMajor} 이상과 VC++ 2015–2022 x64 런타임이 필요해요.") + $" (드라이버: {DriverUrl}, VC++: {VcRedistUrl})";
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
