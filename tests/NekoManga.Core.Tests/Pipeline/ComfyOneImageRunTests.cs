using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;
using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>그림 하나를 올려 워크플로 하나를 돌리는 길(채색·커스텀 검열 제거·표지 색 태그 뽑기 — PipelineRunner.RunOneImageWorkflowAsync)이 어떻게 끝나든 사용자의 ComfyUI 폴더에 아무것도 남기지 않는가.
/// 재생성·글자 자리 다시 그리기에서 먼저 막은 두 틈이 이 길에도 있었다(검토 2026-11-27): 올리는 요청의 답을 못 받으면 올린 파일이 남고,
/// 기다리다 그만둔 프롬프트는 서버에서 끝까지 돌아 치운 뒤에 출력을 남긴다.</summary>
public class ComfyOneImageRunTests
{
    /// <summary>ComfyUI의 요청 넷만 흉내 낸다: /upload/image(input에 쓴다), /prompt(SaveImage 접두사로 output에 파일을 만든다), /history(완료), POST /queue(거두기).</summary>
    private sealed class FakeComfy(string root) : HttpMessageHandler
    {
        public List<string> Uploads { get; } = [];
        public int Prompts { get; private set; }
        public List<string> Cancelled { get; } = [];
        /// <summary>true면 /history가 끝내 완료를 알리지 않는다.</summary>
        public bool NeverFinish { get; set; }
        /// <summary>올린 파일을 input에 쓴 **뒤에** 던질 예외 — 요청은 서버에 닿았는데 답을 못 받은 경우.</summary>
        public Func<Exception?>? ThrowAfterUpload { get; set; }
        private readonly Dictionary<string, string> _outputs = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json = "{}";
            if (path == "/upload/image" && request.Content is MultipartFormDataContent form)
            {
                var file = form.First(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "image");
                string name = file.Headers.ContentDisposition!.FileName!.Trim('"');
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "input"));
                await File.WriteAllBytesAsync(Path.Combine(root, "ComfyUI", "input", name), await file.ReadAsByteArrayAsync(ct), ct);
                Uploads.Add(name);
                if (ThrowAfterUpload?.Invoke() is { } lost) throw lost;
                json = new JsonObject { ["name"] = name }.ToJsonString();
            }
            else if (path == "/prompt")
            {
                var graph = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["prompt"]!.AsObject();
                string id = $"p{++Prompts}";
                string output = graph["9"]!["inputs"]!["filename_prefix"]!.GetValue<string>() + "_00001_.png";
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "output"));
                await File.WriteAllBytesAsync(Path.Combine(root, "ComfyUI", "output", output), [1, 2, 3], ct);
                _outputs[id] = output;
                json = new JsonObject { ["prompt_id"] = id }.ToJsonString();
            }
            else if (path == "/queue" && request.Method == HttpMethod.Post)
            {
                if (JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))?["delete"] is JsonArray ids)
                    Cancelled.AddRange(ids.Select(i => i!.GetValue<string>()));
            }
            else if (path.StartsWith("/history/", StringComparison.Ordinal) && !NeverFinish)
            {
                var id = path["/history/".Length..];
                json = new JsonObject
                {
                    [id] = new JsonObject
                    {
                        ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                        ["outputs"] = new JsonObject { ["9"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["filename"] = _outputs[id], ["subfolder"] = "", ["type"] = "output" }) } },
                    },
                }.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private const string UploadName = "nm_cl_test_0.png", OutPrefix = "nm_color_test_1";

    private static (AppSettings s, FakeComfy fake, ComfyClient client) Setup(TempDir t)
    {
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        var fake = new FakeComfy(s.ComfyRoot);
        return (s, fake, new ComfyClient("http://127.0.0.1:1", fake) { PollMs = 1 });
    }

    private static string OutDir(AppSettings s) => Path.Combine(s.ComfyRoot, "ComfyUI", "output");

    private static string[] Leftovers(AppSettings s) =>
        new[] { "input", "output" }.Select(d => Path.Combine(s.ComfyRoot, "ComfyUI", d)).Where(Directory.Exists).SelectMany(Directory.GetFiles).ToArray();

    /// <summary>올린 이름을 LoadImage에, 접두사를 SaveImage(노드 9)에 넣은 최소 그래프.</summary>
    private static JsonObject Graph(string uploaded) => new()
    {
        ["1"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = uploaded } },
        ["9"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["filename_prefix"] = OutPrefix } },
    };

    private static Task<byte[]> RunAsync(ComfyClient client, AppSettings s, CancellationToken ct, TimeSpan? timeout = null,
        Func<string, JsonObject>? graphFor = null, Func<ComfyClient.ComfyResult, byte[]>? take = null) =>
        PipelineRunner.RunOneImageWorkflowAsync(client, s.ComfyRoot, OutDir(s), [9, 9, 9], UploadName, OutPrefix, graphFor ?? Graph, timeout ?? TimeSpan.FromMinutes(1),
            take ?? (r => File.ReadAllBytes(r.OutputFiles[^1])), ct);

    [Fact]
    public async Task Success_HandsOverTheResultWhileItIsStillThere_ThenLeavesNothingBehind()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);

        var got = await RunAsync(client, s, CancellationToken.None);

        Assert.Equal([1, 2, 3], got);              // 결과를 넘길 때는 출력 파일이 아직 있었다
        Assert.Equal([UploadName], fake.Uploads);
        Assert.Empty(fake.Cancelled);              // 끝난 프롬프트를 거두라고 하지 않는다
        Assert.Empty(Leftovers(s));
    }

    [Fact]
    public async Task UploadAnswerLost_RemovesTheInputByTheNameItAskedFor()
    {
        // 올리는 요청이 서버에 닿아 파일은 생겼는데 답이 끊겼다 — 답(파일 이름)이 없어도 부탁한 이름으로 치운다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.ThrowAfterUpload = () => new HttpRequestException("연결이 끊겼다");

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(client, s, CancellationToken.None));

        Assert.Single(fake.Uploads);   // 전제: 파일은 서버에 쓰였다
        Assert.Equal(0, fake.Prompts);
        Assert.Empty(Leftovers(s));
    }

    [Fact]
    public async Task StoppedRightAfterTheUploadReachedTheServer_LeavesNoInputBehind()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var cts = new CancellationTokenSource();
        fake.ThrowAfterUpload = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(client, s, cts.Token));

        Assert.Single(fake.Uploads);
        Assert.Empty(Leftovers(s));
    }

    [Fact]
    public async Task PromptRunsPastTheTimeLimit_IsWithdrawnFromTheServer_AndNothingIsLeft()
    {
        // 기다리기만 그만두면 프롬프트는 서버에서 끝까지 돌아, 우리가 치운 뒤에 출력을 남긴다 — 거둬 달라고 한다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.NeverFinish = true;

        await Assert.ThrowsAsync<TimeoutException>(() => RunAsync(client, s, CancellationToken.None, TimeSpan.FromMilliseconds(80)));

        Assert.Equal(["p1"], fake.Cancelled);
        Assert.Empty(Leftovers(s));   // 그사이 서버가 쓴 출력도 접두사로 치운다
    }

    [Fact]
    public async Task TakingTheResultFails_TheFinishedPromptIsNotWithdrawn_AndNothingIsLeft()
    {
        // 프롬프트는 끝났다(결과가 왔다) — 결과를 읽다 실패해도 거둘 것은 없다. 올린 입력과 출력은 치운다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RunAsync(client, s, CancellationToken.None, take: _ => throw new InvalidDataException("결과를 읽지 못했다")));

        Assert.Equal(1, fake.Prompts);
        Assert.Empty(fake.Cancelled);
        Assert.Empty(Leftovers(s));
    }

    [Fact]
    public async Task GraphCannotBeBuilt_StillRemovesTheUpload()
    {
        // 워크플로 준비(CustomWorkflow.Prepare)가 던져도 올린 그림은 남기지 않는다 — 줄에 넣지 않았으니 거둘 것은 없다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RunAsync(client, s, CancellationToken.None, graphFor: _ => throw new InvalidDataException("워크플로가 잘못됐다")));

        Assert.Single(fake.Uploads);
        Assert.Empty(fake.Cancelled);
        Assert.Empty(Leftovers(s));
    }
}
