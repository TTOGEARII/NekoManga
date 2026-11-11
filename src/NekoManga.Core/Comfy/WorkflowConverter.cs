using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI **화면 저장 포맷**(nodes/links 배열 — 화면에서 그냥 저장한 파일)을 서버가 받는 **API 포맷**으로 바꾼다 (2026-11-09).
///
/// 사용자가 바탕 화면의 워크플로 파일을 그대로 커스텀 칸에 넣었다가 "화면 저장 포맷이에요"로 거절당한 것이 계기.
/// 화면 포맷은 위젯 값을 이름 없이 순서대로만 담고 있어서(widgets_values), 노드마다 입력 이름·순서를 알려 주는 서버의
/// <c>/object_info</c>가 있어야 풀 수 있다 — 그래서 변환은 엔진이 켜진 뒤(실행 직전)에 한다.
///
/// 규칙(ComfyUI 프론트엔드의 graphToPrompt와 같은 순서):
///  · 입력 정의를 required → optional 순으로 훑으며, 위젯형(INT·FLOAT·STRING·BOOLEAN·목록) 입력마다 widgets_values를 하나씩 쓴다.
///    선으로 연결된 위젯도 자리는 차지한다. seed류 INT 뒤에는 "생성 후 제어" 값이 하나 더 붙어 있다(randomize면 새 시드를 뽑는다).
///  · 연결은 links 표로 푼다. Reroute는 건너뛰고, PrimitiveNode는 그 값으로, 우회(mode 4) 노드는 같은 타입의 입력으로 잇는다.
///  · 꺼진(mode 2) 노드·메모는 버린다.
///  · 이 서버에 없는 몇몇 흔한 노드는 같은 일을 하는 것으로 바꾼다(아래 Substitute* — 재생성 레시피를 옮길 때 손으로 했던 것과 같다).</summary>
public static class WorkflowConverter
{
    private static readonly HashSet<string> VirtualTypes = new(StringComparer.Ordinal) { "Note", "MarkdownNote", "Reroute", "PrimitiveNode" };
    private static readonly HashSet<string> WidgetTypes = new(StringComparer.Ordinal) { "INT", "FLOAT", "STRING", "BOOLEAN", "COMBO" };
    private static readonly HashSet<string> SeedControls = new(StringComparer.Ordinal) { "fixed", "increment", "decrement", "randomize" };

    public static bool IsUiFormat(JsonObject workflow) => workflow["nodes"] is JsonArray && workflow.ContainsKey("links");

    private sealed record Link(int Origin, int OriginSlot);

    private sealed class Graph
    {
        public required Dictionary<int, JsonObject> Nodes;
        public required Dictionary<int, Link> Links;
        public required JsonObject Info;
        public readonly List<string> Missing = [];
    }

