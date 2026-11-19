using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>재생성 v3의 자를 창(RegenCrop.WindowFor)과 영역 묶음(RegenRegion.BuildRegions) — 영역마다 주변을 1024 정사각으로 잘라 SDXL에 보낸다.</summary>
public class RegenCropTests
{
    // 막대만 지우기(RegenRegion.TightenErase) — 감지 마스크는 막대보다 넓어(실측 2026-11-12: 조각의 40~70%만 검은 픽셀) 통째로 지우면 막대 사이로 보이던 선·손까지 사라진다
    private static (Mat Img, Mat Erase) BarPage()
    {
        var img = new Mat(400, 600, MatType.CV_8UC3, Scalar.White);
        img.Rectangle(new Rect(150, 190, 300, 20), Scalar.Black, -1);          // 검은 막대
        img.Line(new Point(120, 178), new Point(480, 178), Scalar.Black, 2);   // 막대 위로 지나가는 가는 선(그림)
        var erase = new Mat(400, 600, MatType.CV_8UC1, Scalar.Black);
        erase.Rectangle(new Rect(130, 165, 340, 65), Scalar.White, -1);        // 막대보다 넓은 감지 마스크(막대가 27% — 실측 조각은 40~70%)
        return (img, erase);
    }

    [Fact]
    public void TightenErase_KeepsBarPixels_DropsMarginAndThinLines()
    {
        var (img, erase) = BarPage();
        using (img) using (erase) using (var tight = RegenRegion.TightenErase(img, erase))
        {
            Assert.Equal(255, tight.At<byte>(200, 300));   // 막대 한가운데
            Assert.Equal(255, tight.At<byte>(190, 150));   // 막대 모서리
            Assert.Equal(0, tight.At<byte>(222, 300));     // 막대 아래 여백(마스크 안)
            Assert.Equal(0, tight.At<byte>(178, 300));     // 가는 선은 그림이다
            Assert.Equal(0, tight.At<byte>(50, 50));       // 마스크 밖
            Assert.True(Cv2.CountNonZero(tight) < Cv2.CountNonZero(erase) / 2);
        }
    }

    [Fact]
    public void TightenErase_PieceWithoutDarkBlob_StaysWhole()
    {
        // 흰 막대·모자이크처럼 검은 덩어리가 없는 조각은 줄이지 않는다 — 줄이면 지울 것이 남지 않는다
        using var img = new Mat(400, 600, MatType.CV_8UC3, new Scalar(200, 200, 200));
        using var erase = new Mat(400, 600, MatType.CV_8UC1, Scalar.Black);
        erase.Rectangle(new Rect(100, 130, 400, 140), Scalar.White, -1);
        using var tight = RegenRegion.TightenErase(img, erase);
        Assert.Equal(Cv2.CountNonZero(erase), Cv2.CountNonZero(tight));
    }

    [Fact]
    public void TightenErase_DecidesPerPiece()
    {
        var (img, erase) = BarPage();
        using (img) using (erase)
        {
            erase.Rectangle(new Rect(20, 300, 60, 60), Scalar.White, -1);   // 검은 덩어리가 없는 둘째 조각
            using var tight = RegenRegion.TightenErase(img, erase);
            Assert.Equal(255, tight.At<byte>(330, 50));   // 둘째 조각은 통째로
            Assert.Equal(0, tight.At<byte>(222, 300));    // 첫 조각은 막대만
        }
    }

    [Fact]
    public void WindowFor_SmallRegionCenter_Is1024Square()
    {
        var w = RegenCrop.WindowFor(new Rect(1400, 1900, 200, 100), new Size(3000, 4000), 1024);
        Assert.Equal(new Size(1024, 1024), w.Window.Size);
        Assert.Equal(1500, w.Window.X + w.Window.Width / 2);     // 영역 중심에 맞춘다
        Assert.Equal(1950, w.Window.Y + w.Window.Height / 2);
        Assert.Equal((1024, 1024), (w.SendW, w.SendH));
        Assert.Equal(1.0, w.Scale, 6);
    }

    [Fact]
    public void WindowFor_NearEdge_ShiftsInside()
    {
        var w = RegenCrop.WindowFor(new Rect(10, 10, 100, 50), new Size(3000, 4000), 1024);
        Assert.Equal(new Rect(0, 0, 1024, 1024), w.Window);
    }

    [Fact]
    public void WindowFor_LargeRegion_ScalesDown()
    {
        // 긴 변 1500 → 창 2400(1.6배), 보내는 크기 1024, 되돌리기 비율 1024/2400
        var w = RegenCrop.WindowFor(new Rect(500, 500, 1500, 900), new Size(4000, 4000), 1024);
        Assert.Equal(new Size(2400, 2400), w.Window.Size);
        Assert.Equal((1024, 1024), (w.SendW, w.SendH));
        Assert.Equal(1024.0 / 2400, w.Scale, 6);
        Assert.True(w.Window.Contains(new Rect(500, 500, 1500, 900)));
    }

    [Fact]
    public void WindowFor_SmallImage_ClampsToImage()
    {
        var w = RegenCrop.WindowFor(new Rect(100, 100, 50, 50), new Size(800, 600), 1024);
        Assert.Equal(new Rect(0, 0, 800, 600), w.Window);
        Assert.Equal((800, 600), (w.SendW, w.SendH));
        Assert.Equal(1.0, w.Scale, 6);
    }

    [Theory]
    [InlineData(1000, 1000, 1000)]   // 8의 배수면 그대로
    [InlineData(1003, 1003, 1000)]   // 아니면 내린다
    public void WindowFor_SendIsMultipleOf8(int imgW, int imgH, int expectSend)
    {
        var w = RegenCrop.WindowFor(new Rect(400, 400, 50, 50), new Size(imgW, imgH), 1024);
        Assert.Equal((expectSend, expectSend), (w.SendW, w.SendH));
        Assert.Equal(0, w.SendW % 8);
    }

    // ── 영역 묶음 ──

    private static Mat MaskWith(Size img, params Rect[] pieces)
    {
        var m = new Mat(img, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in pieces) m.Rectangle(r, Scalar.White, -1);
        return m;
    }
    private static BodyPart Part(int cls, Rect r) => new(cls, 0.9f, r);

