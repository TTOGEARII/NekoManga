using System.IO.Compression;
using System.Net;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>연결해 둔 ComfyUI에 빠진 구성요소를 깔 때 — 찾는 곳과 까는 곳이 같아야 한다.
/// 예전에는 연결된 폴더에서 찾고 RuntimeRoot에 깔아서, 받아도 다시 '필요'로 나오고 앱은 그 파일을 쓰지 않았다(체크포인트면 6.5GB를 헛받는다).</summary>
public class LinkedComfyInstallTests
{
    private sealed record Call(string Exe, string[] Args);

    private static (ComponentInstaller Installer, AppSettings Settings, List<string> Urls, List<Call> Calls) Make(TempDir t, byte[] body)
    {
        var s = new AppSettings { RuntimeRoot = t.At("런타임"), LinkedComfyRoot = t.At("내 ComfyUI") };
        var urls = new List<string>();
        var calls = new List<Call>();
        var installer = new ComponentInstaller(s)
        {
            RetryDelay = TimeSpan.Zero,
            Send = (req, _) =>
            {
                urls.Add(req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            },
            RunProcess = (exe, args, _) =>
            {
                calls.Add(new Call(exe, [.. args]));
                return Task.FromResult((0, args.Contains("list") ? "numpy==2.5.4\n" : ""));
            },
        };
        return (installer, s, urls, calls);
    }

    private static string RuntimeCopy(AppSettings s) => Path.Combine(s.RuntimeRoot, ComponentCatalog.ComfyDirName);

    [Fact]
    public void Resolve_Linked_ComfyPathsGoToTheLinkedFolder_OthersStayInRuntimeRoot()
    {
        using var t = new TempDir();
        var (installer, s, _, _) = Make(t, []);

        Assert.Equal(Path.Combine(s.LinkedComfyRoot, "ComfyUI", "models", "checkpoints", "a.safetensors"),
            installer.Resolve($"{ComponentCatalog.ComfyDirName}/ComfyUI/models/checkpoints/a.safetensors"));
        Assert.Equal(Path.GetFullPath(s.LinkedComfyRoot), installer.Resolve(ComponentCatalog.ComfyDirName));
        Assert.Equal(Path.Combine(s.RuntimeRoot, "models", "ctd.onnx"), installer.Resolve("models/ctd.onnx"));
        Assert.Equal(Path.Combine(s.RuntimeRoot, "tools", "7zr.exe"), installer.Resolve("tools/7zr.exe"));
        // 이름이 같은 글자로 시작할 뿐인 다른 폴더(AMD 묶음이 풀리는 이름)는 ComfyUI 폴더가 아니다
        Assert.Equal(Path.Combine(s.RuntimeRoot, ComponentCatalog.ComfyAmdDirName), installer.Resolve(ComponentCatalog.ComfyAmdDirName));
    }

    [Fact]
    public async Task Install_Linked_MissingModel_LandsInTheLinkedComfy()
    {
        using var t = new TempDir();
        byte[] body = [1, 2, 3, 4];
        var (installer, s, urls, calls) = Make(t, body);
        var c = ComponentCatalog.ModelControlnet;
        Assert.False(installer.IsInstalled(c));

        await installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None);

        Assert.Single(urls);
        Assert.Equal(body, File.ReadAllBytes(Path.Combine(s.LinkedComfyRoot, "ComfyUI", "models", "controlnet", new AppSettings().ColorControlNet)));
        Assert.True(installer.IsInstalled(c));          // 다시 열어도 '필요'로 나오지 않는다
        Assert.False(Directory.Exists(RuntimeCopy(s)));  // RuntimeRoot에 쓰이지 않는 사본을 만들지 않는다
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Install_Linked_MissingNode_IsExtractedThereAndGetsItsPackages()
    {
        using var t = new TempDir();
        var zipPath = t.At("node.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var name in new[] { "ComfyUI_LayerStyle-main/__init__.py", "ComfyUI_LayerStyle-main/requirements.txt" })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(name);
        var (installer, s, _, calls) = Make(t, File.ReadAllBytes(zipPath));
        var python = RuntimeTestHelpers.Touch(Path.Combine(s.LinkedComfyRoot, "python_embeded", "python.exe"));
        var c = ComponentCatalog.NodeLayerStyle;

        await installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None);

        var node = Path.Combine(s.LinkedComfyRoot, "ComfyUI", "custom_nodes", "ComfyUI_LayerStyle");
        Assert.True(File.Exists(Path.Combine(node, "__init__.py")));
        // 앱이 넣은 노드는 그 노드가 돌 패키지까지 넣는다 — 연결한 ComfyUI의 파이썬으로, 이미 깔린 것은 묶어 두고(-c)
        var install = calls.Single(call => call.Args.Contains("install"));
        Assert.Equal(python, install.Exe);
        Assert.Contains("-c", install.Args);
        Assert.Equal(Path.Combine(node, "requirements.txt"), install.Args[Array.IndexOf(install.Args, "-r") + 1]);
        Assert.True(installer.IsInstalled(c));
        Assert.False(Directory.Exists(RuntimeCopy(s)));
    }

    [Fact]
    public async Task Install_Linked_NodeAlreadyThere_IsLeftAlone()
    {
        // 사용자가 직접 넣어 쓰던 노드에는 손대지 않는다 — 패키지를 깔지도, 깔렸는지 따지지도 않는다
        using var t = new TempDir();
        var (installer, s, urls, calls) = Make(t, []);
        var c = ComponentCatalog.NodeLayerStyle;
        RuntimeTestHelpers.Touch(Path.Combine(s.LinkedComfyRoot, "ComfyUI", "custom_nodes", "ComfyUI_LayerStyle", "__init__.py"));

        Assert.True(installer.IsInstalled(c));
        Assert.Empty(installer.NeedsRepair());
        await installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None);   // 눌려도 아무것도 하지 않는다
        Assert.Empty(urls);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Install_Linked_ComfyItselfMissing_SaysTheLinkIsBroken_InsteadOfDownloadingAnotherCopy()
    {
        using var t = new TempDir();
        var (installer, s, urls, _) = Make(t, []);
        Assert.False(installer.IsInstalled(ComponentCatalog.ComfyPortable));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(ComponentCatalog.ComfyPortable, new Progress<InstallProgress>(), CancellationToken.None, "nvidia"));

        Assert.Contains(s.LinkedComfyRoot, ex.Message);
        Assert.Empty(urls);
        Assert.False(Directory.Exists(RuntimeCopy(s)));
    }
}
