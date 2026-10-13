using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Comfy;

/// <summary>ComfyUI HTTP API 클라이언트: 이미지 업로드 → 워크플로 실행 → 결과 수거.</summary>
public class ComfyClient(string baseUrl)
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>input 폴더로 업로드하고 서버가 부여한 파일명을 돌려준다.</summary>
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

    /// <summary>API 포맷 워크플로를 큐에 넣고 prompt_id를 돌려준다.</summary>
    public async Task<string> QueuePromptAsync(JsonObject prompt, CancellationToken ct)
    {
        var body = new JsonObject { ["prompt"] = prompt };
        using var resp = await Http.PostAsync($"{baseUrl}/prompt",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"워크플로 검증 실패: {Truncate(text, 2000)}");
        return JsonNode.Parse(text)!["prompt_id"]!.GetValue<string>();
    }

    public record ComfyResult(IReadOnlyList<string> OutputFiles, IReadOnlyList<string> Texts);

    /// <summary>완료까지 폴링하고 SaveImage 출력 파일 경로(서버 output 폴더 기준 절대경로)와 ShowText 내용을 돌려준다.</summary>
    public async Task<ComfyResult> WaitAsync(string promptId, string comfyOutputDir,
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
