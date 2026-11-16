using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>질내사정 태그를 조각마다(2026-11-16 사용자 "여자거에서 구멍에 전부다 질내사정으로 나오는데 실제 이미지에서 질내사정한 부분만 그렇게 나왔으면 좋겠어").
/// 여성 태그 줄에 늘 들어 있던 'cum in pussy'를 떼어, 질내사정인 조각(사용자가 고른 것 → 없으면 자동 판단)이 있는 영역에만 여성 태그 뒤에 더한다.
/// 모델은 쓰지 않는다 — 자동 판단은 점수 함수(CumRule)를 갈아 끼워 본다.</summary>
public class RegenCumTests
{
    private const int M = BodyPartDetector.ClsPenis, F = BodyPartDetector.ClsPussy;
    private static readonly Size Page = new(3000, 4000);
    private static readonly Rect PieceA = new(1400, 1900, 200, 40), BoxA = new(1300, 1700, 400, 400);
    private static readonly Rect PieceFar = new(2500, 3500, 200, 40), BoxFar = new(2400, 3350, 400, 400);

    private static Mat MaskWith(Size img, params Rect[] pieces)
    {
        var m = new Mat(img, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in pieces) m.Rectangle(r, Scalar.White, -1);
        return m;
    }

    private static List<RegenRegionInfo> Regions(Mat mask, BodyPart[] parts, IReadOnlyList<SexMark>? marks = null, CumRule? cum = null) =>
        RegenRegion.BuildRegions(mask, parts, 0.1, 0.6, mask.Size(), 1024, strongConf: 0.12, marks, cum: cum);

    private static void DisposeAll(IEnumerable<RegenRegionInfo> regions) { foreach (var r in regions) r.Dispose(); }

    private static AppSettings S(string cum = "cum in pussy") =>
        new() { RegenBodyTagsMale = "penis", RegenBodyTagsFemale = "pussy", RegenBodyTagsCum = cum };

    // ── 표시의 글 ──

    [Fact]
    public void Marks_CumOnAndOff_AreWrittenAsCAndN_AndReadBack()
    {
        SexMark[] marks = [new(new Point(10, 20), RegenKind.Female), new(new Point(10, 20), RegenKind.Unknown, true), new(new Point(300, 4), RegenKind.Unknown, false)];
        var text = SexMarks.Format(marks);
        Assert.Equal("10,20,F;10,20,C;300,4,N", text);
        Assert.Equal(marks, SexMarks.Parse(text));
    }

    [Fact]
    public void Marks_Scaled_KeepTheCumChoice()
    {
        var scaled = SexMarks.Scaled([new SexMark(new Point(10, 20), RegenKind.Unknown, true)], 2, 3);
        Assert.Equal(new SexMark(new Point(20, 60), RegenKind.Unknown, true), Assert.Single(scaled));
    }

    // ── 조각마다 ──

