using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>결과 창 [지우기] — 한 장만 검열 제거·식자를 다시 하는 길(RedoPageAsync + RedoPageOptions).
/// 채색은 돌리지 않으므로, 채색된 장은 base(채색 뒤·식자 전)에서, 아니면 pre(검열 제거 전 원고)에서 지운다.</summary>
public class PipelineRunnerRedoTests
{
    // ── LooksColorized: base가 pre에서 (마스크 바깥이) 달라졌으면 채색된 장 ──

    [Fact]
    public void LooksColorized_SameImage_False()
    {
        using var a = ColorImage(60, 40, 1); using var b = a.Clone();
        Assert.False(PipelineRunner.LooksColorized(a, b, null));
    }

    [Fact]
    public void LooksColorized_DifferentSize_True()
    {
        using var a = ColorImage(60, 40, 1); using var b = ColorImage(90, 60, 1);
        Assert.True(PipelineRunner.LooksColorized(a, b, null));
    }

    [Fact]
    public void LooksColorized_DiffersOnlyInsideMask_False()
    {
        // 검열 제거가 마스크 안만 바꾼 base는 채색된 것이 아니다
        using var a = ColorImage(60, 40, 1); using var b = a.Clone();
        var r = new Rect(5, 5, 10, 10); b.Rectangle(r, new Scalar(0, 255, 0), -1);
        using var m = RectMask(60, 40, r);
        Assert.False(PipelineRunner.LooksColorized(a, b, m));
        Assert.True(PipelineRunner.LooksColorized(a, b, null));
    }

    [Fact]
    public void LooksColorized_ShiftedOutsideMask_True()
    {
        using var a = ColorImage(60, 40, 1); using var b = ColorImage(60, 40, 2);
        using var m = RectMask(60, 40, new Rect(5, 5, 10, 10));
        Assert.True(PipelineRunner.LooksColorized(a, b, m));
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

    /// <summary>저장 마스크·pre·base·식자 블록이 다 있는 결과 자리. baseSeed가 pre(seed 4)와 같으면 채색 안 한 장, 다르면 채색된 장.</summary>
    private static (string src, string outPath, Mat pre, Mat baseImg) Page(TempDir t, AppSettings s, int baseSeed)
    {
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(60, 40, new Rect(5, 5, 10, 10));
        var pre = ColorImage(60, 40, seed: 4);
        MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: false, edited: true, confirmed: false);
        var baseImg = ColorImage(60, 40, seed: baseSeed);
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
