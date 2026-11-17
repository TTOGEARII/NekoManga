using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>결과 창 [지우기] — 한 장만 검열 제거·식자를 다시 하는 길(RedoPageAsync + RedoPageOptions).
/// 채색은 돌리지 않으므로, 채색된 장은 base(채색 뒤·식자 전)에서, 아니면 pre(검열 제거 전 원고)에서 지운다.</summary>
public class PipelineRunnerRedoTests
{
    // ── LooksColorized: 흑백 원고에 색이 입혀졌으면(마스크 밖 채도가 늘었으면) 채색된 장 ──
    // 2026-11-13까지는 마스크 밖의 평균 절대차로 봤다 — 부위 통째 재생성은 마스크 밖도 바꿔서 실제 재생성 결과 4쪽 중 3쪽이 '채색'으로 읽혔다(절대차 1.1~3.9, 한계 2.0).
    // 채도(max−min)는 재생성에서 0.03~0.21, 채색 결과에서 12.1 이상(실측) — 한계 3.0

    /// <summary>흑백 원고 대역 — 세 채널 값이 같은 그림.</summary>
    private static Mat GrayPage(int width = 60, int height = 40)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, new Scalar(235, 235, 235));
        img.Rectangle(new Rect(width / 6, height / 5, width / 3, height / 4), new Scalar(20, 20, 20), -1);
        img.Line(0, height - 1, width - 1, 0, new Scalar(90, 90, 90), 2);
        return img;
    }

    /// <summary>채색 결과 대역 — 같은 그림에 색을 입힌 것(밝은 바탕의 채도 90).</summary>
    private static Mat Tinted(Mat gray)
    {
        var c = new Mat();
        Cv2.Subtract(gray, new Scalar(90, 40, 0), c);
        return c;
    }

    /// <summary>재생성 결과 대역 — 마스크 밖 한 덩어리의 밝기가 바뀌었지만 여전히 흑백인 그림.</summary>
    private static Mat Regenerated(Mat gray, Rect around)
    {
        var r = gray.Clone();
        r.Rectangle(around, new Scalar(128, 128, 128), -1);
        return r;
    }

    [Fact]
    public void LooksColorized_SameImage_False()
    {
        using var a = GrayPage(); using var b = a.Clone();
        Assert.False(PipelineRunner.LooksColorized(a, b, null));
        using var c = ColorImage(60, 40, 1); using var d = c.Clone();   // 원래 컬러인 원고도 그대로면 채색된 것이 아니다
        Assert.False(PipelineRunner.LooksColorized(c, d, null));
    }

    [Fact]
    public void LooksColorized_DifferentSize_True()
    {
        using var a = GrayPage(60, 40); using var b = GrayPage(90, 60);
        Assert.True(PipelineRunner.LooksColorized(a, b, null));
    }

    [Fact]
    public void LooksColorized_GrayPageTurnedColor_True()
    {
        using var pre = GrayPage(); using var colored = Tinted(pre);
        using var m = RectMask(60, 40, new Rect(5, 5, 10, 10));
        Assert.True(PipelineRunner.LooksColorized(pre, colored, m));
        Assert.True(PipelineRunner.LooksColorized(pre, colored, null));
    }

    [Fact]
    public void LooksColorized_RegeneratedBeyondTheMask_False()
    {
        // 부위 통째 재생성은 마스크보다 넓게 다시 그린다 — 마스크 밖이 달라졌어도 색이 입혀진 것은 아니다
        using var pre = GrayPage();
        using var regen = Regenerated(pre, new Rect(20, 10, 30, 25));
        using var m = RectMask(60, 40, new Rect(30, 15, 10, 10));
        using var diff = new Mat(); Cv2.Absdiff(pre, regen, diff);
        Assert.True(Cv2.Mean(diff).Val0 > 2.0, "전제: 옛 기준(평균 절대차 2.0)으로는 채색으로 읽히는 그림이다");
        Assert.False(PipelineRunner.LooksColorized(pre, regen, m));
    }

    [Fact]
    public void LooksColorized_ColorOnlyInsideMask_False()
    {
        // 마스크 안은 검열 제거가 바꾼 자리다 — 거기에 색이 들어가도 장이 채색된 것은 아니다
        using var pre = GrayPage(); using var b = pre.Clone();
        var r = new Rect(5, 5, 20, 20); b.Rectangle(r, new Scalar(0, 255, 0), -1);
        using var m = RectMask(60, 40, r);
        Assert.False(PipelineRunner.LooksColorized(pre, b, m));
        Assert.True(PipelineRunner.LooksColorized(pre, b, null));
    }

    [Fact]
    public void IsColorized_RecordedFlagWinsOverThePixels()
    {
        using var t = new TempDir();
        using var pre = GrayPage(); using var colored = Tinted(pre);
        using var m = RectMask(60, 40, new Rect(5, 5, 10, 10));
        var unknown = MaskSidecar.Write(t.At("o", "01.png"), t.At("01.jpg"), m, null, null, "감지", skip: false, edited: false, confirmed: true);
        Assert.Null(unknown.Colorized);
        Assert.True(PipelineRunner.IsColorized(unknown, pre, colored, m));     // 모르면 그림으로
        Assert.False(PipelineRunner.IsColorized(unknown, pre, pre, m));
        Assert.False(PipelineRunner.IsColorized(unknown with { Colorized = false }, pre, colored, m));
        Assert.True(PipelineRunner.IsColorized(unknown with { Colorized = true }, pre, pre, m));
    }

    // ── Grown: 여유(px)만큼 부풀린 사본 ──

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Grown_DilatesByGrow(int grow)
    {
        using var m = RectMask(60, 40, new Rect(20, 10, 10, 10));
        using var g = PipelineRunner.Grown(m, grow);
        Assert.Equal(grow > 0 ? 255 : 0, g.At<byte>(10, 18));   // 왼쪽 2px 밖
        Assert.Equal(0, g.At<byte>(10, 20 - grow - 2));          // 커널 밖은 그대로
        Assert.Equal(255, g.At<byte>(15, 25));
        Assert.Equal(255, m.At<byte>(15, 25)); Assert.Equal(0, m.At<byte>(10, 18));   // 원본은 그대로
    }


    // ── RedoPageAsync + DecensorAndLetterOnly: 엔진 없는 설정(제거·채색이 안 돈다)이라 입력 선택과 식자만 본다 ──

    /// <summary>저장 마스크·pre·base·식자 블록이 다 있는 결과 자리. pre는 흑백 원고, base는 baseSeed가 4면 pre 그대로(채색 안 한 장), 아니면 색을 입힌 그림(채색된 장).
    /// 사이드카에 채색 여부는 적혀 있지 않다(v1.9.1까지의 결과) — <paramref name="colorized"/>를 주면 적는다.</summary>
    private static (string src, string outPath, Mat pre, Mat baseImg) Page(TempDir t, AppSettings s, int baseSeed, bool? colorized = null)
    {
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        var pre = GrayPage();
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false, colorized: colorized);
        var baseImg = baseSeed == 4 ? pre.Clone() : Tinted(pre);
        ImgIo.Write(LetterSidecar.BasePathFor(outPath), baseImg);
        new LetterSidecar(src, 60, 40, LetterSidecar.BasePathFor(outPath),
            [new TextBlockInfo(5, 5, 50, 30, "あ", "아", 12, false, "#000000")], MaskSidecar.LengthOf(src)).Save(outPath);
        return (src, outPath, pre, baseImg);
    }

    private static readonly PipelineRunner.RedoPageOptions DecensorOnly = new(DecensorAndLetterOnly: true);

    [Fact]
    public async Task RedoPageAsync_SkipColor_ColorizedPage_RendersFromBase()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5);   // base ≠ pre → 채색된 장
        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);
        using var outImg = ImgIo.Read(outPath);
        Assert.Equal(baseImg.At<Vec3b>(38, 58), outImg.At<Vec3b>(38, 58));   // 글 밖 구석은 base 픽셀 — 색을 잃지 않는다
        Assert.NotEqual(pre.At<Vec3b>(38, 58), outImg.At<Vec3b>(38, 58));
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_SkipColor_UncoloredPage_RendersFromPre()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);   // base == pre → 채색 안 한 장 (잘못 지운 곳이 되살아나게 pre에서)
        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);
        using var outImg = ImgIo.Read(outPath);
        Assert.Equal(pre.At<Vec3b>(38, 58), outImg.At<Vec3b>(38, 58));
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_SkipColor_SkipFlag_LettersWithoutDecensor()
    {
        // '검열 없음' 표시 장 — 제거를 건너뛰고 식자만 (채색된 장이면 base에서)
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5);
        var sc = MaskSidecar.Load(outPath)!; (sc with { Skip = true }).Save(outPath);
        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);
        using var outImg = ImgIo.Read(outPath);
        Assert.Equal(baseImg.At<Vec3b>(38, 58), outImg.At<Vec3b>(38, 58));
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_NoOptions_BehavesAsBefore_RendersFromPre()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5);
        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None);
        using var outImg = ImgIo.Read(outPath);
        Assert.Equal(pre.At<Vec3b>(38, 58), outImg.At<Vec3b>(38, 58));
        pre.Dispose(); baseImg.Dispose();
    }


    [Fact]
    public async Task RedoPageAsync_KeepsTheFrozenCommonSize_OfTheLetterSidecar()
    {
        // 결과 창이 붙든 쪽 공통 글자 크기(LetterSidecar.CommonSize)는 다시 렌더링의 식자에도 쓰이고 사이드카에 남는다 —
        // 안 그러면 다시 렌더링한 장에서 손대지 않은 풍선의 글자 크기가 다시 중앙값으로 돌아간다(2026-11-17)
        using var t = new TempDir(); var s = EnginelessSettings(t);
        s.LetterFontFamily = "Arial";
        const int W = 600, H = 800;
        var src = t.At("vol1", "01.png");
        using var pre = new Mat(H, W, MatType.CV_8UC3, new Scalar(200, 200, 200));
        ImgIo.Write(src, pre);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using (var mask = RectMask(W, H, new Rect(5, 5, 10, 10)))
            MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false, colorized: false);
        ImgIo.Write(LetterSidecar.BasePathFor(outPath), pre);
        ImgIo.Write(t.At("pre.png"), pre);
        TextBlockInfo[] blocks =
        [
            new(50, 50, 550, 300, "あ", "HHHH", 12, false, "#000000", ManualRect: true),
            new(50, 450, 550, 700, "あ", "HHHH", 12, false, "#000000", ManualRect: true),
        ];
        new LetterSidecar(src, W, H, LetterSidecar.BasePathFor(outPath), [.. blocks], MaskSidecar.LengthOf(src), CommonSize: 9).Save(outPath);

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        Assert.Equal(9, LetterSidecar.Load(outPath)!.CommonSize);
        TextRenderer.RenderBlocks(t.At("pre.png"), src, W, H, blocks, t.At("frozen.png"), s, 9);
        TextRenderer.RenderBlocks(t.At("pre.png"), src, W, H, blocks, t.At("auto.png"), s);
        using var outImg = ImgIo.Read(outPath);
        using var frozen = ImgIo.Read(t.At("frozen.png"));
        using var auto = ImgIo.Read(t.At("auto.png"));
        using var diff = new Mat(); Cv2.Absdiff(frozen, auto, diff);
        Assert.True(Cv2.Sum(diff).Val0 > 0, "붙든 크기와 자동 크기의 그림이 같으면 이 시험은 아무것도 가리지 못한다");
        AssertSamePixels(frozen, outImg);
    }


    // ── 채색 없이 다시 렌더링할 때의 시작 그림 (2026-11-13 점검 1·3번) ──

    [Fact]
    public async Task RedoPageAsync_SkipColor_RegeneratedLetteredPage_RendersFromPre()
    {
        // 재생성으로 끝난 장의 base는 마스크 밖도 pre와 다르다 — 그것을 '채색'으로 읽으면 이미 재생성된 그림 위에서 또 지우고, 마스크에서 뺀 자리가 원래 그림으로 돌아오지 않는다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        using (var regen = Regenerated(pre, new Rect(20, 28, 38, 10))) ImgIo.Write(LetterSidecar.BasePathFor(outPath), regen);   // 글 아래쪽, 마스크 밖

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        Assert.Equal(pre.At<Vec3b>(35, 56), outImg.At<Vec3b>(35, 56));   // 재생성이 바꿨던 자리가 검열 제거 전 원고로 돌아왔다
        Assert.False(MaskSidecar.Load(outPath)!.Colorized);
        pre.Dispose(); baseImg.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedoPageAsync_SkipColor_RecordedFlagDecidesTheStartImage(bool colorized)
    {
        // 그때 채색을 돌렸는지 사이드카에 적혀 있으면 그림으로 추측하지 않는다 — base가 어떻게 보이든 적힌 대로
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: colorized ? 4 : 5, colorized);   // 일부러 그림과 반대로 적었다
        using (var regen = Regenerated(pre, new Rect(20, 28, 38, 10)))
            if (colorized) { ImgIo.Write(LetterSidecar.BasePathFor(outPath), regen); regen.CopyTo(baseImg); }   // 색 없는 base인데 '채색됨'으로 적힌 장

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        Assert.Equal((colorized ? baseImg : pre).At<Vec3b>(35, 56), outImg.At<Vec3b>(35, 56));
        Assert.Equal(colorized, MaskSidecar.Load(outPath)!.Colorized);   // 적힌 값은 다시 렌더링 뒤에도 그대로
        pre.Dispose(); baseImg.Dispose();
    }

    /// <summary>글자가 없어 식자 사이드카·base 없이 끝난 장 — 저장 마스크와 pre, 그리고 결과물만 있다.</summary>
    private static (string src, string outPath, Mat pre) TextlessPage(TempDir t, AppSettings s, Func<Mat, Mat> result, bool? colorized)
    {
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        var pre = GrayPage();
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: true, colorized: colorized);
        using var res = result(pre);
        ImgIo.Write(outPath, res);
        return (src, outPath, pre);
    }

    [Theory]
    [InlineData(true)]    // 2차가 '채색했다'고 적어 둔 장
    [InlineData(null)]    // 옛 결과(적힌 것 없음) — 결과물의 색으로 안다
    public async Task RedoPageAsync_SkipColor_ColorizedPageWithoutText_KeepsItsColor(bool? recorded)
    {
        // 글자 없는 장(번역을 끄고 채색만 쓴 장도)은 식자 사이드카·base가 없다 — pre에서 시작하면 채색을 끄고 도는 다시 렌더링이 결과물을 흑백으로 덮는다(재현 2026-11-13)
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre) = TextlessPage(t, s, Tinted, recorded);
        using var colored = ImgIo.Read(outPath);

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        AssertSamePixels(colored, outImg);   // 엔진 없는 설정이라 검열 제거는 안 돈다 — 결과물이 그대로여야 한다(흑백 pre로 바뀌지 않았다)
        Assert.True(MaskSidecar.Load(outPath)!.Colorized);
        Assert.Null(LetterSidecar.LoadRecord(outPath));   // 없던 식자 사이드카를 만들지 않는다
        pre.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_SkipColor_RegeneratedPageWithoutText_RendersFromPre()
    {
        // 지금 사용자의 설정(재생성, 번역·채색 끔)으로 만든 결과 — 결과물이 마스크 밖까지 pre와 달라도 채색된 것이 아니다. 결과물에서 시작하면 재생성 위에 또 재생성한다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre) = TextlessPage(t, s, p => Regenerated(p, new Rect(20, 20, 38, 18)), colorized: null);

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        AssertSamePixels(pre, outImg);
        Assert.False(MaskSidecar.Load(outPath)!.Colorized);
        pre.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_SkipColor_RecordedColorizedButResultIsGone_RendersFromPre()
    {
        // 적힌 것은 '채색됨'인데 채색된 그림(결과물)이 지워졌다 — 시작할 그림이 pre뿐이다. 끝난 뒤의 기록은 실제 결과물대로
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre) = TextlessPage(t, s, Tinted, colorized: true);
        File.Delete(outPath);

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        AssertSamePixels(pre, outImg);
        Assert.False(MaskSidecar.Load(outPath)!.Colorized);
        pre.Dispose();
    }


    // ── 식자 전 원고(base)를 못 찾을 때: 글이 빠진 결과물로 덮지 않는다 (2026-11-13 점검 2번) ──

    /// <summary>두 그림이 사각형 안에서 다른 픽셀 수 — 글이 그려졌는지 볼 때.</summary>
    private static int DiffIn(Mat a, Mat b, Rect r)
    {
        using var ra = new Mat(a, r); using var rb = new Mat(b, r);
        using var d = new Mat(); Cv2.Absdiff(ra, rb, d);
        using var flat = d.Reshape(1);
        return Cv2.CountNonZero(flat);
    }

    [Fact]
    public async Task RedoPageAsync_FolderMovedAfterTheRun_StillLettersThePage()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        Directory.Move(t.At("vol1"), t.At("옮긴 곳"));   // 원고·결과 폴더가 통째로 — 식자 json의 BasePath는 옛 경로 그대로다
        var movedSrc = t.At("옮긴 곳", "01.jpg");
        var movedOut = Path.Combine(t.At("옮긴 곳"), Path.GetRelativePath(t.At("vol1"), outPath));

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(movedSrc, new ProgressLog(), CancellationToken.None, movedOut, DecensorOnly);

        using var outImg = ImgIo.Read(movedOut);
        Assert.True(DiffIn(pre, outImg, new Rect(5, 5, 45, 25)) > 0, "번역 글이 그려지지 않았다 — 식자 블록 없이 돌았다");
        Assert.Equal(LetterSidecar.BasePathFor(movedOut), LetterSidecar.Load(movedOut)!.BasePath);   // 다시 쓴 사이드카는 새 자리를 가리킨다
        pre.Dispose(); baseImg.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedoPageAsync_LetteredPageWhoseBaseIsGone_StopsWithoutTouchingTheResult(bool decensorOnly)
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        WriteImageFile(outPath, seed: 9);   // 지난 결과물(글이 들어 있다고 치자)
        var before = File.ReadAllBytes(outPath);
        File.Delete(LetterSidecar.BasePathFor(outPath));

        using (var runner = new PipelineRunner(s))
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: decensorOnly ? DecensorOnly : null));
            Assert.Contains("식자 전 원고", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(outPath));
        Assert.NotNull(LetterSidecar.LoadRecord(outPath));   // 고쳐 둔 번역 글은 남는다
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_BaseGoneButEveryBlockHidden_RendersWithoutText()
    {
        // 보일 글이 하나도 없으면 base가 없어도 잃을 것이 없다 — 막지 않는다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        var rec = LetterSidecar.LoadRecord(outPath)!;
        (rec with { Blocks = [.. rec.Blocks.Select(b => b with { Hidden = true })] }).Save(outPath);
        File.Delete(LetterSidecar.BasePathFor(outPath));

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);

        using var outImg = ImgIo.Read(outPath);
        AssertSamePixels(pre, outImg);
        pre.Dispose(); baseImg.Dispose();
    }


    // ── 식자 json이 있는데 못 읽을 때: '없음'으로 치지 않는다 (2026-11-14 검수) ──
    // 결과 창은 [지우기] 직전에 식자 json을 새로 쓴다 — 그 파일을 백신·동기화가 잠깐 쥐면 읽기가 null이고, 그것을 '식자 없는 장'으로 치면
    // 번역 글도 색도 없는 pre 기반 그림으로 결과물을 덮은 뒤 '채색 안 함'까지 적는다(다음부터는 그 기록을 따라 흑백에서 시작한다)

    [Theory]
    [InlineData("cut")]
    [InlineData("empty")]
    public async Task RedoPageAsync_LetterJsonBroken_StopsWithoutTouchingTheResult(string how)
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5, colorized: true);
        WriteImageFile(outPath, seed: 9);
        var before = File.ReadAllBytes(outPath);
        var json = LetterSidecar.JsonPathFor(outPath);
        var text = File.ReadAllText(json);
        File.WriteAllText(json, how == "cut" ? text[..(text.Length / 2)] : "");

        using (var runner = new PipelineRunner(s))
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly));
            Assert.Contains("식자 정보", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(outPath));
        Assert.True(MaskSidecar.Load(outPath)!.Colorized);   // '채색 안 함'으로 바뀌지 않았다
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_LetterJsonBrieflyLocked_WaitsAndLettersThePage()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        // 다른 프로그램처럼 쥐었다가 0.15초 뒤에 놓는다 — 전용 스레드로(스레드풀로 놓으면 느린 CI에서 다시 읽기보다 늦는다)
        var held = new FileStream(LetterSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); }) { IsBackground = true };
        release.Start();

        using (var runner = new PipelineRunner(s))
            await runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly);
        release.Join();

        using var outImg = ImgIo.Read(outPath);
        Assert.True(DiffIn(pre, outImg, new Rect(5, 5, 45, 25)) > 0, "번역 글이 그려지지 않았다 — 잠긴 식자 json을 '없음'으로 쳤다");
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_LetterJsonStaysLocked_StopsAndSaysToTryAgain()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 4);
        WriteImageFile(outPath, seed: 9);
        var before = File.ReadAllBytes(outPath);

        using (new FileStream(LetterSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var runner = new PipelineRunner(s))
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly));
            Assert.Contains("잠시 뒤", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(outPath));
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_SkipColor_RecordedColorizedButLetterDataCannotBeUsed_StopsInsteadOfGoingMonochrome()
    {
        // 적힌 것은 '채색됨'인데 식자 자료가 있으면서 못 쓴다(글이 전부 숨김이고 base가 지워짐) — 결과물에는 글이 구워져 있을 수 있어 거기서 시작하지도 못한다.
        // pre에서 시작하면 채색본을 흑백으로 덮는다: 멈춘다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5, colorized: true);
        WriteImageFile(outPath, seed: 9);
        var before = File.ReadAllBytes(outPath);
        var rec = LetterSidecar.LoadRecord(outPath)!;
        (rec with { Blocks = [.. rec.Blocks.Select(b => b with { Hidden = true })] }).Save(outPath);
        File.Delete(LetterSidecar.BasePathFor(outPath));

        using (var runner = new PipelineRunner(s))
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly));
            Assert.Contains("채색", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(outPath));
        Assert.True(MaskSidecar.Load(outPath)!.Colorized);
        pre.Dispose(); baseImg.Dispose();
    }

    [Fact]
    public async Task RedoPageAsync_ResultCannotBeWritten_DoesNotLeaveTheOldColorizedRecordBehind()
    {
        // 적힌 채색 여부가 이번 결과와 다르면 결과물을 쓰기 전에 '모름'으로 내려 둔다 — 쓰다 실패하거나 그 직후에 앱이 꺼져도 옛 값이 남아 다음 다시 렌더링을 속이지 않게
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre) = TextlessPage(t, s, Tinted, colorized: true);
        File.Delete(outPath);                  // 채색된 결과물이 지워졌다 → pre에서 시작한다(이번 결과는 채색본이 아니다)
        Directory.CreateDirectory(outPath);    // 결과물 자리에 쓸 수 없게

        using (var runner = new PipelineRunner(s))
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RedoPageAsync(src, new ProgressLog(), CancellationToken.None, options: DecensorOnly));

        Assert.Null(MaskSidecar.Load(outPath)!.Colorized);
        pre.Dispose();
    }

    [Fact]
    public void NextOutputPrefix_IsDifferentForEveryPrompt()
    {
        // ComfyUI는 직전과 똑같은 그래프면 아무 노드도 다시 돌리지 않고 지난번 SaveImage 결과(파일 이름)를 그대로 답한다(실측 2026-11-14) — 그 파일은 우리가 이미 지웠다.
        // 접두사가 프롬프트마다 다르면 SaveImage만 다시 돌아 새 파일이 생긴다. 러너끼리도 겹치지 않아야 한다(다른 창의 출력을 지우지 않게)
        using var t = new TempDir(); var s = EnginelessSettings(t);
        using var a = new PipelineRunner(s); using var b = new PipelineRunner(s);
        var names = new[] { a.NextOutputPrefix("regenout"), a.NextOutputPrefix("regenout"), a.NextOutputPrefix("color"), b.NextOutputPrefix("regenout") };
        Assert.Equal(4, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^nm_(regenout|color)_[0-9a-f]{4}_[0-9]+$", n));
        Assert.DoesNotContain(names.Skip(1), n => n.StartsWith(names[0] + "_"));   // 한 접두사가 다른 접두사의 앞머리가 되면 그것으로 훑어 지울 때 남의 파일이 걸린다
    }


    // ── 취소: 단계 경계에서 멈추고 결과물·사이드카는 그대로 ──

    [Fact]
    public async Task RedoPageAsync_AlreadyCancelled_ThrowsAndLeavesResultUntouched()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var (src, outPath, pre, baseImg) = Page(t, s, baseSeed: 5);
        WriteImageFile(outPath, seed: 9);                        // 지난 결과물
        var before = File.ReadAllBytes(outPath);
        var jsonBefore = File.ReadAllText(MaskSidecar.JsonPathFor(outPath));
        using var cts = new CancellationTokenSource(); cts.Cancel();

        using (var runner = new PipelineRunner(s))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RedoPageAsync(src, new ProgressLog(), cts.Token, options: DecensorOnly));

        Assert.Equal(before, File.ReadAllBytes(outPath));
        Assert.Equal(jsonBefore, File.ReadAllText(MaskSidecar.JsonPathFor(outPath)));
        pre.Dispose(); baseImg.Dispose();
    }


    // ── RegenMaskFor: 재생성·커스텀 직전에 마스크를 '부위 전체'로 넓히는 길 (모델·ComfyUI 없이 — detector null = 확장 경로) ──

    [Fact]
    public void RegenMaskFor_WholeRegionOff_ReturnsSameMask()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t); s.RegenWholeRegion = false;
        using var img = ColorImage(200, 120); using var mask = RectMask(200, 120, new Rect(40, 40, 20, 4));
        using var runner = new PipelineRunner(s);
        using var r = runner.RegenMaskFor(img, mask, null);
        AssertSamePixels(mask, r);
    }

    [Fact]
    public void RegenMaskFor_NoDetector_ExpandsComponents()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        using var img = ColorImage(200, 120); using var mask = RectMask(200, 120, new Rect(40, 40, 20, 4));
        using var runner = new PipelineRunner(s);
        using var r = runner.RegenMaskFor(img, mask, null);
        Assert.True(Cv2.CountNonZero(r) > Cv2.CountNonZero(mask) * 5);   // 조각 20×4=80px → 0.6배 확장 타원(좌우 12, 위아래 8)은 수백 px
        Assert.Equal(255, r.At<byte>(42, 50));
        Assert.Equal(0, r.At<byte>(100, 150));
    }

    [Fact]
    public void RegenMaskFor_UsesGivenSettings()
    {
        // 결과 창의 '이 페이지만' 재생성 값(설정 사본)을 넘기면 그 배율을 쓴다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        using var img = ColorImage(200, 120); using var mask = RectMask(200, 120, new Rect(40, 40, 20, 4));
        using var runner = new PipelineRunner(s);
        var wider = s.ShallowClone(); wider.RegenRegionGrow = 2.0;
        using var r1 = runner.RegenMaskFor(img, mask, null);
        using var r2 = runner.RegenMaskFor(img, mask, null, wider);
        Assert.True(Cv2.CountNonZero(r2) > Cv2.CountNonZero(r1) * 2);
    }


    [Fact]
    public void DetectBodyParts_NoModel_ReturnsEmpty()
    {
        // 결과 창 미리보기의 페이지별 검출 캐시용 — 모델이 없으면 빈 목록(예외 없음), 그러면 확장 경로만 그려진다
        using var t = new TempDir(); var s = EnginelessSettings(t);
        using var img = ColorImage(200, 120);
        using var runner = new PipelineRunner(s);
        Assert.Empty(runner.DetectBodyParts(img));
    }


    // ── 재생성 v3: 영역 계획 (모델·ComfyUI 없이 — detector null = 확장 경로) ──

    [Theory]
    [InlineData(false, 1)]   // 전과 같다: 상대 창 안에 보이면 한 영역
    [InlineData(true, 2)]    // 떨어진 자리는 따로(2026-11-16) — 붙이는 범위가 안 겹친다
    public void RegenPlanFor_SplitRegionsSetting_DecidesWhetherPiecesApartShareARegion(bool split, int expected)
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        s.RegenSplitRegions = split;
        using var img = ColorImage(3000, 4000); using var mask = RectMask(3000, 4000, new Rect(1000, 2000, 200, 40));
        mask.Rectangle(new Rect(1500, 2000, 200, 40), Scalar.White, -1);
        using var runner = new PipelineRunner(s);
        var plan = runner.RegenPlanFor(img, mask, null);
        try { Assert.Equal(expected, plan.Count); }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public void RegenPlanFor_NoDetector_OneRegionPerPiece_Unknown()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        using var img = ColorImage(3000, 4000); using var mask = RectMask(3000, 4000, new Rect(200, 200, 200, 40));
        mask.Rectangle(new Rect(2500, 3500, 200, 40), Scalar.White, -1);
        using var runner = new PipelineRunner(s);
        var plan = runner.RegenPlanFor(img, mask, null);
        try
        {
            Assert.Equal(2, plan.Count);
            Assert.All(plan, r => { Assert.Equal(NekoManga.Core.Decensor.RegenKind.Unknown, r.Kind); Assert.True(Cv2.CountNonZero(r.Region) > Cv2.CountNonZero(r.Erase)); });
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public void RegenPlanFor_WholeRegionOff_RegionEqualsPiece()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t); s.RegenWholeRegion = false;
        using var img = ColorImage(3000, 4000); using var mask = RectMask(3000, 4000, new Rect(200, 200, 200, 40));
        using var runner = new PipelineRunner(s);
        var plan = runner.RegenPlanFor(img, mask, null);
        try { var r = Assert.Single(plan); Assert.Equal(Cv2.CountNonZero(r.Erase), Cv2.CountNonZero(r.Region)); }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    [Fact]
    public void CropAndScale_ScalesImageAndMasksToSendSize()
    {
        using var img = ColorImage(4000, 4000); using var region = RectMask(4000, 4000, new Rect(500, 500, 1500, 900)); using var erase = RectMask(4000, 4000, new Rect(1000, 900, 300, 40));
        var w = NekoManga.Core.Decensor.RegenCrop.WindowFor(new Rect(500, 500, 1500, 900), img.Size(), 1024);
        var (ci, cr, ce) = NekoManga.Core.Decensor.RegenCrop.CropAndScale(img, region, erase, w);
        using (ci) using (cr) using (ce)
        {
            Assert.Equal(new Size(w.SendW, w.SendH), ci.Size()); Assert.Equal(ci.Size(), cr.Size()); Assert.Equal(ci.Size(), ce.Size());
            Assert.Equal(MatType.CV_8UC3, ci.Type()); Assert.Equal(MatType.CV_8UC1, cr.Type());
            using var inv = new Mat(); Cv2.InRange(ce, new Scalar(1), new Scalar(254), inv);
            Assert.Equal(0, Cv2.CountNonZero(inv));   // 마스크는 0/255만 (Nearest)
            Assert.True(Cv2.CountNonZero(ce) > 0 && Cv2.CountNonZero(cr) > Cv2.CountNonZero(ce));
        }
    }
}
