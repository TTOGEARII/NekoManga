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

    /// <summary>완료를 묻는 간격(ms) — 시험 이음새(가짜 응답기는 바로 끝나는데 프롬프트마다 2초를 기다리면 재시도 시험이 수십 초가 된다). 실제 서버에는 기본값 그대로.</summary>
    internal int PollMs { get; init; } = 2000;

    /// <summary>요청 하나를 기다리는 한도. HttpClient의 Timeout은 무한이다(긴 생성은 /history를 되풀이해 물어 기다린다) — 그래서 **소켓은 받는데 답을 안 하는 서버**(멈춘·심하게 스왑 중인 프로세스,
    /// 콘솔 글자 선택으로 멈춘 ComfyUI)에서는 올리기·줄 넣기·묻기 요청 하나가 영영 안 돌아왔고, 그 위의 제한 시간(<see cref="WaitAsync"/>의 timeout)도 요청 사이에서만 재서 걸리지 않았다(검토 2026-11-27).
    /// 넘기면 <see cref="TimeoutException"/>(사용자의 멈추기 = 취소 예외와 구별된다). 로컬 서버의 요청은 길어야 몇 초다 — 2분은 서버가 잠깐 바쁜 것(모델을 올리는 중)을 실패로 만들지 않을 만큼.</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>요청 하나를 <see cref="RequestTimeout"/> 안에서 보낸다 — 넘기면 TimeoutException, 사용자가 멈추면(<paramref name="ct"/>) 취소 예외 그대로.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> request, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        try { return await request(limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.F($"엔진(ComfyUI)이 {(int)RequestTimeout.TotalSeconds}초 동안 답하지 않는다냥."));
        }
    }

    /// <summary>서버가 해당 노드 클래스를 아는지 — 커스텀 노드 설치 후 서버를 안 껐으면 모를 수 있다.</summary>
    public async Task<bool> HasNodeAsync(string classType, CancellationToken ct)
    {
        try
        {
            using var r = await SendAsync(t => Http.GetAsync($"{baseUrl}/object_info/{classType}", t), ct);
            if (!r.IsSuccessStatusCode) return false;
            var body = await r.Content.ReadAsStringAsync(ct);
            return body.Contains(classType);
        }
        catch { return false; }
    }

    /// <summary>서버가 아는 모든 노드의 정의(입력 이름·순서·고를 수 있는 값). 화면 포맷 워크플로 변환과 실행 전 점검에 쓴다.</summary>
    public async Task<JsonObject> GetObjectInfoAsync(CancellationToken ct)
    {
        using var r = await SendAsync(t => Http.GetAsync($"{baseUrl}/object_info", t), ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) as JsonObject
               ?? throw new InvalidDataException(Loc.T("ComfyUI의 노드 정보(/object_info)를 읽지 못했다냥."));
    }

    /// <summary>input 폴더로 올리고 서버가 답한 파일 이름을 돌려준다(덮어쓰기라 보통 부탁한 <paramref name="fileName"/> 그대로다). 답을 못 받으면(멈춤·연결 오류·요청 한도) 파일은 이미 쓰였을 수 있다 —
    /// 호출부는 답으로 받은 이름과 **부탁한 이름 둘 다**로 치운다(WorkFolders.RemoveComfyFiles).</summary>
    public async Task<string> UploadImageAsync(byte[] png, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var img = new ByteArrayContent(png);
        img.Headers.ContentType = new("image/png");
        form.Add(img, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        using var resp = await SendAsync(t => Http.PostAsync($"{baseUrl}/upload/image", form, t), ct);
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
            using var resp = await SendAsync(t => Http.PostAsync($"{baseUrl}/prompt",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), t), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(Loc.F($"워크플로 검증 실패다냥: {Truncate(ExplainRejection(text), 2000)}"));
            return JsonNode.Parse(text)!["prompt_id"]!.GetValue<string>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelPromptAsync(id);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or TimeoutException)
        {
            // 요청이 가다가 끊겼다(연결 오류·답 없는 서버의 요청 한도): 서버가 받았는지 모른다 — 받았으면 줄에 들어가 끝까지 돌고, 호출부가 출력을 치운 뒤에 그림을 남긴다(검토 2026-11-27).
            // 우리가 지어 보낸 id로 거둔다(되는 데까지만 — 옛 ComfyUI는 제 id를 쓰니 못 거둔다). 서버가 거절한 것(위의 InvalidOperationException)은 줄에 없다
            await CancelPromptAsync(id);
            throw;
        }
    }

    /// <summary>ComfyUI가 워크플로를 받지 않으며 돌려준 답(JSON)을 사용자가 읽을 글로 푼다 — 알아보지 못한 꼴이면 답을 그대로 돌려준다.
    /// 예전에는 JSON을 그대로 보여 줬다: 새 PC에서 노드가 로드되지 않았을 때(2026-11-18) 쪽마다 `{"error": {"type": "missing_node_type"…`만 보여
    /// 무엇을 해야 하는지 알 수 없었다. 노드가 없는 경우와 모델 파일이 목록에 없는 경우(value_not_in_list)는 할 일을 같이 적는다.</summary>
    internal static string ExplainRejection(string body)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return body; }
        if (root?["error"] is not JsonObject err) return body;
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        var message = Str(err["message"]);
        // 노드 없음 — 새 ComfyUI는 종류(missing_node_type)와 노드 이름을 따로 주고, 옛 ComfyUI는 글("…node X does not exist.")로만 알린다
        var node = Str((err["extra_info"] as JsonObject)?["class_type"]);
        if (node.Length == 0 && System.Text.RegularExpressions.Regex.Match(message, "node (.+?) does not exist") is { Success: true } m) node = m.Groups[1].Value;
        if (node.Length > 0 && (Str(err["type"]) == "missing_node_type" || message.Contains("does not exist")))
            return Loc.F($"엔진(ComfyUI)에 '{node}' 노드가 없다냥 — 설정 → 구성요소에서 설치한 뒤 엔진을 껐다 켜 줘냥.");

        var lines = new List<string>();
        if (root["node_errors"] is JsonObject nodes)
            foreach (var (_, n) in nodes)
            {
                var cls = Str(n?["class_type"]);
                foreach (var e in n?["errors"] as JsonArray ?? [])
                {
                    var info = e?["extra_info"] as JsonObject;
                    var value = info?["received_value"] is { } rv ? (Str(rv) is { Length: > 0 } s ? s : rv.ToJsonString()) : "";
                    lines.Add(Str(e?["type"]) == "value_not_in_list"
                        ? Loc.F($"{cls}: '{value}'을(를) 엔진에서 찾을 수 없다냥 ({Str(info?["input_name"])}) — 설정 → 구성요소에서 받거나 파일 이름을 확인해 줘냥.")
                        : $"{cls}: {Str(e?["message"])} ({Str(e?["details"])})");
                }
            }
        if (lines.Count > 0) return string.Join("\n", lines.Distinct().Take(6));
        return message.Length > 0 ? message + (Str(err["details"]) is { Length: > 0 } d ? $" ({d})" : "") : body;
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
            await Task.Delay(PollMs, ct);
            HttpResponseMessage answer;
            try { answer = await SendAsync(t => Http.GetAsync($"{baseUrl}/history/{promptId}", t), ct); }
            catch (TimeoutException) { continue; }   // 이번 물음에 답이 없다(서버가 바쁘거나 멈췄다) — 한도까지는 다시 묻는다. 연결 오류(서버가 꺼졌다)는 전처럼 바로 던진다
            using var resp = answer;
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
                throw new InvalidOperationException(Loc.F($"엔진 실행 오류다냥: {msg}"));
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
        throw new TimeoutException(Loc.T("작업이 제한 시간을 넘겼다냥."));
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
                        if (tail.Length > 0) msg += Loc.F($"\n위치: {Truncate(tail, 600)}");
                        return Loc.F($"{msg}\n자세한 로그: {ComfyServer.LogPath}");
                    }
        }
        catch { }
        return Loc.F($"원인 미상이다냥 (로그: {ComfyServer.LogPath})");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
