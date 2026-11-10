using NekoManga.Core.Decensor;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>MosaicDetector.BuildBarMask — 흰/검은 막대 검열 검출.
/// 기준값은 앱 기본값(최소 비율 1.8·최소 면적 350px)을 쓰고, 마스크 확장은 0으로 둬 막대 자리 자체를 본다.</summary>
public class MosaicDetectorBarMaskTests
{
    private const double MinAspect = 1.8;
    private const int MinArea = 350;

    private static Mat? BlackOnly(Mat img, IReadOnlyList<Rect>? exclude = null) =>
        MosaicDetector.BuildBarMask(img, white: false, black: true, MinAspect, MinArea, grow: 0, exclude);

    private static Mat? WhiteOnly(Mat img) =>
        MosaicDetector.BuildBarMask(img, white: true, black: false, MinAspect, MinArea, grow: 0);

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(90f)]
    [InlineData(135f)]
    public void BuildBarMask_StraightBlackBar_IsMaskedAtAnyRotation(float angle)
    {
        // 단색 직사각형은 기울어져 있어도 막대다 — 채움비·비율을 회전 사각형으로 재기 때문이다.
        using var img = SolidPage(400, 400);
        var c = new Point2f(200, 200);
        var size = new Size2f(160, 28);
        FillRotated(img, c, size, angle, Black);
        using var bar = RotatedRegion(img, c, size, angle);

        using var mask = BlackOnly(img);

        Assert.True(Coverage(mask, bar) >= 0.97, $"막대 덮음 {Coverage(mask, bar):P1}");
        Assert.True(CountAll(mask) <= Cv2.CountNonZero(bar) * 1.1, $"막대 밖으로 번짐 {CountAll(mask)} vs {Cv2.CountNonZero(bar)}");
    }

    [Theory]
    [InlineData("disk")]          // 말줄임표 같은 원형 점: 원의 채움비(π/4)는 직사각형 기준에 못 미친다
    [InlineData("sStroke")]       // 굽이마다 굵어지는 S자 붓획: 휘고 굵기가 변해 곧은 띠로 덮이지 않는다
    [InlineData("commaStroke")]   // 굵은 머리에 휘며 가늘어지는 꼬리가 달린 붓획
    [InlineData("wavyStrand")]    // 물결치는 머리카락 한 가닥
    [InlineData("thinLine")]      // 패널 안쪽 선 같은 가는 선: 막대는 두껍다
    [InlineData("hugePanel")]     // 먹칠한 패널처럼 페이지의 1/4이 넘는 면: 검열이 아니다
    public void BuildBarMask_BlackShapeThatIsNotABar_IsNotMasked(string shape)
    {
        using var img = SolidPage(400, 400);
        switch (shape)
        {
            case "disk":
                Cv2.Circle(img, new Point(200, 200), 30, Black, -1);
                break;
            case "sStroke":
                // 굽이(꼭대기·바닥)에서 가장 굵고(~26px) 방향이 바뀌는 곳에서 가늘다
                DrawStroke(img, 240, t => (100 + 200 * t, 200 + 60 * Math.Sin(2 * Math.PI * t)),
                    t => 2 + 11 * Math.Abs(Math.Sin(2 * Math.PI * t)));
                break;
            case "commaStroke":
                Cv2.Circle(img, new Point(150, 150), 18, Black, -1);
                DrawStroke(img, 120, t => (150 + 80 * Math.Sin(t * Math.PI * 0.8), 150 + 80 * (1 - Math.Cos(t * Math.PI * 0.8))),
                    t => 1 + 16 * (1 - t));
                break;
            case "wavyStrand":
                DrawStroke(img, 300, t => (60 + 280 * t, 200 + 25 * Math.Sin(6 * Math.PI * t)),
                    t => 3 + 5 * Math.Abs(Math.Sin(6 * Math.PI * t)));
                break;
            case "thinLine":
                Cv2.Line(img, new Point(60, 200), new Point(340, 230), Black, 3);
                break;
            case "hugePanel":
                Cv2.Rectangle(img, new Rect(70, 70, 260, 260), Black, -1);
                break;
        }

        using var mask = BlackOnly(img);

        Assert.True(mask == null, $"{shape}: {CountAll(mask)}px가 막대로 잡혔다");
    }

    [Fact(Skip = "알려진 결함: DecomposeBars가 C자로 말리며 굵기가 4→24→4px로 변하는 붓획(중심 반지름 30px, 270°)의 93.7%를 곧은 띠로 덮어 막대로 받아들인다 — 문서 주석은 휘고 굵기가 변하는 붓획은 덮이지 않는다고 한다")]
    public void BuildBarMask_ThickCurledStroke_IsNotABar()
    {
        // 효과음 'つ'·말린 머리카락 같은 C자 붓획 — 휘고 굵기가 변하므로 곧은 띠들의 합으로 설명되지 않아야 한다.
        using var img = SolidPage(400, 400);
        DrawStroke(img, 270, t => (200 + 30 * Math.Cos(t * 1.5 * Math.PI), 200 + 30 * Math.Sin(t * 1.5 * Math.PI)),
            t => 2 + 10 * Math.Sin(Math.PI * t));

        using var mask = BlackOnly(img);

        Assert.True(mask == null, $"말린 붓획 {CountAll(mask)}px가 막대로 잡혔다");
    }

    [Theory]
    [InlineData("cross")]   // X자로 교차한 두 막대
    [InlineData("corner")]  // 모서리에서 만난 두 막대(ㄱ자)
    [InlineData("tee")]     // 한 막대 가운데에 다른 막대가 붙은 T자
    public void BuildBarMask_TouchingOrCrossingBars_AreRecovered(string layout)
    {
        // 맞닿은 막대 뭉치는 통째로는 직사각형도 길쭉하지도 않지만, 곧은 띠들의 합으로 설명되므로 막대로 되살린다.
        using var img = SolidPage(400, 400);
        using var region = EmptyMask(img);
        var bars = layout switch
        {
            "cross" => new[] { (new Point2f(200, 200), new Size2f(180, 22), 0f), (new Point2f(200, 200), new Size2f(180, 22), 60f) },
            "corner" => new[] { (new Point2f(170, 111), new Size2f(140, 22), 0f), (new Point2f(111, 170), new Size2f(140, 22), 90f) },
            _ => new[] { (new Point2f(200, 120), new Size2f(180, 22), 0f), (new Point2f(200, 191), new Size2f(120, 22), 90f) },
        };
        foreach (var (c, s, a) in bars)
        {
            FillRotated(img, c, s, a, Black);
            FillRotated(region, c, s, a, White);
        }

        using var mask = BlackOnly(img);

        Assert.True(Coverage(mask, region) >= 0.95, $"{layout}: 막대 뭉치 덮음 {Coverage(mask, region):P1}");
    }

    [Theory]
    [InlineData(18, 26, true)]   // 실제 원고에 있던 짧고 뭉툭한 막대 크기대: 작고 꽉 찬 사각형은 막대
    [InlineData(74, 53, false)]  // 효과음 붓획 토막 크기: 크고 뭉툭한 덩어리는 막대가 아니다
    public void BuildBarMask_StubbyBlock_CountsOnlyWhenSmall(int w, int h, bool expected)
    {
        using var img = SolidPage(300, 300);
        var block = new Rect(140, 120, w, h);
        Cv2.Rectangle(img, block, Black, -1);

        using var mask = BlackOnly(img);

        if (expected) Assert.True(Coverage(mask, block) >= 0.97, $"덮음 {Coverage(mask, block):P1}");
        else Assert.Null(mask);
    }

    [Fact]
    public void BuildBarMask_BandAcrossPageEdge_IsIgnoredButSmallBarAtEdgeIsKept()
    {
        // 페이지 끝을 가로지르는 띠는 여백·패널 간격이고, 끝에 걸쳐 있어도 작은 조각은 화면 끝에 걸친 검열 막대다.
        using var img = SolidPage(400, 300);
        var margin = new Rect(0, 0, 400, 30);
        var edgeBar = new Rect(0, 150, 90, 24);
        Cv2.Rectangle(img, margin, Black, -1);
        Cv2.Rectangle(img, edgeBar, Black, -1);

        using var mask = BlackOnly(img);

        Assert.Equal(0, CountIn(mask, margin));
        Assert.True(Coverage(mask, edgeBar) >= 0.97, $"가장자리 막대 덮음 {Coverage(mask, edgeBar):P1}");
    }

    [Fact]
    public void BuildBarMask_BarTouchingExcludedRect_IsSkipped()
    {
        // 말풍선 같은 제외 영역과 겹치는 후보는 막대 모양이어도 버린다(글자 보호).
        using var img = SolidPage(400, 300);
        var nearBubble = new Rect(60, 60, 160, 26);
        var free = new Rect(60, 200, 160, 26);
        Cv2.Rectangle(img, nearBubble, Black, -1);
        Cv2.Rectangle(img, free, Black, -1);
        var bubble = new Rect(200, 20, 120, 80);   // nearBubble 오른쪽 끝을 살짝 덮는다

        using var mask = BlackOnly(img, [bubble]);

        Assert.Equal(0, CountIn(mask, nearBubble));
        Assert.True(Coverage(mask, free) >= 0.97, $"제외 영역 밖 막대 덮음 {Coverage(mask, free):P1}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BuildBarMask_ToneFlags_SelectOnlyRequestedTone(bool white, bool black)
    {
        // 한 번에 한 종류 원칙 — 요청한 색조의 막대만 잡고, 아무것도 요청하지 않으면 마스크가 없다.
        using var img = SolidPage(400, 300, gray: 128);
        var whiteBar = new Rect(60, 60, 160, 26);
        var blackBar = new Rect(60, 200, 160, 26);
        Cv2.Rectangle(img, whiteBar, White, -1);
        Cv2.Rectangle(img, blackBar, Black, -1);

        using var mask = MosaicDetector.BuildBarMask(img, white, black, MinAspect, MinArea, grow: 0);

        if (!white && !black) { Assert.Null(mask); return; }
        Assert.Equal(white, Coverage(mask, whiteBar) >= 0.97);
        Assert.Equal(black, Coverage(mask, blackBar) >= 0.97);
        if (!white) Assert.Equal(0, CountIn(mask, whiteBar));
        if (!black) Assert.Equal(0, CountIn(mask, blackBar));
    }

    [Fact]
    public void BuildBarMask_WhiteBarOnArtwork_IsMaskedButThickWhitePanelIsNot()
    {
        // 흰 막대는 얇다 — 짧은 변이 두꺼운 흰 면(문짝·벽 같은 그림)은 막대 모양이어도 지우면 안 된다.
        using var img = SolidPage(400, 300, gray: 128);
        var bar = new Rect(40, 40, 200, 30);
        var panel = new Rect(40, 150, 240, 100);
        Cv2.Rectangle(img, bar, White, -1);
        Cv2.Rectangle(img, panel, White, -1);

        using var mask = WhiteOnly(img);

        Assert.True(Coverage(mask, bar) >= 0.97, $"흰 막대 덮음 {Coverage(mask, bar):P1}");
        Assert.Equal(0, CountIn(mask, panel));
    }

    [Fact]
    public void BuildBarMask_StackedWhiteStripesOverFlatTone_AreTreatedAsDecoration()
    {
        // 평행한 흰 띠 여러 줄 사이가 밋밋한 톤이면 장식 줄무늬 패널이다 — 통째로 지우면 그림이 망가진다.
        using var img = SolidPage(400, 360, gray: 150);
        foreach (var r in StripeRects()) Cv2.Rectangle(img, r, White, -1);

        using var mask = WhiteOnly(img);

        Assert.True(mask == null, $"장식 줄무늬 {CountAll(mask)}px가 검열로 잡혔다");
    }

    [Fact]
    public void BuildBarMask_StackedWhiteStripesOverDrawing_AreKeptAsHatchedCensorship()
    {
        // 같은 평행 띠라도 사이에 그림(선화)이 있으면 빗금 검열이다 — 띠를 전부 지워야 한다.
        using var img = SolidPage(400, 360, gray: 150);
        for (int x = 100; x < 300; x += 12)
            Cv2.Rectangle(img, new Rect(x, 90, 6, 140), Black, -1);
        var stripes = StripeRects();
        foreach (var r in stripes) Cv2.Rectangle(img, r, White, -1);

        using var mask = WhiteOnly(img);

        foreach (var r in stripes)
            Assert.True(Coverage(mask, r) >= 0.97, $"띠 {r} 덮음 {Coverage(mask, r):P1}");
    }

    /// <summary>위아래로 16px 간격을 두고 쌓은 흰 띠 네 줄(200×14).</summary>
    private static Rect[] StripeRects() =>
        [new(100, 100, 200, 14), new(100, 130, 200, 14), new(100, 160, 200, 14), new(100, 190, 200, 14)];

    /// <summary>중심선 path(t)를 따라 반지름 radius(t)인 원을 촘촘히 찍어 굵기가 변하는 붓획을 그린다 (t = 0…1).</summary>
    private static void DrawStroke(Mat img, int steps, Func<double, (double X, double Y)> path, Func<double, double> radius)
    {
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            var (x, y) = path(t);
            Cv2.Circle(img, new Point((int)Math.Round(x), (int)Math.Round(y)), (int)Math.Round(radius(t)), Black, -1);
        }
    }
}
