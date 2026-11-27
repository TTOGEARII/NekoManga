using NekoManga.Core.Pipeline;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>채색만 쓰는 구성요소는 골라서 받는다(2026-11-18 사용자 결정) — 재생성 검열 제거에 드는 것은 늘 받고, 채색 쪽은 준비 창의 체크로 뺄 수 있다.</summary>
public class ColorOptionalTests
{
    [Fact]
    public void Catalog_ColorOnly_IsExactlyWhatOnlyTheColorWorkflowsUse()
    {
        // 채색 그래프(BuildAutoColor)·표지 태그 추출(BuildTagExtract)만 쓰는 것: WD14 태거·문자열 노드(pysssss)·IPAdapter.
        // 재생성 그래프(BuildRegenCrop)가 쓰는 LayerStyle(MaskGrow)·전처리기(AIO_Preprocessor)·체크포인트·ControlNet은 여기 들면 안 된다
        Component[] expected =
        [
            ComponentCatalog.NodeWd14, ComponentCatalog.ModelColorTagger, ComponentCatalog.ModelColorTaggerTags, ComponentCatalog.NodeCustomScripts,
            ComponentCatalog.NodeIpAdapter, ComponentCatalog.ModelIpAdapter, ComponentCatalog.ModelClipVision,
        ];
        Assert.Equal(expected.Select(c => c.Id).Order(), ComponentCatalog.All.Where(c => c.ColorOnly).Select(c => c.Id).Order());
        Assert.All(expected, c => Assert.True(c.ForComfy));
    }

    [Fact]
    public void IsWanted_FollowsTheInstallColorSetting_OnlyForColorOnlyParts()
    {
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt") };
        var installer = new ComponentInstaller(s);
        Assert.True(s.InstallColor);   // 기본은 받는다
        // (글자 읽기 모델은 고른 엔진에 따라 갈린다 — OcrEngineChoiceTests가 본다)
        var notOcr = ComponentCatalog.All.Where(c => c.Ocr == null).ToList();
        Assert.All(notOcr, c => Assert.True(installer.IsWanted(c)));

        s.InstallColor = false;
        Assert.All(notOcr, c => Assert.Equal(!c.ColorOnly, installer.IsWanted(c)));
        // 재생성에 드는 것은 그대로 받는다
        Assert.True(installer.IsWanted(ComponentCatalog.ModelCheckpoint));
        Assert.True(installer.IsWanted(ComponentCatalog.NodeLayerStyle));
        Assert.True(installer.IsWanted(ComponentCatalog.NodeControlnetAux));
    }

    [Fact]
    public void MissingColorPart_NodesNotInstalled_TellsWhereToGetThem()
    {
        // 채색을 빼고 설치한 뒤 메인 창에서 채색을 켜면 — ComfyUI의 '노드가 없어요' 대신 무엇을 받아야 하는지 시작할 때 알린다
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("rt") };
        var installer = new ComponentInstaller(s);
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.ModelCheckpoint.Probe));
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.ModelControlnet.Probe));

        Assert.Contains("구성요소", PipelineRunner.MissingColorPart(s));
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.NodeWd14.Probe));
        Assert.NotNull(PipelineRunner.MissingColorPart(s));
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.NodeCustomScripts.Probe));
        Assert.Null(PipelineRunner.MissingColorPart(s));   // 표지 매칭(IPAdapter)·태그 모델은 없어도 채색은 돈다
    }
}
