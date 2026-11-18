using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>커스텀 노드의 파이썬 패키지 설치 — pip는 돌리지 않고 가짜 실행기로 넘긴 인자와 표식 파일만 본다.
/// (v1.13.0까지는 노드 zip만 풀어 새 PC에서 cv2·onnxruntime이 없어 LayerStyle·controlnet_aux·WD14 노드가 로드되지 않았다)</summary>
public class NodeDepsTests
{
    private static readonly Component[] NodesWithDeps =
        [ComponentCatalog.NodeWd14, ComponentCatalog.NodeLayerStyle, ComponentCatalog.NodeControlnetAux];

    private sealed record Call(string Exe, string[] Args);

    private static (ComponentInstaller Installer, List<Call> Calls) Make(TempDir t, Func<Call, (int, string)>? reply = null, string linked = "")
    {
        var calls = new List<Call>();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("런타임 rt"), LinkedComfyRoot = linked })
        {
            RunProcess = (exe, args, _) =>
            {
                var call = new Call(exe, [.. args]);
                calls.Add(call);
                return Task.FromResult(reply?.Invoke(call) ?? (0, call.Args.Contains("list") ? "numpy==2.5.4\ntorch==2.14.1+cu130\nscipy @ file:///D:/a/scipy.whl\n" : ""));
            },
        };
        return (installer, calls);
    }

    private static void PutNodeFiles(ComponentInstaller installer, Component c)
    {
        RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));
        RuntimeTestHelpers.Touch(installer.Resolve(c.Requirements!));
        RuntimeTestHelpers.Touch(installer.Resolve($"{ComponentCatalog.ComfyDirName}/python_embeded/python.exe"));
    }

    [Fact]
    public void Catalog_NodesThatShipRequirements_DeclareThemInsideTheirFolder()
    {
        foreach (var c in NodesWithDeps)
        {
            Assert.NotNull(c.Requirements);
            Assert.StartsWith(c.Target + "/", c.Requirements);
            Assert.EndsWith("requirements.txt", c.Requirements);
        }
        // requirements.txt가 비어 있는 노드에는 걸지 않는다 — pip를 헛돌리지 않는다
        Assert.Null(ComponentCatalog.NodeIpAdapter.Requirements);
        Assert.Null(ComponentCatalog.NodeCustomScripts.Requirements);
    }

    [Fact]
    public void IsInstalled_NodeFilesWithoutDeps_IsNotInstalledAndNeedsRepair()
    {
        using var t = new TempDir();
        var (installer, _) = Make(t);
        foreach (var c in NodesWithDeps)
        {
            Assert.DoesNotContain(c, installer.NeedsRepair());   // 아직 아무것도 없으면 고칠 것이 아니라 새로 받을 것
            PutNodeFiles(installer, c);
            Assert.False(installer.IsInstalled(c));
            Assert.Contains(c, installer.NeedsRepair());
            RuntimeTestHelpers.Touch(installer.DepsMarker(c));
            Assert.True(installer.IsInstalled(c));
            Assert.DoesNotContain(c, installer.NeedsRepair());
        }
    }

    [Fact]
    public void IsInstalled_LinkedComfy_DoesNotAskForDeps()
    {
        // 연결된 ComfyUI의 파이썬은 사용자 것이다 — 앱이 패키지를 깔지 않으므로 표식도 요구하지 않는다
        using var t = new TempDir();
        var linked = t.At("내 ComfyUI");
        var (installer, _) = Make(t, linked: linked);
        RuntimeTestHelpers.Touch(Path.Combine(linked, "ComfyUI", "custom_nodes", "ComfyUI_LayerStyle", "__init__.py"));
        Assert.True(installer.IsInstalled(ComponentCatalog.NodeLayerStyle));
        Assert.Empty(installer.NeedsRepair());
    }

    [Fact]
    public async Task InstallAsync_NodeFilesAlreadyThere_OnlyInstallsDepsWithEmbeddedPython()
    {
        using var t = new TempDir();
        string[] constraints = [];
        var (installer, calls) = Make(t, call =>
        {
            if (!call.Args.Contains("install")) return (0, "numpy==2.5.4\r\ntorch==2.14.1+cu130\r\nscipy @ file:///D:/a/scipy.whl\r\nWARNING: something\r\n");
            constraints = File.ReadAllLines(call.Args[Array.IndexOf(call.Args, "-c") + 1]);   // 설치가 끝나면 지워지므로 pip가 읽는 때에 본다
            return (0, "");
        });
        var c = ComponentCatalog.NodeLayerStyle;
        PutNodeFiles(installer, c);

        // 내려받지 않는다 — 주소가 죽어 있어도 끝나야 한다
        await installer.InstallAsync(c with { Url = "https://127.0.0.1:1/none.zip" }, new Progress<InstallProgress>(), CancellationToken.None);

        var python = installer.Resolve($"{ComponentCatalog.ComfyDirName}/python_embeded/python.exe");
        Assert.Equal(3, calls.Count);   // 목록 → 설치 → 목록(opencv 확인)
        Assert.All(calls, call => Assert.Equal(python, call.Exe));
        Assert.All(calls, call => Assert.Equal(["-s", "-m", "pip"], call.Args[..3]));   // -s: 사용자 site-packages를 섞지 않는다
        Assert.Contains("list", calls[0].Args);

        var install = calls[1].Args;
        Assert.Contains("install", install);
        Assert.Equal(installer.Resolve(c.Requirements!), install[Array.IndexOf(install, "-r") + 1]);
        // 이미 깔린 패키지(torch·numpy)는 버전을 묶어 둔다 — 노드의 요구가 ComfyUI의 torch를 바꾸지 못하게
        Assert.Equal(["numpy==2.5.4", "torch==2.14.1+cu130"], constraints);

        Assert.True(File.Exists(installer.DepsMarker(c)));
        Assert.True(installer.IsInstalled(c));
    }

    [Theory]
    [InlineData("opencv-python==5.1.0.95\nopencv-contrib-python==5.1.0.95\n", true)]
    [InlineData("opencv-python-headless==5.1.0.95\nopencv-contrib-python==5.1.0.95\n", true)]
    [InlineData("opencv-contrib-python==5.1.0.95\n", false)]   // contrib만 있으면 덮일 일이 없다
    [InlineData("opencv-python==5.1.0.95\n", false)]
    public async Task InstallAsync_BothOpenCvPackages_ReinstallsContribLast(string afterInstall, bool reinstall)
    {
        // 두 패키지는 같은 cv2 폴더를 쓴다 — opencv-python이 나중에 깔리면 contrib의 cv2.ximgproc이 사라져 LayerStyle이 로드되지 않는다
        // (2026-11-18 실측: WD14 → LayerStyle(contrib) → controlnet_aux(opencv-python) 순서로 깔자 LayerStyle이 IMPORT FAILED)
        using var t = new TempDir();
        int lists = 0;
        var (installer, calls) = Make(t, call => call.Args.Contains("list") ? (0, lists++ == 0 ? "numpy==2.5.4\n" : afterInstall) : (0, ""));
        var c = ComponentCatalog.NodeControlnetAux;
        PutNodeFiles(installer, c);

        await installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None);

        var forced = calls.Where(call => call.Args.Contains("--force-reinstall")).ToList();
        if (!reinstall) { Assert.Empty(forced); }
        else
        {
            var args = Assert.Single(forced).Args;
            Assert.Contains("--no-deps", args);   // 딸린 패키지(numpy)는 건드리지 않는다
            Assert.Equal("opencv-contrib-python==5.1.0.95", args[^1]);
            Assert.Same(forced[0], calls[^1]);
        }
        Assert.True(installer.IsInstalled(c));
    }

    [Fact]
    public async Task InstallAsync_SelfTrainWeights_AlsoInstallsUltralytics()
    {
        // 자가 학습은 ComfyUI의 파이썬으로 ultralytics를 돌린다 — 새로 받은 포터블에는 없어서 학습이 import에서 죽었다
        using var t = new TempDir();
        var (installer, calls) = Make(t);
        var c = ComponentCatalog.ModelCensorPt;
        Assert.Contains("ultralytics", c.PipPackages!);
        RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));
        RuntimeTestHelpers.Touch(installer.Resolve($"{ComponentCatalog.ComfyDirName}/python_embeded/python.exe"));
        Assert.False(installer.IsInstalled(c));
        Assert.Contains(c, installer.NeedsRepair());

        // 가중치는 이미 있으니 내려받지 않는다 — 주소가 죽어 있어도 끝나야 한다
        await installer.InstallAsync(c with { Url = "https://127.0.0.1:1/none.pt" }, new Progress<InstallProgress>(), CancellationToken.None);

        var install = calls.Single(call => call.Args.Contains("install")).Args;
        Assert.DoesNotContain("-r", install);
        Assert.Equal(c.PipPackages, install[^c.PipPackages!.Length..]);
        Assert.Contains("-c", install);
        Assert.Equal(installer.Resolve(c.Target) + ".neko-deps", installer.DepsMarker(c));   // 파일 구성요소의 표식은 그 파일 옆
        Assert.True(installer.IsInstalled(c));
        Assert.Equal(0, new FileInfo(installer.Resolve(c.Probe)).Length);   // 있던 파일은 그대로
    }

    [Fact]
    public async Task InstallAsync_PipFails_ThrowsWithPipOutputAndLeavesNoMarker()
    {
        using var t = new TempDir();
        var (installer, _) = Make(t, call => call.Args.Contains("install") ? (1, "ERROR: No matching distribution found for khatt") : (0, "numpy==2.5.4"));
        var c = ComponentCatalog.NodeLayerStyle;
        PutNodeFiles(installer, c);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None));

        Assert.Contains("khatt", ex.Message);
        Assert.False(File.Exists(installer.DepsMarker(c)));
        Assert.False(installer.IsInstalled(c));   // 다음 설치가 다시 시도한다
    }

    [Fact]
    public async Task InstallAsync_NoEmbeddedPython_ThrowsInsteadOfMarkingInstalled()
    {
        using var t = new TempDir();
        var (installer, calls) = Make(t);
        var c = ComponentCatalog.NodeWd14;
        RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));
        RuntimeTestHelpers.Touch(installer.Resolve(c.Requirements!));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None));
        Assert.Empty(calls);
        Assert.False(File.Exists(installer.DepsMarker(c)));
    }
}
