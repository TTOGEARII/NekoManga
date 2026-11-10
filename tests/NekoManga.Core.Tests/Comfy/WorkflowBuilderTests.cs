using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;
using static NekoManga.Core.Tests.ComfyTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>WorkflowBuilder — 설정값이 ComfyUI API 그래프의 알맞은 노드·입력에 실리고, 노드 사이 연결이 끊기지 않는지.
/// 노드 id 번호는 구현 세부라서 보지 않고, 노드 종류와 링크를 따라가 찾는다.</summary>
public class WorkflowBuilderTests
{
    // ───────── 자동채색 ─────────

    [Fact]
    public void BuildAutoColor_WithoutReference_SamplesCheckpointModelWithoutIpAdapter()
    {
        var g = WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 7 }, "nm_cl_0.png", "nm_color_0");

        // 표지가 없으면(호출부가 refImageName을 주지 않으면) IPAdapter 없이 체크포인트 모델로 바로 샘플링한다
        var (_, sampler) = Only(g, "KSampler");
        Assert.Equal("CheckpointLoaderSimple", ClassType(Source(g, Inputs(sampler)["model"])));
        Assert.Empty(OfType(g, "IPAdapter"));
        Assert.Empty(OfType(g, "IPAdapterUnifiedLoader"));
        // 그림 입력은 업로드한 원고 하나뿐
        var (_, load) = Only(g, "LoadImage");
        Assert.Equal("nm_cl_0.png", Str(Inputs(load)["image"]));
    }

    [Fact]
    public void BuildAutoColor_WithReference_RoutesSamplerModelThroughIpAdapter()
    {
        var s = new AppSettings { ColorSeed = 7, ColorRefStrength = 0.8, IpaEndAt = 0.7 };
        var g = WorkflowBuilder.BuildAutoColor(s, "nm_cl_0.png", "nm_color_0", "nm_cover_ref.png");

        // 표지 색·스타일은 모델에 직접 주입된다: 체크포인트 → IPAdapter 통합 로더 → IPAdapter → KSampler
        var (_, sampler) = Only(g, "KSampler");
        var ipa = Source(g, Inputs(sampler)["model"]);
        Assert.Equal("IPAdapter", ClassType(ipa));
        var loader = Source(g, Inputs(ipa)["model"]);
        Assert.Equal("IPAdapterUnifiedLoader", ClassType(loader));
        Assert.Equal("CheckpointLoaderSimple", ClassType(Source(g, Inputs(loader)["model"])));
        // ipadapter 입력은 같은 통합 로더의 두 번째 출력(IPADAPTER)
        Assert.Equal((LinkOf(Inputs(ipa)["model"]).Id, 1), LinkOf(Inputs(ipa)["ipadapter"]));
        Assert.Equal(0.8, Num(Inputs(ipa)["weight"]));
        Assert.Equal(0.7, Num(Inputs(ipa)["end_at"]));

        // 표지는 원고와 다른 LoadImage로 들어가고, 원고 쪽 입력은 그대로 업로드한 페이지다
        Assert.Equal("nm_cover_ref.png", Str(Inputs(Source(g, Inputs(ipa)["image"]))["image"]));
        var (_, scale) = Only(g, "ImageScaleToTotalPixels");
        Assert.Equal("nm_cl_0.png", Str(Inputs(Source(g, Inputs(scale)["image"]))["image"]));
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.2, 1.2)]
    [InlineData(3.0, 1.5)]
    public void BuildAutoColor_ReferenceStrength_IsClampedToZeroToOnePointFive(double strength, double expected)
    {
        var s = new AppSettings { ColorSeed = 7, ColorRefStrength = strength };
        var g = WorkflowBuilder.BuildAutoColor(s, "in.png", "out", "ref.png");

        // 설정 문서가 정한 범위(0~1.5) 밖의 값은 경계값으로 잘라 보낸다
        Assert.Equal(expected, Num(Inputs(Only(g, "IPAdapter").Node)["weight"]));
    }

    [Fact]
    public void BuildAutoColor_SamplerAndModelSettings_ReachTheirNodes()
    {
        var s = new AppSettings
        {
            ColorSteps = 31, ColorCfg = 6.5, ColorSampler = "euler_ancestral", ColorScheduler = "normal", ColorDenoise = 0.75,
            ColorSeed = 0, ColorMegapixels = 2.3, ColorCheckpoint = "내 체크포인트 v2.safetensors",
            ColorNegative = "lowres, bad hands", ColorTaggerModel = "wd-v1-4-moat-tagger-v2", ColorTaggerThreshold = 0.42,
            ColorExcludeTags = "monochrome, greyscale",
        };
        var g = WorkflowBuilder.BuildAutoColor(s, "in.png", "out");

        var k = Inputs(Only(g, "KSampler").Node);
        Assert.Equal(31, Num(k["steps"]));
        Assert.Equal(6.5, Num(k["cfg"]));
        Assert.Equal("euler_ancestral", Str(k["sampler_name"]));
        Assert.Equal("normal", Str(k["scheduler"]));
        Assert.Equal(0.75, Num(k["denoise"]));
        // 0도 고정 시드다 — 무작위는 음수(-1)일 때만
        Assert.Equal(0, Num(k["seed"]));
        Assert.Equal("내 체크포인트 v2.safetensors", Str(Inputs(Source(g, k["model"]))["ckpt_name"]));
        // img2img의 잠재는 설정한 메가픽셀로 줄인 원고에서
        var encode = Source(g, k["latent_image"]);
        Assert.Equal("VAEEncode", ClassType(encode));
        var scale = Source(g, Inputs(encode)["pixels"]);
        Assert.Equal("ImageScaleToTotalPixels", ClassType(scale));
        Assert.Equal(2.3, Num(Inputs(scale)["megapixels"]));

        var tagger = Inputs(Only(g, "WD14Tagger|pysssss").Node);
        Assert.Equal("wd-v1-4-moat-tagger-v2", Str(tagger["model"]));
        Assert.Equal(0.42, Num(tagger["threshold"]));
        Assert.Equal("monochrome, greyscale", Str(tagger["exclude_tags"]));

        // 네거티브는 KSampler의 negative를 ControlNet 두 단 너머까지 거슬러 올라간 인코더에 실린다
        var stage2 = Source(g, k["negative"]);
        var stage1 = Source(g, Inputs(stage2)["negative"]);
        Assert.Equal("lowres, bad hands", Str(Inputs(Source(g, Inputs(stage1)["negative"]))["text"]));
    }

    [Fact]
    public void BuildAutoColor_TwoControlNetStages_UseTheirOwnStrengthAndRange()
    {
        var s = new AppSettings
        {
            ColorSeed = 7, ColorControlNet = "my-union.safetensors",
            CnLineartStrength = 0.7, CnLineartEnd = 0.5, CnScribbleStrength = 0.4, CnScribbleStart = 0.3,
            PreprocResolution = 1024, ScribbleXdogThreshold = 40,
        };
        var g = WorkflowBuilder.BuildAutoColor(s, "in.png", "out");
        var k = Inputs(Only(g, "KSampler").Node);

        // KSampler 쪽이 2단(scribble), 그 앞이 1단(lineart) — 각 단은 앞 단의 positive·negative 두 출력을 이어받는다
        var (scribbleId, _) = LinkOf(k["positive"]);
        Assert.Equal((scribbleId, 1), LinkOf(k["negative"]));
        var scribbleNode = Assert.IsType<JsonObject>(g[scribbleId]);
        Assert.Equal("ControlNetApplyAdvanced", ClassType(scribbleNode));
        var scribble = Inputs(scribbleNode);
        var (lineartId, _) = LinkOf(scribble["positive"]);
        Assert.Equal((lineartId, 1), LinkOf(scribble["negative"]));
        var lineartNode = Assert.IsType<JsonObject>(g[lineartId]);
        Assert.Equal("ControlNetApplyAdvanced", ClassType(lineartNode));
        var lineart = Inputs(lineartNode);

        // 1단: 선화 힌트, 0부터 설정한 끝 지점까지
        Assert.Equal(0.7, Num(lineart["strength"]));
        Assert.Equal(0.0, Num(lineart["start_percent"]));
        Assert.Equal(0.5, Num(lineart["end_percent"]));
        var lineHint = Source(g, lineart["image"]);
        Assert.Equal("Manga2Anime_LineArt_Preprocessor", ClassType(lineHint));
        Assert.Equal(1024, Num(Inputs(lineHint)["resolution"]));
        Assert.Equal("canny/lineart/anime_lineart/mlsd", Str(Inputs(Source(g, lineart["control_net"]))["type"]));

        // 2단: 스케치 힌트, 설정한 시작 지점부터 끝까지
        Assert.Equal(0.4, Num(scribble["strength"]));
        Assert.Equal(0.3, Num(scribble["start_percent"]));
        Assert.Equal(1.0, Num(scribble["end_percent"]));
        var scribbleHint = Source(g, scribble["image"]);
        Assert.Equal("Scribble_XDoG_Preprocessor", ClassType(scribbleHint));
        Assert.Equal(40, Num(Inputs(scribbleHint)["threshold"]));
        Assert.Equal(1024, Num(Inputs(scribbleHint)["resolution"]));
        Assert.Equal("hed/pidi/scribble/ted", Str(Inputs(Source(g, scribble["control_net"]))["type"]));

        // 두 단 모두 같은 union ControlNet 하나에서 갈라진다
        var (loaderId, loader) = Only(g, "ControlNetLoader");
        Assert.Equal("my-union.safetensors", Str(Inputs(loader)["control_net_name"]));
        var unionTypes = OfType(g, "SetUnionControlNetType");
        Assert.Equal(2, unionTypes.Count);
        Assert.All(unionTypes, u => Assert.Equal((loaderId, 0), LinkOf(Inputs(u.Node)["control_net"])));
    }

    [Theory]
    [InlineData("masterpiece, best quality,", "  pink hair, blue eyes  ", "", "masterpiece, best quality, pink hair, blue eyes")]
    [InlineData("full color", "   ", "twintails,,", "full color, twintails")]
    [InlineData("", "", "", "")]
    public void BuildAutoColor_TagFields_JoinedSkippingBlanksAndTrailingCommas(string baseTags, string refTags, string manualTags, string expected)
    {
        var s = new AppSettings { ColorSeed = 7, ColorBaseTags = baseTags, ColorRefTags = refTags, ColorManualTags = manualTags };
        var g = WorkflowBuilder.BuildAutoColor(s, "in.png", "out");

        // 기본·표지·수동 태그는 빈 칸을 빼고 ", "로 이어 WD14 태그 앞(text_a)에 붙는다 — 쉼표가 겹치거나 끝에 남지 않게
        var (joinId, join) = Only(g, "StringFunction|pysssss");
        Assert.Equal(expected, Str(Inputs(join)["text_a"]));
        Assert.Equal("WD14Tagger|pysssss", ClassType(Source(g, Inputs(join)["text_b"])));
        // 이어 붙인 결과가 포지티브 인코더로 간다 (KSampler ← 2단 ← 1단 ← 인코더)
        var k = Inputs(Only(g, "KSampler").Node);
        var stage1 = Source(g, Inputs(Source(g, k["positive"]))["positive"]);
        var positive = Source(g, Inputs(stage1)["positive"]);
        Assert.Equal((joinId, 0), LinkOf(Inputs(positive)["text"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildAutoColor_NonPositiveLineartOpacity_SavesDecodedImageDirectly(int opacity)
    {
        var g = WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 7, ColorLineartOpacity = opacity }, "in.png", "nm_color_3");

        // 불투명도가 0 이하면 선화 합성을 하지 않는다 — 디코드 결과를 그대로 저장
        var (_, save) = Only(g, "SaveImage");
        Assert.Equal("VAEDecode", ClassType(Source(g, Inputs(save)["images"])));
        Assert.Empty(OfType(g, "LayerUtility: ImageBlend V2"));
    }

    [Fact]
    public void BuildAutoColor_PositiveLineartOpacity_BlendsPageLinesOverDecodedImage()
    {
        var g = WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 7, ColorLineartOpacity = 35 }, "in.png", "nm_color_3");

        // 원고(축소본)의 선화를 luminosity로 디코드 결과 위에 얹어 채색이 선을 뭉개지 않게 하고, 그 결과를 저장한다
        var (_, save) = Only(g, "SaveImage");
        var blend = Source(g, Inputs(save)["images"]);
        Assert.Equal("LayerUtility: ImageBlend V2", ClassType(blend));
        var b = Inputs(blend);
        Assert.Equal("VAEDecode", ClassType(Source(g, b["background_image"])));
        Assert.Equal("ImageScaleToTotalPixels", ClassType(Source(g, b["layer_image"])));
        Assert.Equal("luminosity", Str(b["blend_mode"]));
        Assert.Equal(35, Num(b["opacity"]));
        Assert.Equal("nm_color_3", Str(Inputs(save)["filename_prefix"]));
    }

    // ───────── 엔진 LaMa 검열 제거 ─────────

    [Fact]
    public void BuildLamaDecensor_UploadAlphaIsMaskAndSettingsSetGrowAndBlur()
    {
        var g = WorkflowBuilder.BuildLamaDecensor(new AppSettings { LamaMaskGrow = 13, LamaMaskBlur = 3 }, "nm_lama_2.png", "nm_lamaout_2");

        var (loadId, load) = Only(g, "LoadImage");
        Assert.Equal("nm_lama_2.png", Str(Inputs(load)["image"]));
        // 업로드 PNG의 알파가 마스크다 — LoadImage의 두 번째 출력(MASK)을 뒤집지 않고 LaMa에 준다
        var (lamaId, lama) = Only(g, "LayerUtility: LaMa");
        var l = Inputs(lama);
        Assert.Equal((loadId, 0), LinkOf(l["image"]));
        Assert.Equal((loadId, 1), LinkOf(l["mask"]));
        Assert.False(Bool(l["invert_mask"]));
        Assert.Equal(13, Num(l["mask_grow"]));
        Assert.Equal(3, Num(l["mask_blur"]));
        var (_, save) = Only(g, "SaveImage");
        Assert.Equal((lamaId, 0), LinkOf(Inputs(save)["images"]));
        Assert.Equal("nm_lamaout_2", Str(Inputs(save)["filename_prefix"]));
    }

    // ───────── 재생성 검열 제거 ─────────

    [Fact]
    public void BuildRegenDecensor_WithoutScaling_FeedsUploadedImageAndMaskDirectly()
    {
        var g = WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1, RegenMegapixels = 0 }, "nm_regen_0.png", "out");

        // 0이면 원본 해상도 그대로(사용자 워크플로) — 축소·마스크 변환 노드가 없다
        Assert.Empty(OfType(g, "ImageScaleToTotalPixels"));
        Assert.Empty(OfType(g, "MaskToImage"));
        Assert.Empty(OfType(g, "ImageToMask"));
        var (loadId, load) = Only(g, "LoadImage");
        Assert.Equal("nm_regen_0.png", Str(Inputs(load)["image"]));
        // LaMa는 업로드한 그림을, 마스크 3종은 업로드 PNG의 알파(LoadImage 두 번째 출력)를 바로 쓴다
        Assert.Equal((loadId, 0), LinkOf(Inputs(Only(g, "LayerUtility: LaMa").Node)["image"]));
        var grows = OfType(g, "LayerMask: MaskGrow");
        Assert.Equal(3, grows.Count);
        Assert.All(grows, m => Assert.Equal((loadId, 1), LinkOf(Inputs(m.Node)["mask"])));
    }

    [Fact]
    public void BuildRegenDecensor_WithScaling_ShrinksImageAndMaskToTheSameSize()
    {
        var g = WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1, RegenMegapixels = 1.5 }, "nm_regen_0.png", "out");
        var (loadId, _) = Only(g, "LoadImage");

        // 그림: 업로드 → 설정한 메가픽셀로 축소 → LaMa
        var imageScale = Source(g, Inputs(Only(g, "LayerUtility: LaMa").Node)["image"]);
        Assert.Equal("ImageScaleToTotalPixels", ClassType(imageScale));
        Assert.Equal((loadId, 0), LinkOf(Inputs(imageScale)["image"]));
        Assert.Equal(1.5, Num(Inputs(imageScale)["megapixels"]));

        // 마스크: 업로드 알파 → 이미지로 → 그림과 같은 조건으로 축소 → 다시 마스크 → 마스크 3종 모두
        var grows = OfType(g, "LayerMask: MaskGrow");
        Assert.Equal(3, grows.Count);
        var toMaskLink = LinkOf(Inputs(grows[0].Node)["mask"]);
        Assert.Equal(0, toMaskLink.Slot);
        Assert.All(grows, m => Assert.Equal(toMaskLink, LinkOf(Inputs(m.Node)["mask"])));
        var toMask = Source(g, Inputs(grows[0].Node)["mask"]);
        Assert.Equal("ImageToMask", ClassType(toMask));
        var maskScale = Source(g, Inputs(toMask)["image"]);
        Assert.Equal("ImageScaleToTotalPixels", ClassType(maskScale));
        Assert.Equal(Num(Inputs(imageScale)["megapixels"]), Num(Inputs(maskScale)["megapixels"]));
        Assert.Equal(Num(Inputs(imageScale)["resolution_steps"]), Num(Inputs(maskScale)["resolution_steps"]));
        var maskToImage = Source(g, Inputs(maskScale)["image"]);
        Assert.Equal("MaskToImage", ClassType(maskToImage));
        Assert.Equal((loadId, 1), LinkOf(Inputs(maskToImage)["mask"]));
    }

    [Fact]
    public void BuildRegenDecensor_MaskGrowSettings_ReachLamaEdgeAndCoreSteps()
    {
        var s = new AppSettings
        {
            RegenSeed = 1,
            RegenLamaGrow = 11, RegenLamaBlur = 12, RegenEdgeGrow = 21, RegenEdgeBlur = 22, RegenCoreGrow = 31, RegenCoreBlur = 32,
        };
        var g = WorkflowBuilder.BuildRegenDecensor(s, "in.png", "out");

        // LaMa 1차 복원은 LaMa용 마스크로
        var (lamaId, lama) = Only(g, "LayerUtility: LaMa");
        AssertMaskGrow(Source(g, Inputs(lama)["mask"]), 11, 12);
        // 합성 1단: LaMa 결과 위에 생성 결과를 엣지 마스크로
        var composites = OfType(g, "ImageCompositeMasked");
        Assert.Equal(2, composites.Count);
        var edge = Assert.Single(composites, c => LinkOf(Inputs(c.Node)["destination"]).Id == lamaId);
        AssertMaskGrow(Source(g, Inputs(edge.Node)["mask"]), 21, 22);
        // 합성 2단: 1단 결과 위에 코어 마스크로 — 이것이 저장된다
        var core = Assert.Single(composites, c => LinkOf(Inputs(c.Node)["destination"]).Id == edge.Id);
        AssertMaskGrow(Source(g, Inputs(core.Node)["mask"]), 31, 32);
        Assert.Equal((core.Id, 0), LinkOf(Inputs(Only(g, "SaveImage").Node)["images"]));
        // 두 합성 모두 같은 생성 결과(VAEDecode)를 얹는다
        Assert.All(composites, c => Assert.Equal("VAEDecode", ClassType(Source(g, Inputs(c.Node)["source"]))));

        static void AssertMaskGrow(JsonObject maskGrow, int grow, int blur)
        {
            Assert.Equal("LayerMask: MaskGrow", ClassType(maskGrow));
            Assert.Equal(grow, Num(Inputs(maskGrow)["grow"]));
            Assert.Equal(blur, Num(Inputs(maskGrow)["blur"]));
            Assert.False(Bool(Inputs(maskGrow)["invert_mask"]));
        }
    }

    [Theory]
    [InlineData("controlnet-union-sdxl-promax.safetensors", true)]
    [InlineData("diffusion_pytorch_model_promax.safetensors", true)]
    [InlineData("xinsir-UNION-sdxl-1.0.safetensors", true)]
    [InlineData("mistoLine_rank256.safetensors", false)]
    [InlineData("control-lora-canny-rank256.safetensors", false)]
    public void BuildRegenDecensor_ControlNetName_AddsUnionLineartTypeOnlyForUnionModels(string controlNet, bool union)
    {
        var g = WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1, RegenControlNet = controlNet }, "in.png", "out");

        var (loaderId, loader) = Only(g, "ControlNetLoader");
        Assert.Equal(controlNet, Str(Inputs(loader)["control_net_name"]));
        // union(promax) 모델은 어떤 힌트인지 알려 줘야 선화로 쓴다 — 이름에 union·promax가 들면(대소문자 무관) 타입 노드를 끼운다
        var cn = Source(g, Inputs(Only(g, "ControlNetApplyAdvanced").Node)["control_net"]);
        Assert.Equal(union ? "SetUnionControlNetType" : "ControlNetLoader", ClassType(cn));
        if (union)
        {
            Assert.Equal("canny/lineart/anime_lineart/mlsd", Str(Inputs(cn)["type"]));
            Assert.Equal((loaderId, 0), LinkOf(Inputs(cn)["control_net"]));
        }
    }

    [Fact]
    public void BuildRegenDecensor_SamplerAndControlNetSettings_ReachTheirNodes()
    {
        var s = new AppSettings
        {
            RegenCheckpoint = "waiNSFW v14.safetensors", RegenNegative = "text, watermark",
            RegenSteps = 30, RegenCfg = 4.5, RegenSampler = "euler", RegenScheduler = "simple", RegenDenoise = 0.5, RegenSeed = 0,
            RegenCnStrength = 0.6, RegenCnEnd = 0.7, RegenPreprocResolution = 1024,
        };
        var g = WorkflowBuilder.BuildRegenDecensor(s, "in.png", "out");

        var k = Inputs(Only(g, "KSampler").Node);
        Assert.Equal(30, Num(k["steps"]));
        Assert.Equal(4.5, Num(k["cfg"]));
        Assert.Equal("euler", Str(k["sampler_name"]));
        Assert.Equal("simple", Str(k["scheduler"]));
        Assert.Equal(0.5, Num(k["denoise"]));
        Assert.Equal(0, Num(k["seed"]));   // 0도 고정 시드다
        Assert.Equal("waiNSFW v14.safetensors", Str(Inputs(Source(g, k["model"]))["ckpt_name"]));
        // img2img의 잠재는 LaMa 1차 복원 결과에서
        var encode = Source(g, k["latent_image"]);
        Assert.Equal("VAEEncode", ClassType(encode));
        Assert.Equal("LayerUtility: LaMa", ClassType(Source(g, Inputs(encode)["pixels"])));

        // ControlNet: 강도·끝 지점은 설정, 시작은 0. KSampler는 그 positive·negative 두 출력을 받는다
        var (applyId, _) = LinkOf(k["positive"]);
        Assert.Equal((applyId, 1), LinkOf(k["negative"]));
        var apply = Inputs(Assert.IsType<JsonObject>(g[applyId]));
        Assert.Equal(0.6, Num(apply["strength"]));
        Assert.Equal(0.0, Num(apply["start_percent"]));
        Assert.Equal(0.7, Num(apply["end_percent"]));
        Assert.Equal("text, watermark", Str(Inputs(Source(g, apply["negative"]))["text"]));
        // 선화 힌트는 LaMa 결과에서 Manga2Anime 전처리로, 설정한 해상도로 뽑는다
        var hint = Source(g, apply["image"]);
        Assert.Equal("AIO_Preprocessor", ClassType(hint));
        Assert.Equal("Manga2Anime_LineArt_Preprocessor", Str(Inputs(hint)["preprocessor"]));
        Assert.Equal(1024, Num(Inputs(hint)["resolution"]));
        Assert.Equal("LayerUtility: LaMa", ClassType(Source(g, Inputs(hint)["image"])));
    }

    [Theory]
    [InlineData("masterpiece, manga,", "  detailed skin  ", "masterpiece, manga, detailed skin")]
    [InlineData("masterpiece, manga", "   ", "masterpiece, manga")]
    [InlineData("", "detailed skin", "detailed skin")]
    public void BuildRegenDecensor_PositivePrompt_IsPromptThenBodyTagsSkippingBlanks(string prompt, string bodyTags, string expected)
    {
        var s = new AppSettings { RegenSeed = 1, RegenPrompt = prompt, RegenBodyTags = bodyTags };
        var g = WorkflowBuilder.BuildRegenDecensor(s, "in.png", "out");

        // 신체부위 태그는 기본 프롬프트 뒤에 ", "로 붙는다 — 빈 칸은 빼고, 끝에 남은 쉼표는 지운다
        var apply = Inputs(Only(g, "ControlNetApplyAdvanced").Node);
        Assert.Equal(expected, Str(Inputs(Source(g, apply["positive"]))["text"]));
    }

    // ───────── 태그 추출 ─────────

    [Theory]
    [InlineData(0.35, 0.3)]
    [InlineData(0.2, 0.2)]
    public void BuildTagExtract_TaggerThreshold_NeverAbovePointThree(double configured, double expected)
    {
        var s = new AppSettings { ColorTaggerModel = "wd-eva02-large-tagger-v3", ColorTaggerThreshold = configured };
        var g = WorkflowBuilder.BuildTagExtract(s, "nm_ref_1.png");

        // 표지의 색 태그를 놓치지 않게 문턱을 0.3 이하로 낮춘다 — 사용자가 이미 더 낮게 잡았으면 그대로
        var (taggerId, tagger) = Only(g, "WD14Tagger|pysssss");
        Assert.Equal(expected, Num(Inputs(tagger)["threshold"]));
        Assert.Equal("wd-eva02-large-tagger-v3", Str(Inputs(tagger)["model"]));
        var (loadId, load) = Only(g, "LoadImage");
        Assert.Equal("nm_ref_1.png", Str(Inputs(load)["image"]));
        Assert.Equal((loadId, 0), LinkOf(Inputs(tagger)["image"]));
        // 결과는 그림이 아니라 ShowText의 글자로 수거한다
        var (_, show) = Only(g, "ShowText|pysssss");
        Assert.Equal((taggerId, 0), LinkOf(Inputs(show)["text"]));
    }

    // ───────── 빌더 공통 ─────────

    [Theory]
    [InlineData("autocolor", -1L)]
    [InlineData("autocolor", -987654321L)]
    [InlineData("regen", -1L)]
    public void Builders_NegativeSeed_SendsNonNegativeIntegerSeed(string builder, long seed)
    {
        var s = new AppSettings { ColorSeed = seed, RegenSeed = seed };
        var g = builder == "autocolor"
            ? WorkflowBuilder.BuildAutoColor(s, "in.png", "out")
            : WorkflowBuilder.BuildRegenDecensor(s, "in.png", "out");

        // 음수는 '매번 무작위'라는 설정일 뿐이다 — ComfyUI KSampler는 음수 시드를 받지 않으므로 그래프에는 0 이상의 정수가 실려야 한다
        var sent = Inputs(Only(g, "KSampler").Node)["seed"]!.GetValue<long>();
        Assert.True(sent >= 0, $"보낸 시드 {sent}");
    }

    [Theory]
    [InlineData("autocolor")]
    [InlineData("autocolor-with-reference")]
    [InlineData("autocolor-without-blend")]
    [InlineData("lama")]
    [InlineData("regen")]
    [InlineData("regen-scaled")]
    [InlineData("regen-plain-controlnet")]
    [InlineData("tag-extract")]
    public void Builders_EveryLinkHitsAnExistingNodeAndEveryNodeFeedsAnOutput(string variant)
    {
        var g = BuildVariant(variant);

        foreach (var (id, value) in g)
        {
            var node = Assert.IsType<JsonObject>(value);
            Assert.False(string.IsNullOrEmpty(ClassType(node)), $"{id}: class_type 없음");
            _ = Inputs(node);
        }
        // 끊긴 선이 없다 — 모든 링크가 그래프 안의 노드를 0 이상의 출력 번호로 가리킨다
        foreach (var (from, slot, to, input) in Links(g))
        {
            Assert.True(g.ContainsKey(from), $"{to}.{input} → 없는 노드 {from}");
            Assert.True(slot >= 0, $"{to}.{input} → 출력 번호 {slot}");
        }
        // 헛도는 노드가 없다 — 출력에 닿지 않는 노드는 ComfyUI가 실행하지 않으므로, 남는 노드는 잘못 이어졌다는 뜻이다
        var reachable = ReachableFromOutputs(g);
        var orphans = g.Select(p => p.Key).Where(id => !reachable.Contains(id)).ToList();
        Assert.Empty(orphans);
        // 결과 그림을 받는 SaveImage는 정확히 하나, 접두사는 호출부가 준 값 (태그 추출은 그림을 저장하지 않는다)
        var saves = OfType(g, "SaveImage");
        if (variant == "tag-extract") Assert.Empty(saves);
        else Assert.Equal("nm_out", Str(Inputs(Assert.Single(saves).Node)["filename_prefix"]));
    }

    private static JsonObject BuildVariant(string variant) => variant switch
    {
        "autocolor" => WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 1 }, "in.png", "nm_out"),
        "autocolor-with-reference" => WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 1 }, "in.png", "nm_out", "ref.png"),
        "autocolor-without-blend" => WorkflowBuilder.BuildAutoColor(new AppSettings { ColorSeed = 1, ColorLineartOpacity = 0 }, "in.png", "nm_out"),
        "lama" => WorkflowBuilder.BuildLamaDecensor(new AppSettings(), "in.png", "nm_out"),
        "regen" => WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1 }, "in.png", "nm_out"),
        "regen-scaled" => WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1, RegenMegapixels = 1.5 }, "in.png", "nm_out"),
        "regen-plain-controlnet" => WorkflowBuilder.BuildRegenDecensor(
            new AppSettings { RegenSeed = 1, RegenControlNet = "mistoLine_rank256.safetensors" }, "in.png", "nm_out"),
        "tag-extract" => WorkflowBuilder.BuildTagExtract(new AppSettings(), "in.png"),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}