    [Fact]
    public void NoMarks_NoRule_NothingIsCum()
    {
        using var mask = MaskWith(Page, PieceA);
        var regions = Regions(mask, [new BodyPart(F, 0.7f, BoxA)]);
        try { var r = Assert.Single(regions); Assert.False(r.Cum); Assert.False(Assert.Single(r.Pieces).Cum); }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void UserMarkOn_MakesThePieceAndItsRegionCum_AndDoesNotChangeTheSex()
    {
        using var mask = MaskWith(Page, PieceA, PieceFar);
        var regions = Regions(mask, [new BodyPart(F, 0.7f, BoxA), new BodyPart(F, 0.7f, BoxFar)], [new SexMark(new Point(1500, 1920), RegenKind.Unknown, true)]);
        try
        {
            var marked = Assert.Single(regions, r => r.Bounds.Y < 3000); var other = Assert.Single(regions, r => r.Bounds.Y > 3000);
            Assert.True(marked.Cum);
            Assert.Equal((true, true, RegenKind.Female, false), (marked.Pieces[0].Cum, marked.Pieces[0].CumByUser, marked.Pieces[0].Sex, marked.Pieces[0].ByUser));
            Assert.Equal((RegenKind.Female, true), (marked.Sex, marked.Sure));
            Assert.False(other.Cum);   // 표시하지 않은 조각의 영역은 그대로
        }
        finally { DisposeAll(regions); }
    }

    [Theory]
    [InlineData(0.36f, true)]
    [InlineData(0.35f, true)]    // 임계와 같으면 사정
    [InlineData(0.34f, false)]
    public void AutoRule_DecidesUnmarkedPieces_ByTheThreshold(float score, bool expected)
    {
        using var mask = MaskWith(Page, PieceA);
        var asked = new List<Rect>();
        var regions = Regions(mask, [new BodyPart(F, 0.7f, BoxA)], cum: new CumRule(box => { asked.Add(box); return score; }, 0.35f));
        try
        {
            var r = Assert.Single(regions); var p = Assert.Single(r.Pieces);
            Assert.Equal((expected, expected, false), (p.Cum, p.CumAuto, p.CumByUser));
            Assert.Equal(expected, r.Cum);
            Assert.Equal([PieceA], asked);   // 조각의 상자로, 한 번만 묻는다
        }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void UserMarkOff_BeatsTheAutoRule_AndTheRuleIsNotAsked()
    {
        using var mask = MaskWith(Page, PieceA);
        int asked = 0;
        var regions = Regions(mask, [new BodyPart(F, 0.7f, BoxA)], [new SexMark(new Point(1500, 1920), RegenKind.Unknown, false)], new CumRule(_ => { asked++; return 1f; }, 0.35));
        try
        {
            var r = Assert.Single(regions);
            Assert.False(r.Cum);
            Assert.Equal((false, true), (r.Pieces[0].Cum, r.Pieces[0].CumByUser));
            Assert.Equal(0, asked);
        }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void MalePieces_AreNeverCum_AndAreNotAsked()
    {
        // 남성으로 정해진 조각은 사정 태그가 들어갈 자리가 아니다 — 자동 판단도 묻지 않고, 표시가 걸려 있어도 치지 않는다
        using var mask = MaskWith(Page, PieceA);
        int asked = 0;
        var regions = Regions(mask, [new BodyPart(M, 0.7f, BoxA)], [new SexMark(new Point(1500, 1920), RegenKind.Unknown, true)], new CumRule(_ => { asked++; return 1f; }, 0.35));
        try { var r = Assert.Single(regions); Assert.False(r.Cum); Assert.False(r.Pieces[0].Cum); Assert.Equal(0, asked); }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void PieceTheUserSetToMale_IsNotAsked_OneSetToFemaleIs()
    {
        using var mask = MaskWith(Page, PieceA, PieceFar);
        var asked = new List<Rect>();
        var regions = Regions(mask, [], [new SexMark(new Point(1500, 1920), RegenKind.Male), new SexMark(new Point(2600, 3520), RegenKind.Female)],
            new CumRule(box => { asked.Add(box); return 1f; }, 0.35));
        try { Assert.Equal([PieceFar], asked); Assert.True(Assert.Single(regions, r => r.Bounds.Y > 3000).Cum); Assert.False(Assert.Single(regions, r => r.Bounds.Y < 3000).Cum); }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void UnknownPieces_AreAsked_Specks_AreNot()
    {
        // 근거 없는 조각(모름)은 남녀 태그를 같이 받는 자리라 묻는다. 티끌(큰 조각 옆의 작은 조각)은 묻지 않는다
        using var mask = MaskWith(Page, PieceA, new Rect(1650, 1910, 6, 6));
        var asked = new List<Rect>();
        var regions = Regions(mask, [], cum: new CumRule(box => { asked.Add(box); return 0.9f; }, 0.35));
        try
        {
            var r = Assert.Single(regions);
            Assert.True(r.Cum);
            Assert.Equal([PieceA], asked);
            Assert.False(Assert.Single(r.Pieces, p => p.Speck).Cum);
        }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void SmallPieceWithACumMark_IsNotASpeck_ItsMarkCounts()
    {
        // 큰 조각 옆의 작은 조각에 사정을 골라 두었다 — 티끌로 빠지면 그 표시가 말없이 버려진다(결과 창에 표시도 안 떠 고칠 수도 없다)
        var small = new Rect(1650, 1910, 6, 6);
        using var mask = MaskWith(Page, PieceA, small);
        var regions = Regions(mask, [], [new SexMark(new Point(1652, 1912), RegenKind.Unknown, true)]);
        try
        {
            var r = Assert.Single(regions);
            var p = Assert.Single(r.Pieces, x => x.Box == small);
            Assert.Equal((false, true, true), (p.Speck, p.Cum, p.CumByUser));
            Assert.True(r.Cum);
        }
        finally { DisposeAll(regions); }
    }

    [Fact]
    public void Cum_DoesNotChangeTheRegionShape()
    {
        using var mask = MaskWith(Page, PieceA, PieceFar);
        BodyPart[] parts = [new(F, 0.7f, BoxA)];
        var plain = Regions(mask, parts);
        var withCum = Regions(mask, parts, [new SexMark(new Point(1500, 1920), RegenKind.Unknown, true)], new CumRule(_ => 1f, 0.35));
        try
        {
            Assert.Equal(plain.Select(r => r.Bounds), withCum.Select(r => r.Bounds));
            Assert.Equal(plain.Select(r => Cv2.CountNonZero(r.Region)), withCum.Select(r => Cv2.CountNonZero(r.Region)));
            Assert.Equal(plain.Select(r => (r.Sex, r.Kind)), withCum.Select(r => (r.Sex, r.Kind)));
        }
        finally { DisposeAll(plain.Concat(withCum)); }
    }

    // ── 표시 고치기 ──

    [Fact]
    public void WithCum_ReplacesOnlyThatPiecesCumMark()
    {
        using var mask = MaskWith(Page, PieceA, PieceFar);
        List<SexMark> marks = [new(new Point(1500, 1920), RegenKind.Female), new(new Point(1500, 1920), RegenKind.Unknown, true), new(new Point(2600, 3520), RegenKind.Unknown, true)];
        var off = RegenRegion.WithCum(mask, marks, new Point(1450, 1915), false, out bool onPiece);
        Assert.True(onPiece);
        Assert.Equal("1500,1920,F;2600,3520,C;1450,1915,N", SexMarks.Format(off));
        var auto = RegenRegion.WithCum(mask, off, new Point(1450, 1915), null, out _);
        Assert.Equal("1500,1920,F;2600,3520,C", SexMarks.Format(auto));
        RegenRegion.WithCum(mask, marks, new Point(100, 100), true, out bool none);   // 그 점에 조각이 없다
        Assert.False(none);
    }

    [Fact]
    public void WithSex_LeavesTheCumMarkOfThatPiece()
    {
        using var mask = MaskWith(Page, PieceA);
        List<SexMark> marks = [new(new Point(1500, 1920), RegenKind.Female), new(new Point(1500, 1920), RegenKind.Unknown, true)];
        Assert.Equal("1500,1920,C;1450,1915,M", SexMarks.Format(RegenRegion.WithSex(mask, marks, new Point(1450, 1915), RegenKind.Male, out _)));
        Assert.Equal("1500,1920,C", SexMarks.Format(RegenRegion.WithSex(mask, marks, new Point(1450, 1915), null, out _)));
    }

    [Fact]
    public void ResolveMarks_KeepsOneSexAndOneCumMarkPerPiece_DropsStrayOnes()
    {
        using var mask = MaskWith(Page, PieceA, PieceFar);
        var resolved = RegenRegion.ResolveMarks(mask, [
            new SexMark(new Point(1410, 1901), RegenKind.Unknown, true), new SexMark(new Point(1590, 1935), RegenKind.Unknown, false),   // 한 조각에 넣기와 빼기 → 넣기(나중 것이 이기는 것이 아니다)
            new SexMark(new Point(1500, 1920), RegenKind.Female),
            new SexMark(new Point(2600, 3520), RegenKind.Unknown, false),
            new SexMark(new Point(50, 50), RegenKind.Unknown, true)]);   // 어느 조각의 것도 아니다
        Assert.Equal("1500,1920,F;1500,1920,C;2600,3520,N", SexMarks.Format(resolved));
    }

    // ── 태그 ──

    [Theory]
    [InlineData(RegenKind.Female, true, true, "pussy, cum in pussy")]
    [InlineData(RegenKind.Female, true, false, "pussy")]                    // 사정인 조각이 없으면 여성 태그만 — 전에는 여기에도 늘 들어갔다
    [InlineData(RegenKind.Male, true, true, "penis")]                       // 확정된 남성 영역에는 넣지 않는다
    [InlineData(RegenKind.Both, false, true, "penis, pussy, cum in pussy")] // 남녀를 같이 넣는 영역
    [InlineData(RegenKind.Unknown, false, false, "penis, pussy")]
    public void Tags_CumIsAddedOnlyWhereFemaleTagsGo(RegenKind kind, bool sure, bool cum, string expected)
    {
        Assert.Equal(expected, RegenTags.For(kind, sure, S(), cum));
    }

    [Fact]
    public void Tags_EmptyCumBox_AddsNothing_AndTheNoteDoesNotMentionIt()
    {
        Assert.Equal("pussy", RegenTags.For(RegenKind.Female, true, S(cum: "  "), cum: true));
        Assert.Equal(RegenTags.Note(RegenKind.Female, true, S(cum: "")), RegenTags.Note(RegenKind.Female, true, S(cum: ""), cum: true));
        Assert.NotEqual(RegenTags.Note(RegenKind.Female, true, S()), RegenTags.Note(RegenKind.Female, true, S(), cum: true));   // 들어가면 진행 줄에 보인다
        Assert.Equal(RegenTags.Note(RegenKind.Male, true, S()), RegenTags.Note(RegenKind.Male, true, S(), cum: true));
    }

    [Fact]
    public void Areas_CumGoesToTheFemaleSide_NotTheMaleSide()
    {
        using var map = new Mat(100, 200, MatType.CV_8UC1, Scalar.Black);
        map.Rectangle(new Rect(10, 40, 20, 20), new Scalar(RegenRegion.MaleCode), -1);
        map.Rectangle(new Rect(170, 40, 20, 20), new Scalar(RegenRegion.FemaleCode), -1);
        var s = S(); s.RegenTagsBySex = true; s.RegenTagsPerPiece = true;
        var areas = RegenSexAreas.Split(map, s, cum: true);
        try { Assert.Equal(["penis", "pussy, cum in pussy"], areas!.Select(a => a.Tags)); }
        finally { foreach (var a in areas!) a.Dispose(); }
    }

    // ── 감지기의 창·입력(모델 없이) ──

    [Fact]
    public void Detector_Window_IsASquareAroundThePiece_AtLeast448_InsideTheImage()
    {
        Assert.Equal(new Rect(1200, 1620, 600, 600), CumDetector.WindowFor(PieceA, Page, 3));     // 긴 변 200 × 3
        Assert.Equal(new Rect(1000, 1420, 1000, 1000), CumDetector.WindowFor(PieceA, Page, 5));
        Assert.Equal(new Rect(0, 0, 448, 448), CumDetector.WindowFor(new Rect(5, 5, 30, 30), Page, 3));   // 작은 조각은 448, 모서리에서는 안으로
        Assert.Equal(new Rect(200, 0, 600, 600), CumDetector.WindowFor(new Rect(300, 200, 400, 100), new Size(800, 600), 5));   // 그림의 짧은 변을 넘지 않는다
    }

    [Fact]
    public void Detector_Preprocess_IsBgr0To255_448Square()
    {
        using var img = new Mat(600, 800, MatType.CV_8UC3, new Scalar(10, 120, 250));
        var data = CumDetector.Preprocess(img, new Rect(100, 0, 600, 600));
        Assert.Equal(448 * 448 * 3, data.Length);
        Assert.Equal([10f, 120f, 250f], data[..3]);
        Assert.Equal([10f, 120f, 250f], data[^3..]);
        // 줄일 때는 넓이 평균(Area) — 한 줄 건너 검정·흰색인 896 그림은 448에서 고른 회색이 된다(가장 가까운 픽셀을 집으면 0이나 255)
        using var stripes = new Mat(896, 896, MatType.CV_8UC3, Scalar.Black);
        for (int x = 0; x < 896; x += 2) stripes.Line(new Point(x, 0), new Point(x, 895), Scalar.White);
        Assert.All(CumDetector.Preprocess(stripes, new Rect(0, 0, 896, 896)), v => Assert.InRange(v, 126f, 129f));
        using var gray = new Mat(600, 800, MatType.CV_8UC1, new Scalar(77));
        Assert.Equal([77f, 77f, 77f], CumDetector.Preprocess(gray, new Rect(0, 0, 448, 448))[..3]);
    }

    [Fact]
    public void Detector_Model_RuntimeFileFirst_ThenTheComfyNodesCopy_ElseNotReady()
    {
        using var t = new TempDir();
        string runtime = t.At("rt"), comfy = t.At("comfy");
        using var env = new OnnxEnv(new AppSettings { RuntimeRoot = runtime });
        Assert.False(new CumDetector(env, comfy).IsReady);
        Assert.False(new CumDetector(env, null).IsReady);

        var nodeDir = Path.Combine(comfy, "ComfyUI", "custom_nodes", "ComfyUI-WD14-Tagger", "models");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllBytes(Path.Combine(nodeDir, CumDetector.ComfyNodeModel), [1]);
        Assert.Equal(Path.Combine(nodeDir, CumDetector.ComfyNodeModel), new CumDetector(env, comfy).Model);

        Directory.CreateDirectory(Path.Combine(runtime, "models"));
        File.WriteAllBytes(Path.Combine(runtime, "models", CumDetector.ModelFile), [1]);
        Assert.Equal(CumDetector.ModelFile, new CumDetector(env, comfy).Model);
    }
}
