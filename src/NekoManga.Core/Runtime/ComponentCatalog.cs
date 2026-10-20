namespace NekoManga.Core.Runtime;

public enum ComponentKind
{
    /// <summary>7z 아카이브 — 내려받아 RuntimeRoot에 푼다 (7zr.exe 사용).</summary>
    Archive7z,
    /// <summary>zip 아카이브 — 지정 폴더에 푼다.</summary>
    ArchiveZip,
    /// <summary>단일 파일 — 지정 경로로 내려받는다.</summary>
    File,
}

public record Component(
    string Id,
    string DisplayName,
    ComponentKind Kind,
    string Url,
    /// <summary>RuntimeRoot 기준 상대 목적지. Archive는 풀 폴더, File은 파일 경로.</summary>
    string Target,
    /// <summary>설치 여부 판정에 쓰는 존재 확인 경로 (RuntimeRoot 기준 상대).</summary>
    string Probe,
    long ApproxBytes,
    /// <summary>zip 최상위 폴더 한 겹을 벗겨서 푼다 (GitHub 소스 zip용).</summary>
    bool StripRootFolder = false,
    /// <summary>civitai처럼 Authorization 토큰이 필요할 수 있는 다운로드.</summary>
    bool UseCivitaiKey = false,
    /// <summary>채색·엔진 LaMa(ComfyUI) 전용 구성요소 — 내장 기능(번역·기본 검열제거)에는 불필요.</summary>
    bool ForComfy = false,
    /// <summary>이 구성요소가 대체한 옛 파일(RuntimeRoot 기준 상대) — 설치가 끝나면 지운다 (예: 만화판 LaMa가 대체한 Carve lama_fp32.onnx).</summary>
    string[]? Obsolete = null
);

/// <summary>필요 구성요소 목록. 검증된 소스 (2026-10 기준, 실제 설치에 사용한 URL).</summary>
public static class ComponentCatalog
{
    public const string ComfyDirName = "ComfyUI_windows_portable";

    // ── 내장 엔진 모델 (번역·검열 감지·기본 검열제거 — 필수, 총 ~780MB) ──
    public static readonly Component ModelCtd = new(
        "model-ctd", "텍스트 감지 모델 (comictextdetector)", ComponentKind.File,
        "https://huggingface.co/dreMaz/mit_models/resolve/main/comictextdetector.pt.onnx",
        "models/ctd.onnx", "models/ctd.onnx", 94_670_000);

    /// <summary>글자·검열 자리 복원 모델 — **만화·애니 학습 big-lama**(dreMaz/AnimeMangaInpainting `lama_large_512px.ckpt`, MIT,
    /// 만화·애니 30만 장 미세조정)를 ONNX로 이식한 것(D:\NekoManga-train\lama_manga\lama_port.py, fp32 211MB).
    /// 2026-10-19까지는 사진(Places) 학습 Carve `lama_fp32.onnx`였다 — 입출력 계약이 같아 LamaInpainter 코드는 그대로.
    /// 211MB라 git(100MiB 제한)에 못 넣으므로 **고정 태그 `models`의 사전 릴리스 자산**으로 올린다(latest 릴리스와 무관하게 주소 고정).
    /// **이 자산을 올리기 전에는 아래 URL이 404라 새 설치·업그레이드의 엔진 설치가 통째로 실패한다** — 커밋/태그 전에 한 번 올릴 것
    /// (GitHub CLI 필요: `winget install GitHub.cli` 후 `gh auth login`, 또는 웹 Releases UI에서 수동 업로드):
    /// `gh release create models -R TTOGEARII/NekoManga --prerelease --target 44c4380424aaebd6b28ee695c395fff9c621d8ff --title "모델 자산"
    ///   --notes "앱이 내려받는 대용량 모델 (lama_manga.onnx)" "D:\NekoManga-train\lama_manga\lama_manga.onnx"`
    /// `--target`은 v0.1.0 커밋 — GitHub의 latest는 릴리스 created_at(=태그 커밋 날짜) 기준이라, 만에 하나 사전 릴리스 표시가 풀려도 latest가 되지
    /// 않게 가장 오래된 커밋에 건다. (사전 릴리스는 Velopack 업데이트 검색(GithubSource prerelease:false)·releases/latest에서 제외된다.)
    /// 확인: `curl -sI https://github.com/TTOGEARII/NekoManga/releases/download/models/lama_manga.onnx` 가 302(objects.githubusercontent.com)면 OK.
    /// 옛 Carve `lama_fp32.onnx`(208MB)는 설치 후 Obsolete로 지운다(업그레이드 설치에 남는 죽은 파일).</summary>
    public static readonly Component ModelLama = new(
        "model-lama", "배경 복원 모델 (LaMa 만화판)", ComponentKind.File,
        "https://github.com/TTOGEARII/NekoManga/releases/download/models/lama_manga.onnx",
        "models/lama_manga.onnx", "models/lama_manga.onnx", 210_935_000,
        Obsolete: new[] { "models/lama_fp32.onnx" });

