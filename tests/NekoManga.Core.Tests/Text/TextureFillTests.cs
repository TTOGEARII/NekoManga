using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>무늬 있는 바탕(톤·줄무늬·속도선) 위의 글자 자리를 주변 무늬로 메우기(TextureFill, 2026-11-26 사용자 "복구된 부분이 부자연스러워" · "여기도 뭉개졌어").
/// LaMa 만화판은 줄무늬·망점을 잇지 못하고 회색으로 뭉갠다 — 무늬는 둘레에서 옮겨 온다(OpenCV xphoto의 shift-map). 모델이 없어 합성 쪽으로 다 볼 수 있다.</summary>
public class TextureFillTests
{
    /// <summary>가로 1px 검은 줄을 pitch px마다 그은 BGR 쪽 — 줄무늬 톤.</summary>
    private static Mat Stripes(int width, int height, int pitch = 4)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < height; y += pitch) img.Line(0, y, width - 1, y, Scalar.Black, 1);
        return img;
    }

    /// <summary>쪽 크기의 구멍 마스크와 창들로 메운다 — 러너는 블록마다 제 구멍을 따로 넘긴다(TextureFill.FillAll의 일감 꼴). 여기서는 창 안의 마스크를 그 창의 구멍으로 삼는다.</summary>
    private static bool[] FillAll(Mat img, Mat hole, Mat avoid, IReadOnlyList<Rect> areas, CancellationToken ct = default)
    {
        var bounds = new Rect(0, 0, img.Width, img.Height);
        var jobs = new List<(Rect Area, Mat Own)>();
        try
        {
            foreach (var a in areas)
            {
                var area = a & bounds;
                using var src = new Mat(hole, area);
                jobs.Add((area, src.Clone()));
            }
            return TextureFill.FillAll(img, jobs, avoid, ct);
        }
        finally { foreach (var (_, own) in jobs) own.Dispose(); }
    }

    private static bool TryFill(Mat img, Mat hole, Mat avoid, Rect area) => FillAll(img, hole, avoid, [area])[0];

    /// <summary>rect 안에서 두 그림이 눈에 띄게(40 넘게) 다른 픽셀의 비율.</summary>
    private static double Mismatch(Mat a, Mat b, Rect rect)
    {
        using var ra = new Mat(a, rect); using var rb = new Mat(b, rect);
        using var diff = new Mat(); using var gray = new Mat(); using var bin = new Mat();
        Cv2.Absdiff(ra, rb, diff);
        Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, bin, 40, 255, ThresholdTypes.Binary);
        return Cv2.CountNonZero(bin) / (double)(rect.Width * rect.Height);
    }

    private static int ChangedOutside(Mat a, Mat b, Rect except)
    {
        using var diff = new Mat(); using var gray = new Mat();
        Cv2.Absdiff(a, b, diff);
        Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
        gray.Rectangle(except, Scalar.Black, -1);
        return Cv2.CountNonZero(gray);
    }

    [Fact]
    public void TryFill_HoleInStripes_ContinuesTheStripes_AndTouchesNothingElse()
    {
        using var original = Stripes(400, 300);
        using var page = original.Clone();
        var hole = new Rect(160, 100, 70, 90);
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);   // 글자 대신 회색 덩어리가 줄무늬를 가리고 있다
        using var mask = RectMask(400, 300, hole);

        Assert.True(TryFill(page, mask, mask, hole));

        Assert.True(Mismatch(page, original, hole) < 0.08, $"줄무늬가 이어지지 않았다: {Mismatch(page, original, hole):f3}");
        // 메우는 범위는 구멍 + 가장자리 2px뿐이다
        Assert.Equal(0, ChangedOutside(page, original, new Rect(hole.X - 3, hole.Y - 3, hole.Width + 6, hole.Height + 6)));
    }

    [Fact]
    public void TryFill_HoleInStripes_IsExact()
    {
        // 되풀이 무늬는 격자만큼 옮겨 메운다(LatticeFill) — 한 픽셀도 안 틀린다. shift-map만으로는 줄 끝이 몇 픽셀씩 어긋났다
        using var original = Stripes(400, 300, 6);
        using var page = original.Clone();
        var hole = new Rect(160, 100, 70, 90);
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var mask = RectMask(400, 300, hole);

        Assert.True(TryFill(page, mask, mask, hole));

        Assert.Equal(0, ChangedOutside(page, original, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void TryFill_DoesNotBorrowFromAvoidedPixels()
    {
        // 창 안에 아직 안 지운 다른 글자(여기서는 회색 네모)가 있어도 그것을 무늬로 가져오지 않는다 — avoid로 넘긴 자리는 재료에서 뺀다
        using var original = Stripes(400, 300);
        using var page = original.Clone();
        var hole = new Rect(160, 100, 60, 80); var other = new Rect(240, 100, 60, 80);
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);
        page.Rectangle(other, new Scalar(128, 128, 128), -1);
        using var mask = RectMask(400, 300, hole);
        using var avoid = RectMask(400, 300, hole);
        avoid.Rectangle(other, Scalar.White, -1);

        Assert.True(TryFill(page, mask, avoid, hole));

        Assert.True(Mismatch(page, original, hole) < 0.08, $"{Mismatch(page, original, hole):f3}");
        Assert.Equal(new Vec3b(128, 128, 128), page.At<Vec3b>(other.Y + 40, other.X + 30));   // 남의 자리는 메우지 않는다(그 블록이 제 차례에 지운다)
    }

    [Fact]
    public void TryFill_FlatBlackNextToStripes_KeepsBothSides()
    {
        // 먹칠한 면과 줄무늬 톤에 걸친 글(실제 26쪽): 위는 검게, 아래는 줄무늬로 — LaMa는 둘 다 회색으로 뭉갰다
        using var original = Stripes(400, 300);
        original.Rectangle(new Rect(0, 0, 400, 140), Scalar.Black, -1);
        using var page = original.Clone();
        var hole = new Rect(170, 90, 50, 110);
        page.Rectangle(hole, Scalar.White, -1);
        using var mask = RectMask(400, 300, hole);

        Assert.True(TryFill(page, mask, mask, hole));

        Assert.True(Mismatch(page, original, new Rect(hole.X, hole.Y, hole.Width, 40)) < 0.10);        // 먹칠 쪽(가운데 경계는 빼고)
        Assert.True(Mismatch(page, original, new Rect(hole.X, hole.Bottom - 45, hole.Width, 45)) < 0.15);   // 줄무늬 쪽
    }

    [Fact]
    public void TryFill_MaskSpillingOverAPanelBorder_KeepsTheBorder()
    {
        // 칸 테두리(굵은 세로 먹줄)에 붙은 글(실제 26쪽): 감지기 마스크는 획을 불린 것이라 테두리 위까지 덮는다 — 구멍을 그대로 메우면 테두리가 줄무늬에 파먹힌다
        using var original = Stripes(400, 300, 6);
        original.Rectangle(new Rect(150, 0, 15, 300), Scalar.Black, -1);   // 테두리 x 150..164
        using var page = original.Clone();
        var text = new Rect(165, 100, 50, 90);                              // 테두리에 붙은 글
        page.Rectangle(text, Scalar.Black, -1);
        var hole = new Rect(156, 100, 59, 90);                              // 마스크는 테두리 위로 9px 번졌다
        using var mask = RectMask(400, 300, hole);

        Assert.True(TryFill(page, mask, mask, hole));

        Assert.Equal(0.0, Mismatch(page, original, new Rect(150, 90, 15, 110)));   // 테두리는 그대로
        Assert.True(Mismatch(page, original, text) < 0.02, $"글 자리가 줄무늬로 메워지지 않았다: {Mismatch(page, original, text):f3}");
    }

    [Fact]
    public void TryFill_LongStrokeOfTheTextItself_IsStillErased()
    {
        // 글자의 긴 획(큰 글자의 세로획, 장음 부호)은 통째로 구멍 안이다 — 칸 테두리처럼 구멍 밖으로 이어지지 않는다. 남기면 안 된다
        using var original = Stripes(400, 300, 6);
        using var page = original.Clone();
        var stroke = new Rect(190, 80, 12, 130);
        page.Rectangle(stroke, Scalar.Black, -1);
        var hole = new Rect(185, 75, 22, 140);
        using var mask = RectMask(400, 300, hole);

        Assert.True(TryFill(page, mask, mask, hole));

        Assert.True(Mismatch(page, original, hole) < 0.02, $"{Mismatch(page, original, hole):f3}");
    }

    [Fact]
    public void TryFill_HoleAtTheImageCorner_Works()
    {
        using var original = Stripes(200, 160);
        using var page = original.Clone();
        var hole = new Rect(0, 0, 50, 40);
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var mask = RectMask(200, 160, hole);

        Assert.True(TryFill(page, mask, mask, hole));
        Assert.True(Mismatch(page, original, hole) < 0.10);
    }

    [Fact]
    public void TryFill_NothingToFill_OrNothingToBorrow_ReturnsFalse()
    {
        using var page = Stripes(200, 160);
        using var before = page.Clone();
        using var empty = new Mat(160, 200, MatType.CV_8UC1, Scalar.Black);
        using var everything = new Mat(160, 200, MatType.CV_8UC1, Scalar.White);

        Assert.False(TryFill(page, empty, empty, new Rect(50, 50, 40, 40)));            // 메울 자리가 없다
        Assert.False(TryFill(page, everything, everything, new Rect(0, 0, 200, 160)));   // 가져올 무늬가 없다 — 호출부가 LaMa로 넘긴다
        Assert.Equal(0, ChangedOutside(page, before, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void TryFill_SameInput_GivesTheSameResult()
    {
        // 같은 원고를 다시 돌리면 같은 결과가 나와야 한다(해시로 회귀를 본다). 먹칠과 줄무늬에 걸친 구멍이라 격자가 아니라 shift-map으로 간다 —
        // 그쪽은 난수를 쓰지 않는다(씨앗을 바꿔도 같은 결과, 실측). 나란히 도는 창이 여럿이어도 메우기 전 그림에서 따로 준비하므로 순서와 무관하다
        Mat Run()
        {
            var page = Stripes(300, 240);
            page.Rectangle(new Rect(0, 0, 300, 110), Scalar.Black, -1);
            for (int x = 0; x < 300; x += 9) page.Line(x, 120, x + 40, 239, new Scalar(90, 90, 90), 1);   // 빗금을 섞어 선택의 여지를 만든다
            var hole = new Rect(120, 70, 60, 90);
            page.Rectangle(hole, Scalar.White, -1);
            using var mask = RectMask(300, 240, hole);
            Assert.True(TryFill(page, mask, mask, hole));
            return page;
        }
        using var a = Run(); using var b = Run();
        Assert.Equal(0, ChangedOutside(a, b, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void FillAll_HoleThatIsNothingButAPanelBorder_CountsAsDone_AndChangesNothing()
    {
        // 마스크가 칸 테두리 위에만 걸쳐 있다(글은 다른 블록이 지운다): 메울 것이 없다 — 실패로 돌려주면 호출부가 그 자리를 LaMa에 넘겨 테두리를 뭉갠다
        using var page = Stripes(400, 300, 6);
        page.Rectangle(new Rect(150, 0, 15, 300), Scalar.Black, -1);
        using var before = page.Clone();
        var hole = new Rect(152, 100, 10, 90);
        using var mask = RectMask(400, 300, hole);

        Assert.Equal([true], FillAll(page, mask, mask, [hole]));
        Assert.Equal(0, ChangedOutside(page, before, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void FillAll_HoleTooLargeToFill_IsReportedFalse_AndLeftAlone()
    {
        using var page = Stripes(900, 700, 6);
        page.Rectangle(new Rect(0, 0, 900, 200), Scalar.Black, -1);   // 먹칠과 줄무늬에 걸쳐 격자로는 못 간다
        var hole = new Rect(150, 100, 500, 420);                       // 21만 px — MaxHolePx(20만)를 넘는다
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var before = page.Clone();
        using var mask = RectMask(900, 700, hole);

        Assert.Equal([false], FillAll(page, mask, mask, [hole]));
        Assert.Equal(0, ChangedOutside(page, before, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void FillAll_JobThatCannotEvenBePrepared_IsReportedFalse_InsteadOfThrowing()
    {
        // 준비 단계(창 자르기·칸 테두리 가리기)의 예외도 메우기의 실패일 뿐이다 — 밖으로 나가면 그 장이 통째로 실패한다. 여기서는 크기가 안 맞는 재료 마스크로 일으킨다
        using var page = Stripes(400, 300, 6);
        using var before = page.Clone();
        var hole = new Rect(150, 100, 60, 80);
        using var own = new Mat(hole.Height, hole.Width, MatType.CV_8UC1, Scalar.White);
        using var avoidTooSmall = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);

        Assert.Equal([false], TextureFill.FillAll(page, [(hole, own)], avoidTooSmall));
        Assert.Equal(0, ChangedOutside(page, before, new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void FillAll_JobsWithTheirOwnHoles_DoNotFillEachOthersStrokes()
    {
        // 가까운 두 블록: A의 창(1.15배) 안에 B의 획이 들어와 있다. 창으로 쪽 마스크를 다시 자르면 A의 구멍에 B의 획이 섞여 둘이 같은 자리를 따로 메우고(뒤에 붙인 쪽이 이긴다),
        // A가 실패하면 B의 획까지 LaMa로 넘어갔다(검토 2026-11-27). 일감마다 제 구멍만 든다
        using var original = Stripes(500, 300, 6);
        using var page = original.Clone();
        var a = new Rect(100, 100, 60, 80); var b = new Rect(170, 100, 60, 80);
        page.Rectangle(a, new Scalar(128, 128, 128), -1);
        page.Rectangle(b, new Scalar(128, 128, 128), -1);
        using var all = RectMask(500, 300, a);
        all.Rectangle(b, Scalar.White, -1);
        var areaA = new Rect(90, 90, 100, 100);   // B의 왼쪽 20px이 들어온다
        using var ownA = new Mat(areaA.Height, areaA.Width, MatType.CV_8UC1, Scalar.Black);
        ownA.Rectangle(new Rect(a.X - areaA.X, a.Y - areaA.Y, a.Width, a.Height), Scalar.White, -1);

        var done = TextureFill.FillAll(page, [(areaA, ownA)], all);

        Assert.Equal([true], done);
        Assert.True(Mismatch(page, original, a) < 0.02);
        Assert.Equal(new Vec3b(128, 128, 128), page.At<Vec3b>(b.Y + 40, b.X + 5));   // A의 창 안에 든 B의 획은 그대로(B가 제 차례에 메운다)
    }

    // ── 여러 블록을 한꺼번에(FillAll) — 창마다 따로 준비해 나란히 돌리고 차례로 붙인다 ──

    [Fact]
    public void FillAll_TwoHoles_FillsBoth()
    {
        using var original = Stripes(500, 300);
        using var page = original.Clone();
        var a = new Rect(100, 100, 60, 80); var b = new Rect(300, 110, 60, 80);
        page.Rectangle(a, new Scalar(128, 128, 128), -1);
        page.Rectangle(b, new Scalar(128, 128, 128), -1);
        using var mask = RectMask(500, 300, a);
        mask.Rectangle(b, Scalar.White, -1);

        var done = FillAll(page, mask, mask, [a, b]);

        Assert.Equal([true, true], done);
        Assert.True(Mismatch(page, original, a) < 0.08, $"{Mismatch(page, original, a):f3}");
        Assert.True(Mismatch(page, original, b) < 0.08, $"{Mismatch(page, original, b):f3}");
    }

    [Fact]
    public void FillAll_AnAreaWithNothingToFill_IsReportedFalse_TheOthersAreStillFilled()
    {
        using var original = Stripes(500, 300);
        using var page = original.Clone();
        var a = new Rect(100, 100, 60, 80);
        page.Rectangle(a, new Scalar(128, 128, 128), -1);
        using var mask = RectMask(500, 300, a);

        var done = FillAll(page, mask, mask, [new Rect(350, 100, 60, 80), a]);

        Assert.Equal([false, true], done);   // 호출부는 false인 창을 LaMa로 넘긴다
        Assert.True(Mismatch(page, original, a) < 0.08);
    }

    [Fact]
    public void FillAll_Cancelled_Throws_AndLeavesThePageAlone()
    {
        using var page = Stripes(400, 300);
        var a = new Rect(160, 100, 70, 90);
        page.Rectangle(a, new Scalar(128, 128, 128), -1);
        using var before = page.Clone();
        using var mask = RectMask(400, 300, a);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FillAll(page, mask, mask, [a], cts.Token));
        Assert.Equal(0, ChangedOutside(page, before, new Rect(0, 0, 0, 0)));
    }
}

/// <summary>진단 덤프(NEKO_TEXFILL_DBG) — 환경변수를 바꾸므로 따로 묶는다.</summary>
[Collection(EnvCollection.Name)]
public class TextureFillDumpTests
{
    [Fact]
    public void FillAll_WithTheDumpFolderSet_WritesEachWindowUnderItsPageTag()
    {
        // 쪽마다 창의 자리(x_y)가 겹칠 수 있다 — 이름 앞에 쪽의 꼬리표를 붙여 여러 쪽의 덤프가 서로 덮지 않게 한다(검토 2026-11-27)
        using var dir = new TempDir();
        string? before = Environment.GetEnvironmentVariable("NEKO_TEXFILL_DBG");
        Environment.SetEnvironmentVariable("NEKO_TEXFILL_DBG", dir.Root);
        try
        {
            using var page = new Mat(240, 300, MatType.CV_8UC3, Scalar.White);
            for (int y = 0; y < 240; y += 4) page.Line(0, y, 299, y, Scalar.Black, 1);
            var area = new Rect(120, 80, 60, 70);
            page.Rectangle(area, new Scalar(128, 128, 128), -1);
            using var own = new Mat(area.Height, area.Width, MatType.CV_8UC1, Scalar.White);
            using var avoid = new Mat(240, 300, MatType.CV_8UC1, Scalar.Black);
            avoid.Rectangle(area, Scalar.White, -1);

            Assert.Equal([true], TextureFill.FillAll(page, [(area, own)], avoid, tag: "p07"));

            foreach (string kind in new[] { "patch.png", "valid.png", "own.png", "filled.png", "method.txt" })
                Assert.True(File.Exists(dir.At($"p07_120_80_{kind}")), kind);
            Assert.StartsWith("lattice", File.ReadAllText(dir.At("p07_120_80_method.txt")));
        }
        finally { Environment.SetEnvironmentVariable("NEKO_TEXFILL_DBG", before); }
    }
}
