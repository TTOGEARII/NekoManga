using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;

namespace NekoManga.Core.Tests;

/// <summary>ComfyClient — 서버도 네트워크도 없이 가짜 응답기(HttpMessageHandler)로: 사용자가 멈추면 서버에 남은 **우리** 프롬프트만 거두는지.</summary>
public class ComfyClientTests
{
    private const string Url = "http://127.0.0.1:1";
    private const string Ours = "11111111-aaaa", Theirs = "22222222-bbbb", ServerMinted = "33333333-cccc";

    /// <summary>요청을 적어 두고 정해 둔 답을 돌려주는 가짜 ComfyUI. /history는 늘 '아직'(빈 객체), GET /queue는 지금 도는 프롬프트 하나(없으면 빈 목록),
    /// POST /prompt는 서버가 지은 id(옛 ComfyUI처럼 보낸 id를 보지 않는다).</summary>
    private sealed class FakeComfy(string? running) : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, string Body)> _calls = [];

        /// <summary>서버가 꺼져 있다 — 모든 요청이 연결 오류.</summary>
        public bool Down { get; init; }

        /// <summary>GET /queue의 답을 이 글로 바꾼다(읽을 수 없는 답).</summary>
        public string? QueueReply { get; init; }

        /// <summary>POST /prompt를 받고 답하지 않는다(요청이 취소될 때까지) — 넣는 요청이 가 있는 사이.</summary>
        public bool HangSubmit { get; init; }

        /// <summary>POST /prompt가 도착했다.</summary>
        public TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>GET /history의 처음 이만큼은 답하지 않는다(요청이 취소될 때까지) — 그 뒤로는 '끝났다'고 답한다. 0이면 늘 '아직'.</summary>
        public int HangHistory { get; init; }
        private int _historyAsked;

        /// <summary>POST /upload/image를 받고 답하지 않는다.</summary>
        public bool HangUpload { get; init; }

        /// <summary>GET /object_info…에 답하지 않는다.</summary>
        public bool HangObjectInfo { get; init; }

        public IReadOnlyList<(string Method, string Path, string Body)> Calls { get { lock (_calls) return _calls.ToList(); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            lock (_calls) _calls.Add((request.Method.Method, path, body));
            if (Down) throw new HttpRequestException("연결할 수 없음");
            string json = "{}";
            if (request.Method == HttpMethod.Post && path == "/prompt")
            {
                Submitted.TrySetResult();
                if (HangSubmit) await Task.Delay(Timeout.Infinite, cancellationToken);
                json = new JsonObject { ["prompt_id"] = ServerMinted }.ToJsonString();
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/history/", StringComparison.Ordinal) && HangHistory > 0)
            {
                if (Interlocked.Increment(ref _historyAsked) <= HangHistory) await Task.Delay(Timeout.Infinite, cancellationToken);
                json = new JsonObject
                {
                    [path["/history/".Length..]] = new JsonObject
                    {
                        ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                        ["outputs"] = new JsonObject(),
                    },
                }.ToJsonString();
            }
            else if (request.Method == HttpMethod.Post && path == "/upload/image" && HangUpload)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            else if (request.Method == HttpMethod.Get && path.StartsWith("/object_info", StringComparison.Ordinal) && HangObjectInfo)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            else if (request.Method == HttpMethod.Get && path == "/queue")
            {
                // 항목의 짜임은 ComfyUI의 (번호, prompt_id, 프롬프트, …)
                json = QueueReply ?? new JsonObject
                {
                    ["queue_running"] = running == null ? new JsonArray() : new JsonArray(new JsonArray(7, running, new JsonObject())),
                    ["queue_pending"] = new JsonArray(),
                }.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>우리 프롬프트의 완료를 기다리는 중에 사용자가 [멈추기]를 누른다 — 기다리기는 취소 예외로 끝나야 한다.</summary>
    private static async Task<FakeComfy> StopWhileWaitingAsync(FakeComfy fake)
    {
        var client = new ComfyClient(Url, fake);
        using var cts = new CancellationTokenSource();
        var wait = client.WaitAsync(Ours, "out", TimeSpan.FromMinutes(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        return fake;
    }

    private static string[] DequeuedIds(FakeComfy fake) =>
        [.. JsonNode.Parse(Assert.Single(fake.Calls, c => c.Method == "POST" && c.Path == "/queue").Body)!["delete"]!.AsArray().Select(n => n!.GetValue<string>())];

    [Fact]
    public async Task WaitAsync_UserStopsWhileOurPromptIsRunning_InterruptsThatPrompt()
    {
        // [멈추기]를 눌러도 ComfyUI에서 돌던 생성은 끝까지 돌았다(실측 2026-11-13: 79초) — 그사이 다시 시작한 작업은 그 뒤에 줄을 선다
        var fake = await StopWhileWaitingAsync(new FakeComfy(running: Ours));

        var interrupt = Assert.Single(fake.Calls, c => c.Path == "/interrupt");
        Assert.Equal("POST", interrupt.Method);
        Assert.Equal(Ours, JsonNode.Parse(interrupt.Body)!["prompt_id"]!.GetValue<string>());   // 새 ComfyUI는 이 id가 돌고 있을 때만 끊는다
    }

    [Fact]
    public async Task WaitAsync_UserStopsWhileOurPromptIsStillQueued_TakesItOutOfTheQueue()
    {
        // 아직 차례가 안 온 우리 프롬프트(앞에 사용자가 직접 돌리는 것이 있다)는 줄에서 뺀다 — 안 빼면 멈춘 뒤에 혼자 돌기 시작한다
        var fake = await StopWhileWaitingAsync(new FakeComfy(running: Theirs));

        Assert.Equal([Ours], DequeuedIds(fake));
    }

    [Fact]
    public async Task WaitAsync_UserStops_TakesOursOutOfTheQueueBeforeAskingWhatIsRunning()
    {
        // 순서가 반대면, 무엇이 도는지 물은 뒤 줄에서 빼기 전에 차례가 온 우리 프롬프트가 '돌고 있지 않았음 + 줄에 없음'으로 빠져나가 끝까지 돈다
        var fake = await StopWhileWaitingAsync(new FakeComfy(running: Theirs));

        var calls = fake.Calls.Select(c => $"{c.Method} {c.Path}").ToList();
        int dequeue = calls.IndexOf("POST /queue"), ask = calls.IndexOf("GET /queue");
        Assert.True(0 <= dequeue && dequeue < ask, string.Join(", ", calls));
    }

    [Theory]
    [InlineData(Theirs)]   // 사용자가 ComfyUI 화면에서 직접 돌리는 것
    [InlineData(null)]     // 우리 것이 그사이 끝났다
    public async Task WaitAsync_UserStopsWhileOurPromptIsNotTheOneRunning_NeverInterrupts(string? running)
    {
        // 옛 ComfyUI의 /interrupt는 id를 보지 않고 지금 도는 것을 끊는다 — 남의 생성을 끊지 않게 우리 것이 돌고 있을 때만 보낸다
        var fake = await StopWhileWaitingAsync(new FakeComfy(running));

        Assert.Contains(fake.Calls, c => c.Method == "GET" && c.Path == "/queue");   // 전제: 무엇이 도는지 물어봤다
        Assert.DoesNotContain(fake.Calls, c => c.Path == "/interrupt");
    }

    [Theory]
    [InlineData("지금은 답할 수 없음")]             // json이 아니다
    [InlineData("""{"queue_running": 5}""")]        // 짜임이 다르다
    public async Task WaitAsync_UserStopsButWhatIsRunningIsUnreadable_NeverInterrupts(string reply)
    {
        // 무엇이 도는지 모르면 끊지 않는다 — 우리 것이 돌고 있더라도(끝까지 돌 뿐이다). 모르는 채 끊으면 옛 ComfyUI에서는 남의 생성일 수 있다
        var fake = await StopWhileWaitingAsync(new FakeComfy(running: Ours) { QueueReply = reply });

        Assert.Contains(fake.Calls, c => c.Method == "GET" && c.Path == "/queue");
        Assert.DoesNotContain(fake.Calls, c => c.Path == "/interrupt");
    }

    [Fact]
    public async Task WaitAsync_UserStopsWhileServerIsUnreachable_StillEndsAsCancellation()
    {
        // 거두기는 되는 데까지만 — 서버가 꺼져 있어도 멈추기는 취소로 끝난다(연결 오류로 바뀌면 러너가 그 장만 실패로 두고 다음 장을 돌린다)
        var fake = await StopWhileWaitingAsync(new FakeComfy(running: Ours) { Down = true });

        Assert.NotEmpty(fake.Calls);   // 전제: 거두려고 해 봤다
    }

    // ── 넣는 요청(POST /prompt) ──

    private static string SentId(FakeComfy fake) =>
        JsonNode.Parse(Assert.Single(fake.Calls, c => c.Path == "/prompt").Body)!["prompt_id"]!.GetValue<string>();

    [Fact]
    public async Task QueuePromptAsync_SendsItsOwnCanonicalIdButReturnsTheServersId()
    {
        // id를 우리가 지어 보낸다(답을 못 받아도 거둘 수 있게). 새 ComfyUI는 소문자 하이픈 UUID가 아니면 400으로 거절하고(0.39 validate_job_id),
        // 옛 ComfyUI는 보낸 id를 안 보고 제 id를 준다 — 기다릴 때는 늘 서버가 돌려준 id여야 한다
        var fake = new FakeComfy(running: null);

        var id = await new ComfyClient(Url, fake).QueuePromptAsync(new JsonObject(), CancellationToken.None);

        Assert.Equal(ServerMinted, id);
        var sent = SentId(fake);
        Assert.Equal(Guid.Parse(sent).ToString(), sent);
    }

    [Fact]
    public async Task QueuePromptAsync_UserStopsWhileThePromptIsBeingSubmitted_ReclaimsItByTheIdItSent()
    {
        // 넣는 요청이 가 있는 사이 멈추면 답(id)을 못 받는다 — 서버는 그 프롬프트를 이미 줄에 넣었을 수 있고, id를 모르면 끝까지 돈다
        var fake = new FakeComfy(running: null) { HangSubmit = true };
        using var cts = new CancellationTokenSource();
        var submit = new ComfyClient(Url, fake).QueuePromptAsync(new JsonObject(), cts.Token);
        await fake.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);

        Assert.Equal([SentId(fake)], DequeuedIds(fake));
    }

    // ── 답하지 않는 서버(소켓은 받는데 답이 없다 — 멈춘 프로세스) ──
    // HttpClient의 Timeout은 무한이다(긴 생성은 /history를 되풀이해 물어 기다린다). 요청 하나가 영영 안 돌아오면 그 위의 제한 시간도 재지지 않는다 — 요청마다 한도를 둔다(검토 2026-11-27)

    /// <summary>끝나지 않으면(요청 한도가 없던 때) 30초 뒤 실패로 끝낸다 — 시험이 영영 매달리지 않게. 돌려주는 것은 끝난 그 작업.</summary>
    private static async Task<Task<T>> Settled<T>(Task<T> work)
    {
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(first, work), "요청이 끝나지 않는다");
        return work;
    }

    [Fact]
    public async Task WaitAsync_OnePollGoesUnanswered_AsksAgain_AndStillGetsTheResult()
    {
        // 물음 하나에 답이 없다고 기다리기를 그만두지 않는다(서버가 잠깐 바빴을 뿐일 수 있다) — 한도까지는 다시 묻는다
        var fake = new FakeComfy(running: Ours) { HangHistory = 1 };
        var client = new ComfyClient(Url, fake) { PollMs = 1, RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var wait = await Settled(client.WaitAsync(Ours, "out", TimeSpan.FromMinutes(5), CancellationToken.None));

        Assert.Empty((await wait).OutputFiles);
        Assert.True(fake.Calls.Count(c => c.Path == "/history/" + Ours) >= 2);   // 답 없는 첫 물음 뒤에 다시 물었다(느린 기계에서 한 번 더 놓쳐도 된다)
    }

    [Fact]
    public async Task UploadImageAsync_ServerNeverAnswers_GivesUpAfterTheRequestLimit_AsATimeout()
    {
        var fake = new FakeComfy(running: null) { HangUpload = true };
        var client = new ComfyClient(Url, fake) { RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var upload = await Settled(client.UploadImageAsync([1, 2, 3], "nm_x.png", CancellationToken.None));

        // 사용자가 멈춘 것(취소 예외)과 구별된다 — 취소로 보이면 러너가 작업 전체를 멈춘다
        await Assert.ThrowsAsync<TimeoutException>(() => upload);
    }

    [Fact]
    public async Task QueuePromptAsync_ServerNeverAnswers_GivesUp_AndReclaimsThePromptByTheIdItSent()
    {
        // 답이 없어도 서버는 그 프롬프트를 줄에 넣었을 수 있다 — 우리가 지어 보낸 id로 거둔다
        var fake = new FakeComfy(running: null) { HangSubmit = true };
        var client = new ComfyClient(Url, fake) { RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var submit = await Settled(client.QueuePromptAsync(new JsonObject(), CancellationToken.None));

        await Assert.ThrowsAsync<TimeoutException>(() => submit);
        Assert.Equal([SentId(fake)], DequeuedIds(fake));
    }

    [Fact]
    public async Task GetObjectInfoAsync_ServerNeverAnswers_GivesUpAfterTheRequestLimit_AsATimeout()
    {
        // 커스텀 워크플로의 시작 전 점검·설정 창의 워크플로 점검이 부른다 — 답 없는 서버에서 창이 영영 매달리지 않게
        var fake = new FakeComfy(running: null) { HangObjectInfo = true };
        var client = new ComfyClient(Url, fake) { RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var info = await Settled(client.GetObjectInfoAsync(CancellationToken.None));

        await Assert.ThrowsAsync<TimeoutException>(() => info);
    }

    [Fact]
    public async Task HasNodeAsync_ServerNeverAnswers_SaysNo_InsteadOfHanging()
    {
        var fake = new FakeComfy(running: null) { HangObjectInfo = true };
        var client = new ComfyClient(Url, fake) { RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var has = await Settled(client.HasNodeAsync("LoadImage", CancellationToken.None));

        Assert.False(await has);
    }

    [Fact]
    public async Task UploadImageAsync_UserStopsWhileTheServerIsSilent_IsStillACancellation()
    {
        // 요청 한도는 사용자의 멈추기를 시간 초과로 바꾸지 않는다
        var fake = new FakeComfy(running: null) { HangUpload = true };
        var client = new ComfyClient(Url, fake) { RequestTimeout = TimeSpan.FromSeconds(60) };
        using var cts = new CancellationTokenSource();

        var upload = client.UploadImageAsync([1, 2, 3], "nm_x.png", cts.Token);
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await (await Settled(upload)));
    }
}
