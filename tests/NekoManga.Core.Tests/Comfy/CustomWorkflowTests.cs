using System.Text;
using System.Text.Json.Nodes;
using NekoManga.Core.Comfy;
using static NekoManga.Core.Tests.ComfyTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>CustomWorkflow — 사용자가 ComfyUI에서 짠 워크플로 파일의 규약(LoadImage 하나·SaveImage 하나) 점검,
/// 실행용 확정(Resolve), 입력 그림·출력 접두사 끼우기(Prepare), 내장 워크플로 복사.</summary>
public class CustomWorkflowTests
{
    private const string MinimalApi = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "example.png" }, "_meta": { "title": "Load Image" } },
          "2": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "ComfyUI" }, "_meta": { "title": "Save Image" } }
        }
        """;

    // ───────── Load ─────────

    [Theory]
    [InlineData("커스텀 워크플로.json", false)]
    [InlineData("検閲 除去 ワークフロー.json", false)]
    [InlineData("my workflow (1).JSON", true)]
    public void Load_ApiWorkflowAtUnusualPath_ReturnsTheGraph(string fileName, bool withBom)
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.At("작업 폴더"));
        var path = t.At("작업 폴더", fileName);
        // 메모장 등으로 고친 파일은 UTF-8 BOM이 붙어 있을 수 있다
        File.WriteAllText(path, MinimalApi, new UTF8Encoding(withBom));

        var workflow = CustomWorkflow.Load(path);

        Assert.False(WorkflowConverter.IsUiFormat(workflow));
        Assert.Equal("LoadImage", ClassType(Assert.IsType<JsonObject>(workflow["1"])));
        Assert.Equal("SaveImage", ClassType(Assert.IsType<JsonObject>(workflow["2"])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("없는 워크플로.json")]
    public void Load_MissingOrBlankPath_ThrowsFileNotFound(string name)
    {
        using var t = new TempDir();
        var path = string.IsNullOrWhiteSpace(name) ? name : t.At(name);

        Assert.Throws<FileNotFoundException>(() => CustomWorkflow.Load(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"1": {"inputs": {}}}""")]
    public void Load_NotAWorkflowObject_ThrowsInvalidData(string content)
    {
        using var t = new TempDir();
        var path = t.At("broken.json");
        File.WriteAllText(path, content);

        // 앱은 이 예외의 메시지를 그대로 보여 주므로, 깨진 JSON·배열·class_type 없는 노드 모두 사람이 읽을 InvalidDataException이어야 한다
        Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
    }

    [Theory]
    [InlineData("""{}""", "LoadImage")]
    [InlineData("""{"1": {"class_type": "SaveImage", "inputs": {}}}""", "LoadImage")]
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {}}}""", "SaveImage")]
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage", "mode": 2}, {"id": 2, "type": "SaveImage", "mode": 0}], "links": []}""", "LoadImage")]
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage"}, {"id": 2, "type": "SaveImage", "mode": 4}], "links": []}""", "SaveImage")]
    public void Load_WithoutActiveLoadOrSaveImage_SaysWhichNodeIsMissing(string content, string missing)
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, content);

        // 앱이 원고를 넣을 LoadImage와 결과를 받을 SaveImage가 켜진 채로 있어야 한다 — 꺼짐(2)·우회(4)는 없는 것과 같다
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
        Assert.StartsWith(missing + " 노드가 없어요", ex.Message);
    }

    [Fact]
    public void Load_ObjectWhoseValuesAreNotNodes_ThrowsInvalidData()
    {
        using var t = new TempDir();
        var path = t.At("comfy.settings.json");
        // ComfyUI의 user/default 폴더(워크플로 고르기 창이 여는 workflows의 바로 위)에 있는 설정 파일 꼴
        File.WriteAllText(path, """{"Comfy.Locale": "ko", "Comfy.UseNewMenu": "Top"}""");

        Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
    }

    [Theory]
    [InlineData("""{"Comfy.Locale": "ko", "Comfy.UseNewMenu": "Top"}""", "Comfy.Locale")]
    [InlineData("""{"Comfy.Workflow.SortNodeIdOnSave": false}""", "Comfy.Workflow.SortNodeIdOnSave")]
    [InlineData("""{"Comfy.Keybinding.NewBindings": []}""", "Comfy.Keybinding.NewBindings")]
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {}}, "2": {"class_type": "SaveImage", "inputs": {}}, "version": 0.4}""", "version")]
    [InlineData("""{"1": null}""", "1")]
    public void Load_ValueThatIsNotANode_SaysWhichEntryIsWrong(string content, string key)
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, content);

        // 노드 자리의 값이 객체가 아니면(글자·참거짓·배열·숫자·null) 워크플로가 아니다 — 앞의 노드가 멀쩡해도 마찬가지.
        // 설정 창은 메시지를 그대로 띄우므로 .NET의 영어 메시지 대신 어느 항목이 노드가 아닌지 알려야 한다
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
        Assert.StartsWith("워크플로 파일이 아니에요", ex.Message);
        Assert.Contains($"'{key}'", ex.Message);
    }

    [Theory]
    [InlineData("""{"1": {"class_type": 7, "inputs": {}}}""")]
    [InlineData("""{"1": {"class_type": true, "inputs": {}}}""")]
    [InlineData("""{"1": {"class_type": ["LoadImage"], "inputs": {}}}""")]
    [InlineData("""{"1": {"class_type": {"name": "LoadImage"}, "inputs": {}}}""")]
    public void Load_ClassTypeThatIsNotText_ThrowsInvalidData(string content)
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, content);

        // class_type이 노드 이름(글자)이 아니면 없는 것과 같다 — 같은 안내 메시지
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
        Assert.StartsWith("API 포맷이 아니에요", ex.Message);
    }

    [Fact]
    public void Load_ApiWorkflow_ReturnsTheFileGraphUnchanged()
    {
        // 규약 점검은 읽기만 한다 — 검열 제거·채색의 실행 그래프는 Load가 돌려준 그래프에서 만들어지므로 파일 내용과 한 글자도 달라지면 안 된다
        using var t = new TempDir();
        var path = t.At("재생성 레시피 (API).json");
        var recipe = WorkflowBuilder.BuildRegenCrop(new AppSettings { RegenSeed = 1 }, "nm_regen_input.png", "nm_regen_erase.png", "NekoManga_out", "penis, glans", 1024, 1024);
        File.WriteAllText(path, recipe.ToJsonString());

        var loaded = CustomWorkflow.Load(path);

        Assert.Equal(JsonNode.Parse(File.ReadAllText(path))!.ToJsonString(), loaded.ToJsonString());
    }

    [Theory]
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {}}, "1": {"class_type": "SaveImage", "inputs": {}}}""")]                               // 같은 노드 번호가 두 번
    [InlineData("""{"1": {"class_type": "LoadImage", "class_type": "LoadImage", "inputs": {}}, "2": {"class_type": "SaveImage", "inputs": {}}}""")]  // 노드 안에 같은 키
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage", "mode": "0"}, {"id": 2, "type": "SaveImage"}], "links": []}""")]                        // 화면 포맷: mode가 글자
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage", "mode": 0.5}, {"id": 2, "type": "SaveImage"}], "links": []}""")]                        // 화면 포맷: mode가 정수가 아니다
    [InlineData("""{"nodes": [{"id": 1, "type": 7}, {"id": 2, "type": "SaveImage"}], "links": []}""")]                                               // 화면 포맷: type이 숫자
    public void Load_HandEditedJsonOfWrongShape_ThrowsReadableInvalidData(string content)
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, content);

        // 손으로 고친 파일에서만 생기는 꼴이다 — 설정 창은 메시지를 그대로 띄우므로 .NET의 영어 예외 대신 사람이 읽을 InvalidDataException이어야 한다
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
        Assert.StartsWith("워크플로 파일을 읽지 못했어요", ex.Message);
        // 어디가 틀렸는지는 원래 예외만 안다 — 화면은 메시지만 보여 주므로 그 말을 끝에 붙여 둔다
        Assert.EndsWith($"(자세히: {ex.InnerException!.Message})", ex.Message);
    }

    // ───────── Resolve ─────────

    [Fact]
    public void Resolve_BundledScreenWorkflow_BecomesApiGraphReadyToPrepare()
    {
        // Load는 화면 저장 포맷을 바꾸지 않고 돌려준다(노드 정보가 있어야 풀 수 있다) — 엔진이 켜진 뒤 Resolve가 바꾼다
        var loaded = CustomWorkflow.Load(BundledRegenWorkflowPath);
        Assert.True(WorkflowConverter.IsUiFormat(loaded));

        var api = CustomWorkflow.Resolve(loaded, BundledRegenObjectInfo());
        var prepared = CustomWorkflow.Prepare(api, "nm_custom_1.png", "nm_customout_1");

        Assert.False(WorkflowConverter.IsUiFormat(api));
        Assert.True(WorkflowConverter.IsUiFormat(loaded));   // 읽어 둔 원본은 그대로
        // 입력은 '원본 이미지 + 수동 마스크 로드'(3), 출력은 SaveImage(20) — 각각 하나뿐이다
        Assert.Equal(("3", "20", 1, 1), (prepared.InputNodeId, prepared.OutputNodeId, prepared.LoadImageCount, prepared.SaveImageCount));
        Assert.Equal("nm_custom_1.png", Str(prepared.Prompt["3"]!["inputs"]!["image"]));
        Assert.Equal("nm_customout_1", Str(prepared.Prompt["20"]!["inputs"]!["filename_prefix"]));
    }

    [Fact]
    public void Resolve_ApiWorkflow_ReturnsCheckedCopyAndLeavesTheInputAlone()
    {
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "nm_regen_input.png" } },
              "2": { "class_type": "ControlNetLoader", "inputs": { "control_net_name": "diffusion_pytorch_model_promax.safetensors" } },
              "3": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "x" } }
            }
            """);

        var resolved = CustomWorkflow.Resolve(api, BundledRegenObjectInfo());

        // 점검은 사본에서 한다: 이름만 다른 promax ControlNet은 사본에서만 설치된 이름으로 바뀐다
        Assert.NotSame(api, resolved);
        Assert.Equal("controlnet-union-sdxl-promax.safetensors", Str(resolved["2"]!["inputs"]!["control_net_name"]));
        Assert.Equal("diffusion_pytorch_model_promax.safetensors", Str(api["2"]!["inputs"]!["control_net_name"]));
    }

    [Fact]
    public void Resolve_ValidApiWorkflow_ReturnsTheSameGraph()
    {
        // 고칠 것이 없는 올바른 그래프는 값 하나 바뀌지 않고 돌아온다 — 검열 제거·채색의 실행 그래프가 여기서 나온다
        var api = Json(MinimalApi);

        var resolved = CustomWorkflow.Resolve(api, BundledRegenObjectInfo());

        Assert.Equal(api.ToJsonString(), resolved.ToJsonString());
    }

    [Theory]
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {"image": "a.png"}, "_meta": {"title": 5}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}""")]          // 제목이 숫자
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {"image": "a.png"}, "_meta": "Load Image"}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}""")]        // _meta가 객체가 아니다
    [InlineData("""{"1": {"class_type": "LoadImage", "inputs": {"image": "a.png", "image": "b.png"}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}""")]       // 입력에 같은 키
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage"}, {"id": 1, "type": "SaveImage"}], "links": []}""")]                                                                                         // 화면 포맷: 같은 노드 번호
    [InlineData("""{"nodes": [{"id": "1", "type": "LoadImage"}, {"id": 2, "type": "SaveImage"}], "links": []}""")]                                                                                       // 화면 포맷: id가 글자
    [InlineData("""{"nodes": [{"id": 1, "type": "LoadImage"}, {"id": 2, "type": "SaveImage", "inputs": [{"name": "images", "link": "5"}]}], "links": [[5, 1, 0, 2, 0, "IMAGE"]]}""")]                   // 화면 포맷: 링크 번호가 글자
    public void Resolve_HandEditedJsonOfWrongShape_ThrowsReadableInvalidData(string content)
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, content);
        var loaded = CustomWorkflow.Load(path);   // 규약(LoadImage·SaveImage) 점검이 보지 않는 자리라 읽기는 통과한다

        // 실행 직전의 변환·서버 점검에서 나던 영어 예외도 사람이 읽을 InvalidDataException으로 — 작업 시작 오류에 그대로 보인다
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Resolve(loaded, BundledRegenObjectInfo()));
        Assert.StartsWith("워크플로를 이 ComfyUI에 맞게 바꾸지 못했어요", ex.Message);
        Assert.EndsWith($"(자세히: {ex.InnerException!.Message})", ex.Message);
    }

    [Fact]
    public void Resolve_ScreenWorkflowNodeWithoutId_ThrowsReadableInvalidData()
    {
        using var t = new TempDir();
        var path = t.At("workflow.json");
        File.WriteAllText(path, """{"nodes": [{"type": "LoadImage"}, {"id": 2, "type": "SaveImage"}], "links": []}""");
        var loaded = CustomWorkflow.Load(path);

        // 손으로 고치다 노드의 id를 지운 파일 — 변환기 안의 NullReferenceException이 그대로 나가지 않고 무엇이 빠졌는지 알린다
        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Resolve(loaded, BundledRegenObjectInfo()));
        Assert.StartsWith("워크플로 파일을 읽지 못했어요", ex.Message);
        Assert.Contains("id가 없는 노드", ex.Message);
    }

    [Fact]
    public void Resolve_ScreenWorkflowWithCoreIntNode_BecomesApiGraphReadyToPrepare()
    {
        // ComfyUI가 저장한 그대로의 파일이다(코어 'Int' 노드, 저장 꼴 [1024, "fixed"]) — '손으로 고친 파일'로 거절되면 안 된다
        using var t = new TempDir();
        var path = t.At("크기 맞추기.json");
        File.WriteAllText(path, CoreIntNodeWorkflow);

        var api = CustomWorkflow.Resolve(CustomWorkflow.Load(path), CoreIntNodeObjectInfo());
        var prepared = CustomWorkflow.Prepare(api, "nm_custom_1.png", "nm_customout_1");

        Assert.Equal(("1", "2", 1, 1), (prepared.InputNodeId, prepared.OutputNodeId, prepared.LoadImageCount, prepared.SaveImageCount));
        Assert.Equal(1024, Num(prepared.Prompt["3"]!["inputs"]!["value"]));
        Assert.Equal(("3", 0), LinkOf(prepared.Prompt["4"]!["inputs"]!["width"]));
    }

    [Fact]
    public void Resolve_NodeInfoFromServerOfUnexpectedShape_DoesNotBlameTheFileAndShowsTheCause()
    {
        // 파일은 ComfyUI가 저장한 그대로이고, 서버가 알려 준 노드 정보(/object_info)에 앱이 모르는 꼴이 있다 — 여기서는 글자여야 할 표시 이름이 객체다
        var info = CoreIntNodeObjectInfo();
        info["PrimitiveInt"]!["display_name"] = new JsonObject { ["ko"] = "정수" };
        var loaded = Json(CoreIntNodeWorkflow);
        var raw = Assert.Throws<InvalidOperationException>(() => WorkflowConverter.ToApi(loaded, info));

        var ex = Assert.Throws<InvalidDataException>(() => CustomWorkflow.Resolve(loaded, info));

        // 사람이 읽을 InvalidDataException이되 '손으로 고친 파일' 탓으로 단정하지 않고, 원래 예외의 말을 붙여 원인이 보이게 한다
        Assert.StartsWith("워크플로를 이 ComfyUI에 맞게 바꾸지 못했어요", ex.Message);
        Assert.DoesNotContain("워크플로 파일을 읽지 못했어요", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.EndsWith($"(자세히: {raw.Message})", ex.Message);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), true)]            // 한 객체에 같은 키가 두 번
    [InlineData(typeof(InvalidOperationException), true)]    // 값의 형식이 다르다
    [InlineData(typeof(FormatException), true)]              // 정수 자리의 소수
    [InlineData(typeof(NullReferenceException), false)]
    [InlineData(typeof(InvalidDataException), false)]        // 이미 사람이 읽을 안내다
    public void IsMalformed_OnlyJsonShapeErrorsAreRewritten(Type exception, bool expected)
    {
        // NullReferenceException은 파일 꼴이 아니라 코드 결함의 신호다 — '파일을 읽지 못했어요'로 바꿔 보이면 원인을 못 찾는다
        Assert.Equal(expected, CustomWorkflow.IsMalformed((Exception)Activator.CreateInstance(exception)!));
    }

    // ───────── Prepare ─────────

    [Fact]
    public void Prepare_SeveralSaveImages_TitledOneIsOutputAndOthersBecomePreviews()
    {
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "원고.png" }, "_meta": { "title": "Load Image" } },
              "5": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "check" }, "_meta": { "title": "Save Image" } },
              "9": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "ComfyUI" }, "_meta": { "title": "Neko 결과" } }
            }
            """);

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_4.png", "nm_customout_4");

        // 제목에 neko(대소문자 무관)가 든 SaveImage가 앱이 받아 올 출력이다
        Assert.Equal(("1", "9", 1, 2), (prepared.InputNodeId, prepared.OutputNodeId, prepared.LoadImageCount, prepared.SaveImageCount));
        var p = prepared.Prompt;
        Assert.Equal("nm_custom_4.png", Str(p["1"]!["inputs"]!["image"]));
        Assert.Equal("nm_customout_4", Str(p["9"]!["inputs"]!["filename_prefix"]));
        // 나머지 SaveImage는 결과 파일이 섞이지 않게 PreviewImage로 바꾸고 접두사 입력을 뗀다 (그림 입력은 그대로)
        Assert.Equal("PreviewImage", Str(p["5"]!["class_type"]));
        Assert.False(p["5"]!["inputs"]!.AsObject().ContainsKey("filename_prefix"));
        Assert.Equal(("1", 0), LinkOf(p["5"]!["inputs"]!["images"]));
        // 원본 그래프는 건드리지 않는다 — 같은 워크플로를 페이지마다 다시 쓴다
        Assert.Equal("원고.png", Str(api["1"]!["inputs"]!["image"]));
        Assert.Equal("SaveImage", Str(api["5"]!["class_type"]));
        Assert.Equal("ComfyUI", Str(api["9"]!["inputs"]!["filename_prefix"]));
    }

    [Theory]
    [InlineData("Load Image", "NEKO input", "2")]
    [InlineData("참고 그림", "원고 입력", "2")]
    [InlineData("neko 입력", "neko 입력 2", "1")]
    [InlineData("Load Image", "Load Image", "1")]
    [InlineData("출력 비교", "원고 입력", "2")]
    public void Prepare_SeveralLoadImages_TitledInputWinsElseTheFirst(string firstTitle, string secondTitle, string expected)
    {
        var original = new Dictionary<string, string> { ["1"] = "a.png", ["2"] = "b.png" };
        var api = new JsonObject
        {
            ["1"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = original["1"] }, ["_meta"] = new JsonObject { ["title"] = firstTitle } },
            ["2"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = original["2"] }, ["_meta"] = new JsonObject { ["title"] = secondTitle } },
            ["3"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["filename_prefix"] = "x" } },
        };

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_0.png", "out");

        // 제목에 neko(대소문자 무관)나 '입력'이 든 것이 먼저, 그런 것이 없으면 첫 번째('출력'은 보지 않는다). 고르지 않은 LoadImage는 그대로 둔다
        Assert.Equal(expected, prepared.InputNodeId);
        Assert.Equal(2, prepared.LoadImageCount);
        Assert.Equal("nm_custom_0.png", Str(prepared.Prompt[expected]!["inputs"]!["image"]));
        foreach (var other in original.Keys.Where(id => id != expected))
            Assert.Equal(original[other], Str(prepared.Prompt[other]!["inputs"]!["image"]));
    }

    [Fact]
    public void Prepare_SaveImageTitledWithInputWord_IsNotPickedAsOutput()
    {
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "page.png" } },
              "5": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "final" }, "_meta": { "title": "Save Image" } },
              "9": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "check" }, "_meta": { "title": "입력 마스크 확인" } }
            }
            """);

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_0.png", "nm_customout_0");

        // 규약: SaveImage가 여럿이면 제목에 neko나 '출력'이 든 것, 없으면 첫 번째 — '입력'은 LoadImage를 고르는 낱말이다
        Assert.Equal("5", prepared.OutputNodeId);
        // 진짜 결과 SaveImage가 접두사를 받고, 입력 확인용 저장만 PreviewImage로 바뀐다
        Assert.Equal("SaveImage", Str(prepared.Prompt["5"]!["class_type"]));
        Assert.Equal("nm_customout_0", Str(prepared.Prompt["5"]!["inputs"]!["filename_prefix"]));
        Assert.Equal("PreviewImage", Str(prepared.Prompt["9"]!["class_type"]));
    }

    [Fact]
    public void Prepare_LoadImageTitledWithOutputWord_IsNotPickedAsInput()
    {
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "page.png" }, "_meta": { "title": "Load Image" } },
              "4": { "class_type": "LoadImage", "inputs": { "image": "before.png" }, "_meta": { "title": "출력 비교용 이전 결과" } },
              "9": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "ComfyUI" }, "_meta": { "title": "Save Image" } }
            }
            """);

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_0.png", "nm_customout_0");

        // 규약: LoadImage가 여럿이면 제목에 neko나 '입력'이 든 것, 없으면 첫 번째 — '출력'은 SaveImage를 고르는 낱말이다
        Assert.Equal("1", prepared.InputNodeId);
        Assert.Equal("nm_custom_0.png", Str(prepared.Prompt["1"]!["inputs"]!["image"]));
        // 제목에 '출력'이 든 LoadImage(비교용으로 불러온 그림 등)는 원래 그림을 그대로 쓴다
        Assert.Equal("before.png", Str(prepared.Prompt["4"]!["inputs"]!["image"]));
    }

    [Theory]
    [InlineData("Save Image", "NEKO 결과", "3")]
    [InlineData("미리 보기", "결과 출력", "3")]
    [InlineData("neko 출력", "neko 출력 2", "2")]
    [InlineData("Save Image", "Save Image", "2")]
    [InlineData("Save Image", "입력 마스크 확인", "2")]
    [InlineData("입력 확인", "최종 출력", "3")]
    public void Prepare_SeveralSaveImages_TitledOutputWinsElseTheFirst(string firstTitle, string secondTitle, string expected)
    {
        var api = new JsonObject
        {
            ["1"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = "page.png" } },
            ["2"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["filename_prefix"] = "a" }, ["_meta"] = new JsonObject { ["title"] = firstTitle } },
            ["3"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["filename_prefix"] = "b" }, ["_meta"] = new JsonObject { ["title"] = secondTitle } },
        };

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_0.png", "out");

        // 제목에 neko(대소문자 무관)나 '출력'이 든 것이 먼저, 그런 것이 없으면 첫 번째 — '입력'은 보지 않는다.
        // 고른 것만 접두사를 받고 나머지는 PreviewImage가 된다
        Assert.Equal(expected, prepared.OutputNodeId);
        Assert.Equal(2, prepared.SaveImageCount);
        Assert.Equal("out", Str(prepared.Prompt[expected]!["inputs"]!["filename_prefix"]));
        Assert.Equal("PreviewImage", Str(prepared.Prompt[expected == "2" ? "3" : "2"]!["class_type"]));
    }

    [Fact]
    public void Prepare_SeveralNodesWithoutTitles_FirstOfEachKindIsPicked()
    {
        // 손으로 쓴 API 파일처럼 _meta가 없으면 제목으로 고를 수 없다 — 종류마다 첫 번째
        var api = Json("""
            {
              "1": { "class_type": "LoadImage", "inputs": { "image": "a.png" } },
              "2": { "class_type": "SaveImage", "inputs": { "images": ["1", 0], "filename_prefix": "a" } },
              "3": { "class_type": "LoadImage", "inputs": { "image": "b.png" } },
              "4": { "class_type": "SaveImage", "inputs": { "images": ["3", 0], "filename_prefix": "b" } }
            }
            """);

        var prepared = CustomWorkflow.Prepare(api, "nm_custom_0.png", "nm_customout_0");

        Assert.Equal(("1", "2", 2, 2), (prepared.InputNodeId, prepared.OutputNodeId, prepared.LoadImageCount, prepared.SaveImageCount));
        Assert.Equal("b.png", Str(prepared.Prompt["3"]!["inputs"]!["image"]));
        Assert.Equal("PreviewImage", Str(prepared.Prompt["4"]!["class_type"]));
    }

    // ───────── 내장 워크플로·내보낸 레시피 ─────────

    [Fact]
    public void InstallBundledWorkflow_CopiesIntoComfyWorkflowBrowserFolder()
    {
        using var t = new TempDir();
        var s = new AppSettings { LinkedComfyRoot = t.At("ComfyUI 포터블 (테스트)") };

        var dst = CustomWorkflow.InstallBundledWorkflow(s, BundledRegenWorkflowPath);

        // ComfyUI 화면의 워크플로 탐색기가 보는 곳(user/default/workflows)에 같은 이름으로 — 없던 폴더는 만든다
        Assert.Equal(Path.Combine(s.ComfyRoot, "ComfyUI", "user", "default", "workflows", "NekoManga_decensor_regen.json"), dst);
        Assert.Equal(File.ReadAllBytes(BundledRegenWorkflowPath), File.ReadAllBytes(dst));
        // 버튼을 다시 눌러도(이미 복사된 파일이 있어도) 실패하지 않는다
        Assert.Equal(dst, CustomWorkflow.InstallBundledWorkflow(s, BundledRegenWorkflowPath));
    }

    [Fact]
    public void RegenRecipeExport_LoadedAsCustomWorkflow_UsesItsOnlyInputAndOutput()
    {
        // 설정 창의 'API로 내보내기'는 재생성 레시피를 커스텀 워크플로로 쓰라고 저장한다 — 그대로 규약을 통과해야 한다
        using var t = new TempDir();
        var path = t.At("재생성 레시피 (API).json");
        // 재생성 v3 레시피는 LoadImage가 둘(그림+영역 알파 / 그림+조각 알파) — 제목에 "neko 입력"이 든 그림 노드가 앱 입력으로 뽑혀야 한다
        var recipe = WorkflowBuilder.BuildRegenCrop(new AppSettings { RegenSeed = 1 }, "nm_regen_input.png", "nm_regen_erase.png", "NekoManga_out", "penis, glans", 1024, 1024);
        File.WriteAllText(path, recipe.ToJsonString());

        var prepared = CustomWorkflow.Prepare(CustomWorkflow.Load(path), "nm_custom_2.png", "nm_customout_2");

        Assert.Equal("1", prepared.InputNodeId);
        Assert.Equal(Only(recipe, "SaveImage").Id, prepared.OutputNodeId);
        Assert.Equal("nm_custom_2.png", Str(prepared.Prompt[prepared.InputNodeId]!["inputs"]!["image"]));
        Assert.Equal("nm_customout_2", Str(prepared.Prompt[prepared.OutputNodeId]!["inputs"]!["filename_prefix"]));
    }
}
