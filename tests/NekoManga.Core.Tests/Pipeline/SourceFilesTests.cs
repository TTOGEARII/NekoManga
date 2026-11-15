using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>폴더를 끌어다 놓았을 때 원고로 받을 파일(PipelineRunner.EnumerateSourceFiles) — 하위 폴더까지 받되 앱이 만든 폴더(결과 폴더·사이드카 폴더)는 뺀다.
/// 결과 폴더는 원고 폴더 **안**에 생긴다(vol1\vol1_neko). 확장자만 보고 받던 때는 한 번 돌린 폴더를 다시 끌어다 놓으면 결과물과 .neko의 마스크·검열 제거 전 원고 그림까지
/// 원고 행으로 들어왔다 — 그대로 [시작]하면 원래 원고는 '결과 파일 이름이 목록의 다른 원고와 같아요'로 실패하고 결과물이 원고로 돌았다(2026-11-13 점검 6번).</summary>
public class SourceFilesTests
{
    /// <summary>한 번 돌린 화 폴더 — 원고 둘, 그 결과물, 사이드카 그림들.</summary>
    private static (string Dir, string[] Sources) ProcessedVolume(TempDir t, string name, AppSettings s)
    {
        var a = WriteImageFile(t.At(name, "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At(name, "02.png"), seed: 2);
        foreach (var src in new[] { a, b })
        {
            var outPath = PipelineRunner.OutputPathFor(s, src);
            WriteImageFile(outPath, seed: 9);
            WriteImageFile(MaskSidecar.MaskPathFor(outPath), seed: 3);
            WriteImageFile(MaskSidecar.PrePathFor(outPath), seed: 4);
            WriteImageFile(LetterSidecar.BasePathFor(outPath), seed: 5);
        }
        return (t.At(name), [a, b]);
    }

    private static string[] Sorted(IEnumerable<string> files) => [.. files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void ProcessedFolderDroppedAgain_ReturnsOnlyTheSources()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (dir, sources) = ProcessedVolume(t, "vol1", s);
        var skipped = new List<string>();

        var files = PipelineRunner.EnumerateSourceFiles(dir, s, skipped);

        Assert.Equal(Sorted(sources), Sorted(files));
        Assert.Equal([t.At("vol1", "vol1_neko")], skipped);   // 무엇을 건너뛰었는지 알려 줄 수 있게
    }

    [Fact]
    public void ParentOfSeveralVolumes_SkipsEachVolumesOwnResultFolder()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (_, v1) = ProcessedVolume(t, Path.Combine("책", "1화"), s);
        var (_, v2) = ProcessedVolume(t, Path.Combine("책", UnicodeName), s);
        var loose = WriteImageFile(t.At("책", "표지.webp"), seed: 6);

        var files = PipelineRunner.EnumerateSourceFiles(t.At("책"), s);

        Assert.Equal(Sorted([.. v1, .. v2, loose]), Sorted(files));
    }

    [Fact]
    public void ResultFolderItselfDropped_TakesTheResultsButNeverTheSidecarImages()
    {
        // 결과 폴더를 직접 끌어다 놓는 것은 사용자의 선택이다(지운 결과물을 채색만 다시 하는 식) — 그래도 .neko의 마스크·원고 사본은 원고가 아니다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (dir, sources) = ProcessedVolume(t, "vol1", s);
        var resultDir = Path.Combine(dir, "vol1_neko");

        var files = PipelineRunner.EnumerateSourceFiles(resultDir, s);

        Assert.Equal(Sorted(sources.Select(src => PipelineRunner.OutputPathFor(s, src))), Sorted(files));
    }

    [Fact]
    public void FoldersThatOnlyLookLikeResultFolders_AreKept()
    {
        // 건너뛰는 것은 '<그 폴더 이름>_neko' 하나뿐이다 — 이름이 _neko로 끝난다고 다 결과 폴더는 아니다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var a = WriteImageFile(t.At("vol1", "다른화_neko", "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At("vol1", "vol1_neko2", "01.jpg"), seed: 2);
        var c = WriteImageFile(t.At("vol1", "sub", "vol1_neko", "01.jpg"), seed: 3);   // 상위 이름은 sub다

        Assert.Equal(Sorted([a, b, c]), Sorted(PipelineRunner.EnumerateSourceFiles(t.At("vol1"), s)));
    }

    /// <summary>고정한 출력 폴더에 그 원고의 결과물을 러너처럼 남긴다 — 결과물 png와 주인 기록(.neko\&lt;이름&gt;.owner).</summary>
    private static string ResultInFixedFolder(AppSettings s, string src)
    {
        var outPath = PipelineRunner.OutputPathFor(s, src);
        WriteImageFile(outPath, seed: 9);
        Assert.Null(PipelineRunner.RecordResultOwner(outPath, src));
        return outPath;
    }

    [Fact]
    public void FixedOutputFolderInsideTheDroppedFolder_ItsResultsAreSkipped()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t, outputRoot: t.At("작업", "결과 모음"));
        var src = WriteImageFile(t.At("작업", "vol1", "01.jpg"), seed: 1);
        ResultInFixedFolder(s, src);
        var skipped = new List<string>();

