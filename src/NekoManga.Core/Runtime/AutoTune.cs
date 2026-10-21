using System.Globalization;

namespace NekoManga.Core.Runtime;

/// <summary>설치 PC 사양(그래픽카드·VRAM·RAM)에 맞춰 설정을 추천·적용한다 (2026-10-21, 사용자 "설치할 때 PC 환경에 맞춰 세팅되게").
/// 기준은 전부 이 프로젝트에서 실측한 것: 8GB(RTX 2070S)에서 채색 1.5MP가 상한이고 전체 페이지 재생성은 축소 1.5MP가 안전,
/// 6GB는 --lowvram + 1.0MP, ComfyUI가 RAM을 13GB까지 먹어 16GB 미만 PC에선 미리 켜기·꺼두지 않기를 끄는 게 낫다.
/// 전용 GPU가 없거나 엔진 빌드가 지원하지 않으면 채색·엔진 검열제거를 끄고 내장 엔진(번역·기본 검열제거)만 쓰게 한다.
/// 값이 이미 같으면 변경 목록에 넣지 않는다 — 사용자가 손댄 값이 바뀌는지 목록으로 보고 고를 수 있게.</summary>
public static class AutoTune
{
    /// <summary>Key = 적용 분기 식별자(표시 문구와 분리 — 라벨을 고쳐도 Apply가 안 깨지게). Label/From/To/Why는 사용자에게 보여 주는 글.</summary>
    public sealed record Change(string Key, string Label, string From, string To, string Why, Action<AppSettings> Apply);

    public sealed record Plan(string Vendor, string GpuName, int VramMb, double RamGb, IReadOnlyList<Change> Changes)
    {
        public string HardwareLine =>
            (GpuName.Length > 0 ? GpuName : Vendor switch { "amd" => "AMD Radeon (이름 미확인)", "nvidia" or "nvidia-old" => "NVIDIA (이름 미확인)", _ => "전용 그래픽카드 없음" }) +
            (VramMb > 0 ? $" · VRAM {VramMb / 1024.0:0.#}GB" : "") +
            (RamGb > 0 ? $" · RAM {RamGb:0}GB" : "");
        public string Summary => Changes.Count == 0
            ? "지금 설정이 이 PC에 맞아요 — 바꿀 것이 없어요."
            : string.Join("\n", Changes.Select(c => $"• {c.Label}: {c.From} → {c.To}  ({c.Why})"));
    }

