using NekoManga.Core.Comfy;
using NekoManga.Core.Pipeline;

namespace NekoManga.Core.Tests;

/// <summary>ComfyUI가 워크플로를 받지 않았을 때의 안내 — 예전에는 답의 JSON을 그대로 보여 줘, 무엇을 해야 하는지 알 수 없었다.</summary>
public class ComfyRejectionTests
{
    [Fact]
    public void MissingNode_NamesTheNodeAndSaysWhatToDo()
    {
        // 2026-11-18 새 PC 설치 시험에서 실제로 온 답(ComfyUI 포터블 cu130)
        const string body = """
            {"error": {"type": "missing_node_type", "message": "Node 'LayerMask: MaskGrow' not found. The custom node may not be installed.",
             "details": "Node ID '#5'", "extra_info": {"node_id": "5", "class_type": "LayerMask: MaskGrow", "node_title": "LayerMask: MaskGrow"}}, "node_errors": {}}
            """;
        var text = ComfyClient.ExplainRejection(body);
        Assert.Contains("LayerMask: MaskGrow", text);
        Assert.Contains("구성요소", text);
        Assert.DoesNotContain("{", text);   // JSON을 그대로 내보이지 않는다
    }

    [Fact]
    public void MissingNode_OlderComfyWording_IsRecognisedToo()
    {
        const string body = """{"error": {"type": "invalid_prompt", "message": "Cannot execute because node WD14Tagger|pysssss does not exist.", "details": "Node ID '#5'", "extra_info": {}}, "node_errors": []}""";
        var text = ComfyClient.ExplainRejection(body);
        Assert.Contains("WD14Tagger|pysssss", text);
        Assert.Contains("구성요소", text);
    }

    [Fact]
    public void MissingModelFile_NamesTheFile()
    {
        const string body = """
            {"error": {"type": "prompt_outputs_failed_validation", "message": "Prompt outputs failed validation", "details": "", "extra_info": {}},
             "node_errors": {"4": {"errors": [{"type": "value_not_in_list", "message": "Value not in list",
               "details": "ckpt_name: 'waiIllustriousSDXL_v170.safetensors' not in []",
               "extra_info": {"input_name": "ckpt_name", "input_config": [[], {}], "received_value": "waiIllustriousSDXL_v170.safetensors"}}],
               "dependent_outputs": ["19"], "class_type": "CheckpointLoaderSimple"}}}
            """;
        var text = ComfyClient.ExplainRejection(body);
        Assert.Contains("waiIllustriousSDXL_v170.safetensors", text);
        Assert.Contains("CheckpointLoaderSimple", text);
        Assert.DoesNotContain("input_config", text);
    }

    [Theory]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("")]
    [InlineData("""{"error": "plain text error"}""")]
    public void UnknownShape_FallsBackToTheRawAnswer(string body)
    {
        Assert.Equal(body, ComfyClient.ExplainRejection(body));
    }

    [Fact]
    public void OtherValidationError_ShowsServerMessageAndDetails()
    {
        const string body = """
            {"error": {"type": "prompt_outputs_failed_validation", "message": "Prompt outputs failed validation", "details": "", "extra_info": {}},
             "node_errors": {"7": {"errors": [{"type": "required_input_missing", "message": "Required input is missing", "details": "mask", "extra_info": {"input_name": "mask"}}],
               "dependent_outputs": ["9"], "class_type": "LayerMask: MaskGrow"}}}
            """;
        var text = ComfyClient.ExplainRejection(body);
        Assert.Contains("Required input is missing", text);
        Assert.Contains("mask", text);
        Assert.Contains("LayerMask: MaskGrow", text);
    }

    [Fact]
    public void MissingColorPart_AppInstalledComfy_NamesTheMissingFile()
    {
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt") };
        var models = Path.Combine(s.ComfyRoot, "ComfyUI", "models");

        Assert.Contains(s.ColorCheckpoint, PipelineRunner.MissingColorPart(s));
        RuntimeTestHelpers.Touch(Path.Combine(models, "checkpoints", s.ColorCheckpoint));
        Assert.Contains(s.ColorControlNet, PipelineRunner.MissingColorPart(s));
        RuntimeTestHelpers.Touch(Path.Combine(models, "controlnet", s.ColorControlNet));
        Assert.DoesNotContain(s.ColorControlNet, PipelineRunner.MissingColorPart(s) ?? "");   // 모델은 다 있다 — 남은 것은 채색 노드(ColorOptionalTests)
    }

    [Fact]
    public void MissingColorPart_LinkedComfy_IsNotChecked()
    {
        // 연결한 ComfyUI는 extra_model_paths.yaml로 모델을 다른 폴더에 둘 수 있다 — 파일이 안 보인다고 막지 않는다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt"), LinkedComfyRoot = t.At("내 ComfyUI") };
        Assert.Null(PipelineRunner.MissingColorPart(s));
    }
}
