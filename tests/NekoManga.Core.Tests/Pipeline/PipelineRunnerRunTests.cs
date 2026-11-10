using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>번역·검열 제거·채색을 모두 끈 러너로 결과 쓰기 규칙을 끝까지 돌려 본다.
/// 엔진이 꺼져 있으면 모델·ComfyUI·작업 폴더 없이 원고마다 '원본 그대로' 복사본을 내보내는데, 결과 자리 충돌 검사·옛 이름 옮기기·
/// 다른 형태 치우기는 그대로 탄다.</summary>
public class PipelineRunnerRunTests
{
    private static async Task<ProgressLog> RunAsync(AppSettings s, params string[] files)
    {
        var log = new ProgressLog();
        using var runner = new PipelineRunner(s);
        await runner.RunAsync(files, log, CancellationToken.None);
        return log;
    }

    // ── 결과가 서로·원고를 덮지 않는다 ──

    [Fact]
    public async Task RunAsync_SameNameJpgPngWebpInOneFolder_EachGetsItsOwnUntouchedCopy()
    {
        // 결과 이름에 원고 확장자를 남기는 이유 — 같은 폴더의 01.jpg·01.png·01.webp가 한 결과 자리를 두고 서로 덮으면 안 된다
        using var t = new TempDir();
        var vol = t.At("1화 テスト");
        var jpg = WriteImageFile(Path.Combine(vol, "01.jpg"), seed: 1);
        var png = WriteImageFile(Path.Combine(vol, "01.png"), seed: 2);
        var webp = WriteImageFile(Path.Combine(vol, "01.webp"), seed: 3);
        var s = EnginelessSettings(t);

        var log = await RunAsync(s, jpg, png, webp);

        Assert.Equal(["01.png", "01_jpg.jpg", "01_webp.webp"], FilesUnder(Path.Combine(vol, "1화 テスト_neko")));
        foreach (var f in new[] { jpg, png, webp })
        {
            Assert.Null(log.FinalOf(f).Error);
            var shown = PipelineRunner.ResolveResultPath(PipelineRunner.OutputPathFor(s, f), f);
            Assert.NotEqual(f, shown);                                      // 결과 폴더의 자기 결과를 보여 주고
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(shown));   // 손댈 게 없었으니 원본 바이트 그대로
        }
    }

    [Fact]
    public async Task RunAsync_FixedOutputRootWithSameFolderAndFileNames_FailsLaterPageInsteadOfOverwriting()
    {
        // 출력 폴더를 고정하면 이름 앞에 상위 폴더 이름을 붙이지만 A\ch1\001.jpg·B\ch1\001.jpg는 그래도 같은 자리다 — 뒤 장은 덮지 말고 실패로 알린다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "ch1", "001.jpg"), seed: 1);
        var b = WriteImageFile(t.At("B", "ch1", "001.jpg"), seed: 2);
        var outDir = t.At("결과");

        var log = await RunAsync(EnginelessSettings(t, outDir), a, b);

        Assert.Null(log.FinalOf(a).Error);
        Assert.NotNull(log.FinalOf(b).Error);
        Assert.Equal(["ch1_001_jpg.jpg"], FilesUnder(outDir));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(Path.Combine(outDir, "ch1_001_jpg.jpg")));
    }

    [Theory]
    [InlineData("01_jpg.jpg")]   // src의 '원본 그대로' 자리 — 복사가 덮어쓸 판이다
    [InlineData("01_jpg.png")]   // src의 결과물 자리 — 복사본을 쓰고 나면 '다른 형태'로 지워질 판이다
    public async Task RunAsync_ResultSlotIsAnotherListedSource_FailsThatPageAndKeepsTheFile(string slotName)
    {
        // 전에 만든 결과물을 원고로 다시 올린 경우 — 그 파일이 이번 장의 결과 자리면 덮거나 지우지 않고 이번 장을 실패로 둔다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var listed = WriteImageFile(t.At("vol1", "vol1_neko", slotName), seed: 9);
        var before = File.ReadAllBytes(listed);

        var log = await RunAsync(EnginelessSettings(t), src, listed);

        Assert.NotNull(log.FinalOf(src).Error);
        Assert.Null(log.FinalOf(listed).Error);   // 다시 올린 쪽은 자기 결과 폴더로 정상 처리
        Assert.Equal(before, File.ReadAllBytes(listed));
    }

    [Fact]
    public async Task RunAsync_OutputRootIsSourceFolder_LeavesFolderExactlyAsItWas()
    {
        // 출력 폴더가 원고 폴더면 '원본 그대로'는 원본 자신이다 — 손댈 게 없는 장은 아무것도 쓰거나 옮기거나 지우지 않는다
        // (옛 이름 옮기기가 보는 '<이름>.png'가 바로 옆의 png 원고라 특히 조심해야 한다)
        using var t = new TempDir();
        var vol = t.At("vol1");
        var jpg = WriteImageFile(Path.Combine(vol, "01.jpg"), seed: 1);
        var png = WriteImageFile(Path.Combine(vol, "01.png"), seed: 2);
        var names = FilesUnder(vol);
        var bytes = names.Select(n => File.ReadAllBytes(Path.Combine(vol, n))).ToArray();

        var log = await RunAsync(EnginelessSettings(t, vol), jpg, png);

        Assert.Null(log.FinalOf(jpg).Error);
        Assert.Null(log.FinalOf(png).Error);
        Assert.Equal(names, FilesUnder(vol));
        Assert.Equal(bytes, names.Select(n => File.ReadAllBytes(Path.Combine(vol, n))).ToArray());
    }

    [Fact]
    public async Task RunAsync_PngResultLeftFromEarlierRun_IsReplacedByUntouchedCopy()
    {
        // 한 원고의 결과는 png와 '원본 그대로' 중 하나만 남는다 — 지난번 png가 남아 있으면 보기 규칙이 헷갈린다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t);
        WriteImageFile(PipelineRunner.OutputPathFor(s, src), seed: 7);

        var log = await RunAsync(s, src);

        Assert.Null(log.FinalOf(src).Error);
        Assert.Equal(["01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(t.At("vol1", "vol1_neko", "01_jpg.jpg")));
    }

    // ── 옛 이름(v1.5.5까지)의 결과·사이드카 ──

    [Fact]
    public async Task RunAsync_LegacyMaskSidecarUnderSharedOldName_MovesOnlyToItsOwnSource()
    {
        // 출력 폴더를 고정한 옛 결과는 1화·2화의 001.jpg가 같은 이름(001) 밑에 사이드카를 남겼다 — 새 이름으로 옮길 때
        // 이름만 같은 다른 화가 가져가면 남의 마스크로 감지·검토를 건너뛴다. 바이트 길이까지 같은 원고만 넘겨받는다
        using var t = new TempDir();
        var ch1 = WriteImageFile(t.At("1화", "001.jpg"), seed: 1, width: 60, height: 40);
        var ch2 = WriteImageFile(t.At("2화", "001.jpg"), seed: 2, width: 90, height: 70);
        Assert.NotEqual(new FileInfo(ch1).Length, new FileInfo(ch2).Length);
        var outDir = t.At("결과");
        var s = EnginelessSettings(t, outDir);
        var legacyOut = Path.Combine(outDir, "001.png");
        using var mask = RectMask(90, 70, new Rect(10, 10, 30, 20));
        MaskSidecar.Write(legacyOut, ch2, mask, null, null, "2화 마스크", skip: false, edited: true, confirmed: true);

        await RunAsync(s, ch1);

        Assert.Null(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, ch1)));
        Assert.NotNull(MaskSidecar.Load(legacyOut));

        await RunAsync(s, ch2);

        var moved = MaskSidecar.Load(PipelineRunner.OutputPathFor(s, ch2));
        Assert.NotNull(moved);
        Assert.Equal(ch2, moved.Source);
        Assert.Equal("2화 마스크", moved.Summary);
        Assert.Null(MaskSidecar.Load(legacyOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_LegacyPngResult_IsTakenOverOnlyWhenOwnerIsCertain(bool pngSourceInSameFolder)
    {
        // 옛 규칙에서 01.jpg의 결과는 기본 출력 폴더의 01.png였다. 원고 폴더의 01.*가 01.jpg 하나뿐이면 그 결과의 주인이 확실해 넘겨받고
        // (이번 실행은 손댈 게 없어 '원본 그대로'로 바뀌며 치워진다), 01.png 원고도 있으면 주인을 모르므로 건드리지 않는다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        if (pngSourceInSameFolder) WriteImageFile(t.At("vol1", "01.png"), seed: 2);
        var legacy = WriteImageFile(t.At("vol1", "vol1_neko", "01.png"), seed: 7);
        var legacyBytes = File.ReadAllBytes(legacy);

        var log = await RunAsync(EnginelessSettings(t), jpg);

        Assert.Null(log.FinalOf(jpg).Error);
        if (pngSourceInSameFolder)
        {
            Assert.Equal(["01.png", "01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
            Assert.Equal(legacyBytes, File.ReadAllBytes(legacy));
        }
        else Assert.Equal(["01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
    }

    // ── 결과 창의 '채색부터 다시' ──

    [Fact]
    public async Task RedoPageAsync_StoredMaskBelongsToAnotherSource_RefusesAndWritesNothing()
    {
        // 결과 자리가 같은 다른 원고(같은 출력 폴더의 A\ch1\001.jpg·B\ch1\001.jpg)의 마스크로 다시 돌리면 남의 그림이 이 장의 결과가 된다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "ch1", "001.jpg"), seed: 1, width: 60, height: 40);
        var b = WriteImageFile(t.At("B", "ch1", "001.jpg"), seed: 2, width: 90, height: 70);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Equal(outPath, PipelineRunner.OutputPathFor(s, b));   // 전제: 둘이 같은 결과 자리
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(60, 40, seed: 3);
        MaskSidecar.Write(outPath, a, mask, null, pre, "A의 마스크", skip: false, edited: false, confirmed: false);
        var before = FilesUnder(t.Root);

        using (var runner = new PipelineRunner(s))
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RedoPageAsync(b, new ProgressLog(), CancellationToken.None));

        Assert.Equal(before, FilesUnder(t.Root));
        Assert.False(MaskSidecar.Load(outPath)!.Confirmed);
    }

    [Fact]
    public async Task RedoPageAsync_GivenUntouchedCopyPath_WritesPngResultAndMarksMaskConfirmed()
    {
        // 결과 창은 보던 파일('원본 그대로' 복사본 jpg)을 결과 경로로 넘기기도 한다 — 다시 돌린 결과는 늘 같은 이름의 png이고 사이드카도 같은 것을 쓴다.
        // 성공하면 마스크를 '확인됨'으로 남겨 다음 실행이 검토를 다시 기다리지 않게 하고, 옛 복사본은 치운다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        var copy = PipelineRunner.AsIsPathFor(outPath, src);
        ImgIo.CopyAtomic(src, copy);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(60, 40, seed: 4);
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false);
        var log = new ProgressLog();

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, log, CancellationToken.None, outPath: copy);

        Assert.Null(log.FinalOf(src).Error);
        Assert.False(File.Exists(copy));
        Assert.Equal(outPath, PipelineRunner.ResolveResultPath(outPath, src));
        using var result = ImgIo.Read(outPath);
        AssertSamePixels(pre, result);   // 엔진이 모두 꺼져 있으니 검열 제거 전 원고가 그대로 결과가 된다
        Assert.True(MaskSidecar.Load(outPath)!.Confirmed);
    }
}