    /// <summary>현재 설정과 비교해 바꿀 항목을 계산한다 (적용은 안 함). 하드웨어 조회는 1~2초(nvidia-smi) 걸릴 수 있으니 UI 스레드 밖에서 부를 것.</summary>
    public static Plan Build(AppSettings s)
    {
        var (vendor, name) = SystemCheck.DetectVendor();
        if (s.GpuVendor != "auto") { vendor = s.GpuVendor; name = SystemCheck.AdapterNameFor(vendor); }   // 고른 벤더의 카드 이름으로만 판정
        int vram = 0;
        var nv = vendor is "nvidia" or "nvidia-old" ? SystemCheck.QueryNvidia() : null;
        if (nv != null) { vram = nv.MemoryMb; if (name.Length == 0) name = nv.Name; }
        if (vram == 0 && name.Length > 0) vram = SystemCheck.AdapterVramMb(name);
        double ram = SystemCheck.RamGb();

        var ch = new List<Change>();
        var d = new AppSettings(); // 기본값 원본
        string Mp(double v) => v <= 0 ? "원본 크기" : v.ToString("0.#", CultureInfo.InvariantCulture) + "MP";

        bool gpuUsable = vendor is "nvidia" or "nvidia-old" or "amd"
                         && (vendor != "amd" || name.Length == 0 || SystemCheck.LooksRocmSupported(name));
        if (!gpuUsable)
        {
            string why = vendor == "none" ? "전용 그래픽카드가 없어요" : "엔진 빌드가 이 그래픽카드를 지원하지 않아요";
            if (s.EnableAutoColor) ch.Add(new("color-off", "자동 채색", "켬", "끔", why, x => x.EnableAutoColor = false));
            if (s.DecensorMode is "ComfyLama" or "ComfyRegen" or "ComfyCustom") ch.Add(new("decensor-basic", "검열 제거 방식", s.DecensorMode, "기본 (내장 LaMa)", why, x => x.DecensorMode = "Basic"));
            if (s.EnableSelfTraining) ch.Add(new("selftrain-off", "자가 학습", "켬", "끔", why, x => x.EnableSelfTraining = false));
        }
        else if (vram > 0)
        {
            // VRAM 등급: <8 빠듯(--lowvram·1.0MP) / 8~12 표준(실측 기준 PC: 1.5MP) / 12+ 여유(2.0MP·재생성 원본 크기)
            double colorMp = vram < 8000 ? 1.0 : vram < 12000 ? 1.5 : 2.0;
            double regenMp = vram < 8000 ? 1.0 : vram < 12000 ? 1.5 : 0;
            string tier = $"VRAM {vram / 1024.0:0.#}GB";
            bool lowvram = vram < 8000;
            if (Math.Abs(s.ColorMegapixels - colorMp) > 0.01) ch.Add(new("color-mp", "채색 해상도", Mp(s.ColorMegapixels), Mp(colorMp), tier, x => x.ColorMegapixels = colorMp));
            if (Math.Abs(s.RegenMegapixels - regenMp) > 0.01) ch.Add(new("regen-mp", "재생성 축소", Mp(s.RegenMegapixels), Mp(regenMp), tier, x => x.RegenMegapixels = regenMp));
            // 선화 추출 해상도는 빠듯한 카드에서만 낮춘다 — 넉넉한 카드에서 사용자가 올려 둔 값을 '기본값'으로 되돌릴 이유는 없다
            if (lowvram && s.PreprocResolution > 1024) ch.Add(new("preproc-res", "선화 추출 해상도", s.PreprocResolution.ToString(), "1024", tier, x => x.PreprocResolution = 1024));
            if (lowvram && s.RegenPreprocResolution > 1024) ch.Add(new("regen-res", "재생성 선화 해상도", s.RegenPreprocResolution.ToString(), "1024", tier, x => x.RegenPreprocResolution = 1024));
            var args = s.ComfyExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            bool hasLow = args.Contains("--lowvram") || args.Contains("--novram");
            if (lowvram && !hasLow)
            {
                var to = string.Join(' ', args.Append("--lowvram"));
                ch.Add(new("extra-args", "엔진 인자", args.Count == 0 ? "(없음)" : s.ComfyExtraArgs, to, tier + " — 메모리 절약 모드", x => x.ComfyExtraArgs = to));
            }
            else if (!lowvram && args.Contains("--lowvram"))
            {
                var to = string.Join(' ', args.Where(a => a != "--lowvram"));
                ch.Add(new("extra-args", "엔진 인자", s.ComfyExtraArgs, to.Length == 0 ? "(없음)" : to, tier + " — 절약 모드 불필요", x => x.ComfyExtraArgs = to));
            }
        }

        // 엔진 상주: GPU를 못 쓰면 켤 이유가 없고, RAM이 16GB 미만이면 ComfyUI(실측 최대 13GB)를 평소엔 꺼 두는 게 낫다
        string? warmWhy = !gpuUsable ? (vendor == "none" ? "전용 그래픽카드가 없어요" : "엔진 빌드가 이 그래픽카드를 지원하지 않아요")
                        : ram > 0 && ram < 15.5 ? $"RAM {ram:0}GB — 엔진이 메모리를 10GB 넘게 써서 평소엔 꺼 두는 게 나아요" : null;
        if (warmWhy != null)
        {
            if (s.PrewarmComfyOnLaunch) ch.Add(new("prewarm-off", "엔진 미리 켜기", "켬", "끔", warmWhy, x => x.PrewarmComfyOnLaunch = false));
            if (s.KeepComfyWarm) ch.Add(new("keepwarm-off", "엔진 꺼두지 않기", "켬", "끔", warmWhy, x => x.KeepComfyWarm = false));
        }
        return new Plan(vendor, name, vram, ram, ch);
    }

    /// <summary>Build가 낸 계획을 설정에 적용한다 (저장은 호출자가).</summary>
    public static void Apply(AppSettings s, Plan p)
    {
        foreach (var c in p.Changes) c.Apply(s);
    }
}
