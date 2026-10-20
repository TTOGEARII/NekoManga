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

    // ── 검열제거 (검열 감지 + LaMa) ────────────────────
    /// <summary>"Basic"=내장 LaMa 만화판 (빠름, 추가 설치 불필요) / "ComfyLama"=ComfyUI LayerUtility LaMa (GPU, 원본 레시피).
    /// 옛 "Strong"(SDXL+LoRA 재생성)과 "DeepCream"(DeepCreamPy PConv)은 2026-10-19 폐기 — MigrateLegacy가 Basic으로 바꾼다.
    /// 검열 **감지**는 모드와 무관하게 CensorNet/규칙 감지기가 맡는다.</summary>
    public string DecensorMode { get; set; } = "Basic";
    /// <summary>무엇을 검열로 볼지 — "Auto"(AI) / "BlackBar"(검은 막대·먹줄) / "WhiteBar"(흰 막대) /
    /// "Mosaic"(격자) / "Color"(색 막대·틴트 모자이크 — 채도 기반).
    /// 한 번에 하나만 추적한다: 여러 종류를 동시에 켜면 서로의 오탐이 섞여 정확도가 떨어진다
    /// (실측: 흰 경로를 함께 켜자 오탐 6600px이 검은 막대 결과까지 오염시켰다).
    /// 원고의 검열 방식에 맞는 것 하나를 고르는 쪽이 늘 깨끗하다.
    /// "Color"는 AI(흑/백/모자이크 합성 학습)가 아예 못 보는 **색 검열**(Benantoka 파란 막대·
    /// mashiro 빨간 막대·Sumiyao 틴트 모자이크 등)용 수동 모드다 — 흑백 원고의 채도 덩어리를 찾는다.
    /// 채도만으로는 체크무늬 색 천과 구분이 안 돼(실측 오탐) Auto엔 넣지 않고 수동으로만 둔다.</summary>
    public string DecensorTarget { get; set; } = "BlackBar";
    /// <summary>막대로 인정할 최소 가로세로비. 낮출수록 정사각형에 가까운 것도 잡지만 오탐이 는다.</summary>
    public double DecensorBarMinAspect { get; set; } = 1.8;
    /// <summary>막대로 인정할 최소 면적(px²). 작은 글자·점 노이즈를 거른다.
    /// 600은 실제 원고의 짧은 막대(13×40 정도, 약 530px²)를 놓쳐 350으로 낮췄다.</summary>
    public int DecensorBarMinArea { get; set; } = 350;

    /// <summary>채색에 쓰는 SDXL 체크포인트 파일명 (models/checkpoints 안).
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

    /// <summary>모자이크 블록 크기(px). 이미지에 맞게 조절.</summary>
    public int DecensorBlockSize { get; set; } = 8;
    /// <summary>감지 민감도. 낮을수록 더 많은 영역을 모자이크로 판단.</summary>
    public int DecensorThreshold { get; set; } = 15;
    /// <summary>감지된 마스크 확장(px).</summary>
    public int DecensorMaskGrow { get; set; } = 8;
    /// <summary>흰 검출의 말풍선 필터(글자 블록과 겹치는 흰 성분 삭제). 2026-10-20 실측: 글자 감지기가 흰 검열 막대를 글자로 잡아 진짜 흰선을 지웠다
    /// (Confusion 실라벨 78개 중 겹침 15% 기준 11개·60% 기준 6개 손실, 대신 라벨 밖 발화 36% 감소). 기본 끔 — 흰 말풍선이 자꾸 지워지는 원고에서만 켠다.</summary>
    public bool DecensorBubbleFilter { get; set; } = false;
    /// <summary>검열 제거 전에 감지된 마스크를 사용자가 확인·추가·삭제하는 검토 창을 띄운다 (2026-10-20 사용자 요청: 감지가 놓치는 검열을 직접 칠하고 제거).
    /// 흐름: 모든 페이지 번역+감지 → 검토 창(페이지 넘기며 브러시·다각형으로 마스크 편집) → 제거+채색+식자. 헤드리스(--run)에서는 무시.</summary>
    public bool DecensorReview { get; set; } = true;
    /// <summary>검토 창에서 고친 마스크를 학습 재료로 모은다 (2026-10-20 사용자 요청 "검열 추적이 자동으로 향상되게"): 번역 뒤 원고 + 자동 감지 마스크 + 최종 마스크를
    /// %AppData%\NekoManga\training\corrections\&lt;날짜&gt;\ 에 저장하고 index.jsonl에 한 줄씩 적는다. 전부 로컬 파일이며 어디로도 보내지 않는다.
    /// 학습 자체는 D:\NekoManga-train 의 파이프라인(또는 추후 앱 내 학습)이 이 폴더를 읽어 한다.</summary>
    public bool CollectCorrections { get; set; } = true;
    /// <summary>자가 학습 기능(검토 재료로 감지 모델 미세조정 → 전/후 비교 → 사용자가 적용) 켜기. 끄면 설정 창의 학습 구역이 숨고 아무것도 돌지 않는다.</summary>
    public bool EnableSelfTraining { get; set; } = false;
    public int SelfTrainEpochs { get; set; } = 5;
    public double SelfTrainLr { get; set; } = 0.0002;
    /// <summary>앞에서부터 동결할 층 수(10 = YOLOv8 백본 전부). 재료가 적을 때 망각을 줄인다.</summary>
    public int SelfTrainFreeze { get; set; } = 10;
    public int SelfTrainMinPages { get; set; } = 20;

    // ── 고급 옵션 (설정 창의 [고급 옵션] — 기본값 = 기존 하드코딩, 워크플로를 아는 사용자용) ──
    // (강력(SDXL) 워크플로 설정 DecensorStrong*/DecensorLora*/DecensorCn*/DecensorSeed/DecensorCompositeGrow는
    //  2026-10-19 폐기 — 옛 settings.json에 남은 값은 역직렬화에서 무시된다.)
    /// <summary>엔진 LaMa(원본 레시피) 워크플로의 마스크 확장/블러.</summary>
    public int LamaMaskGrow { get; set; } = 8;
    public int LamaMaskBlur { get; set; } = 8;

    // ── 재생성(ComfyRegen) — 사용자가 ComfyUI에서 직접 짠 "manga_restore_masked_uncensor_v2" 워크플로(2026-10-20)를 그대로 이식 ──
    //   LaMa 1차 복원(마스크 확장 4·블러 4) → Manga2Anime 선화 전처리(1472) → SDXL 체크포인트 + ControlNet(0.85, 0~0.85)
    //   → KSampler(24, cfg 5, dpmpp_sde_gpu/karras, denoise 0.62) → 엣지 마스크(확장 12·블러 8)·코어 마스크(확장 4·블러 1)로 LaMa 결과 위에 합성.
    //   체크포인트·ControlNet·프롬프트는 사용자의 워크플로 값이 기본이며 전부 설정에서 바꿀 수 있다 (파일은 ComfyUI models/ 폴더에 직접 넣는다).
    /// <summary>사용자 워크플로 원본은 waiNSFWIllustrious_v140 — 이 PC엔 없어 사용자 결정(10-20)으로 채색과 같은 WAI v170을 기본으로 쓴다(같은 Illustrious 계열, 그래프 동일).</summary>
    public string RegenCheckpoint { get; set; } = "waiIllustriousSDXL_v170.safetensors";
    /// <summary>사용자 워크플로 원본은 diffusion_pytorch_model_promax(xinsir union promax) — 앱 설치본과 같은 모델이라 사용자 결정(10-20)으로 설치된 이름을 기본으로 쓴다.</summary>
    public string RegenControlNet { get; set; } = "controlnet-union-sdxl-promax.safetensors";
    public string RegenPrompt { get; set; } = "masterpiece, monochrome manga, clean lineart, consistent screentone, preserve original composition, preserve original character design, preserve original grayscale tone, seamless restoration, no sticker effect, no residual censorship marks";
    /// <summary>사용자 워크플로의 "신체부위 복원 태그 (직접 수정)" 노드 — 기본 프롬프트 뒤에 ", "로 붙는다.</summary>
    public string RegenBodyTags { get; set; } = "penis, glains, clitoris, pussy, vulva, cum in pussy";
    public string RegenNegative { get; set; } = "text, letters, sound effects, symbols, watermark, speech bubble text, black bar censor, bar censor, mosaic censoring, censorship, broken lines, extra limbs, blurry lineart, colorized, oversmoothed, residual artifacts";
    public int RegenSteps { get; set; } = 24;
    public double RegenCfg { get; set; } = 5.0;
    public string RegenSampler { get; set; } = "dpmpp_sde_gpu";
    public string RegenScheduler { get; set; } = "karras";
    public double RegenDenoise { get; set; } = 0.62;
    /// <summary>-1이면 매번 무작위(사용자 워크플로의 randomize).</summary>
    public long RegenSeed { get; set; } = -1;
    public double RegenCnStrength { get; set; } = 0.85;
    public double RegenCnEnd { get; set; } = 0.85;
    public int RegenPreprocResolution { get; set; } = 1472;
    public int RegenLamaGrow { get; set; } = 4;
    public int RegenLamaBlur { get; set; } = 4;
    public int RegenEdgeGrow { get; set; } = 12;
    public int RegenEdgeBlur { get; set; } = 8;
    public int RegenCoreGrow { get; set; } = 4;
    public int RegenCoreBlur { get; set; } = 1;
    /// <summary>0이면 원본 해상도 그대로(사용자 워크플로). 큰 페이지에서 VRAM이 모자라면 1.5 같은 값으로 줄여 보낸다(결과는 원본 크기로 되돌림).</summary>
    public double RegenMegapixels { get; set; } = 0;

    // ── 커스텀 워크플로(ComfyCustom) — ComfyUI 화면에서 직접 짠 API 포맷 JSON을 그대로 돌린다 (2026-10-20, CustomWorkflow.cs 규약) ──
    /// <summary>검열제거용 커스텀 워크플로(API JSON) 경로. DecensorMode="ComfyCustom"일 때 쓴다.</summary>
    public string CustomDecensorWorkflow { get; set; } = "";
    /// <summary>커스텀 검열제거 결과를 원본에 되돌릴 범위: 마스크를 이 px만큼 넓힌 안쪽만 받는다. 0이면 결과 페이지 전체를 받는다.</summary>
    public int CustomDecensorReach { get; set; } = 24;
    /// <summary>채색용 커스텀 워크플로(API JSON) 경로. 비어 있지 않고 파일이 있으면 내장 채색 워크플로 대신 쓴다.</summary>
    public string ColorCustomWorkflow { get; set; } = "";
    /// <summary>엔진 추가 실행 인자 (예: --lowvram). 엔진을 껐다 켜면 적용.</summary>
    public string ComfyExtraArgs { get; set; } = "";
    /// <summary>채색 ControlNet 1단(선화 lineart) 강도와 적용 구간 끝(0~1).</summary>
    public double CnLineartStrength { get; set; } = 1.0;
    public double CnLineartEnd { get; set; } = 0.6;
    /// <summary>채색 ControlNet 2단(스케치 scribble) 강도와 적용 구간 시작(0~1).</summary>
    public double CnScribbleStrength { get; set; } = 1.0;
    public double CnScribbleStart { get; set; } = 0.6;
    /// <summary>선화/스케치 전처리 해상도.</summary>
    public int PreprocResolution { get; set; } = 1280;
    /// <summary>Scribble XDoG 전처리 임계값.</summary>
    public int ScribbleXdogThreshold { get; set; } = 32;
    /// <summary>IPAdapter(표지 주입) 적용 끝 지점(0~1). 높을수록 끝까지 표지 색이 유지된다.</summary>
    public double IpaEndAt { get; set; } = 0.9;

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
    /// <summary>식자 기본 정렬 — "Left"/"Center"/"Right". 블록별 지정이 없을 때 쓰인다.</summary>
    public string LetterAlign { get; set; } = "Center";
    /// <summary>식자 기본 기울임(이탤릭).</summary>
    public bool LetterItalic { get; set; } = false;
    /// <summary>식자 기본 줄간격 배율.</summary>
    public double LetterLineSpacing { get; set; } = 1.0;

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

    /// <summary>설정 파일 경로. 환경변수 NEKO_SETTINGS로 바꿀 수 있다 — 회귀 하네스(D:\NekoManga-train\bench\dc_batch.py)가
    /// 사용자 설정을 건드리지 않고 중립 프롬프트·고정 시드로 돌리기 위한 것. 없으면 %AppData%\NekoManga\settings.json.</summary>
    private static string SettingsPath =>
        Environment.GetEnvironmentVariable("NEKO_SETTINGS") is { Length: > 0 } ov ? ov : Path.Combine(DataDir, "settings.json");

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
        // 옛 '강력'(SDXL+LoRA)·DeepCream 모드는 2026-10-19 폐기 — 둘 다 기본(내장 LaMa)으로 돌린다
        if (DecensorMode is not ("Basic" or "ComfyLama" or "ComfyRegen" or "ComfyCustom")) DecensorMode = "Basic";
        CustomDecensorReach = Math.Clamp(CustomDecensorReach, 0, 512);
        SelfTrainEpochs = Math.Clamp(SelfTrainEpochs, 1, 50); SelfTrainLr = Math.Clamp(SelfTrainLr, 0.00001, 0.01);
        SelfTrainFreeze = Math.Clamp(SelfTrainFreeze, 0, 22); SelfTrainMinPages = Math.Clamp(SelfTrainMinPages, 5, 500);

        // 고급 옵션 안전망 — 손으로 json을 고치다 깨진 값이 워크플로를 터뜨리지 않게
        LamaMaskGrow = Math.Clamp(LamaMaskGrow, 0, 64);
        LamaMaskBlur = Math.Clamp(LamaMaskBlur, 0, 64);
        RegenSteps = Math.Clamp(RegenSteps, 1, 150);
        RegenCfg = Math.Clamp(RegenCfg, 0, 30);
        RegenDenoise = Math.Clamp(RegenDenoise, 0.05, 1.0);
        RegenCnStrength = Math.Clamp(RegenCnStrength, 0, 2);
        RegenCnEnd = Math.Clamp(RegenCnEnd, 0, 1);
        RegenPreprocResolution = Math.Clamp(RegenPreprocResolution, 256, 4096);
        RegenLamaGrow = Math.Clamp(RegenLamaGrow, 0, 64); RegenLamaBlur = Math.Clamp(RegenLamaBlur, 0, 64);
        RegenEdgeGrow = Math.Clamp(RegenEdgeGrow, 0, 128); RegenEdgeBlur = Math.Clamp(RegenEdgeBlur, 0, 64);
        RegenCoreGrow = Math.Clamp(RegenCoreGrow, 0, 64); RegenCoreBlur = Math.Clamp(RegenCoreBlur, 0, 64);
        RegenMegapixels = Math.Clamp(RegenMegapixels, 0, 8);
        if (string.IsNullOrWhiteSpace(RegenSampler)) RegenSampler = "dpmpp_sde_gpu";
        if (string.IsNullOrWhiteSpace(RegenScheduler)) RegenScheduler = "karras";
        CnLineartStrength = Math.Clamp(CnLineartStrength, 0, 2);
        CnLineartEnd = Math.Clamp(CnLineartEnd, 0, 1);
        CnScribbleStrength = Math.Clamp(CnScribbleStrength, 0, 2);
        CnScribbleStart = Math.Clamp(CnScribbleStart, 0, 1);
        PreprocResolution = Math.Clamp(PreprocResolution, 256, 4096);
        ScribbleXdogThreshold = Math.Clamp(ScribbleXdogThreshold, 1, 128);
        IpaEndAt = Math.Clamp(IpaEndAt, 0, 1);
        if (LetterAlign is not ("Left" or "Center" or "Right")) LetterAlign = "Center";
        LetterLineSpacing = Math.Clamp(LetterLineSpacing, 0.6, 3.0);
    }

    public void Save()
    {
        // NEKO_SETTINGS가 디렉터리 없는 상대 파일명이면 GetDirectoryName이 ""를 돌려줘 CreateDirectory("")가 던진다 → 전체 경로로 푼 뒤 만든다
        var dir = Path.GetDirectoryName(Path.GetFullPath(SettingsPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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

    /// <summary>ComfyUI가 사용 가능한가 (채색·엔진 LaMa 검열제거의 전제조건).</summary>
    [JsonIgnore]
    public bool ComfyAvailable =>
        File.Exists(Path.Combine(ComfyRoot, "ComfyUI", "main.py")) &&
        File.Exists(Path.Combine(ComfyRoot, "python_embeded", "python.exe"));
}
