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

    // ── 자동채색 (ComfyUI) ─────────────────────────────────────
    /// <summary>리사이즈 상한 (메가픽셀). 8GB VRAM 기준 1.5가 안전, 원본 레시피는 2.3.</summary>
    public double ColorMegapixels { get; set; } = 1.5;
    public int ColorSteps { get; set; } = 25;
    public double ColorCfg { get; set; } = 5.5;   // 원본 워크플로 값
    /// <summary>img2img 강도. 높을수록 원고 명암을 참고해 새로 그린다.</summary>
    public double ColorDenoise { get; set; } = 0.90;
    public string ColorSampler { get; set; } = "dpmpp_sde_gpu"; // 품질 우선. 속도 우선은 dpmpp_2m
    public string ColorScheduler { get; set; } = "karras";
    /// <summary>캐릭터 머리색·눈색 등 수동 태그. 페이지 공통으로 프롬프트 앞에 붙는다.</summary>
    public string ColorManualTags { get; set; } = "";
    public string ColorBaseTags { get; set; } = "masterpiece, best quality, full color, colored";
    public string ColorExcludeTags { get; set; } = "monochrome, greyscale, censored, yuri, multiple girls, 2girls";   // 원본 워크플로 값
    public string ColorNegative { get; set; } =
        "lowres, worst quality, bad anatomy, bad hands, monochrome, greyscale, text, watermark, signature";
    /// <summary>선화 합성(luminosity) 불투명도 %. 0이면 합성 안 함.</summary>
    public int ColorLineartOpacity { get; set; } = 20;
    /// <summary>-1이면 매번 랜덤.</summary>
    public long ColorSeed { get; set; } = -1;
    public string ColorTaggerModel { get; set; } = "wd-eva02-large-tagger-v3";
    public double ColorTaggerThreshold { get; set; } = 0.35;

    // ── 검열제거 (모자이크 감지 + LaMa/SDXL) ────────────────────
    /// <summary>"Basic"=내장 LaMa (빠름, 추가 설치 불필요), "Strong"=ComfyUI SDXL 인페인트 (고품질, ComfyUI 필요).</summary>
    public string DecensorMode { get; set; } = "Basic";
    /// <summary>무엇을 검열로 볼지 — "BlackBar"(검은 막대·먹줄) / "WhiteBar"(흰 막대) / "Mosaic"(격자).
    /// 한 번에 하나만 추적한다: 여러 종류를 동시에 켜면 서로의 오탐이 섞여 정확도가 떨어진다
    /// (실측: 흰 경로를 함께 켜자 오탐 6600px이 검은 막대 결과까지 오염시켰다).
    /// 원고의 검열 방식에 맞는 것 하나를 고르는 쪽이 늘 깨끗하다.</summary>
    public string DecensorTarget { get; set; } = "BlackBar";
    /// <summary>막대로 인정할 최소 가로세로비. 낮출수록 정사각형에 가까운 것도 잡지만 오탐이 는다.</summary>
    public double DecensorBarMinAspect { get; set; } = 1.8;
    /// <summary>막대로 인정할 최소 면적(px²). 작은 글자·점 노이즈를 거른다.
    /// 600은 실제 원고의 짧은 막대(13×40 정도, 약 530px²)를 놓쳐 350으로 낮췄다.</summary>
    public int DecensorBarMinArea { get; set; } = 350;

    /// <summary>채색·강력검열에 쓰는 SDXL 체크포인트 파일명 (models/checkpoints 안).
    /// 상위 모델이 나오면 엔진 탭에서 바꿔 끼울 수 있다.</summary>
    public string ColorCheckpoint { get; set; } = "waiIllustriousSDXL_v170.safetensors";
    /// <summary>ControlNet 모델 파일명 (models/controlnet 안).</summary>
    public string ColorControlNet { get; set; } = "controlnet-union-sdxl-promax.safetensors";
    /// <summary>앱을 켜면 채색 엔진(ComfyUI)을 미리 백그라운드로 켜 둔다 —
    /// 첫 채색에서 1~2분 기다리는 일이 없어진다.</summary>
    public bool PrewarmComfyOnLaunch { get; set; } = true;

    /// <summary>컬러 표지(또는 캐릭터 컬러 일러스트) 경로. 메인 캐릭터의 머리·눈·의상 색을
    /// 여기서 뽑아 채색 프롬프트에 넣는다 — 페이지마다 캐릭터 색이 일관돼진다.</summary>
    public string ColorRefImage { get; set; } = "";
    /// <summary>표지에서 뽑은 색 태그 (자동 추출 후 캐시, 직접 수정 가능). 비어 있으면 채색 시작 때
    /// ColorRefImage가 있으면 자동으로 한 번 뽑는다.</summary>
    public string ColorRefTags { get; set; } = "";
    /// <summary>IPAdapter로 표지를 반영하는 강도 (0~1.5). 높을수록 표지 색·스타일을 강하게 따른다.
    /// IPAdapter 모델이 설치돼 있을 때만 쓰인다 (없으면 태그만으로 동작).</summary>
    public double ColorRefStrength { get; set; } = 0.5;

    /// <summary>강력(SDXL) 검열제거의 시드. 고정해야 같은 원고를 다시 돌려도 같은 그림이 나온다
    /// (-1이면 매번 무작위 — 실행할 때마다 결과가 달라진다). 기본값은 실제 원고에서 결과가 좋았던 시드.</summary>
    public long DecensorSeed { get; set; } = 3080993336;

    /// <summary>모자이크 블록 크기(px). 이미지에 맞게 조절.</summary>
    public int DecensorBlockSize { get; set; } = 8;
    /// <summary>감지 민감도. 낮을수록 더 많은 영역을 모자이크로 판단.</summary>
    public int DecensorThreshold { get; set; } = 15;
    /// <summary>감지된 마스크 확장(px).</summary>
    public int DecensorMaskGrow { get; set; } = 8;
    /// <summary>강력 모드 재생성 강도 (denoise).</summary>
    public double DecensorStrongDenoise { get; set; } = 0.75;

    // ── 번역 (내장 엔진: CTD + manga-ocr + LaMa) ────────────────
    /// <summary>"Google"(무료, 기본) · "DeepL" · "DeepSeek" · "Grok" (뒤 셋은 API 키 필요).</summary>
    public string TranslateEngine { get; set; } = "Google";
    /// <summary>언어 코드. OCR이 일본어 특화라 원본은 ja 고정 권장.</summary>
    public string TranslateSource { get; set; } = "ja";
    public string TranslateTarget { get; set; } = "ko";
    public string DeepLApiKey { get; set; } = "";
    public string DeepSeekApiKey { get; set; } = "";
    /// <summary>비우면 기본 모델(deepseek-chat).</summary>
    public string DeepSeekModel { get; set; } = "";
    public string GrokApiKey { get; set; } = "";
    /// <summary>비우면 기본 모델(grok-4-fast).</summary>
    public string GrokModel { get; set; } = "";

    // ── 식자 (말풍선에 그리는 번역문 — BT의 lettering 설정에서 핵심만) ──
    /// <summary>글꼴 이름. 설치 안 된 이름이면 GDI+가 기본 글꼴로 대체한다.</summary>
    public string LetterFontFamily { get; set; } = "Malgun Gothic";
    /// <summary>굵게.</summary>
    public bool LetterBold { get; set; } = true;
    /// <summary>글자 크기 배율. OCR이 추정한 원문 크기에 곱한다 (1.0 = 원문 크기 그대로).</summary>
    public double LetterSizeScale { get; set; } = 1.0;
    /// <summary>글자 색 "#RRGGBB". 비우면 자동 — 원문 글자 색을 따라간다.</summary>
    public string LetterColorHex { get; set; } = "";
    /// <summary>외곽선(halo) 색. 어떤 배경에서도 읽히게 글자 둘레에 두른다.</summary>
    public string LetterOutlineColorHex { get; set; } = "#FFFFFF";
    /// <summary>외곽선 두께 배율. 0이면 외곽선 없음 (1.0 = 글자 크기의 1/7 정도).</summary>
    public double LetterOutlineScale { get; set; } = 1.0;

    /// <summary>UI 테마 — "Light"(기본, 만화 원고풍 흑백) / "Dark"(반전).</summary>
    public string Theme { get; set; } = "Light";

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
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new();
                s.MigrateLegacy();
                return s;
            }
        }
        catch { /* 손상된 설정은 기본값으로 */ }
        return new AppSettings();
    }

    /// <summary>v0.1.x(BallonsTranslator 연동 시절) 설정값을 내장 엔진 형식으로 변환한다.</summary>
    private void MigrateLegacy()
    {
        TranslateSource = TranslateSource switch { "日本語" => "ja", "English" => "en", "简体中文" => "zh", _ => TranslateSource };
        TranslateTarget = TranslateTarget switch { "한국어" => "ko", "English" => "en", _ => TranslateTarget };
        // Papago 등 구 BT 엔진만 Google로 되돌린다 (DeepSeek·Grok은 유효).
        if (TranslateEngine is not ("Google" or "DeepL" or "DeepSeek" or "Grok")) TranslateEngine = "Google";
        if (DecensorMode is not ("Basic" or "ComfyLama" or "Strong")) DecensorMode = "Basic";
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

    /// <summary>ComfyUI가 사용 가능한가 (채색·강력 검열제거의 전제조건).</summary>
    [JsonIgnore]
    public bool ComfyAvailable =>
        File.Exists(Path.Combine(ComfyRoot, "ComfyUI", "main.py")) &&
        File.Exists(Path.Combine(ComfyRoot, "python_embeded", "python.exe"));
}
