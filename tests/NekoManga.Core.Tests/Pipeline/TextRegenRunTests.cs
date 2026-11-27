using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;
using static NekoManga.Core.Tests.ComfyTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>글자 지운 자리 다시 그리기의 그리는 길(PipelineRunner.TextRegenAsync·TextRegenFileAsync·CheckTextRegenModels)을 가짜 ComfyUI로 —
/// 창마다 그림 하나를 올리고, 제 구멍만 붙이고, 빈 조각이면 새 시드로 다시 그리고, 실패하면 그림을 그대로 두고(LaMa 결과), ComfyUI 폴더에 아무것도 남기지 않는다.
/// 서버도 모델도 없다: 올린 파일은 가짜가 임시 폴더의 input에 쓰고, 출력은 시험이 정한 그림이다(완료 묻기 간격은 ComfyClient.PollMs로 1ms).</summary>
public class TextRegenRunTests
{
    /// <summary>ComfyUI의 요청만 흉내 낸다: /upload/image(input 폴더에 쓴다), /prompt(그래프를 적고 SaveImage의 출력을 만든다), /history/{id}(완료). 그 밖(/queue 등)은 빈 답.</summary>
    private sealed class FakeComfy(string root) : HttpMessageHandler
    {
        public List<string> Uploads { get; } = [];
        public List<JsonObject> Graphs { get; } = [];
        public int Prompts => Graphs.Count;
        /// <summary>프롬프트 번호(0부터)와 올린 그림(BGR, 보내는 크기)을 받아 출력 그림을 만든다 — 없으면 같은 크기의 회색(128).</summary>
        public Func<int, Mat, Mat>? Output { get; set; }
        /// <summary>프롬프트 번호 → true면 그 /prompt를 500으로 거절한다.</summary>
        public Func<int, bool>? FailPrompt { get; set; }
        /// <summary>프롬프트 번호 → 예외를 주면 그 /prompt에서 던진다(HttpClient의 시간 초과 = TaskCanceledException 흉내).</summary>
        public Func<int, Exception?>? ThrowOnPrompt { get; set; }
        /// <summary>true면 /history가 끝내 완료를 알리지 않는다(멈춘 서버 — 제한 시간을 넘긴다).</summary>
        public bool NeverFinish { get; set; }
        /// <summary>서버에 거두라고 온 프롬프트 id(POST /queue {"delete": […]}).</summary>
        public List<string> Cancelled { get; } = [];
        /// <summary>true면 /upload/image를 500으로 거절한다.</summary>
        public bool FailUpload { get; set; }
        /// <summary>올린 파일을 input에 쓴 **뒤에** 던질 예외(올린 이름을 받는다) — 요청은 서버에 닿았는데 답을 못 받은 경우.</summary>
        public Func<string, Exception?>? ThrowAfterUpload { get; set; }
        /// <summary>true면 프롬프트가 끝났다고 답하되 출력 그림이 없다.</summary>
        public bool NoOutputs { get; set; }
        /// <summary>true면 출력 파일에 그림이 아닌 바이트를 쓴다.</summary>
        public bool GarbageOutput { get; set; }
        private readonly Dictionary<string, string> _outputs = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json = "{}";
            if (path == "/upload/image" && request.Content is MultipartFormDataContent form)
            {
                if (FailUpload) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var file = form.First(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "image");
                string name = file.Headers.ContentDisposition!.FileName!.Trim('"');
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "input"));
                await File.WriteAllBytesAsync(Path.Combine(root, "ComfyUI", "input", name), await file.ReadAsByteArrayAsync(ct), ct);
                lock (Uploads) Uploads.Add(name);
                if (ThrowAfterUpload?.Invoke(name) is { } lost) throw lost;
                json = new JsonObject { ["name"] = name }.ToJsonString();
            }
            else if (path == "/prompt")
            {
                var graph = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["prompt"]!.AsObject();
                string id; int n;
                lock (Graphs) { Graphs.Add(graph); n = Graphs.Count - 1; id = $"p{Graphs.Count}"; }
                if (ThrowOnPrompt?.Invoke(n) is { } boom) throw boom;
                if (FailPrompt?.Invoke(n) == true)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\": \"fake\"}", Encoding.UTF8, "application/json") };
                var output = Str(Inputs(Only(graph, "SaveImage").Node)["filename_prefix"]) + "_00001_.png";
                var upName = Str(Inputs(Only(graph, "LoadImage").Node)["image"]);
                using var up = ImgIo.Read(Path.Combine(root, "ComfyUI", "input", upName), ImreadModes.Color);
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "output"));
                using var drawn = Output?.Invoke(n, up) ?? new Mat(up.Rows, up.Cols, MatType.CV_8UC3, new Scalar(128, 128, 128));
                if (GarbageOutput) File.WriteAllBytes(Path.Combine(root, "ComfyUI", "output", output), [1, 2, 3, 4]);
                else ImgIo.Write(Path.Combine(root, "ComfyUI", "output", output), drawn);
                lock (Graphs) _outputs[id] = output;
                json = new JsonObject { ["prompt_id"] = id }.ToJsonString();
            }
            else if (path == "/queue" && request.Method == HttpMethod.Post)
            {
                if (JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))?["delete"] is JsonArray ids)
                    lock (Cancelled) Cancelled.AddRange(ids.Select(i => i!.GetValue<string>()));
            }
            else if (path.StartsWith("/history/", StringComparison.Ordinal) && !NeverFinish)
            {
                var id = path["/history/".Length..];
                string output;
                lock (Graphs) output = _outputs[id];
                json = new JsonObject
                {
                    [id] = new JsonObject
                    {
                        ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                        ["outputs"] = new JsonObject { ["20"] = new JsonObject { ["images"] = NoOutputs ? new JsonArray() : new JsonArray(new JsonObject { ["filename"] = output, ["subfolder"] = "", ["type"] = "output" }) } },
                    },
                }.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>부른 스레드에서 바로 적는 IProgress — Progress&lt;T&gt;는 동기화 컨텍스트로 미뤄 시험이 끝난 뒤에 올 수 있다.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static (AppSettings s, FakeComfy fake, ComfyClient client) Setup(TempDir t, int retries = 0, long seed = 7)
    {
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        s.TextRegen = true; s.TextRegenSeed = seed; s.TextRegenWhiteRetries = retries;
        var fake = new FakeComfy(s.ComfyRoot);
        return (s, fake, new ComfyClient("http://127.0.0.1:1", fake) { PollMs = 1 });
    }

    private static string OutDir(AppSettings s) => Path.Combine(s.ComfyRoot, "ComfyUI", "output");

    private static string[] ComfyLeftovers(AppSettings s) =>
        new[] { "input", "output" }.Select(d => Path.Combine(s.ComfyRoot, "ComfyUI", d)).Where(Directory.Exists).SelectMany(Directory.GetFiles).ToArray();

    /// <summary>가로 1px 검은 줄을 4px마다 그은 BGR 쪽 — 속도선·빗금 바탕의 대역(흰 75%).</summary>
    private static Mat Stripes(int width, int height)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < height; y += 4) img.Line(0, y, width - 1, y, Scalar.Black, 1);
        return img;
    }

    private static Mat Filled(Mat like, int gray) => new(like.Rows, like.Cols, MatType.CV_8UC3, new Scalar(gray, gray, gray));

    /// <summary>두 그림이 다른 픽셀 수 — <paramref name="except"/> 안은 세지 않는다.</summary>
    private static int ChangedOutside(Mat a, Mat b, Rect except)
    {
        using var diff = new Mat(); using var gray = new Mat();
        Cv2.Absdiff(a, b, diff);
        Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
        gray.Rectangle(except, Scalar.Black, -1);
        return Cv2.CountNonZero(gray);
    }

    private static readonly Vec3b Gray128 = new(128, 128, 128), White = new(255, 255, 255);

    // ── 창마다 그리기 ──

    [Fact]
    public async Task TextRegenAsync_PastesOnlyTheHole_AndLeavesNothingInTheComfyFolders()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        var hole = new Rect(300, 200, 120, 60);
        using var mask = RectMask(800, 600, hole);

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 3, CancellationToken.None);

        Assert.Equal((1, 1, null), (r.Drawn, r.Total, r.Error));
        Assert.Equal(1, fake.Prompts);
        Assert.StartsWith("nm_textregen_", Assert.Single(fake.Uploads));
        Assert.Equal(Gray128, img.At<Vec3b>(230, 360));   // 구멍 안은 그린 것
        // 붙이는 범위는 구멍 + (TextRegenInpaintGrow 8 + 2)px 안쪽뿐이다 — 그 밖은 한 픽셀도 바뀌지 않는다
        Assert.Equal(0, ChangedOutside(img, before, new Rect(hole.X - 11, hole.Y - 11, hole.Width + 22, hole.Height + 22)));
        Assert.Empty(ComfyLeftovers(s));   // 사용자의 ComfyUI 폴더다
    }

    [Fact]
    public async Task TextRegenAsync_MonochromePage_PastesTheResultInGray()
    {
        // 흑백 쪽인데 SDXL이 색을 얹는 일이 있다(실제 26쪽: 빗금 위 대사 자리에 연두색 얼룩, 2026-11-27) — 붙이기 전에 밝기만 남긴다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.Output = (_, up) => new Mat(up.Rows, up.Cols, MatType.CV_8UC3, new Scalar(40, 200, 90));   // 연두색
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Null(r.Error);
        var px = img.At<Vec3b>(230, 360);
        Assert.True(px.Item0 == px.Item1 && px.Item1 == px.Item2, $"구멍 안에 색이 남았다: {px.Item0},{px.Item1},{px.Item2}");
        Assert.InRange(px.Item0, 100, 200);   // 그린 것(연두의 밝기)이 붙긴 했다 — 흰 종이도 검은 줄도 아니다
    }

    [Fact]
    public async Task TextRegenAsync_TwoGroupsWithOverlappingWindows_EachWindowDrawsOnlyItsOwnHole()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.Output = (n, up) => Filled(up, n == 0 ? 128 : 64);   // 첫 창은 128, 둘째 창은 64로 그린다
        using var runner = new PipelineRunner(s);
        using var img = Stripes(2064, 2880);
        var a = new Rect(300, 300, 120, 200); var c = new Rect(300, 900, 120, 200);   // 다른 묶음, 창은 서로 겹친다(TextRegenPlanTests)
        using var mask = RectMask(2064, 2880, a);
        mask.Rectangle(c, Scalar.White, -1);

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal((2, 2, null), (r.Drawn, r.Total, r.Error));
        Assert.Equal(2, fake.Prompts);
        Assert.Equal(2, fake.Uploads.Count);
        // 위의 구멍은 통째로 첫 창의 것 — 둘째 창이 걸친 아래쪽 끝까지 128 그대로(다시 그려졌으면 64)
        Assert.Equal(Gray128, img.At<Vec3b>(400, 360));
        Assert.Equal(Gray128, img.At<Vec3b>(497, 360));
        Assert.Equal(new Vec3b(64, 64, 64), img.At<Vec3b>(1000, 360));
        Assert.Equal(new Vec3b(64, 64, 64), img.At<Vec3b>(903, 360));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_OutputSizeDiffersFromTheWindow_IsResizedBack()
    {
        // 쪽이 창보다 작으면 창 = 쪽 전체(610×805)이고 보내는 크기는 8의 배수(608×800)다 — 받은 그림을 창 크기로 되돌려 붙인다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        Size? sent = null;
        fake.Output = (_, up) => { sent = up.Size(); return Filled(up, 128); };
        using var runner = new PipelineRunner(s);
        using var img = Stripes(610, 805);
        using var mask = RectMask(610, 805, new Rect(200, 300, 100, 80));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal(new Size(608, 800), sent);
        Assert.Equal((1, null), (r.Drawn, r.Error));
        Assert.Equal(Gray128, img.At<Vec3b>(340, 250));
        Assert.Equal(new Size(610, 805), img.Size());
    }

    // ── 빈 조각 다시 그리기 ──

    [Fact]
    public async Task TextRegenAsync_BlankResult_RetriesWithADerivedSeed_AndKeepsTheDrawnOne()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t, retries: 1, seed: 7);
        fake.Output = (n, up) => n == 0 ? Filled(up, 255) : up.Clone();   // 첫 시도는 흰 칠, 둘째는 줄무늬 그대로
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var notes = new List<string>();

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None, new SyncProgress(notes.Add));

        Assert.Equal((1, null), (r.Drawn, r.Error));
        Assert.Equal(2, fake.Prompts);
        Assert.Single(fake.Uploads);   // 올린 그림은 그대로 쓰고 그래프만 다시 보낸다
        Assert.Equal(7, Num(Inputs(Only(fake.Graphs[0], "KSampler").Node)["seed"]));
        Assert.Equal(RegenWhiteCheck.RetrySeed(7, 1), Inputs(Only(fake.Graphs[1], "KSampler").Node)["seed"]!.GetValue<long>());
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));   // 고른 것은 줄무늬(= 원래 그림과 같다)
        Assert.Contains(notes, n => n.Contains("빈 조각"));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_EveryAttemptBlank_StopsAtTheLimit_AndKeepsTheFirst()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t, retries: 2);
        fake.Output = (n, up) => Filled(up, 255 - n);   // 255·254·253 — 전부 흰 칠(≥ 235)
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal(3, fake.Prompts);   // 첫 시도 + 재시도 2
        Assert.Equal(White, img.At<Vec3b>(230, 360));   // 똑같이 비었으면 먼저 그린 것
    }

    [Theory]
    [InlineData(0, 120, 60)]   // 재시도 0 = 끔
    [InlineData(1, 10, 10)]    // 구멍이 300px 미만이면 잴 조각이 없다 — 한 번만 그린다
    public async Task TextRegenAsync_RetriesOff_OrOnlyTinyPieces_DrawsOnce(int retries, int holeW, int holeH)
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t, retries);
        fake.Output = (_, up) => Filled(up, 255);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, holeW, holeH));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal(1, fake.Prompts);
        Assert.Equal((1, null), (r.Drawn, r.Error));
    }

    [Fact]
    public async Task TextRegenAsync_RetryAttemptFails_KeepsTheFirstResult_WithoutAnError()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t, retries: 2);
        fake.Output = (_, up) => Filled(up, 255);
        fake.FailPrompt = n => n == 1;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var notes = new List<string>();

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None, new SyncProgress(notes.Add));

        Assert.Equal(2, fake.Prompts);   // 실패한 재시도 뒤에는 더 보내지 않는다
        Assert.Equal((1, null), (r.Drawn, r.Error));
        Assert.Equal(White, img.At<Vec3b>(230, 360));
        Assert.Contains(notes, n => n.Contains("멈췄다"));
        Assert.Empty(ComfyLeftovers(s));
    }

    // ── 실패·멈춤 ──

    [Fact]
    public async Task TextRegenAsync_FirstAttemptFails_ReportsTheError_AndLeavesTheImageAlone()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = _ => true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal((0, 1), (r.Drawn, r.Total));
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_HttpTimeoutOnThePrompt_IsAnError_NotACancel()
    {
        // 사용자가 멈춘 것이 아닌 취소 예외(다른 곳의 시간 초과가 TaskCanceledException으로 올라온 것 — rules/pipeline.md의 v1.5.4 실사고)는 작업 전체를 멈추지 않고 그 단계의 실패로 둔다.
        // ComfyClient 자신은 이제 요청 한도를 TimeoutException으로 알린다(ComfyClientTests) — 여기서는 러너의 예외 가르기만 본다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.ThrowOnPrompt = _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal(0, r.Drawn);
        Assert.NotNull(r.Error);
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_PromptRunsPastTheTimeLimit_IsCancelledOnTheServer()
    {
        // 기다리기만 그만두면 프롬프트는 서버에서 끝까지 돌아, 치운 뒤에 출력 파일을 남긴다(사용자의 ComfyUI 폴더) — 거둬 달라고 한다(검수 2026-11-26)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.NeverFinish = true;
        using var runner = new PipelineRunner(s) { TextRegenTimeout = TimeSpan.FromMilliseconds(80) };
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(["p1"], fake.Cancelled);
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_SecondWindowFails_KeepsWhatTheFirstWindowDrew_AndStops()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = n => n >= 1;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(2064, 2880); using var before = img.Clone();
        var a = new Rect(300, 300, 120, 200); var far = new Rect(1500, 2000, 120, 200); var farther = new Rect(300, 2400, 120, 200);
        using var mask = RectMask(2064, 2880, a);
        mask.Rectangle(far, Scalar.White, -1); mask.Rectangle(farther, Scalar.White, -1);

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal((1, 3), (r.Drawn, r.Total));
        Assert.NotNull(r.Error);
        Assert.Equal(2, fake.Prompts);   // 실패한 창 뒤의 창은 보내지 않는다(서버가 죽었으면 창마다 기다리게 된다)
        Assert.Equal(Gray128, img.At<Vec3b>(400, 360));
        Assert.Equal(before.At<Vec3b>(2100, 1560), img.At<Vec3b>(2100, 1560));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_CancelledWhileDrawing_Throws_AndLeavesNothingInTheComfyFolders()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var cts = new CancellationTokenSource();
        fake.Output = (_, up) => { cts.Cancel(); return Filled(up, 128); };   // 그리는 도중에 사용자가 멈춘다
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.TextRegenAsync(client, OutDir(s), img, mask, 0, cts.Token));

        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
        Assert.Empty(ComfyLeftovers(s));
    }

    // ── 파일 단위(1차가 부르는 길) ──

    private static (string Img, string Mask) WritePage(TempDir t, Mat img, Mat mask, string stem = "p01")
    {
        var ip = t.At("work", stem + ".inpainted.png"); var mp = t.At("work", stem + ".textregen.png");
        Directory.CreateDirectory(Path.GetDirectoryName(ip)!);
        Assert.True(ImgIo.Write(ip, img)); Assert.True(ImgIo.Write(mp, mask));
        return (ip, mp);
    }

    [Fact]
    public async Task TextRegenFileAsync_Success_OverwritesTheErasedPage()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

        Assert.Null(warn);
        using var saved = ImgIo.Read(ip, ImreadModes.Color);
        Assert.Equal(Gray128, saved.At<Vec3b>(230, 360));
        Assert.Equal(img.At<Vec3b>(100, 100), saved.At<Vec3b>(100, 100));
    }

    [Fact]
    public async Task TextRegenFileAsync_ComfyError_ReturnsAWarning_AndLeavesTheLamaResult()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = _ => true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        var bytes = File.ReadAllBytes(ip);

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

        Assert.Contains("LaMa", warn);
        Assert.Equal(bytes, File.ReadAllBytes(ip));   // 쪽은 실패하지 않는다 — 지운 그림 그대로 다음 단계로 간다
    }

    [Fact]
    public async Task TextRegenFileAsync_ResultCannotBeWritten_ReturnsAWarning_AndLeavesTheLamaResult()
    {
        // 다시 그린 그림을 못 쓰면(읽기 전용·디스크 가득) 그 장을 실패시키지 않는다 — 지운 그림(LaMa 결과)은 그대로 있다. ImgIo.Write는 못 쓰면 던진다(false는 인코딩 실패뿐 — 검토 2026-11-26)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        var bytes = File.ReadAllBytes(ip);
        File.SetAttributes(ip, FileAttributes.ReadOnly);
        try
        {
            var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

            Assert.Contains("쓰지 못했다냥", warn);
            Assert.Equal(1, fake.Prompts);                    // 그리기는 했다
            Assert.Equal(bytes, File.ReadAllBytes(ip));       // 지운 그림은 그대로
            Assert.Empty(ComfyLeftovers(s));
        }
        finally { File.SetAttributes(ip, FileAttributes.Normal); }
    }

    [Fact]
    public async Task TextRegenFileAsync_ProgressSaysHowManyBlocksWentToSdxl()
    {
        // 대상 고르기(RegenStats 0.98)가 블록을 몇 개나 SDXL로 보내는지 진행 줄에 보인다 — 창 하나에 30~60초라 켜 놓고 느려진 까닭을 알 수 있어야 한다(검토 2026-11-26)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        var notes = new List<string>();

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None, new SyncProgress(notes.Add), blocks: 3);

        Assert.Null(warn);
        Assert.Contains(notes, n => n.Contains("블록 3개") && n.Contains("1/1"));
    }

    // ── 검토(2026-11-27)가 짚은 틈: 올린 파일·줄에 든 프롬프트가 남는 길, 붙이는 범위, 실패의 알림 ──

    [Fact]
    public async Task TextRegenAsync_StoppedRightAfterTheUploadReachedTheServer_LeavesNoInputBehind()
    {
        // 올리는 요청이 서버에 닿아 파일은 생겼는데 답(파일 이름)을 받기 전에 멈췄다 — 답이 없어도 우리가 지어 보낸 이름으로 치운다(서버는 overwrite=true라 그 이름 그대로 쓴다)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var cts = new CancellationTokenSource();
        fake.ThrowAfterUpload = _ => { cts.Cancel(); return new OperationCanceledException(cts.Token); };
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.TextRegenAsync(client, OutDir(s), img, mask, 0, cts.Token));

        Assert.Single(fake.Uploads);
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_UploadAnswerLost_FailsThatWindow_AndLeavesNoInputBehind()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.ThrowAfterUpload = _ => new HttpRequestException("connection reset");
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal(0, r.Drawn);
        Assert.NotNull(r.Error);
        Assert.Equal(0, fake.Prompts);
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_UploadRejected_FailsThatWindow_WithoutQueueingAnything()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailUpload = true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Equal((0, 1), (r.Drawn, r.Total));
        Assert.NotNull(r.Error);
        Assert.Equal(0, fake.Prompts);
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public async Task TextRegenAsync_PromptFinishesWithoutAnOutput_FailsThatWindow()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.NoOutputs = true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Contains("결과 파일이 없다냥", r.Error);
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
        Assert.Empty(ComfyLeftovers(s));   // 답에 이름이 없어도 접두사로 찾아 치운다
    }

    [Fact]
    public async Task TextRegenAsync_OutputThatIsNotAnImage_FailsThatWindow()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.GarbageOutput = true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600); using var before = img.Clone();
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Contains("읽지 못했다냥", r.Error);
        Assert.Equal(0, ChangedOutside(img, before, new Rect(0, 0, 0, 0)));
        Assert.Empty(ComfyLeftovers(s));
    }

    [Fact]
    public async Task TextRegenAsync_QueueRequestFailsInTransit_WithdrawsThePromptItMayHaveQueued()
    {
        // 큐에 넣는 요청이 가다가 끊겼다(연결 오류·HTTP 시간 초과): 서버가 받았는지 모른다 — 받았으면 끝까지 돌아 치운 뒤에 출력을 남긴다. 우리가 지어 보낸 id로 거둔다.
        // 서버가 거절한 것(검증 실패 500)은 줄에 없으니 거둘 것도 없다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.ThrowOnPrompt = _ => new HttpRequestException("connection reset");
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.True(Guid.TryParse(Assert.Single(fake.Cancelled), out _));

        var (s2, fake2, client2) = Setup(t);
        fake2.FailPrompt = _ => true;
        using var runner2 = new PipelineRunner(s2);
        await runner2.TextRegenAsync(client2, OutDir(s2), img, mask, 0, CancellationToken.None);
        Assert.Empty(fake2.Cancelled);
    }

    [Theory]
    [InlineData(8, 1.0, 10)]
    [InlineData(8, 0.877, 12)]    // 1168 창을 1024로 줄여 보낸다: 보낸 그림의 8px = 창의 9.1px
    [InlineData(8, 0.5, 20)]      // 넓힘 8 + 블러 2 = 보낸 그림의 10px = 창의 20px(블러도 보낸 그림의 px다 — 넓힘만 늘리면 붙이는 가장자리가 블러 띠 안에 떨어진다)
    [InlineData(0, 0.5, 4)]
    [InlineData(8, 1.3, 10)]      // 키워 보내는 일은 없다 — 있어도 좁히지 않는다
    public void TextRegenPasteReach_FollowsTheWindowScale(int grow, double scale, int expected) =>
        Assert.Equal(expected, PipelineRunner.TextRegenPasteReach(grow, scale));

    [Fact]
    public async Task TextRegenAsync_DownscaledWindow_PastesAsFarAsTheGraphRedrew()
    {
        // 긴 블록(700px)의 창은 1168이라 1024로 줄여 보낸다. 그래프는 보낸 그림에서 8px 넓혀 다시 그리니 창에서는 9.1px이다 —
        // 창의 px로 8 + 2만 붙이면 다시 그린 띠의 바깥쪽이 잘려 딱딱한 가장자리가 남는다(검토 2026-11-27)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(2064, 2880); using var before = img.Clone();
        var hole = new Rect(500, 500, 60, 700);
        using var mask = RectMask(2064, 2880, hole);

        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(Gray128, img.At<Vec3b>(801, hole.Right - 1 + 11));                                   // 구멍에서 11px — 예전 범위(10px) 밖, 지금 범위(12px) 안
        Assert.Equal(before.At<Vec3b>(801, hole.Right - 1 + 14), img.At<Vec3b>(801, hole.Right - 1 + 14));   // 14px은 그대로
    }

    [Fact]
    public async Task TextRegenFileAsync_SomeWindowsDrawnThenAFailure_SaysHowFarItGot()
    {
        // 창 셋 가운데 하나를 그리고 멈췄다: 그린 데까지는 쓰고, 경고는 "LaMa 결과로 간다"가 아니라 어디까지 했는지를 말한다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = n => n >= 1;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(2064, 2880);
        var a = new Rect(300, 300, 120, 200);
        using var mask = RectMask(2064, 2880, a);
        mask.Rectangle(new Rect(1500, 2000, 120, 200), Scalar.White, -1);
        mask.Rectangle(new Rect(300, 2400, 120, 200), Scalar.White, -1);
        var (ip, mp) = WritePage(t, img, mask);

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

        Assert.Contains("1/3", warn);
        Assert.DoesNotContain("실패다냥 — LaMa 결과로 간다냥", warn);
        using var saved = ImgIo.Read(ip, ImreadModes.Color);
        Assert.Equal(Gray128, saved.At<Vec3b>(a.Y + 100, a.X + 60));   // 첫 창은 그려져 저장됐다
    }

    [Fact]
    public async Task TextRegenFileAsync_PathThatCannotEvenBeOpened_ReturnsAWarning()
    {
        // 읽기가 입출력 예외가 아닌 것으로 죽어도(잘못된 경로 글자 → ArgumentException) 이 단계의 실패는 경고일 뿐이다 — 그 장은 LaMa 결과로 끝까지 간다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, _) = WritePage(t, img, mask);

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, t.At("mask\0.png"), 0, CancellationToken.None);

        Assert.NotNull(warn);
        Assert.Equal(0, fake.Prompts);
    }

    [Fact]
    public async Task TextRegenFileAsync_TwoFailuresInARow_StopsTryingForTheRestOfTheJob()
    {
        // 프롬프트를 받기만 하고 끝내지 못하는 ComfyUI: 쪽마다 한도(10분)까지 기다리면 긴 작업이 기어간다. 이어서 두 번 실패하면 이 작업의 남은 쪽은 하지 않는다(경고는 그 쪽에 한 번)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = _ => true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);

        var first = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);
        var second = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 1, CancellationToken.None);
        var third = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 2, CancellationToken.None);
        var fourth = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 3, CancellationToken.None);

        Assert.Contains("LaMa", first);
        Assert.Contains("남은 쪽", second);
        Assert.Null(third);
        Assert.Null(fourth);
        Assert.Equal(2, fake.Prompts);   // 셋째 쪽부터는 보내지도 않는다
    }

    [Fact]
    public async Task TextRegenFileAsync_AFailureThenASuccess_KeepsTrying()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = n => n is 0 or 2;   // 한 번 건너 한 번 실패 — 이어서 두 번은 아니다
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);

        for (int page = 0; page < 4; page++) await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, page, CancellationToken.None);

        Assert.Equal(4, fake.Prompts);
    }

    [Fact]
    public async Task TextRegenFileAsync_BreakerTripsOnAPageThatDrewSomeWindows_StillSaysHowFarItGot()
    {
        // 이어서 두 번째로 실패한 쪽이 창 셋 가운데 하나는 그려 저장했다: '그만둔다'는 알림에 묻혀 "LaMa 결과로 간다"고만 하면 그 쪽에 다시 그린 창이 있다는 것이 안 보인다(검토 2026-11-27)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = n => n != 1;   // 첫 쪽의 첫 창 실패 → 둘째 쪽의 첫 창만 성공
        using var runner = new PipelineRunner(s);
        using var small = Stripes(800, 600);
        using var smallMask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip1, mp1) = WritePage(t, small, smallMask, "one");
        using var img = Stripes(2064, 2880);
        using var mask = RectMask(2064, 2880, new Rect(300, 300, 120, 200));
        mask.Rectangle(new Rect(1500, 2000, 120, 200), Scalar.White, -1);
        mask.Rectangle(new Rect(300, 2400, 120, 200), Scalar.White, -1);
        var (ip2, mp2) = WritePage(t, img, mask, "two");

        var first = await runner.TextRegenFileAsync(client, OutDir(s), ip1, mp1, 0, CancellationToken.None);
        var second = await runner.TextRegenFileAsync(client, OutDir(s), ip2, mp2, 1, CancellationToken.None);
        var third = await runner.TextRegenFileAsync(client, OutDir(s), ip1, mp1, 2, CancellationToken.None);

        Assert.DoesNotContain("남은 쪽", first);
        Assert.Contains("1/3", second);
        Assert.Contains("남은 쪽", second);
        Assert.Null(third);
        Assert.Equal(3, fake.Prompts);
    }

    [Fact]
    public async Task TextRegenFileAsync_SomeWindowsDrawnButTheResultCannotBeWritten_DoesNotClaimThePartialWork()
    {
        // 창 하나를 그리고 멈췄는데 그 그림도 쓰지 못했다(읽기 전용): 파일은 LaMa 결과 그대로다 — "1/3창까지 했다"고 하면 거짓이다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = n => n >= 1;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(2064, 2880);
        using var mask = RectMask(2064, 2880, new Rect(300, 300, 120, 200));
        mask.Rectangle(new Rect(1500, 2000, 120, 200), Scalar.White, -1);
        mask.Rectangle(new Rect(300, 2400, 120, 200), Scalar.White, -1);
        var (ip, mp) = WritePage(t, img, mask);
        var bytes = File.ReadAllBytes(ip);
        File.SetAttributes(ip, FileAttributes.ReadOnly);
        try
        {
            var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

            Assert.DoesNotContain("1/3", warn);
            Assert.Contains("LaMa 결과로 간다냥", warn);
            Assert.Equal(bytes, File.ReadAllBytes(ip));
        }
        finally { File.SetAttributes(ip, FileAttributes.Normal); }
    }

    [Fact]
    public async Task NewJob_StartsCountingFailuresAfresh()
    {
        // '이어서 두 번 실패하면 그만둔다'는 작업 하나의 일이다 — 같은 러너로 다음 작업을 시작하면(ComfyUI를 고친 뒤) 다시 해 본다
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        fake.FailPrompt = _ => true;
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);
        await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 1, CancellationToken.None);
        Assert.Null(await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 2, CancellationToken.None));   // 전제: 그만뒀다
        Assert.Equal(2, fake.Prompts);

        // 새 작업의 시작(없는 원고의 다시 하기 — 모델 없이 시작 준비만 돈다)
        await runner.RedoPagesAsync([(t.At("없는 원고.png"), t.At("out", "없는 원고.png"))], new Sink(), CancellationToken.None);
        fake.FailPrompt = null;

        Assert.Null(await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None));
        Assert.Equal(3, fake.Prompts);
    }

    private sealed class Sink : IProgress<PageProgress>
    {
        public void Report(PageProgress value) { }
    }

    [Fact]
    public void JoinWarning_KeepsTheEarlierWarning_AndDoesNotRepeatTheSameOne()
    {
        // 장의 경고 자리는 하나다. 덮어쓰면 "컬러 쪽이라 건너뛴다" 같은 알림이 앞선 진짜 경고(옛 결과를 못 옮김)를 지우고, 뒤 단계의 경고가 글자 자리 다시 그리기의 실패를 지운다(검토 2026-11-27)
        Assert.Equal("첫 경고", PipelineRunner.JoinWarning(null, "첫 경고"));
        Assert.Equal("첫 경고 / 둘째 경고", PipelineRunner.JoinWarning("첫 경고", "둘째 경고"));
        Assert.Equal("첫 경고 / 둘째 경고", PipelineRunner.JoinWarning("첫 경고 / 둘째 경고", "둘째 경고"));
        Assert.Equal("첫 경고", PipelineRunner.JoinWarning("첫 경고", "첫 경고"));
    }

    [Fact]
    public async Task TextRegenFileAsync_MaskOfAnotherSize_ReturnsAWarning_WithoutSendingAnything()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(400, 300, new Rect(100, 100, 60, 40));
        var (ip, mp) = WritePage(t, img, mask);
        var bytes = File.ReadAllBytes(ip);

        var warn = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

        Assert.NotNull(warn);
        Assert.Equal(0, fake.Prompts);
        Assert.Empty(fake.Uploads);
        Assert.Equal(bytes, File.ReadAllBytes(ip));
    }

    [Fact]
    public async Task TextRegenFileAsync_MissingMaskFile_ReturnsAWarning()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        File.Delete(mp);

        Assert.NotNull(await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None));
        Assert.Equal(0, fake.Prompts);
    }

    [Fact]
    public async Task TextRegenFileAsync_ColorPage_IsSkipped_AndSaysSoInThePageNote()
    {
        // 기본 프롬프트가 흑백 만화용이라 컬러 표지에 회색 조각이 생긴다 — LaMa 결과 그대로 둔다.
        // 건너뛴 것은 그 장의 끝 줄에 남긴다: 누렇게 바랜 흑백 스캔도 채도 때문에 여기로 오는데, 지나가는 진행 줄에만 적으면 켰는데 아무 일도 없는 까닭이 보이지 않는다(검수 2026-11-26)
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, new Scalar(200, 160, 120));
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);
        var bytes = File.ReadAllBytes(ip);

        var note = await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None);

        Assert.Contains("컬러", note);
        Assert.Equal(0, fake.Prompts);
        Assert.Equal(bytes, File.ReadAllBytes(ip));
    }

    [Fact]
    public async Task TextRegenFileAsync_EmptyMask_DoesNothing()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = new Mat(600, 800, MatType.CV_8UC1, Scalar.Black);
        var (ip, mp) = WritePage(t, img, mask);

        Assert.Null(await runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, CancellationToken.None));
        Assert.Equal(0, fake.Prompts);
    }

    [Fact]
    public async Task TextRegenFileAsync_Cancelled_Throws()
    {
        using var t = new TempDir();
        var (s, fake, client) = Setup(t);
        using var cts = new CancellationTokenSource();
        fake.Output = (_, up) => { cts.Cancel(); return Filled(up, 128); };
        using var runner = new PipelineRunner(s);
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var (ip, mp) = WritePage(t, img, mask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.TextRegenFileAsync(client, OutDir(s), ip, mp, 0, cts.Token));
    }

    // ── 시작 전 모델 확인 ──

    private static (AppSettings s, string Checkpoints, string ControlNets) ModelSetup(TempDir t)
    {
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        s.TextRegen = true;
        s.RegenCheckpoint = "regen.safetensors"; s.RegenControlNet = "regen-union.safetensors";
        var models = Path.Combine(s.ComfyRoot, "ComfyUI", "models");
        return (s, Path.Combine(models, "checkpoints"), Path.Combine(models, "controlnet"));
    }

    [Fact]
    public void CheckTextRegenModels_ModelsThere_Passes_UsingTheTextBoxesWhenFilled()
    {
        using var t = new TempDir();
        var (s, ck, cn) = ModelSetup(t);
        s.TextRegenCheckpoint = "text.safetensors"; s.TextRegenControlNet = "text-union.safetensors";
        RuntimeTestHelpers.Touch(Path.Combine(ck, "text.safetensors"));
        RuntimeTestHelpers.Touch(Path.Combine(cn, "text-union.safetensors"));
        using var runner = new PipelineRunner(s);

        runner.CheckTextRegenModels();   // 재생성 모델 파일은 없어도 된다 — 글자용 칸이 채워져 있다
    }

    [Fact]
    public void CheckTextRegenModels_EmptyBoxes_LookForTheRegenModels()
    {
        using var t = new TempDir();
        var (s, ck, cn) = ModelSetup(t);
        RuntimeTestHelpers.Touch(Path.Combine(cn, "regen-union.safetensors"));
        using var runner = new PipelineRunner(s);

        var ex = Assert.Throws<FileNotFoundException>(runner.CheckTextRegenModels);
        Assert.Contains("regen.safetensors", ex.Message);

        RuntimeTestHelpers.Touch(Path.Combine(ck, "regen.safetensors"));
        runner.CheckTextRegenModels();
    }

    [Fact]
    public void CheckTextRegenModels_MissingControlNet_Throws_UnlessTheStrengthIsZero()
    {
        using var t = new TempDir();
        var (s, ck, _) = ModelSetup(t);
        RuntimeTestHelpers.Touch(Path.Combine(ck, "regen.safetensors"));
        using var runner = new PipelineRunner(s);

        var ex = Assert.Throws<FileNotFoundException>(runner.CheckTextRegenModels);
        Assert.Contains("regen-union.safetensors", ex.Message);

        s.TextRegenCnStrength = 0;   // ControlNet 없이 그리는 설정이면 파일도 필요 없다(그래프에 노드가 없다)
        runner.CheckTextRegenModels();
    }

    [Fact]
    public async Task CheckTextRegenModels_PromaxUnderTheInstalledName_IsUsedForThisRun_WithoutChangingAnySetting()
    {
        // 사용자 워크플로의 promax 이름이 설정에 있고 파일은 앱 설치기의 이름으로 있다 — 설치된 promax를 쓴다(재생성과 같은 대체).
        // **설정에는 적지 않는다**(검토 2026-11-26): `--run`은 끝나며 설정을 저장한다 — 글자용 칸에 적으면 "비움 = 재생성의 ControlNet을 따른다"가 말없이 풀린다. 이 러너의 그래프에만 쓴다
        using var t = new TempDir();
        var (s, ck, cn) = ModelSetup(t);
        s.RegenControlNet = "diffusion_pytorch_model_promax.safetensors";
        s.ColorControlNet = "controlnet-union-sdxl-promax.safetensors";
        s.TextRegenSeed = 7; s.TextRegenWhiteRetries = 0;
        RuntimeTestHelpers.Touch(Path.Combine(ck, "regen.safetensors"));
        RuntimeTestHelpers.Touch(Path.Combine(cn, "controlnet-union-sdxl-promax.safetensors"));
        using var runner = new PipelineRunner(s);

        runner.CheckTextRegenModels();

        Assert.Equal("", s.TextRegenControlNet);
        Assert.Equal("diffusion_pytorch_model_promax.safetensors", s.RegenControlNet);
        var fake = new FakeComfy(s.ComfyRoot);
        var client = new ComfyClient("http://127.0.0.1:1", fake) { PollMs = 1 };
        using var img = Stripes(800, 600);
        using var mask = RectMask(800, 600, new Rect(300, 200, 120, 60));
        var r = await runner.TextRegenAsync(client, OutDir(s), img, mask, 0, CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal("controlnet-union-sdxl-promax.safetensors", Str(Inputs(Only(fake.Graphs[0], "ControlNetLoader").Node)["control_net_name"]));
    }
}
