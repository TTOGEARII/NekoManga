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
    /// <summary>말풍선 밖의 효과음은 번역하지 않는다(2026-11-18 사용자 "번역은 말풍선 안에 있는 것만") — 풍선 밖으로 보이고(<see cref="Engine.BubbleSolver.IsInBubble"/>)
    /// 효과음처럼 생긴(<see cref="Engine.EmbeddedTranslator.LooksLikeSfx"/>) 글은 번역·지움·식자하지 않고 그대로 둔다. 그런 글은 번역기가 폭주하기 쉽고 식자해도 그림을 가린다.
    /// 풍선 판정만으로 거르지 않는다 — 실제 원고에서 테두리가 닫히지 않는 풍선이 많아 대사가 빠진다(notes/translate-letter.md).</summary>
    public bool TranslateBubbleOnly { get; set; } = true;
    /// <summary>풍선 밖 효과음을 어떻게 할까(2026-11-26, CarrotMangaTranslator 참고 — **시험 중**): "Keep"(기본 — 전처럼 그대로 둔다) | "Replace"(LaMa로 지우고 번역으로 덮는다:
    /// 용어집 → 내장 의성어 사전 → 번역기, 굵게, 높이가 폭의 1.5배를 넘으면 글자를 세로로 쌓는다 — TextBlockInfo.Sfx). <see cref="TranslateBubbleOnly"/>가 꺼져 있으면 효과음을 가르지 않으니 뜻이 없다.</summary>
    public string SfxMode { get; set; } = "Keep";
    /// <summary>번역 용어집(2026-11-26) — 한 줄에 하나 "원문=번역"(탭으로 갈라도 된다, #은 주석). DeepSeek·Grok에는 지시문으로, Google·DeepL에는 원문의 용어를 번역어로 바꿔 보낸다. 효과음에도 먼저 쓴다.</summary>
    public string TranslateGlossary { get; set; } = "";
    /// <summary>앞 쪽의 대사(원문→번역, 최근 30줄)를 DeepSeek·Grok에 문맥으로 같이 보낸다(2026-11-26) — 인물의 말투·호칭이 쪽 사이에 흔들리지 않게. Google·DeepL은 쓰지 않는다.</summary>
    public bool TranslateCarryContext { get; set; } = true;

    // ── 경로 ──────────────────────────────────────────────────
    /// <summary>ComfyUI·BallonsTranslator·모델이 설치되는 루트. 용량이 커서(약 15GB) 여유 있는 드라이브 권장.</summary>
    public string RuntimeRoot { get; set; } = DefaultRuntimeRoot();
    /// <summary>결과물 저장 폴더. 비워두면 입력 파일 옆에 "이름_neko" 폴더를 만든다.</summary>
    public string OutputRoot { get; set; } = "";
    /// <summary>이미 설치된 ComfyUI portable 루트를 연결해 재다운로드를 피한다 (예: D:\ComfyUI\ComfyUI_windows_portable).</summary>
    public string LinkedComfyRoot { get; set; } = "";
    /// <summary>채색에만 쓰는 구성요소(태거·IPAdapter 등 약 5GB)도 받는다 — 준비 창의 '채색도 설치'. 끄면 번역·검열 제거(기본·재생성)에 드는 것만 받는다
    /// (2026-11-18 사용자 결정: 재생성까지는 늘 받고 채색은 고른다).</summary>
    public bool InstallColor { get; set; } = true;

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

    // ── 검열제거 (검열 감지 + 복원) ────────────────────
    /// <summary>"Basic"=내장 엔진 (빠름, 추가 설치 불필요 — 2026-11-09부터 Aletheia Lens의 DeepCreamPy 막대/모자이크 모델,
    /// 기존 방식으로 찾은 흰선만 LaMa 만화판) / "ComfyRegen"(SDXL 영역 재생성) / "ComfyCustom"(사용자 워크플로).
    /// 옛 "Strong"(SDXL+LoRA 재생성)과 "DeepCream"(DeepCreamPy PConv)은 2026-10-19, "ComfyLama"(ComfyUI LayerUtility LaMa)는 2026-11-12 폐기 — MigrateLegacy가 Basic으로 바꾼다.
    /// 검열 **감지**는 모드와 무관하게 Aletheia Lens(Mask R-CNN) + 전용 AI(CensorNet) 보완이 맡는다 — 옛 "DecensorEngine"(Aletheia/Legacy 선택)은 2026-11-12 삭제, json의 키는 무시된다.</summary>
    public string DecensorMode { get; set; } = "Basic";
    /// <summary>Aletheia 엔진의 자동 감지(Auto)에서 기존 CensorNet이 보완할 종류 (settings.json 전용).
    /// "White"(기본 — 사용자 지시 "Aletheia가 못 잡는 흰 막대선은 기존 방식으로") / "WhiteMosaic" / "All" / "None".
    /// 2026-11-09 실측(사용자 덧칠 라벨, 적중 = 라벨의 30% 이상 덮음; Aletheia / CensorNet c11 / 합집합):
    /// 흰선 51·72·75 /78, 검은선 195·199·200 /200, **모자이크 27·70·70 /89** — 모자이크는 Aletheia가 크게 못 미쳐 "WhiteMosaic"이 그 차이를 메운다.
    /// 보완으로 찾은 자리는 기본 복원에서 기존 방식(LaMa)으로 지운다.</summary>
    public string DecensorLegacyAssist { get; set; } = "White";
    /// <summary>보완(기존 방식)으로 찾은 자리를 기본 복원에서 무엇으로 지울지 — "Aletheia"(기본, Aletheia 막대 모델) / "Lama"(LaMa 만화판).
    /// 기본값은 2026-11-09에 Lama → Aletheia로 바꿨다: 사용자가 결과를 직접 비교해 "LaMa 쪽은 너무 뭉개진다"고 판단
    /// (정답 벤치의 PSNR은 LaMa가 높지만 질감 대비(std비 0.83 vs 0.92)·선명도는 Aletheia가 높다).</summary>
    public string DecensorAssistRepair { get; set; } = "Aletheia";
    /// <summary>내장 AI(글자 감지·OCR·LaMa·검열 감지)를 그래픽카드(DirectML)로 돌릴지 — "Auto"(기본) / "On" / "Off".
    /// Auto는 ComfyUI를 쓰는 작업(자동 채색·GPU 검열 제거·글자 자리 다시 그리기)에서는 CPU, 그 밖에는 GPU (Engine.OnnxEnv.GpuAllowed — 전부 올리면 VRAM ~5GB).
    /// 2026-11-09 전에는 DirectML.dll이 빠져 있어 설정과 무관하게 늘 CPU였다.</summary>
    public string OnnxGpu { get; set; } = "Auto";
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
    /// <summary>ComfyUI 포터블 종류 (2026-10-21 AMD 지원): "auto"(감지) / "nvidia"(RTX 20 이상, CUDA 13) / "nvidia-old"(GTX 10 이하, CUDA 12.6) / "amd"(Radeon RDNA2+, ROCm, Windows 11).
    /// 내장 엔진(번역·검열 감지·기본 복원)은 ONNX DirectML이라 그래픽카드 종류와 무관하다.</summary>
    public string GpuVendor { get; set; } = "auto";
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
    /// <summary>검토 방식 (2026-10-21 사용자 요청). "PerPage" = 감지 0인 페이지는 바로 2단계로, 감지된 페이지만 '검토 대기'로 두고
    /// 결과 창의 '마스크 수정' 탭에서 페이지별로 고친 뒤 진행 (기본). "Window" = 옛 방식 — 모든 페이지 감지 뒤 검토 창 하나로 한꺼번에.</summary>
    public string DecensorReviewMode { get; set; } = "PerPage";
    /// <summary>같은 원고를 다시 돌릴 때 결과 폴더 .neko에 남은 마스크(사용자가 고친 것 포함)를 감지 대신 쓴다 — 고친 마스크가 재실행에도 유지되게.</summary>
    public bool ReuseStoredMasks { get; set; } = true;
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

    // ── 재생성(ComfyRegen) — 사용자가 ComfyUI에서 직접 짠 "manga_restore_masked_uncensor_v2" 워크플로(2026-10-20)를 그대로 이식 ──
    //   LaMa 1차 복원(마스크 확장 4·블러 4) → Manga2Anime 선화 전처리(1472) → SDXL 체크포인트 + ControlNet(0.85, 0~0.85)
    //   → KSampler(24, cfg 5, dpmpp_sde_gpu/karras, denoise 0.62) → 엣지 마스크(확장 12·블러 8)·코어 마스크(확장 4·블러 1)로 LaMa 결과 위에 합성.
    //   체크포인트·ControlNet·프롬프트는 사용자의 워크플로 값이 기본이며 전부 설정에서 바꿀 수 있다 (파일은 ComfyUI models/ 폴더에 직접 넣는다).
    /// <summary>사용자 워크플로 원본은 waiNSFWIllustrious_v140 — 이 PC엔 없어 사용자 결정(10-20)으로 채색과 같은 WAI v170을 기본으로 쓴다(같은 Illustrious 계열, 그래프 동일).</summary>
    public string RegenCheckpoint { get; set; } = "waiIllustriousSDXL_v170.safetensors";
    /// <summary>사용자 워크플로 원본은 diffusion_pytorch_model_promax(xinsir union promax) — 앱 설치본과 같은 모델이라 사용자 결정(10-20)으로 설치된 이름을 기본으로 쓴다.</summary>
    public string RegenControlNet { get; set; } = "controlnet-union-sdxl-promax.safetensors";
    public string RegenPrompt { get; set; } = "masterpiece, monochrome manga, clean lineart, consistent screentone, preserve original composition, preserve original character design, preserve original grayscale tone, seamless restoration, no sticker effect, no residual censorship marks";
    /// <summary>(옛 칸, v1.8.3까지) 한 줄 신체부위 태그 — Load 때 MigrateLegacy가 남성/여성으로 나눠 옮기고 비운다. 코드는 더 읽지 않는다.</summary>
    public string RegenBodyTags { get; set; } = "";
    /// <summary>부위 종류별 태그(재생성 v3, 2026-11-12 사용자: "여자는 잘 나오는데 남자는 이상하다" — 한 줄 태그에 여성 태그가 늘 섞였다).
    /// 한쪽 성별로 확정된 영역에는 그쪽 줄만, 나머지는 두 줄을 같이 넣는다(RegenTags.For, RegenTagsBySex). 기본값은 옛 한 줄을 나눈 것(glains→glans 오타 수정).</summary>
    public string RegenBodyTagsMale { get; set; } = "penis, glans";
    public string RegenBodyTagsFemale { get; set; } = "clitoris, pussy, vulva";
    /// <summary>질내사정 태그(2026-11-16 사용자 "여자거에서 구멍에 전부다 질내사정으로 나오는데 실제 이미지에서 질내사정한 부분만 그렇게 나왔으면 좋겠어").
    /// v1.10.0까지는 여성 태그 줄에 'cum in pussy'가 들어 있어 여성 부위마다 늘 들어갔다 — 떼어 내 **질내사정인 조각이 있는 영역에만** 여성 태그 뒤에 더한다(RegenTags.For).
    /// 질내사정인지는 조각마다: 결과 창에서 직접 고른 것(SexMark.Cum)이 먼저, 없으면 자동 판단(RegenCumAuto). 비우면 어디에도 넣지 않는다.
    /// 이 칸이 없는 settings.json(v1.10.0까지)을 읽으면 MigrateLegacy가 여성 태그 줄에서 'cum in pussy'를 뗀다(한 번만 — 저장하면 칸이 생긴다). 코드는 <see cref="CumTags"/>로 읽는다.</summary>
    public string RegenBodyTagsCum { get; set; } = DefaultCumTags;
    public const string DefaultCumTags = "cum in pussy";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CumTags => (RegenBodyTagsCum ?? "").Trim();
    /// <summary>질내사정의 자동 판단(기본 켬) — 사용자가 표시하지 않은 조각은 그 둘레를 WD 태거(Engine.CumDetector)에 물어 정한다. 모델이 없으면 자동 판단 없이 표시한 조각만.
    /// 끄면 표시한 조각만 사정으로 친다.</summary>
    public bool RegenCumAuto { get; set; } = true;
    /// <summary>자동 판단의 임계 — 태거의 점수(cum_in_pussy·cumdrip·overflow·after_vaginal·internal_cumshot 중 가장 높은 것)가 이 값 이상이면 사정. 0.35는 WD 태거의 통상 임계(settings.json 전용).</summary>
    public double RegenCumThreshold { get; set; } = 0.35;
    /// <summary>한쪽 성별로 **확정된** 영역에는 그쪽 태그만 넣는다(기본 켬, 2026-11-15 사용자 "남자거에 계속 여자거가 그려지는데 검열부분을 보고 남/여 구분을 확실하게").
    /// 확정은 검열 조각마다 성별을 가려서 정한다(RegenRegion.PieceSex — 부위 감지의 근거, 약한 상자도; 결과 창에서 사용자가 고른 것이 있으면 그것) — 티끌 아닌 조각이 전부 같은 한쪽일 때.
    /// 확정이 아닌 영역(삽입처럼 가려진 다른 쪽이 있을 수 있는 곳, 근거 없는 조각이 낀 곳)은 남녀 태그를 같이 넣는다 — 그런 영역을 조각 쪽마다 나눠 넣는 것은 RegenTagsPerPiece.
    /// 끄면 남녀 태그를 늘 같이 넣는다(2026-11-13의 기본). 경위: v1.9.1은 감지한 쪽만 넣어 삽입 장면의 가려진 쪽이 안 그려졌고(시험 4쪽 — 2·4번 남성만, 3번 여성만 잡힘),
    /// 그래서 늘 같이 넣자 남성 부위만 있는 장에 여성 부위가 그려졌다. 2026-11-13~14 빌드의 옛 칸 RegenBodyTagsByKind(감지한 쪽만의 켬/끔)는 더 읽지 않는다.</summary>
    public bool RegenTagsBySex { get; set; } = true;
    /// <summary>자리별 태그(2026-11-15) — 남성 조각과 여성 조각이 한 영역에 같이 있으면 그림을 조각의 성별로 나눠 자리마다 그 성별의 태그만 넣는다(RegenSexAreas, RegenTagsBySex가 켜져 있을 때).
    /// 꺼 두면 그런 영역에는 남녀 태그를 같이 넣는다(전과 같다). 섞인 영역만 자리 수만큼 프롬프트 추론이 늘어 느려진다(두 자리 1.5배쯤).
    /// **기본은 끔** — 그리는 방식이 바뀌는 것이라 사용자가 전/후를 보고 정한 뒤에 켠다.</summary>
    public bool RegenTagsPerPiece { get; set; }
    /// <summary>떨어진 검열 자리는 따로 다시 그린다(2026-11-16 사용자 "남자거 여자거 말고 인체 부위도 제대로 안나오는게 있는데").
    /// 꺼 두면 전과 같다 — 한쪽 영역이 상대 창(최소 RegenCropSize) 안에 보이면 합치는데, 1813px 쪽에서는 떨어진 자리끼리 줄줄이 합쳐져 쪽 전체를 0.4배로 줄여 보내고 검열 밖의 몸이 뭉개졌다.
    /// 켜면 붙이는 범위가 겹칠 때만 합친다(RegenRegion.SplitReachFor) — 영역이 늘어 생성 횟수가 는다(사용자 원고 18쪽에서 28 → 46번).
    /// **기본은 끔** — 그리는 결과가 바뀌는 것이라 사용자가 전/후를 보고 정한 뒤에 켠다.</summary>
    public bool RegenSplitRegions { get; set; }
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
    public int RegenEdgeGrow { get; set; } = 12;
    public int RegenEdgeBlur { get; set; } = 8;
    public int RegenCoreGrow { get; set; } = 4;
    public int RegenCoreBlur { get; set; } = 1;
    /// <summary>재생성 v3: 영역마다 그 주변을 이 크기(정사각, px)로 잘라 SDXL에 보낸다 — SDXL은 1024 근처에서 가장 잘 그린다. 영역이 더 크면 1.6배 창을 이 크기로 줄여 보내고 되돌린다.</summary>
    public int RegenCropSize { get; set; } = 1024;
    /// <summary>재생성 v3 1단계(검열 조각 인페인트, 디노이즈 1.0)의 노이즈 마스크 확장(px) — 조각 테두리의 잔상까지 새로 그리게.</summary>
    public int RegenInpaintGrow { get; set; } = 8;
    /// <summary>1단계 ControlNet 종류. "Lineart"(기본) = 선화 ControlNet에 조각 자리를 검게 칠한 것.
    /// "Repaint"(실험용) = union promax의 repaint — 조각 자리를 검게 비운 원고를 넣어 채운다. 맞닿은 남녀 부위에서 막대 밑 부위가 지워지는 보고(2026-11-12)에 시도했으나
    /// 사용자 실측으로 검게 뭉개지고(검은 선이 많은 원고일수록 심함 — 검게 비운 자리를 먹칠로 읽는다) 더 나빴다. union이 아닌 ControlNet은 종류 설정이 없어 늘 Lineart.</summary>
    public string RegenInpaintControl { get; set; } = "Lineart";
    /// <summary>1단계(조각 인페인트) ControlNet 강도 — 2단계(RegenCnStrength)와 따로. 기본 0.25(2026-11-20 사용자가 같은 시드 A/B 0 / 0.25 / 0.5 — 5쪽 22조각 — 를 보고 고름: 0은 "주변과 안 이어져 이상").
    /// 1단계의 제어 그림은 조각 자리를 검게 비운 선화라, 강도가 높으면 그 구멍을 '선 없음'으로 따라 평평한 흰 막대로 메우고(0.5: 2026-11-19 3쪽 15조각×4시드에서 13/60), 0이면 주변 선의 안내가 없어 채운 그림이 주변과 안 이어진다.
    /// 흰 메움 수는 0 / 0.25 / 0.5에서 5 / 6 / 7(22조각)로 비슷해 시드 운이 더 크다. 0.5는 2026-11-12 실측으로 정했던 값(0.85에서 막대 밑 부위가 빈 살결로 메워진 시험 4쪽 중 2쪽), 0은 2026-11-19~20의 기본.
    /// 끝 지점(RegenCnEnd)은 둘이 같이 쓴다.</summary>
    public double RegenInpaintCnStrength { get; set; } = 0.25;

    // ── 재생성·커스텀의 '부위 통째 다시 그리기' (2026-11-12, 사용자 요청: 막대 자리만이 아니라 검열이 덮은 부위 전체를 재생성) ──
    // 검열 조각마다 겹치는 부위 검출 상자(BodyPartDetector, penis/pussy)를 여백만큼 넓혀 합치고, 검출이 없는 조각은 크기의 배율만큼 넓힌 타원으로(RegenRegion).
    // 저장 마스크·감지 결과는 안 바뀐다 — 재생성 때만 계산. 라벨 원고 144쪽 평가(scratchpad censor_model/eval_detector.py): 임계 0.12에서 부위 상자가
    // 검은 막대 94%·모자이크 79%·흰선 36%를 덮고 상자는 조각의 중앙값 4.5배. 가슴(nipple_f)은 쓰지 않는다(사용자 결정).
    public bool RegenWholeRegion { get; set; } = true;
    /// <summary>재생성 1단계가 감지 마스크 통째가 아니라 그 안의 검은 막대 픽셀만 지운다(RegenRegion.TightenErase) — 막대 사이로 보이는 윤곽·손을 남겨 모양을 잇게.
    /// 기본 켬(2026-11-13 사용자 판정): 원고 4쪽 × 후보 4개(지금 / 막대만 / 막대만+남녀 태그 / 남녀 태그만, 시드 고정)를 나란히 보고 "막대만+남녀 태그가 제일 잘 나왔다".
    /// 조각 안 선 밀도가 빈 면으로 메워지던 조각 0.029→0.235, 손으로 쥔 장면 0.046→0.139. 끄면 v1.9.1까지처럼 마스크 통째를 지운다. 짝이 되는 태그 쪽은 RegenTagsBySex.</summary>
    public bool RegenEraseTight { get; set; } = true;
    /// <summary>재생성 결과에 '평평한 흰 조각'(막대 자리의 ≥235 픽셀 비율 0.6 이상, RegenWhiteCheck)이 남으면 새 시드로 이만큼 더 그려 덜 흰 것을 고른다. 0 = 끔.
    /// 기본 2(2026-11-20 사용자 "또 흰색으로 칠해진다" → 조치): 흰 조각은 어느 CN 강도에서든 3분의 1쯤 나오는 시드 운이라(같은 시드 A/B 5·6·7/22, 새 시드 5/14) 설정으로는 줄지 않는다.
    /// 한 번 더 그릴 때마다 영역 하나에 1~2분이 든다(사용자 GPU 실측 117~161초). 시드를 고정한 장은 다시 그리지 않는다(사용자가 붙잡은 결과). 재시도 시드는 첫 시드에서 끌어낸다(RegenWhiteCheck.RetrySeed — 설정의 시드를 고정해도 같은 결과).</summary>
    public int RegenWhiteRetries { get; set; } = 2;
    /// <summary>부위 검출 임계 — 영역의 모양은 검열 조각과 겹치는 검출만 넓히므로 낮아도 모양에는 비용이 없다. 다만 조각 성별에서는 '뚜렷한 상자'의 기준이라(RegenRegion.PieceSex, 2026-11-19)
    /// 내리면 다른 성별의 약한 상자가 뚜렷한 상자로 올라와 남녀 태그가 늘어난다(28쪽의 여성 0.07은 0.07 이하로 내리면 다시 남녀 태그).</summary>
    public double RegenRegionConf { get; set; } = 0.12;
    public double RegenRegionPad { get; set; } = 0.10;    // 검출 상자를 사방으로 넓히는 비율(상자 크기 기준)
    public double RegenRegionGrow { get; set; } = 0.6;    // 검출이 없는 조각을 넓히는 배율(조각 크기 기준, 사방)

    // ── 글자 지운 자리 다시 그리기(TextRegen, 2026-11-26 사용자 "ComfyUI SDXL로 다시 그리는 방향으로" · "검열 제거와 별도로 특화된 모델을 따로" · "인페인팅과 컨트롤넷도 글자용으로 따로") ──
    // 번역 단계에서 LaMa로 지운 자리 가운데 바탕에 그림이 있는 곳(풍선 밖 대사·투명 풍선·빗금이 지나가는 풍선 — BubbleSolver.RegenStats)만 SDXL 인페인트 한 단계(WorkflowBuilder.BuildTextRegen)로
    // 다시 그린다. 흰 풍선 안은 LaMa 그대로. LaMa 만화판은 속도선·빗금·줄무늬를 잇지 못하고 뿌옇게 뭉갠다(실제 쪽 05·26, 조각을 160px로 줄여도 같았다).
    // 검열 재생성(Regen*)과 값을 나누지 않는다 — 부위 태그·2단계 다듬기가 글자 자리에는 맞지 않고, 사용자가 글자용 모델을 따로 고르려 한다. 기본값은 재생성 1단계의 값에서 시작한다.
    /// <summary>무늬 있는 바탕(스크린톤·줄무늬·속도선) 위의 글자 자리를 둘레의 무늬를 옮겨 메운다(Engine.TextureFill — OpenCV shift-map, 모델 없음). 기본 켬(2026-11-26 사용자
    /// "복구된 부분이 부자연스러워" · "여기도 뭉개졌어": LaMa는 그런 자리를 회색으로 뭉갠다). 끄면 v1.18.0처럼 단색이 아닌 블록을 전부 LaMa로 지운다 — 설정 창에 칸은 없다(json).</summary>
    public bool EraseTextureFill { get; set; } = true;
    /// <summary>글자 지운 자리를 SDXL로 다시 그린다(기본 끔, 시험 중). 번역을 켠 작업에서만 돈다(<see cref="UsesTextRegen"/>).</summary>
    public bool TextRegen { get; set; }
    /// <summary>글자용 체크포인트 파일 이름 — 비워 두면 재생성 체크포인트(<see cref="RegenCheckpoint"/>)를 따른다(<see cref="TextRegenCheckpointInUse"/>).</summary>
    public string TextRegenCheckpoint { get; set; } = "";
    /// <summary>글자용 ControlNet 파일 이름 — 비워 두면 재생성 ControlNet을 따른다(<see cref="TextRegenControlNetInUse"/>).</summary>
    public string TextRegenControlNet { get; set; } = "";
    /// <summary>글자 자리의 양성 프롬프트 — 부위 태그를 붙이지 않고 이것만 넣는다.</summary>
    public string TextRegenPrompt { get; set; } = "monochrome, greyscale, manga, screentone, halftone, hatching, speed lines, simple background, no humans, clean lineart, seamless background";
    /// <summary>음성 프롬프트 — 글자·말풍선과, 실제 쪽에서 지어내던 것(구름·상자·테 모양의 새 윤곽, 색)을 막는다.</summary>
    public string TextRegenNegative { get; set; } = "text, letters, speech bubble, sound effects, symbol, watermark, signature, object, cloud, box, frame, border, new outline, color, colorful, blurry, oversmoothed, artifacts";
    /// <summary>v1.18.1의 기본 프롬프트 — 결과를 보지 않고 정한 값이었다. 그대로 저장된 파일은 새 기본값으로 옮긴다(<see cref="SettingsVersion"/> 2).</summary>
    internal const string OldTextRegenPrompt = "monochrome manga, clean lineart, consistent screentone, seamless background";
    internal const string OldTextRegenNegative = "text, letters, sound effects, symbols, watermark, speech bubble, broken lines, blurry lineart, colorized, oversmoothed, residual artifacts";
    /// 12스텝·euler_ancestral(2026-11-27, RTX 2070 SUPER 8GB에서 잰 값): 창 하나가 62초(24스텝 dpmpp_sde_gpu — 스텝마다 두 번 계산한다) → 20초. 세 쪽 9블록의 결과는 비슷했다.
    public int TextRegenSteps { get; set; } = 12;
    public double TextRegenCfg { get; set; } = 5.0;
    public string TextRegenSampler { get; set; } = "euler_ancestral";
    public string TextRegenScheduler { get; set; } = "karras";
    /// <summary>-1이면 창마다 새로 뽑는다.</summary>
    public long TextRegenSeed { get; set; } = -1;
    /// <summary>구멍(지울 마스크)을 넓히는 px — 노이즈 마스크와 붙이는 범위(+2px)에 쓴다.</summary>
    public int TextRegenInpaintGrow { get; set; } = 8;
    /// <summary>ControlNet 종류: "Lineart"(구멍을 검게 비운 선화) | "Repaint"(union promax일 때만 — 구멍을 검게 비운 원고). 뜻은 <see cref="RegenInpaintControl"/>과 같다.</summary>
    public string TextRegenControl { get; set; } = "Lineart";
    /// <summary>ControlNet 강도 — 0이면 ControlNet 없이 프롬프트만으로 그린다(그래프에 체인을 만들지 않는다).</summary>
    /// 0.6·끝 1.0(2026-11-27, 실제 세 쪽): 0.25·0.85일 때는 구멍에 구름·상자 같은 윤곽을 지어냈다 — 구멍을 "선 없음"으로 세게, 끝까지 안내하면 줄어든다.
    public double TextRegenCnStrength { get; set; } = 0.6;
    public double TextRegenCnEnd { get; set; } = 1.0;
    /// <summary>디노이즈(0.05~1). 1이면 구멍을 노이즈에서 새로 그리고, 낮추면 먼저 메운 그림(무늬 메우기·LaMa 결과)을 밑그림 삼아 다듬는다.</summary>
    /// 0.45(2026-11-27, 실제 세 쪽 13블록): 1.0은 블록마다 없는 선·물체·검은 덩어리를 지어냈고(ControlNet 종류·강도를 바꿔도), 0.65도 흰 선을 그었다.
    /// 0.45는 속도선·빗금·짙은 톤 위의 자리에서 메운 자국을 다듬고, 0.35는 다듬는 것이 적다. 고른 망점은 SDXL이 조금 옅게 만든다(잠재 공간의 한계) — 그런 자리는 애초에 보내지 않는다.
    public double TextRegenDenoise { get; set; } = 0.45;
    public int TextRegenPreprocResolution { get; set; } = 1472;
    /// <summary>창 하나의 크기(정사각, px) — 한 쪽의 대상 블록을 이 창으로 묶어 보낸다(TextRegenPlan.WindowsFor).</summary>
    public int TextRegenCropSize { get; set; } = 1024;
    /// <summary>그린 자리가 둘레보다 눈에 띄게 희면(빈 조각 — TextRegenCheck) 새 시드로 이만큼 더 그린다. 0 = 끔. 재생성(2)보다 적은 1: 창 하나에 1분쯤 들고, 쪽마다 창이 여럿이다.</summary>
    public int TextRegenWhiteRetries { get; set; } = 1;

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

    // ── 번역 (내장 엔진: CTD + 글자 읽기(Hayai OCR 또는 manga-ocr) + LaMa) ────────────────
    /// <summary>"Google"(무료, 기본) · "DeepL" · "DeepSeek" · "Grok" (뒤 셋은 API 키 필요).</summary>
    public string TranslateEngine { get; set; } = "Google";
    /// <summary>글자 읽기(OCR) 엔진: "Hayai"(기본, 2026-11-27 — Hayai OCR v2.7 Supernova) · "MangaOcr"(v1.18.1까지의 엔진).
    /// 이 앱의 조각 72개로 잰 글자 오류율이 4.93% → 2.25%이고 손글씨·효과음에서 갈린다(11자 → 1자 / 54자). 고른 엔진의 모델만 받는다(<see cref="Runtime.ComponentInstaller.IsWanted"/>) —
    /// 고른 엔진의 모델이 없으면 다른 엔진으로 넘어가지 않고 번역이 준비되지 않은 것으로 본다.</summary>
    public string OcrEngine { get; set; } = "Hayai";
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
    /// <summary>글자 색 "#RRGGBB". 기본은 완전 검정(2026-11-18 사용자 결정 — 전에는 비움 = 원문 색 자동이었는데, 그 자동은 획 픽셀의
    /// 평균이라 안티에일리어싱 때문에 짙은 회색이 나왔다). 원문 색을 따라가려면 <see cref="LetterColorAuto"/>("auto")라고 적는다.
    /// 비우면 MigrateLegacy가 검정으로 채운다(옛 settings.json의 ""가 자동으로 남지 않게).</summary>
    public string LetterColorHex { get; set; } = "#000000";
    /// <summary>글자 색 칸에 적으면 원문 글자 색을 따라가는 낱말(대소문자 무관).</summary>
    public const string LetterColorAuto = "auto";
    /// <summary>글자 색 값이 '원문 색 자동'인지.</summary>
    public static bool IsAutoColor(string? value) =>
        value != null && value.Trim().Equals(LetterColorAuto, StringComparison.OrdinalIgnoreCase);
    /// <summary>외곽선(halo) 색. 어떤 배경에서도 읽히게 글자 둘레에 두른다.</summary>
    public string LetterOutlineColorHex { get; set; } = "#FFFFFF";
    /// <summary>외곽선 두께 배율. 0이면 외곽선 없음 (1.0 = 글자 크기의 1/7 정도).</summary>
    public double LetterOutlineScale { get; set; } = 1.0;
    /// <summary>바탕이 어두우면 글자 색과 외곽선 색을 서로 바꿔 그린다(2026-11-26 사용자 "바탕 밝기에 따라 글자색·외곽선색 자동 반전") — 투명 풍선·검은 바탕의 글이
    /// 흰 테두리 검은 글로 튀지 않고 검은 테두리 흰 글이 된다. 글이 놓이는 자리 아래 그림의 평균 밝기가 128 미만일 때(TextRenderer.DarkBehind).
    /// 블록에 색을 직접 적었거나 글자 색이 auto면 바꾸지 않는다. 두께는 그대로다.</summary>
    public bool LetterAutoContrast { get; set; } = true;
    /// <summary>식자 기본 정렬 — "Left"/"Center"/"Right". 블록별 지정이 없을 때 쓰인다.</summary>
    public string LetterAlign { get; set; } = "Center";
    /// <summary>식자 기본 기울임(이탤릭).</summary>
    public bool LetterItalic { get; set; } = false;
    /// <summary>식자 기본 줄간격 배율.</summary>
    public double LetterLineSpacing { get; set; } = 1.0;
    /// <summary>식자 어절 간격 배율 — 글꼴의 띄어쓰기 폭에 곱한다(1 = 글꼴 그대로). 사람이 식자한 원고는 띄어쓰기가 글꼴 기본보다 좁아서
    /// 기본값을 0.75로 둔다 (2026-11-09, 사용자 예시 원고 실측: 띄어쓰기 ≈ 글자 높이의 0.16~0.25배).</summary>
    public double LetterWordSpacing { get; set; } = 0.75;

    /// <summary>UI 테마 — "Light"(기본, 만화 원고풍 흑백) / "Dark"(반전).</summary>
    public string Theme { get; set; } = "Light";
    /// <summary>화면 언어 — "" = 아직 안 고름(Windows의 화면 언어를 따른다: 한국어·일본어가 아니면 영어 — Loc.Resolve) / "ko" / "en" / "ja".
    /// 만화를 옮기는 언어(TranslateTarget)와는 따로다. 2026-11-16 사용자 요청(이슈 #2 — 한국어를 못 읽는 사람도 쓸 수 있게).</summary>
    public string UiLanguage { get; set; } = "";
    /// <summary>결과 창 오른쪽 패널의 '안내' 카드(단축키 표)를 펼쳐 둘지 — 기본 접힘(2026-11-20 사용자 "접었다 펼 수 있는 기능으로").
    /// 결과 창에서 제목 띠를 누르면 바로 저장돼 다음 창도 같은 상태로 뜬다(ResultWindow.ShowGuide).</summary>
    public bool ResultGuideOpen { get; set; } = false;

    // ── 기타 ──────────────────────────────────────────────────
    /// <summary>civitai 모델(채색 체크포인트) 다운로드용 API 키. 로그인 필요 모델 대비 (선택).</summary>
    public string CivitaiApiKey { get; set; } = "";
    public int ComfyPort { get; set; } = 8188;
    /// <summary>작업 후 ComfyUI 서버를 유지해 다음 작업을 빠르게 시작한다.</summary>
    public bool KeepComfyWarm { get; set; } = true;

    // ── 저장/로드 ──────────────────────────────────────────────
    /// <summary>이 파일을 저장한 앱의 설정 판 — <see cref="Save"/>가 <see cref="CurrentSettingsVersion"/>으로 찍는다. 0 = 이 칸이 생기기 전(v1.15.0까지)에 저장된 파일, 또는 아직 저장하지 않은 새 설정.
    /// 기본값이 바뀐 칸은 값만 보고는 '옛 기본값이 그대로 적힌 것'과 '사용자가 고른 것'을 가를 수 없어, 어느 판이 저장했는지로 가른다(MigrateLegacy — 그 판까지의 기본값과 똑같을 때만 새 기본값으로).
    /// 2026-11-20 사용자 "다른 사람들이 릴리스 파일을 설치했을 때 기본값이 0.25가 되게": 코드의 기본값만 바꾸면 새로 설치한 사람만 받고, 업데이트한 사람의 settings.json에는 옛 기본값이 적혀 있어 그대로였다.</summary>
    public int SettingsVersion { get; set; } = 0;

    /// <summary>지금 앱의 설정 판. 기본값을 바꾸고 옛 파일의 값을 옮겨야 할 때 1 올리고 MigrateLegacy에 그 판의 규칙을 더한다. 1 = v1.15.1(1단계 CN 강도의 옛 기본값 0·0.5 → 0.25).</summary>
    public const int CurrentSettingsVersion = 2;   // 2 = v1.18.2(글자 자리 다시 그리기의 기본 프롬프트·ControlNet 강도·끝·샘플러·스텝 — 실제 쪽으로 맞춘 값)

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

    /// <summary>얕은 복사 — 결과 창이 '이 페이지만' 재생성 프롬프트·수치를 바꿔 쓸 때 전역 설정을 건드리지 않게 (2026-10-21). 문자열·숫자뿐이라 얕은 복사면 충분.</summary>
    public AppSettings ShallowClone() => (AppSettings)MemberwiseClone();

    /// <summary>마지막 <see cref="Load"/>가 설정 파일을 읽지 못했을 때 무엇을 했는지(백업에서 되살림 / 기본값으로 시작, 못 읽은 파일을 어디에 남겼는지) — 문제가 없었으면 null.
    /// 앱이 시작하면서 한 번 알린다(App.OnStartup).</summary>
    public static string? LoadProblem { get; private set; }

    /// <summary>설정을 읽는다. 파일이 없으면(첫 실행) 기본값.
    /// 읽히면 그 파일의 사본을 <c>settings.json.bak</c>으로 남긴다. 파일이 있는데 못 읽으면(쓰다 끊김·0바이트·깨진 JSON) 그 파일을 <c>settings.json.broken-&lt;시각&gt;</c>으로 남겨 두고
    /// 백업에서, 백업도 없으면 기본값으로 시작한다 — 무슨 일이 있었는지는 <see cref="LoadProblem"/>.
    /// 전에는 못 읽은 설정을 말없이 기본값으로 쳤고, 앱이 시작하면서 그 기본값을 저장해 API 키·프롬프트·연결한 ComfyUI 경로가 백업 없이 사라졌다(2026-11-13 점검 11번).</summary>
    public static AppSettings Load()
    {
        LoadProblem = null;
        _saveBlocked = false;
        var path = SettingsPath;
        if (!File.Exists(path)) return new AppSettings();
        if (TryRead(path, out bool locked) is { } s)
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }   // 사본을 못 남겨도 설정은 읽혔다
            return s;
        }
        if (locked)
        {
            // 다른 프로그램이 끝내 쥐고 있어 못 읽었다 — 깨진 것이 아니다. 백업(없으면 기본값)으로 이번 실행만 돌고 **저장하지 않는다**:
            // 저장하면 잠금이 풀린 멀쩡한 원본을 지난 세션을 시작할 때의 값으로 덮는다(2026-11-14 검수)
            _saveBlocked = true;
            var fromBackup = TryRead(path + ".bak", out _);
            // 경우마다 온 문장으로 적는다 — 가운데 낱말만 갈아 끼우면 다른 언어로 옮길 수 없다(Loc)
            LoadProblem = fromBackup != null
                ? Loc.T("설정 파일을 다른 프로그램이 쥐고 있어 읽지 못했다냥 — 이번 실행은 백업 설정으로 돈다냥. 바꾼 설정은 저장되지 않으니 앱을 다시 켜 줘냥.")
                : Loc.T("설정 파일을 다른 프로그램이 쥐고 있어 읽지 못했다냥 — 이번 실행은 기본값으로 돈다냥. 바꾼 설정은 저장되지 않으니 앱을 다시 켜 줘냥.");
            return fromBackup ?? new AppSettings();
        }
        string? kept = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Copy(path, kept, overwrite: true); } catch { kept = null; }
        var where = kept != null ? Loc.F($"\n읽지 못한 파일은 {Path.GetFileName(kept)} 이름으로 같은 폴더({Path.GetDirectoryName(Path.GetFullPath(kept))})에 남겨 두었다냥.") : "";
        if (TryRead(path + ".bak", out _) is { } restored)
        {
            LoadProblem = Loc.T("설정 파일을 읽지 못해 백업 설정으로 되살렸다냥 — 마지막에 바꾼 설정은 빠졌을 수 있다냥.") + where;
            return restored;
        }
        LoadProblem = Loc.T("설정 파일을 읽지 못해 기본값으로 시작한다냥 — API 키·프롬프트·ComfyUI 연결을 다시 넣어 줘냥.") + where;
        return new AppSettings();
    }

    /// <summary>설정 파일을 잠겨서 못 읽은 실행 — <see cref="Save"/>가 아무것도 쓰지 않는다(<see cref="Load"/>가 켜고 끈다).</summary>
    private static bool _saveBlocked;

    /// <summary>설정 파일 하나를 읽는다 — 없거나 못 읽으면 null. 다른 프로그램이 잠깐 쥐고 있으면(백신 검사·동기화) 두 번까지 기다렸다 다시 읽고,
    /// 그래도 잠겨 있으면 <paramref name="locked"/>(깨진 것과 가른다).</summary>
    private static AppSettings? TryRead(string path, out bool locked)
    {
        locked = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts);
                if (s == null) return null;   // 내용이 null 한 낱말 — 설정이 아니다
                s.MigrateLegacy(hadCumTags: text.Contains("\"" + nameof(RegenBodyTagsCum) + "\"", StringComparison.Ordinal),
                    hadFemaleTags: text.Contains("\"" + nameof(RegenBodyTagsFemale) + "\"", StringComparison.Ordinal));
                return s;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                if (attempt >= 2) { locked = true; return null; }
                Thread.Sleep(300);
            }
            catch { return null; }
        }
    }

    /// <summary>v0.1.x(BallonsTranslator 연동 시절) 설정값을 내장 엔진 형식으로 변환한다.</summary>
    /// <param name="hadCumTags">읽은 json에 사정 태그 칸(RegenBodyTagsCum)이 있었는가 — 없었으면 그 칸이 생기기 전의 설정이다.</param>
    /// <param name="hadFemaleTags">읽은 json에 여성 태그 칸이 있었는가(없으면 기본값이 들어와 있다 — 사용자가 뺀 것이 아니다).</param>
    private void MigrateLegacy(bool hadCumTags = true, bool hadFemaleTags = true)
    {
        TranslateSource = TranslateSource switch { "日本語" => "ja", "English" => "en", "简体中文" => "zh", _ => TranslateSource };
        TranslateTarget = TranslateTarget switch { "한국어" => "ko", "English" => "en", _ => TranslateTarget };   // loc:none
        // Papago 등 구 BT 엔진만 Google로 되돌린다 (DeepSeek·Grok은 유효).
        if (TranslateEngine is not ("Google" or "DeepL" or "DeepSeek" or "Grok")) TranslateEngine = "Google";
        if (SfxMode is not ("Keep" or "Replace")) SfxMode = "Keep";   // 효과음(시험 중) — 모르는 값은 끔(전처럼 그대로 둔다)
        if (OcrEngine is not ("Hayai" or "MangaOcr")) OcrEngine = "Hayai";
        // 옛 '강력'(SDXL+LoRA)·DeepCream 모드는 2026-10-19 폐기 — 둘 다 기본(내장 LaMa)으로 돌린다
        // ComfyLama(엔진 LaMa)는 2026-11-12 사용자 선택으로 삭제 — 기본으로. '이전 방식' 감지 엔진(DecensorEngine)도 같이 삭제(옛 json의 키는 무시된다)
        if (DecensorMode is not ("Basic" or "ComfyRegen" or "ComfyCustom")) DecensorMode = "Basic";
        if (DecensorLegacyAssist is not ("White" or "WhiteMosaic" or "All" or "None")) DecensorLegacyAssist = "White";
        if (DecensorAssistRepair is not ("Lama" or "Aletheia")) DecensorAssistRepair = "Aletheia";
        if (OnnxGpu is not ("Auto" or "On" or "Off")) OnnxGpu = "Auto";
        RegenCumThreshold = Math.Clamp(RegenCumThreshold, 0.05, 0.95);
        // 손으로 고친 settings.json에 "…": null 이 들어오면 문자열 속성이 null이 돼 .Contains/.Trim에서 앱이 죽는다 — 전부 ""로
        // (인스턴스 속성만 — 플래그 없이 훑으면 정적 속성 LoadProblem까지 ""로 바꾼다)
        foreach (var p in GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetIndexParameters().Length == 0))
            if (p.GetValue(this) == null) p.SetValue(this, "");
        if (GpuVendor is not ("auto" or "nvidia" or "nvidia-old" or "amd")) GpuVendor = "auto";
        if (UiLanguage is not ("ko" or "en" or "ja")) UiLanguage = "";   // 모르는 값 = 안 고름(Windows 언어를 따른다)
        if (DecensorReviewMode is not ("PerPage" or "Window")) DecensorReviewMode = "PerPage";
        CustomDecensorReach = Math.Clamp(CustomDecensorReach, 0, 512);
        SelfTrainEpochs = Math.Clamp(SelfTrainEpochs, 1, 50); SelfTrainLr = Math.Clamp(SelfTrainLr, 0.00001, 0.01);
        SelfTrainFreeze = Math.Clamp(SelfTrainFreeze, 0, 22); SelfTrainMinPages = Math.Clamp(SelfTrainMinPages, 5, 500);

        // 고급 옵션 안전망 — 손으로 json을 고치다 깨진 값이 워크플로를 터뜨리지 않게
        RegenSteps = Math.Clamp(RegenSteps, 1, 150);
        RegenCfg = Math.Clamp(RegenCfg, 0, 30);
        RegenDenoise = Math.Clamp(RegenDenoise, 0.05, 1.0);
        RegenCnStrength = Math.Clamp(RegenCnStrength, 0, 2);
        RegenCnEnd = Math.Clamp(RegenCnEnd, 0, 1);
        RegenPreprocResolution = Math.Clamp(RegenPreprocResolution, 256, 4096);
        RegenEdgeGrow = Math.Clamp(RegenEdgeGrow, 0, 128); RegenEdgeBlur = Math.Clamp(RegenEdgeBlur, 0, 64);
        RegenCoreGrow = Math.Clamp(RegenCoreGrow, 0, 64); RegenCoreBlur = Math.Clamp(RegenCoreBlur, 0, 64);
        RegenRegionConf = Math.Clamp(RegenRegionConf, 0.01, 0.9); RegenRegionPad = Math.Clamp(RegenRegionPad, 0, 1); RegenRegionGrow = Math.Clamp(RegenRegionGrow, 0, 3);
        RegenCropSize = Math.Clamp(RegenCropSize, 512, 2048); RegenInpaintGrow = Math.Clamp(RegenInpaintGrow, 0, 64);
        // 옛 한 줄 신체부위 태그(v1.8.3까지) → 남성/여성 두 칸(재생성 v3). penis/glans(glains 오타)는 남성, 나머지는 여성. 한 번 옮기면 비워 다시 안 옮긴다.
        // 새 칸이 이미 기본값이 아니면(사용자가 나눈 뒤 저장한 json) 옛 칸은 그대로 둔다 — 저장·읽기 왕복에서 값이 변하지 않게
        var fresh = new AppSettings();
        bool splitLegacyLine = false;
        if (!string.IsNullOrWhiteSpace(RegenBodyTags) && RegenBodyTagsMale == fresh.RegenBodyTagsMale && RegenBodyTagsFemale == fresh.RegenBodyTagsFemale)
        {
            splitLegacyLine = true;
            var male = new List<string>(); var female = new List<string>();
            foreach (var raw in RegenBodyTags.Split(','))
            {
                var tag = raw.Trim();
                if (tag.Length == 0) continue;
                var lower = tag.ToLowerInvariant();
                if (lower.Contains("penis") || lower.Contains("glans") || lower.Contains("glains")) male.Add(lower == "glains" ? "glans" : tag);
                else female.Add(tag);
            }
            if (male.Count > 0) RegenBodyTagsMale = string.Join(", ", male);
            if (female.Count > 0) RegenBodyTagsFemale = string.Join(", ", female);
            RegenBodyTags = "";
        }
        // 질내사정 태그 떼기(2026-11-16, 한 번만 — 그 칸이 아직 없던 json): 여성 태그 줄의 'cum in pussy'는 이제 사정 태그 칸의 몫이다(칸은 기본값으로 시작한다).
        // 옛 한 줄 태그를 나눈 **뒤에** 뗀다 — 먼저 떼면 v1.8.3까지의 json은 나누면서 여성 줄에 다시 들어가 늘 들어가고, 사정인 영역에는 두 번 들어간다(검수).
        // 여성 줄을 갖고 있었는데 그 태그가 없었다면 사용자가 이미 빼 둔 것이다 — 사정 칸을 비워 자동 판단으로 다시 넣지 않는다(설정에서 적으면 켜진다)
        if (!hadCumTags)
        {
            var kept = RegenBodyTagsFemale.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            int removed = kept.RemoveAll(t => t.Replace('_', ' ').Equals(DefaultCumTags, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) RegenBodyTagsFemale = string.Join(", ", kept);
            else if (hadFemaleTags || splitLegacyLine) RegenBodyTagsCum = "";
        }
        if (RegenInpaintControl is not ("Repaint" or "Lineart")) RegenInpaintControl = "Lineart";
        // 1단계 CN 강도의 옛 기본값 옮기기(판 0 → 1, 2026-11-20): v1.9.0~v1.13.x는 0.5, v1.14.0~v1.14.1은 0이 기본이었고 저장하면 그 값이 파일에 적힌다.
        // 판이 안 찍힌 파일에서 그 둘과 똑같은 값만 새 기본 0.25로 — 다른 값은 사용자가 고른 것. 그 뒤 사용자가 0이나 0.5로 돌려 저장하면 판이 찍혀 다시 옮기지 않는다.
        // 클램프 앞에서 본다 — 손으로 적은 -1을 0으로 맞춘 뒤에 보면 옮겨져, 클램프 하한 시험(-1 → 0)과 어긋난다.
        if (SettingsVersion < 1 && RegenInpaintCnStrength is 0.0 or 0.5) RegenInpaintCnStrength = 0.25;
        RegenInpaintCnStrength = Math.Clamp(RegenInpaintCnStrength, 0, 2);
        RegenWhiteRetries = Math.Clamp(RegenWhiteRetries, 0, 5);
        // 글자 지운 자리 다시 그리기 — 재생성과 같은 범위
        TextRegenSteps = Math.Clamp(TextRegenSteps, 1, 150); TextRegenCfg = Math.Clamp(TextRegenCfg, 0, 30);
        TextRegenInpaintGrow = Math.Clamp(TextRegenInpaintGrow, 0, 64);
        if (TextRegenControl is not ("Repaint" or "Lineart")) TextRegenControl = "Lineart";
        TextRegenCnStrength = Math.Clamp(TextRegenCnStrength, 0, 2); TextRegenCnEnd = Math.Clamp(TextRegenCnEnd, 0, 1);
        TextRegenDenoise = Math.Clamp(TextRegenDenoise, 0.05, 1);   // 0이면 한 스텝도 안 돈다 — 켰는데 아무 일도 없는 설정이 된다
        // 설정 판 2(2026-11-27): v1.18.1이 저장한 옛 기본값(손대지 않은 값)만 실제 쪽으로 맞춘 새 기본값으로 옮긴다. 디노이즈는 새 칸이라 옛 파일에 없다(기본값이 들어온다)
        if (SettingsVersion < 2)
        {
            var tuned = new AppSettings();
            if (TextRegenPrompt == OldTextRegenPrompt) TextRegenPrompt = tuned.TextRegenPrompt;
            if (TextRegenNegative == OldTextRegenNegative) TextRegenNegative = tuned.TextRegenNegative;
            if (TextRegenCnStrength == 0.25) TextRegenCnStrength = tuned.TextRegenCnStrength;
            if (TextRegenCnEnd == 0.85) TextRegenCnEnd = tuned.TextRegenCnEnd;
            if (TextRegenSampler == "dpmpp_sde_gpu") TextRegenSampler = tuned.TextRegenSampler;
            if (TextRegenSteps == 24) TextRegenSteps = tuned.TextRegenSteps;
        }
        TextRegenPreprocResolution = Math.Clamp(TextRegenPreprocResolution, 256, 4096);
        TextRegenCropSize = Math.Clamp(TextRegenCropSize, 512, 2048);
        TextRegenWhiteRetries = Math.Clamp(TextRegenWhiteRetries, 0, 5);
        TextRegenSeed = Math.Max(-1, TextRegenSeed);   // -1 = 매번 새로. 그보다 작은 값은 뜻이 없다
        if (string.IsNullOrWhiteSpace(TextRegenSampler)) TextRegenSampler = "dpmpp_sde_gpu";
        if (string.IsNullOrWhiteSpace(TextRegenScheduler)) TextRegenScheduler = "karras";
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
        // 글자 색 비움은 옛 기본값(원문 색 자동) — 기본이 검정이 된 뒤(2026-11-18)로는 검정으로 채운다. 자동은 "auto"로만.
        // 16진수(#RRGGBB)도 auto도 아닌 값("red"·"#12345"·알파 있는 #AARRGGBB)은 다른 설정처럼 유효값으로 — 렌더러는 어차피 검정으로 떨어졌다(검수 2026-11-19)
        if (string.IsNullOrWhiteSpace(LetterColorHex) || (!IsAutoColor(LetterColorHex) && !System.Text.RegularExpressions.Regex.IsMatch(LetterColorHex.Trim(), "^#[0-9A-Fa-f]{6}$")))
            LetterColorHex = "#000000";
        LetterLineSpacing = Math.Clamp(LetterLineSpacing, 0.6, 3.0);
        LetterWordSpacing = LetterWordSpacing <= 0 ? 0.75 : Math.Clamp(LetterWordSpacing, 0.3, 2.0);
    }

    /// <summary>설정을 저장한다 — 임시 파일에 다 쓴 뒤 바꿔치기한다(<see cref="ImgIo.WriteAllTextAtomic"/>). 제자리 쓰기는 파일을 비운 뒤 채워서,
    /// 그 사이에 앱이 꺼지면 잘린 설정이 남았다(다음 실행이 그것을 못 읽는다). 쓰기 전에 <see cref="SettingsVersion"/>을 이 앱의 판으로 찍는다.</summary>
    public void Save()
    {
        if (_saveBlocked) return;   // 설정 파일을 잠겨서 못 읽은 실행 — 지금 쓰면 읽지 못한 멀쩡한 원본을 덮는다(Load가 알렸다)
        SettingsVersion = CurrentSettingsVersion;   // 이 판까지의 옛 기본값 옮기기는 끝났다 — 다음 실행의 MigrateLegacy가 이 파일을 다시 옮기지 않는다(첫 실행의 저장도 찍어야 그 뒤 사용자가 고른 0이 남는다)
        // NEKO_SETTINGS가 디렉터리 없는 상대 파일명이면 폴더 부분이 ""다 → 전체 경로로 푼 뒤 쓴다(폴더는 쓰는 쪽이 만든다)
        ImgIo.WriteAllTextAtomic(Path.GetFullPath(SettingsPath), JsonSerializer.Serialize(this, JsonOpts));
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

    /// <summary>글자 자리 다시 그리기가 실제로 쓰는 체크포인트 — 글자용 칸이 비었으면 재생성 것.</summary>
    [JsonIgnore]
    public string TextRegenCheckpointInUse => string.IsNullOrWhiteSpace(TextRegenCheckpoint) ? RegenCheckpoint : TextRegenCheckpoint.Trim();
    /// <summary>글자 자리 다시 그리기가 실제로 쓰는 ControlNet — 글자용 칸이 비었으면 재생성 것.</summary>
    [JsonIgnore]
    public string TextRegenControlNetInUse => string.IsNullOrWhiteSpace(TextRegenControlNet) ? RegenControlNet : TextRegenControlNet.Trim();
    /// <summary>이 작업에서 글자 자리 다시 그리기가 도는가 — 번역을 꺼 두면 글자를 지우지도 않는다.</summary>
    [JsonIgnore]
    public bool UsesTextRegen => EnableTranslate && TextRegen;
    /// <summary>이 설정의 작업이 ComfyUI를 쓰는가(자동 채색 · 재생성/커스텀 검열 제거 · 글자 자리 다시 그리기) — 내장 AI의 GPU 자동 규칙(OnnxEnv)과 시작 전 확인이 같이 본다.</summary>
    [JsonIgnore]
    public bool UsesComfy => UsesComfyApartFromTextRegen || UsesTextRegen;
    /// <summary>글자 자리 다시 그리기를 뺀 ComfyUI 사용(자동 채색 · 재생성/커스텀 검열 제거) — 번역기를 쓰지 않는 실행과 러너 밖의 길(결과 창의 제자리 지우기·부위 감지)은 이것만 센다:
    /// 글자 자리 다시 그리기는 번역 단계에서만 돈다(<c>PipelineRunner.NeedsComfy</c>, <c>OnnxEnv.GpuAllowed</c>).</summary>
    [JsonIgnore]
    public bool UsesComfyApartFromTextRegen => EnableAutoColor || (EnableDecensor && DecensorMode is "ComfyRegen" or "ComfyCustom");

    /// <summary>ComfyUI가 사용 가능한가 (채색·재생성·커스텀 검열제거·글자 자리 다시 그리기의 전제조건).</summary>
    [JsonIgnore]
    public bool ComfyAvailable =>
        File.Exists(Path.Combine(ComfyRoot, "ComfyUI", "main.py")) &&
        File.Exists(Path.Combine(ComfyRoot, "python_embeded", "python.exe"));
}
