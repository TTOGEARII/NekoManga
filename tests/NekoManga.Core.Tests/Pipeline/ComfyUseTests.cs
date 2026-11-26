using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>'이 실행이 ComfyUI를 쓰는가'(<see cref="PipelineRunner.NeedsComfy"/>)와 그것을 내장 AI의 GPU 자동 규칙에 알려 주는 일(OnnxEnv.JobUsesComfy) — 검토 2026-11-26:
/// 글자 자리 다시 그리기(TextRegen)를 켜 둔 설정은 '쓸 수 있는' 설정일 뿐이다. 번역기를 쓰지 않는 실행(결과 창의 다시 지우기·다시 식자·다시 검열 제거)은 SDXL을 부르지 않는데,
/// 설정만 보던 규칙은 그런 실행까지 내장 AI를 CPU로 돌렸다.</summary>
public class ComfyUseTests
{
    private sealed class Sink : IProgress<PageProgress>
    {
        public void Report(PageProgress value) { }
    }

    [Theory]
    // 채색, 검열 제거 켬, 복원 방식, 번역 켬, 글자 자리 다시 그리기, 번역기를 쓰는 실행 → ComfyUI를 쓰는가
    [InlineData(false, false, "Basic", false, false, true, false)]
    [InlineData(true, false, "Basic", false, false, false, true)]             // 채색은 어느 실행이든
    [InlineData(false, true, "ComfyRegen", false, false, false, true)]
    [InlineData(false, true, "ComfyCustom", false, false, false, true)]
    [InlineData(false, true, "Basic", false, false, true, false)]
    [InlineData(false, false, "ComfyRegen", false, false, true, false)]       // 검열 제거를 껐으면 복원 방식은 상관없다
    [InlineData(false, false, "Basic", true, true, true, true)]               // 글자 자리 다시 그리기: 번역기를 쓰는 실행에서만
    [InlineData(false, false, "Basic", true, true, false, false)]             // 결과 창의 다시 지우기·다시 식자
    [InlineData(false, false, "Basic", false, true, true, false)]             // 번역을 껐으면 글자를 지우지도 않는다
    public void NeedsComfy_ByWhatThisRunActuallyDoes(bool color, bool decensor, string mode, bool translate, bool textRegen, bool needTranslator, bool expected)
    {
        var s = new AppSettings { EnableAutoColor = color, EnableDecensor = decensor, DecensorMode = mode, EnableTranslate = translate, TextRegen = textRegen };

        Assert.Equal(expected, PipelineRunner.NeedsComfy(s, needTranslator));
    }

    [Fact]
    public async Task RedoRun_WithTextRegenOnInSettings_TellsTheOnnxEnvironmentItUsesNoComfy()
    {
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        s.EnableTranslate = true; s.TextRegen = true;   // 설정으로만 보면 ComfyUI를 쓰는 작업이다
        using var runner = new PipelineRunner(s);
        Assert.Null(runner.Onnx.JobUsesComfy);

        await runner.RedoPagesAsync([(t.At("없는 원고.png"), t.At("out", "없는 원고.png"))], new Sink(), CancellationToken.None);

        Assert.False(runner.Onnx.JobUsesComfy);
    }

    [Fact]
    public async Task FullRun_WithTextRegenOn_TellsTheOnnxEnvironmentItUsesComfy()
    {
        using var t = new TempDir();
        var s = EnginelessSettings(t);
        s.EnableTranslate = true; s.TextRegen = true;
        using var runner = new PipelineRunner(s);

        // 번역 모델이 없어 시작 전 확인에서 멈춘다 — 그 전에 이 실행의 ComfyUI 사용은 이미 정해져 있어야 한다(모델을 올리기 전에)
        await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RunAsync([t.At("원고.png")], new Sink(), CancellationToken.None));

        Assert.True(runner.Onnx.JobUsesComfy);
    }
}
