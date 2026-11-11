using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

    [Fact]
    public void FilterColorTags_ColorWordOnlyInsideAnotherWord_IsDropped()
    {
        // tan은 standing 안에, red는 covered 안에 들어 있을 뿐 색이 아니다 — 낱말 단위로 봐야 한다
        Assert.Equal("pink hair", ColorRefHelper.FilterColorTags("pink hair, standing, covered navel"));
    }

    [Theory]
    [InlineData("standing")]          // tan
    [InlineData("tank top")]          // tan
    [InlineData("tank_top")]          // 밑줄 표기
    [InlineData("katana")]            // tan
    [InlineData("covered_navel")]     // red
    [InlineData("layered skirt")]     // red
    [InlineData("feathered wings")]   // red
    [InlineData("blackboard")]        // black
    [InlineData("pinky out")]         // pink
    [InlineData("goldfish")]          // gold
    public void FilterColorTags_ColorLettersInsideAnotherWord_IsNotAColorTag(string tag)
    {
        // 전신 표지면 WD14가 거의 늘 내는 standing 같은 흔한 태그가 모든 쪽의 채색 프롬프트에 붙으면 안 된다
        Assert.Equal("", ColorRefHelper.FilterColorTags(tag));
    }

    [Theory]
    [InlineData("light brown hair")]
    [InlineData("light_brown_hair")]       // replace_underscore를 끈 WD14 출력
    [InlineData("blue eyes")]
    [InlineData("Blue Eyes")]
    [InlineData("red-framed eyewear")]     // 붙임표로 이은 색 낱말
    [InlineData("dark-skinned female")]    // 두 낱말짜리 색 낱말
    [InlineData("very dark skin")]
    [InlineData("dark_skin")]
    [InlineData("multicolored hair")]
    [InlineData("tan")]
    public void FilterColorTags_ColorWordAsWholeWord_IsKept(string tag)
    {
        // 색 낱말이 낱말로(공백·밑줄·붙임표 사이에) 들어 있는 태그는 그대로 남긴다
        Assert.Equal(tag, ColorRefHelper.FilterColorTags(tag));
    }

    [Fact]
    public void FilterColorTags_FullBodyCoverOutput_KeepsOnlyTheColorTags()
    {
        const string wd14 = "1girl, solo, standing, full body, long hair, light brown hair, blue eyes, tank top, covered navel, red-framed eyewear, katana, white background";

        Assert.Equal("light brown hair, blue eyes, red-framed eyewear", ColorRefHelper.FilterColorTags(wd14));
    }

    [Theory]
    [InlineData("colored inner hair")]   // 머리색 배치 — 대신 남는 태그가 없다
    [InlineData("colored tips")]
    [InlineData("tanlines")]             // 피부 무늬
    [InlineData("colored skin")]
    public void FilterColorTags_AppearanceColorWithoutColorName_IsKept(string tag)
    {
        // 색 이름은 없어도 캐릭터 외모의 색을 말하는 태그다 — 부분 문자열로 찾던 때는 colored 속 red, tanlines 속 tan으로 남던 것이라 빠지면 안 된다
        Assert.Equal(tag, ColorRefHelper.FilterColorTags(tag));
    }

    [Theory]
    [InlineData("partially colored")]            // 화풍
    [InlineData(@"colored pencil \(medium\)")]   // 화구 — WD14는 괄호를 \( \)로 내보낸다
    [InlineData("colored shadow")]
    [InlineData("standing")]
    public void FilterColorTags_ColoredStyleOrNoColorWord_IsDropped(string tag)
    {
        // colored를 색 낱말로 봐도 화풍·화구·그림자 태그는 캐릭터 색이 아니다
        Assert.Equal("", ColorRefHelper.FilterColorTags(tag));
    }

    [Fact]
    public void FilterColorTags_CoverWithColorLayoutTags_KeepsThemAndDropsStyleTags()
    {
        const string wd14 = @"1girl, black hair, pink hair, colored inner hair, multicolored hair, colored tips, dark-skinned female, tanlines, partially colored, colored pencil \(medium\), standing";

        Assert.Equal("black hair, pink hair, colored inner hair, multicolored hair, colored tips, dark-skinned female, tanlines", ColorRefHelper.FilterColorTags(wd14));
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

    [Fact]
    public void IpAdapterAvailable_PlusOnlyInEngineFolderName_ReturnsFalse()
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "ComfyUI-plus", customNode: true, "ip-adapter_sdxl_vit-h.safetensors", ClipVision);

        Assert.False(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Fact]
    public void IpAdapterAvailable_EngineFolderNamedPlusWithPlusModel_ReturnsTrue()
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "ComfyUI-plus", customNode: true, PlusSdxl, ClipVision);

        // plus는 모델 파일 이름에서만 본다 — 엔진 폴더 이름에 plus가 있어도 PLUS 모델이 있으면 준비된 것이다
        Assert.True(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Theory]
    [InlineData("ip-adapter-plus-face_sdxl_vit-h.safetensors", ClipVision)]    // 얼굴 전용 PLUS
    [InlineData("ip-adapter-faceid-plusv2_sdxl.bin", ClipVision)]              // FaceID — 이름에 plus와 sdxl이 들었을 뿐이다
    [InlineData(PlusSdxl + ".part", ClipVision)]                               // 받다 만 파일 (설치기는 같은 폴더에 .part로 받는다)
    [InlineData(PlusSdxl, "CLIP-ViT-bigG-14-laion2B-39B-b160k.safetensors")]  // bigG CLIP — 이 프리셋은 ViT-H를 찾는다
    public void IpAdapterAvailable_OnlyFilesTheLoaderDoesNotPick_ReturnsFalse(string ipadapterModel, string clipVisionModel)
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "엔진 폴더", customNode: true, ipadapterModel, clipVisionModel);

        // IPAdapter_plus 로더가 'PLUS (high strength)' + SDXL에서 고르는 파일이 아니면 준비된 것이 아니다 — 참으로 보면 채색이 실행 중에 실패한다
        Assert.False(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Theory]
    [InlineData(@"sdxl_models\" + PlusSdxl, ClipVision)]                                     // 하위 폴더 — 로더는 하위 폴더까지 찾는다
    [InlineData(PlusSdxl, @"ViT-H\" + ClipVision)]
    [InlineData("ip-adapter-plus_sdxl_vit-h.bin", "CLIP-ViT-H-14-laion2B-s32B-b79K.bin")]  // .bin도 고른다
    [InlineData(PlusSdxl, "IPAdapter_image_encoder_sd15.safetensors")]                      // 로더 규칙의 다른 CLIP 이름 꼴
    [InlineData(PlusSdxl, @"SD1.5\model.safetensors")]
    public void IpAdapterAvailable_FilesTheLoaderPicks_ReturnsTrue(string ipadapterModel, string clipVisionModel)
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "엔진 폴더", customNode: true, ipadapterModel, clipVisionModel);

        Assert.True(ColorRefHelper.IpAdapterAvailable(s));
    }

    [Theory]
    [InlineData(null, false)]                        // 모델이 없다 — 예외 없이 '준비 안 됨'(태그만으로 채색)
    [InlineData(@"z_models\" + ClipVision, true)]    // 열리지 않는 폴더 다음 폴더에 있다 — 건너뛰고 계속 찾는다
    public void IpAdapterAvailable_SubfolderThatCannotBeOpened_IsSkippedWithoutThrowing(string? clipVisionModel, bool expected)
    {
        using var t = new TempDir();
        var s = FakeEngine(t, "엔진 폴더", customNode: true, PlusSdxl, clipVisionModel);
        var shared = Directory.CreateDirectory(Path.Combine(s.ComfyRoot, "ComfyUI", "models", "clip_vision", "a_shared")).FullName;
        // 꺼진 NAS를 가리키는 폴더 링크처럼 '있지만 열리지 않는' 하위 폴더. 그런 링크는 권한이 있어야 만들 수 있어서,
        // 폴더를 공유 없이 열어 쥐는 것으로 같은 갈래의 오류(IOException — 접근 거부가 아니라 IgnoreInaccessible이 넘기지 않는다)를 낸다
        using var held = HoldExclusively(shared);
        Assert.Throws<IOException>(() => Directory.GetFiles(shared));

        // 이 판정은 작업 준비의 try 밖에서 불린다 — 여기서 예외가 나가면 채색 작업 전체가 첫 장 전에 멈춘다.
        // ComfyUI의 모델 목록(os.walk)처럼 열리지 않는 폴더만 건너뛰고 나머지로 판정한다
        Assert.Equal(expected, ColorRefHelper.IpAdapterAvailable(s));
    }

    /// <summary>폴더를 공유 없이 열어 쥔다 — 쥐고 있는 동안 다른 열기(폴더 내용 읽기)는 공유 위반 IOException으로 실패한다.</summary>
    private static SafeFileHandle HoldExclusively(string dir)
    {
        const uint genericRead = 0x80000000, openExisting = 3, backupSemantics = 0x02000000;   // 폴더는 FILE_FLAG_BACKUP_SEMANTICS가 있어야 열린다
        int error = 0;
        // 막 만든 폴더를 백신·색인기가 잠깐 열고 있으면 공유 없는 열기가 실패한다 — 몇 번 다시 해 본다
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var handle = CreateFileW(dir, genericRead, 0, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Thread.Sleep(50);
        }
        throw new IOException($"시험용 폴더를 공유 없이 열지 못했어요 (Win32 오류 {error}): {dir}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>임시 폴더 안에 IPAdapter 판정에 쓰는 폴더·파일만 갖춘 가짜 ComfyUI 설치를 만든다. 모델 이름에 하위 폴더를 넣을 수 있다(sdxl_models\…).</summary>
    private static AppSettings FakeEngine(TempDir t, string rootName, bool customNode, string ipadapterModel, string? clipVisionModel)
    {
        var s = new AppSettings { LinkedComfyRoot = t.At(rootName) };
        var comfy = Path.Combine(s.ComfyRoot, "ComfyUI");
        if (customNode) Directory.CreateDirectory(Path.Combine(comfy, "custom_nodes", "ComfyUI_IPAdapter_plus"));
        var ipaDir = Directory.CreateDirectory(Path.Combine(comfy, "models", "ipadapter")).FullName;
        Touch(Path.Combine(ipaDir, ipadapterModel));
        var clipDir = Directory.CreateDirectory(Path.Combine(comfy, "models", "clip_vision")).FullName;
        if (clipVisionModel != null) Touch(Path.Combine(clipDir, clipVisionModel));
        return s;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}
