using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>결과 주인 기록(<c>.neko\&lt;이름&gt;.owner</c>) — 사이드카가 하나도 없는 결과도 같은 결과 자리로 떨어진 다른 원고에 덮이지 않는지.
/// 엔진을 모두 끈 러너의 결과는 '원본 그대로' 복사본뿐이고 마스크·식자 사이드카가 남지 않는다 — 검토 없이 돌린 글자 없는 장과 같은, 지금까지 보호받지 못하던 경우다
/// (v1.8.2: 출력 폴더를 고정하고 A\vol1\01.png, B\vol1\01.png를 따로 돌리면 둘 다 성공하고 결과가 B의 것으로 바뀌었다).</summary>
public class PipelineRunnerResultOwnerTests
{
    private static async Task<ProgressLog> RunAsync(AppSettings s, params string[] files)
    {
        var log = new ProgressLog();
        using var runner = RuleRunner(s);
        await runner.RunAsync(files, log, CancellationToken.None);
        return log;
    }

    /// <summary>출력 폴더를 고정하면 같은 결과 자리로 떨어지는 두 원고 A\vol1\01.jpg·B\vol1\01.jpg(이름·해상도는 같고 내용·바이트 길이가 다르다). 결과 자리는 비어 있다.</summary>
    private static (string A, string B, AppSettings S, string OutPath) SharedSlot(TempDir t)
    {
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At("B", "vol1", "01.jpg"), seed: 2);
        Assert.NotEqual(new FileInfo(a).Length, new FileInfo(b).Length);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Equal(outPath, PipelineRunner.OutputPathFor(s, b));
        return (a, b, s, outPath);
    }

    /// <summary>A를 먼저 따로 돌려 결과 자리를 차지하게 한다 — 남는 것은 '원본 그대로' 복사본과 주인 기록뿐이다.</summary>
    private static async Task<(string A, string B, AppSettings S, string OutPath)> SlotTakenByAAsync(TempDir t)
    {
        var slot = SharedSlot(t);
        Assert.Null((await RunAsync(slot.S, slot.A)).FinalOf(slot.A).Error);
        Assert.False(File.Exists(MaskSidecar.JsonPathFor(slot.OutPath)));     // 전제: 주인을 알려 줄 사이드카가 하나도 없다
        Assert.False(File.Exists(LetterSidecar.JsonPathFor(slot.OutPath)));
        Assert.True(File.Exists(ResultOwner.PathFor(slot.OutPath)));
        return slot;
    }

    private static (string[] Names, byte[][] Bytes) Snapshot(string root)
    {
        var names = FilesUnder(root);
        return (names, names.Select(n => File.ReadAllBytes(Path.Combine(root, n))).ToArray());
    }

    private static void AssertUnchanged((string[] Names, byte[][] Bytes) before, string root)
    {
        var now = Snapshot(root);
        Assert.Equal(before.Names, now.Names);
        Assert.Equal(before.Bytes, now.Bytes);
    }

    private const int BarW = 400, BarH = 300;

    /// <summary>검열 제거를 켠 페이지별 검토 흐름(규칙 감지기(시험 이음새 DetectorOverride) — 모델 없이 돈다) — 1차와 2차 사이에 끼어들어야 하는 시험용. 복원기까지 가지 않게 쓴다.</summary>
    private static AppSettings BarPageSettings(TempDir t, string outputRoot)
    {
        var s = new AppSettings
        {
            EnableTranslate = false, EnableDecensor = true, EnableAutoColor = false,
            DecensorMode = "Basic", DecensorTarget = "BlackBar",
            DecensorReview = true, DecensorReviewMode = "PerPage", ReuseStoredMasks = true,
            CollectCorrections = false, OnnxGpu = "Off",
            RuntimeRoot = t.At("runtime"), OutputRoot = outputRoot,
        };
        WriteBytesFile(Path.Combine(s.RuntimeRoot, "models", LamaInpainter.ActiveModelFile), 64);
        return s;
    }

    /// <summary>모델 없이 돌리는 러너 — 감지를 규칙 감지기(검은 막대)로 바꿔 끼운다. '이전 방식' 엔진(규칙 감지기 경로)이 2026-11-12 삭제돼
    /// Aletheia 모델 없이는 감지가 안 되므로 시험 이음새 DetectorOverride를 쓴다.</summary>
    private static PipelineRunner RuleRunner(AppSettings s) =>
        new(s) { DetectorOverride = (img, exclude) => MosaicDetector.BuildMaskForTarget(s, img, exclude, out _) };

    /// <summary>밝은 바탕에 검은 막대 하나가 있는 원고 — 규칙 감지기가 그 막대를 찾아 '검토 대기'에 들어간다.</summary>
    private static string WriteBarPage(string path, int x)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new Mat(BarH, BarW, MatType.CV_8UC3, new Scalar(235, 235, 235));
        img.Rectangle(new Rect(x, 130, 160, 40), Scalar.Black, -1);
        Assert.True(ImgIo.Write(path, img));
        return path;
    }

    // ── 따로 돌린 다른 원고가 사이드카 없는 결과를 덮지 않는다 ──

    [Fact]
    public async Task RunAsync_SeparateRunsOntoOneSlotWithoutSidecars_LaterSourceFailsAndEarlierResultStays()
    {
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);
        Assert.Equal([Path.Combine(".neko", "vol1_01_jpg.owner"), "vol1_01_jpg.jpg"], before.Names);

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);                             // 누구의 결과인지
        Assert.Contains("vol1_01_jpg.jpg", error);             // 덮일 뻔한 파일(그 원고의 '원본 그대로' 복사본)
        Assert.Contains("출력 폴더를 바꾸거나 비워", error);   // 출력 폴더를 고정해 생긴 충돌이다
        AssertUnchanged(before, outDir);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(Path.Combine(outDir, "vol1_01_jpg.jpg")));

        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);   // 주인은 제 자리를 그대로 다시 돌린다
    }

    [Theory]
    [InlineData("sameSource")]     // 같은 원고를 다시 돌린다
    [InlineData("copiedFolder")]   // 원고 폴더를 복사해 와서 돌린다 — 원래 폴더가 남아 있어도 이름+바이트 길이+내용이 같으면 같은 원고. 주인은 원래 원고 그대로다
    [InlineData("movedFolder")]    // 원고 폴더를 옮겨 와서 돌린다 — 옛 경로에 원고가 없으니 기록이 새 경로를 따라온다
    public async Task RunAsync_SameSourceAgain_OverwritesItsResultAndRecordFollowsOnlyAMovedSource(string state)
    {
        using var t = new TempDir();
        var (a, _, s, outPath) = await SlotTakenByAAsync(t);
        var result = PipelineRunner.AsIsPathFor(outPath, a);
        File.WriteAllBytes(result, [1, 2, 3]);   // 지난 결과 — 다시 돌리면 덮여야 한다
        var run = a;
        if (state == "copiedFolder")
        {
            run = t.At("C", "vol1", "01.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.Copy(a, run);
        }
        if (state == "movedFolder")
        {
            Directory.Move(t.At("A"), t.At("C"));
            run = t.At("C", "vol1", "01.jpg");
        }

        var log = await RunAsync(s, run);

        Assert.Null(log.FinalOf(run).Error);
        Assert.Equal(File.ReadAllBytes(run), File.ReadAllBytes(result));
        var owner = ResultOwner.Load(outPath);
        Assert.NotNull(owner);
        // 복사본은 그 자리를 빌려 쓸 뿐이다(마스크 사이드카와 같은 규칙) — 원래 원고가 그대로 있으면 주인은 넘어가지 않는다
        Assert.Equal(state == "copiedFolder" ? a : run, owner.Source);
        Assert.Equal(new FileInfo(run).Length, owner.SrcLen);
        Assert.Equal(ResultOwner.HashOf(run), owner.SrcHash);
    }

    // ── 이름도 바이트 길이도 같은 다른 원고(압축하지 않는 포맷) ──

    [Theory]
    [InlineData(".bmp")]
    [InlineData(".ppm")]
    [InlineData(".tif")]
    public async Task RunAsync_SameNameAndSameLengthButDifferentContent_IsAnotherSourceAndDoesNotOverwrite(string ext)
    {
        // 압축하지 않는 포맷은 해상도가 같으면 길이가 늘 같다 — 이름+길이만 보면 A\vol1\01.bmp와 B\vol1\01.bmp가 같은 원고로 읽혀 B가 A의 결과를 조용히 덮었다.
        // 주인 기록의 내용 지문이 가른다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01" + ext), seed: 1);
        var b = WriteImageFile(t.At("B", "vol1", "01" + ext), seed: 2);
        Assert.Equal(new FileInfo(a).Length, new FileInfo(b).Length);          // 전제: 길이까지 같다
        Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        AssertUnchanged(before, outDir);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, a)));
    }

    [Fact]
    public async Task RunAsync_SamePathEditedInPlaceKeepingLength_IsStillTheSameSource()
    {
        // 같은 경로의 원고는 제자리에서 고쳐 저장했어도(bmp는 길이도 그대로다) 같은 원고다 — 지문이 달라졌다고 제 결과에 막히면 안 된다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.bmp"), seed: 1);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        var first = ResultOwner.Load(outPath)!;
        WriteImageFile(a, seed: 2);   // 같은 자리에서 고쳐 저장 — 길이는 같고 내용이 다르다
        Assert.Equal(first.SrcLen, new FileInfo(a).Length);

        var log = await RunAsync(s, a);

        Assert.Null(log.FinalOf(a).Error);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, a)));
        var now = ResultOwner.Load(outPath)!;
        Assert.Equal(a, now.Source);
        Assert.NotEqual(first.SrcHash, now.SrcHash);   // 기록은 지금 내용의 지문으로 바뀐다
        Assert.Equal(ResultOwner.HashOf(a), now.SrcHash);
    }

    [Fact]
    public async Task RunAsync_MovedFolderOfUncompressedSources_IsRecognizedByContent()
    {
        // 폴더를 옮긴 같은 원고(이름·길이·내용이 같다)는 지문이 있어도 전처럼 같은 원고다 — 출력 폴더를 고정했어도 막히지 않는다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.bmp"), seed: 1);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        Directory.Move(t.At("A"), t.At("C"));
        var c = t.At("C", "vol1", "01.bmp");

        var log = await RunAsync(s, c);

        Assert.Null(log.FinalOf(c).Error);
        Assert.Equal(c, ResultOwner.Load(outPath)!.Source);
    }

    // ── 주인은 한 번 다른 경로로 돌렸다고 넘어가지 않는다 ──

    [Fact]
    public async Task RunAsync_CopyRunOnce_DoesNotTakeOwnershipSoResavedOriginalStillRuns()
    {
        // 복사본(또는 같은 파일의 다른 경로 표기 — 정션·subst·매핑 드라이브)을 한 번 돌리면 기록의 주인이 그 경로로 바뀌어,
        // 원래 원고를 다시 저장한 뒤(길이가 달라진다) 돌리면 제 결과에 '다른 원고의 결과'라며 막혔다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        var c = t.At("C", "vol1", "01.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(c)!);
        File.Copy(a, c);
        Assert.Null((await RunAsync(s, c)).FinalOf(c).Error);
        Assert.Equal(a, ResultOwner.Load(outPath)!.Source);   // 주인은 그대로 원래 원고

        WriteImageFile(a, seed: 1, width: 61);   // 원래 원고를 다시 저장
        var log = await RunAsync(s, a);

        Assert.Null(log.FinalOf(a).Error);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, a)));
        Assert.NotNull((await RunAsync(s, c)).FinalOf(c).Error);   // 옛 내용의 복사본은 이제 다른 원고다 — 주인의 새 결과를 덮지 않는다
    }

    [Fact]
    public void ResultOwner_Record_TakesOwnershipOnlyWhenTheRecordedSourceIsGoneOrChanged()
    {
        using var t = new TempDir();
        var outPath = t.At("결과", "vol1_01_jpg.png");
        var a = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var c = WriteBytesFile(t.At("C", "vol1", "01.jpg"), 1234);   // 같은 이름·길이·내용
        ResultOwner.Record(outPath, a);

        Assert.Equal(a, ResultOwner.Record(outPath, c).Source);      // 원래 원고가 그대로 있다 — 넘어가지 않는다
        Assert.Equal(a, ResultOwner.Load(outPath)!.Source);

        WriteBytesFile(a, 999);                                      // 그 경로에 다른 것이 왔다(길이가 다르다)
        Assert.Equal(c, ResultOwner.Record(outPath, c).Source);
        File.Delete(a);
        var d = WriteBytesFile(t.At("D", "vol1", "01.jpg"), 1234);
        File.Delete(c);                                              // 옮겼다 — 옛 경로에 원고가 없다
        Assert.Equal(d, ResultOwner.Record(outPath, d).Source);

        var other = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 1234, fill: 0x11);   // 이름·길이만 같은 다른 원고 — 결과를 쓴 쪽이 주인이다
        Assert.Equal(other, ResultOwner.Record(outPath, other).Source);
    }

    [Theory]
    [InlineData(false)]   // 기본 출력 폴더(<원고 폴더>_neko) — 그 폴더의 원고만 쓰는 자리라 이름이 같고 주인이 없으면 옮긴 같은 장이다 → 진행
    [InlineData(true)]    // 출력 폴더 고정 — 이름만 같은 다른 화일 수 있고 남은 결과가 유일한 사본일 수 있다 → 덮지 않는다
    public async Task RunAsync_MovedFolderThenResavedSource_FollowsTheSidecarRule(bool fixedOutputRoot)
    {
        // 사이드카의 규칙(IsOtherSource·SlotFollowsSource) 그대로다 — 이제 모든 장에 기록이 있으므로, 옮기고 다시 저장한 화의 모든 장이 같은 판정을 받는다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t, fixedOutputRoot ? t.At("결과") : "");
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        var aBytes = File.ReadAllBytes(a);

        Directory.Move(t.At("A"), t.At("C"));
        var c = WriteImageFile(t.At("C", "vol1", "01.jpg"), seed: 1, width: 61);   // 같은 장을 다시 저장해 길이가 바뀌었다
        var outPath = PipelineRunner.OutputPathFor(s, c);
        var result = PipelineRunner.AsIsPathFor(outPath, c);
        Assert.Equal(aBytes, File.ReadAllBytes(result));   // 전제: 옮겨 온(또는 고정 폴더에 남은) 자리에 지난 결과가 있다

        var log = await RunAsync(s, c);

        if (fixedOutputRoot)
        {
            var error = log.FinalOf(c).Error;
            Assert.NotNull(error);
            Assert.Contains("지금 없어도", error);
            Assert.Equal(aBytes, File.ReadAllBytes(result));
            return;
        }
        Assert.Null(log.FinalOf(c).Error);
        Assert.Equal(File.ReadAllBytes(c), File.ReadAllBytes(result));
        Assert.True(ResultOwner.Load(outPath)!.MatchesSource(c));
    }

    [Fact]
    public async Task RunAsync_OwnerSourceDeleted_StillProtectsItsResult()
    {
        // 원고를 지웠거나 드라이브가 끊겼으면 남은 결과가 유일한 사본이다 — 사이드카와 같은 규칙
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        var outDir = Path.GetDirectoryName(outPath)!;
        File.Delete(a);
        var before = Snapshot(outDir);

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        Assert.Contains("지금 없어도", error);
        AssertUnchanged(before, outDir);
    }

    [Fact]
    public async Task RunAsync_SameFolderSourcesSharingOneSlotRunSeparately_LaterOneFails()
    {
        // 01.jpg와 01_jpg.png는 어느 출력 폴더에서나 같은 결과 이름(01_jpg)이다 — 같이 돌리면 뒤 장이 실패하는데 따로 돌리면 두 결과가 한 이름 밑에 섞였다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var png = WriteImageFile(t.At("vol1", "01_jpg.png"), seed: 2);
        var s = EnginelessSettings(t);
        Assert.Equal(PipelineRunner.OutputPathFor(s, jpg), PipelineRunner.OutputPathFor(s, png));
        Assert.Null((await RunAsync(s, jpg)).FinalOf(jpg).Error);
        var outDir = t.At("vol1", "vol1_neko");
        var before = Snapshot(outDir);

        var log = await RunAsync(s, png);

        var error = log.FinalOf(png).Error;
        Assert.NotNull(error);
        Assert.Contains("같은 폴더의 다른 원고(01.jpg)", error);
        Assert.Contains("01_jpg.jpg", error);            // 덮일 뻔한 그 원고의 결과
        Assert.DoesNotContain("출력 폴더를", error);     // 출력 폴더를 고정해 생긴 일이 아니다
        AssertUnchanged(before, outDir);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]    // 출력 폴더를 고정했어도 같은 폴더의 두 원고는 어느 출력 폴더에서나 겹친다 — '출력 폴더를 바꾸거나 비워'는 소용없다
    public async Task RunAsync_SameFolderSourcesSharingOneSlotRunSeparately_TellsToRenameLikeWhenRunTogether(bool fixedOutputRoot)
    {
        // '덮어도 되면 결과와 .neko 파일을 지운 뒤 다시'라고 안내하면 서로 번갈아 막힌다: 지우고 01_jpg.png를 돌리면 이번엔 01.jpg가 그 결과에 막힌다.
        // 같이 돌렸을 때(목록의 앞 장과 겹침)와 같은 말로 이름을 바꾸게 한다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var png = WriteImageFile(t.At("vol1", "01_jpg.png"), seed: 2);
        var s = EnginelessSettings(t, fixedOutputRoot ? t.At("결과") : "");
        Assert.Null((await RunAsync(s, jpg)).FinalOf(jpg).Error);

        var apart = (await RunAsync(s, png)).FinalOf(png).Error;

        Assert.NotNull(apart);
        Assert.Contains("둘 중 하나의 파일 이름을 바꿔 주세요.", apart);
        Assert.DoesNotContain("지운 뒤", apart);
        Assert.DoesNotContain("출력 폴더를", apart);

        // 같이 돌렸을 때의 안내와 끝 문장이 같다
        using var t2 = new TempDir();
        var jpg2 = WriteImageFile(t2.At("vol1", "01.jpg"), seed: 1);
        var png2 = WriteImageFile(t2.At("vol1", "01_jpg.png"), seed: 2);
        var together = (await RunAsync(EnginelessSettings(t2, fixedOutputRoot ? t2.At("결과") : ""), jpg2, png2)).FinalOf(png2).Error;
        Assert.NotNull(together);
        Assert.Contains("같은 폴더의 다른 원고(01.jpg)", together);
        Assert.EndsWith("둘 중 하나의 파일 이름을 바꿔 주세요.", together);
        Assert.EndsWith("둘 중 하나의 파일 이름을 바꿔 주세요.", apart);

        // 안내대로 이름을 바꾸면 둘 다 돈다
        var renamed = t.At("vol1", "01b.png");
        File.Move(png, renamed);
        Assert.Null((await RunAsync(s, renamed)).FinalOf(renamed).Error);
        Assert.Null((await RunAsync(s, jpg)).FinalOf(jpg).Error);
    }

    [Fact]
    public async Task RunAsync_SlotOwnerInSameFolderIsGone_KeepsTheGeneralGuidance()
    {
        // 그 원고가 지금 없으면 '둘 중 하나의 이름을 바꿔'라고 할 둘이 없다 — 남은 결과가 유일한 사본일 수 있다는 일반 안내 그대로
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var png = WriteImageFile(t.At("vol1", "01_jpg.png"), seed: 2);
        var s = EnginelessSettings(t);
        Assert.Null((await RunAsync(s, jpg)).FinalOf(jpg).Error);
        File.Delete(jpg);

        var error = (await RunAsync(s, png)).FinalOf(png).Error;

        Assert.NotNull(error);
        Assert.Contains(jpg, error);
        Assert.Contains("지금 없어도", error);
        Assert.Contains("지운 뒤", error);
    }

    // ── 낡았거나 못 읽는 기록이 제 주인을 막지 않는다 ──

    [Fact]
    public async Task RunAsync_OwnerRecordWhoseResultWasDeleted_DoesNotBlockTheNextSource()
    {
        // 기록 자체에는 지킬 것이 없다 — 사용자가 결과물만 지우고 .neko를 남긴 자리는 빈 자리다(안내도 '결과를 지운 뒤 다시'라고 한다)
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        File.Delete(PipelineRunner.AsIsPathFor(outPath, a));

        var log = await RunAsync(s, b);

        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, b)));
        Assert.Equal(b, ResultOwner.Load(outPath)!.Source);

        Assert.NotNull((await RunAsync(s, a)).FinalOf(a).Error);   // 이제 자리는 B의 것이다
    }

    [Fact]
    public async Task RunAsync_OwnerRecordWhoseResultWasDeleted_IsNotRevivedByAnUnrelatedFileOfTheSameName()
    {
        // 기록이 근거가 되는 것은 **그 원고의 결과물**(png 또는 그 확장자의 복사본)이 있을 때다 — 결과 이름이 같은 다른 파일(사용자가 옆에 둔 메모·작업 파일)은 그 원고의 결과가 아니다
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        File.Delete(PipelineRunner.AsIsPathFor(outPath, a));
        var memo = Path.ChangeExtension(outPath, ".txt");
        File.WriteAllText(memo, "메모");

        var log = await RunAsync(s, b);

        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(b, ResultOwner.Load(outPath)!.Source);
        Assert.Equal("메모", File.ReadAllText(memo));
    }

    [Theory]
    [InlineData("empty")]       // 쓰다 끊긴 0바이트
    [InlineData("truncated")]   // 반쯤 쓰인 json
    [InlineData("noSource")]    // 원고 경로가 없는 json
    public async Task RunAsync_BrokenOwnerRecord_IsNoEvidenceAndIsRewritten(string how)
    {
        // 사이드카와 같다 — 깨진 기록으로는 주인을 알 수 없으니 예외로 막지 않고 예전처럼 진행하며, 이번 결과의 기록으로 바꿔 쓴다
        using var t = new TempDir();
        var (_, b, s, outPath) = await SlotTakenByAAsync(t);
        var path = ResultOwner.PathFor(outPath);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, how switch { "empty" => "", "truncated" => text[..(text.Length / 2)], _ => "{}" });

        var log = await RunAsync(s, b);

        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, b)));
        Assert.Equal(b, ResultOwner.Load(outPath)!.Source);
    }

    [Fact]
    public async Task RunAsync_OwnerRecordLocked_FailsAsRetryLaterAndLeavesSlotUntouched()
    {
        // 잠겨서 못 읽은 기록을 '없음'으로 넘기면 남의 결과를 덮는다 — 잠긴 사이드카처럼 덮지 않고 잠시 뒤 다시 돌리게 한다
        using var t = new TempDir();
        var (_, b, s, outPath) = await SlotTakenByAAsync(t);
        var outDir = Path.GetDirectoryName(outPath)!;
        var before = Snapshot(outDir);

        ProgressLog log;
        using (new FileStream(ResultOwner.PathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
            log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains("vol1_01_jpg.owner", error);
        Assert.Contains("잠시 뒤 다시", error);
        AssertUnchanged(before, outDir);
    }

    [Fact]
    public async Task RunAsync_OwnerRecordCannotBeWritten_WarnsButKeepsTheResult()
    {
        // 기록은 결과물 뒤에 쓰는 덤이다 — 못 써도 그 장을 실패로 두지 않고 끝 줄에 경고만 붙인다
        using var t = new TempDir();
        var (a, _, s, outPath) = SharedSlot(t);
        Directory.CreateDirectory(ResultOwner.PathFor(outPath));   // 그 이름의 폴더가 자리를 막고 있다

        var log = await RunAsync(s, a);

        var done = log.FinalOf(a);
        Assert.Null(done.Error);
        Assert.Contains("⚠", done.Stage);
        Assert.Contains("vol1_01_jpg.owner", done.Stage);
        Assert.Contains("주인 표시 없이 남아", done.Stage);   // 무슨 일이 따르는지: 기록이 없으니 예전처럼 덮일 수 있다
        Assert.Contains("묻지 않고 덮여요", done.Stage);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, a)));
        AssertNoTempFiles(Path.GetDirectoryName(outPath)!);
    }

    [Fact]
    public async Task RunAsync_StaleRecordOfAnotherSourceThatCannotBeOverwritten_IsRemovedSoTheNewResultIsNotMistakenForTheirs()
    {
        // A의 결과물은 지워지고 기록만 남았는데 그 기록이 읽기 전용이다(백업에서 되살림) — B의 결과는 나오고 새 기록 쓰기만 실패해 자리에 'B의 결과 + A의 기록'이 남았다.
        // 그 뒤 B는 제 결과인데 막히고, A는 B의 결과를 묻지 않고 덮었다. 덮기로 정한 시점에 낡은 기록을 먼저 치운다
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        File.Delete(PipelineRunner.AsIsPathFor(outPath, a));
        var record = ResultOwner.PathFor(outPath);
        File.SetAttributes(record, File.GetAttributes(record) | FileAttributes.ReadOnly);
        try
        {
            var first = (await RunAsync(s, b)).FinalOf(b);

            Assert.Null(first.Error);
            Assert.DoesNotContain("⚠", first.Stage);                 // 기록까지 제대로 바뀌었다
            Assert.Equal(b, ResultOwner.Load(outPath)!.Source);
            Assert.Null((await RunAsync(s, b)).FinalOf(b).Error);    // B는 제 결과를 다시 돌릴 수 있고
            Assert.NotNull((await RunAsync(s, a)).FinalOf(a).Error); // A는 B의 결과를 덮지 않는다
            Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, b)));
        }
        finally { if (File.Exists(record)) File.SetAttributes(record, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RunAsync_StaleRecordOfAnotherSource_IsDroppedAtTheSlotCheckBeforeTheResultIsWritten()
    {
        // 낡은 남의 기록은 결과를 쓴 뒤 바꿔 쓰기를 기다리지 않고 자리 검사를 통과한 그 자리에서 치운다 — 그 뒤 새 기록을 못 써도 자리에 남의 기록이 남지 않는다.
        // 페이지별 검토로 1차(자리 검사)와 2차(결과·기록 쓰기) 사이에 끼어들어, 그 사이에 기록 자리를 폴더로 막아 쓰기를 실패시킨다
        using var t = new TempDir();
        var s = BarPageSettings(t, t.At("결과"));
        var a = WriteBarPage(t.At("A", "vol1", "01.png"), x: 120);
        var b = WriteBarPage(t.At("B", "vol1", "01.png"), x: 60);
        var outPath = PipelineRunner.OutputPathFor(s, b);
        var record = ResultOwner.PathFor(outPath);
        ResultOwner.Write(outPath, a);   // A의 결과물은 없고 기록만 남은 자리
        var log = new ProgressLog();
        bool? recordLeftAfterSlotCheck = null;
        using var runner = RuleRunner(s);
        runner.PageWaiting += pg =>
        {
            recordLeftAfterSlotCheck = File.Exists(record);
            Directory.CreateDirectory(record);   // 이제 기록을 쓸 수 없다
            using (var cleared = new Mat(BarH, BarW, MatType.CV_8UC1, Scalar.Black)) ImgIo.Write(MaskSidecar.MaskPathFor(pg.OutPath), cleared);   // 오탐이었다 — 지울 곳 없음
            runner.ConfirmPage(pg.File);
        };

        await runner.RunAsync([b], log, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        var done = log.FinalOf(b);
        Assert.Null(done.Error);
        Assert.False(recordLeftAfterSlotCheck);                  // 자리 검사 때 이미 치웠다
        Assert.Contains("주인 표시 없이 남아", done.Stage);      // 새 기록은 못 썼다
        Assert.Null(ResultOwner.Load(outPath));                  // 그래도 A의 기록이 남아 있지 않다
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(outPath));   // png 원고의 '원본 그대로' 복사본
    }

    [Fact]
    public void RecordResultOwner_ForeignRecordThatCanBeNeitherRewrittenNorDeleted_SaysWhatFollows()
    {
        // 남의 기록을 다른 프로그램이 끝내 쥐고 있다 — '다른 원고가 이 결과를 덮을 수 있어요'만으로는 다음에 이 원고가 막힌다는 것을 알 수 없다
        using var t = new TempDir();
        var a = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var b = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 4321);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        ResultOwner.Write(outPath, a);

        string? warning;
        using (new FileStream(ResultOwner.PathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
            warning = PipelineRunner.RecordResultOwner(outPath, b);

        Assert.NotNull(warning);
        Assert.Contains("vol1_01_jpg.owner", warning);
        Assert.Contains("고쳐 쓰지도 지우지도 못했어요", warning);
        Assert.Contains("건너뛰고", warning);                     // 다음에 이 원고는 막히고
        Assert.Contains("이 결과가 덮여요", warning);             // 그 원고는 덮는다
        Assert.Contains("그 파일을 지워 주세요", warning);
        Assert.Equal(a, ResultOwner.Load(outPath)!.Source);       // 잠금이 풀린 뒤에도 남의 기록 그대로다 — 경고한 대로
        AssertNoTempFiles(t.Root);
    }

    [Fact]
    public void RecordResultOwner_OwnRecordThatCannotBeRewritten_HasNothingToWarnAbout()
    {
        // 못 고쳐 쓴 기록이 이미 이 원고 것이면 따르는 일이 없다 — 경고하지 않는다
        using var t = new TempDir();
        var a = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        ResultOwner.Write(outPath, a);

        string? warning;
        using (new FileStream(ResultOwner.PathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.Read))   // 읽을 수는 있고 바꾸거나 지울 수는 없다
            warning = PipelineRunner.RecordResultOwner(outPath, a);

        Assert.Null(warning);
        Assert.Equal(a, ResultOwner.Load(outPath)!.Source);
    }

    [Fact]
    public void RecordResultOwner_WritesTheRecordForAResultSavedOutsideTheRunner()
    {
        // 결과 창 [저장]이 부르는 길 — 낡은 남의 기록이 남은 자리에 저장한 결과가 그 원고 것으로 읽히지 않게 기록을 이 원고로 바꾼다
        using var t = new TempDir();
        var a = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var b = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 4321);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        ResultOwner.Write(outPath, a);
        WriteImageFile(outPath, seed: 9);   // B를 고쳐 저장했다

        Assert.Null(PipelineRunner.RecordResultOwner(outPath, b));

        var owner = ResultOwner.Load(outPath)!;
        Assert.Equal(b, owner.Source);
        Assert.Equal(4321, owner.SrcLen);
        Assert.Equal(ResultOwner.HashOf(b), owner.SrcHash);
    }

    [Fact]
    public void RecordResultOwner_ReadOnlyRecordOfAnotherSource_IsReplacedNotLeftInPlace()
    {
        // 결과 창 [저장]은 자리 검사를 거치지 않는다 — 바꿔 쓸 수 없는(읽기 전용) 남의 기록은 여기서 지우고 다시 쓴다. 남겨 두면 저장한 결과가 그 원고 것으로 읽힌다
        using var t = new TempDir();
        var a = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var b = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 4321);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        ResultOwner.Write(outPath, a);
        var record = ResultOwner.PathFor(outPath);
        File.SetAttributes(record, File.GetAttributes(record) | FileAttributes.ReadOnly);
        try
        {
            Assert.Null(PipelineRunner.RecordResultOwner(outPath, b));

            Assert.Equal(b, ResultOwner.Load(outPath)!.Source);
            AssertNoTempFiles(t.Root);
        }
        finally { if (File.Exists(record)) File.SetAttributes(record, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RunAsync_ResultSavedFromResultWindowOverStaleRecord_BelongsToItsSourceAfterwards()
    {
        // 결과 창 [저장]은 러너 밖에서 png를 쓴다. 기록을 안 남기던 때에는 A의 낡은 기록이 남은 자리에 B를 고쳐 저장하면, B를 다시 돌릴 때 막히고 A를 돌리면 저장한 것이 덮였다
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        File.Delete(PipelineRunner.AsIsPathFor(outPath, a));   // A의 결과를 치웠다(기록은 남았다)
        WriteImageFile(outPath, seed: 9);                      // 결과 창이 B의 결과를 고쳐 저장 — ImgIo.Write(OutPath) 뒤
        Assert.Null(PipelineRunner.RecordResultOwner(outPath, b));   // 주인 기록
        var saved = File.ReadAllBytes(outPath);

        var logA = await RunAsync(s, a);

        Assert.NotNull(logA.FinalOf(a).Error);                 // A는 B가 저장한 결과를 덮지 않는다
        Assert.Equal(saved, File.ReadAllBytes(outPath));
        Assert.Null((await RunAsync(s, b)).FinalOf(b).Error);  // B는 제 자리를 다시 돌릴 수 있다
    }

    // ── 잠깐 잠긴 기록 ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SameSourceRerunWhileOwnerRecordIsBrieflyLocked_WaitsAndProceeds(bool fixedOutputRoot)
    {
        // 모든 결과에 기록이 있어, 백신·동기화가 잠깐 쥔 순간에 같은 원고를 다시 돌리기만 해도 그 장이 실패했다 — 마스크 사이드카처럼 기다렸다 다시 읽는다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t, fixedOutputRoot ? t.At("결과") : "");
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);
        var held = new FileStream(ResultOwner.PathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
        // 다시 읽기(0.7초 뒤)보다 먼저 풀린다. 전용 스레드로 푼다 — Task.Delay(스레드풀)로 풀면 테스트가 병렬로 도는 느린 CI에서
        // 풀이 밀려 0.7초를 넘겼다(v1.9.1 Build Run 77 실패: "…owner를 다른 프로그램이 쥐고 있어")
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };
        release.Start();

        var log = await RunAsync(s, a);
        release.Join();

        Assert.Null(log.FinalOf(a).Error);
        Assert.DoesNotContain("⚠", log.FinalOf(a).Stage);
        Assert.True(ResultOwner.Load(outPath)!.MatchesSource(a));
    }

    [Fact]
    public async Task RunAsync_OwnerRecordLockedButSlotHasNoResult_ProceedsBecauseThereIsNothingToProtect()
    {
        // 결과물이 없는 자리는 기록이 누구 것이든 지킬 것이 없다 — 잠겨서 못 읽어도 그 장을 막지 않는다(기다리지도 않는다)
        using var t = new TempDir();
        var (a, b, s, outPath) = await SlotTakenByAAsync(t);
        File.Delete(PipelineRunner.AsIsPathFor(outPath, a));

        ProgressLog log;
        using (new FileStream(ResultOwner.PathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
            log = await RunAsync(s, b);

        var done = log.FinalOf(b);
        Assert.Null(done.Error);
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.AsIsPathFor(outPath, b)));
        Assert.Contains("고쳐 쓰지도 지우지도 못했어요", done.Stage);   // 결과는 썼고, 끝내 잠긴 기록은 무슨 일이 따르는지 경고한다
    }

    [Fact]
    public async Task RunAsync_OwnerRecordLockedAndSlotHoldsACopyWithAnotherExtension_StillFailsAsRetryLater()
    {
        // '지킬 결과물이 없다'는 이 원고의 확장자만 보고 정하면 안 된다 — 01.jpg의 복사본(01_jpg.jpg)이 있는 자리를 01_jpg.png가 잠긴 기록을 못 읽은 채 덮는다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var png = WriteImageFile(t.At("vol1", "01_jpg.png"), seed: 2);
        var s = EnginelessSettings(t);
        Assert.Null((await RunAsync(s, jpg)).FinalOf(jpg).Error);
        var outDir = t.At("vol1", "vol1_neko");
        var before = Snapshot(outDir);

        ProgressLog log;
        using (new FileStream(ResultOwner.PathFor(PipelineRunner.OutputPathFor(s, png)), FileMode.Open, FileAccess.Read, FileShare.None))
            log = await RunAsync(s, png);

        Assert.Contains("잠시 뒤 다시", log.FinalOf(png).Error);
        AssertUnchanged(before, outDir);
    }

    // ── 기록은 결과물을 쓴 뒤에 적는다 ──

    [Fact]
    public async Task RunAsync_UntouchedCopyCannotBeWritten_LeavesNoOwnerRecord()
    {
        // 기록을 결과물보다 먼저 적으면, 쓰기에 실패한 자리(남의·주인 모를 옛 결과가 있을 수 있다)가 이 원고 것으로 적힌다
        using var t = new TempDir();
        var (a, _, s, outPath) = SharedSlot(t);
        Directory.CreateDirectory(PipelineRunner.AsIsPathFor(outPath, a));   // '원본 그대로' 복사본 자리를 폴더가 막고 있다

        var log = await RunAsync(s, a);

        Assert.NotNull(log.FinalOf(a).Error);
        Assert.False(File.Exists(ResultOwner.PathFor(outPath)));
    }

    [Fact]
    public async Task RedoPageAsync_PngResultCannotBeWritten_LeavesTheOwnerRecordAsItWas()
    {
        // png를 쓰는 가지도 같다 — 결과물을 못 썼으면 기록을 건드리지 않는다(있던 남의 기록이 이 원고 것으로 바뀌면 그 원고의 결과가 덮인다)
        using var t = new TempDir();
        var src = WriteImageFile(t.At("B", "vol1", "01.jpg"), seed: 2);
        var other = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(60, 40, seed: 4);
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false);
        var before = ResultOwner.Write(outPath, other);
        Directory.CreateDirectory(outPath);   // 결과 png 자리를 폴더가 막고 있다
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RedoPageAsync(src, log, CancellationToken.None));

        Assert.Equal(before, ResultOwner.Load(outPath));
    }

    // ── png로 다시 인코딩한 결과(번역·검열 제거·채색을 거친 보통의 결과)도 지킨다 ──

    [Fact]
    public async Task RunAsync_PngResultOfAJpgSource_IsProtectedFromAnotherSourceOnTheSameSlot()
    {
        // 위의 시험들은 '원본 그대로' 복사본(vol1_01_jpg.jpg)을 지킨다. 실제로 흔한 것은 jpg 원고 → png 결과(vol1_01_jpg.png)다 —
        // [채색부터 다시]로 진짜 png 결과와 기록을 만들고, 주인을 알려 줄 다른 근거(마스크 사이드카)는 치워 기록만 남긴다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        using (var mask = RectMask(60, 40, new Rect(5, 5, 10, 10)))
        using (var pre = ColorImage(60, 40, seed: 4))
            MaskSidecar.Write(outPath, a, mask, null, pre, "감지", skip: false, edited: true, confirmed: false);
        using (var runner = RuleRunner(s))
            await runner.RedoPageAsync(a, new ProgressLog(), CancellationToken.None);
        var outDir = Path.GetDirectoryName(outPath)!;
        foreach (var f in new[] { MaskSidecar.JsonPathFor(outPath), MaskSidecar.MaskPathFor(outPath), MaskSidecar.PrePathFor(outPath) }) File.Delete(f);
        var before = Snapshot(outDir);
        Assert.Equal([Path.Combine(".neko", "vol1_01_jpg.owner"), "vol1_01_jpg.png"], before.Names);   // 전제: png 결과와 주인 기록뿐이다

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        Assert.Contains("vol1_01_jpg.png", error);   // 덮일 뻔한 것은 png 결과다
        AssertUnchanged(before, outDir);
        Assert.Null((await RunAsync(s, a)).FinalOf(a).Error);   // 주인은 다시 돌릴 수 있다
    }

    // ── 결과물을 쓰는 다른 길 ──

    [Theory]
    [InlineData(false)]   // 글자 없는 장 — png로 다시 인코딩
    [InlineData(true)]    // 식자한 장
    public async Task RedoPageAsync_WritesOwnerRecordForThePngResult(bool lettered)
    {
        // 결과 창 [채색부터 다시]도 2차를 타므로 그 결과(png)에도 기록이 남는다 — 지난 실행이 '원본 그대로'였으면 그때의 기록을 그대로 잇는다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(60, 40, seed: 4);
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false);
        if (lettered)
        {
            var basePath = WriteImageFile(LetterSidecar.BasePathFor(outPath), seed: 4);
            new LetterSidecar(src, 60, 40, basePath, [new TextBlockInfo(5, 5, 50, 30, "あ", "아", 12, false, "#000000")], MaskSidecar.LengthOf(src)).Save(outPath);
        }
        Assert.False(File.Exists(ResultOwner.PathFor(outPath)));
        var log = new ProgressLog();

        using (var runner = RuleRunner(s))
            await runner.RedoPageAsync(src, log, CancellationToken.None);

        Assert.Null(log.FinalOf(src).Error);
        Assert.True(File.Exists(outPath));
        var owner = ResultOwner.Load(outPath);
        Assert.NotNull(owner);
        Assert.Equal(src, owner.Source);
        Assert.Equal(new FileInfo(src).Length, owner.SrcLen);
    }

    [Fact]
    public async Task RunAsync_OutputFolderIsSourceFolder_UntouchedPageLeavesNoRecord()
    {
        // 출력 폴더가 원고 폴더면 '원본 그대로'는 원본 자신이다 — 쓴 결과물이 없으니 기록도 없다(원고 폴더에 .neko를 만들지 않는다)
        using var t = new TempDir();
        var vol = t.At("vol1");
        var jpg = WriteImageFile(Path.Combine(vol, "01.jpg"), seed: 1);
        var s = EnginelessSettings(t, vol);

        var log = await RunAsync(s, jpg);

        Assert.Null(log.FinalOf(jpg).Error);
        Assert.Equal(["01.jpg"], FilesUnder(vol));
        Assert.Null(ResultOwner.Load(PipelineRunner.OutputPathFor(s, jpg)));
    }

    [Fact]
    public async Task RunAsync_LegacyNameSlotHoldsRecordedResultOfDeletedSibling_IsNotTakenOver()
    {
        // 옛 이름 옮기기는 기본 _neko의 <이름>.png를 '원고 폴더에 그 이름의 원고가 이것 하나뿐'이면 이 원고의 옛 결과로 보고 가져간다.
        // 그런데 그 자리는 지금 규칙으로 01.png 원고의 결과 자리다 — 01.png를 지워 01.jpg가 하나뿐인 01.*이 되면 01.png의 결과를 가져가 지웠다.
        // 주인 기록이 있는 결과는 옛 버전의 것이 아니니 그대로 둔다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var png = WriteImageFile(t.At("vol1", "01.png"), seed: 2);
        var s = EnginelessSettings(t);
        await RunAsync(s, jpg, png);
        var pngResult = t.At("vol1", "vol1_neko", "01.png");
        var pngBytes = File.ReadAllBytes(pngResult);
        Assert.Equal(File.ReadAllBytes(png), pngBytes);
        File.Delete(png);   // 원고를 지웠다 — 결과만 남았다

        var log = await RunAsync(s, jpg);

        Assert.Null(log.FinalOf(jpg).Error);
        Assert.True(File.Exists(pngResult));
        Assert.Equal(pngBytes, File.ReadAllBytes(pngResult));
        Assert.Equal(File.ReadAllBytes(jpg), File.ReadAllBytes(t.At("vol1", "vol1_neko", "01_jpg.jpg")));
    }

    [Theory]
    [InlineData("corrupt", false)]       // 기록이 있는데 깨져서 누구 것인지 모른다 — 가져오지 않는다
    [InlineData("locked", false)]        // 기록이 있는데 잠겨서 못 읽는다(내용은 이 원고 것이어도) — 가져오지 않는다
    [InlineData("otherSource", false)]   // 기록의 주인이 다른 원고다
    [InlineData("thisSource", true)]     // 기록의 주인이 이 원고다 — 옛 이름의 제 결과라 전처럼 새 이름으로 넘겨받는다
    public async Task RunAsync_LegacyNameSlotWithOwnerRecord_IsTakenOverOnlyWhenTheRecordReadsAsThisSource(string record, bool takenOver)
    {
        // 기본 _neko의 <이름>.png는 그 이름의 원고가 이것 하나뿐이면 이 원고의 옛(v1.5.5) 결과로 보고 넘겨받는다 — 주인 기록이 있으면 그 판단을 기록이 대신한다.
        // 기록이 이 원고 것이라고 **읽힐 때만** 넘겨받는다: 못 읽는 기록(잠김·깨짐)을 '기록 없음'처럼 다루면 남의 결과를 가져가 2차가 지운다
        using var t = new TempDir();
        var jpg = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var s = EnginelessSettings(t);
        var legacy = WriteImageFile(t.At("vol1", "vol1_neko", "01.png"), seed: 7);
        var legacyBytes = File.ReadAllBytes(legacy);
        var path = ResultOwner.PathFor(legacy);
        ResultOwner.Write(legacy, record == "otherSource" ? WriteBytesFile(t.At("elsewhere", "01.png"), 77) : jpg);
        if (record == "corrupt") File.WriteAllText(path, File.ReadAllText(path)[..20]);

        ProgressLog log;
        using (record == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            log = await RunAsync(s, jpg);

        Assert.Null(log.FinalOf(jpg).Error);
        Assert.Equal(File.ReadAllBytes(jpg), File.ReadAllBytes(t.At("vol1", "vol1_neko", "01_jpg.jpg")));
        if (takenOver) Assert.False(File.Exists(legacy));   // 새 이름으로 옮겨져 이번 결과로 바뀌었다
        else Assert.Equal(legacyBytes, File.ReadAllBytes(legacy));
    }

    // ── 기록 자체 ──

    [Fact]
    public void ResultOwner_WriteThenLoad_RoundTripsAndMatchesByPathOrByNameLengthAndContent()
    {
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var sameBytesElsewhere = WriteBytesFile(t.At("C", "vol1", "01.jpg"), 1234);
        var otherLength = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 1235);
        var sameLengthOtherContent = WriteBytesFile(t.At("D", "vol1", "01.jpg"), 1234, fill: 0x11);
        var otherName = WriteBytesFile(t.At("A", "vol1", "02.jpg"), 1234);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        Assert.Null(ResultOwner.Load(outPath));   // 없으면 null

        var written = ResultOwner.Write(outPath, src);
        var loaded = ResultOwner.Load(outPath);

        Assert.Equal(written, loaded);
        Assert.Equal(src, loaded!.Source);
        Assert.Equal(1234, loaded.SrcLen);
        Assert.Matches("^[0-9A-F]{32}$", loaded.SrcHash);
        Assert.Equal(t.At("결과", ".neko", "vol1_01_jpg.owner"), ResultOwner.PathFor(outPath));
        Assert.True(loaded.MatchesSource(src));
        Assert.True(loaded.MatchesSource(sameBytesElsewhere));        // 폴더를 옮기거나 복사한 같은 원고(이름+바이트 길이+내용)
        Assert.False(loaded.MatchesSource(otherLength));              // 이름만 같은 다른 원고
        Assert.False(loaded.MatchesSource(sameLengthOtherContent));   // 이름·길이까지 같은 다른 원고 — 내용 지문이 가른다
        Assert.False(loaded.MatchesSource(otherName));
        File.WriteAllBytes(src, File.ReadAllBytes(sameLengthOtherContent));
        Assert.True(loaded.MatchesSource(src));                       // 같은 경로는 제자리에서 고쳐 저장해도 같은 원고다
        AssertNoTempFiles(t.Root);
    }

    [Fact]
    public void ResultOwner_RecordWithoutFingerprint_StillLoadsAndFallsBackToNameAndLength()
    {
        // 지문 필드가 없는 기록(이 필드가 생기기 전에 쓴 것, 원고를 읽지 못해 지문 없이 쓴 것)도 읽히고, 그때는 이름+길이까지만 본다
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("A", "vol1", "01.jpg"), 1234);
        var sameLengthOtherContent = WriteBytesFile(t.At("D", "vol1", "01.jpg"), 1234, fill: 0x11);
        var otherLength = WriteBytesFile(t.At("B", "vol1", "01.jpg"), 1235);
        var outPath = t.At("결과", "vol1_01_jpg.png");
        Directory.CreateDirectory(Path.GetDirectoryName(ResultOwner.PathFor(outPath))!);
        File.WriteAllText(ResultOwner.PathFor(outPath),
            System.Text.Json.JsonSerializer.Serialize(new { Source = src, SrcLen = 1234L, SavedAt = new DateTime(2026, 11, 11, 12, 0, 0) }));

        var loaded = ResultOwner.Load(outPath);

        Assert.NotNull(loaded);
        Assert.Equal(src, loaded.Source);
        Assert.Equal("", loaded.SrcHash);
        Assert.True(loaded.MatchesSource(sameLengthOtherContent));
        Assert.False(loaded.MatchesSource(otherLength));
    }

    [Fact]
    public void ResultOwner_HashOf_CoversTheWholeFileNotJustItsHead()
    {
        // 해상도가 같은 bmp는 머리가 같고 앞부분(아래 여백)도 같기 쉽다 — 파일 어디가 달라도 지문이 달라야 한다
        using var t = new TempDir();
        var bytes = new byte[3 * 1024 * 1024];   // 3MB, 전부 흰색
        Array.Fill(bytes, (byte)0xFF);
        var whole = t.At("whole.bmp");
        File.WriteAllBytes(whole, bytes);
        var reference = ResultOwner.HashOf(whole);
        Assert.Matches("^[0-9A-F]{32}$", reference);
        Assert.Equal(reference, ResultOwner.HashOf(whole));   // 같은 내용은 같은 지문

        foreach (var at in new[] { 0, 54, 4096, 70_000, bytes.Length / 2, bytes.Length - 70_000, bytes.Length - 1 })
        {
            var changed = (byte[])bytes.Clone();
            changed[at] = 0x00;
            var path = t.At($"changed_{at}.bmp");
            File.WriteAllBytes(path, changed);
            Assert.NotEqual(reference, ResultOwner.HashOf(path));
        }
        Assert.Equal("", ResultOwner.HashOf(t.At("없는 파일.bmp")));   // 못 읽으면 빈 지문 — 그 기록은 이름+길이 규칙으로 돌아간다
    }

    [Fact]
    public void ResultOwner_PathFor_NeverCollidesWithOtherSidecarsOfAnyResultName()
    {
        // 확장자를 .json·.png로 두지 않은 이유 — 01.png의 기록이 '01.owner.json'이면 01.owner.png의 식자 사이드카와 같은 파일이 된다
        using var t = new TempDir();
        string[] outs = [.. new[] { "01.png", "01.owner.png", "01.mask.png", "01.base.png" }.Select(n => t.At("out", n))];

        var all = outs.SelectMany(o => new[]
        {
            ResultOwner.PathFor(o), LetterSidecar.JsonPathFor(o), LetterSidecar.BasePathFor(o),
            MaskSidecar.JsonPathFor(o), MaskSidecar.MaskPathFor(o), MaskSidecar.AutoPathFor(o), MaskSidecar.PrePathFor(o), MaskSidecar.KindPathFor(o),
        }).ToList();

        var owners = outs.Select(ResultOwner.PathFor).ToList();
        Assert.Equal(owners.Count, owners.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(owners, o => Assert.Single(all, p => string.Equals(p, o, StringComparison.OrdinalIgnoreCase)));
    }
}
