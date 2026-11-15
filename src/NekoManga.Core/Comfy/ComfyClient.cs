using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI HTTP API 클라이언트: 이미지 업로드 → 워크플로 실행 → 결과 수거.</summary>
public class ComfyClient(string baseUrl)
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient Http = SharedHttp;

    /// <summary>시험 이음새 — 서버 없이 가짜 응답기로 요청·응답을 본다.</summary>
    internal ComfyClient(string baseUrl, HttpMessageHandler handler) : this(baseUrl) =>
        Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>input 폴더로 업로드하고 서버가 부여한 파일명을 돌려준다.</summary>
    /// <summary>서버가 해당 노드 클래스를 아는지 — 커스텀 노드 설치 후 서버를 안 껐으면 모를 수 있다.</summary>
    public async Task<bool> HasNodeAsync(string classType, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync($"{baseUrl}/object_info/{classType}", ct);
            if (!r.IsSuccessStatusCode) return false;
            var body = await r.Content.ReadAsStringAsync(ct);
            return body.Contains(classType);
        }
        catch { return false; }
    }

    /// <summary>서버가 아는 모든 노드의 정의(입력 이름·순서·고를 수 있는 값). 화면 포맷 워크플로 변환과 실행 전 점검에 쓴다.</summary>
    public async Task<JsonObject> GetObjectInfoAsync(CancellationToken ct)
    {
        using var r = await Http.GetAsync($"{baseUrl}/object_info", ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) as JsonObject
               ?? throw new InvalidDataException("ComfyUI의 노드 정보(/object_info)를 읽지 못했어요.");
    }

    public async Task<string> UploadImageAsync(byte[] png, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var img = new ByteArrayContent(png);
        img.Headers.ContentType = new("image/png");
        form.Add(img, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        using var resp = await Http.PostAsync($"{baseUrl}/upload/image", form, ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        return json["name"]!.GetValue<string>();
    }

    /// <summary>API 포맷 워크플로를 큐에 넣고 prompt_id를 돌려준다. 넣는 요청이 가 있는 사이 사용자가 멈추면(<paramref name="ct"/>) 그 프롬프트를 거둔다.</summary>
    public async Task<string> QueuePromptAsync(JsonObject prompt, CancellationToken ct)
    {
        // id를 지어 보낸다 — 요청 도중 멈추면 답(id)을 못 받는데, 서버는 이미 줄에 넣었을 수 있다(id를 모르면 끝까지 돈다).
        // 새 ComfyUI는 소문자 하이픈 UUID만 받고(0.39 validate_job_id — 아니면 400), 옛 ComfyUI는 이 값을 안 보고 제 id를 준다. 그래서 돌려주는 것은 늘 서버가 답한 id
        var id = Guid.NewGuid().ToString();
        var body = new JsonObject { ["prompt"] = prompt, ["prompt_id"] = id };
        try
        {
            using var resp = await Http.PostAsync($"{baseUrl}/prompt",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"워크플로 검증 실패: {Truncate(text, 2000)}");
            return JsonNode.Parse(text)!["prompt_id"]!.GetValue<string>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelPromptAsync(id);
            throw;
        }
    }

    public record ComfyResult(IReadOnlyList<string> OutputFiles, IReadOnlyList<string> Texts);

    /// <summary>완료까지 폴링하고 SaveImage 출력 파일 경로(서버 output 폴더 기준 절대경로)와 ShowText 내용을 돌려준다.
    /// 사용자가 멈추면(<paramref name="ct"/>) 서버에 남은 이 프롬프트를 거두고 취소 예외로 끝난다.</summary>
    public async Task<ComfyResult> WaitAsync(string promptId, string comfyOutputDir,
        TimeSpan timeout, CancellationToken ct)
    {
        try { return await PollAsync(promptId, comfyOutputDir, timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 폴링만 그만두면 생성은 서버에서 끝까지 돈다(실측 2026-11-13: [멈추기] 뒤 79초) — 그사이 다시 시작한 작업은 그 뒤에 줄을 선다
            await CancelPromptAsync(promptId);
            throw;
        }
    }

    /// <summary>이 프롬프트만 거둔다 — 아직 줄에 있으면 빼고, 지금 돌고 있으면 끊는다. 다른 프롬프트(사용자가 ComfyUI 화면에서 직접 돌리는 것)는 건드리지 않는다.
    /// 되는 데까지만: 서버가 꺼졌거나 답이 없어도 예외를 내지 않는다(멈추기를 막지 않게).</summary>
    public async Task CancelPromptAsync(string promptId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using (await PostJsonAsync("/queue", new JsonObject { ["delete"] = new JsonArray(promptId) }, cts.Token)) { }
            // 끊기 전에 무엇이 도는지 본다 — 옛 ComfyUI의 /interrupt는 본문을 안 보고 지금 도는 것을 끊는다(새 ComfyUI는 prompt_id가 돌 때만).
            // 항목의 짜임은 (번호, prompt_id, 프롬프트, …)
            using var queue = await Http.GetAsync($"{baseUrl}/queue", cts.Token);
            if (!queue.IsSuccessStatusCode) return;
            var running = (JsonNode.Parse(await queue.Content.ReadAsStringAsync(cts.Token)) as JsonObject)?["queue_running"] as JsonArray;
            bool ours = running != null && running.Any(item =>
                item is JsonArray { Count: >= 2 } a && a[1] is JsonValue id && id.TryGetValue<string>(out var s) && s == promptId);
            if (!ours) return;
            using (await PostJsonAsync("/interrupt", new JsonObject { ["prompt_id"] = promptId }, cts.Token)) { }
        }
        catch { }
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, JsonObject body, CancellationToken ct) =>
        Http.PostAsync($"{baseUrl}{path}", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

    private async Task<ComfyResult> PollAsync(string promptId, string comfyOutputDir,
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);
            using var resp = await Http.GetAsync($"{baseUrl}/history/{promptId}", ct);
            if (!resp.IsSuccessStatusCode) continue;
            var hist = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (hist == null || !hist.ContainsKey(promptId)) continue;

            var entry = hist[promptId]!;
            var status = entry["status"];
            var statusStr = status?["status_str"]?.GetValue<string>();
            var completed = status?["completed"]?.GetValue<bool>() ?? false;
            if (statusStr == "error")
            {
                var msg = ExtractError(status);
                throw new InvalidOperationException($"채색 엔진 실행 오류: {msg}");
            }
            if (!completed) continue;

            var files = new List<string>();
            var texts = new List<string>();
            if (entry["outputs"] is JsonObject outputs)
                foreach (var (_, outNode) in outputs)
                {
                    if (outNode?["images"] is JsonArray imgs)
                        foreach (var im in imgs)
                        {
                            if (im?["type"]?.GetValue<string>() != "output") continue;
                            var sub = im["subfolder"]?.GetValue<string>() ?? "";
                            files.Add(Path.Combine(comfyOutputDir, sub, im["filename"]!.GetValue<string>()));
                        }
                    if (outNode?["text"] is JsonArray txts)
                        foreach (var t in txts)
                            if (t != null) texts.Add(t.GetValue<string>());
                }
            return new ComfyResult(files, texts);
        }
        throw new TimeoutException("채색 작업이 제한 시간을 넘겼어요.");
    }

    private static string ExtractError(JsonNode? status)
    {
        try
        {
            if (status?["messages"] is JsonArray msgs)
                foreach (var m in msgs)
                    if (m is JsonArray { Count: >= 2 } arr && arr[0]?.GetValue<string>() == "execution_error")
                    {
                        var info = arr[1];
                        // 예외 메시지만으로는 원인을 못 찾는 경우가 많다(예: KSampler "[Errno 22] Invalid argument").
                        // 어느 파이썬 프레임에서 터졌는지가 결정적이라 트레이스백 마지막 줄들을 함께 보여준다.
                        var tail = info?["traceback"] is JsonArray tb
                            ? string.Join(" | ", tb.TakeLast(3).Select(t => t?.GetValue<string>()?.Trim()).Where(t => !string.IsNullOrEmpty(t)))
                            : "";
                        var msg = $"{info?["node_type"]} — {Truncate(info?["exception_message"]?.GetValue<string>() ?? "", 500)}";
                        if (tail.Length > 0) msg += $"\n위치: {Truncate(tail, 600)}";
                        return $"{msg}\n자세한 로그: {ComfyServer.LogPath}";
                    }
        }
        catch { }
        return $"원인 미상 (로그 확인: {ComfyServer.LogPath})";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
