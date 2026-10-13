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
    bool UseCivitaiKey = false
);

/// <summary>필요 구성요소 목록. 검증된 소스 (2026-10 기준, 이전 세션에서 실제 설치에 사용한 URL).</summary>
public static class ComponentCatalog
{
    public const string ComfyDirName = "ComfyUI_windows_portable";

    public static readonly Component SevenZip = new(
        "7zr", "7-Zip 압축해제기", ComponentKind.File,
        "https://www.7-zip.org/a/7zr.exe",
        "tools/7zr.exe", "tools/7zr.exe", 600_000);

    public static readonly Component ComfyPortable = new(
        "comfyui", "ComfyUI (포터블, 내장 Python 포함)", ComponentKind.Archive7z,
        "https://github.com/comfyanonymous/ComfyUI/releases/latest/download/ComfyUI_windows_portable_nvidia.7z",
        ".", $"{ComfyDirName}/ComfyUI/main.py", 1_800_000_000);

    // ── ComfyUI 커스텀 노드 (GitHub 소스 zip) ──
    public static readonly Component NodeWd14 = new(
        "node-wd14", "WD14 Tagger 노드 (자동 태그)", ComponentKind.ArchiveZip,
        "https://github.com/pythongosssss/ComfyUI-WD14-Tagger/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-WD14-Tagger",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-WD14-Tagger/wd14tagger.py", 1_000_000, StripRootFolder: true);

    public static readonly Component NodeCustomScripts = new(
        "node-pysssss", "Custom-Scripts 노드 (문자열 결합)", ComponentKind.ArchiveZip,
        "https://github.com/pythongosssss/ComfyUI-Custom-Scripts/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-Custom-Scripts",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI-Custom-Scripts/pysssss.py", 2_000_000, StripRootFolder: true);

    public static readonly Component NodeLayerStyle = new(
        "node-layerstyle", "LayerStyle 노드 (이미지 합성)", ComponentKind.ArchiveZip,
        "https://github.com/chflame163/ComfyUI_LayerStyle/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle/__init__.py", 40_000_000, StripRootFolder: true);

    public static readonly Component NodeLayerStyleAdvance = new(
        "node-layerstyle-adv", "LayerStyle Advance 노드 (LaMa 지우개)", ComponentKind.ArchiveZip,
        "https://github.com/chflame163/ComfyUI_LayerStyle_Advance/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle_Advance",
        $"{ComfyDirName}/ComfyUI/custom_nodes/ComfyUI_LayerStyle_Advance/__init__.py", 60_000_000, StripRootFolder: true);

    public static readonly Component NodeControlnetAux = new(
        "node-cnaux", "ControlNet 전처리기 노드 (LineArt·XDoG)", ComponentKind.ArchiveZip,
        "https://github.com/Fannovel16/comfyui_controlnet_aux/archive/refs/heads/main.zip",
        $"{ComfyDirName}/ComfyUI/custom_nodes/comfyui_controlnet_aux",
        $"{ComfyDirName}/ComfyUI/custom_nodes/comfyui_controlnet_aux/__init__.py", 2_000_000, StripRootFolder: true);

    // ── 모델 ──
    public static readonly Component ModelCheckpoint = new(
        "model-wai", "채색 체크포인트 WAI-illustrious v17 (6.5GB)", ComponentKind.File,
        "https://civitai.com/api/download/models/2883731?fileId=2763986",
        $"{ComfyDirName}/ComfyUI/models/checkpoints/waiIllustriousSDXL_v170.safetensors",
        $"{ComfyDirName}/ComfyUI/models/checkpoints/waiIllustriousSDXL_v170.safetensors",
        6_930_000_000, UseCivitaiKey: true);

    public static readonly Component ModelControlnet = new(
        "model-cn-union", "ControlNet union promax (2.4GB)", ComponentKind.File,
        "https://huggingface.co/xinsir/controlnet-union-sdxl-1.0/resolve/main/diffusion_pytorch_model_promax.safetensors",
        $"{ComfyDirName}/ComfyUI/models/controlnet/controlnet-union-sdxl-promax.safetensors",
        $"{ComfyDirName}/ComfyUI/models/controlnet/controlnet-union-sdxl-promax.safetensors",
        2_510_000_000);

    // ── BallonsTranslator (윈도우 미니멈 배포판: 소스 + 전용 Python 환경 포함) ──
    public static readonly Component Ballons = new(
        "ballons", "BallonsTranslator (번역 엔진)", ComponentKind.ArchiveZip,
        "https://github.com/dmMaze/BallonsTranslator/releases/latest/download/Ballonstranslator_win_minium.zip",
        "BallonsTranslator",
        "BallonsTranslator/ballontranslator/launch.py", 35_000_000, StripRootFolder: true);

    /// <summary>설치 순서대로. 7zr이 먼저 있어야 ComfyUI 7z를 풀 수 있다.</summary>
    public static IReadOnlyList<Component> All { get; } = new[]
    {
        SevenZip, ComfyPortable,
        NodeWd14, NodeCustomScripts, NodeLayerStyle, NodeLayerStyleAdvance, NodeControlnetAux,
        ModelCheckpoint, ModelControlnet,
        Ballons,
    };

    /// <summary>WD14 eva02 태거 모델(~1GB)과 LaMa·BT 모델들은 각 엔진이 첫 실행 때 자동 다운로드한다.
    /// 설치 마법사에서는 "첫 실행 시 자동 설치" 항목으로만 안내한다.</summary>
    public const string FirstRunNote =
        "태그 모델(1GB)·LaMa·번역 모델은 첫 작업을 실행할 때 자동으로 내려받아요 (1회).";
}