    public static readonly Component ModelOcrEncoder = new(
        "model-ocr-enc", "글자 인식 모델 1/2 (manga-ocr)", ComponentKind.File,
        "https://huggingface.co/mayocream/manga-ocr-onnx/resolve/main/encoder_model.onnx",
        "models/ocr_encoder.onnx", "models/ocr_encoder.onnx", 343_455_000);

    public static readonly Component ModelOcrDecoder = new(
        "model-ocr-dec", "글자 인식 모델 2/2 (manga-ocr)", ComponentKind.File,
        "https://huggingface.co/mayocream/manga-ocr-onnx/resolve/main/decoder_model.onnx",
        "models/ocr_decoder.onnx", "models/ocr_decoder.onnx", 117_481_000);

    public static readonly Component ModelOcrVocab = new(
        "model-ocr-vocab", "글자 인식 사전", ComponentKind.File,
        "https://huggingface.co/mayocream/manga-ocr-onnx/resolve/main/vocab.txt",
        "models/ocr_vocab.txt", "models/ocr_vocab.txt", 31_000);

    /// <summary>NekoManga 맞춤 검열 검출 모델 — 이 앱 전용으로 직접 학습했다 (릴리스 자산).
    /// 무검열 원고에 검열을 합성 + 검열/무검열 쌍 diff 실라벨로 학습해, 흰 막대·흰 박스·
    /// 단색 밀기·검은 막대·모자이크를 한 번에 찾는다 (CensorNet 주석 참조).</summary>
    public static readonly Component ModelCensor = new(
        "model-censor", "검열 감지 AI 모델 (NekoManga 전용)", ComponentKind.File,
        "https://github.com/TTOGEARII/NekoManga/releases/latest/download/censor_seg.onnx",
        "models/censor_seg.onnx", "models/censor_seg.onnx", 14_060_000);

    /// <summary>검열 감지 모델의 PyTorch 가중치 — 자가 학습(SelfTrainer)의 시작점. ComfyUI의 python_embeded로만 학습하므로 ComfyUI 묶음에 넣는다 (7MB).</summary>
    public static readonly Component ModelCensorPt = new(
        "model-censor-pt", "검열 감지 AI 학습용 가중치 (자가 학습)", ComponentKind.File,
        "https://github.com/TTOGEARII/NekoManga/releases/latest/download/censor_seg.pt",
        "models/censor_seg.pt", "models/censor_seg.pt", 7_120_000, ForComfy: true);

    // ── ComfyUI (채색·엔진 LaMa 검열제거 전용 — 선택 설치) ──
    public static readonly Component SevenZip = new(
        "7zr", "7-Zip 압축해제기", ComponentKind.File,
        "https://www.7-zip.org/a/7zr.exe",
        "tools/7zr.exe", "tools/7zr.exe", 600_000, ForComfy: true);

    public static readonly Component ComfyPortable = new(
        "comfyui", "ComfyUI (포터블, 내장 Python 포함)", ComponentKind.Archive7z,
        "https://github.com/comfyanonymous/ComfyUI/releases/latest/download/ComfyUI_windows_portable_nvidia.7z",
        ".", $"{ComfyDirName}/ComfyUI/main.py", 1_800_000_000, ForComfy: true);

    public static readonly Component NodeWd14 = new(
        "node-wd14", "WD14 Tagger 노드 (자동 태그)", ComponentKind.ArchiveZip,
        "https://github.com/pythongosssss/ComfyUI-WD14-Tagger/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-WD14-Tagger",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-WD14-Tagger/wd14tagger.py", 1_000_000,
        StripRootFolder: true, ForComfy: true);

    public static readonly Component NodeCustomScripts = new(
        "node-pysssss", "Custom-Scripts 노드 (문자열 결합)", ComponentKind.ArchiveZip,
        "https://github.com/pythongosssss/ComfyUI-Custom-Scripts/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-Custom-Scripts",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-Custom-Scripts/pysssss.py", 2_000_000,
        StripRootFolder: true, ForComfy: true);