    /// <summary>화면 포맷을 API 포맷으로. 풀 수 없으면(서버에 없는 노드, 서브그래프) 사람이 읽을 메시지의 InvalidDataException.</summary>
    public static JsonObject ToApi(JsonObject ui, JsonObject objectInfo)
    {
        if (ui["definitions"]?["subgraphs"] is JsonArray { Count: > 0 })
            throw new InvalidDataException("서브그래프가 든 워크플로는 아직 변환하지 못해요 — ComfyUI에서 'Export (API)'로 내보낸 파일을 골라 주세요.");

        var graph = new Graph
        {
            Nodes = ui["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n => n["id"]!.GetValue<int>()),
            Links = ReadLinks(ui["links"]),
            Info = objectInfo,
        };

        var api = new JsonObject();
        foreach (var (id, node) in graph.Nodes.OrderBy(p => p.Key))
        {
            string type = Type(node);
            if (Mode(node) != 0 || VirtualTypes.Contains(type) || IsFoldedText(graph, node)) continue;
            var emitted = EmitNode(graph, node);
            if (emitted != null) api[id.ToString()] = emitted;
        }

        if (graph.Missing.Count > 0)
            throw new InvalidDataException(
                "이 워크플로가 쓰는 노드 가운데 앱의 ComfyUI에 없는 것이 있어요: " + string.Join(", ", graph.Missing.Distinct()) +
                "\n설정 → 엔진 → 'ComfyUI 화면 열기'에서 이 파일을 열면 빠진 노드가 빨갛게 보여요. 그 노드를 설치하거나 다른 노드로 바꿔 주세요.");
        return api;
    }

    // ───────── 노드 하나 내보내기 ─────────

    private static JsonObject? EmitNode(Graph g, JsonObject node)
    {
        string type = Type(node);
        if (g.Info[type] is not JsonObject info)
        {
            var substituted = SubstituteAdvancedControlNet(g, node);
            if (substituted == null) g.Missing.Add(type);
            return substituted;
        }

        var inputs = new JsonObject();
        var widgets = node["widgets_values"];
        int cursor = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string group in (string[])["required", "optional"])
        {
            if (info["input"]?[group] is not JsonObject defs) continue;
            foreach (var (name, spec) in defs)
            {
                seen.Add(name);
                var typeNode = spec?[0];
                var options = spec is JsonArray { Count: > 1 } a ? a[1] as JsonObject : null;
                bool isWidget = (typeNode is JsonArray || (typeNode is JsonValue tv && tv.TryGetValue<string>(out var tn) && WidgetTypes.Contains(tn)))
                                && options?["forceInput"]?.GetValue<bool>() != true;

                JsonNode? value = null;
                if (isWidget)
                {
                    if (widgets is JsonObject byName) value = byName[name]?.DeepClone();
                    else if (widgets is JsonArray list && cursor < list.Count)
                    {
                        value = list[cursor++]?.DeepClone();
                        // seed류 뒤에는 "생성 후 제어" 위젯 값이 하나 더 있다
                        bool seedLike = name is "seed" or "noise_seed" || HasSeedControl(options);
                        if (seedLike && cursor < list.Count && list[cursor] is JsonValue cv && cv.TryGetValue<string>(out var control) && SeedControls.Contains(control))
                        {
                            cursor++;
                            if (control == "randomize") value = Random.Shared.NextInt64(0, 0xFFFF_FFFF_FFFFL);
                        }
                    }
                }

                // 선이 연결돼 있으면 위젯 값보다 선이 우선이다
                if (FindInputLink(node, name) is { } linkId && Resolve(g, linkId) is { } source) value = source;
                if (value != null) inputs[name] = value;
            }
        }

        // 정의에 없는 이름으로 연결된 입력(노드가 동적으로 늘리는 입력)은 그대로 잇는다
        if (node["inputs"] is JsonArray uiInputs)
            foreach (var input in uiInputs.OfType<JsonObject>())
            {
                string? name = input["name"]?.GetValue<string>();
                if (name == null || seen.Contains(name) || input["link"] is null) continue;
                if (Resolve(g, input["link"]!.GetValue<int>()) is { } source) inputs[name] = source;
            }

        return new JsonObject
        {
            ["inputs"] = inputs,
            ["class_type"] = type,
            ["_meta"] = new JsonObject { ["title"] = Title(node, info) },
        };
    }

    /// <summary>입력 옵션에 "생성 후 제어" 표시(control_after_generate)가 켜져 있는가. 옛 꼴 노드는 true/false로 내놓고,
    /// V3 스키마 노드(코어 'Int' = PrimitiveInt 등)는 제어 위젯의 처음 값인 글자("fixed" 등)로 내놓는다 — 글자를 bool로만 읽다가
    /// InvalidOperationException으로 터져, 그런 노드가 든 화면 저장 워크플로를 통째로 못 썼다 (2026-11-11).</summary>
    private static bool HasSeedControl(JsonObject? options) =>
        options?["control_after_generate"] is JsonValue flag
        && (flag.TryGetValue<bool>(out var on) ? on : flag.TryGetValue<string>(out var initial) && SeedControls.Contains(initial));

    // ───────── 연결 풀기 ─────────

