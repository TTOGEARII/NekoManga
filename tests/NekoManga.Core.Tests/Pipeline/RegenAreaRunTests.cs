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
    /// <summary>ComfyUI의 세 요청만 흉내 낸다: /upload/image(input 폴더에 쓴다), /prompt(그래프를 적고 SaveImage의 출력을 만든다), /history/{id}(완료).</summary>
    private sealed class FakeComfy(string root) : HttpMessageHandler
    {
        public List<string> Uploads { get; } = [];
        public JsonObject? Graph { get; private set; }
        private string _output = "";

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
                json = new JsonObject { ["name"] = name }.ToJsonString();
            }
            else if (path == "/prompt")
            {
                Graph = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["prompt"]!.AsObject();
                var (_, save) = Only(Graph, "SaveImage");
                var (_, empty) = Only(Graph, "EmptyImage");
                _output = Str(Inputs(save)["filename_prefix"]) + "_00001_.png";
                Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "output"));
                using var gray = new Mat((int)Num(Inputs(empty)["height"]), (int)Num(Inputs(empty)["width"]), MatType.CV_8UC3, new Scalar(128, 128, 128));
                ImgIo.Write(Path.Combine(root, "ComfyUI", "output", _output), gray);
                json = new JsonObject { ["prompt_id"] = "p1" }.ToJsonString();
            }
            else if (path == "/history/p1")
            {
                json = new JsonObject
                {
                    ["p1"] = new JsonObject
                    {
                        ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                        ["outputs"] = new JsonObject { ["20"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["filename"] = _output, ["subfolder"] = "", ["type"] = "output" }) } },
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
