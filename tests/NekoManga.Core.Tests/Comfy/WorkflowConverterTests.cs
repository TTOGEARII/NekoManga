using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;
using static NekoManga.Core.Tests.ComfyTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>WorkflowConverter — ComfyUI 화면 저장 포맷을 API 포맷으로 바꾸는 규칙(위젯 순서·연결 풀기·대신하기)과 실행 전 점검.
/// 실제 입력은 앱에 내장된 재생성 워크플로 파일이고, /object_info는 필요한 노드만 담아 테스트에서 만든다.</summary>
public class WorkflowConverterTests
{
    [Theory]
    [InlineData("""{"nodes": [], "links": []}""", true)]
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {"image": "a.png"}}}""", false)]
    [InlineData("""{"nodes": []}""", false)]
    [InlineData("""{"nodes": {}, "links": []}""", false)]
    [InlineData("""{}""", false)]
    public void IsUiFormat_TellsScreenFormatFromApiFormat(string json, bool expected)
    {
        // 화면 포맷의 표지는 nodes 배열과 links 표가 함께 있는 것이다
        Assert.Equal(expected, WorkflowConverter.IsUiFormat(Json(json)));
    }

    // ───────── 내장 재생성 워크플로 (실제 화면 저장 파일) ─────────

    [Fact]
    public void ToApi_BundledRegenWorkflow_ConvertsToGraphThatPassesValidation()
    {
        var ui = LoadBundledRegenWorkflow();
        var info = BundledRegenObjectInfo();
        Assert.True(WorkflowConverter.IsUiFormat(ui));

        var api = WorkflowConverter.ToApi(ui, info);

        // 서버에 없는 글자 노드(Text 9·10, Text Concatenate 11)는 받는 쪽 입력에 접혀 사라지고, 나머지는 같은 id로 남는다
        var expectedIds = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "12", "13", "14", "15", "16", "17", "18", "19", "20" };
        Assert.Equal(expectedIds.Order(StringComparer.Ordinal), api.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(api, p => ClassType(p.Value!.AsObject()) is "Text" or "Text Concatenate" or "ACN_AdvancedControlNetApply");
        // 변환 결과는 같은 노드 정보로 실행 전 점검을 통과한다
        WorkflowConverter.Validate(api, info);
    }

    [Fact]
    public void ToApi_BundledRegenWorkflow_KeepsRecipeValuesAndMaskRoles()
    {
        var api = WorkflowConverter.ToApi(LoadBundledRegenWorkflow(), BundledRegenObjectInfo());

        // KSampler 위젯 [시드, "randomize", 24, 5, "dpmpp_sde_gpu", "karras", 0.62] — 시드 뒤의 생성 후 제어 값은 입력이 아니라 건너뛴다
        var k = Inputs(Only(api, "KSampler").Node);
        Assert.Equal(24, Num(k["steps"]));
        Assert.Equal(5, Num(k["cfg"]));
        Assert.Equal("dpmpp_sde_gpu", Str(k["sampler_name"]));
        Assert.Equal("karras", Str(k["scheduler"]));
        Assert.Equal(0.62, Num(k["denoise"]));
        Assert.True(k["seed"]!.GetValue<long>() >= 0);   // randomize라 새로 뽑지만 0 이상의 정수여야 한다
        // 전처리기의 위젯은 optional 쪽 입력이다 (required → optional 순서)
        var aio = Inputs(Only(api, "AIO_Preprocessor").Node);
        Assert.Equal("Manga2Anime_LineArt_Preprocessor", Str(aio["preprocessor"]));
        Assert.Equal(1472, Num(aio["resolution"]));
        // LaMa 위젯 ["lama", "cuda", false, 0, 0]
        var (lamaId, lama) = Only(api, "LayerUtility: LaMa");
        Assert.Equal("lama", Str(Inputs(lama)["lama_model"]));
        Assert.Equal("cuda", Str(Inputs(lama)["device"]));
        Assert.Equal(0, Num(Inputs(lama)["mask_grow"]));

        // 마스크 3종이 제자리로 이어진다: LaMa ← (4, 4), 엣지 합성 ← (12, 8), 코어 합성 ← (4, 1)
        AssertMaskGrow(api, Inputs(lama)["mask"], 4, 4);
        var composites = OfType(api, "ImageCompositeMasked");
        var edge = Assert.Single(composites, c => LinkOf(Inputs(c.Node)["destination"]).Id == lamaId);
        AssertMaskGrow(api, Inputs(edge.Node)["mask"], 12, 8);
        var core = Assert.Single(composites, c => LinkOf(Inputs(c.Node)["destination"]).Id == edge.Id);
        AssertMaskGrow(api, Inputs(core.Node)["mask"], 4, 1);
    }

    [Fact]
    public void ToApi_BundledRegenWorkflow_SendsAdvancedControlNetAsCoreNode()
    {
        var api = WorkflowConverter.ToApi(LoadBundledRegenWorkflow(), BundledRegenObjectInfo());

        // 서버에 Advanced-ControlNet이 없으면 같은 강도·구간의 기본 ControlNetApplyAdvanced로, 같은 노드 id·제목으로 보낸다
        var node = Assert.IsType<JsonObject>(api["14"]);
        Assert.Equal("ControlNetApplyAdvanced", ClassType(node));
        Assert.Equal("라인아트 컨트롤넷 적용", Str(node["_meta"]!["title"]));
        var cn = Inputs(node);
        Assert.Equal(0.85, Num(cn["strength"]));
        Assert.Equal(0, Num(cn["start_percent"]));
        Assert.Equal(0.85, Num(cn["end_percent"]));
        Assert.Equal("CLIPTextEncode", ClassType(Source(api, cn["positive"])));
        Assert.Equal("CLIPTextEncode", ClassType(Source(api, cn["negative"])));
        Assert.Equal("ControlNetLoader", ClassType(Source(api, cn["control_net"])));
        Assert.Equal("AIO_Preprocessor", ClassType(Source(api, cn["image"])));
        // 출력 순서(positive, negative)가 같아서 뒤에 이어진 KSampler의 선은 그대로 맞는다
        var k = Inputs(Only(api, "KSampler").Node);
        Assert.Equal(("14", 0), LinkOf(k["positive"]));
        Assert.Equal(("14", 1), LinkOf(k["negative"]));
    }

    [Fact]
    public void ToApi_BundledRegenWorkflow_FoldsPromptTextNodesIntoEncoder()
    {
        var ui = LoadBundledRegenWorkflow();
        var api = WorkflowConverter.ToApi(ui, BundledRegenObjectInfo());

        // 'Text Concatenate'(구분자 ", ", 공백 정리)가 잇던 두 'Text' 노드의 글을 미리 이어, 포지티브 인코더 입력에 직접 적는다
        string TextOf(int id) => ui["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<int>() == id)!["widgets_values"]![0]!.GetValue<string>();
        var encoder = Inputs(Assert.IsType<JsonObject>(api["12"]));
        Assert.Equal(TextOf(9).Trim() + ", " + TextOf(10).Trim(), Str(encoder["text"]));
        Assert.Equal(("1", 1), LinkOf(encoder["clip"]));
    }

    // ───────── 규칙별 작은 그래프 ─────────

    [Fact]
    public void ToApi_WidgetValues_FillRequiredThenOptionalWidgetsInOrder()
    {
        var ui = Json("""
            {
              "nodes": [
                { "id": 1, "type": "LoadImage", "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }], "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "OrderProbe",
                  "inputs": [
                    { "name": "image", "type": "IMAGE", "link": 1 },
                    { "name": "strength", "type": "FLOAT", "widget": { "name": "strength" }, "link": 2 },
                    { "name": "caption", "type": "STRING", "link": null }
                  ],
                  "widgets_values": ["lanczos", 0.75, 12, "dpmpp_2m", false] },
                { "id": 3, "type": "PrimitiveNode", "outputs": [{ "name": "FLOAT", "type": "FLOAT", "links": [2], "widget": { "name": "strength" } }], "widgets_values": [0.5] }
              ],
              "links": [[1, 1, 0, 2, 0, "IMAGE"], [2, 3, 0, 2, 1, "FLOAT"]]
            }
            """);

        var probe = Inputs(Assert.IsType<JsonObject>(WorkflowConverter.ToApi(ui, ProbeInfo())["2"]));

        // 위젯 값은 이름 없이 순서대로다: required의 위젯(method, strength) 다음 optional의 위젯(steps, sampler, enabled)
        Assert.Equal("lanczos", Str(probe["method"]));
        Assert.Equal(12, Num(probe["steps"]));
        Assert.Equal("dpmpp_2m", Str(probe["sampler"]));   // 새 표기 ["COMBO", {options}]도 위젯이다
        Assert.False(Bool(probe["enabled"]));
        // 선이 이어진 위젯은 선의 값(PrimitiveNode 0.5)을 쓰되 자기 자리(0.75)는 차지한다 — 그래서 steps가 12로 맞는다
        Assert.Equal(0.5, Num(probe["strength"]));
        // forceInput 입력은 위젯이 아니어서 자리를 차지하지 않고, 이어지지 않았으니 비어 있다
        Assert.False(probe.ContainsKey("caption"));
        Assert.Equal(("1", 0), LinkOf(probe["image"]));
    }

    [Theory]
    [InlineData("SeedProbe", "seed", """[42, "fixed", 30, 6.5]""")]
    [InlineData("SeedProbe", "seed", """[42, "increment", 30, 6.5]""")]
    [InlineData("SeedProbe", "seed", """[42, 30, 6.5]""")]
    [InlineData("FlagSeedProbe", "rng", """[42, "decrement", 30, 6.5]""")]
    public void ToApi_SeedControlWord_IsSkippedOnlyWhenPresent(string nodeType, string seedInput, string widgets)
    {
        var ui = new JsonObject
        {
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = 1, ["type"] = nodeType, ["widgets_values"] = JsonNode.Parse(widgets) }),
            ["links"] = new JsonArray(),
        };

        var probe = Inputs(Assert.IsType<JsonObject>(WorkflowConverter.ToApi(ui, ProbeInfo())["1"]));

        // 시드(이름이 seed이거나 control_after_generate 표시가 있는 INT) 뒤의 생성 후 제어 값은 입력이 아니라 건너뛴다.
        // 옛 파일처럼 제어 값이 없으면 아무것도 건너뛰지 않고, fixed·increment·decrement면 적힌 시드를 그대로 쓴다
        Assert.Equal(42, Num(probe[seedInput]));
        Assert.Equal(30, Num(probe["steps"]));
        Assert.Equal(6.5, Num(probe["cfg"]));
    }

    [Fact]
    public void ToApi_RerouteAndPrimitive_ResolveToSourceNodeAndValue()
    {
        var ui = Json("""
            {
              "nodes": [
                { "id": 1, "type": "LoadImage", "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }], "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "Reroute", "inputs": [{ "name": "", "type": "*", "link": 1 }], "outputs": [{ "name": "", "type": "IMAGE", "links": [2] }] },
                { "id": 3, "type": "Reroute", "inputs": [{ "name": "", "type": "*", "link": 2 }], "outputs": [{ "name": "", "type": "IMAGE", "links": [3] }] },
                { "id": 4, "type": "SaveImage",
                  "inputs": [
                    { "name": "images", "type": "IMAGE", "link": 3 },
                    { "name": "filename_prefix", "type": "STRING", "widget": { "name": "filename_prefix" }, "link": 4 }
                  ],
                  "widgets_values": ["ComfyUI"] },
                { "id": 5, "type": "PrimitiveNode", "outputs": [{ "name": "STRING", "type": "STRING", "links": [4], "widget": { "name": "filename_prefix" } }], "widgets_values": ["neko_result"] }
              ],
              "links": [[1, 1, 0, 2, 0, "IMAGE"], [2, 2, 0, 3, 0, "*"], [3, 3, 0, 4, 0, "IMAGE"], [4, 5, 0, 4, 1, "STRING"]]
            }
            """);

        var api = WorkflowConverter.ToApi(ui, ProbeInfo());

        // Reroute·PrimitiveNode는 화면에만 있는 노드라 보내지 않는다
        Assert.Equal(new[] { "1", "4" }, api.Select(p => p.Key).Order(StringComparer.Ordinal));
        var save = Inputs(Assert.IsType<JsonObject>(api["4"]));
        // Reroute 두 개를 건너 원래 출처(LoadImage)로 잇고, PrimitiveNode는 그 값을 입력에 직접 적는다 (위젯의 옛 값보다 우선)
        Assert.Equal(("1", 0), LinkOf(save["images"]));
        Assert.Equal("neko_result", Str(save["filename_prefix"]));
    }

    [Fact]
    public void ToApi_BypassedNode_PassesItsSameTypedInputThrough()
    {
        var ui = Json("""
            {
              "nodes": [
                { "id": 1, "type": "LoadImage",
                  "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }, { "name": "MASK", "type": "MASK", "links": [2] }],
                  "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "MaskedSharpenProbe", "mode": 4,
                  "inputs": [{ "name": "mask", "type": "MASK", "link": 2 }, { "name": "image", "type": "IMAGE", "link": 1 }],
                  "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [3] }],
                  "widgets_values": [0.5] },
                { "id": 3, "type": "SaveImage", "inputs": [{ "name": "images", "type": "IMAGE", "link": 3 }], "widgets_values": ["neko"] }
              ],
              "links": [[1, 1, 0, 2, 1, "IMAGE"], [2, 1, 1, 2, 0, "MASK"], [3, 2, 0, 3, 0, "IMAGE"]]
            }
            """);

        var api = WorkflowConverter.ToApi(ui, ProbeInfo());

        // 우회(mode 4)한 노드는 보내지 않으므로 서버에 없는 종류여도 오류가 아니다.
        // 그 IMAGE 출력은 첫 입력(MASK)이 아니라 같은 타입의 입력(IMAGE)이 받던 선으로 이어진다
        Assert.False(api.ContainsKey("2"));
        Assert.Equal(("1", 0), LinkOf(Inputs(Assert.IsType<JsonObject>(api["3"]))["images"]));
    }

    [Fact]
    public void ToApi_MutedNodeAndNotes_AreDroppedAndValidateNamesTheCutInput()
    {
        var ui = Json("""
            {
              "nodes": [
                { "id": 1, "type": "LoadImage", "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }], "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "ImageScaleBy", "mode": 2,
                  "inputs": [{ "name": "image", "type": "IMAGE", "link": 1 }],
                  "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [2] }],
                  "widgets_values": ["lanczos", 2.0] },
                { "id": 3, "type": "SaveImage", "inputs": [{ "name": "images", "type": "IMAGE", "link": 2 }], "widgets_values": ["neko_out"] },
                { "id": 4, "type": "Note", "widgets_values": ["여기는 메모"] },
                { "id": 5, "type": "MarkdownNote", "widgets_values": ["# 설명"] }
              ],
              "links": [[1, 1, 0, 2, 0, "IMAGE"], [2, 2, 0, 3, 0, "IMAGE"]]
            }
            """);
        var info = ProbeInfo();

        var api = WorkflowConverter.ToApi(ui, info);

        // 꺼진(mode 2) 노드와 메모는 버린다 — 꺼진 노드에 기대던 입력은 비게 된다
        Assert.Equal(new[] { "1", "3" }, api.Select(p => p.Key).Order(StringComparer.Ordinal));
        var save = Inputs(Assert.IsType<JsonObject>(api["3"]));
        Assert.False(save.ContainsKey("images"));
        Assert.Equal("neko_out", Str(save["filename_prefix"]));
        // 그 빈 필수 입력은 실행 전 점검이 이름으로 알려 준다
        var ex = Assert.Throws<InvalidDataException>(() => WorkflowConverter.Validate(api, info));
        Assert.Contains("'images'", ex.Message);
    }

    [Fact]
    public void ToApi_ObjectStyleLinks_AreReadWithTheirOriginSlot()
    {
        // 새 판 프론트엔드는 links를 [id, 출처, 자리, …] 배열 대신 같은 이름의 객체로 저장한다
        var ui = Json("""
            {
              "nodes": [
                { "id": 1, "type": "LoadImage",
                  "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [] }, { "name": "MASK", "type": "MASK", "links": [10] }],
                  "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "MaskToImage", "inputs": [{ "name": "mask", "type": "MASK", "link": 10 }], "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [11] }] },
                { "id": 3, "type": "SaveImage", "inputs": [{ "name": "images", "type": "IMAGE", "link": 11 }], "widgets_values": ["mask_check"] }
              ],
              "links": [
                { "id": 10, "origin_id": 1, "origin_slot": 1, "target_id": 2, "target_slot": 0, "type": "MASK" },
                { "id": 11, "origin_id": 2, "origin_slot": 0, "target_id": 3, "target_slot": 0, "type": "IMAGE" }
              ]
            }
            """);

        var api = WorkflowConverter.ToApi(ui, ProbeInfo());

        Assert.Equal(("1", 1), LinkOf(Inputs(Assert.IsType<JsonObject>(api["2"]))["mask"]));
        Assert.Equal(("2", 0), LinkOf(Inputs(Assert.IsType<JsonObject>(api["3"]))["images"]));
    }

    [Theory]
    [InlineData("unknown-node", "FancyUpscaler")]
    [InlineData("advanced-controlnet-with-keyframes", "ACN_AdvancedControlNetApply")]
    [InlineData("concatenate-non-text", "Text Concatenate")]
    [InlineData("subgraph", "Export (API)")]
    public void ToApi_UnconvertibleWorkflow_ThrowsNamingTheCulpritOnce(string scenario, string culprit)
    {
        var ui = Json(UnconvertibleUi(scenario));

        var ex = Assert.Throws<InvalidDataException>(() => WorkflowConverter.ToApi(ui, ProbeInfo()));

        // 사람이 고칠 수 있게 무엇이 걸렸는지 이름으로 알리고, 같은 종류가 여러 개여도 한 번만 적는다
        Assert.Equal(1, CountOccurrences(ex.Message, culprit));
    }

    // ───────── 실행 전 점검 ─────────

    [Fact]
    public void Validate_RunnableGraph_PassesWithAbsentOptionalInputAndUnlistedUploadName()
    {
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "nm_custom_7.png" } },
              "2": { "class_type": "ImageCompositeMasked", "inputs": { "destination": ["1", 0], "source": ["1", 0], "x": 0, "y": 0, "resize_source": false } },
              "3": { "class_type": "SaveImage", "inputs": { "images": ["2", 0], "filename_prefix": "nm" } }
            }
            """);

        // optional 입력(mask)은 비어도 되고, LoadImage의 그림 이름은 앱이 실행 때 바꿔 끼우므로 서버 목록과 대조하지 않는다
        var error = Record.Exception(() => WorkflowConverter.Validate(api, BundledRegenObjectInfo()));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_SeveralProblems_AreAllReportedInOneMessage()
    {
        var info = Json("""
            {
              "LoadImage": { "input": { "required": { "image": [["example.png"], { "image_upload": true }] } } },
              "CheckpointLoaderSimple": { "input": { "required": { "ckpt_name": [["animagine-xl.safetensors", "waiIllustriousSDXL_v170.safetensors"]] } } },
              "KSamplerSelect": { "input": { "required": { "sampler_name": ["COMBO", { "options": ["euler", "dpmpp_2m"] }] } } },
              "SaveImage": { "input": { "required": { "images": ["IMAGE"], "filename_prefix": ["STRING", { "default": "ComfyUI" }] } } }
            }
            """);
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "page.png" } },
              "2": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "ponyDiffusion.safetensors" }, "_meta": { "title": "모델 불러오기" } },
              "3": { "class_type": "KSamplerSelect", "inputs": { "sampler_name": "dpmpp_9m" } },
              "4": { "class_type": "UltimateSDUpscale", "inputs": {} },
              "5": { "class_type": "SaveImage", "inputs": { "filename_prefix": "nm" }, "_meta": { "title": "결과 저장" } }
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkflowConverter.Validate(api, info));

        // 서버의 영어 오류 대신 고칠 것을 한 번에 보인다: 없는 노드, 목록에 없는 파일(있는 것과 함께, 노드 제목으로),
        // 새 표기 목록의 값, 비어 있는 필수 입력
        Assert.Contains("UltimateSDUpscale", ex.Message);
        Assert.Contains("모델 불러오기", ex.Message);
        Assert.Contains("ponyDiffusion.safetensors", ex.Message);
        Assert.Contains("animagine-xl.safetensors", ex.Message);
        Assert.Contains("dpmpp_9m", ex.Message);
        Assert.Contains("결과 저장", ex.Message);
        Assert.Contains("'images'", ex.Message);
    }

    [Fact]
    public void Validate_PromaxFileName_IsRewrittenToInstalledUnionModel()
    {
        var info = Json("""
            { "ControlNetLoader": { "input": { "required": { "control_net_name": [["control-lora-canny.safetensors", "controlnet-union-sdxl-promax.safetensors"]] } } } }
            """);
        var api = Json("""
            { "1": { "class_type": "ControlNetLoader", "inputs": { "control_net_name": "diffusion_pytorch_model_promax.safetensors" } } }
            """);

        // 사용자 워크플로의 xinsir union promax 원래 이름은 앱이 받아 둔 같은 모델의 이름으로 바꿔 통과시킨다
        WorkflowConverter.Validate(api, info);

        Assert.Equal("controlnet-union-sdxl-promax.safetensors", Str(Inputs(api["1"]!.AsObject())["control_net_name"]));
    }

    // ───────── 도우미 ─────────

    private static void AssertMaskGrow(JsonObject api, JsonNode? maskLink, int grow, int blur)
    {
        var maskGrow = Source(api, maskLink);
        Assert.Equal("LayerMask: MaskGrow", ClassType(maskGrow));
        Assert.Equal(grow, Num(Inputs(maskGrow)["grow"]));
        Assert.Equal(blur, Num(Inputs(maskGrow)["blur"]));
    }

    /// <summary>합성 그래프용 /object_info — 위젯 순서·시드 규칙을 보려고 만든 탐침 노드(…Probe)를 함께 담는다.</summary>
    private static JsonObject ProbeInfo() => Json("""
        {
          "LoadImage": { "input": { "required": { "image": [["example.png"], { "image_upload": true }] } }, "display_name": "Load Image" },
          "SaveImage": { "input": { "required": { "images": ["IMAGE"], "filename_prefix": ["STRING", { "default": "ComfyUI" }] } }, "display_name": "Save Image" },
          "ImageScaleBy": { "input": { "required": { "image": ["IMAGE"], "upscale_method": [["nearest-exact", "lanczos"]], "scale_by": ["FLOAT", { "default": 1.0 }] } } },
          "MaskToImage": { "input": { "required": { "mask": ["MASK"] } } },
          "CLIPTextEncode": { "input": { "required": { "text": ["STRING", { "multiline": true }], "clip": ["CLIP"] } } },
          "ControlNetApplyAdvanced": {
            "input": {
              "required": {
                "positive": ["CONDITIONING"], "negative": ["CONDITIONING"], "control_net": ["CONTROL_NET"], "image": ["IMAGE"],
                "strength": ["FLOAT"], "start_percent": ["FLOAT"], "end_percent": ["FLOAT"]
              },
              "optional": { "vae": ["VAE"] }
            }
          },
          "ACN_TimestepKeyframe": { "input": { "required": { "start_percent": ["FLOAT"] } } },
          "OrderProbe": {
            "input": {
              "required": {
                "image": ["IMAGE"],
                "method": [["nearest", "lanczos"]],
                "strength": ["FLOAT", { "default": 1.0 }],
                "caption": ["STRING", { "forceInput": true }]
              },
              "optional": {
                "steps": ["INT", { "default": 4 }],
                "sampler": ["COMBO", { "options": ["euler", "dpmpp_2m"] }],
                "enabled": ["BOOLEAN", { "default": true }]
              }
            }
          },
          "SeedProbe": { "input": { "required": { "seed": ["INT", { "default": 0 }], "steps": ["INT", { "default": 20 }], "cfg": ["FLOAT", { "default": 8.0 }] } } },
          "FlagSeedProbe": { "input": { "required": { "rng": ["INT", { "default": 0, "control_after_generate": true }], "steps": ["INT", { "default": 20 }], "cfg": ["FLOAT", { "default": 8.0 }] } } }
        }
        """);

    private static string UnconvertibleUi(string scenario) => scenario switch
    {
        // 서버에 없는 노드 — 같은 종류가 두 개
        "unknown-node" => """
            {
              "nodes": [
                { "id": 1, "type": "LoadImage", "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }], "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "FancyUpscaler", "inputs": [{ "name": "image", "type": "IMAGE", "link": 1 }], "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [2] }], "widgets_values": [2] },
                { "id": 3, "type": "FancyUpscaler", "inputs": [{ "name": "image", "type": "IMAGE", "link": 2 }], "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [3] }], "widgets_values": [2] },
                { "id": 4, "type": "SaveImage", "inputs": [{ "name": "images", "type": "IMAGE", "link": 3 }], "widgets_values": ["neko"] }
              ],
              "links": [[1, 1, 0, 2, 0, "IMAGE"], [2, 2, 0, 3, 0, "IMAGE"], [3, 3, 0, 4, 0, "IMAGE"]]
            }
            """,
        // 키프레임이 이어진 Advanced-ControlNet은 기본 노드로 대신할 수 없다
        "advanced-controlnet-with-keyframes" => """
            {
              "nodes": [
                { "id": 1, "type": "ACN_TimestepKeyframe", "outputs": [{ "name": "TIMESTEP_KF", "type": "TIMESTEP_KEYFRAME", "links": [1] }], "widgets_values": [0.0] },
                { "id": 2, "type": "ACN_AdvancedControlNetApply",
                  "inputs": [{ "name": "positive", "type": "CONDITIONING", "link": null }, { "name": "timestep_kf", "type": "TIMESTEP_KEYFRAME", "link": 1 }],
                  "widgets_values": [0.85, 0, 0.85] }
              ],
              "links": [[1, 1, 0, 2, 1, "TIMESTEP_KEYFRAME"]]
            }
            """,
        // 글자가 아닌 것(IMAGE)이 이어진 Text Concatenate는 결과를 미리 만들 수 없다
        "concatenate-non-text" => """
            {
              "nodes": [
                { "id": 1, "type": "LoadImage", "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }], "widgets_values": ["page.png", "image"] },
                { "id": 2, "type": "Text Concatenate", "inputs": [{ "name": "text_a", "type": "STRING", "link": 1 }], "outputs": [{ "name": "STRING", "type": "STRING", "links": [2] }], "widgets_values": [", ", "true"] },
                { "id": 3, "type": "CLIPTextEncode",
                  "inputs": [{ "name": "clip", "type": "CLIP", "link": null }, { "name": "text", "type": "STRING", "widget": { "name": "text" }, "link": 2 }],
                  "widgets_values": [""] }
              ],
              "links": [[1, 1, 0, 2, 0, "IMAGE"], [2, 2, 0, 3, 1, "STRING"]]
            }
            """,
        // 서브그래프는 아직 풀지 못한다 — API로 내보낸 파일을 고르라고 안내한다
        "subgraph" => """{ "definitions": { "subgraphs": [{ "id": "sg-1", "nodes": [] }] }, "nodes": [], "links": [] }""",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
