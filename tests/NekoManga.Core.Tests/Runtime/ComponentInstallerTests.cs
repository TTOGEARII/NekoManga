using System.IO.Compression;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>ComponentInstaller의 설치 여부 판정과 zip 풀기 — 내려받지 않고 임시 폴더의 파일 존재와 임시 폴더에서 만든 zip으로 본다.</summary>
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

    [Fact]
    public void ExtractZip_GithubSourceZip_StripsRootFolderIntoProbePath()
    {
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("런타임") });
        var node = ComponentCatalog.NodeWd14;
        var dest = installer.Resolve(node.Target);
        // GitHub 소스 zip은 '저장소-브랜치/' 한 겹 안에 들어 있다
        var zip = MakeZip(t.At("node-wd14.zip"),
            "ComfyUI-WD14-Tagger-main/",
            "ComfyUI-WD14-Tagger-main/wd14tagger.py",
            "ComfyUI-WD14-Tagger-main/js/",
            "ComfyUI-WD14-Tagger-main/js/wd14tagger.js",
            "ComfyUI-WD14-Tagger-main/models/",
            "ComfyUI-WD14-Tagger-main/sub/../pysssss.json");   // 대상 폴더 안에서 끝나는 '..'은 받는다
        RuntimeTestHelpers.Touch(Path.Combine(dest, "wd14tagger.py"));   // 지난 설치가 남긴 옛 파일

        ComponentInstaller.ExtractZip(zip, dest, node.StripRootFolder);

        // 한 겹을 벗겨 대상 폴더 바로 아래에 푼다 — 설치 판정(Probe)이 그 자리를 본다. 옛 파일은 덮어쓴다
        Assert.True(installer.IsInstalled(node));
        Assert.Equal("ComfyUI-WD14-Tagger-main/wd14tagger.py", File.ReadAllText(Path.Combine(dest, "wd14tagger.py")));
        Assert.Equal("ComfyUI-WD14-Tagger-main/js/wd14tagger.js", File.ReadAllText(Path.Combine(dest, "js", "wd14tagger.js")));
        Assert.True(File.Exists(Path.Combine(dest, "pysssss.json")));
        Assert.True(Directory.Exists(Path.Combine(dest, "models")));   // 빈 폴더 항목도 만든다
        Assert.False(Directory.Exists(Path.Combine(dest, "ComfyUI-WD14-Tagger-main")));
        Assert.False(Directory.Exists(Path.Combine(dest, "sub")));

        // 벗기지 않으면 zip 안의 경로 그대로 푼다
        var plain = t.At("그대로 풀기");
        ComponentInstaller.ExtractZip(zip, plain, stripRoot: false);
        Assert.True(File.Exists(Path.Combine(plain, "ComfyUI-WD14-Tagger-main", "wd14tagger.py")));
        Assert.True(File.Exists(Path.Combine(plain, "ComfyUI-WD14-Tagger-main", "js", "wd14tagger.js")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractZip_EntriesLeavingTarget_AreSkipped(bool stripRoot)
    {
        using var t = new TempDir();
        var dest = t.At("custom_nodes", "ComfyUI-WD14-Tagger");
        var top = stripRoot ? "ComfyUI-WD14-Tagger-main/" : "";
        // 빠져나가는 곳은 모두 임시 폴더 안이다 — 검사가 뚫려도 이 테스트가 임시 폴더 밖에 쓰지 않게
        var zip = MakeZip(t.At("악성.zip"),
            top + "wd14tagger.py",
            top + "../ComfyUI-WD14-Tagger-evil/x.py",                // 대상 폴더 이름으로 시작하는 형제 폴더 — 끝 구분자 없이 비교하면 통과하던 것
            top + "../ComfyUI-WD14-Tagger-evil/",                    // 같은 형제 폴더의 폴더 항목
            top + "../ComfyUI-WD14-Tagger.py",                       // 대상 폴더 이름으로 시작하는 형제 파일
            top + @"..\ComfyUI-WD14-Tagger-evil\y.py",               // 역슬래시로 적은 이름도 같다
            top + "../../escaped.py",                                // 상위 폴더
            top + t.At("밖", "절대 경로.py").Replace('\\', '/'),     // 절대 경로
            top + "./");                                             // 대상 폴더 자신의 폴더 항목은 받는다

        ComponentInstaller.ExtractZip(zip, dest, stripRoot);

        // 대상 폴더 밖에는 파일도 폴더도 생기지 않는다
        var made = Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(t.Root, p))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([
            "custom_nodes",
            Path.Combine("custom_nodes", "ComfyUI-WD14-Tagger"),
            Path.Combine("custom_nodes", "ComfyUI-WD14-Tagger", "wd14tagger.py"),
            "악성.zip",
        ], made);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractZip_FileEntryResolvingToTargetItself_IsSkippedAndRestExtracted(bool stripRoot)
    {
        using var t = new TempDir();
        var dest = t.At("custom_nodes", "ComfyUI-WD14-Tagger");
        var top = stripRoot ? "ComfyUI-WD14-Tagger-main/" : "";
        // 대상 폴더 자신으로 풀리는 '파일' 항목 — 받으면 ExtractToFile(대상 폴더)이 UnauthorizedAccessException을 던져
        // 그 구성요소 설치가 통째로 멈추고 뒤의 항목도 풀리지 않았다
        var zip = MakeZip(t.At("이상한.zip"),
            top + "wd14tagger.py",
            top + ".",
            top + "sub/..",
            top + "js/wd14tagger.js",
            top + "js/.",          // 이미 푼 하위 폴더로 풀리는 파일 항목도 같다
            top + "z.py");

        ComponentInstaller.ExtractZip(zip, dest, stripRoot);

        // 그 항목만 건너뛰고 나머지는 그대로 푼다 — 대상 폴더는 폴더로 남는다
        Assert.True(Directory.Exists(dest));
        Assert.Equal(top + "wd14tagger.py", File.ReadAllText(Path.Combine(dest, "wd14tagger.py")));
        Assert.Equal(top + "js/wd14tagger.js", File.ReadAllText(Path.Combine(dest, "js", "wd14tagger.js")));
        Assert.Equal(top + "z.py", File.ReadAllText(Path.Combine(dest, "z.py")));
        Assert.False(Directory.Exists(Path.Combine(dest, "sub")));
    }

    /// <summary>적은 이름 그대로 항목을 담은 zip을 만든다 — ZipArchive는 '../'·절대 경로 이름도 막지 않아 악성 zip을 흉내 낼 수 있다.
    /// '/'로 끝나는 이름은 폴더 항목, 나머지는 자기 이름을 내용으로 담은 파일.</summary>
    private static string MakeZip(string path, params string[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            var entry = zip.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var w = new StreamWriter(entry.Open());
            w.Write(name);
        }
        return path;
    }
}
