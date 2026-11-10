using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>ComponentInstaller의 설치 여부 판정 — 내려받지 않고 임시 폴더의 파일 존재만으로 본다.</summary>
public class ComponentInstallerTests
{
    [Fact]
    public void Missing_EmptyRuntimeRoot_ListsWholeCatalogInInstallOrder()
    {
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("새 런타임 폴더") });

        // 설치 창은 이 순서대로 받는다 — 목록 순서(필수 엔진 먼저)를 바꾸지 않아야 한다
        Assert.Equal(ComponentCatalog.All, installer.Missing());
        Assert.Equal(ComponentCatalog.EngineComponents, installer.MissingEngine());
    }

    [Fact]
    public void Missing_EngineFilesPresent_LeavesOnlyComfyBundle()
    {
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("런타임 テスト") });

        // 받다 만 .part만 있으면 아직 설치가 아니다 — 다음 설치가 이어받는다
        foreach (var c in ComponentCatalog.EngineComponents) RuntimeTestHelpers.Touch(installer.Resolve(c.Probe) + ".part");
        Assert.Equal(ComponentCatalog.EngineComponents, installer.MissingEngine());

        foreach (var c in ComponentCatalog.EngineComponents) RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));
        Assert.Empty(installer.MissingEngine());
        Assert.Equal(ComponentCatalog.ComfyComponents, installer.Missing());
    }

    [Theory]
    [InlineData(null)]                    // 연결 안 함 → RuntimeRoot 아래 포터블
    [InlineData("   ")]                   // 공백뿐인 값도 연결 안 함
    [InlineData("기존 ComfyUI 설치")]     // 이미 설치된 포터블을 연결
    public void IsInstalled_ComfyParts_LookInSameFolderAsSettingsComfyRoot(string? linked)
    {
        using var t = new TempDir();
        var s = new AppSettings
        {
            RuntimeRoot = t.At("런타임"),
            LinkedComfyRoot = string.IsNullOrWhiteSpace(linked) ? linked ?? "" : t.At(linked),
        };
        var installer = new ComponentInstaller(s);

        // 설치 판정과 실행(AppSettings.ComfyRoot)이 같은 ComfyUI를 봐야 '설치됨'인데 실행할 수 없는 일이 없다
        Assert.False(installer.IsInstalled(ComponentCatalog.ComfyPortable));
        RuntimeTestHelpers.Touch(Path.Combine(s.ComfyRoot, "ComfyUI", "main.py"));
        Assert.True(installer.IsInstalled(ComponentCatalog.ComfyPortable));

        // 커스텀 노드는 그 ComfyUI의 custom_nodes에서 찾는다
        Assert.False(installer.IsInstalled(ComponentCatalog.NodeWd14));
        RuntimeTestHelpers.Touch(Path.Combine(s.ComfyRoot, "ComfyUI", "custom_nodes", "ComfyUI-WD14-Tagger", "wd14tagger.py"));
        Assert.True(installer.IsInstalled(ComponentCatalog.NodeWd14));

        // 실행에는 내장 파이썬도 있어야 한다 — main.py만으로는 ComfyAvailable이 아니다
        Assert.False(s.ComfyAvailable);
        RuntimeTestHelpers.Touch(Path.Combine(s.ComfyRoot, "python_embeded", "python.exe"));
        Assert.True(s.ComfyAvailable);
    }

    [Fact]
    public void IsInstalled_LinkedComfy_IgnoresRuntimeCopyButKeepsOtherFilesInRuntimeRoot()
    {
        using var t = new TempDir();
        var s = new AppSettings { RuntimeRoot = t.At("런타임"), LinkedComfyRoot = t.At("기존 ComfyUI") };
        var installer = new ComponentInstaller(s);

        // 연결했으면 앱은 연결된 ComfyUI를 돌린다 — RuntimeRoot에 남은 사본은 설치로 치지 않는다
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.ComfyPortable.Probe));
        Assert.False(installer.IsInstalled(ComponentCatalog.ComfyPortable));

        // ComfyUI 폴더 밖의 구성요소(7zr·엔진 모델)는 그대로 RuntimeRoot에서 찾는다
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.SevenZip.Probe));
        RuntimeTestHelpers.Touch(installer.Resolve(ComponentCatalog.ModelCtd.Probe));
        Assert.True(installer.IsInstalled(ComponentCatalog.SevenZip));
        Assert.True(installer.IsInstalled(ComponentCatalog.ModelCtd));
    }
}
