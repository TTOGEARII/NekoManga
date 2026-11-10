using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>ComponentCatalog — 내려받을 구성요소 목록의 건전성과, 설치 위치가 엔진·설정이 찾는 위치와 맞는지.</summary>
public class ComponentCatalogTests
{
    private static readonly string[] GpuKinds = ["nvidia", "nvidia-old", "amd"];

    [Fact]
    public void All_IdsAreUniqueAndUsableInFileNames()
    {
        var ids = ComponentCatalog.All.Select(c => c.Id).ToList();

        // id는 진행 표시의 열쇠이자 임시 파일 이름(tmp/<id>.zip)이다 — Windows 파일 이름은 대소문자를 가리지 않는다
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id =>
        {
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, id);
        });
    }

    [Fact]
    public void Urls_AreAbsoluteHttps()
    {
        var urls = ComponentCatalog.All.Select(c => c.Url).Concat(GpuKinds.Select(ComponentCatalog.ComfyPortableUrl));

        Assert.All(urls, url =>
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), url);
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        });
    }

    [Fact]
    public void Paths_StayInsideRuntimeRootAndProbeMatchesTarget()
    {
        Assert.All(ComponentCatalog.All, c =>
        {
            foreach (var rel in new[] { c.Target, c.Probe })
            {
                Assert.False(string.IsNullOrWhiteSpace(rel), c.Id);
                Assert.False(Path.IsPathRooted(rel), $"{c.Id}: {rel}");          // RuntimeRoot 기준 상대 경로
                Assert.DoesNotContain("..", rel.Split('/', '\\'));               // RuntimeRoot 밖으로 나가지 않는다
            }
            if (c.Kind == ComponentKind.File)
                Assert.Equal(c.Target, c.Probe);   // 받은 파일 자체가 설치 표시 — 다르면 받고 나서도 계속 '필요'로 남는다
            else
                Assert.True(c.Target == "." || c.Probe.StartsWith(c.Target + "/", StringComparison.Ordinal),
                            $"{c.Id}: 확인 경로 {c.Probe}가 푸는 폴더 {c.Target} 안에 있어야 한다");
        });
    }

    [Fact]
    public void Sha256_WhenGiven_IsWellFormed()
    {
        // 남의 저장소에서 온 Aletheia 모델은 받은 뒤 해시로 확인한다
        Assert.All(new[] { ComponentCatalog.ModelAletheiaDetector, ComponentCatalog.ModelAletheiaBar, ComponentCatalog.ModelAletheiaMosaic },
                   c => Assert.NotNull(c.Sha256));
        // 형식이 틀린 해시는 어떤 파일과도 맞지 않아 그 구성요소를 영영 설치할 수 없게 된다
        Assert.All(ComponentCatalog.All.Where(c => c.Sha256 != null),
                   c => Assert.Equal(32, Convert.FromHexString(c.Sha256!).Length));
    }

    [Fact]
    public void Obsolete_NeverDeletesAnotherComponentsFile()
    {
        static string Norm(string p) => p.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var owned = ComponentCatalog.All.SelectMany(c => new[] { c.Target, c.Probe }).Select(Norm).ToHashSet();

        // 만화판 LaMa가 대체한 옛 Carve LaMa는 설치 뒤 지운다
        Assert.Contains("models/lama_fp32.onnx", ComponentCatalog.ModelLama.Obsolete ?? []);
        Assert.All(ComponentCatalog.All.SelectMany(c => c.Obsolete ?? []), rel =>
        {
            Assert.False(Path.IsPathRooted(rel), rel);
            // 지금 목록의 파일을 지우면 설치하자마자 다시 '필요'가 된다
            Assert.DoesNotContain(Norm(rel), owned);
        });
    }

    [Fact]
    public void All_InstallOrder_PutsPrerequisitesFirst()
    {
        var all = ComponentCatalog.All.ToList();

        // 내장 엔진(필수) 먼저, ComfyUI 묶음(선택)은 뒤에 — 채색을 건너뛰어도 번역·검열 제거가 먼저 준비된다
        Assert.True(all.FindLastIndex(c => !c.ForComfy) < all.FindIndex(c => c.ForComfy));
        // 7z 묶음은 7zr.exe로 푼다
        int sevenZip = all.IndexOf(ComponentCatalog.SevenZip);
        Assert.All(all.Where(c => c.Kind == ComponentKind.Archive7z), c => Assert.True(all.IndexOf(c) > sevenZip, c.Id));
        // ComfyUI 폴더 안에 들어가는 노드·모델은 ComfyUI 포터블을 푼 뒤에
        int portable = all.IndexOf(ComponentCatalog.ComfyPortable);
        Assert.All(all.Where(c => c.Target.StartsWith(ComponentCatalog.ComfyDirName + "/", StringComparison.Ordinal)),
                   c => Assert.True(all.IndexOf(c) > portable, c.Id));
    }

    [Theory]
    [InlineData("nvidia", null)]           // RTX 20 이상 — CUDA 13 표준 묶음
    [InlineData("nvidia-old", "cu126")]    // GTX 10 이하 — CUDA 12.6 묶음
    [InlineData("amd", "amd")]             // Radeon — ROCm 묶음
    public void ComfyPortableUrl_EachGpuKindGetsItsOwnBuild(string vendor, string? marker)
    {
        var url = ComponentCatalog.ComfyPortableUrl(vendor);
        var file = Path.GetFileName(new Uri(url).AbsolutePath);

        Assert.EndsWith(".7z", file);   // ComfyPortable은 Archive7z — 어느 종류든 7zr로 푼다
        Assert.DoesNotContain(url, GpuKinds.Where(v => v != vendor).Select(ComponentCatalog.ComfyPortableUrl));
        if (marker == null)
        {
            // 목록에 실린 기본 항목이 표준 NVIDIA 묶음이다
            Assert.Equal(ComponentCatalog.ComfyPortable.Url, url);
            Assert.DoesNotContain("cu126", file);
            Assert.DoesNotContain("amd", file);
        }
        else Assert.Contains(marker, file);
    }

    [Theory]
    [InlineData("model-ctd", TextDetector.ModelFile)]
    [InlineData("model-lama", LamaInpainter.ModelFile)]
    [InlineData("model-ocr-enc", MangaOcr.EncoderFile)]
    [InlineData("model-ocr-dec", MangaOcr.DecoderFile)]
    [InlineData("model-ocr-vocab", MangaOcr.VocabFile)]
    [InlineData("model-censor", CensorNet.ModelFile)]
    [InlineData("model-aletheia-mrcnn", AletheiaDetector.ModelFile)]
    [InlineData("model-aletheia-bar", AletheiaInpainter.BarModelFile)]
    [InlineData("model-aletheia-mosaic", AletheiaInpainter.MosaicModelFile)]
    public void EngineModel_InstalledWhereEngineLooks(string componentId, string engineFile)
    {
        var c = Assert.Single(ComponentCatalog.All, x => x.Id == componentId);
        // 번역·검열 제거에 필요한 모델이라 ComfyUI 없이 받는 필수 묶음에 든다
        Assert.False(c.ForComfy);

        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("런타임 폴더 テスト") };
        using var env = new OnnxEnv(s);
        Assert.False(env.ModelExists(engineFile));
        RuntimeTestHelpers.Touch(new ComponentInstaller(s).Resolve(c.Target));

        // 설치기가 쓴 자리 = 엔진이 읽는 자리 — 파일 이름 한쪽만 바꾸면 '설치됨'인데 엔진은 모델이 없다고 한다
        Assert.True(env.ModelExists(engineFile));
    }

    [Fact]
    public void ComfyModels_MatchSettingsDefaultFileNames()
    {
        var d = new AppSettings();
        static string InComfy(string rel) => $"{ComponentCatalog.ComfyDirName}/ComfyUI/{rel}";

        // 채색 워크플로는 설정의 파일 이름으로 models/checkpoints·models/controlnet에서 모델을 고른다
        Assert.Equal(InComfy("models/checkpoints/" + d.ColorCheckpoint), ComponentCatalog.ModelCheckpoint.Target);
        Assert.Equal(InComfy("models/controlnet/" + d.ColorControlNet), ComponentCatalog.ModelControlnet.Target);
        // 재생성도 기본은 채색과 같은 설치본을 쓴다
        Assert.Equal(d.ColorCheckpoint, d.RegenCheckpoint);
        Assert.Equal(d.ColorControlNet, d.RegenControlNet);
    }
}