    [Theory]
    [InlineData(BodyPartDetector.ClsPenis, RegenKind.Male)]
    [InlineData(BodyPartDetector.ClsPussy, RegenKind.Female)]
    public void BuildRegions_KindFromParts(int cls, RegenKind expected)
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [Part(cls, new Rect(1300, 1700, 400, 400))], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try { Assert.Equal(expected, Assert.Single(regions).Kind); }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_BothClasses_Both_NoParts_Unknown()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var both = RegenRegion.BuildRegions(mask, [Part(BodyPartDetector.ClsPenis, new Rect(1300, 1700, 200, 400)), Part(BodyPartDetector.ClsPussy, new Rect(1500, 1700, 200, 400))], 0.1, 0.6, mask.Size(), 1024, 0.12);
        var none = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try
        {
            Assert.Equal(RegenKind.Both, Assert.Single(both).Kind);
            Assert.Equal(RegenKind.Unknown, Assert.Single(none).Kind);
        }
        finally { foreach (var r in both.Concat(none)) r.Dispose(); }
    }

    // ── 한쪽 성별로 확정(Sure) — 2026-11-15 사용자 "남자거에 계속 여자거가 그려지는데 검열부분을 보고 남/여 구분을 확실하게" ──
    // 시험 원고 23쪽의 부위 감지 수치로 정한 규칙: 영역의 모든 조각에 그 성별의 근거(약한 상자 0.05 이상도)가 있고, 어느 조각에도 다른 성별의 근거가 없을 때만 확정.
    // 2026-11-19부터 뚜렷한(임계 이상) 상자가 한쪽뿐이면 다른 쪽의 약한 상자는 세지 않는다 — 삽입 장면(남성 0.59 + 여성 0.07)도 남성 확정(가려진 쪽은 결과 창 표시로),
    // 여성 0.27 + 옆 조각은 근거 없음은 확정이 아니어서 남녀 태그를 같이 받고, 남성만 있는 장(여성 근거 0)은 남성 태그만 받는다.
    private const int M = BodyPartDetector.ClsPenis, F = BodyPartDetector.ClsPussy;
    private static readonly Size Page = new(3000, 4000);
    private static readonly Rect PieceA = new(1400, 1900, 200, 40), BoxA = new(1300, 1700, 400, 400);
    private static List<RegenRegionInfo> Regions(Mat mask, params BodyPart[] parts) =>
        RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, strongConf: 0.12);
    private static (RegenKind Kind, bool Sure) Only(List<RegenRegionInfo> regions)
    {
        try { var r = Assert.Single(regions); return (r.Kind, r.Sure); }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Theory]
    [InlineData(M, RegenKind.Male)]
    [InlineData(F, RegenKind.Female)]
    public void BuildRegions_PieceInsideOneSexBox_NothingOfTheOther_IsSure(int cls, RegenKind kind)
    {
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((kind, true), Only(Regions(mask, new BodyPart(cls, 0.7f, BoxA))));
    }

    [Fact]
    public void BuildRegions_WeakBoxOfTheOtherSexOnThePiece_GoesWithTheStrongOne()
    {
        // 사용자 원고 28쪽(2026-11-19, 그림은 안 봄): 남성 0.74 상자의 조각에 여성 0.07 상자가 3px 옆에서 여백으로 걸쳤다 — 남녀 태그가 같이 들어가
        // 남성 부위 윗부분이 살결로 메워졌다(같은 시드 15번 전부, 시드·CN 강도와 무관). 뚜렷한(임계 이상) 상자가 한쪽뿐이면 그쪽이다 — 약한 상자는 뚜렷한 상자가 없을 때만 근거다.
        // 2026-11-15의 삽입 장면(남성 0.59 + 여성 0.07)은 이 규칙으로 남성 태그만 받는다 — 가려진 쪽은 결과 창의 조각 표시로 고른다(사용자 결정 2026-11-19)
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.59f, BoxA), new BodyPart(F, 0.07f, new Rect(1380, 1880, 120, 60)))));
        Assert.Equal((RegenKind.Female, true), Only(Regions(mask, new BodyPart(F, 0.74f, BoxA), new BodyPart(M, 0.07f, new Rect(1380, 1880, 120, 60)))));
    }

    [Theory]
    [InlineData(M, F)]
    [InlineData(F, M)]
    public void BuildRegions_OtherSexBoxExactlyAtTheThreshold_IsStrong_SoBoth(int strongCls, int atThresholdCls)
    {
        // 임계와 똑같은 점수 0.12f — 임계는 설정의 double 0.12, 점수는 float: float로 맞춰 견줘야 뚜렷한 상자다((float)0.12 < 0.12라 double로 견주면 약한 상자로 떨어져 한쪽 태그만 간다 — 검수 2026-11-19 변이 확인)
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((RegenKind.Both, false), Only(Regions(mask, new BodyPart(strongCls, 0.7f, BoxA), new BodyPart(atThresholdCls, 0.12f, new Rect(1380, 1880, 120, 60)))));
    }

    [Fact]
    public void BuildRegions_WeakBoxOfTheOtherSex_CountsWhenTheUserLoweredTheThresholdToIt()
    {
        // '뚜렷하다'는 설정의 임계(RegenRegionConf)다 — 사용자가 0.05로 내렸으면 0.07 상자도 뚜렷한 상자라 남녀가 같이 있는 조각이다
        using var mask = MaskWith(Page, PieceA);
        var regions = RegenRegion.BuildRegions(mask, [new BodyPart(M, 0.59f, BoxA), new BodyPart(F, 0.07f, new Rect(1380, 1880, 120, 60))], 0.1, 0.6, mask.Size(), 1024, strongConf: 0.05);
        Assert.Equal((RegenKind.Both, false), Only(regions));
    }

    [Fact]
    public void BuildRegions_OtherSexBelowTheFloor_OrAwayFromThePieces_DoesNotCount()
    {
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.7f, BoxA), new BodyPart(F, 0.04f, new Rect(1380, 1880, 120, 60)))));   // 0.05 미만은 잡음
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.7f, BoxA), new BodyPart(F, 0.6f, new Rect(2400, 300, 200, 200)))));    // 같은 쪽의 다른 칸에 있는 여성 부위
    }

    [Fact]
    public void BuildRegions_MergedPieceWithoutAnyEvidence_IsNotSure()
    {
        // 여성은 잡혔고(0.27) 400px 옆의 조각에는 아무 상자도 없다 — 그 조각이 남성 부위일 수 있다(시험 원고 3번: '여자 것만' 그려졌다)
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1900, 200, 40));
        Assert.Equal((RegenKind.Female, false), Only(Regions(mask, new BodyPart(F, 0.27f, BoxA))));
    }

    [Fact]
    public void BuildRegions_MergedPieceWithWeakEvidenceOfTheSameSex_IsSure()
    {
        // 뚜렷한 상자 밖의 조각에도 같은 성별이 약하게(0.07) 잡혔다 — 한쪽 성별뿐이다
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1900, 200, 40));
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.7f, BoxA), new BodyPart(M, 0.07f, new Rect(1750, 1850, 300, 150)))));
    }

    [Fact]
    public void BuildRegions_PieceInThePaddedMarginOfTheBox_CountsAsThatSex()
    {
        // 상자 바로 아래(여백 10% = 40px 안)의 조각 — 다시 그리는 사각형에는 들어가므로 그 부위의 조각으로 본다(오늘 원고 03쪽)
        using var mask = MaskWith(Page, PieceA, new Rect(1400, 2110, 200, 20));
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.7f, BoxA))));
    }

    [Fact]
    public void BuildRegions_OnlyWeakBoxes_DoNotWidenTheRegion_ButDecideTheSex()
    {
        // 임계(0.12) 아래의 상자는 영역의 모양·종류를 바꾸지 않는다(전과 같다). 다만 한쪽 성별의 근거뿐이면 그쪽으로 정한다 —
        // 사용자 원고(2026-11-15)의 05·06쪽은 남성이 0.11·0.06으로만 잡혀 남녀 태그를 받았다("남자거에 계속 여자거가 그려지는데")
        using var mask = MaskWith(Page, PieceA);
        var weak = Regions(mask, new BodyPart(M, 0.08f, BoxA));
        var none = Regions(mask);
        try
        {
            var w = Assert.Single(weak); var n = Assert.Single(none);
            Assert.Equal((RegenKind.Unknown, RegenKind.Male, true), (w.Kind, w.Sex, w.Sure));
            Assert.Equal((RegenKind.Unknown, RegenKind.Both, false), (n.Kind, n.Sex, n.Sure));
            Assert.Equal(n.Bounds, w.Bounds);
            using var diff = new Mat(); Cv2.BitwiseXor(n.Region, w.Region, diff);
            Assert.Equal(0, Cv2.CountNonZero(diff));
        }
        finally { foreach (var r in weak.Concat(none)) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_WeakBoxNextToAStrongOne_ChangesNothing()
    {
        using var mask = MaskWith(Page, PieceA);
        var with = Regions(mask, new BodyPart(M, 0.7f, BoxA), new BodyPart(F, 0.07f, new Rect(1200, 1850, 600, 300)));
        var without = Regions(mask, new BodyPart(M, 0.7f, BoxA));
        try
        {
            var a = Assert.Single(with); var b = Assert.Single(without);
            Assert.Equal(b.Bounds, a.Bounds);
            using var diff = new Mat(); Cv2.BitwiseXor(a.Region, b.Region, diff);
            Assert.Equal(0, Cv2.CountNonZero(diff));
            Assert.Equal(RegenKind.Male, a.Kind);   // 약한 상자는 종류를 '둘 다'로 바꾸지 않는다
            Assert.True(a.Sure); Assert.True(b.Sure);   // 뚜렷한 남성 상자가 있으니 약한 여성 상자는 확정도 풀지 않는다(2026-11-19 — 전에는 확정만 풀어 남녀 태그가 같이 갔다)
        }
        finally { foreach (var r in with.Concat(without)) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_BothSexesStrong_OrNoBoxes_AreNeverSure()
    {
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((RegenKind.Both, false), Only(Regions(mask, new BodyPart(M, 0.7f, new Rect(1300, 1700, 200, 400)), new BodyPart(F, 0.6f, new Rect(1500, 1700, 200, 400)))));
        Assert.Equal((RegenKind.Unknown, false), Only(Regions(mask)));
    }

    // ── 조각의 성별·티끌·직접 고른 표시 (2026-11-15 사용자 "앞으로 작업하는 모든 페이지가 남/여 구별되서 들어가야해") ──
    // 사용자 원고 44쪽(52영역, 그림은 안 봄): 한쪽 태그를 받는 영역이 17곳뿐이었다 — 약한 근거뿐인 영역, 근거 없는 티끌이 낀 영역, 부위 감지가 못 잡는 조각(23개)이 남녀 태그로 갔다.
    // 그래서 성별을 **조각마다** 정한다: 부위 감지의 근거(약한 상자도), 사용자가 찍은 표시가 있으면 그것. 영역은 티끌 아닌 조각이 전부 같은 한쪽일 때 그쪽.
    private static readonly Rect PieceB = new(1800, 1900, 200, 40);   // PieceA와 한 영역으로 합쳐지는 옆 조각
    private static List<RegenRegionInfo> Marked(Mat mask, IReadOnlyList<SexMark> marks, params BodyPart[] parts) =>
        RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, strongConf: 0.12, marks);
    private static T One<T>(List<RegenRegionInfo> regions, Func<RegenRegionInfo, T> pick)
    {
        try { return pick(Assert.Single(regions)); }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_WeakBoxesOfBothSexes_OrBelowTheFloor_AreNotDecided()
    {
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal(RegenKind.Both, One(Regions(mask, new BodyPart(M, 0.08f, BoxA), new BodyPart(F, 0.07f, BoxA)), r => r.Sex));
        Assert.Equal(RegenKind.Both, One(Regions(mask, new BodyPart(M, 0.04f, BoxA)), r => r.Sex));   // 0.05 미만은 근거가 아니다
    }

    [Fact]
    public void BuildRegions_SpeckWithoutEvidence_DoesNotUndoTheDecision()
    {
        // 사용자 원고 b07: 남성 0.76이 닿은 조각 넷 + 근거 없는 28px 티끌 — 티끌 하나 때문에 남녀 태그가 들어갔다(사용자가 남성 태그만으로 좋다고 본 장)
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1910, 8, 8));   // 8000px + 64px(0.8%)
        var regions = Regions(mask, new BodyPart(M, 0.7f, BoxA));
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal((RegenKind.Male, true), (r.Sex, r.Sure));
            Assert.Equal(2, r.Pieces.Count);
            Assert.True(Assert.Single(r.Pieces, p => p.Area == 64).Speck);
            Assert.False(Assert.Single(r.Pieces, p => p.Area == 8000).Speck);
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_SmallPieceOfOnePercentOrMore_StillCounts()
    {
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1910, 10, 10));   // 8000px + 100px(1.2%) — 근거 없는 조각이 남아 있다
        Assert.Equal(RegenKind.Both, One(Regions(mask, new BodyPart(M, 0.7f, BoxA)), r => r.Sex));
    }

    [Fact]
    public void BuildRegions_TinyPieceWithEvidence_OrMarkedByTheUser_IsNotASpeck()
    {
        // 티끌로 빼는 것은 '근거 없는' 작은 조각뿐이다 — 큰 모자이크 옆의 작은 조각이 다른 성별로 잡혔으면(검수의 예: 여성 40,000px 옆 남성 360px) 그것은 진짜 부위다
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1910, 8, 8));   // 8000px + 64px(0.8%)
        var tinyFemale = new BodyPart(F, 0.3f, new Rect(1780, 1890, 60, 60));
        var regions = Regions(mask, new BodyPart(M, 0.7f, BoxA), tinyFemale);
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal(RegenKind.Both, r.Sex);   // 남성 조각 + 여성 조각
            Assert.DoesNotContain(r.Pieces, p => p.Speck);
            Assert.Equal(RegenRegion.FemaleCode, r.SexMap!.At<byte>(1912, 1802));
        }
        finally { foreach (var r in regions) r.Dispose(); }

        // 근거는 없지만 사용자가 고른 작은 조각도 센다
        var marked = Marked(mask, [new SexMark(new Point(1803, 1913), RegenKind.Female)], new BodyPart(M, 0.7f, BoxA));
        try
        {
            var r = Assert.Single(marked);
            Assert.Equal(RegenKind.Both, r.Sex);
            Assert.DoesNotContain(r.Pieces, p => p.Speck);
        }
        finally { foreach (var r in marked) r.Dispose(); }
    }

    [Theory]
    [InlineData(true)]    // 나머지 점들에 남성 근거가 있다
    [InlineData(false)]   // 나머지 점들은 근거가 없다
    public void BuildRegions_OnlySpecks_OneMarked_TheOthersStillCount(bool othersHaveEvidence)
    {
        // 자잘한 조각뿐인 영역에서 하나만 고르면, 그 하나가 영역 전체를 정해서는 안 된다(나머지 109개가 티끌로 빠지면 여성 태그만 들어간다)
        var dots = Enumerable.Range(0, 110).Select(i => new Rect(100 + 20 * (i % 11), 100 + 20 * (i / 11), 4, 4)).ToArray();
        using var mask = MaskWith(new Size(640, 640), dots);
        BodyPart[] parts = othersHaveEvidence ? [new BodyPart(M, 0.7f, new Rect(80, 80, 300, 300))] : [];
        var regions = RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, strongConf: 0.12, [new SexMark(new Point(101, 101), RegenKind.Female)]);
        try
        {
            var r = Assert.Single(regions);
            Assert.DoesNotContain(r.Pieces, p => p.Speck);
            Assert.Equal(RegenKind.Both, r.Sex);
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_OnlySpecksWithoutEvidence_AreNotDroppedAsSpecks()
    {
        // 근거 없는 자잘한 조각뿐인 영역: 전부 티끌로 빼면 판단할 조각이 하나도 남지 않는다 — 전부 센다(모르는 조각들 = 남녀 같이)
        var dots = Enumerable.Range(0, 110).Select(i => new Rect(100 + 20 * (i % 11), 100 + 20 * (i / 11), 4, 4)).ToArray();
        using var mask = MaskWith(new Size(640, 640), dots);
        var regions = Regions(mask);
        try
        {
            var r = Assert.Single(regions);
            Assert.DoesNotContain(r.Pieces, p => p.Speck);
            Assert.Equal((RegenKind.Both, false), (r.Sex, r.SexMap != null));
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_MarkBetweenTwoPieces_GoesToTheNearerOne()
    {
        // 두 조각이 다 24px 안이면 가까운 쪽의 것이다
        using var mask = MaskWith(Page, new Rect(1400, 1900, 200, 40), new Rect(1400, 1960, 200, 40));   // y 1900~1939, 1960~1999 — 사이 20px
        var regions = Marked(mask, [new SexMark(new Point(1500, 1955), RegenKind.Female)]);   // 위 조각에서 16px, 아래 조각에서 5px — 먼저 훑이는 위 조각이 아니라 가까운 아래 조각
        try
        {
            var r = Assert.Single(regions);
            var bottom = Assert.Single(r.Pieces, p => p.Box.Y == 1960);
            Assert.Equal((RegenKind.Female, true), (bottom.Sex, bottom.ByUser));
            Assert.False(Assert.Single(r.Pieces, p => p.Box.Y == 1900).ByUser);
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_OnlySpecks_AllCount()
    {
        // 자잘한 조각뿐인 영역: 1% 기준으로는 전부 티끌이다 — 그때는 전부 센다(판단할 조각이 하나도 없으면 안 된다)
        var dots = Enumerable.Range(0, 110).Select(i => new Rect(100 + 20 * (i % 11), 100 + 20 * (i / 11), 4, 4)).ToArray();
        using var mask = MaskWith(new Size(640, 640), dots);   // 작은 그림 — 조각마다 그림 크기 마스크를 둘씩 든다
        var regions = Regions(mask, new BodyPart(M, 0.7f, new Rect(80, 80, 300, 300)));
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal(110, r.Pieces.Count);
            Assert.DoesNotContain(r.Pieces, p => p.Speck);
            Assert.Equal(RegenKind.Male, r.Sex);
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_UserMark_WinsOverTheEvidence()
    {
        using var mask = MaskWith(Page, PieceA);
        var at = new Point(1500, 1920);
        var regions = Marked(mask, [new SexMark(at, RegenKind.Female)], new BodyPart(M, 0.7f, BoxA));
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal((RegenKind.Male, RegenKind.Female, true), (r.Kind, r.Sex, r.Sure));   // 종류(부위 상자)는 그대로 — 태그만 사용자가 고른 쪽
            var piece = Assert.Single(r.Pieces);
            Assert.Equal((RegenKind.Female, true), (piece.Sex, piece.ByUser));
            Assert.Equal(RegenKind.Male, piece.Auto);   // 자동 판단은 따로 남는다 — 결과 창이 '자동이면 남성'을 알려 준다
        }
        finally { foreach (var r in regions) r.Dispose(); }
        Assert.Equal(RegenKind.Both, One(Marked(mask, [new SexMark(at, RegenKind.Both)], new BodyPart(M, 0.7f, BoxA)), r => r.Sex));
        Assert.False(One(Regions(mask, new BodyPart(M, 0.7f, BoxA)), r => Assert.Single(r.Pieces).ByUser));
    }

    [Fact]
    public void BuildRegions_UserMark_DecidesThePieceTheDetectorMissed()
    {
        // 여성 0.27 + 근거 없는 옆 조각(시험 원고 3번) — 자동으로는 못 가린다. 사용자가 옆 조각을 골라 주면 정해진다
        using var mask = MaskWith(Page, PieceA, PieceB);
        var onB = new Point(1900, 1920);
        Assert.Equal(RegenKind.Both, One(Regions(mask, new BodyPart(F, 0.27f, BoxA)), r => r.Sex));
        Assert.Equal(RegenKind.Female, One(Marked(mask, [new SexMark(onB, RegenKind.Female)], new BodyPart(F, 0.27f, BoxA)), r => r.Sex));
        Assert.Equal(RegenKind.Both, One(Marked(mask, [new SexMark(onB, RegenKind.Male)], new BodyPart(F, 0.27f, BoxA)), r => r.Sex));   // 여성 조각 + 남성 조각 = 섞인 영역
    }

    [Fact]
    public void BuildRegions_MarkJustOutsideThePiece_StillApplies_AFarOneDoesNot()
    {
        // 표시는 조각 안의 한 점으로 기억한다 — 마스크를 조금 고쳐 그 점이 조각 밖으로 나가도(24px 안) 그 조각의 것이다
        using var mask = MaskWith(Page, PieceA);   // y 1900~1939
        Assert.Equal(RegenKind.Female, One(Marked(mask, [new SexMark(new Point(1500, 1955), RegenKind.Female)]), r => r.Sex));   // 조각 아래 16px
        Assert.Equal(RegenKind.Both, One(Marked(mask, [new SexMark(new Point(1500, 2040), RegenKind.Female)]), r => r.Sex));     // 100px 밖 — 어느 조각의 것도 아니다
        Assert.Equal(RegenKind.Both, One(Marked(mask, [new SexMark(new Point(-5, 99999), RegenKind.Female)]), r => r.Sex));      // 그림 밖의 점(망가진 기록)에도 터지지 않는다
    }

    [Fact]
    public void BuildRegions_TwoDifferentMarksOnOnePiece_MeanBoth()
    {
        // 따로 표시한 두 조각을 칠해서 이으면 한 조각에 표시가 둘이다 — 남녀가 같이 있는 조각
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal(RegenKind.Both, One(Marked(mask, [new SexMark(new Point(1420, 1920), RegenKind.Male), new SexMark(new Point(1580, 1920), RegenKind.Female)]), r => r.Sex));
        Assert.Equal(RegenKind.Male, One(Marked(mask, [new SexMark(new Point(1420, 1920), RegenKind.Male), new SexMark(new Point(1580, 1920), RegenKind.Male)]), r => r.Sex));
    }

    [Fact]
    public void BuildRegions_MixedPieces_CarryASexMap_OfThePiecePixels()
    {
        // 남성 조각과 여성 조각이 한 영역에(사용자 원고 h5: 한 조각 남성 0.63, 다른 조각 여성 0.60) — 자리별 태그의 재료: 조각 픽셀에 그 성별의 값
        using var mask = MaskWith(Page, PieceA, PieceB, new Rect(1650, 2200, 6, 6));   // + 근거 없는 티끌(36px — 두 상자의 여백 밖)
        var regions = Regions(mask, new BodyPart(M, 0.63f, BoxA), new BodyPart(F, 0.60f, new Rect(1760, 1750, 300, 300)));
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal((RegenKind.Both, RegenKind.Both, false), (r.Kind, r.Sex, r.Sure));
            Assert.NotNull(r.SexMap);
            Assert.Equal(RegenRegion.MaleCode, r.SexMap!.At<byte>(1920, 1500));
            Assert.Equal(RegenRegion.FemaleCode, r.SexMap.At<byte>(1920, 1900));
            Assert.Equal(0, r.SexMap.At<byte>(2202, 1652));   // 티끌은 칠하지 않는다 — 가까운 조각의 자리에 든다
            Assert.Equal(0, r.SexMap.At<byte>(1800, 1500));   // 조각 밖
            Assert.Equal(8000 + 8000, Cv2.CountNonZero(r.SexMap));
            Assert.Equal(mask.Size(), r.SexMap.Size());
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_UndecidedPieceNextToADecidedOne_IsOnTheSexMapAsBoth()
    {
        using var mask = MaskWith(Page, PieceA, PieceB);
        var regions = Regions(mask, new BodyPart(F, 0.27f, BoxA));
        try
        {
            var r = Assert.Single(regions);
            Assert.NotNull(r.SexMap);
            Assert.Equal(RegenRegion.FemaleCode, r.SexMap!.At<byte>(1920, 1500));
            Assert.Equal(RegenRegion.BothCode, r.SexMap.At<byte>(1920, 1900));   // 근거 없는 조각 — 남녀 태그 자리
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_AllPiecesAlike_HaveNoSexMap()
    {
        // 나눌 것이 없는 영역(전부 남성, 전부 모름)은 지도를 만들지 않는다 — 한 줄 프롬프트로 간다
        using var mask = MaskWith(Page, PieceA, PieceB);
        Assert.True(One(Regions(mask, new BodyPart(M, 0.7f, BoxA), new BodyPart(M, 0.07f, new Rect(1750, 1850, 300, 150))), r => r.SexMap == null));
        Assert.True(One(Regions(mask), r => r.SexMap == null));
    }

    [Fact]
    public void BuildRegions_Marks_DoNotChangeTheRegionShape()
    {
        // 표시는 태그만 정한다 — 다시 그리는 범위·지우는 조각·종류는 그대로(복원 범위가 바뀌면 결과가 통째로 달라진다)
        using var mask = MaskWith(Page, PieceA, PieceB);
        var with = Marked(mask, [new SexMark(new Point(1900, 1920), RegenKind.Male)], new BodyPart(F, 0.27f, BoxA));
        var without = Regions(mask, new BodyPart(F, 0.27f, BoxA));
        try
        {
            var a = Assert.Single(with); var b = Assert.Single(without);
            Assert.Equal((b.Bounds, b.Kind), (a.Bounds, a.Kind));
            using var d1 = new Mat(); Cv2.BitwiseXor(a.Region, b.Region, d1);
            using var d2 = new Mat(); Cv2.BitwiseXor(a.Erase, b.Erase, d2);
            Assert.Equal((0, 0), (Cv2.CountNonZero(d1), Cv2.CountNonZero(d2)));
        }
        finally { foreach (var r in with.Concat(without)) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_PieceAnchor_IsAPixelOfThatPiece()
    {
        // 표시를 그리고 기억하는 점 — 상자 가운데가 조각 밖인 모양(고리)에서도 조각 위여야 그 점에 찍은 표시가 그 조각의 것이 된다
        using var mask = new Mat(Page, MatType.CV_8UC1, Scalar.Black);
        mask.Rectangle(new Rect(1400, 1800, 200, 200), Scalar.White, 12);   // 속이 빈 고리
        var anchor = One(Regions(mask), r => Assert.Single(r.Pieces).Anchor);
        Assert.Equal(255, mask.At<byte>(anchor.Y, anchor.X));
        Assert.Equal(RegenKind.Male, One(Marked(mask, [new SexMark(anchor, RegenKind.Male)]), r => r.Sex));
    }

    // 결과 창에서 조각의 표시를 눌러 성별을 바꿀 때 — 그 조각에 걸린 옛 표시를 걷고 새 표시를 조각의 점(Anchor)에 둔다
    [Fact]
    public void WithSex_ReplacesTheMarksOnThatPiece_KeepsTheOthers()
    {
        using var mask = MaskWith(Page, PieceA, PieceB);
        SexMark onA1 = new(new Point(1420, 1920), RegenKind.Male), onA2 = new(new Point(1580, 1945), RegenKind.Female);   // 둘째는 조각 바로 아래(24px 안)
        SexMark onB = new(new Point(1900, 1920), RegenKind.Male), nowhere = new(new Point(100, 100), RegenKind.Female);
        SexMark[] marks = [onA1, onB, onA2, nowhere];
        var anchorA = new Point(1500, 1920);

        Assert.Equal([onB, nowhere, new SexMark(anchorA, RegenKind.Both)], RegenRegion.WithSex(mask, marks, anchorA, RegenKind.Both, out bool found));
        Assert.True(found);
        Assert.Equal([onB, nowhere], RegenRegion.WithSex(mask, marks, anchorA, null, out _));                 // 자동으로 — 그 조각의 표시만 걷는다
        Assert.Equal([onB, nowhere], RegenRegion.WithSex(mask, marks, anchorA, RegenKind.Unknown, out _));   // Unknown도 '자동'이다
    }

    // 표시의 정리 — 결과 창이 사이드카에 적는 꼴이자 '달라졌는가'를 견주는 꼴: 조각마다 하나, 그 조각의 점에, 조각 순서로
    [Fact]
    public void ResolveMarks_OnePerMarkedPiece_AtItsAnchor_WhateverTheOrderOrThePoint()
    {
        using var mask = MaskWith(Page, PieceA, PieceB);
        var anchors = RegenRegion.PieceAnchors(mask);   // A, B 순서
        SexMark onA = new(new Point(1420, 1905), RegenKind.Male), onB = new(new Point(1990, 1935), RegenKind.Female);
        SexMark[] expected = [new(anchors[0], RegenKind.Male), new(anchors[1], RegenKind.Female)];

        Assert.Equal(expected, RegenRegion.ResolveMarks(mask, [onA, onB]));
        Assert.Equal(expected, RegenRegion.ResolveMarks(mask, [onB, onA]));                                  // 찍은 순서와 무관
        Assert.Equal(expected, RegenRegion.ResolveMarks(mask, [onB, onA, new SexMark(new Point(100, 100), RegenKind.Male)]));   // 조각이 없는 표시는 버린다
        Assert.Equal(expected, RegenRegion.ResolveMarks(mask, expected));                                    // 정리한 것을 다시 정리해도 같다
        Assert.Equal([new SexMark(anchors[0], RegenKind.Both)], RegenRegion.ResolveMarks(mask, [onA, new SexMark(new Point(1580, 1920), RegenKind.Female)]));   // 한 조각에 다른 표시 둘
        Assert.Empty(RegenRegion.ResolveMarks(mask, null));
    }

    [Fact]
    public void PieceAnchors_OnePointPerPiece_OnThatPiece_AndTheyMarkEveryPiece()
    {
        // 결과 창의 [모두 남성]: 조각마다 그 점에 표시를 찍는다 — 영역 계획(조각마다 그림 크기 마스크 둘)을 만들지 않고 성분만 센다
        using var mask = MaskWith(Page, PieceA, PieceB, new Rect(1650, 2000, 6, 6));
        mask.Rectangle(new Rect(400, 400, 200, 200), Scalar.White, 12);   // 고리 — 상자 가운데가 조각 밖
        var anchors = RegenRegion.PieceAnchors(mask);
        Assert.Equal(4, anchors.Count);
        Assert.All(anchors, a => Assert.Equal(255, mask.At<byte>(a.Y, a.X)));
        var regions = Marked(mask, anchors.Select(a => new SexMark(a, RegenKind.Male)).ToList());
        try { Assert.All(regions, r => Assert.All(r.Pieces, p => Assert.Equal((RegenKind.Male, true), (p.Sex, p.ByUser)))); }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void WithSex_PointOnNoPiece_ChangesNothing()
    {
        // 표시를 누르는 사이 마스크가 바뀌어 그 자리에 조각이 없다 — 어느 조각의 것도 아닌 표시를 만들지 않는다
        using var mask = MaskWith(Page, PieceA);
        SexMark[] marks = [new(new Point(1420, 1920), RegenKind.Male)];
        Assert.Equal(marks, RegenRegion.WithSex(mask, marks, new Point(2500, 3000), RegenKind.Female, out bool found));
        Assert.False(found);   // 결과 창이 '골랐어요'라고 말하지 않게
    }

    [Fact]
    public void BuildRegions_BoxExactlyAtTheThreshold_StillShapesTheRegion()
    {
        // 임계는 설정의 double(0.12), 상자의 점수는 float — (float)0.12는 double 0.12보다 조금 작다. 전에는 감지기가 float끼리 견줘 0.12f짜리 상자를 받았다
        using var mask = MaskWith(Page, PieceA);
        Assert.Equal((RegenKind.Male, true), Only(Regions(mask, new BodyPart(M, 0.12f, BoxA))));
    }

    [Fact]
    public void BuildRegions_ThresholdSetBelowTheEvidenceFloor_ShapesTheRegionButIsNotSure()
    {
        // 사용자가 임계를 0.03으로 내렸다: 0.04짜리 상자가 영역의 모양·종류를 정하지만(설정대로), 성별의 근거로는 약해(0.05 미만) 한쪽으로 확정하지 않는다
        using var mask = MaskWith(Page, PieceA);
        var regions = RegenRegion.BuildRegions(mask, [new BodyPart(M, 0.04f, BoxA)], 0.1, 0.6, mask.Size(), 1024, strongConf: 0.03);
        Assert.Equal((RegenKind.Male, false), Only(regions));
    }

    [Fact]
    public void BuildRegions_TwoPiecesMergeAndAThirdStaysApart_EachRegionIsJudgedByItsOwnPieces()
    {
        // 앞의 두 조각은 한 영역으로 합쳐지고 셋째는 따로 — 합칠 때 조각 목록이 영역 목록과 어긋나면 셋째 영역이 둘째 조각의 근거로 판정된다
        using var mask = MaskWith(Page, PieceA, new Rect(1800, 1900, 200, 40), new Rect(2500, 3500, 200, 40));
        var regions = Regions(mask,
            new BodyPart(M, 0.7f, BoxA), new BodyPart(M, 0.6f, new Rect(1700, 1700, 400, 400)),            // 합쳐지는 두 조각: 남성만
            new BodyPart(M, 0.08f, new Rect(2400, 3350, 400, 400)));   // 떨어진 조각: 약한 남성만 — 종류(Unknown)와 성별(Male)이 갈려 장부가 어긋나면 드러난다
        try
        {
            Assert.Equal(2, regions.Count);
            var top = Assert.Single(regions, r => r.Bounds.Y < 3000); var bottom = Assert.Single(regions, r => r.Bounds.Y > 3000);
            Assert.Equal((RegenKind.Male, RegenKind.Male, true), (top.Kind, top.Sex, top.Sure));
            Assert.Equal((RegenKind.Unknown, RegenKind.Male, true), (bottom.Kind, bottom.Sex, bottom.Sure));
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_SeparateRegions_AreJudgedSeparately()
    {
        // 멀리 떨어진 두 영역: 위는 남성만(확정), 아래는 약한 남성만(종류 Unknown·성별 Male) — 한 영역의 근거가 다른 영역을 흔들지 않는다
        using var mask = MaskWith(Page, new Rect(200, 200, 200, 40), new Rect(2500, 3500, 200, 40));
        var regions = Regions(mask, new BodyPart(M, 0.7f, new Rect(100, 50, 400, 400)), new BodyPart(M, 0.08f, new Rect(2400, 3350, 400, 400)));
        try
        {
            Assert.Equal(2, regions.Count);
            var top = Assert.Single(regions, r => r.Bounds.Y < 1000); var bottom = Assert.Single(regions, r => r.Bounds.Y > 1000);
            Assert.Equal((RegenKind.Male, RegenKind.Male, true), (top.Kind, top.Sex, top.Sure));
            Assert.Equal((RegenKind.Unknown, RegenKind.Male, true), (bottom.Kind, bottom.Sex, bottom.Sure));
        }
        finally { foreach (var r in regions) r.Dispose(); }
    }

    [Fact]
    public void BuildRegions_OverlappingWindows_Merged()
    {
        // 300px 떨어진 두 조각 — 1024 창이 겹치므로 한 영역으로 (마스크 OR, 경계 합집합)
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(1500, 2000, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try
        {
            var r = Assert.Single(regions);
            Assert.True(r.Bounds.Contains(new Rect(1000, 2000, 200, 40)) && r.Bounds.Contains(new Rect(1500, 2000, 200, 40)));
            Assert.Equal(255, r.Erase.At<byte>(2020, 1100)); Assert.Equal(255, r.Erase.At<byte>(2020, 1600));
        }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_WindowsTouchButPiecesApart_Separate()
    {
        // 2000px 폭에 조각이 x≈500과 x≈1500 — 1024 창은 48px 겹치지만 어느 조각도 상대 창 안에 안 보인다 → 따로(합치면 창 1.9k → 0.53배 축소로 둘 다 흐려진다, 최종 검수 지적)
        using var mask = MaskWith(new Size(2000, 3000), new Rect(450, 1500, 100, 40), new Rect(1450, 1500, 100, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try { Assert.Equal(2, regions.Count); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_FarApart_Separate()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(200, 200, 200, 40), new Rect(2500, 3500, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try { Assert.Equal(2, regions.Count); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_EraseIsOnlyThePiece_RegionIsWider()
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1400, 1900, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        try
        {
            var r = Assert.Single(regions);
            Assert.Equal(200 * 40, Cv2.CountNonZero(r.Erase));
            Assert.True(Cv2.CountNonZero(r.Region) > 200 * 40 * 3);
            Assert.Equal(mask.Size(), r.Region.Size()); Assert.Equal(mask.Size(), r.Erase.Size());
        }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    // ── 떨어진 자리는 따로(splitReach ≥ 0, 설정 RegenSplitRegions) — 사용자 보고 2026-11-16 "인체 부위도 제대로 안나오는게 있는데":
    // 상대 창 안에 보이기만 해도 합치던 규칙이 1813px 쪽에서 줄줄이 합쳐 창이 쪽 전체(0.40배)가 됐다. 붙이는 범위(상자 + reach)가 겹칠 때만 합친다.
    // 조각 200x40, 부위 상자 없음 → 영역 상자는 조각에서 좌우 120·위아래 80 넓다(타원 0.6배)

    [Fact]
    public void BuildRegions_Split_VisibleInTheOtherWindowButApart_Separate()
    {
        // 300px 떨어진 두 조각: 옛 규칙은 합친다(BuildRegions_OverlappingWindows_Merged) — 영역 상자 사이가 60px라 붙이는 범위(22px씩)는 안 겹친다
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(1500, 2000, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12, splitReach: 22);
        try
        {
            Assert.Equal(2, regions.Count);
            Assert.All(regions, r => Assert.Equal(200 * 40, Cv2.CountNonZero(r.Erase)));   // 조각은 제 영역에만
            Assert.All(regions, r => Assert.Equal(1.0, RegenCrop.WindowFor(r.Bounds, mask.Size(), 1024).Scale));
        }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Theory]
    [InlineData(1270, 1)]   // 영역 상자가 겹친다
    [InlineData(1470, 1)]   // 사이 29px < 붙이는 범위 둘(44px) — 나중 것이 먼저 것의 결과를 덮는다 → 하나로
    [InlineData(1484, 1)]   // 사이 43px (타원은 880~1320을 칠해 상자의 오른쪽 끝이 1321이다)
    [InlineData(1485, 2)]   // 사이 44px — 붙이는 범위가 맞닿을 뿐 겹치지 않는다
    [InlineData(1490, 2)]
    public void BuildRegions_Split_MergesOnlyWhenThePasteRangesOverlap(int secondX, int expected)
    {
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(secondX, 2000, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12, splitReach: 22);
        try { Assert.Equal(expected, regions.Count); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_Split_ChainOfTouchingRegions_StillMergesIntoOne()
    {
        // 셋이 차례로 붙어 있으면 하나 — 가운데가 양쪽과 겹친다
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(1400, 2000, 200, 40), new Rect(1800, 2000, 200, 40));
        var regions = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12, splitReach: 22);
        try { Assert.Equal(3 * 200 * 40, Cv2.CountNonZero(Assert.Single(regions).Erase)); }
        finally { foreach (var x in regions) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_Split_PageOfScatteredPieces_KeepsEveryWindowAtFullScale()
    {
        // 1813x2560 쪽에 흩어진 조각 넷(사용자 원고 19쪽의 꼴 — 다시 그리는 자리는 쪽의 몇 %뿐): 옛 규칙은 줄줄이 합쳐 창이 쪽 전체가 된다
        var pieces = new[] { new Rect(300, 400, 160, 40), new Rect(1100, 900, 160, 40), new Rect(500, 1500, 160, 40), new Rect(1300, 2100, 160, 40) };
        using var mask = MaskWith(new Size(1813, 2560), pieces);
        var old = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12);
        var split = RegenRegion.BuildRegions(mask, [], 0.1, 0.6, mask.Size(), 1024, 0.12, splitReach: 22);
        try
        {
            Assert.True(RegenCrop.WindowFor(Assert.Single(old).Bounds, mask.Size(), 1024).Scale < 0.5);
            Assert.Equal(4, split.Count);
            Assert.All(split, r => Assert.Equal(1.0, RegenCrop.WindowFor(r.Bounds, mask.Size(), 1024).Scale));
        }
        finally { foreach (var x in old.Concat(split)) x.Dispose(); }
    }

    [Fact]
    public void BuildRegions_Split_EachRegionIsJudgedByItsOwnPieces()
    {
        // 옛 규칙이면 한 영역(남성 + 여성 → 섞임)인 두 조각이 따로가 되면 저마다 제 근거로 확정된다
        using var mask = MaskWith(new Size(3000, 4000), new Rect(1000, 2000, 200, 40), new Rect(1500, 2000, 200, 40));
        BodyPart[] parts = [new(BodyPartDetector.ClsPenis, 0.7f, new Rect(950, 1900, 300, 240)), new(BodyPartDetector.ClsPussy, 0.7f, new Rect(1450, 1900, 300, 240))];
        var old = RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, 0.12);
        var split = RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, 0.12, splitReach: 22);
        try
        {
            Assert.False(Assert.Single(old).Sure);
            Assert.Equal(2, split.Count);
            Assert.Equal((RegenKind.Male, true), (split.Single(r => r.Bounds.X < 1400).Sex, split.Single(r => r.Bounds.X < 1400).Sure));
            Assert.Equal((RegenKind.Female, true), (split.Single(r => r.Bounds.X > 1400).Sex, split.Single(r => r.Bounds.X > 1400).Sure));
            Assert.All(split, r => Assert.Null(r.SexMap));
        }
        finally { foreach (var x in old.Concat(split)) x.Dispose(); }
    }

    [Fact]
    public void SplitReachFor_IsThePasteReach_OnlyWhenTheSettingIsOn()
    {
        var s = new AppSettings { RegenEdgeGrow = 4, RegenEdgeBlur = 9, RegenCoreGrow = 6, RegenCoreBlur = 14 };
        Assert.Equal(22, RegenRegion.PasteReach(s));            // 넓은 쪽(6 + 14) + 2
        Assert.Equal(-1, RegenRegion.SplitReachFor(s));         // 꺼져 있으면 옛 규칙
        s.RegenSplitRegions = true;
        Assert.Equal(22, RegenRegion.SplitReachFor(s));
        s.RegenEdgeGrow = 30;
        Assert.Equal(41, RegenRegion.SplitReachFor(s));         // 30 + 9 + 2
    }
}
