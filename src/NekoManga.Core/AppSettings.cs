using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoManga.Core;

/// <summary>앱 전체 설정. %AppData%\NekoManga\settings.json 에 저장된다.</summary>
public class AppSettings
{
    // ── 기능 토글 ──────────────────────────────────────────────
    public bool EnableAutoColor { get; set; } = true;
    public bool EnableDecensor { get; set; } = false;
    public bool EnableTranslate { get; set; } = true;

    // ── 경로 ──────────────────────────────────────────────────
    /// <summary>ComfyUI·BallonsTranslator·모델이 설치되는 루트. 용량이 커서(약 15GB) 여유 있는 드라이브 권장.</summary>
    public string RuntimeRoot { get; set; } = DefaultRuntimeRoot();
    /// <summary>결과물 저장 폴더. 비워두면 입력 파일 옆에 "이름_neko" 폴더를 만든다.</summary>
    public string OutputRoot { get; set; } = "";
    /// <summary>이미 설치된 ComfyUI portable 루트를 연결해 재다운로드를 피한다 (예: D:\ComfyUI\ComfyUI_windows_portable).</summary>
    public string LinkedComfyRoot { get; set; } = "";
    /// <summary>이미 설치된 BallonsTranslator 루트를 연결한다 (예: 바탕화면\Ballonstranslator_win_minium).</summary>
    public string LinkedBallonsRoot { get; set; } = "";

    // ── 자동채색 (ComfyUI) ─────────────────────────────────────
    /// <summary>리사이즈 상한 (메가픽셀). 8GB VRAM 기준 1.5가 안전, 원본 레시피는 2.3.</summary>
    public double ColorMegapixels { get; set; } = 1.5;
    public int ColorSteps { get; set; } = 25;
    public double ColorCfg { get; set; } = 5.0;
    /// <summary>img2img 강도. 높을수록 원고 명암을 참고해 새로 그린다.</summary>
    public double ColorDenoise { get; set; } = 0.90;
    public string ColorSampler { get; set; } = "dpmpp_sde_gpu"; // 품질 우선. 속도 우선은 dpmpp_2m
    public string ColorScheduler { get; set; } = "karras";
    /// <summary>캐릭터 머리색·눈색 등 수동 태그. 페이지 공통으로 프롬프트 앞에 붙는다.</summary>
    public string ColorManualTags { get; set; } = "";
    public string ColorBaseTags { get; set; } = "masterpiece, best quality, full color, colored";
    public string ColorExcludeTags { get; set; } = "monochrome, greyscale, censored";
    public string ColorNegative { get; set; } =
        "lowres, worst quality, bad anatomy, bad hands, monochrome, greyscale, text, watermark, signature";
    /// <summary>선화 합성(luminosity) 불투명도 %. 0이면 합성 안 함.</summary>
    public int ColorLineartOpacity { get; set; } = 20;
    /// <summary>-1이면 매번 랜덤.</summary>
    public long ColorSeed { get; set; } = -1;
    public string ColorTaggerModel { get; set; } = "wd-eva02-large-tagger-v3";
    public double ColorTaggerThreshold { get; set; } = 0.35;

    // ── 검열제거 (모자이크 감지 + LaMa) ─────────────────────────
    /// <summary>모자이크 블록 크기(px). 이미지에 맞게 조절.</summary>
    public int DecensorBlockSize { get; set; } = 8;
    /// <summary>감지 민감도. 낮을수록 더 많은 영역을 모자이크로 판단.</summary>
    public int DecensorThreshold { get; set; } = 15;
    /// <summary>감지된 마스크 확장(px).</summary>
    public int DecensorMaskGrow { get; set; } = 8;

    // ── 번역 (BallonsTranslator) ───────────────────────────────
    public string TranslateEngine { get; set; } = "Papago";
    public string TranslateSource { get; set; } = "日本語";
    public string TranslateTarget { get; set; } = "한국어";

    // ── 기타 ──────────────────────────────────────────────────
    /// <summary>civitai 모델(채색 체크포인트) 다운로드용 API 키. 로그인 필요 모델 대비 (선택).</summary>
    public string CivitaiApiKey { get; set; } = "";
    public int ComfyPort { get; set; } = 8188;
    /// <summary>작업 후 ComfyUI 서버를 유지해 다음 작업을 빠르게 시작한다.</summary>
    public bool KeepComfyWarm { get; set; } = true;

    // ── 저장/로드 ──────────────────────────────────────────────
    [JsonIgnore]
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoManga");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new();
        }
        catch { /* 손상된 설정은 기본값으로 */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    private static string DefaultRuntimeRoot()
    {
        // 모델이 10GB가 넘으므로 여유가 가장 큰 고정 드라이브를 기본값으로 고른다.
        try
        {
            var best = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            if (best != null)
                return Path.Combine(best.RootDirectory.FullName, "NekoManga-runtime");
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "NekoManga", "runtime");
    }

    /// <summary>실제 사용할 ComfyUI portable 루트 (연결된 기존 설치 우선).</summary>
    [JsonIgnore]
    public string ComfyRoot =>
        !string.IsNullOrWhiteSpace(LinkedComfyRoot) ? LinkedComfyRoot
        : Path.Combine(RuntimeRoot, "ComfyUI_windows_portable");

    /// <summary>실제 사용할 BallonsTranslator 루트 (연결된 기존 설치 우선).</summary>
    [JsonIgnore]
    public string BallonsRoot =>
        !string.IsNullOrWhiteSpace(LinkedBallonsRoot) ? LinkedBallonsRoot
        : Path.Combine(RuntimeRoot, "BallonsTranslator");
}
