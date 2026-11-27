using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>화 전체 검수가 번역기에 묻는 길(<see cref="LlmTranslator.AskAsync"/>)의 오류 처리 — 실제 번역기로는 정상 답 열한 번만 돌려 봤고 오류는 일으켜 보지 못했다(2026-11-28).
/// 보내는 자리(<see cref="LlmTranslator.SendOverride"/>)만 가짜로 바꿔 본다: 네트워크 오류는 한 번 다시 보내고, 제한 시간은 사용자가 멈춘 것과 갈라 알리고,
/// thinking을 모르는 모델에는 그것 없이 다시 묻고, 답이 길이로 끊기면 한 번 키워 다시 묻되 그래도 끊기면 받은 데까지 쓰지 않는다.</summary>
public class LlmAskTests
{
    private sealed class Wire
    {
        public readonly List<JsonNode> Bodies = [];
        public readonly Queue<Func<HttpResponseMessage>> Next = new();
        public LlmTranslator Llm(bool thinkingOff = false) =>
            new("http://test.invalid/v1/chat/completions", "key", "model-x", "DeepSeek", thinkingOff) { SendOverride = Send, ResendWait = TimeSpan.Zero };

        private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
        {
            Bodies.Add(JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!);
            return Next.Dequeue()();
        }
    }

    private static HttpResponseMessage Answer(string content, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = finish } } })),
    };

    [Fact]
    public async Task Ask_SendsTheInstructionAndTheText_AndReturnsTheAnswerBody()
    {
        var wire = new Wire();
        wire.Next.Enqueue(() => Answer("[2] 다나카 군"));

        var answer = await wire.Llm().AskAsync("지시문", "보내는 글", 2048, CancellationToken.None);

        Assert.Equal("[2] 다나카 군", answer);
        var body = Assert.Single(wire.Bodies);
        Assert.Equal(("model-x", 2048), (body["model"]!.GetValue<string>(), body["max_tokens"]!.GetValue<int>()));
        Assert.Equal(("지시문", "보내는 글"), (body["messages"]![0]!["content"]!.GetValue<string>(), body["messages"]![1]!["content"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Ask_ANetworkErrorOnce_IsSentAgain_ButNotForever()
    {
        var wire = new Wire();
        wire.Next.Enqueue(() => throw new HttpRequestException("끊겼다"));
        wire.Next.Enqueue(() => Answer("NONE"));
        Assert.Equal("NONE", await wire.Llm().AskAsync("s", "u", 2048, CancellationToken.None));
        Assert.Equal(2, wire.Bodies.Count);

        var dead = new Wire();
        dead.Next.Enqueue(() => throw new HttpRequestException("끊겼다"));
        dead.Next.Enqueue(() => throw new HttpRequestException("또 끊겼다"));
        await Assert.ThrowsAsync<HttpRequestException>(() => dead.Llm().AskAsync("s", "u", 2048, CancellationToken.None));
        Assert.Equal(2, dead.Bodies.Count);
    }

    [Fact]
    public async Task Ask_TheTimeLimit_IsATimeout_NotACancel()
    {
        // HttpClient의 제한 시간은 취소 예외로 온다 — 그대로 나가면 사용자가 멈춘 것으로 읽혀 '멈췄다'로 끝난다(번역기가 늦은 것을 알 수 없다)
        var wire = new Wire();
        wire.Next.Enqueue(() => throw new TaskCanceledException("제한 시간"));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => wire.Llm().AskAsync("s", "u", 2048, CancellationToken.None));

        Assert.Contains("DeepSeek", ex.Message);
        Assert.Single(wire.Bodies);   // 300초를 기다린 물음을 말없이 다시 보내지 않는다
    }

    [Fact]
    public async Task Ask_WhenTheUserStops_StaysACancel()
    {
        var wire = new Wire();
        using var cts = new CancellationTokenSource();
        wire.Next.Enqueue(() => { cts.Cancel(); throw new TaskCanceledException("멈춤"); });

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.Llm().AskAsync("s", "u", 2048, cts.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task Ask_AModelThatDoesNotKnowThinking_IsAskedAgainWithoutIt()
    {
        var wire = new Wire();
        wire.Next.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        wire.Next.Enqueue(() => Answer("NONE"));

        Assert.Equal("NONE", await wire.Llm(thinkingOff: true).AskAsync("s", "u", 2048, CancellationToken.None));

        Assert.NotNull(wire.Bodies[0]["thinking"]);
        Assert.Null(wire.Bodies[1]["thinking"]);

        // thinking을 끄지 않은 물음의 400, 다른 오류는 다시 묻지 않고 알린다
        foreach (var (off, code) in new[] { (false, HttpStatusCode.BadRequest), (true, HttpStatusCode.Unauthorized) })
        {
            var refused = new Wire();
            refused.Next.Enqueue(() => new HttpResponseMessage(code) { Content = new StringContent("{}") });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.Llm(off).AskAsync("s", "u", 2048, CancellationToken.None));
            Assert.Contains(((int)code).ToString(), ex.Message);
            Assert.Single(refused.Bodies);
        }
    }

    [Fact]
    public async Task Ask_AnAnswerCutByLength_IsAskedOnceMoreWithMoreRoom_ThenRefused()
    {
        // 끊긴 답을 받은 데까지 쓰면 끊긴 마지막 줄이 번역으로 들어간다 — 한 번 키워 다시 묻고, 그래도 끊기면 던진다
        var wire = new Wire();
        wire.Next.Enqueue(() => Answer("[1] 다나", finish: "length"));
        wire.Next.Enqueue(() => Answer("[1] 다나카 군"));
        Assert.Equal("[1] 다나카 군", await wire.Llm().AskAsync("s", "u", 2048, CancellationToken.None));
        Assert.Equal([2048, 8192], wire.Bodies.Select(b => b["max_tokens"]!.GetValue<int>()));

        var cut = new Wire();
        cut.Next.Enqueue(() => Answer("[1] 다나", finish: "length"));
        cut.Next.Enqueue(() => Answer("[1] 다나카", finish: "length"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.Llm().AskAsync("s", "u", 2048, CancellationToken.None));
        Assert.Equal(2, cut.Bodies.Count);
    }
}