    /// <summary>링크가 가리키는 출처를 API 값으로: 보통은 ["노드 id", 출력 번호], 상수로 접힌 노드면 그 값, 끊겼으면 null.</summary>
    private static JsonNode? Resolve(Graph g, int linkId, int depth = 0)
    {
        if (depth > 64 || !g.Links.TryGetValue(linkId, out var link) || !g.Nodes.TryGetValue(link.Origin, out var origin)) return null;
        string type = Type(origin);
        int mode = Mode(origin);
        if (mode == 2) return null;

        if (type == "Reroute") return FirstInputLink(origin) is { } next ? Resolve(g, next, depth + 1) : null;
        if (type == "PrimitiveNode") return (origin["widgets_values"] as JsonArray)?.FirstOrDefault()?.DeepClone();
        if (mode == 4)
        {
            // 우회: 이 출력과 같은 타입의 입력으로 곧장 잇는다 (같은 자리 번호를 먼저 본다)
            string? outType = (origin["outputs"] as JsonArray)?.ElementAtOrDefault(link.OriginSlot)?["type"]?.GetValue<string>();
            var ins = (origin["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var pass = ins.ElementAtOrDefault(link.OriginSlot) is { } same && same["type"]?.GetValue<string>() == outType && same["link"] != null
                ? same
                : ins.FirstOrDefault(i => i["type"]?.GetValue<string>() == outType && i["link"] != null);
            return pass != null ? Resolve(g, pass["link"]!.GetValue<int>(), depth + 1) : null;
        }
        if (IsFoldedText(g, origin)) return FoldText(g, origin, depth + 1) is { } text ? JsonValue.Create(text) : null;
        return new JsonArray(link.Origin.ToString(), link.OriginSlot);
    }

    private static int? FindInputLink(JsonObject node, string name) =>
        (node["inputs"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(i => i["name"]?.GetValue<string>() == name && i["link"] != null)?["link"]!.GetValue<int>();

    private static int? FirstInputLink(JsonObject node) =>
        (node["inputs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(i => i["link"] != null)?["link"]!.GetValue<int>();

    /// <summary>links는 판에 따라 [id, 출처 노드, 출처 자리, 대상 노드, 대상 자리, 타입] 배열이거나 같은 이름의 객체다.</summary>
    private static Dictionary<int, Link> ReadLinks(JsonNode? links)
    {
        var map = new Dictionary<int, Link>();
        if (links is not JsonArray list) return map;
        foreach (var item in list)
        {
            if (item is JsonArray { Count: >= 3 } a) map[a[0]!.GetValue<int>()] = new Link(a[1]!.GetValue<int>(), a[2]!.GetValue<int>());
            else if (item is JsonObject o && o["id"] != null && o["origin_id"] != null)
                map[o["id"]!.GetValue<int>()] = new Link(o["origin_id"]!.GetValue<int>(), o["origin_slot"]?.GetValue<int>() ?? 0);
        }
        return map;
    }

    // ───────── 이 서버에 없는 흔한 노드 대신하기 ─────────

    /// <summary>글자만 내놓는 노드("Text": 글 한 덩이, "Text Concatenate": 이어 붙이기 — WAS Node Suite)가 서버에 없으면
    /// 노드를 보내는 대신 결과 글자를 미리 만들어 받는 쪽 입력에 직접 적는다.</summary>
    private static bool IsFoldedText(Graph g, JsonObject node)
    {
        string type = Type(node);
        return type is "Text" or "Text Multiline" or "Text Concatenate" && g.Info[type] is null && Mode(node) == 0;
    }

    private static string? FoldText(Graph g, JsonObject node, int depth)
    {
        var widgets = node["widgets_values"] as JsonArray;
        if (Type(node) != "Text Concatenate")
            return widgets?.FirstOrDefault() is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

        // 위젯: [구분자, 공백 정리 여부]. 연결된 text_a… 를 순서대로, 빈 것은 빼고 잇는다.
        string delimiter = widgets?.ElementAtOrDefault(0) is JsonValue d && d.TryGetValue<string>(out var ds) ? ds : ", ";
        bool clean = widgets?.ElementAtOrDefault(1) is JsonValue c && (c.TryGetValue<string>(out var cs) ? cs == "true" : c.TryGetValue<bool>(out var cb) && cb);
        var parts = new List<string>();
        foreach (var input in (node["inputs"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (input["link"] is null) continue;
            if (Resolve(g, input["link"]!.GetValue<int>(), depth) is not JsonValue pv || !pv.TryGetValue<string>(out var part))
            {
                g.Missing.Add("Text Concatenate");   // 글자가 아닌 것이 연결돼 있어 미리 만들 수 없다
                return null;
            }
            if (clean) part = part.Trim();
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join(delimiter, parts);
    }

    /// <summary>ACN_AdvancedControlNetApply(Advanced-ControlNet)가 없으면 같은 강도·구간의 기본 ControlNetApplyAdvanced로 보낸다.
    /// 출력 순서(positive, negative)가 같아서 뒤에 이어진 선은 그대로 맞는다. 키프레임·가중치 같은 고급 입력이 연결돼 있으면 대신할 수 없다.</summary>
    private static JsonObject? SubstituteAdvancedControlNet(Graph g, JsonObject node)
    {
        if (Type(node) != "ACN_AdvancedControlNetApply" || g.Info["ControlNetApplyAdvanced"] is null) return null;
        foreach (string advanced in (string[])["mask_optional", "timestep_kf", "latent_kf_override", "weights_override", "model_optional"])
            if (FindInputLink(node, advanced) != null) return null;

        var inputs = new JsonObject();
        foreach (var (from, to) in ((string, string)[])[("positive", "positive"), ("negative", "negative"), ("control_net", "control_net"), ("image", "image"), ("vae_optional", "vae")])
            if (FindInputLink(node, from) is { } linkId && Resolve(g, linkId) is { } source) inputs[to] = source;

        var widgets = node["widgets_values"] as JsonArray;
        inputs["strength"] = widgets?.ElementAtOrDefault(0)?.DeepClone() ?? 1.0;
        inputs["start_percent"] = widgets?.ElementAtOrDefault(1)?.DeepClone() ?? 0.0;
        inputs["end_percent"] = widgets?.ElementAtOrDefault(2)?.DeepClone() ?? 1.0;
        return new JsonObject
        {
            ["inputs"] = inputs,
            ["class_type"] = "ControlNetApplyAdvanced",
            ["_meta"] = new JsonObject { ["title"] = node["title"]?.GetValue<string>() ?? "ControlNetApplyAdvanced" },
        };
    }

    // ───────── 실행 전 점검 ─────────

    /// <summary>API 그래프를 서버 정보와 맞춰 본다: 없는 노드, 비어 있는 필수 입력, 목록에 없는 파일(모델 등).
    /// 문제를 한꺼번에 모아 InvalidDataException으로 알린다 — 서버의 영어 오류 대신 무엇을 고칠지 바로 보이게.
    /// 이름만 다른 같은 ControlNet(xinsir union promax)은 설치된 이름으로 바꿔 준다(재생성 방식과 같은 규칙).</summary>
    public static void Validate(JsonObject api, JsonObject objectInfo)
    {
        var problems = new List<string>();
        foreach (var (id, nodeValue) in api)
        {
            if (nodeValue is not JsonObject node) continue;
            string type = node["class_type"]?.GetValue<string>() ?? "";
            string title = node["_meta"]?["title"]?.GetValue<string>() is { Length: > 0 } t ? t : type;
            if (objectInfo[type] is not JsonObject info)
            {
                problems.Add($"'{type}' 노드가 앱의 ComfyUI에 없어요.");
                continue;
            }
            var inputs = node["inputs"] as JsonObject ?? new JsonObject();
            foreach (string group in (string[])["required", "optional"])
            {
                if (info["input"]?[group] is not JsonObject defs) continue;
                foreach (var (name, spec) in defs)
                {
                    var value = inputs[name];
                    if (value is null)
                    {
                        if (group == "required") problems.Add($"'{title}' 노드의 입력 '{name}'이(가) 비어 있어요 (연결이 끊겼거나 값이 없어요).");
                        continue;
                    }
                    // 목록에서 고르는 값(모델 파일 등)이 이 서버에 있는지. 앱이 바꿔 끼우는 LoadImage의 그림 이름은 보지 않는다.
                    if (type == "LoadImage" && name == "image") continue;
                    if (Choices(spec) is not { } choices || value is not JsonValue v || !v.TryGetValue<string>(out var chosen) || choices.Contains(chosen)) continue;
                    if (name == "control_net_name" && chosen == "diffusion_pytorch_model_promax.safetensors" && choices.Contains("controlnet-union-sdxl-promax.safetensors"))
                    {
                        inputs[name] = "controlnet-union-sdxl-promax.safetensors";
                        continue;
                    }
                    string have = choices.Count == 0 ? "없음" : string.Join(", ", choices.Take(6)) + (choices.Count > 6 ? " …" : "");
                    problems.Add($"'{title}' 노드의 {name}: '{chosen}'이(가) 앱의 ComfyUI에 없어요 (있는 것: {have}).");
                }
            }
        }
        if (problems.Count > 0)
            throw new InvalidDataException("이 워크플로를 그대로는 돌릴 수 없어요.\n· " + string.Join("\n· ", problems) +
                "\n없는 파일은 ComfyUI의 models 폴더에 넣거나(설정 → 엔진 → 파일 가져오기), ComfyUI 화면에서 워크플로를 열어 있는 것으로 바꿔 저장해 주세요.");
    }

    /// <summary>입력 정의가 고르는 목록이면 그 항목들. 옛 표기([목록, 옵션])와 새 표기(["COMBO", {options: 목록}]) 둘 다 본다.</summary>
    private static List<string>? Choices(JsonNode? spec)
    {
        var list = spec?[0] as JsonArray ?? (spec is JsonArray { Count: > 1 } a ? a[1]?["options"] as JsonArray : null);
        return list?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null).Select(s => s!).ToList();
    }

    private static string Type(JsonObject node) => node["type"]?.GetValue<string>() ?? "";

    private static int Mode(JsonObject node) => node["mode"]?.GetValue<int>() ?? 0;

    private static string Title(JsonObject node, JsonObject info) =>
        node["title"]?.GetValue<string>() is { Length: > 0 } title ? title
        : info["display_name"]?.GetValue<string>() is { Length: > 0 } display ? display
        : Type(node);
}
