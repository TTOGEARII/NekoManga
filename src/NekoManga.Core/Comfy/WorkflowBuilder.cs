using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI API 포맷 워크플로 생성기 (채색·엔진 LaMa·태그 추출).
/// 자동채색 레시피는 kone.gg "만화 자동채색" 원본 설정을 따른다
/// (SDXL WAI-illustrious + ControlNet union lineart/scribble 2단 + WD14 태그 + 선화 luminosity 합성).</summary>
public static class WorkflowBuilder
{
    /// <summary>흑백 원고 1장을 자동채색하는 워크플로.
    /// 입력 이미지는 글자가 지워진 상태(번역 단계의 인페인트 결과)를 권장.</summary>
    public static JsonObject BuildAutoColor(AppSettings s, string uploadedName, string filenamePrefix,
        string? refImageName = null)
    {
        long seed = s.ColorSeed >= 0 ? s.ColorSeed : Random.Shared.NextInt64(0, uint.MaxValue);
        var manual = string.Join(", ",
            new[] { s.ColorBaseTags, s.ColorRefTags, s.ColorManualTags }
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimEnd(',')));

        var p = new JsonObject
        {
            ["1"] = Node("LoadImage", new() { ["image"] = uploadedName }),
            ["3"] = Node("ImageScaleToTotalPixels", new()
            {
                ["image"] = Link("1", 0),
                ["upscale_method"] = "lanczos",
                ["megapixels"] = s.ColorMegapixels,
                ["resolution_steps"] = 8,
            }),
            ["4"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = s.ColorCheckpoint }),
            ["5"] = Node("WD14Tagger|pysssss", new()
            {
                ["image"] = Link("3", 0),
                ["model"] = s.ColorTaggerModel,
                ["threshold"] = s.ColorTaggerThreshold,
                ["character_threshold"] = 0.90,
                ["replace_underscore"] = true,
                ["trailing_comma"] = false,
                ["exclude_tags"] = s.ColorExcludeTags,
            }),
            ["6"] = Node("StringFunction|pysssss", new()
            {
                ["action"] = "append",
                ["tidy_tags"] = "yes",
                ["text_a"] = manual,
                ["text_b"] = Link("5", 0),
                ["text_c"] = "",
            }),
            ["7"] = Node("ShowText|pysssss", new() { ["text"] = Link("6", 0) }),
            ["8"] = Node("CLIPTextEncode", new() { ["clip"] = Link("4", 1), ["text"] = Link("6", 0) }),
            ["9"] = Node("CLIPTextEncode", new() { ["clip"] = Link("4", 1), ["text"] = s.ColorNegative }),
            ["10"] = Node("ControlNetLoader", new() { ["control_net_name"] = s.ColorControlNet }),
            ["11"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("10", 0), ["type"] = "canny/lineart/anime_lineart/mlsd" }),
            ["12"] = Node("Manga2Anime_LineArt_Preprocessor", new() { ["image"] = Link("3", 0), ["resolution"] = s.PreprocResolution }),
            ["13"] = Node("ControlNetApplyAdvanced", new()
            {
                ["positive"] = Link("8", 0), ["negative"] = Link("9", 0),
                ["control_net"] = Link("11", 0), ["image"] = Link("12", 0),
                ["strength"] = s.CnLineartStrength, ["start_percent"] = 0.0, ["end_percent"] = s.CnLineartEnd, ["vae"] = Link("4", 2),
            }),
            ["14"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("10", 0), ["type"] = "hed/pidi/scribble/ted" }),
            ["15"] = Node("Scribble_XDoG_Preprocessor", new() { ["image"] = Link("3", 0), ["threshold"] = s.ScribbleXdogThreshold, ["resolution"] = s.PreprocResolution }),
            ["16"] = Node("ControlNetApplyAdvanced", new()
            {
                ["positive"] = Link("13", 0), ["negative"] = Link("13", 1),
                ["control_net"] = Link("14", 0), ["image"] = Link("15", 0),
                ["strength"] = s.CnScribbleStrength, ["start_percent"] = s.CnScribbleStart, ["end_percent"] = 1.0, ["vae"] = Link("4", 2),
            }),
            ["17"] = Node("VAEEncode", new() { ["pixels"] = Link("3", 0), ["vae"] = Link("4", 2) }),
            ["18"] = Node("KSampler", new()
            {
                ["model"] = refImageName != null ? Link("32", 0) : Link("4", 0),
                ["positive"] = Link("16", 0), ["negative"] = Link("16", 1),
                ["latent_image"] = Link("17", 0),
                ["seed"] = seed, ["steps"] = s.ColorSteps, ["cfg"] = s.ColorCfg,
                ["sampler_name"] = s.ColorSampler, ["scheduler"] = s.ColorScheduler,
                ["denoise"] = s.ColorDenoise,
            }),
            ["19"] = Node("VAEDecode", new() { ["samples"] = Link("18", 0), ["vae"] = Link("4", 2) }),
        };

        // 표지 정밀 매칭 (IPAdapter): 표지 이미지의 색·스타일을 모델에 직접 주입한다.
        // 태그보다 훨씬 정확하게 캐릭터 색이 맞는다. 모델/노드가 없으면 호출부에서 refImageName을 안 준다.
        if (refImageName != null)
        {
            p["30"] = Node("LoadImage", new() { ["image"] = refImageName });
            p["31"] = Node("IPAdapterUnifiedLoader", new()
            {
                ["model"] = Link("4", 0),
                ["preset"] = "PLUS (high strength)",
            });
            p["32"] = Node("IPAdapter", new()
            {
                ["model"] = Link("31", 0), ["ipadapter"] = Link("31", 1),
                ["image"] = Link("30", 0),
                ["weight"] = Math.Clamp(s.ColorRefStrength, 0.0, 1.5),
                ["start_at"] = 0.0, ["end_at"] = s.IpaEndAt,
                ["weight_type"] = "standard",
            });
        }

        string finalImage;
        if (s.ColorLineartOpacity > 0)
        {
            // 원본 선화를 luminosity로 얹어 채색이 선을 뭉개지 않게 한다.
            p["20"] = Node("LayerUtility: ImageBlend V2", new()
            {
                ["background_image"] = Link("19", 0),
                ["layer_image"] = Link("3", 0),
                ["invert_mask"] = true,
                ["blend_mode"] = "luminosity",
                ["opacity"] = s.ColorLineartOpacity,
            });
            finalImage = "20";
        }
        else finalImage = "19";

        p["21"] = Node("SaveImage", new() { ["images"] = Link(finalImage, 0), ["filename_prefix"] = filenamePrefix });
        return p;
    }

    // (강력 검열제거 BuildStrongDecensor·LoRA 헬퍼는 2026-10-19에 폐기. git 이력 c4adc65 참조.)

    /// <summary>엔진 LaMa 검열제거 — 사용자가 ComfyUI에서 직접 쓰던 원본 파이프라인의 노드 그대로
    /// (LayerUtility: LaMa, 'lama' 모델, CUDA, 마스크 확장·블러는 고급 옵션 — 기본 8·8).
    /// 업로드 PNG의 알파 0 영역이 마스크다 (ComfyUI LoadImage 규약).</summary>
    public static JsonObject BuildLamaDecensor(AppSettings s, string uploadedName, string filenamePrefix) => new()
    {
        ["1"] = Node("LoadImage", new() { ["image"] = uploadedName }),
        ["2"] = Node("LayerUtility: LaMa", new()
        {
            ["image"] = Link("1", 0), ["mask"] = Link("1", 1),
            ["lama_model"] = "lama", ["device"] = "cuda",
            ["invert_mask"] = false, ["mask_grow"] = s.LamaMaskGrow, ["mask_blur"] = s.LamaMaskBlur,
        }),
        ["3"] = Node("SaveImage", new() { ["images"] = Link("2", 0), ["filename_prefix"] = filenamePrefix }),
    };

    /// <summary>재생성 검열제거 — 사용자가 ComfyUI에서 직접 짠 "manga_restore_masked_uncensor_v2"(2026-10-20)의 복원 부분을 API 포맷으로 옮긴 것.
    /// 원본 그래프: LoadImage(알파=마스크) → MaskGrow 3종(LaMa용 4/4·엣지 12/8·코어 4/1) → LayerUtility:LaMa(lama, cuda, 0/0) 1차 복원
    /// → AIO_Preprocessor(Manga2Anime_LineArt, 1472) → CLIPTextEncode(기본 프롬프트 + 신체부위 태그 / 네거티브) → ControlNet 적용(0.85, 0~0.85)
    /// → VAEEncode(LaMa 결과) → KSampler(24, cfg 5, dpmpp_sde_gpu, karras, denoise 0.62) → VAEDecode
    /// → ImageCompositeMasked(LaMa 결과 위에 생성 결과를 엣지 마스크로) → ImageCompositeMasked(코어 마스크로) → SaveImage.
    /// 바꾼 점: ① 텍스트 결합(WAS Text Concatenate)·텍스트 노드(kaytool)는 C# 문자열 결합으로 대체(설치 의존 제거).
    /// ② ACN_AdvancedControlNetApply(comfyui-advanced-controlnet, 이 PC 미설치) → 코어 ControlNetApplyAdvanced(같은 strength/start/end)
    ///    + union 모델이면 SetUnionControlNetType(lineart) 힌트. ③ RegenMegapixels>0이면 보내기 전에 축소(VRAM), 결과는 호출부가 원본 크기로 되돌린다.</summary>
    public static JsonObject BuildRegenDecensor(AppSettings s, string uploadedName, string filenamePrefix)
    {
        long seed = s.RegenSeed >= 0 ? s.RegenSeed : Random.Shared.NextInt64(0, uint.MaxValue);
        var positive = string.Join(", ", new[] { s.RegenPrompt, s.RegenBodyTags }
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimEnd(',')));
        bool scale = s.RegenMegapixels > 0;
        string img = scale ? "3" : "1";   // 원본(또는 축소본) 이미지 노드
        string msk = scale ? "4" : "1";   // 마스크 노드 (축소본이면 마스크도 같은 크기로)
        var p = new JsonObject
        {
            ["1"] = Node("LoadImage", new() { ["image"] = uploadedName }),
            ["2"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = s.RegenCheckpoint }),
        };
        if (scale)
        {
            p["3"] = Node("ImageScaleToTotalPixels", new()
            {
                ["image"] = Link("1", 0), ["upscale_method"] = "lanczos", ["megapixels"] = s.RegenMegapixels, ["resolution_steps"] = 8,
            });
            // 마스크는 이미지로 바꿔 같은 크기로 줄인 뒤 다시 마스크로
            p["30"] = Node("MaskToImage", new() { ["mask"] = Link("1", 1) });
            p["31"] = Node("ImageScaleToTotalPixels", new()
            {
                ["image"] = Link("30", 0), ["upscale_method"] = "lanczos", ["megapixels"] = s.RegenMegapixels, ["resolution_steps"] = 8,
            });
            p["4"] = Node("ImageToMask", new() { ["image"] = Link("31", 0), ["channel"] = "red" });
        }
        // 마스크 3종 (사용자 워크플로의 LayerMask: MaskGrow 노드 그대로)
        p["5"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link(msk, scale ? 0 : 1), ["invert_mask"] = false, ["grow"] = s.RegenLamaGrow, ["blur"] = s.RegenLamaBlur });   // LaMa용
        p["6"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link(msk, scale ? 0 : 1), ["invert_mask"] = false, ["grow"] = s.RegenEdgeGrow, ["blur"] = s.RegenEdgeBlur });   // 엣지
        p["7"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link(msk, scale ? 0 : 1), ["invert_mask"] = false, ["grow"] = s.RegenCoreGrow, ["blur"] = s.RegenCoreBlur });   // 코어
        // LaMa 1차 복원
        p["8"] = Node("LayerUtility: LaMa", new()
        {
            ["image"] = Link(img, 0), ["mask"] = Link("5", 0),
            ["lama_model"] = "lama", ["device"] = "cuda", ["invert_mask"] = false, ["mask_grow"] = 0, ["mask_blur"] = 0,
        });
        // 선화 전처리 + 프롬프트 + ControlNet
        p["9"] = Node("AIO_Preprocessor", new() { ["image"] = Link("8", 0), ["preprocessor"] = "Manga2Anime_LineArt_Preprocessor", ["resolution"] = s.RegenPreprocResolution });
        p["10"] = Node("CLIPTextEncode", new() { ["clip"] = Link("2", 1), ["text"] = positive });
        p["11"] = Node("CLIPTextEncode", new() { ["clip"] = Link("2", 1), ["text"] = s.RegenNegative });
        p["12"] = Node("ControlNetLoader", new() { ["control_net_name"] = s.RegenControlNet });
        string cn = "12";
        if (s.RegenControlNet.Contains("union", StringComparison.OrdinalIgnoreCase) || s.RegenControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase))
        {
            p["13"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("12", 0), ["type"] = "canny/lineart/anime_lineart/mlsd" });
            cn = "13";
        }
        p["14"] = Node("ControlNetApplyAdvanced", new()
        {
            ["positive"] = Link("10", 0), ["negative"] = Link("11", 0),
            ["control_net"] = Link(cn, 0), ["image"] = Link("9", 0),
            ["strength"] = s.RegenCnStrength, ["start_percent"] = 0.0, ["end_percent"] = s.RegenCnEnd, ["vae"] = Link("2", 2),
        });
        // 전체 페이지 재생성 (img2img, LaMa 결과를 잠재로)
        p["15"] = Node("VAEEncode", new() { ["pixels"] = Link("8", 0), ["vae"] = Link("2", 2) });
        p["16"] = Node("KSampler", new()
        {
            ["model"] = Link("2", 0), ["positive"] = Link("14", 0), ["negative"] = Link("14", 1), ["latent_image"] = Link("15", 0),
            ["seed"] = seed, ["steps"] = s.RegenSteps, ["cfg"] = s.RegenCfg,
            ["sampler_name"] = s.RegenSampler, ["scheduler"] = s.RegenScheduler, ["denoise"] = s.RegenDenoise,
        });
        p["17"] = Node("VAEDecode", new() { ["samples"] = Link("16", 0), ["vae"] = Link("2", 2) });
        // 합성: LaMa 결과 위에 생성 결과를 엣지 마스크(부드럽게) → 코어 마스크로
        p["18"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("8", 0), ["source"] = Link("17", 0), ["mask"] = Link("6", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
        p["19"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("18", 0), ["source"] = Link("17", 0), ["mask"] = Link("7", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
        p["20"] = Node("SaveImage", new() { ["images"] = Link("19", 0), ["filename_prefix"] = filenamePrefix });
        return p;
    }

    /// <summary>표지/참고 일러스트에서 WD14 태그만 뽑는 미니 워크플로 (ShowText로 결과 수거).</summary>
    public static JsonObject BuildTagExtract(AppSettings s, string uploadedName) => new()
    {
        ["1"] = Node("LoadImage", new() { ["image"] = uploadedName }),
        ["2"] = Node("WD14Tagger|pysssss", new()
        {
            ["image"] = Link("1", 0),
            ["model"] = s.ColorTaggerModel,
            ["threshold"] = Math.Min(0.3, s.ColorTaggerThreshold),  // 색 태그는 놓치지 않게 낮게
            ["character_threshold"] = 0.90,
            ["replace_underscore"] = true,
            ["trailing_comma"] = false,
            ["exclude_tags"] = "",
        }),
        ["3"] = Node("ShowText|pysssss", new() { ["text"] = Link("2", 0) }),
    };

    private static JsonObject Node(string classType, JsonObject inputs) =>
        new() { ["class_type"] = classType, ["inputs"] = inputs };

    private static JsonArray Link(string nodeId, int output) => new(nodeId, output);
}
