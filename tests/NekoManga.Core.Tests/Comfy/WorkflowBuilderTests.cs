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

    // ───────── 재생성 검열 제거 ─────────

    // ───────── 재생성 v3 그래프 (2026-11-12): 영역 자르기 입력, SDXL 두 단계, LaMa 없음 ─────────

    private static void AssertMaskGrow(JsonObject node, int grow, int blur)
    {
        Assert.Equal("LayerMask: MaskGrow", ClassType(node));
        Assert.Equal(grow, Num(Inputs(node)["grow"]));
        Assert.Equal(blur, Num(Inputs(node)["blur"]));
    }

    private static JsonObject Crop(AppSettings? s = null, string tags = "penis, glans", int w = 1024, int h = 1024) =>
        WorkflowBuilder.BuildRegenCrop(s ?? new AppSettings { RegenSeed = 1 }, "nm_regen_0_0.png", "nm_regenmask_0_0.png", "out", tags, w, h);

    [Fact]
    public void BuildRegenCrop_HasNoLamaNode_AndTwoLoadImages()
    {
        var g = Crop();
        Assert.Empty(OfType(g, "LayerUtility: LaMa"));
        Assert.Empty(OfType(g, "ImageScaleToTotalPixels"));   // 자르기는 호출자가 — 그래프에서 축소하지 않는다
        var loads = OfType(g, "LoadImage");
        Assert.Equal(2, loads.Count);
        Assert.Contains(loads, l => Str(Inputs(l.Node)["image"]) == "nm_regen_0_0.png");
        Assert.Contains(loads, l => Str(Inputs(l.Node)["image"]) == "nm_regenmask_0_0.png");
    }

    [Fact]
    public void BuildRegenCrop_TwoPasses_InpaintPieceThenRefineRegion()
    {
        var s = new AppSettings { RegenSeed = 7, RegenDenoise = 0.62, RegenInpaintGrow = 8 };
        var g = Crop(s);
        var region = Assert.Single(OfType(g, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == "nm_regen_0_0.png");
        var erase = Assert.Single(OfType(g, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == "nm_regenmask_0_0.png");
        var samplers = OfType(g, "KSampler");
        Assert.Equal(2, samplers.Count);
        // 1단계: 디노이즈 1.0, 잠재 ← SetLatentNoiseMask(마스크 ← 지우기 마스크의 MaskGrow(RegenInpaintGrow))
        var pass1 = Assert.Single(samplers, k => Num(Inputs(k.Node)["denoise"]) == 1.0);
        var noise1 = Source(g, Inputs(pass1.Node)["latent_image"]);
        Assert.Equal("SetLatentNoiseMask", ClassType(noise1));
        var grow1 = Source(g, Inputs(noise1)["mask"]);
        AssertMaskGrow(grow1, 8, 2);
        Assert.Equal((erase.Id, 1), LinkOf(Inputs(grow1)["mask"]));
        Assert.Equal((region.Id, 0), LinkOf(Inputs(Source(g, Inputs(noise1)["samples"]))["pixels"]));   // 잠재는 원고에서
        Assert.Equal(7, Num(Inputs(pass1.Node)["seed"]));
        // 2단계: 디노이즈 설정값, 노이즈 마스크 ← 영역(원고 알파)의 MaskGrow(0, 4), 잠재 ← 1단계 합성 결과, 시드+1
        var pass2 = Assert.Single(samplers, k => Num(Inputs(k.Node)["denoise"]) == 0.62);
        var noise2 = Source(g, Inputs(pass2.Node)["latent_image"]);
        Assert.Equal("SetLatentNoiseMask", ClassType(noise2));
        var grow2 = Source(g, Inputs(noise2)["mask"]);
        AssertMaskGrow(grow2, 0, 4);
        Assert.Equal((region.Id, 1), LinkOf(Inputs(grow2)["mask"]));
        var stage1 = Source(g, Inputs(Source(g, Inputs(noise2)["samples"]))["pixels"]);
        Assert.Equal("ImageCompositeMasked", ClassType(stage1));
        Assert.Equal((region.Id, 0), LinkOf(Inputs(stage1)["destination"]));       // 원고 위에
        Assert.Equal("VAEDecode", ClassType(Source(g, Inputs(stage1)["source"])));  // 1단계 결과를
        Assert.Equal(8, Num(Inputs(pass2.Node)["seed"]));
    }

    [Fact]
    public void BuildRegenCrop_Stage1ControlImage_Lineart_BlacksOutThePiece()
    {
        // 기본(Lineart) — union이 아닌 ControlNet도 이 길이다
        var g = Crop(w: 1000, h: 800);
        Assert.Equal("Lineart", new AppSettings().RegenInpaintControl);
        var erase = Assert.Single(OfType(g, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == "nm_regenmask_0_0.png");
        var applies = OfType(g, "ControlNetApplyAdvanced");
        Assert.Equal(2, applies.Count);
        // 1단계 선화 = 원고 선화 위에 검은 EmptyImage를 지우기 마스크로 덮은 것 — 막대 테두리를 선으로 읽지 않게
        var blackout = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "ImageCompositeMasked").Node;
        var comp = Source(g, Inputs(blackout)["image"]);
        var empty = Source(g, Inputs(comp)["source"]);
        Assert.Equal("EmptyImage", ClassType(empty));
        Assert.Equal((1000, 800, 0), ((int)Num(Inputs(empty)["width"]), (int)Num(Inputs(empty)["height"]), (int)Num(Inputs(empty)["color"])));
        Assert.Equal((erase.Id, 1), LinkOf(Inputs(Source(g, Inputs(comp)["mask"]))["mask"]));
        var lineart1 = Source(g, Inputs(comp)["destination"]);
        Assert.Equal("AIO_Preprocessor", ClassType(lineart1));
        Assert.Equal("LoadImage", ClassType(Source(g, Inputs(lineart1)["image"])));   // 원고에서
        Assert.DoesNotContain(OfType(g, "SetUnionControlNetType"), n => Str(Inputs(n.Node)["type"]) == "repaint");
    }

    [Fact]
    public void BuildRegenCrop_Stage1_NonUnionControlNet_AlwaysLineart()
    {
        // union이 아닌 ControlNet은 종류를 고를 수 없어 Repaint 설정이어도 선화 검게 칠하기
        var g = Crop(new AppSettings { RegenSeed = 1, RegenControlNet = "mistoLine_rank256.safetensors", RegenInpaintControl = "Repaint" }, w: 1000, h: 800);
        Assert.Empty(OfType(g, "SetUnionControlNetType"));
        Assert.Equal(2, OfType(g, "AIO_Preprocessor").Count);
        var stage1 = Assert.Single(OfType(g, "ControlNetApplyAdvanced"), a => ClassType(Source(g, Inputs(a.Node)["image"])) == "ImageCompositeMasked").Node;
        Assert.Equal("AIO_Preprocessor", ClassType(Source(g, Inputs(Source(g, Inputs(stage1)["image"]))["destination"])));
    }

    [Fact]
    public void BuildRegenCrop_Stage1_CnStrength_IsSeparateFromStage2()
    {
        // 1단계(조각 인페인트) ControlNet 강도는 따로(RegenInpaintCnStrength) — 맞닿은 남녀 부위에서 막대 밑 부위가 지워지는 보고에
        // 선화 지시를 약하게 해 보는 손잡이. 기본값 0.5(2단계 0.85보다 약하게 — 막대 밑 부위가 빈 살결로 메워지던 쪽이 살아났다). 끝 지점(RegenCnEnd)은 둘이 같이 쓴다
        var g = Crop(new AppSettings { RegenSeed = 1, RegenCnStrength = 0.9, RegenInpaintCnStrength = 0.4, RegenCnEnd = 0.7 });
        var applies = OfType(g, "ControlNetApplyAdvanced");
        var stage1 = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "ImageCompositeMasked").Node;
        var stage2 = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "AIO_Preprocessor").Node;
        Assert.Equal(0.4, Num(Inputs(stage1)["strength"]));
        Assert.Equal(0.9, Num(Inputs(stage2)["strength"]));
        Assert.Equal(0.7, Num(Inputs(stage1)["end_percent"])); Assert.Equal(0.7, Num(Inputs(stage2)["end_percent"]));
        Assert.Equal(0.5, new AppSettings().RegenInpaintCnStrength);   // 실측으로 정한 기본값
    }

    [Fact]
    public void BuildRegenCrop_Stage1_RepaintOption_UsesOriginalImageWithPieceBlackedAndRepaintType()
    {
        // 2026-11-12 사용자 보고: 남녀 부위가 맞닿은 장면에서 기둥만 남고 여성 쪽이 통째로 지워짐 — 1단계 선화 ControlNet이 조각 자리를
        // "선 없음"으로 밀어붙이는 탓으로 보고 union promax의 'repaint' 종류를 시도했으나, 사용자 실측으로 검게 뭉개지고 더 나빴다 → 기본은 선화(Lineart)로 되돌리고
        // 리페인트는 실험용 선택지로만 남긴다: 제어 그림 = 원고의 조각 자리를 검게 비운 것(선화 아님). 2단계는 그대로 선화.
        var g = Crop(new AppSettings { RegenSeed = 1, RegenInpaintControl = "Repaint" }, w: 1000, h: 800);
        var erase = Assert.Single(OfType(g, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == "nm_regenmask_0_0.png");
        var applies = OfType(g, "ControlNetApplyAdvanced");
        Assert.Equal(2, applies.Count);
        var stage1 = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "ImageCompositeMasked").Node;
        var cnType = Source(g, Inputs(stage1)["control_net"]);
        Assert.Equal("SetUnionControlNetType", ClassType(cnType));
        Assert.Equal("repaint", Str(Inputs(cnType)["type"]));
        var comp = Source(g, Inputs(stage1)["image"]);
        Assert.Equal("LoadImage", ClassType(Source(g, Inputs(comp)["destination"])));   // 선화가 아니라 원고 그대로
        Assert.Equal("EmptyImage", ClassType(Source(g, Inputs(comp)["source"])));
        Assert.Equal((erase.Id, 1), LinkOf(Inputs(Source(g, Inputs(comp)["mask"]))["mask"]));
        var stage2 = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "AIO_Preprocessor").Node;
        Assert.Equal("canny/lineart/anime_lineart/mlsd", Str(Inputs(Source(g, Inputs(stage2)["control_net"]))["type"]));
        Assert.Single(OfType(g, "AIO_Preprocessor"));   // 1단계 선화 추출은 없다
    }

    [Fact]
    public void BuildRegenCrop_Stage2_LineartFromStage1()
    {
        var g = Crop();
        var applies = OfType(g, "ControlNetApplyAdvanced");
        var second = Assert.Single(applies, a => ClassType(Source(g, Inputs(a.Node)["image"])) == "AIO_Preprocessor").Node;
        var lineart2 = Source(g, Inputs(second)["image"]);
        Assert.Equal("ImageCompositeMasked", ClassType(Source(g, Inputs(lineart2)["image"])));   // 1단계 합성 결과에서 선화를 다시 뽑는다
    }

    [Fact]
    public void BuildRegenCrop_Composites_UseRegionEdgeAndCore_AndSave()
    {
        var s = new AppSettings { RegenSeed = 1, RegenEdgeGrow = 21, RegenEdgeBlur = 22, RegenCoreGrow = 31, RegenCoreBlur = 32 };
        var g = Crop(s);
        var region = Assert.Single(OfType(g, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == "nm_regen_0_0.png");
        var (saveId, save) = Only(g, "SaveImage");
        var core = Source(g, Inputs(save)["images"]);
        Assert.Equal("ImageCompositeMasked", ClassType(core));
        AssertMaskGrow(Source(g, Inputs(core)["mask"]), 31, 32);
        Assert.Equal((region.Id, 1), LinkOf(Inputs(Source(g, Inputs(core)["mask"]))["mask"]));
        var edge = Source(g, Inputs(core)["destination"]);
        AssertMaskGrow(Source(g, Inputs(edge)["mask"]), 21, 22);
        Assert.Equal("out", Str(Inputs(save)["filename_prefix"]));
    }

    [Theory]
    [InlineData("masterpiece, manga,", "penis, glans", "masterpiece, manga, penis, glans")]
    [InlineData("masterpiece, manga", "", "masterpiece, manga")]
    public void BuildRegenCrop_Prompt_IsPromptThenGivenTags(string prompt, string tags, string expected)
    {
        var g = Crop(new AppSettings { RegenSeed = 1, RegenPrompt = prompt }, tags);
        Assert.All(OfType(g, "ControlNetApplyAdvanced"), a => Assert.Equal(expected, Str(Inputs(Source(g, Inputs(a.Node)["positive"]))["text"])));
    }

    [Theory]
    [InlineData("controlnet-union-sdxl-promax.safetensors", true)]
    [InlineData("diffusion_pytorch_model_promax.safetensors", true)]
    [InlineData("xinsir-UNION-sdxl-1.0.safetensors", true)]
    [InlineData("mistoLine_rank256.safetensors", false)]
    [InlineData("control-lora-canny-rank256.safetensors", false)]
    public void BuildRegenCrop_ControlNetName_AddsUnionLineartTypeOnlyForUnionModels(string controlNet, bool union)
    {
        var g = Crop(new AppSettings { RegenSeed = 1, RegenControlNet = controlNet });
        var (loaderId, loader) = Only(g, "ControlNetLoader");
        Assert.Equal(controlNet, Str(Inputs(loader)["control_net_name"]));
        Assert.All(OfType(g, "ControlNetApplyAdvanced"), a =>
        {
            var cn = Source(g, Inputs(a.Node)["control_net"]);
            Assert.Equal(union ? "SetUnionControlNetType" : "ControlNetLoader", ClassType(cn));
            if (union) Assert.Equal((loaderId, 0), LinkOf(Inputs(cn)["control_net"]));
        });
    }

    [Fact]
    public void BuildRegenCrop_SamplerAndControlNetSettings_ReachBothPasses()
    {
        var s = new AppSettings
        {
            RegenCheckpoint = "waiNSFW v14.safetensors", RegenNegative = "text, watermark",
            RegenSteps = 30, RegenCfg = 4.5, RegenSampler = "euler", RegenScheduler = "simple", RegenDenoise = 0.5, RegenSeed = 0,
            RegenCnStrength = 0.6, RegenInpaintCnStrength = 0.6, RegenCnEnd = 0.7, RegenPreprocResolution = 1024,   // 1단계 강도는 따로(RegenInpaintCnStrength) — 여기서는 같게 둔다
        };
        var g = Crop(s);
        foreach (var k in OfType(g, "KSampler").Select(x => Inputs(x.Node)))
        {
            Assert.Equal(30, Num(k["steps"])); Assert.Equal(4.5, Num(k["cfg"]));
            Assert.Equal("euler", Str(k["sampler_name"])); Assert.Equal("simple", Str(k["scheduler"]));
            Assert.Equal("waiNSFW v14.safetensors", Str(Inputs(Source(g, k["model"]))["ckpt_name"]));
        }
        foreach (var a in OfType(g, "ControlNetApplyAdvanced").Select(x => Inputs(x.Node)))
        {
            Assert.Equal(0.6, Num(a["strength"])); Assert.Equal(0.0, Num(a["start_percent"])); Assert.Equal(0.7, Num(a["end_percent"]));
            Assert.Equal("text, watermark", Str(Inputs(Source(g, a["negative"]))["text"]));
        }
        Assert.All(OfType(g, "AIO_Preprocessor"), p => { Assert.Equal("Manga2Anime_LineArt_Preprocessor", Str(Inputs(p.Node)["preprocessor"])); Assert.Equal(1024, Num(Inputs(p.Node)["resolution"])); });
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
            : WorkflowBuilder.BuildRegenCrop(s, "in.png", "erase.png", "out", "", 1024, 1024);

        // 음수는 '매번 무작위'라는 설정일 뿐이다 — ComfyUI KSampler는 음수 시드를 받지 않으므로 그래프에는 0 이상의 정수가 실려야 한다 (재생성은 두 단계 모두)
        Assert.All(OfType(g, "KSampler"), k => Assert.True(Inputs(k.Node)["seed"]!.GetValue<long>() >= 0));
    }

    [Theory]
    [InlineData("autocolor")]
    [InlineData("autocolor-with-reference")]
    [InlineData("autocolor-without-blend")]
    [InlineData("regen")]
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
        "regen" => WorkflowBuilder.BuildRegenCrop(new AppSettings { RegenSeed = 1 }, "in.png", "erase.png", "nm_out", "penis, glans", 1024, 1024),
        "regen-plain-controlnet" => WorkflowBuilder.BuildRegenCrop(
            new AppSettings { RegenSeed = 1, RegenControlNet = "mistoLine_rank256.safetensors" }, "in.png", "erase.png", "nm_out", "penis, glans", 1024, 1024),
        "tag-extract" => WorkflowBuilder.BuildTagExtract(new AppSettings(), "in.png"),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}
