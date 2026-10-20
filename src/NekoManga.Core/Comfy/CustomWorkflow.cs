using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>사용자가 ComfyUI 화면에서 직접 짠 워크플로를 앱 파이프라인에 끼우는 다리 (2026-10-20).
///
/// 입력은 ComfyUI의 **API 포맷** JSON(워크플로 메뉴 → "Export (API)")이다. 화면 저장 포맷(nodes/links 배열)은 노드 정의 없이는
/// 위젯 값의 이름을 알 수 없어 받지 않는다 — 대신 어떤 포맷인지 알려 주는 오류를 낸다.
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

    /// <summary>API 포맷 JSON 파일을 읽고 규약을 검사한다. 문제가 있으면 사람이 읽을 메시지의 InvalidDataException.</summary>
    public static JsonObject Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"커스텀 워크플로 파일이 없어요: {path}");
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException($"워크플로 JSON을 읽지 못했어요: {ex.Message}"); }
        if (root is not JsonObject obj)
            throw new InvalidDataException("워크플로 JSON의 최상위가 객체가 아니에요.");
        if (obj.ContainsKey("nodes") && obj.ContainsKey("links"))
            throw new InvalidDataException("화면 저장 포맷(nodes/links)이에요. ComfyUI에서 워크플로 메뉴 → 'Export (API)'로 내보낸 파일을 골라 주세요.");
        int loads = 0, saves = 0;
        foreach (var (_, node) in obj)
        {
            var ct = node?["class_type"]?.GetValue<string>();
            if (ct == "LoadImage") loads++;
            else if (ct == "SaveImage") saves++;
            else if (ct == null) throw new InvalidDataException("API 포맷이 아니에요 — 각 노드에 class_type이 있어야 해요. 'Export (API)'로 내보낸 파일인지 확인해 주세요.");
        }
        if (loads == 0) throw new InvalidDataException("LoadImage 노드가 없어요 — 앱이 원고를 넣어 줄 LoadImage 노드가 하나 필요해요.");
        if (saves == 0) throw new InvalidDataException("SaveImage 노드가 없어요 — 앱이 결과를 받아 올 SaveImage 노드가 하나 필요해요.");
        return obj;
    }

    /// <summary>업로드한 입력 파일 이름과 출력 접두사를 끼운 실행용 그래프를 만든다 (원본 JsonObject는 건드리지 않는다).</summary>
    public static Prepared Prepare(JsonObject api, string uploadedName, string filenamePrefix)
    {
        var p = (JsonObject)api.DeepClone();
        string? inId = PickNode(p, "LoadImage", out int loads);
        string? outId = PickNode(p, "SaveImage", out int saves);
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

    private static string? PickNode(JsonObject p, string classType, out int count)
    {
        string? first = null, titled = null; count = 0;
        foreach (var (id, node) in p)
        {
            if (node?["class_type"]?.GetValue<string>() != classType) continue;
            count++;
            first ??= id;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (titled == null && (title.Contains("neko", StringComparison.OrdinalIgnoreCase) || title.Contains("입력") || title.Contains("출력")))
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
