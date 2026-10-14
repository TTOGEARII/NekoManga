using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI API 포맷 워크플로 생성기.
/// 자동채색 레시피는 kone.gg "만화 자동채색" 원본 설정을 따른다
/// (SDXL WAI-illustrious + ControlNet union lineart/scribble 2단 + WD14 태그 + 선화 luminosity 합성).</summary>
public static class WorkflowBuilder
{
    /// <summary>흑백 원고 1장을 자동채색하는 워크플로.
    /// 입력 이미지는 글자가 지워진 상태(번역 단계의 인페인트 결과)를 권장.</summary>
    public static JsonObject BuildAutoColor(AppSettings s, string uploadedName, string filenamePrefix)
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
            ["4"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = "waiIllustriousSDXL_v170.safetensors" }),
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
            ["10"] = Node("ControlNetLoader", new() { ["control_net_name"] = "controlnet-union-sdxl-promax.safetensors" }),
            ["11"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("10", 0), ["type"] = "canny/lineart/anime_lineart/mlsd" }),
            ["12"] = Node("Manga2Anime_LineArt_Preprocessor", new() { ["image"] = Link("3", 0), ["resolution"] = 1280 }),
            ["13"] = Node("ControlNetApplyAdvanced", new()
            {
                ["positive"] = Link("8", 0), ["negative"] = Link("9", 0),
                ["control_net"] = Link("11", 0), ["image"] = Link("12", 0),
                ["strength"] = 1.0, ["start_percent"] = 0.0, ["end_percent"] = 0.6, ["vae"] = Link("4", 2),
            }),
            ["14"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("10", 0), ["type"] = "hed/pidi/scribble/ted" }),
            ["15"] = Node("Scribble_XDoG_Preprocessor", new() { ["image"] = Link("3", 0), ["threshold"] = 32, ["resolution"] = 1280 }),
            ["16"] = Node("ControlNetApplyAdvanced", new()
            {
                ["positive"] = Link("13", 0), ["negative"] = Link("13", 1),
                ["control_net"] = Link("14", 0), ["image"] = Link("15", 0),
                ["strength"] = 1.0, ["start_percent"] = 0.6, ["end_percent"] = 1.0, ["vae"] = Link("4", 2),
            }),
            ["17"] = Node("VAEEncode", new() { ["pixels"] = Link("3", 0), ["vae"] = Link("4", 2) }),
            ["18"] = Node("KSampler", new()
            {
                ["model"] = Link("4", 0), ["positive"] = Link("16", 0), ["negative"] = Link("16", 1),
                ["latent_image"] = Link("17", 0),
                ["seed"] = seed, ["steps"] = s.ColorSteps, ["cfg"] = s.ColorCfg,
                ["sampler_name"] = s.ColorSampler, ["scheduler"] = s.ColorScheduler,
                ["denoise"] = s.ColorDenoise,
            }),
            ["19"] = Node("VAEDecode", new() { ["samples"] = Link("18", 0), ["vae"] = Link("4", 2) }),
        };

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

    /// <summary>강력 검열제거: 검열 영역을 SDXL 인페인트로 재생성한다 (ComfyUI 필요).
    /// 업로드 PNG의 알파 채널이 마스크다 (알파 0 = 재생성 영역, ComfyUI LoadImage 규약).
    /// SetLatentNoiseMask로 마스크 영역만 다시 그리고, 최종적으로 마스크 영역만 원본에 합성해
    /// 나머지 픽셀은 1비트도 바뀌지 않게 한다.</summary>
    public static JsonObject BuildStrongDecensor(AppSettings s, string uploadedName, string filenamePrefix,
        long seed)
    {
        return new JsonObject
        {
            ["1"] = Node("LoadImage", new() { ["image"] = uploadedName }),
            ["2"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = "waiIllustriousSDXL_v170.safetensors" }),
            ["3"] = Node("CLIPTextEncode", new()
            {
                ["clip"] = Link("2", 1),
                ["text"] = "anime, high quality, detailed, clean, best quality, uncensored",
            }),
            ["4"] = Node("CLIPTextEncode", new()
            {
                ["clip"] = Link("2", 1),
                ["text"] = "mosaic, pixelated, blurry, censored, bar censor, low quality, worst quality",
            }),
            ["5"] = Node("VAEEncode", new() { ["pixels"] = Link("1", 0), ["vae"] = Link("2", 2) }),
            ["6"] = Node("SetLatentNoiseMask", new() { ["samples"] = Link("5", 0), ["mask"] = Link("1", 1) }),
            ["7"] = Node("KSampler", new()
            {
                ["model"] = Link("2", 0), ["positive"] = Link("3", 0), ["negative"] = Link("4", 0),
                ["latent_image"] = Link("6", 0),
                ["seed"] = seed, ["steps"] = 24, ["cfg"] = 6.0,
                ["sampler_name"] = "dpmpp_2m", ["scheduler"] = "karras",
                ["denoise"] = s.DecensorStrongDenoise,
            }),
            ["8"] = Node("VAEDecode", new() { ["samples"] = Link("7", 0), ["vae"] = Link("2", 2) }),
            ["9"] = Node("GrowMask", new() { ["mask"] = Link("1", 1), ["expand"] = 4, ["tapered_corners"] = true }),
            ["10"] = Node("ImageCompositeMasked", new()
            {
                ["destination"] = Link("1", 0), ["source"] = Link("8", 0),
                ["x"] = 0, ["y"] = 0, ["resize_source"] = false, ["mask"] = Link("9", 0),
            }),
            ["11"] = Node("SaveImage", new() { ["images"] = Link("10", 0), ["filename_prefix"] = filenamePrefix }),
        };
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
