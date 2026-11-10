using NekoManga.Core.Comfy;

namespace NekoManga.Core.Tests;

/// <summary>EngineManager에서 파일만 다루는 부분 — 모델 목록과 모델 들여오기 (임시 폴더의 가짜 엔진에서만. 프로세스·내려받기는 다루지 않는다).</summary>
public class EngineManagerTests
{
    [Fact]
    public void ListModels_ListsOnlyModelFilesByNameInOrder()
    {
        using var t = new TempDir();
        var s = new AppSettings { LinkedComfyRoot = t.At("엔진") };
        var dir = Directory.CreateDirectory(Path.Combine(s.ComfyRoot, "ComfyUI", "models", "checkpoints")).FullName;
        foreach (var name in new[] { "zeta.safetensors", "alpha.CKPT", "beta.SafeTensors", "readme.txt", "gamma.safetensors.part", "embedding.pt" })
            File.WriteAllText(Path.Combine(dir, name), "");

        // 드롭다운에는 모델 형식(.safetensors·.ckpt, 확장자 대소문자 무관)만 파일 이름으로, 이름순으로 — 받다 만 .part는 빠진다
        Assert.Equal(new[] { "alpha.CKPT", "beta.SafeTensors", "zeta.safetensors" }, EngineManager.ListModels(s, "checkpoints"));
        // 폴더가 아직 없으면 빈 목록 (엔진을 설치하기 전에도 설정 창이 열려야 한다)
        Assert.Empty(EngineManager.ListModels(s, "controlnet"));
    }

    [Fact]
    public void ImportModel_CopiesFileIntoModelsFolderAndReturnsItsName()
    {
        using var t = new TempDir();
        var s = new AppSettings { LinkedComfyRoot = t.At("엔진 폴더") };
        Directory.CreateDirectory(t.At("내 모델"));
        var source = t.At("내 모델", "アニメ 線画 v2.safetensors");
        File.WriteAllBytes(source, [1, 2, 3]);

        var name = EngineManager.ImportModel(s, "controlnet", source);

        // 엔진의 models/<종류> 폴더(없으면 만든다)로 복사하고 파일 이름을 돌려준다 — 원본은 그대로 남는다
        Assert.Equal("アニメ 線画 v2.safetensors", name);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(s.ComfyRoot, "ComfyUI", "models", "controlnet", name)));
        Assert.True(File.Exists(source));
        Assert.Contains(name, EngineManager.ListModels(s, "controlnet"));
        // 같은 파일을 다시 들여와도 실패하지 않는다
        Assert.Equal(name, EngineManager.ImportModel(s, "controlnet", source));
    }
}
