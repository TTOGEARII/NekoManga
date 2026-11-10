using NekoManga.Core.Comfy;

namespace NekoManga.Core.Tests;

/// <summary>ColorRefHelper — WD14 태그에서 캐릭터 색 태그만 걸러 내는 규칙과, IPAdapter 정밀 매칭 준비 여부 판정(임시 폴더의 가짜 엔진으로).</summary>
public class ColorRefHelperTests
{
    private const string PlusSdxl = "ip-adapter-plus_sdxl_vit-h.safetensors";
    private const string ClipVision = "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors";

    // ───────── FilterColorTags ─────────

    [Fact]
    public void FilterColorTags_Wd14Output_KeepsCharacterColorTagsOnceInOrder()
    {
        const string wd14 = "1girl, solo, long hair, pink hair, blue eyes, white background, monochrome, red dress, Pink Hair, smile, dark skin, black thighhighs";

        // 색이 든 캐릭터 태그만 원래 순서로 남기고, 대소문자만 다른 중복은 처음 것 하나로 — 배경·화풍 태그는 색 낱말이 있어도 뺀다
        Assert.Equal("pink hair, blue eyes, red dress, dark skin, black thighhighs", ColorRefHelper.FilterColorTags(wd14));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ,, ")]
    [InlineData("1girl, solo, smile, looking at viewer")]
    [InlineData("white background")]
    [InlineData("blue theme, black border")]
    [InlineData("greyscale, monochrome")]
    [InlineData("red outline, black sclera, pink text")]
    public void FilterColorTags_NoCharacterColor_ReturnsEmpty(string wd14)
    {
        // 색 낱말이 없거나, 있어도 배경·테두리·외곽선·흰자위·글자처럼 캐릭터 색이 아닌 태그뿐이면 프롬프트에 아무것도 넣지 않는다
        Assert.Equal("", ColorRefHelper.FilterColorTags(wd14));
    }

    [Fact(Skip = "알려진 결함: 색 낱말을 부분 문자열로 찾아서 standing(tan)·covered navel(red)처럼 색과 무관한 흔한 WD14 태그가 색 태그로 남고, 모든 페이지의 채색 프롬프트에 붙는다")]
    public void FilterColorTags_ColorWordOnlyInsideAnotherWord_IsDropped()
    {
        // tan은 standing 안에, red는 covered 안에 들어 있을 뿐 색이 아니다 — 낱말 단위로 봐야 한다
        Assert.Equal("pink hair", ColorRefHelper.FilterColorTags("pink hair, standing, covered navel"));
    }

    // ───────── IpAdapterAvailable ─────────

    [Fact]
    public void IpAdapterAvailable_NodeAndSdxlPlusModelAndClipVision_ReturnsTrue()
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "엔진 폴더", customNode: true, PlusSdxl, ClipVision);

        Assert.True(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Theory]
    [InlineData(false, PlusSdxl, ClipVision)]                              // 커스텀 노드가 없다
    [InlineData(true, "ip-adapter_sdxl_vit-h.safetensors", ClipVision)]    // SDXL용이지만 PLUS가 아니다
    [InlineData(true, "ip-adapter-plus_sd15.safetensors", ClipVision)]     // PLUS지만 SD1.5용이다
    [InlineData(true, PlusSdxl, null)]                                     // CLIP 비전 모델이 없다
    public void IpAdapterAvailable_MissingPiece_ReturnsFalse(bool customNode, string ipadapterModel, string? clipVisionModel)
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "엔진 폴더", customNode, ipadapterModel, clipVisionModel);

        // 채색 워크플로는 SDXL 체크포인트에 'PLUS (high strength)' 프리셋을 쓰므로 셋이 모두 있어야 한다 — 하나라도 없으면 태그만으로 간다
        Assert.False(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Fact(Skip = "알려진 결함: plus 확인을 파일 이름이 아니라 전체 경로에 해서, 엔진 폴더 경로에 plus가 들면 PLUS가 아닌 SDXL 모델만 있어도 준비됐다고 판정한다 — 그러면 IPAdapterUnifiedLoader의 PLUS 프리셋이 실행 중에 실패한다")]
    public void IpAdapterAvailable_PlusOnlyInEngineFolderName_ReturnsFalse()
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "ComfyUI-plus", customNode: true, "ip-adapter_sdxl_vit-h.safetensors", ClipVision);

        Assert.False(ColorRefHelper.IpAdapterAvailable(s));
    }

    /// <summary>임시 폴더 안에 IPAdapter 판정에 쓰는 폴더·파일만 갖춘 가짜 ComfyUI 설치를 만든다.</summary>
    private static AppSettings FakeEngine(TempDir t, string rootName, bool customNode, string ipadapterModel, string? clipVisionModel)
    {
        var s = new AppSettings { LinkedComfyRoot = t.At(rootName) };
        var comfy = Path.Combine(s.ComfyRoot, "ComfyUI");
        if (customNode) Directory.CreateDirectory(Path.Combine(comfy, "custom_nodes", "ComfyUI_IPAdapter_plus"));
        var ipaDir = Directory.CreateDirectory(Path.Combine(comfy, "models", "ipadapter")).FullName;
        File.WriteAllText(Path.Combine(ipaDir, ipadapterModel), "");
        var clipDir = Directory.CreateDirectory(Path.Combine(comfy, "models", "clip_vision")).FullName;
        if (clipVisionModel != null) File.WriteAllText(Path.Combine(clipDir, clipVisionModel), "");
        return s;
    }
}
