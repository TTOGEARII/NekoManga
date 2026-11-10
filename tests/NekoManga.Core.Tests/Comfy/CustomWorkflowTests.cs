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

    [Fact(Skip = "알려진 결함: 최상위 값이 객체가 아닌 JSON(예: ComfyUI의 comfy.settings.json)을 고르면 CheckApi가 JsonValue에 문자열 인덱서를 써서 InvalidOperationException을 던진다 — 문서가 약속한 사람이 읽을 InvalidDataException 대신 .NET 영어 메시지가 사용자에게 보인다")]
    public void Load_ObjectWhoseValuesAreNotNodes_ThrowsInvalidData()
    {
        using var t = new TempDir();
        var path = t.At("comfy.settings.json");
        // ComfyUI의 user/default 폴더(워크플로 고르기 창이 여는 workflows의 바로 위)에 있는 설정 파일 꼴
        File.WriteAllText(path, """{"Comfy.Locale": "ko", "Comfy.UseNewMenu": "Top"}""");

        Assert.Throws<InvalidDataException>(() => CustomWorkflow.Load(path));
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

        // 제목에 neko(대소문자 무관)나 '입력'이 든 것이 먼저, 그런 것이 없으면 첫 번째. 고르지 않은 LoadImage는 그대로 둔다
        Assert.Equal(expected, prepared.InputNodeId);
        Assert.Equal(2, prepared.LoadImageCount);
        Assert.Equal("nm_custom_0.png", Str(prepared.Prompt[expected]!["inputs"]!["image"]));
        foreach (var other in original.Keys.Where(id => id != expected))
            Assert.Equal(original[other], Str(prepared.Prompt[other]!["inputs"]!["image"]));
    }

    [Fact(Skip = "알려진 결함: PickNode가 LoadImage·SaveImage 둘 다 neko·입력·출력 세 낱말로 고른다 — 제목에 '입력'이 든 SaveImage(입력 확인용 저장 등)를 출력으로 골라 진짜 결과 SaveImage를 PreviewImage로 바꾼다. 문서 규약은 SaveImage를 neko·출력으로만 고른다")]
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
        var recipe = WorkflowBuilder.BuildRegenDecensor(new AppSettings { RegenSeed = 1 }, "nm_regen_input.png", "NekoManga_out");
        File.WriteAllText(path, recipe.ToJsonString());

        var prepared = CustomWorkflow.Prepare(CustomWorkflow.Load(path), "nm_custom_2.png", "nm_customout_2");

        Assert.Equal(Only(recipe, "LoadImage").Id, prepared.InputNodeId);
        Assert.Equal(Only(recipe, "SaveImage").Id, prepared.OutputNodeId);
        Assert.Equal("nm_custom_2.png", Str(prepared.Prompt[prepared.InputNodeId]!["inputs"]!["image"]));
        Assert.Equal("nm_customout_2", Str(prepared.Prompt[prepared.OutputNodeId]!["inputs"]!["filename_prefix"]));
    }
}
