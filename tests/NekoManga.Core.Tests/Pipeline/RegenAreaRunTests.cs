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

/// <summary>재생성 한 영역의 길(PipelineRunner.ComfyRegenDecensorAsync)을 가짜 ComfyUI로 — 자리별 태그가 켜져 있고 조각의 성별이 섞인 영역이면
/// 자리 마스크를 올리고, 그래프에 자리마다의 프롬프트가 들어가고, 끝나면 올린 파일 셋과 출력을 ComfyUI 폴더에서 치우고, 결과를 영역 자리에 붙인다.
/// 서버도 모델도 없다: 올린 파일은 가짜가 임시 폴더의 input에 쓰고, 출력은 가짜가 만든 회색 그림이다. (완료를 2초 간격으로 묻는 길이라 2초쯤 걸린다.)</summary>
public class RegenAreaRunTests
{
    /// <summary>ComfyUI의 세 요청만 흉내 낸다: /upload/image(input 폴더에 쓴다), /prompt(그래프를 적고 SaveImage의 출력을 만든다), /history/{id}(완료).
    /// 프롬프트마다 번호(p1, p2, …)를 매기고 그래프를 전부 적어 둔다 — 흰 조각 재시도(2026-11-20)는 한 영역에 프롬프트를 여러 번 보낸다. 출력 색은 <see cref="OutputColor"/>(시도 번호 → 색, 기본 회색).</summary>
    private sealed class FakeComfy(string root) : HttpMessageHandler
    {
        public List<string> Uploads { get; } = [];
        public List<JsonObject> Graphs { get; } = [];
        /// <summary>마지막 그래프.</summary>
        public JsonObject? Graph => Graphs.Count > 0 ? Graphs[^1] : null;
        public int Prompts => Graphs.Count;
        public Func<int, Scalar>? OutputColor { get; set; }
        /// <summary>시도 번호 → true면 그 /prompt를 500으로 거절한다(재시도 중의 ComfyUI 오류 흉내).</summary>
        public Func<int, bool>? FailPrompt { get; set; }
        /// <summary>true면 /history가 끝내 완료를 알리지 않는다(멈춘 서버 — 제한 시간을 넘긴다).</summary>
        public bool NeverFinish { get; set; }
        /// <summary>서버에 거두라고 온 프롬프트 id(POST /queue {"delete": […]}).</summary>
        public List<string> Cancelled { get; } = [];
        /// <summary>올린 파일을 input에 쓴 **뒤에** 던질 예외(올린 이름을 받는다) — 요청은 서버에 닿았는데 답을 못 받은 경우.</summary>
        public Func<string, Exception?>? ThrowAfterUpload { get; set; }
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
                lock (Uploads) Uploads.Add(name);
                if (ThrowAfterUpload?.Invoke(name) is { } lost) throw lost;
                json = new JsonObject { ["name"] = name }.ToJsonString();
            }
            else if (path == "/prompt")
            {
                var graph = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["prompt"]!.AsObject();
                string id;
                lock (Graphs) { Graphs.Add(graph); id = $"p{Graphs.Count}"; }
                if (FailPrompt?.Invoke(Graphs.Count - 1) == true)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\": \"fake\"}", Encoding.UTF8, "application/json") };
                var (_, save) = Only(graph, "SaveImage");
                var output = Str(Inputs(save)["filename_prefix"]) + "_00001_.png";
                // 자른 그림의 크기는 올린 영역 그림(LoadImage nm_regen_*)에서 — 1단계 CN 강도 0이면 EmptyImage 노드가 없다
                var regionName = OfType(graph, "LoadImage").Select(l => Str(Inputs(l.Node)["image"])).First(n => n.StartsWith("nm_regen_"));
                using var region = ImgIo.Read(Path.Combine(root, "ComfyUI", "input", regionName), ImreadModes.Color);
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "output"));
                using var fill = new Mat(region.Rows, region.Cols, MatType.CV_8UC3, OutputColor?.Invoke(Graphs.Count - 1) ?? new Scalar(128, 128, 128));
                ImgIo.Write(Path.Combine(root, "ComfyUI", "output", output), fill);
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
                        ["outputs"] = new JsonObject { ["20"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["filename"] = output, ["subfolder"] = "", ["type"] = "output" }) } },
                    },
                }.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_MixedRegion_PerPieceOn_UploadsTheAreas_UsesThem_AndCleansUp()
    {
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        s.RegenTagsBySex = true; s.RegenTagsPerPiece = true; s.RegenSeed = 7; s.RegenEraseTight = false;
        s.RegenPrompt = "manga"; s.RegenBodyTagsMale = "penis"; s.RegenBodyTagsFemale = "pussy";
        var fake = new FakeComfy(s.ComfyRoot);
        var client = new ComfyClient("http://127.0.0.1:1", fake);
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        mask.Rectangle(new Rect(200, 320, 120, 30), Scalar.White, -1);
        // 위 조각은 남성, 아래 조각은 여성으로 골랐다 — 한 영역에 성별이 섞였다
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Male), new SexMark(new Point(260, 335), RegenKind.Female)]);
        try
        {
            Assert.NotNull(Assert.Single(plan).SexMap);
            var notes = new List<string>();
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None,
                status: new SyncProgress(notes.Add));

            Assert.Equal([7L], seeds);
            Assert.Contains(notes, n => n.Contains("자리별 태그(남성·여성)"));
            // 그림·지우기 마스크·자리 마스크 셋을 올렸고, 그래프가 그 자리 마스크를 읽는다
            Assert.Equal(3, fake.Uploads.Count);
            var area = Assert.Single(fake.Uploads, n => n.StartsWith("nm_regenarea_"));
            Assert.Contains(OfType(fake.Graph!, "LoadImage"), l => Str(Inputs(l.Node)["image"]) == area);
            Assert.Equal(["manga, penis", "manga, pussy"], OfType(fake.Graph!, "ConditioningSetMask").Select(m => Str(Inputs(Source(fake.Graph!, Inputs(m.Node)["conditioning"]))["text"])).Order());
            // 끝나면 ComfyUI 폴더에 아무것도 남기지 않는다(사용자의 폴더다) — 자리 마스크도
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "input")));
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "output")));
            // 결과(회색)가 조각 자리에 붙었고, 영역 밖은 그대로다
            Assert.Equal(new Vec3b(128, 128, 128), img.At<Vec3b>(215, 260));
            Assert.Equal(new Vec3b(255, 255, 255), img.At<Vec3b>(580, 780));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Theory]
    [InlineData(0.9f, true, true)]     // 자동 판단이 사정으로 본 조각
    [InlineData(0.1f, true, false)]
    [InlineData(0.9f, false, false)]   // 자동 판단을 꺼 두면 묻지 않는다
    public async Task ComfyRegenDecensorAsync_CumTags_GoInOnlyWhenAPieceIsJudgedCum(float score, bool auto, bool expected)
    {
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        s.RegenTagsBySex = true; s.RegenSeed = 7; s.RegenEraseTight = false; s.RegenCumAuto = auto;
        s.RegenPrompt = "manga"; s.RegenBodyTagsMale = "penis"; s.RegenBodyTagsFemale = "pussy"; s.RegenBodyTagsCum = "cum in pussy";
        var fake = new FakeComfy(s.ComfyRoot);
        var client = new ComfyClient("http://127.0.0.1:1", fake);
        using var runner = new PipelineRunner(s);
        runner.CumRuleOverride = (_, st) => new CumRule(_ => score, st.RegenCumThreshold);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            Assert.Equal(expected, Assert.Single(plan).Cum);
            var notes = new List<string>();
            await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None, status: new SyncProgress(notes.Add));
            var prompts = OfType(fake.Graph!, "CLIPTextEncode").Select(n => Str(Inputs(n.Node)["text"])).ToList();
            Assert.Contains(expected ? "manga, pussy, cum in pussy" : "manga, pussy", prompts);
            Assert.DoesNotContain(expected ? "manga, pussy" : "manga, pussy, cum in pussy", prompts);
            Assert.Equal(expected, notes.Any(n => n.Contains("사정")));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    // ── 흰 조각 자동 재시도 (2026-11-20, 사용자 "또 흰색으로 칠해진다" → "조치 진행해줘") ──
    // 1단계가 막대 자리를 평평한 흰 칠로 메우는 조각이 어느 CN 강도에서든 3분의 1쯤 나온다(같은 시드 A/B 두 번: 5·6·7/22, 5/14). 결과를 재서(RegenWhiteCheck) 흰 조각이 있으면
    // 첫 시드에서 끌어낸 시드로 다시 그리고(RetrySeed — 설정의 시드를 고정한 사람도 같은 결과), 덜 흰 것을 고른다. 시도마다 올린 입력은 그대로 쓰고 출력만 다시 받는다.

    private static (AppSettings s, FakeComfy fake, ComfyClient client) WhiteRetrySetup(TempDir t, int retries)
    {
        var s = EnginelessSettings(t);
        s.LinkedComfyRoot = t.At("comfy");
        s.RegenTagsBySex = true; s.RegenSeed = 7; s.RegenEraseTight = false; s.RegenWhiteRetries = retries;
        s.RegenPrompt = "manga"; s.RegenBodyTagsMale = "penis"; s.RegenBodyTagsFemale = "pussy";
        var fake = new FakeComfy(s.ComfyRoot);
        return (s, fake, new ComfyClient("http://127.0.0.1:1", fake));
    }

    private static readonly Scalar Gray = new(128, 128, 128);

    [Fact]
    public async Task ComfyRegenDecensorAsync_FlatWhitePiece_RetriesWithADerivedSeed_AndKeepsTheDrawnResult()
    {
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries: 2);
        fake.OutputColor = attempt => attempt == 0 ? Scalar.White : Gray;   // 첫 시도는 흰 칠, 두 번째는 그림
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            var notes = new List<string>();
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None, status: new SyncProgress(notes.Add));

            Assert.Equal(2, fake.Prompts);
            long second = RegenWhiteCheck.RetrySeed(7, 1);
            Assert.Equal([second], seeds);   // 사이드카에는 고른 결과를 만든 시드가 남는다 — 시드 고정으로 그대로 재현된다
            Assert.Contains(second, OfType(fake.Graphs[1], "KSampler").Select(n => Inputs(n.Node)["seed"]!.GetValue<long>()));
            Assert.Contains(notes, n => n.Contains("흰"));
            Assert.Equal(new Vec3b(128, 128, 128), img.At<Vec3b>(215, 260));
            // 두 시도의 출력과 올린 입력 전부를 치운다
            Assert.Equal(2, fake.Uploads.Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "input")));
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "output")));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_EveryAttemptWhite_StopsAtTheLimit_AndKeepsTheFirst()
    {
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries: 2);
        fake.OutputColor = _ => Scalar.White;
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Gray);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None);

            Assert.Equal(3, fake.Prompts);   // 첫 시도 + 재시도 2
            Assert.Equal([7L], seeds);       // 전부 똑같이 희면 첫 결과
            Assert.Equal(new Vec3b(255, 255, 255), img.At<Vec3b>(215, 260));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Theory]
    [InlineData(0, false, 1)]   // 재시도 0 = 끔 — 흰 결과를 그대로 받는다
    [InlineData(2, false, 1)]   // 시드를 고정한 장은 그 시드의 결과를 바꾸지 않는다(사용자가 붙잡은 결과)
    [InlineData(2, true, 2)]    // 하네스: 첫 시도는 적어 둔 시드로 재현하고 흰 조각만 다시
    public async Task ComfyRegenDecensorAsync_LockedSeedOrRetriesOff_RetriesOnlyWhenAsked(int retries, bool retryLocked, int prompts)
    {
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries);
        fake.OutputColor = attempt => attempt == 0 ? Scalar.White : Gray;
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Gray);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None, lockedSeeds: [11L], retryLocked: retryLocked);

            Assert.Equal(prompts, fake.Prompts);
            Assert.Equal([prompts == 2 ? RegenWhiteCheck.RetrySeed(11, 1) : 11L], seeds);
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_RetryAttemptFails_KeepsTheFirstResultInsteadOfFailingThePage()
    {
        // 다시 그리다 ComfyUI가 거절·시간 초과하면 첫 결과(전과 같은 그림)를 버리지 않는다 — 몇 분을 그려 놓고 장 전체를 실패로 두면 더 나쁘다(검수 2026-11-20)
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries: 2);
        fake.OutputColor = _ => Scalar.White;
        fake.FailPrompt = attempt => attempt == 1;
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Gray);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            var notes = new List<string>();
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None, status: new SyncProgress(notes.Add));

            Assert.Equal(2, fake.Prompts);   // 실패한 재시도 뒤에는 더 보내지 않는다
            Assert.Equal([7L], seeds);
            Assert.Equal(new Vec3b(255, 255, 255), img.At<Vec3b>(215, 260));
            Assert.Contains(notes, n => n.Contains("멈췄다"));
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "input")));
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "output")));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_TimedOutPrompt_IsWithdrawnFromTheServer()
    {
        // 기다리기만 그만두면(제한 시간·연결 오류) 프롬프트는 서버에서 끝까지 돌아, 우리가 출력을 치운 뒤에 사용자의 ComfyUI 폴더에 그림을 남긴다 — 거둬 달라고 한다.
        // 글자 자리 다시 그리기에서 먼저 고친 틈이다(검수 2026-11-26). 재생성에도 같은 틈이 있었다
        using var t = new TempDir();
        var (s, _, _) = WhiteRetrySetup(t, retries: 0);
        var fake = new FakeComfy(s.ComfyRoot) { NeverFinish = true };
        var client = new ComfyClient("http://127.0.0.1:1", fake) { PollMs = 1 };
        using var runner = new PipelineRunner(s) { RegenTimeout = TimeSpan.FromMilliseconds(80) };
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None));

            Assert.Equal(["p1"], fake.Cancelled);
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "input")));
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "output")));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_StoppedRightAfterAnUploadReachedTheServer_LeavesNoInputBehind()
    {
        // 둘째 그림(지울 자리 마스크)을 올리는 요청이 서버에 닿아 파일은 생겼는데 답을 받기 전에 멈췄다 — 답(파일 이름)이 없어도 우리가 지어 보낸 이름으로 치운다(검토 2026-11-27)
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries: 0);
        using var cts = new CancellationTokenSource();
        fake.ThrowAfterUpload = name => name.StartsWith("nm_regenmask_", StringComparison.Ordinal) ? Stop() : null;
        Exception Stop() { cts.Cancel(); return new OperationCanceledException(cts.Token); }
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, cts.Token));

            Assert.Equal(2, fake.Uploads.Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(s.ComfyRoot, "ComfyUI", "input")));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public async Task ComfyRegenDecensorAsync_DefaultTightErase_WhitePieceStillRetries()
    {
        // 기본값(막대만 지우기)에서는 막대 픽셀만 잰다 — 검은 픽셀이 없는 조각은 TightenErase가 통째로 남기므로 흰 결과가 그대로 흰 조각으로 잡힌다
        using var t = new TempDir();
        var (s, fake, client) = WhiteRetrySetup(t, retries: 2);
        s.RegenEraseTight = true;
        fake.OutputColor = attempt => attempt == 0 ? Scalar.White : Gray;
        using var runner = new PipelineRunner(s);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));
        var plan = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(260, 215), RegenKind.Female)]);
        try
        {
            var seeds = await runner.ComfyRegenDecensorAsync(client, Path.Combine(s.ComfyRoot, "ComfyUI", "output"), img, plan, 3, CancellationToken.None);

            Assert.Equal(2, fake.Prompts);
            Assert.Equal([RegenWhiteCheck.RetrySeed(7, 1)], seeds);
            Assert.Equal(new Vec3b(128, 128, 128), img.At<Vec3b>(215, 260));
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public void CumRuleFor_NoModel_OrEmptyTagBox_IsNull()
    {
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        using var runner = new PipelineRunner(s);
        using var img = new Mat(60, 80, MatType.CV_8UC3, Scalar.White);
        Assert.Null(runner.CumRuleFor(img));   // 모델이 없다 — 표시한 조각만 사정으로 친다
        runner.CumRuleOverride = (_, _) => new CumRule(_ => 1f, 0.35);
        Assert.NotNull(runner.CumRuleFor(img));
        var empty = s.ShallowClone(); empty.RegenBodyTagsCum = " ";
        Assert.Null(runner.CumRuleFor(img, empty));   // 넣을 태그가 없으면 잴 것도 없다
    }

    [Fact]
    public void RegenPlanFor_CumSource_IsTheImageJudged_AndBoxesAreMovedToItsSize()
    {
        // 채색된 장의 다시 렌더링: 그리는 그림은 이미 한 번 다시 그려졌다 — 판단은 넘겨받은 검열 제거 전 원고에서, 상자는 그 크기로 옮겨서
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        using var runner = new PipelineRunner(s);
        Mat? judged = null; var boxes = new List<Rect>();
        runner.CumRuleOverride = (img, st) => { judged = img; return new CumRule(b => { boxes.Add(b); return 0.9f; }, st.RegenCumThreshold); };
        using var drawn = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        using var pre = new Mat(1200, 1600, MatType.CV_8UC3, Scalar.White);   // 두 배 크기
        using var mask = RectMask(800, 600, new Rect(200, 200, 120, 30));

        var plain = runner.RegenPlanFor(drawn, mask, null);
        Assert.Same(drawn, judged); Assert.Equal([new Rect(200, 200, 120, 30)], boxes);
        boxes.Clear();
        var fromPre = runner.RegenPlanFor(drawn, mask, null, cumSource: pre);
        try
        {
            Assert.Same(pre, judged);
            Assert.Equal([new Rect(400, 400, 240, 60)], boxes);
            Assert.True(Assert.Single(fromPre).Cum);
        }
        finally { foreach (var r in plain.Concat(fromPre)) r.Dispose(); }
    }

    [Fact]
    public void CumRule_AfterOneFailure_GivesMinusOne_AndTheRunnerStopsJudging()
    {
        // 깨진 모델 파일이면 조각마다 다시 읽다 실패한다 — 한 번 실패하면 이 러너는 자동 판단을 끈다. 점수 -1은 임계 밑이라 '아님'이고, 결과 창은 적어 두지 않는다
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        using var runner = new PipelineRunner(s);
        int calls = 0;
        runner.CumRuleOverride = (_, st) => new CumRule(_ => { calls++; throw new InvalidDataException("broken"); }, st.RegenCumThreshold);
        using var img = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        var rule = runner.CumRuleFor(img)!;
        Assert.True(runner.CumAutoReady());
        Assert.Equal(-1f, rule.Score(new Rect(10, 10, 50, 50)));
        Assert.Equal(-1f, rule.Score(new Rect(10, 10, 50, 50)));
        Assert.Equal(1, calls);                     // 두 번째는 묻지도 않는다
        Assert.False(runner.CumAutoReady());
        Assert.Null(runner.CumRuleFor(img));
    }

    /// <summary>부른 스레드에서 바로 적는 IProgress — Progress&lt;T&gt;는 동기화 컨텍스트로 미뤄 시험이 끝난 뒤에 올 수 있다.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
