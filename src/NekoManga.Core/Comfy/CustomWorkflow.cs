using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>사용자가 ComfyUI 화면에서 직접 짠 워크플로를 앱 파이프라인에 끼우는 다리 (2026-10-20).
///
/// 입력은 ComfyUI의 **API 포맷** JSON(워크플로 메뉴 → "Export (API)")이거나 **화면 저장 포맷**(nodes/links 배열 — 화면에서 그냥 저장한 파일)이다.
/// 화면 포맷은 노드 정의가 있어야 풀 수 있어서, 파일을 읽을 때(<see cref="Load"/>)는 그대로 두고 엔진이 켜진 뒤 <see cref="Resolve"/>가
/// 서버의 /object_info로 API 포맷으로 바꾼다(<see cref="WorkflowConverter"/>, 2026-11-09 — 그전에는 화면 포맷을 거절했다).
///
/// 규약(둘만 지키면 된다):
///  · LoadImage 노드 하나 = 앱이 넣어 주는 입력. 여러 개면 제목(_meta.title)에 "neko"나 "입력"이 든 것, 없으면 첫 번째.
///    검열제거용이면 알파 채널이 마스크다(ComfyUI LoadImage 규약, 기존 엔진 LaMa·재생성과 동일).
///  · SaveImage 노드 하나 = 앱이 받아 오는 출력. 여러 개면 제목에 "neko"나 "출력"이 든 것, 없으면 첫 번째. 나머지 SaveImage는
///    PreviewImage로 바꿔 결과 파일이 섞이지 않게 한다.
/// 그 밖의 노드·값(모델·프롬프트·시드·샘플러…)은 파일에 적힌 그대로 보낸다 — 앱은 건드리지 않는다.</summary>
public static class CustomWorkflow
{
    public sealed record Prepared(JsonObject Prompt, string InputNodeId, string OutputNodeId, int LoadImageCount, int SaveImageCount);