        var files = PipelineRunner.EnumerateSourceFiles(t.At("작업"), s, skipped);

        Assert.Equal([src], files);
        Assert.Equal([t.At("작업", "결과 모음")], skipped);
    }

    [Fact]
    public void FixedOutputFolderThatAlsoHoldsSources_KeepsTheSourcesAndSkipsOnlyResults()
    {
        // 출력 폴더를 원고 폴더들의 상위(또는 원고 폴더 자신)로 고정한 배치 — 폴더째 건너뛰면 원고까지 통째로 빠진다(2026-11-14 검수).
        // 결과물은 .neko에 그 이름의 기록이 있는 파일이다. 끌어다 놓은 폴더가 출력 폴더 자신이어도 같다
        using var t = new TempDir(); var s = EnginelessSettings(t, outputRoot: t.At("만화"));
        var a = WriteImageFile(t.At("만화", "vol1", "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At("만화", "02.png"), seed: 2);           // 출력 폴더 바로 아래의 원고
        var extra = WriteImageFile(t.At("만화", "표지.webp"), seed: 3);    // 기록이 없는 파일은 원고다
        var outA = ResultInFixedFolder(s, a); var outB = ResultInFixedFolder(s, b);
        Assert.Equal(t.At("만화"), Path.GetDirectoryName(outA));           // 전제: 결과물이 원고와 같은 폴더에 섞여 있다

        foreach (var dropped in new[] { t.At("만화"), t.Root })
        {
            var files = PipelineRunner.EnumerateSourceFiles(dropped, s);
            Assert.Equal(Sorted([a, b, extra]), Sorted(files.Where(f => f.StartsWith(t.At("만화"), StringComparison.OrdinalIgnoreCase))));
            Assert.DoesNotContain(outA, files); Assert.DoesNotContain(outB, files);
        }
    }

    [Fact]
    public void OtherFilesAreReturnedToo_TheCallerTellsImagesFromTheRest()
    {
        // 그림이 아닌 파일·읽지 못하는 그림(.avif)은 받는 쪽이 가려서 알린다(ImgIo.UnreadableNotice) — 여기서 말없이 빼면 왜 안 들어왔는지 모른다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var img = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var avif = WriteBytesFile(t.At("vol1", "02.avif"), 16);
        var txt = WriteBytesFile(t.At("vol1", "메모.txt"), 16);

        Assert.Equal(Sorted([img, avif, txt]), Sorted(PipelineRunner.EnumerateSourceFiles(t.At("vol1"), s)));
    }

    [Fact]
    public void MissingFolder_ReturnsNothing()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        Assert.Empty(PipelineRunner.EnumerateSourceFiles(t.At("없는 폴더"), s));
    }
}
