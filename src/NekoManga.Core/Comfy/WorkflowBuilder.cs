using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI API 포맷 워크플로 생성기 (채색·재생성·태그 추출).
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

    // (강력 검열제거 BuildStrongDecensor·LoRA 헬퍼는 2026-10-19에, 엔진 LaMa BuildLamaDecensor(LayerUtility: LaMa 한 노드)는 2026-11-12 사용자 선택으로 폐기. git 이력 참조.)

    /// <summary>재생성 v3 (2026-11-12) — 영역 하나를 자른 그림에 SDXL 두 단계, LaMa 없음. 호출자가 영역마다 창을 잘라(RegenCrop) 두 PNG를 올린다:
    /// <paramref name="imgUpload"/>(알파 = 넓힌 부위 영역) · <paramref name="eraseUpload"/>(같은 그림, 알파 = 검열 조각만).
    /// 1단계: 조각을 노이즈 마스크(MaskGrow RegenInpaintGrow/2)로 디노이즈 1.0 인페인트 — 선화 ControlNet 입력은 원고 선화에서 조각 자리를 검게 비운 것(막대 테두리를 선으로 읽지 않게).
    /// 2단계: 1단계 합성 결과에서 선화를 다시 뽑고, 영역(MaskGrow 0/4)을 노이즈 마스크로 RegenDenoise로 다듬어 주변과 잇는다. 합성은 엣지·코어 마스크(사용자 레시피 값).
    /// LaMa로 메운 흐릿한 덩어리에서 출발하던 v2가 "주변과 무관한 그림"을 내던 것을 사용자 결정으로 바꿨다("SDXL 때는 LaMa를 안 쓰는 게 좋지 않아?").
    /// 코어 노드 + 설치된 LayerStyle(MaskGrow)·controlnet_aux(AIO_Preprocessor)만 쓴다. w·h는 보내는 그림 크기(EmptyImage).</summary>
    public static JsonObject BuildRegenCrop(AppSettings s, string imgUpload, string eraseUpload, string filenamePrefix, string bodyTags, int w, int h)
    {
        long seed = s.RegenSeed >= 0 ? s.RegenSeed : Random.Shared.NextInt64(0, uint.MaxValue);
        var positive = string.Join(", ", new[] { s.RegenPrompt, bodyTags }
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimEnd(',')));
        var p = new JsonObject
        {
            ["1"] = Node("LoadImage", new() { ["image"] = imgUpload }),      // 그림 + 알파 = 영역
            ["2"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = s.RegenCheckpoint }),
            ["21"] = Node("LoadImage", new() { ["image"] = eraseUpload }),   // 알파 = 검열 조각
        };
        // 제목(_meta.title): API JSON으로 내보내 커스텀 워크플로로 쓸 때 규약(CustomWorkflow.PickNode)이 그림 노드를 앱 입력으로 고르게
        p["1"]!["_meta"] = new JsonObject { ["title"] = "NekoManga 입력 (알파 = 다시 그릴 영역)" };
        p["21"]!["_meta"] = new JsonObject { ["title"] = "지우기 마스크 (알파 = 검열 조각)" };
        // 마스크: 지우기(1단계 노이즈), 영역 부드럽게(2단계 노이즈), 엣지·코어(합성)
        p["5"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link("21", 1), ["invert_mask"] = false, ["grow"] = s.RegenInpaintGrow, ["blur"] = 2 });
        p["23"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link("1", 1), ["invert_mask"] = false, ["grow"] = 0, ["blur"] = 4 });
        p["6"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link("1", 1), ["invert_mask"] = false, ["grow"] = s.RegenEdgeGrow, ["blur"] = s.RegenEdgeBlur });
        p["7"] = Node("LayerMask: MaskGrow", new() { ["mask"] = Link("1", 1), ["invert_mask"] = false, ["grow"] = s.RegenCoreGrow, ["blur"] = s.RegenCoreBlur });
        // 프롬프트·ControlNet
        p["10"] = Node("CLIPTextEncode", new() { ["clip"] = Link("2", 1), ["text"] = positive });
        p["11"] = Node("CLIPTextEncode", new() { ["clip"] = Link("2", 1), ["text"] = s.RegenNegative });
        p["12"] = Node("ControlNetLoader", new() { ["control_net_name"] = s.RegenControlNet });
        string cn = "12";
        bool union = s.RegenControlNet.Contains("union", StringComparison.OrdinalIgnoreCase) || s.RegenControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase);
        if (union)
        {
            p["13"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("12", 0), ["type"] = "canny/lineart/anime_lineart/mlsd" });
            cn = "13";
        }
        JsonObject CnApply(string imageNode, string cnNode, double strength) => Node("ControlNetApplyAdvanced", new()
        {
            ["positive"] = Link("10", 0), ["negative"] = Link("11", 0),
            ["control_net"] = Link(cnNode, 0), ["image"] = Link(imageNode, 0),
            ["strength"] = strength, ["start_percent"] = 0.0, ["end_percent"] = s.RegenCnEnd, ["vae"] = Link("2", 2),
        });
        JsonObject Sampler(string condNode, string latentNode, long sd, double denoise) => Node("KSampler", new()
        {
            ["model"] = Link("2", 0), ["positive"] = Link(condNode, 0), ["negative"] = Link(condNode, 1), ["latent_image"] = Link(latentNode, 0),
            ["seed"] = sd, ["steps"] = s.RegenSteps, ["cfg"] = s.RegenCfg,
            ["sampler_name"] = s.RegenSampler, ["scheduler"] = s.RegenScheduler, ["denoise"] = denoise,
        });
        // ── 1단계: 조각 인페인트 (디노이즈 1.0) ──
        // 제어 그림은 조각 자리를 검게 비운 것. 기본(Lineart)은 원고 선화에 검게 칠한 것을 선화 종류로 — 조각 자리를 "선 없음"으로 밀어
        // 맞닿은 남녀 부위에서 막대 밑 부위가 평평하게 지워지는 보고(2026-11-12)가 있어 강도만 따로(RegenInpaintCnStrength) 둔다.
        // 'Repaint'(union promax repaint 종류에 원고 그대로)는 그 보고에 시도했으나 사용자 실측으로 검게 뭉개져(검은 선 많은 원고일수록) 실험용 선택지로만 남긴다
        bool repaint = union && s.RegenInpaintControl == "Repaint";
        p["24"] = Node("EmptyImage", new() { ["width"] = w, ["height"] = h, ["batch_size"] = 1, ["color"] = 0 });
        if (repaint)
        {
            p["34"] = Node("SetUnionControlNetType", new() { ["control_net"] = Link("12", 0), ["type"] = "repaint" });
            p["25"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("1", 0), ["source"] = Link("24", 0), ["mask"] = Link("5", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = true });
            p["14"] = CnApply("25", "34", s.RegenInpaintCnStrength);
        }
        else
        {
            p["9"] = Node("AIO_Preprocessor", new() { ["image"] = Link("1", 0), ["preprocessor"] = "Manga2Anime_LineArt_Preprocessor", ["resolution"] = s.RegenPreprocResolution });
            p["25"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("9", 0), ["source"] = Link("24", 0), ["mask"] = Link("5", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = true });
            p["14"] = CnApply("25", cn, s.RegenInpaintCnStrength);
        }
        p["15"] = Node("VAEEncode", new() { ["pixels"] = Link("1", 0), ["vae"] = Link("2", 2) });
        p["26"] = Node("SetLatentNoiseMask", new() { ["samples"] = Link("15", 0), ["mask"] = Link("5", 0) });
        p["16"] = Sampler("14", "26", seed, 1.0);
        p["17"] = Node("VAEDecode", new() { ["samples"] = Link("16", 0), ["vae"] = Link("2", 2) });
        p["27"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("1", 0), ["source"] = Link("17", 0), ["mask"] = Link("5", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
        // ── 2단계: 영역 다듬기 (디노이즈 설정값) ──
        p["28"] = Node("AIO_Preprocessor", new() { ["image"] = Link("27", 0), ["preprocessor"] = "Manga2Anime_LineArt_Preprocessor", ["resolution"] = s.RegenPreprocResolution });
        p["29"] = CnApply("28", cn, s.RegenCnStrength);
        p["30"] = Node("VAEEncode", new() { ["pixels"] = Link("27", 0), ["vae"] = Link("2", 2) });
        p["31"] = Node("SetLatentNoiseMask", new() { ["samples"] = Link("30", 0), ["mask"] = Link("23", 0) });
        p["32"] = Sampler("29", "31", seed + 1, s.RegenDenoise);
        p["33"] = Node("VAEDecode", new() { ["samples"] = Link("32", 0), ["vae"] = Link("2", 2) });
        // 합성: 1단계 결과 위에 2단계 결과를 엣지(부드럽게) → 코어로
        p["18"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("27", 0), ["source"] = Link("33", 0), ["mask"] = Link("6", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
        p["19"] = Node("ImageCompositeMasked", new() { ["destination"] = Link("18", 0), ["source"] = Link("33", 0), ["mask"] = Link("7", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
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
