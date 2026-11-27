using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>글자 읽기 엔진 고르기(설정 <see cref="AppSettings.OcrEngine"/>, 2026-11-27): 기본은 Hayai OCR, 전의 manga-ocr도 고를 수 있다.
/// 고른 엔진의 모델만 받고, 고른 엔진의 모델이 없으면 다른 엔진으로 조용히 넘어가지 않는다(번역이 준비되지 않은 것으로 본다).</summary>
public class OcrEngineChoiceTests
{
    private static readonly Component[] Hayai =
        [ComponentCatalog.ModelHayaiPrefill, ComponentCatalog.ModelHayaiDecode, ComponentCatalog.ModelHayaiPos, ComponentCatalog.ModelHayaiTokenizer];
    private static readonly Component[] Manga = [ComponentCatalog.ModelOcrEncoder, ComponentCatalog.ModelOcrDecoder, ComponentCatalog.ModelOcrVocab];

    [Fact]
    public void Default_IsHayai()
    {
        Assert.Equal("Hayai", new AppSettings().OcrEngine);
    }

    [Fact]
    public void Catalog_OcrModelsAreTaggedWithTheirEngine_AndNothingElseIs()
    {
        Assert.Equal(Hayai, ComponentCatalog.All.Where(c => c.Ocr == "Hayai"));
        Assert.Equal(Manga, ComponentCatalog.All.Where(c => c.Ocr == "MangaOcr"));
        Assert.Equal(Hayai.Length + Manga.Length, ComponentCatalog.All.Count(c => c.Ocr != null));
        // 번역에 쓰는 모델이라 ComfyUI 없이 받는 묶음에 든다
        Assert.All(Hayai.Concat(Manga), c => Assert.False(c.ForComfy));
    }

    [Fact]
    public void Catalog_HayaiFiles_ArePinnedToOneRevision_AndChecked()
    {
        // 저장소의 main이 바뀌어도 같은 파일을 받게 판(커밋)을 박아 두고, 받은 뒤 해시로 확인한다 — main을 가리키면 모델이 바뀐 날부터 해시가 어긋나 새 설치가 막힌다
        Assert.All(Hayai, c =>
        {
            Assert.StartsWith("https://huggingface.co/JustANormalTinkerer/hayai-ocr-v2.7-supernova-onnx/resolve/", c.Url);
            Assert.DoesNotContain("/resolve/main/", c.Url);
            Assert.Equal(64, c.Sha256?.Length);
        });
    }

    [Theory]
    [InlineData("Hayai")]
    [InlineData("MangaOcr")]
    public void IsWanted_OnlyTheChosenEnginesOcrModels(string engine)
    {
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = engine });

        Assert.All(Hayai, c => Assert.Equal(engine == "Hayai", installer.IsWanted(c)));
        Assert.All(Manga, c => Assert.Equal(engine == "MangaOcr", installer.IsWanted(c)));
        Assert.All(ComponentCatalog.All.Where(c => c.Ocr == null), c => Assert.True(installer.IsWanted(c)));
    }

    [Fact]
    public void MissingEngine_AsksOnlyForTheChosenEnginesModels()
    {
        // v1.18.1까지 쓰던 PC: manga-ocr 모델은 있고 Hayai 모델은 없다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = "MangaOcr" };
        var installer = new ComponentInstaller(s);
        foreach (var c in ComponentCatalog.EngineComponents.Except(Hayai)) RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));

        Assert.Empty(installer.MissingEngine());              // manga-ocr를 고른 채면 더 받을 것이 없다

        s.OcrEngine = "Hayai";
        Assert.Equal(Hayai, installer.MissingEngine());       // Hayai를 고르면 그 모델 넷을 받아야 한다
    }

    [Theory]
    [InlineData("Hayai", false)]
    [InlineData("MangaOcr", true)]
    public void Translator_IsReady_OnlyWithTheChosenEnginesModels(string engine, bool ready)
    {
        // manga-ocr 모델만 있는 PC에서 Hayai를 골랐으면 준비되지 않은 것이다 — manga-ocr로 조용히 읽지 않는다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = engine };
        using var env = new OnnxEnv(s);
        foreach (var file in new[] { TextDetector.ModelFile, LamaInpainter.ModelFile, MangaOcr.EncoderFile, MangaOcr.DecoderFile, MangaOcr.VocabFile })
            RuntimeTestHelpers.Touch(env.ModelPath(file));

        Assert.Equal(ready, new EmbeddedTranslator(s, env).IsReady);

        foreach (var file in HayaiOcr.Files) RuntimeTestHelpers.Touch(env.ModelPath(file));
        Assert.True(new EmbeddedTranslator(s, env).IsReady);
    }
}