    /// <summary>워크플로 JSON 파일을 읽고 규약(LoadImage·SaveImage)을 검사한다. 문제가 있으면 사람이 읽을 메시지의 InvalidDataException.
    /// 화면 저장 포맷이면 변환하지 않은 채로 돌려준다 — 실행하려면 엔진을 켠 뒤 <see cref="Resolve"/>를 거쳐야 한다.</summary>
    public static JsonObject Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"커스텀 워크플로 파일이 없어요: {path}");
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException($"워크플로 JSON을 읽지 못했어요: {ex.Message}"); }
        if (root is not JsonObject obj)
            throw new InvalidDataException("워크플로 JSON의 최상위가 객체가 아니에요.");
        try
        {
            if (WorkflowConverter.IsUiFormat(obj))
            {
                // 화면 포맷: 켜져 있는(mode 0) LoadImage·SaveImage가 하나씩은 있어야 한다
                var active = obj["nodes"]!.AsArray().OfType<JsonObject>()
                    .Where(n => (n["mode"]?.GetValue<int>() ?? 0) == 0).Select(n => n["type"]?.GetValue<string>()).ToList();
                if (!active.Contains("LoadImage")) throw new InvalidDataException("LoadImage 노드가 없어요 — 앱이 원고를 넣어 줄 LoadImage 노드가 하나 필요해요.");
                if (!active.Contains("SaveImage")) throw new InvalidDataException("SaveImage 노드가 없어요 — 앱이 결과를 받아 올 SaveImage 노드가 하나 필요해요.");
                return obj;
            }
            CheckApi(obj);
            return obj;
        }
        catch (Exception ex) when (IsMalformed(ex)) { throw Unreadable(ex); }
    }

    /// <summary>읽어 둔 워크플로를 실행할 수 있는 API 포맷으로 확정한다: 화면 포맷이면 변환하고, 서버에 없는 노드·파일을 미리 찾아 알린다.
    /// objectInfo는 켜져 있는 엔진의 /object_info (<see cref="ComfyClient.GetObjectInfoAsync"/>).</summary>
    public static JsonObject Resolve(JsonObject workflow, JsonObject objectInfo)
    {
        try
        {
            bool ui = WorkflowConverter.IsUiFormat(workflow);
            if (ui) CheckUiNodeIds(workflow);
            var api = ui ? WorkflowConverter.ToApi(workflow, objectInfo) : (JsonObject)workflow.DeepClone();
            CheckApi(api);
            WorkflowConverter.Validate(api, objectInfo);
            return api;
        }
        catch (Exception ex) when (IsMalformed(ex)) { throw Unconvertible(ex); }
    }

    // JsonNode·변환기는 꼴이 틀린 값을 영어 .NET 예외로 알린다 — 한 객체에 같은 키가 두 번이면 ArgumentException(객체를 처음 열 때),
    // 값의 형식이 틀리면 InvalidOperationException(숫자 자리의 글자, 객체 자리의 글자 …)이나 FormatException(정수 자리의 소수).
    // 설정 창·작업 시작 오류는 메시지를 그대로 보여 주므로 사람이 읽을 InvalidDataException으로 바꾸고, 원래 메시지를 끝에 붙여 원인이 보이게 한다(InnerException을 보여 주는 화면이 없다).
    // NullReferenceException은 바꾸지 않는다 — 파일 꼴이 아니라 코드 결함의 신호라, 파일 탓으로 바꿔 보이면 원인을 못 찾는다.
    internal static bool IsMalformed(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or FormatException;

    private static InvalidDataException Unreadable(Exception inner) =>
        new("워크플로 파일을 읽지 못했어요: ComfyUI가 저장하는 꼴과 다른 곳이 있어요(같은 키가 두 번 있거나, 숫자 자리에 글자가 있거나, 있어야 할 값이 빠진 것 등). " +
            "손으로 고친 곳이 있으면 되돌리거나, ComfyUI에서 열어 다시 저장하거나 'Export (API)'로 내보내 주세요." + Detail(inner), inner);

    // Resolve에서는 파일뿐 아니라 서버의 /object_info를 읽다가도 같은 예외가 난다 — ComfyUI가 저장한 그대로의 파일이 V3 노드의 글자 control_after_generate 때문에
    // '손으로 고친 파일'로 거절된 적이 있다(2026-11-11). 그래서 파일 탓으로 단정하지 않는다.
    private static InvalidDataException Unconvertible(Exception inner) =>
        new("워크플로를 이 ComfyUI에 맞게 바꾸지 못했어요: 워크플로 파일이나 ComfyUI가 알려 준 노드 정보에 앱이 풀지 못하는 꼴이 있어요. " +
            "파일을 손으로 고쳤으면 되돌리거나 ComfyUI에서 열어 다시 저장해 보고, 그래도 같으면 'Export (API)'로 내보낸 파일을 골라 주세요." + Detail(inner), inner);

    private static string Detail(Exception inner) => $" (자세히: {inner.Message})";

    /// <summary>화면 포맷 노드에 id가 없으면 변환기가 NullReferenceException으로 터진다. NullReferenceException은 통째로 바꾸지 않으므로 손으로 고친 파일의 이 꼴만 미리 가려 알린다.</summary>
    private static void CheckUiNodeIds(JsonObject ui)
    {
        if (ui["nodes"]!.AsArray().OfType<JsonObject>().Any(n => n["id"] is null))
            throw new InvalidDataException("워크플로 파일을 읽지 못했어요: id가 없는 노드가 있어요. " +
                "손으로 고친 곳이 있으면 되돌리거나, ComfyUI에서 열어 다시 저장하거나 'Export (API)'로 내보내 주세요.");
    }

    private static void CheckApi(JsonObject obj)
    {
        int loads = 0, saves = 0;
        foreach (var (id, node) in obj)
        {
            // 노드 자리에 객체가 아닌 값이 있으면(워크플로 고르기 창이 여는 workflows 바로 위 폴더의 comfy.settings.json 같은 파일) JsonNode 인덱서가
            // 영어 .NET 메시지의 InvalidOperationException을 던진다. Validate처럼 건너뛰지 않는다 — Prepare가 같은 값에서 다시 터진다.
            if (node is not JsonObject n)
                throw new InvalidDataException($"워크플로 파일이 아니에요 — '{id}' 항목이 노드가 아니에요. ComfyUI에서 저장했거나 'Export (API)'로 내보낸 워크플로 파일인지 확인해 주세요.");
            // class_type이 글자가 아니어도 GetValue가 같은 예외를 던진다 — 없는 것과 같이 알린다
            var ct = n["class_type"] is JsonValue v && v.TryGetValue<string>(out var type) ? type : null;
            if (ct == "LoadImage") loads++;
            else if (ct == "SaveImage") saves++;
            else if (ct == null) throw new InvalidDataException("API 포맷이 아니에요 — 각 노드에 class_type이 있어야 해요. 'Export (API)'로 내보낸 파일인지 확인해 주세요.");
        }
        if (loads == 0) throw new InvalidDataException("LoadImage 노드가 없어요 — 앱이 원고를 넣어 줄 LoadImage 노드가 하나 필요해요.");
        if (saves == 0) throw new InvalidDataException("SaveImage 노드가 없어요 — 앱이 결과를 받아 올 SaveImage 노드가 하나 필요해요.");
    }

    /// <summary>업로드한 입력 파일 이름과 출력 접두사를 끼운 실행용 그래프를 만든다 (원본 JsonObject는 건드리지 않는다).</summary>
    public static Prepared Prepare(JsonObject api, string uploadedName, string filenamePrefix)
    {
        var p = (JsonObject)api.DeepClone();
        string? inId = PickNode(p, "LoadImage", "입력", out int loads);
        string? outId = PickNode(p, "SaveImage", "출력", out int saves);
        if (inId == null || outId == null) throw new InvalidDataException("LoadImage/SaveImage 노드를 찾지 못했어요.");
        p[inId]!["inputs"]!["image"] = uploadedName;
        p[outId]!["inputs"]!["filename_prefix"] = filenamePrefix;
        foreach (var (id, node) in p)
            if (id != outId && node?["class_type"]?.GetValue<string>() == "SaveImage")
            {
                node["class_type"] = "PreviewImage";
                (node["inputs"] as JsonObject)?.Remove("filename_prefix");
            }
        return new Prepared(p, inId, outId, loads, saves);
    }

    /// <summary>classType 노드 가운데 제목(_meta.title)에 neko(대소문자 무관)나 word가 든 첫 노드, 없으면 첫 노드. count는 그 종류의 노드 수.
    /// 낱말은 종류마다 따로 본다(LoadImage는 "입력", SaveImage는 "출력") — 함께 보면 '입력 마스크 확인' 같은 확인용 SaveImage가 출력으로 뽑혀
    /// 진짜 결과 SaveImage가 PreviewImage로 바뀐다 (2026-11-11 — 그전에는 두 종류 모두 neko·입력·출력 세 낱말로 골랐다).</summary>
    private static string? PickNode(JsonObject p, string classType, string word, out int count)
    {
        string? first = null, titled = null; count = 0;
        foreach (var (id, node) in p)
        {
            if (node?["class_type"]?.GetValue<string>() != classType) continue;
            count++;
            first ??= id;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (titled == null && (title.Contains("neko", StringComparison.OrdinalIgnoreCase) || title.Contains(word)))
                titled = id;
        }
        return titled ?? first;
    }

    /// <summary>앱에 내장된 기본 검열제거 워크플로(사용자가 ComfyUI에서 짠 화면 포맷 JSON)를 ComfyUI의 워크플로 목록 폴더에 복사한다 —
    /// ComfyUI 화면의 워크플로 탐색기에서 "NekoManga_decensor_regen"으로 열어 고칠 수 있다. 복사된 경로를 돌려준다.</summary>
    public static string InstallBundledWorkflow(AppSettings s, string bundledJsonPath)
    {
        if (!File.Exists(bundledJsonPath)) throw new FileNotFoundException($"내장 워크플로 파일이 없어요: {bundledJsonPath}");
        var dir = Path.Combine(s.ComfyRoot, "ComfyUI", "user", "default", "workflows");
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, Path.GetFileName(bundledJsonPath));
        File.Copy(bundledJsonPath, dst, overwrite: true);
        return dst;
    }

    /// <summary>앱이 띄운 엔진의 ComfyUI 화면을 기본 브라우저로 연다 (같은 서버 — 모델·커스텀 노드·큐를 앱과 공유).</summary>
    public static void OpenUi(AppSettings s)
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{s.ComfyPort}/") { UseShellExecute = true });
    }
}
