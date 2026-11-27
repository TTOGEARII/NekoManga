using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>글자 읽기 엔진 고르기(설정 <see cref="AppSettings.OcrEngine"/>, 2026-11-27): 기본은 manga-ocr-nar-small, Hayai OCR과 전의 manga-ocr도 고를 수 있다.
/// 고른 엔진의 모델만 받고, 고른 엔진의 모델이 없으면 다른 엔진으로 조용히 넘어가지 않는다(번역이 준비되지 않은 것으로 본다).</summary>
public class OcrEngineChoiceTests
{
    private static readonly Component[] Nar = [ComponentCatalog.ModelNarOcr, ComponentCatalog.ModelNarVocab];
    private static readonly Component[] Hayai =
        [ComponentCatalog.ModelHayaiPrefill, ComponentCatalog.ModelHayaiDecode, ComponentCatalog.ModelHayaiPos, ComponentCatalog.ModelHayaiTokenizer];
    private static readonly Component[] Manga = [ComponentCatalog.ModelOcrEncoder, ComponentCatalog.ModelOcrDecoder, ComponentCatalog.ModelOcrVocab];

    private static Component[] ModelsOf(string engine) => engine switch { "NarSmall" => Nar, "Hayai" => Hayai, _ => Manga };
    private static string[] FilesOf(string engine) => engine switch
    {
        "NarSmall" => NarOcr.Files,
        "Hayai" => HayaiOcr.Files,
        _ => [MangaOcr.EncoderFile, MangaOcr.DecoderFile, MangaOcr.VocabFile],
    };
    private static readonly string[] Engines = ["NarSmall", "Hayai", "MangaOcr"];

    [Fact]
    public void Default_IsNarSmall()
    {
        Assert.Equal("NarSmall", new AppSettings().OcrEngine);
    }

    [Fact]
    public void Catalog_OcrModelsAreTaggedWithTheirEngine_AndNothingElseIs()
    {
        Assert.Equal(Nar, ComponentCatalog.All.Where(c => c.Ocr == "NarSmall"));
        Assert.Equal(Hayai, ComponentCatalog.All.Where(c => c.Ocr == "Hayai"));
        Assert.Equal(Manga, ComponentCatalog.All.Where(c => c.Ocr == "MangaOcr"));
        Assert.Equal(Nar.Length + Hayai.Length + Manga.Length, ComponentCatalog.All.Count(c => c.Ocr != null));
        // 번역에 쓰는 모델이라 ComfyUI 없이 받는 묶음에 든다
        Assert.All(Nar.Concat(Hayai).Concat(Manga), c => Assert.False(c.ForComfy));
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

    [Fact]
    public void Catalog_NarSmallFiles_ComeFromOurModelsRelease_AndAreChecked()
    {
        // 기본 엔진의 모델은 우리 모델 릴리스(고정 태그 models)에서 받는다 — 사용자가 비영리 개인 프로젝트로 바꾸지 않고 다시 올렸다(2026-11-27, CC BY-NC-SA 4.0).
        // 주소의 파일 이름은 올린 자산의 이름 그대로여야 하고(다르면 새 설치가 404), 받은 뒤 해시로 원본과 같은 파일인지 확인한다
        Assert.Equal("https://github.com/TTOGEARII/NekoManga/releases/download/models/ocr_nar.onnx", ComponentCatalog.ModelNarOcr.Url);
        Assert.Equal("https://github.com/TTOGEARII/NekoManga/releases/download/models/ocr_nar_vocab.json", ComponentCatalog.ModelNarVocab.Url);
        Assert.Equal("5ee628dfd40c59c522022d87c31f8f903104198877b9ba4cf795603dc540dcf5", ComponentCatalog.ModelNarOcr.Sha256);
        Assert.Equal("9f1059427b54216c869dbf1244157f86bef74b1a0061c46a6001402c6f0c1dbf", ComponentCatalog.ModelNarVocab.Sha256);
    }

    [Theory]
    [InlineData("NarSmall")]
    [InlineData("Hayai")]
    [InlineData("MangaOcr")]
    public void IsWanted_OnlyTheChosenEnginesOcrModels(string engine)
    {
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = engine });

        foreach (var other in Engines)
            Assert.All(ModelsOf(other), c => Assert.Equal(other == engine, installer.IsWanted(c)));
        Assert.All(ComponentCatalog.All.Where(c => c.Ocr == null), c => Assert.True(installer.IsWanted(c)));
    }

    [Fact]
    public void MissingEngine_AsksOnlyForTheChosenEnginesModels()
    {
        // v1.18.1까지 쓰던 PC: manga-ocr 모델은 있고 새 엔진들의 모델은 없다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = "MangaOcr" };
        var installer = new ComponentInstaller(s);
        foreach (var c in ComponentCatalog.EngineComponents.Except(Nar).Except(Hayai)) RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));

        Assert.Empty(installer.MissingEngine());              // manga-ocr를 고른 채면 더 받을 것이 없다

        s.OcrEngine = "NarSmall";
        Assert.Equal(Nar, installer.MissingEngine());         // 기본 엔진으로 가면 그 모델 둘만 받는다(Hayai의 것은 받지 않는다)

        s.OcrEngine = "Hayai";
        Assert.Equal(Hayai, installer.MissingEngine());
    }

    [Theory]
    [InlineData("NarSmall")]
    [InlineData("Hayai")]
    [InlineData("MangaOcr")]
    public void Translator_IsReady_OnlyWithTheChosenEnginesModels(string engine)
    {
        // 다른 엔진의 모델만 있는 PC에서는 준비되지 않은 것이다 — 있는 엔진으로 조용히 읽지 않는다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt"), OcrEngine = engine };
        using var env = new OnnxEnv(s);
        foreach (var file in new[] { TextDetector.ModelFile, LamaInpainter.ModelFile }.Concat(Engines.Where(e => e != engine).SelectMany(FilesOf)))
            RuntimeTestHelpers.Touch(env.ModelPath(file));

        Assert.False(new EmbeddedTranslator(s, env).IsReady);

        foreach (var file in FilesOf(engine)) RuntimeTestHelpers.Touch(env.ModelPath(file));
        Assert.True(new EmbeddedTranslator(s, env).IsReady);
    }
}
