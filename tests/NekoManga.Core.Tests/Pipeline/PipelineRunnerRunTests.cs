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

    /// <summary>결과 폴더 기준으로 본 주인 기록 파일 — 러너가 결과물을 쓸 때마다 <c>.neko\&lt;결과 이름&gt;.owner</c>를 남긴다.</summary>
    private static string OwnerFile(string stem) => Path.Combine(".neko", stem + ".owner");

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

        // 결과마다 주인 기록(.neko\<이름>.owner)이 하나씩 따라 남는다
        Assert.Equal([OwnerFile("01"), OwnerFile("01_jpg"), OwnerFile("01_webp"), "01.png", "01_jpg.jpg", "01_webp.webp"], FilesUnder(Path.Combine(vol, "1화 テスト_neko")));
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
        Assert.Equal([OwnerFile("ch1_001_jpg"), "ch1_001_jpg.jpg"], FilesUnder(outDir));
        Assert.True(ResultOwner.Load(Path.Combine(outDir, "ch1_001_jpg.png"))!.MatchesSource(a));   // 자리를 차지한 앞 장의 기록이다
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
        Assert.Equal([OwnerFile("01_jpg"), "01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(t.At("vol1", "vol1_neko", "01_jpg.jpg")));
    }

    // ── 따로 돌린 원고가 남의 결과 자리를 덮지 않는다 ──

    /// <summary>출력 폴더를 고정하면 같은 결과 자리로 떨어지는 두 원고 A\vol1\01.jpg·B\vol1\01.jpg — 해상도는 같고(1화·2화처럼) 내용·바이트 길이가 다르다.
    /// 결과 자리에는 A를 전에 돌려 만든 결과물(png)을 놓아 둔다.</summary>
    private static (string A, string B, AppSettings S, string OutPath) SharedSlot(TempDir t)
    {
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At("B", "vol1", "01.jpg"), seed: 2);
        Assert.NotEqual(new FileInfo(a).Length, new FileInfo(b).Length);   // 전제: 이름만 같은 다른 원고
        var s = EnginelessSettings(t, t.At("결과"));
        var outPath = PipelineRunner.OutputPathFor(s, a);
        Assert.Equal(outPath, PipelineRunner.OutputPathFor(s, b));          // 전제: 둘이 같은 결과 자리
        WriteImageFile(outPath, seed: 7);
        return (a, b, s, outPath);
    }

    /// <summary>원고를 검토 흐름으로 돌렸을 때 결과 자리에 남는 마스크 사이드카 — 저장 API로 만든다. legacy면 원고 길이를 적기 전(v1.5.5 전)의 사이드카.</summary>
    private static void WriteMaskSidecarOf(string source, string outPath, bool legacy = false)
    {
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        using var pre = ColorImage(60, 40, seed: 3);
        var sc = MaskSidecar.Write(outPath, source, mask, null, pre, "감지", skip: false, edited: true, confirmed: true);
        if (legacy) (sc with { SrcLen = 0 }).Save(outPath);
    }

    /// <summary>원고를 번역해 돌렸을 때 결과 자리에 남는 식자 사이드카 — 저장 API로 만든다. legacy면 원고 길이를 적기 전(v1.6.0 전)의 사이드카.</summary>
    private static void WriteLetterSidecarOf(string source, string outPath, bool legacy = false)
    {
        var basePath = WriteImageFile(LetterSidecar.BasePathFor(outPath), seed: 4);
        new LetterSidecar(source, 60, 40, basePath, [], legacy ? 0 : MaskSidecar.LengthOf(source)).Save(outPath);
    }

    [Theory]
    [InlineData("mask", false)]           // 검토 흐름이 남긴 마스크 사이드카
    [InlineData("letter", false)]         // 번역이 남긴 식자 사이드카
    [InlineData("letterLegacy", false)]   // 길이 기록이 없는 옛 식자 사이드카 — 해상도가 같은 다른 화라 크기로는 같은 원고라고 볼 수 없다
    [InlineData("onlyLetterIsA", false)]  // 마스크 사이드카는 B 것이어도 식자 사이드카가 A 것이면 — 둘 중 하나라도 남의 것이면 덮지 않는다
    // 주인 원고가 지워졌거나 잠깐 없다(압축해 지움·외장 드라이브 끊김) — 그러면 남은 결과가 유일한 사본이다. 원고 길이를 적은 사이드카는 이름이 같고 길이가 다르다는 것만으로
    // 다른 원고라는 근거가 되니 주인 파일이 없어도 덮지 않는다(처음 고칠 때는 '지킬 결과의 주인이 없다'며 진행해 그 유일한 결과를 지웠다)
    [InlineData("mask", true)]
    [InlineData("letter", true)]
    public async Task RunAsync_SlotHoldsSidecarOfAnotherSource_FailsAndLeavesSlotUntouched(string sidecar, bool ownerGone)
    {
        // 같이 돌리면 앞 장이 차지한 자리라 실패로 두지만, 따로 돌리면 B가 A의 결과물·사이드카를 조용히 덮었다 —
        // 그 자리 사이드카가 A의 것이면 B는 아무것도 쓰지 않고 실패로 안내한다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        if (sidecar == "mask") WriteMaskSidecarOf(a, outPath);
        else WriteLetterSidecarOf(a, outPath, legacy: sidecar == "letterLegacy");
        if (sidecar == "onlyLetterIsA") WriteMaskSidecarOf(b, outPath);
        if (ownerGone) File.Delete(a);
        var outDir = Path.GetDirectoryName(outPath)!;
        var names = FilesUnder(outDir);
        var bytes = names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray();

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);   // 누구의 결과인지 알려 준다
        Assert.Contains("출력 폴더를 바꾸거나 비워", error);   // 출력 폴더를 고정해 생긴 충돌이다
        Assert.Equal(names, FilesUnder(outDir));
        Assert.Equal(bytes, names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray());
    }

    [Theory]
    [InlineData("sameSource")]           // 같은 원고를 다시 돌린다
    [InlineData("copiedFolder")]         // A의 원고 폴더를 복사해 와서 돌린다 — 원래 폴더가 남아 있어도 이름+바이트 길이가 같으면 같은 원고
    [InlineData("movedFolder")]          // A의 원고 폴더를 옮겨 와서 돌린다 — 옛 경로에 원고는 없어도 이름+바이트 길이가 같으면 같은 원고
    [InlineData("legacyOwnerDeleted")]   // 길이 기록이 없는 옛 사이드카에 주인 원고가 없다 — 경로로만 가리므로 폴더를 옮긴 같은 원고일 수도 있어 지금처럼 덮는다(한계)
    [InlineData("noSidecar")]            // 사이드카도 주인 기록도 없는 결과(v1.8.2까지 검토 흐름 없이 돌린 글자 없는 장) — 주인을 가릴 수 없어 예전처럼 덮는다(한계). 지금 만드는 결과는 주인 기록이 지킨다(PipelineRunnerResultOwnerTests)
    public async Task RunAsync_SlotSidecarHasNoOtherExistingOwner_ProceedsAndWritesOwnResult(string state)
    {
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        bool legacy = state == "legacyOwnerDeleted";
        if (state != "noSidecar") { WriteMaskSidecarOf(a, outPath, legacy); WriteLetterSidecarOf(a, outPath, legacy); }
        var run = state == "sameSource" ? a : b;
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
        if (legacy) File.Delete(a);

        var log = await RunAsync(s, run);

        Assert.Null(log.FinalOf(run).Error);
        var shown = PipelineRunner.ResolveResultPath(outPath, run);
        Assert.NotEqual(run, shown);
        Assert.Equal(File.ReadAllBytes(run), File.ReadAllBytes(shown));   // 결과 자리가 이번 원고의 결과('원본 그대로')로 바뀌었다
    }

    [Theory]
    [InlineData(false, true)]    // 마스크 사이드카 json이 0바이트
    [InlineData(false, false)]   // 마스크 사이드카 json이 반쯤 쓰였다
    [InlineData(true, true)]     // 식자 사이드카 json이 0바이트
    [InlineData(true, false)]    // 식자 사이드카 json이 반쯤 쓰였다
    public async Task RunAsync_BrokenSidecarInSlot_IsNoEvidenceAndPageProceeds(bool letter, bool empty)
    {
        // 깨진 사이드카로는 주인을 알 수 없다 — 예외로 장을 막지 않고 지금처럼 진행하며, 사이드카는 읽기만 한다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        if (letter) WriteLetterSidecarOf(a, outPath); else WriteMaskSidecarOf(a, outPath);
        var json = letter ? LetterSidecar.JsonPathFor(outPath) : MaskSidecar.JsonPathFor(outPath);
        var text = File.ReadAllText(json);
        File.WriteAllText(json, empty ? "" : text[..(text.Length / 2)]);
        var brokenBytes = File.ReadAllBytes(json);

        var log = await RunAsync(s, b);

        Assert.Null(log.FinalOf(b).Error);
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, b)));
        Assert.Equal(brokenBytes, File.ReadAllBytes(json));
    }

    [Theory]
    [InlineData(false)]   // 마스크 사이드카 json
    [InlineData(true)]    // 식자 사이드카 json
    public async Task RunAsync_SidecarJsonLockedInSlot_FailsAsRetryLaterAndLeavesSlotUntouched(bool letter)
    {
        // 백신 검사·동기화가 사이드카 json을 잠깐 쥐고 있으면 Load는 깨진 것과 똑같이 null이다 — 근거 없음으로 덮으면 잠금이 풀린 뒤 자리에
        // B의 결과와 A의 사이드카가 섞여 남아, B는 다음부터 제 결과인데 막히고 A는 B를 덮는다. 주인을 확인할 수 없으니 덮지 않고 잠시 뒤 다시 돌리게 한다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        if (letter) WriteLetterSidecarOf(a, outPath); else WriteMaskSidecarOf(a, outPath);
        var json = letter ? LetterSidecar.JsonPathFor(outPath) : MaskSidecar.JsonPathFor(outPath);
        var outDir = Path.GetDirectoryName(outPath)!;
        var names = FilesUnder(outDir);
        var bytes = names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray();

        ProgressLog log;
        using (new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.None))
            log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains("잠시 뒤 다시", error);
        Assert.Equal(names, FilesUnder(outDir));
        Assert.Equal(bytes, names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray());
    }

    [Theory]
    [InlineData("png")]    // 결과물(png)이 있다
    [InlineData("asIs")]   // 결과가 '원본 그대로' 복사본이다 — 지우거나 덮일 파일은 그 이름이다
    [InlineData("none")]   // 결과 파일 없이 사이드카만 있다(검토 중에 멈췄거나 2차가 실패) — 덮일 것은 저장된 자료다
    public async Task RunAsync_SlotConflictMessage_NamesWhatIsActuallyInTheSlot(string result)
    {
        // 안내가 가리키는 파일이 디스크에 없으면 사용자가 무엇을 지키거나 지워야 하는지 찾지 못한다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);   // 결과 자리에 A의 결과물 png를 놓아 둔다
        var asIs = PipelineRunner.AsIsPathFor(outPath, a);
        if (result != "png") File.Delete(outPath);
        if (result == "asIs") ImgIo.CopyAtomic(a, asIs);
        WriteMaskSidecarOf(a, outPath);

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        if (result == "png") Assert.Contains(Path.GetFileName(outPath), error);
        else Assert.DoesNotContain(Path.GetFileName(outPath), error);
        if (result == "asIs") Assert.Contains(Path.GetFileName(asIs), error);
        if (result == "none")
        {
            Assert.Contains("저장된 마스크·식자 자료", error);
            Assert.DoesNotContain("결과 파일", error);
        }
    }

    [Fact]
    public async Task RunAsync_SlotConflictInDefaultNekoFolder_DoesNotAdviseChangingOutputFolder()
    {
        // 기본 _neko는 그 원고 폴더의 결과만 받으니 '출력 폴더를 바꾸거나 비워'는 소용없는 말이다 — 거기서 걸리는 것은 폴더를 복사한 뒤(원래 폴더가 남아 있다)
        // 원고를 다시 저장한 경우(또는 옛 이름 자료)이고, 덮어도 되는지는 사용자가 정해 지워야 풀린다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var c = WriteImageFile(t.At("C", "vol1", "01.jpg"), seed: 1, width: 61);   // A 폴더를 _neko째 복사해 와서 원고를 손봤다 — 이름은 같고 길이가 다르다
        Assert.NotEqual(new FileInfo(a).Length, new FileInfo(c).Length);
        var s = EnginelessSettings(t);
        var outC = PipelineRunner.OutputPathFor(s, c);
        WriteImageFile(outC, seed: 7);
        WriteMaskSidecarOf(a, outC);   // 복사해 온 _neko의 사이드카는 원래 폴더의 원고를 가리킨다

        var log = await RunAsync(s, c);

        var error = log.FinalOf(c).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        Assert.DoesNotContain("출력 폴더를", error);
        Assert.Contains("지운 뒤 다시 돌려", error);
    }

    [Theory]
    [InlineData(false)]   // 기본 출력 폴더(<원고 폴더>_neko) → 옮긴 같은 장으로 보고 진행
    [InlineData(true)]    // 출력 폴더 = 원고 폴더 → 고정한 폴더라 다른 곳의 같은 이름 폴더도 이 자리로 올 수 있다 — 덮지 않는다
    public async Task RunAsync_MovedSourceFolderThenResavedSource_ProceedsOnlyInDefaultFolder(bool outputIsSourceFolder)
    {
        // 기본 _neko는 그 원고 폴더의 원고만 쓰는 자리다 — 이름이 같은데 주인이 없는 사이드카는 옮긴 같은 장뿐이다.
        // '길이 기록이 있으면 주인이 없어도 충돌'을 여기까지 적용해, 폴더를 결과째 옮기고 원고를 다시 저장(재인코딩·최적화)한 제 장이 '다른 원고의 결과'로 막혔다
        using var t = new TempDir();
        var a = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var before = EnginelessSettings(t, outputIsSourceFolder ? t.At("A", "vol1") : "");
        var slotA = PipelineRunner.OutputPathFor(before, a);
        if (!outputIsSourceFolder) WriteImageFile(slotA, seed: 7);   // 지난번 결과물
        WriteMaskSidecarOf(a, slotA);
        WriteLetterSidecarOf(a, slotA);

        Directory.Move(t.At("A"), t.At("C"));
        var c = WriteImageFile(t.At("C", "vol1", "01.jpg"), seed: 1, width: 61);   // 같은 장을 다시 저장해 길이가 바뀌었다
        var s = EnginelessSettings(t, outputIsSourceFolder ? t.At("C", "vol1") : "");
        var outPath = PipelineRunner.OutputPathFor(s, c);
        var stored = MaskSidecar.Load(outPath);
        Assert.NotNull(stored);                    // 전제: 옮겨 온 자리에 옛 경로·옛 길이를 적은 사이드카가 있다
        Assert.False(stored.MatchesSource(c));
        Assert.False(File.Exists(stored.Source));

        var log = await RunAsync(s, c);

        if (outputIsSourceFolder)
        {
            Assert.NotNull(log.FinalOf(c).Error);
            Assert.False(MaskSidecar.Load(outPath)!.MatchesSource(c));   // 사이드카는 그대로다
            return;
        }
        Assert.Null(log.FinalOf(c).Error);
        Assert.Equal(File.ReadAllBytes(c), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, c)));
    }

    [Fact]
    public async Task RunAsync_FixedOutputFolderMoved_LetterSidecarOfAnotherSourceStillProtectsSlot()
    {
        // 식자 사이드카는 식자 전 원고의 절대 경로를 적는다 — 출력 폴더를 옮기면 그 경로가 옛 폴더를 가리켜 다시 식자할 사이드카로는 읽히지 않는다.
        // 그 읽기로 주인을 가려, 식자 사이드카만 있는 자리(검토 흐름 없이 번역한 장)에서 B가 A의 결과를 덮거나 '다른 형태'로 지웠다 — 주인은 json의 원고 경로·길이로 가린다
        using var t = new TempDir();
        var (a, b, _, oldOut) = SharedSlot(t);
        WriteLetterSidecarOf(a, oldOut);
        Directory.Move(t.At("결과"), t.At("옮긴 결과"));
        var s = EnginelessSettings(t, t.At("옮긴 결과"));
        var outPath = PipelineRunner.OutputPathFor(s, b);
        Assert.True(File.Exists(LetterSidecar.JsonPathFor(outPath)));
        Assert.Null(LetterSidecar.Load(outPath));   // 전제: json은 있는데 식자 전 원고 경로가 옛 폴더다
        var outDir = Path.GetDirectoryName(outPath)!;
        var names = FilesUnder(outDir);
        var bytes = names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray();

        var log = await RunAsync(s, b);

        var error = log.FinalOf(b).Error;
        Assert.NotNull(error);
        Assert.Contains(a, error);
        Assert.Equal(names, FilesUnder(outDir));
        Assert.Equal(bytes, names.Select(n => File.ReadAllBytes(Path.Combine(outDir, n))).ToArray());
    }

    [Theory]
    [InlineData("defaultNeko")]         // 출력 폴더를 비웠다 — '출력 폴더를 고정해 … 비워 주세요'는 틀린 말이다
    [InlineData("fixedSameFolder")]     // 출력 폴더를 고정했어도 같은 폴더의 두 원고면 폴더 이름이 같은 화가 섞인 것이 아니다
    [InlineData("fixedOtherFolders")]   // 출력 폴더를 고정했고 폴더 이름이 같은 두 화 — 출력 폴더를 바꾸거나 비우면 풀린다
    public async Task RunAsync_LaterPageOnSlotTakenByEarlierPage_AdviceMatchesTheCause(string state)
    {
        // 01.jpg와 01_jpg.png는 어느 출력 폴더에서나 같은 결과 자리(01_jpg.png)다 — 앞 장이 차지한 자리라는 안내가 늘 출력 폴더 고정을 탓했다
        using var t = new TempDir();
        var first = WriteImageFile(t.At("A", "vol1", "01.jpg"), seed: 1);
        var second = WriteImageFile(t.At(state == "fixedOtherFolders" ? "B" : "A", "vol1", "01_jpg.png"), seed: 2);
        var s = EnginelessSettings(t, state == "defaultNeko" ? "" : t.At("결과"));
        Assert.Equal(PipelineRunner.OutputPathFor(s, first), PipelineRunner.OutputPathFor(s, second));   // 전제: 둘이 같은 결과 자리

        var log = await RunAsync(s, first, second);

        Assert.Null(log.FinalOf(first).Error);
        var error = log.FinalOf(second).Error;
        Assert.NotNull(error);
        if (state == "fixedOtherFolders")
        {
            Assert.Contains(first, error);
            Assert.Contains("출력 폴더를 바꿔 따로 돌리거나 비워", error);
        }
        else
        {
            Assert.DoesNotContain("출력 폴더", error);
            Assert.Contains("같은 폴더의 다른 원고(01.jpg)", error);
            Assert.Contains("파일 이름을 바꿔", error);
        }
    }

    [Theory]
    [InlineData("A")]       // 자리의 사이드카 주인 A가 목록에서 B 뒤에 있다(이름이 같으면 올린 순서 그대로다) — B는 실패, 주인 A는 제 자리를 돈다
    [InlineData("other")]   // 목록 밖의 다른 원고 X의 사이드카 — 아무도 자리를 차지하지 못했으니 A도 'B와 같다'가 아니라 X 탓으로 실패한다
    [InlineData("none")]    // 사이드카가 없다 — 먼저 온 B가 자리를 차지하고 같이 돌린 A는 실패(지금까지와 같다)
    public async Task RunAsync_TwoListedPagesOnOneSlot_OnlyThePageThatTakesTheSlotBlocksTheOther(string sidecarOf)
    {
        // 같은 자리로 떨어지는 앞 장을 목록 순서만으로 정하면 [B, A]에서 B는 A의 사이드카에, A는 '앞 장 B'에 막혀 아무도 안 썼는데 주인도 못 돌았다
        using var t = new TempDir();
        var (a, b, s, outPath) = SharedSlot(t);
        var x = WriteImageFile(t.At("X", "vol1", "01.jpg"), seed: 3, width: 62);
        Assert.NotEqual(new FileInfo(x).Length, new FileInfo(a).Length);   // 전제: X도 이름만 같은 다른 원고
        Assert.NotEqual(new FileInfo(x).Length, new FileInfo(b).Length);
        if (sidecarOf == "A") WriteMaskSidecarOf(a, outPath);
        if (sidecarOf == "other") WriteMaskSidecarOf(x, outPath);

        var log = await RunAsync(s, b, a);

        var errorB = log.FinalOf(b).Error;
        var errorA = log.FinalOf(a).Error;
        if (sidecarOf == "A")
        {
            Assert.Contains(a, errorB);
            Assert.Null(errorA);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, a)));
        }
        else if (sidecarOf == "other")
        {
            Assert.Contains(x, errorB);
            Assert.Contains(x, errorA);
            Assert.DoesNotContain(b, errorA);
        }
        else
        {
            Assert.Null(errorB);
            Assert.Contains(b, errorA);
            Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(PipelineRunner.ResolveResultPath(outPath, b)));
        }
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
            Assert.Equal([OwnerFile("01_jpg"), "01.png", "01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
            Assert.Equal(legacyBytes, File.ReadAllBytes(legacy));
        }
        else Assert.Equal([OwnerFile("01_jpg"), "01_jpg.jpg"], FilesUnder(t.At("vol1", "vol1_neko")));
    }

    [Theory]
    [InlineData("asRecorded")]         // 사이드카에 적힌 경로에 01.webp가 그대로 있다
    [InlineData("movedFolder")]        // v1.5.5 뒤에 원고 폴더를 옮겼다 — 적힌 경로는 옛 경로지만 이 폴더의 01.webp가 이름+길이로 그 원고다(없는 원고라고 하면 안 된다)
    [InlineData("siblingSlotTaken")]   // 01.webp의 새 이름 자리에 이미 사이드카가 있다 — 옛 이름 옮기기는 그 자리를 덮지 않으니 '먼저 돌리면 옮겨진다'는 틀린 말이 된다
    public async Task RunAsync_LegacyNameDataOfSameNameSiblingInSlot_AdvisesRunningThatSourceFirst(string state)
    {
        // v1.5.5까지는 결과 이름이 원고 이름 그대로라 01.webp의 결과·사이드카가 기본 _neko의 01.png·.neko\01.*였다 — 지금 그 자리는 01.png 원고의 것이다.
        // 이름순(01.png가 앞)으로 같이 돌려도 01.png가 01.webp의 옛 이름 옮기기보다 먼저 걸린다. 지우라고 하면 아직 옮기지 않은 01.webp의 고친 마스크가
        // 사라지므로 01.webp부터 돌리라고 안내하고, 그대로 하면 01.png도 돈다
        using var t = new TempDir();
        var png = WriteImageFile(t.At("vol1", "01.png"), seed: 1);
        var webp = WriteImageFile(t.At("vol1", "01.webp"), seed: 2);
        var s = EnginelessSettings(t);
        var legacyOut = t.At("vol1", "vol1_neko", "01.png");
        Assert.Equal(legacyOut, PipelineRunner.OutputPathFor(s, png));   // 전제: 01.webp의 옛 결과 자리가 01.png의 새 결과 자리
        WriteImageFile(legacyOut, seed: 7);
        WriteMaskSidecarOf(webp, legacyOut);
        if (state == "movedFolder") (MaskSidecar.Load(legacyOut)! with { Source = t.At("옮기기 전", "vol1", "01.webp") }).Save(legacyOut);
        if (state == "siblingSlotTaken") WriteMaskSidecarOf(webp, PipelineRunner.OutputPathFor(s, webp));

        var log = await RunAsync(s, png, webp);

        var error = log.FinalOf(png).Error;
        Assert.NotNull(error);
        Assert.Contains(webp, error);
        Assert.DoesNotContain("지금 없어도", error);
        Assert.Null(log.FinalOf(webp).Error);
        if (state == "siblingSlotTaken")
        {
            Assert.DoesNotContain("부터 한 번 돌리면", error);
            Assert.Contains("지운 뒤 다시 돌려", error);
            return;
        }
        Assert.Contains("01.webp부터 한 번 돌리면", error);
        Assert.DoesNotContain("지운 뒤", error);
        Assert.True(MaskSidecar.Load(PipelineRunner.OutputPathFor(s, webp))?.MatchesSource(webp));   // 같이 돌린 01.webp가 옛 이름 자료를 새 이름으로 옮겼다
        Assert.Null(MaskSidecar.Load(legacyOut));

        var again = await RunAsync(s, png);

        Assert.Null(again.FinalOf(png).Error);
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
