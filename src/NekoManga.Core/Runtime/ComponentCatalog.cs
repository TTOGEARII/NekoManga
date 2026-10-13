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
    /// <summary>채색·강력 검열제거(ComfyUI) 전용 구성요소 — 내장 기능(번역·기본 검열제거)에는 불필요.</summary>
    bool ForComfy = false
);

/// <summary>필요 구성요소 목록. 검증된 소스 (2026-10 기준, 실제 설치에 사용한 URL).</summary>
public static class ComponentCatalog
{
    public const string ComfyDirName = "ComfyUI_windows_portable";

    // ── 내장 엔진 모델 (번역·기본 검열제거 — 필수, 총 ~760MB) ──
    public static readonly Component ModelCtd = new(
        "model-ctd", "텍스트 감지 모델 (comictextdetector)", ComponentKind.File,
        "https://huggingface.co/dreMaz/mit_models/resolve/main/comictextdetector.pt.onnx",
        "models/ctd.onnx", "models/ctd.onnx", 94_670_000);

    public static readonly Component ModelLama = new(
        "model-lama", "배경 복원 모델 (LaMa)", ComponentKind.File,
        "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx",
        "models/lama_fp32.onnx", "models/lama_fp32.onnx", 208_045_000);

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

    // ── ComfyUI (채색·강력 검열제거 전용 — 선택 설치) ──
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

    /// <summary>설치 순서대로. 내장 엔진 모델 먼저, ComfyUI(선택)는 뒤에.</summary>
    public static IReadOnlyList<Component> All { get; } = new[]
    {
        ModelCtd, ModelLama, ModelOcrEncoder, ModelOcrDecoder, ModelOcrVocab,
        SevenZip, ComfyPortable,
        NodeWd14, NodeCustomScripts, NodeLayerStyle, NodeControlnetAux,
        ModelCheckpoint, ModelControlnet,
    };

    public static IEnumerable<Component> EngineComponents => All.Where(c => !c.ForComfy);
    public static IEnumerable<Component> ComfyComponents => All.Where(c => c.ForComfy);

    public const string FirstRunNote =
        "번역·기본 검열제거는 작은 모델(0.8GB)만 있으면 돼요. 채색·강력 검열제거를 쓰려면 ComfyUI(11GB)까지 설치해 주세요.";
}