    public static readonly Component NodeLayerStyle = new(
        "node-layerstyle", "LayerStyle 노드 (이미지 합성)", ComponentKind.ArchiveZip,
        "https://github.com/chflame163/ComfyUI_LayerStyle/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle/__init__.py", 40_000_000,
        StripRootFolder: true, ForComfy: true);

    public static readonly Component NodeControlnetAux = new(
        "node-cnaux", "ControlNet 전처리기 노드 (LineArt·XDoG)", ComponentKind.ArchiveZip,
        "https://github.com/Fannovel16/comfyui_controlnet_aux/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/comfyui_controlnet_aux",
        $"{ComfyDirName}/ComfyUI/custom_nodes/comfyui_controlnet_aux/__init__.py", 2_000_000,
        StripRootFolder: true, ForComfy: true);

    public static readonly Component ModelCheckpoint = new(
        "model-wai", "채색 체크포인트 WAI-illustrious v17 (6.5GB)", ComponentKind.File,
        "https://civitai.com/api/download/models/2883731?fileId=2763986",
        $"{ComfyDirName}/ComfyUI/models/checkpoints/waiIllustriousSDXL_v170.safetensors",
        $"{ComfyDirName}/ComfyUI/models/checkpoints/waiIllustriousSDXL_v170.safetensors",
        6_930_000_000, UseCivitaiKey: true, ForComfy: true);

    public static readonly Component ModelControlnet = new(
        "model-cn-union", "ControlNet union promax (2.4GB)", ComponentKind.File,
        "https://huggingface.co/xinsir/controlnet-union-sdxl-1.0/resolve/main/diffusion_pytorch_model_promax.safetensors",
        $"{ComfyDirName}/ComfyUI/models/controlnet/controlnet-union-sdxl-promax.safetensors",
        $"{ComfyDirName}/ComfyUI/models/controlnet/controlnet-union-sdxl-promax.safetensors",
        2_510_000_000, ForComfy: true);

    // ── 표지 정밀 매칭 (IPAdapter — 선택이지만 기본 설치) ──
    public static readonly Component NodeIpAdapter = new(
        "node-ipadapter", "IPAdapter 노드 (표지 색 매칭)", ComponentKind.ArchiveZip,
        "https://github.com/cubiq/ComfyUI_IPAdapter_plus/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_IPAdapter_plus",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_IPAdapter_plus/IPAdapterPlus.py", 400_000,
        StripRootFolder: true, ForComfy: true);

    public static readonly Component ModelIpAdapter = new(
        "model-ipadapter", "IPAdapter SDXL 모델 (0.85GB)", ComponentKind.File,
        "https://huggingface.co/h94/IP-Adapter/resolve/main/sdxl_models/ip-adapter-plus_sdxl_vit-h.safetensors",
        $"{ComfyDirName}/ComfyUI/models/ipadapter/ip-adapter-plus_sdxl_vit-h.safetensors",
        $"{ComfyDirName}/ComfyUI/models/ipadapter/ip-adapter-plus_sdxl_vit-h.safetensors",
        847_517_000, ForComfy: true);

    public static readonly Component ModelClipVision = new(
        "model-clipvision", "CLIP 비전 모델 ViT-H (2.5GB)", ComponentKind.File,
        "https://huggingface.co/h94/IP-Adapter/resolve/main/models/image_encoder/model.safetensors",
        $"{ComfyDirName}/ComfyUI/models/clip_vision/CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors",
        $"{ComfyDirName}/ComfyUI/models/clip_vision/CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors",
        2_528_373_000, ForComfy: true);

    /// <summary>설치 순서대로. 내장 엔진 모델 먼저, ComfyUI(선택)는 뒤에.</summary>
    public static IReadOnlyList<Component> All { get; } = new[]
    {
        ModelCtd, ModelLama, ModelOcrEncoder, ModelOcrDecoder, ModelOcrVocab, ModelCensor,
        SevenZip, ComfyPortable,
        NodeWd14, NodeCustomScripts, NodeLayerStyle, NodeControlnetAux, NodeIpAdapter,
        ModelCheckpoint, ModelControlnet, ModelIpAdapter, ModelClipVision, ModelCensorPt,
    };

    public static IEnumerable<Component> EngineComponents => All.Where(c => !c.ForComfy);
    public static IEnumerable<Component> ComfyComponents => All.Where(c => c.ForComfy);

    public const string FirstRunNote =
        "번역·검열 감지·검열 복원(LaMa)은 작은 모델(0.8GB)만 있으면 돼요. 채색까지 쓰려면 채색 엔진(약 20GB)을 함께 설치해 주세요.";
}
