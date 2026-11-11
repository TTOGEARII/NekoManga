using System.Globalization;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Tests;

/// <summary>ComfyUI 워크플로 테스트가 함께 쓰는 도우미 — API 그래프 탐색, 값 꺼내기, 내장 워크플로와 그에 맞는 최소 /object_info.</summary>
internal static class ComfyTestHelpers
{
    /// <summary>앱에 내장된 재생성 검열 제거 워크플로(ComfyUI 화면 저장 포맷). 테스트 프로젝트가 출력 폴더의 TestData 아래로 복사한다.</summary>
    public static string BundledRegenWorkflowPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "NekoManga_decensor_regen.json");

    public static JsonObject LoadBundledRegenWorkflow() => Json(File.ReadAllText(BundledRegenWorkflowPath));

    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>내장 워크플로가 쓰는 노드 종류만 담은 /object_info. 입력 이름·순서는 ComfyUI 코어와
    /// LayerStyle·controlnet_aux 커스텀 노드의 정의를 따른다. WAS의 Text·Text Concatenate와
    /// Advanced-ControlNet의 ACN_AdvancedControlNetApply는 앱의 ComfyUI에 없는 것으로 둔다 — 변환기가 대신하는 길을 타게.</summary>
    public static JsonObject BundledRegenObjectInfo() => Json("""
        {
          "LoadImage": {
            "input": { "required": { "image": [["example.png"], { "image_upload": true }] } },
            "display_name": "Load Image"
          },
          "SaveImage": {
            "input": { "required": { "images": ["IMAGE"], "filename_prefix": ["STRING", { "default": "ComfyUI" }] } },
            "display_name": "Save Image"
          },
          "CheckpointLoaderSimple": {
            "input": { "required": { "ckpt_name": [["waiIllustriousSDXL_v170.safetensors"]] } },
            "display_name": "Load Checkpoint"
          },
          "ControlNetLoader": {
            "input": { "required": { "control_net_name": [["controlnet-union-sdxl-promax.safetensors"]] } },
            "display_name": "Load ControlNet Model"
          },
          "LayerMask: MaskGrow": {
            "input": { "required": {
              "mask": ["MASK"],
              "invert_mask": ["BOOLEAN", { "default": true }],
              "grow": ["INT", { "default": 4 }],
              "blur": ["INT", { "default": 4 }]
            } }
          },
          "LayerUtility: LaMa": {
            "input": { "required": {
              "image": ["IMAGE"],
              "mask": ["MASK"],
              "lama_model": [["lama", "ldm", "zits", "mat", "fcf", "manga"]],
              "device": [["cuda", "cpu"]],
              "invert_mask": ["BOOLEAN", { "default": false }],
              "mask_grow": ["INT", { "default": 25 }],
              "mask_blur": ["INT", { "default": 8 }]
            } }
          },
          "AIO_Preprocessor": {
            "input": {
              "required": { "image": ["IMAGE"] },
              "optional": {
                "preprocessor": [["none", "LineArtPreprocessor", "Manga2Anime_LineArt_Preprocessor"], { "default": "none" }],
                "resolution": ["INT", { "default": 512 }]
              }
            }
          },
          "CLIPTextEncode": {
            "input": { "required": { "text": ["STRING", { "multiline": true, "dynamicPrompts": true }], "clip": ["CLIP"] } },
            "display_name": "CLIP Text Encode (Prompt)"
          },
          "ControlNetApplyAdvanced": {
            "input": {
              "required": {
                "positive": ["CONDITIONING"], "negative": ["CONDITIONING"], "control_net": ["CONTROL_NET"], "image": ["IMAGE"],
                "strength": ["FLOAT", { "default": 1.0 }], "start_percent": ["FLOAT", { "default": 0.0 }], "end_percent": ["FLOAT", { "default": 1.0 }]
              },
              "optional": { "vae": ["VAE"] }
            }
          },
          "VAEEncode": { "input": { "required": { "pixels": ["IMAGE"], "vae": ["VAE"] } } },
          "VAEDecode": { "input": { "required": { "samples": ["LATENT"], "vae": ["VAE"] } } },
          "KSampler": {
            "input": { "required": {
              "model": ["MODEL"],
              "seed": ["INT", { "default": 0, "control_after_generate": true }],
              "steps": ["INT", { "default": 20 }],
              "cfg": ["FLOAT", { "default": 8.0 }],
              "sampler_name": [["euler", "dpmpp_2m", "dpmpp_sde_gpu"]],
              "scheduler": [["normal", "karras", "simple"]],
              "positive": ["CONDITIONING"],
              "negative": ["CONDITIONING"],
              "latent_image": ["LATENT"],
              "denoise": ["FLOAT", { "default": 1.0 }]
            } }
          },
          "ImageCompositeMasked": {
            "input": {
              "required": {
                "destination": ["IMAGE"], "source": ["IMAGE"],
                "x": ["INT", { "default": 0 }], "y": ["INT", { "default": 0 }],
                "resize_source": ["BOOLEAN", { "default": false }]
              },
              "optional": { "mask": ["MASK"] }
            }
          }
        }
        """);

    /// <summary>코어 'Int' 노드(PrimitiveInt) 하나가 크기 조절의 가로·세로에 이어진 화면 저장 워크플로.
    /// 노드의 저장 꼴("widgets_values": [1024, "fixed"])은 ComfyUI가 저장하는 그대로다 — 값 뒤에 생성 후 제어 값이 붙는다.</summary>
    public const string CoreIntNodeWorkflow = """
        {
          "last_node_id": 4, "last_link_id": 4,
          "nodes": [
            { "id": 1, "type": "LoadImage", "mode": 0, "inputs": [],
              "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [1] }, { "name": "MASK", "type": "MASK", "links": null }],
              "properties": { "Node name for S&R": "LoadImage" }, "widgets_values": ["page.png", "image"] },
            { "id": 3, "type": "PrimitiveInt", "mode": 0, "inputs": [],
              "outputs": [{ "name": "INT", "type": "INT", "links": [2, 3] }],
              "properties": { "Node name for S&R": "PrimitiveInt", "cnr_id": "comfy-core" }, "widgets_values": [1024, "fixed"] },
            { "id": 4, "type": "ImageScale", "mode": 0,
              "inputs": [
                { "name": "image", "type": "IMAGE", "link": 1 },
                { "name": "width", "type": "INT", "widget": { "name": "width" }, "link": 2 },
                { "name": "height", "type": "INT", "widget": { "name": "height" }, "link": 3 }
              ],
              "outputs": [{ "name": "IMAGE", "type": "IMAGE", "links": [4] }],
              "properties": { "Node name for S&R": "ImageScale" }, "widgets_values": ["lanczos", 512, 512, "disabled"] },
            { "id": 2, "type": "SaveImage", "mode": 0,
              "inputs": [{ "name": "images", "type": "IMAGE", "link": 4 }],
              "outputs": [], "properties": {}, "widgets_values": ["ComfyUI"] }
          ],
          "links": [[1, 1, 0, 4, 0, "IMAGE"], [2, 3, 0, 4, 1, "INT"], [3, 3, 0, 4, 2, "INT"], [4, 4, 0, 2, 0, "IMAGE"]],
          "groups": [], "config": {}, "extra": {}, "version": 0.4
        }
        """;

    /// <summary><see cref="CoreIntNodeWorkflow"/>에 맞는 /object_info. PrimitiveInt는 V3 스키마 노드라 control_after_generate를 true가 아니라
    /// 제어 위젯의 처음 값인 글자로 내놓는다(comfy_extras/nodes_primitive.py의 io.ControlAfterGenerate.fixed → "fixed").</summary>
    public static JsonObject CoreIntNodeObjectInfo() => Json("""
        {
          "LoadImage": { "input": { "required": { "image": [["page.png"], { "image_upload": true }] } }, "display_name": "Load Image" },
          "SaveImage": { "input": { "required": { "images": ["IMAGE"], "filename_prefix": ["STRING", { "default": "ComfyUI" }] } }, "display_name": "Save Image" },
          "ImageScale": {
            "input": { "required": {
              "image": ["IMAGE"],
              "upscale_method": [["nearest-exact", "bilinear", "area", "bicubic", "lanczos"]],
              "width": ["INT", { "default": 512, "min": 0, "max": 16384, "step": 1 }],
              "height": ["INT", { "default": 512, "min": 0, "max": 16384, "step": 1 }],
              "crop": [["disabled", "center"]]
            } },
            "display_name": "Upscale Image"
          },
          "PrimitiveInt": {
            "input": { "required": { "value": ["INT", { "min": -9223372036854775807, "max": 9223372036854775807, "control_after_generate": "fixed" }] } },
            "display_name": "Int"
          }
        }
        """);

    // ───────── API 그래프 탐색 ─────────

    public static JsonObject Inputs(JsonObject node) => Assert.IsType<JsonObject>(node["inputs"]);

    public static string ClassType(JsonObject node) => node["class_type"]!.GetValue<string>();

    public static List<(string Id, JsonObject Node)> OfType(JsonObject graph, string classType) =>
        graph.Where(p => p.Value is JsonObject n && n["class_type"]?.GetValue<string>() == classType)
             .Select(p => (p.Key, p.Value!.AsObject()))
             .ToList();

    /// <summary>그 종류의 노드가 정확히 하나여야 한다.</summary>
    public static (string Id, JsonObject Node) Only(JsonObject graph, string classType) =>
        Assert.Single(OfType(graph, classType));

    /// <summary>입력 값이 링크([노드 id, 출력 번호])여야 한다.</summary>
    public static (string Id, int Slot) LinkOf(JsonNode? value)
    {
        var link = Assert.IsType<JsonArray>(value);
        Assert.Equal(2, link.Count);
        return (link[0]!.GetValue<string>(), link[1]!.GetValue<int>());
    }

    /// <summary>링크가 가리키는 노드 (그래프 안에 있어야 한다).</summary>
    public static JsonObject Source(JsonObject graph, JsonNode? linkValue) =>
        Assert.IsType<JsonObject>(graph[LinkOf(linkValue).Id]);

    /// <summary>그래프의 모든 링크: (보내는 노드, 출력 번호, 받는 노드, 입력 이름).</summary>
    public static List<(string From, int Slot, string To, string Input)> Links(JsonObject graph)
    {
        var links = new List<(string, int, string, string)>();
        foreach (var (id, node) in graph)
            if (node?["inputs"] is JsonObject inputs)
                foreach (var (name, value) in inputs)
                    if (value is JsonArray { Count: 2 } a && a[0] is JsonValue v && v.TryGetValue<string>(out var from))
                        links.Add((from, a[1]!.GetValue<int>(), id, name));
        return links;
    }

    /// <summary>출력 노드(SaveImage·PreviewImage·ShowText)에서 링크를 거슬러 올라가 닿는 노드 id들.
    /// ComfyUI는 출력에 닿지 않는 노드를 실행하지 않으므로, 여기서 빠진 노드는 잘못 이어진 것이다.</summary>
    public static HashSet<string> ReachableFromOutputs(JsonObject graph)
    {
        string[] outputs = ["SaveImage", "PreviewImage", "ShowText|pysssss"];
        var incoming = Links(graph).ToLookup(l => l.To, l => l.From);
        var seen = new HashSet<string>();
        var stack = new Stack<string>(graph.Where(p => outputs.Contains(ClassType(p.Value!.AsObject()))).Select(p => p.Key));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            foreach (var from in incoming[id]) stack.Push(from);
        }
        return seen;
    }

    // ───────── 값 꺼내기 ─────────

    /// <summary>숫자 값. 빌더는 CLR 값(int·long·double)을, 파싱한 JSON은 JsonElement를 담으므로 JSON 표기를 거쳐 읽는다.</summary>
    public static double Num(JsonNode? value) =>
        double.Parse(Assert.IsAssignableFrom<JsonValue>(value).ToJsonString(), CultureInfo.InvariantCulture);

    public static string Str(JsonNode? value) => Assert.IsAssignableFrom<JsonValue>(value).GetValue<string>();

    public static bool Bool(JsonNode? value) => Assert.IsAssignableFrom<JsonValue>(value).GetValue<bool>();

    public static int CountOccurrences(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
