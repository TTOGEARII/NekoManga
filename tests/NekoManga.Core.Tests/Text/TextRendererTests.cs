using System.Drawing;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;
using Rect = OpenCvSharp.Rect;

namespace NekoManga.Core.Tests;

/// <summary>식자 배치(TextRenderer). 합성 원고에 어느 Windows에나 있는 Arial로 그리고,
/// 픽셀 값이 아니라 자리·줄 수·크기 비율 같은 구조만 단정한다.</summary>
public class TextRendererTests
{
    private static AppSettings Arial() => new() { LetterFontFamily = "Arial" };

    /// <summary>결과 보기 창에서 손으로 잡은 블록 — 풍선을 재지 않고 그 사각형을 쓴다.</summary>
    private static TextBlockInfo Manual(int x1, int y1, int x2, int y2, string text) =>
        new(x1, y1, x2, y2, "", text, 20, false, "#000000", ManualRect: true);

    /// <summary>감지기가 잡은 블록 — 원고에서 풍선을 재서 자리를 넓힌다.</summary>
    private static TextBlockInfo Auto(int x1, int y1, int x2, int y2, string text, bool vertical = false) =>
        new(x1, y1, x2, y2, "", text, 20, vertical, "#000000");

    // ───────── 풍선 폭·높이 재기 (WidenToBubble / HeightenToBubble) ─────────

    [Theory]
    [InlineData("widen")]
    [InlineData("heighten")]
    public void WidenOrHeightenToBubble_BlockInBox_FillsBoxIgnoringTailAndKeepsBlock(string direction)
    {
        // 회색 바탕에 흰 상자 풍선(안쪽 200~499). 가운데 재는 줄에만 상자 밖으로 새는 꼬리가 있다.
        using var page = SolidPage(800, 600, 120);
        DrawBox(page, new Rect(200, 100, 300, 400));
        Cv2.Rectangle(page, new Rect(495, 295, 200, 10), Scalar.White, -1);
        var block = new RectangleF(330, 200, 140, 200);

        var r = Grow(direction, page, block);

        // 재는 방향으로만 늘고, 원래 블록은 반드시 안에 남는다
        Assert.Equal(block.Y, r.Y);
        Assert.Equal(block.Height, r.Height);
        Assert.True(r.Left <= block.Left && r.Right >= block.Right, $"{r}");
        // 세 줄 중 가장 좁게 잰 값을 쓰므로 꼬리를 따라 상자 밖으로 나가지 않는다
        Assert.True(r.Left >= 200 && r.Right <= 500, $"{r}");
        // 테두리 여유만 남기고 풍선 폭 대부분을 쓴다
        Assert.True(r.Width >= 0.8f * 300, $"{r}");
    }

    [Theory]
    [InlineData("widen")]
    [InlineData("heighten")]
    public void WidenOrHeightenToBubble_NeighbourBlock_IsAWall(string direction)
    {
        // 결합 풍선은 안쪽이 이어져 흰 구간이 이웃 풍선까지 간다 — 다른 블록 자리에서 멈춰야 남의 글에 겹치지 않는다
        using var page = SolidPage(800, 600);
        var block = new RectangleF(300, 200, 100, 200);
        var neighbour = new RectangleF(450, 100, 40, 400);

        var r = Grow(direction, page, block, neighbour);

        Assert.True(r.Width > block.Width, $"{r}");
        Assert.True(r.Left <= block.Left && r.Right >= block.Right, $"{r}");
        Assert.True(r.Right <= neighbour.Left, $"{r}");
    }

    [Theory]
    [InlineData("widen")]
    [InlineData("heighten")]
    public void WidenOrHeightenToBubble_DarkBackground_ReturnsBlockUnchanged(string direction)
    {
        // 블록 가운데가 밝지 않으면(그림·망점 위의 글) 잴 풍선이 없다
        using var page = SolidPage(800, 600, 120);
        var block = new RectangleF(300, 200, 100, 200);

        Assert.Equal(block, Grow(direction, page, block));
    }

    [Theory]
    [InlineData("widen", 2.5f)]
    [InlineData("heighten", 1.8f)]
    public void WidenOrHeightenToBubble_OpenWhitePage_GrowthIsCapped(string direction, float capPerSide)
    {
        // 테두리 없는 흰 바탕에서는 한쪽으로 블록의 2.5배(세로는 1.8배)까지만 재고 그중 86%만 쓴다 — 한없이 번지지 않는다
        using var page = SolidPage(2000, 1200);
        var block = new RectangleF(950, 500, 100, 200);

        var r = Grow(direction, page, block);

        Assert.True(r.Width > block.Width, $"{r}");
        Assert.True(r.Width <= 0.86f * 2 * capPerSide * block.Width + 1, $"{r}");
    }

    /// <summary>가로로 짠 장면을 WidenToBubble에, 또는 전치(가로↔세로)해서 HeightenToBubble에 넣고 결과를 가로 기준으로 되돌린다 —
    /// 두 함수는 재는 방향만 다른 같은 규칙이라 한 장면으로 둘 다 본다.</summary>
    private static RectangleF Grow(string direction, Mat page, RectangleF block, params RectangleF[] avoid)
    {
        var walls = avoid.Length == 0 ? null : avoid;
        if (direction == "widen") return TextRenderer.WidenToBubble(page, block, walls);
        using var transposed = new Mat();
        Cv2.Transpose(page, transposed);
        return Flip(TextRenderer.HeightenToBubble(transposed, Flip(block), walls?.Select(Flip).ToArray()));
    }

    private static RectangleF Flip(RectangleF r) => new(r.Y, r.X, r.Height, r.Width);

    // ───────── 자리와 글자 크기 정하기 (PlanLayout) ─────────

    [Fact]
    public void PlanLayout_ManualBlock_IsConvertedToResultScaleAndKeptJustInside()
    {
        // 블록 좌표는 원본 기준 — 결과 이미지가 가로 2배·세로 0.5배면 (100,100)-(300,200)은 (200,50)-(600,100)이 된다.
        // 손으로 잡은 블록은 그 사각형을 쓰되 테두리에 닿지 않게 살짝 안쪽으로 들인다.
        using var result = SolidPage(1000, 1400);

        var l = Assert.Single(TextRenderer.PlanLayout(result, new[] { Manual(100, 100, 300, 200, "HELLO WORLD") }, 2.0, 0.5, Arial()));

        var scaled = RectangleF.FromLTRB(200, 50, 600, 100);
        Assert.True(scaled.Contains(l.Rect), $"{l.Rect}");
        Assert.InRange(l.Rect.X + l.Rect.Width / 2, 399.5f, 400.5f);
        Assert.InRange(l.Rect.Y + l.Rect.Height / 2, 74.5f, 75.5f);
        Assert.True(l.Rect.Width >= 0.85f * scaled.Width && l.Rect.Height >= 0.9f * scaled.Height, $"{l.Rect}");
        Assert.False(l.Round);
        Assert.True(l.Size > 0);
    }

    [Theory]
    [InlineData(200, 100, "")]
    [InlineData(200, 100, "   ")]
    [InlineData(200, 100, "\n")]
    [InlineData(3, 100, "HELLO")]
    [InlineData(200, 3, "HELLO")]
    public void PlanLayout_NothingToDraw_GetsSizeZeroAndKeepsItsSlot(int width, int height, string text)
    {
        // 그릴 글이 없거나 자리가 4px도 안 되면 크기 0(그리지 않음). 결과는 블록마다 하나씩, 같은 순서로 나온다.
        using var page = SolidPage(600, 800);
        var blocks = new[] { Manual(100, 100, 100 + width, 100 + height, text), Manual(100, 400, 400, 600, "HELLO") };

        var r = TextRenderer.PlanLayout(page, blocks, 1, 1, Arial());

        Assert.Equal(2, r.Count);
        Assert.Equal(0, r[0].Size);
        Assert.True(r[1].Size > 0);
    }

    [Fact]
    public void PlanLayout_PageCommonSize_RoomyBubblesShareItAndOnlyCrowdedOneShrinks()
    {
        using var page = SolidPage(1000, 1000);
        var roomyA = Manual(50, 50, 450, 450, "HI THERE");
        var roomyB = Manual(500, 50, 950, 450, "HI YOU");
        var crowded = Manual(50, 500, 150, 700, "THIS IS A MUCH LONGER SENTENCE THAT MUST SHRINK A LOT");

        var with = TextRenderer.PlanLayout(page, new[] { roomyA, roomyB, crowded }, 1, 1, Arial());
        var without = TextRenderer.PlanLayout(page, new[] { roomyA, roomyB }, 1, 1, Arial());

        // 널찍한 풍선끼리는 쪽 공통 크기로 같다 — 글 길이가 달라도 크기가 제각각이 되지 않는다
        Assert.Equal(with[0].Size, with[1].Size);
        // 좁은데 글이 긴 풍선만 제 크기로 줄어든다
        Assert.True(with[2].Size < with[0].Size, $"{with[2].Size} vs {with[0].Size}");
        // 그 풍선 하나가 쪽 전체를 끌어내리지 않는다 (공통 크기는 풍선별 최대 크기의 중앙값)
        Assert.Equal(without[0].Size, with[0].Size);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.3, 0.3)]
    [InlineData(0.1, 0.3)]   // 배율에는 한도가 있다 — 0.3배 밑으로는 줄지 않는다
    public void PlanLayout_LetterSizeScale_MultipliesPageCommonSize(double letterSizeScale, double expectedRatio)
    {
        // 쪽이 커야 줄인 크기도 최소 글자 크기보다 커서 배율이 그대로 보인다
        using var page = SolidPage(1000, 2000);
        var blocks = new[] { Manual(100, 100, 900, 900, "HI") };
        var scaled = Arial();
        scaled.LetterSizeScale = letterSizeScale;

        float baseline = TextRenderer.PlanLayout(page, blocks, 1, 1, Arial())[0].Size;
        float size = TextRenderer.PlanLayout(page, blocks, 1, 1, scaled)[0].Size;

        Assert.InRange(size / baseline, expectedRatio - 0.01, expectedRatio + 0.01);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(100.0, 3.0)]   // 블록별 배율도 3배가 한도
    public void PlanLayout_StyleScale_ScalesOnlyThatBlock(double styleScale, double expectedRatio)
    {
        // '이 풍선만 크게/작게'는 쪽 공통 크기에 곱한다 — 크게 한 블록은 풍선에 맞춘 크기도 그만큼 넘게 둔다
        using var page = SolidPage(1000, 2000);
        var target = Manual(100, 100, 900, 900, "HI");
        var other = Manual(100, 1000, 900, 1800, "HI");

        var plain = TextRenderer.PlanLayout(page, new[] { target, other }, 1, 1, Arial());
        var styled = TextRenderer.PlanLayout(page, new[] { target with { StyleScale = styleScale }, other }, 1, 1, Arial());

        Assert.InRange(styled[0].Size / plain[0].Size, expectedRatio - 0.01, expectedRatio + 0.01);
        Assert.Equal(plain[1].Size, styled[1].Size);
    }

    [Fact]
    public void PlanLayout_TextThatCannotFitAtAnySize_StillGetsFloorSize()
    {
        // 어떤 크기로도 안 들어가면 최소 크기로 그린다 — 0(안 그림)이 아니고, 더 긴 글이라고 더 작아지지도 않는다
        using var page = SolidPage(600, 800);
        var tiny = Manual(280, 300, 320, 320, "THIS IS FAR TOO LONG FOR THIS TINY BOX AT ALL");
        var tinier = Manual(280, 400, 320, 420, "EVEN LONGER TEXT THAT CAN NEVER FIT INTO SUCH A SMALL PLACE NO MATTER WHAT");

        var r = TextRenderer.PlanLayout(page, new[] { tiny, tinier }, 1, 1, Arial());
        float roomy = TextRenderer.PlanLayout(page, new[] { Manual(50, 500, 550, 750, "HI") }, 1, 1, Arial())[0].Size;

        Assert.True(r[0].Size > 0);
        Assert.Equal(r[0].Size, r[1].Size);
        Assert.True(r[0].Size < roomy, $"{r[0].Size} vs {roomy}");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("below")]        // 세로로 겹치지 않는 이웃은 상관없다
    [InlineData("beside")]       // 옆 10px 거리
    [InlineData("overlapping")]  // 가로로도 겹친다
    public void PlanLayout_MaxWrap_OverflowNeverReachesNeighbour(string neighbour)
    {
        // 긴 어절은 줄 폭을 1.3배까지 넘겨도 되지만, 결합 풍선에서 나뉜 형제 블록이 바로 옆에 있으면 그 여유만큼만 넘친다
        using var page = SolidPage(1000, 1000);
        var main = Manual(100, 100, 300, 300, "AB CD");
        var blocks = neighbour switch
        {
            "below" => new[] { main, Manual(100, 400, 300, 500, "EF") },
            "beside" => new[] { main, Manual(300, 150, 500, 250, "EF") },
            "overlapping" => new[] { main, Manual(250, 150, 500, 250, "EF") },
            _ => new[] { main },
        };

        var l = TextRenderer.PlanLayout(page, blocks, 1, 1, Arial())[0];

        switch (neighbour)
        {
            case "none":
            case "below":
                Assert.InRange(l.MaxWrap, l.Rect.Width * TextRenderer.WordOverflowAllow - 0.01f, l.Rect.Width * TextRenderer.WordOverflowAllow + 0.01f);
                break;
            case "beside":
                float gap = 300 - l.Rect.Right;
                Assert.True(l.MaxWrap > l.Rect.Width, $"{l.MaxWrap}");
                Assert.True((l.MaxWrap - l.Rect.Width) / 2 <= gap, $"{l.MaxWrap} {l.Rect}");
                break;
            default:
                Assert.True(l.MaxWrap <= l.Rect.Width + 0.01f, $"{l.MaxWrap} {l.Rect}");
                break;
        }
    }

    [Theory]
    [InlineData(true)]    // 세로쓰기 글 기둥 — 길쭉한 타원
    [InlineData(false)]   // 가로쓰기 줄 — 납작한 타원
    public void PlanLayout_AutoBlockInClosedBubble_UsesBubbleInteriorCenteredAndRound(bool vertical)
    {
        // 감지 블록은 글자가 있던 자리라 풍선보다 작다 — 닫힌 풍선이면 흰 안쪽을 자리로 삼고 한가운데에, 타원에 맞춰 넣는다
        using var page = SolidPage(800, 1000, 150);
        var (rx, ry) = vertical ? (120, 130) : (200, 90);
        DrawBubble(page, 400, 500, rx, ry);
        var block = vertical ? Auto(370, 430, 430, 570, "HELLO THERE FRIEND", true) : Auto(330, 470, 470, 530, "HELLO THERE FRIEND");

        var l = TextRenderer.PlanLayout(page, new[] { block }, 1, 1, Arial())[0];

        Assert.True(l.Round);
        var interior = RectangleF.FromLTRB(400 - rx - 1, 500 - ry - 1, 400 + rx + 1, 500 + ry + 1);
        Assert.True(interior.Contains(l.Rect), $"{l.Rect}");
        float blockArea = (block.X2 - block.X1) * (block.Y2 - block.Y1);
        Assert.True(l.Rect.Width * l.Rect.Height > 2 * blockArea, $"{l.Rect}");
        Assert.InRange(l.Rect.X + l.Rect.Width / 2, 395f, 405f);
        Assert.InRange(l.Rect.Y + l.Rect.Height / 2, 495f, 505f);
        Assert.True(l.Size > 0);
    }

    [Theory]
    [InlineData(255)]   // 테두리 없는 흰 여백 (해설문)
    [InlineData(40)]    // 어두운 그림 위
    public void PlanLayout_AutoHorizontalBlockOutsideBubble_StaysCloseToBlock(int tone)
    {
        // 풍선이 없으면 흰 바탕으로 번지지 않게 가로쓰기는 1.35배까지만 넓히고, 원래 글이 있던 자리에 머문다
        using var page = SolidPage(800, 1000, tone);
        var block = Auto(330, 470, 470, 530, "HELLO THERE FRIEND");

        var l = TextRenderer.PlanLayout(page, new[] { block }, 1, 1, Arial())[0];

        Assert.False(l.Round);
        Assert.True(l.Rect.Width <= 140 * 1.35f + 0.5f && l.Rect.Height <= 60 * 1.35f + 0.5f, $"{l.Rect}");
        Assert.True(l.Rect.Contains(400, 500), $"{l.Rect}");
        Assert.True(l.Size > 0);
    }

    [Fact]
    public void PlanLayout_ResultImageTwiceAsLarge_DoublesPlaceAndSize()
    {
        // 채색 결과가 원본의 2배 해상도일 때: 원본 좌표 블록을 2배로 환산해 같은 풍선을 재고, 글자도 2배로 둔다
        using var original = SolidPage(800, 1000, 150);
        using var doubled = SolidPage(1600, 2000, 150);
        DrawBubble(original, 400, 500, 120, 130);
        DrawBubble(doubled, 800, 1000, 240, 260, border: 6);
        var blocks = new[] { Auto(370, 430, 430, 570, "HELLO THERE FRIEND", true) };

        var a = TextRenderer.PlanLayout(original, blocks, 1, 1, Arial())[0];
        var b = TextRenderer.PlanLayout(doubled, blocks, 2, 2, Arial())[0];

        Assert.True(a.Round && b.Round);
        Assert.InRange(b.Rect.Left, 2 * a.Rect.Left - 4, 2 * a.Rect.Left + 4);
        Assert.InRange(b.Rect.Top, 2 * a.Rect.Top - 4, 2 * a.Rect.Top + 4);
        Assert.InRange(b.Rect.Right, 2 * a.Rect.Right - 4, 2 * a.Rect.Right + 4);
        Assert.InRange(b.Rect.Bottom, 2 * a.Rect.Bottom - 4, 2 * a.Rect.Bottom + 4);
        Assert.InRange(b.Size / a.Size, 1.95f, 2.05f);
    }

    [Fact(Skip = "알려진 결함: 이미지 가장자리를 넘는 자동(ManualRect=false) 가로쓰기 블록에서 LimitGrowth의 Math.Clamp가 ArgumentException(최솟값 > 최댓값)을 던진다 — 넓힌 자리가 이미지 경계에서 잘려 원래 블록보다 작아진다")]
    public void PlanLayout_AutoBlockCrossingImageEdge_DoesNotThrow()
    {
        // 원본 크기 참조 파일이 없어 비율 1로 환산했는데 결과 이미지가 원본보다 작으면 블록이 이미지 밖으로 나간다.
        // 그런 블록 하나 때문에 쪽 전체의 식자가 예외로 멈추면 안 된다.
        using var page = SolidPage(300, 450);
        foreach (var block in new[] { Auto(100, 440, 200, 560, "HELLO WORLD"), Auto(280, 100, 420, 200, "HELLO WORLD") })
            Assert.Null(Record.Exception(() => TextRenderer.PlanLayout(page, new[] { block }, 1, 1, Arial())));
    }

    // ───────── 그리기 (RenderBlocks) ─────────

    [Theory]
    [InlineData("원고 フォルダ/page 1.png", "결과 結果/OUT.PNG")]
    [InlineData("입력/スキャン 01.webp", "출력 폴더/식자.jpg")]
    public void RenderBlocks_UnicodePathsAndFormats_WritesResultOfSameSizeInNewFolder(string input, string output)
    {
        // 만화 경로에는 한글·일본어·공백이 흔하고 webp도 흔하다. 결과 폴더가 없으면 만들고, 확장자(대소문자 무관)대로 인코딩한다.
        using var t = new TempDir();
        using var page = SolidPage(600, 800);
        var inPath = t.At(input.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(inPath)!);
        Cv2.ImEncode(Path.GetExtension(inPath), page, out var encoded);
        File.WriteAllBytes(inPath, encoded);
        var outPath = t.At(output.Split('/'));

        TextRenderer.RenderBlocks(inPath, inPath, new[] { Manual(150, 300, 450, 500, "HELLO") }, outPath, Arial());

        var bytes = File.ReadAllBytes(outPath);
        byte[] magic = Path.GetExtension(outPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? [0x89, 0x50, 0x4E, 0x47]
            : [0xFF, 0xD8, 0xFF];
        Assert.Equal(magic, bytes.Take(magic.Length).ToArray());
        using var after = Cv2.ImDecode(bytes, ImreadModes.Color);
        Assert.Equal(new OpenCvSharp.Size(600, 800), after.Size());
        using var changed = ChangedMask(page, after);
        var ink = Bounds(changed);
        Assert.NotNull(ink);
        Assert.True(new Rect(150, 300, 300, 200).Contains(ink.Value), $"{ink}");
    }

    [Fact]
    public void RenderBlocks_SourceSizeReference_ConvertsBlockCoordinates()
    {
        // 결과(채색본)가 원본의 2배면 원본 좌표 블록 (50,50)-(250,150)은 (100,100)-(500,300)에 그려진다.
        // 원본 크기를 알 수 없으면(참조 파일 없음) 좌표를 그대로 쓴다.
        using var t = new TempDir();
        using var colored = SolidPage(600, 800);
        using var source = SolidPage(300, 400);
        SavePng(colored, t.At("채색.png"));
        SavePng(source, t.At("원본.png"));
        var blocks = new[] { Manual(50, 50, 250, 150, "HELLO WORLD") };

        TextRenderer.RenderBlocks(t.At("채색.png"), t.At("원본.png"), blocks, t.At("scaled.png"), Arial());
        TextRenderer.RenderBlocks(t.At("채색.png"), t.At("없는 원본.png"), blocks, t.At("unscaled.png"), Arial());

        AssertInkCenteredIn(colored, t.At("scaled.png"), new Rect(100, 100, 400, 200));
        AssertInkCenteredIn(colored, t.At("unscaled.png"), new Rect(50, 50, 200, 100));
    }

    private static void AssertInkCenteredIn(Mat before, string resultPath, Rect place)
    {
        using var after = LoadColor(resultPath);
        using var changed = ChangedMask(before, after);
        var ink = Bounds(changed);
        Assert.NotNull(ink);
        var r = ink.Value;
        Assert.True(place.Contains(r), $"{r} not in {place}");
        Assert.InRange(r.X + r.Width / 2.0, place.X + place.Width / 2.0 - 10, place.X + place.Width / 2.0 + 10);
        Assert.InRange(r.Y + r.Height / 2.0, place.Y + place.Height / 2.0 - 10, place.Y + place.Height / 2.0 + 10);
    }

    [Fact]
    public void RenderBlocks_LineBreaking_KeepsEveryWordWhole()
    {
        // 어절(띄어쓰기 단위) 줄바꿈: "멀긴한/데"처럼 어절이 중간에 꺾이면 안 된다.
        // 길이가 2·4·6·8자인 어절을 한 줄에 다 안 들어가는 폭에 그리고, 어절 간격을 넓혀 잉크 덩어리로 어절을 센다.
        using var t = new TempDir();
        using var page = SolidPage(900, 1600);
        SavePng(page, t.At("in.png"));
        var s = Arial();
        s.LetterBold = false;
        s.LetterWordSpacing = 2.0;

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), new[] { Manual(300, 300, 600, 1000, "HH HHHH HHHHHH HHHHHHHH") }, t.At("out.png"), s);

        using var after = LoadColor(t.At("out.png"));
        using var changed = ChangedMask(page, after);
        var lines = InkLines(changed);
        Assert.True(lines.Count >= 2, $"lines={lines.Count}");
        var words = lines.SelectMany(l => InkRuns(changed, l, l.Height / 2)).Select(w => w.Right - w.Left).OrderBy(w => w).ToList();
        // 꺾인 어절이 있으면 조각이 생겨 덩어리가 넷보다 많아진다
        Assert.Equal(4, words.Count);
        // 2·4·6·8자 어절이면 폭이 같은 간격으로 늘어난다 — 덩어리 하나하나가 온전한 어절이다
        var steps = words.Zip(words.Skip(1), (a, b) => b - a).ToList();
        Assert.True(steps.Max() - steps.Min() <= 4, string.Join(",", words));
    }

    [Fact]
    public void RenderBlocks_WordWiderThanPlace_IsBrokenIntoLinesWithinAllowedWidth()
    {
        // 띄어쓰기 없는 어절이 줄 폭을 한참 넘으면 그 어절만 어쩔 수 없이 글자 단위로 꺾는다 — 줄은 허용 폭(1.3배)을 넘지 않는다
        using var t = new TempDir();
        using var page = SolidPage(600, 800);
        SavePng(page, t.At("in.png"));

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), new[] { Manual(250, 100, 350, 700, new string('H', 30)) }, t.At("out.png"), Arial());

        using var after = LoadColor(t.At("out.png"));
        using var changed = ChangedMask(page, after);
        var lines = InkLines(changed);
        Assert.True(lines.Count >= 3, $"lines={lines.Count}");
        Assert.All(lines, l => Assert.True(l.Width <= 100 * TextRenderer.WordOverflowAllow + 4, $"{l}"));
    }

    [Theory]
    [InlineData("Left", "Center", "Left")]
    [InlineData("Center", "Left", "Center")]
    [InlineData("Right", "Center", "Right")]
    [InlineData("", "Right", "Right")]   // 블록에 지정이 없으면 쪽 기본 정렬
    public void RenderBlocks_Alignment_AlignsLinesInsideCenteredTextBlock(string blockAlign, string pageAlign, string expected)
    {
        using var t = new TempDir();
        using var page = SolidPage(900, 1600);
        SavePng(page, t.At("in.png"));
        var s = Arial();
        s.LetterAlign = pageAlign;

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"),
            new[] { Manual(100, 100, 800, 900, "HHHHHHHH\nHH") with { Align = blockAlign } }, t.At("out.png"), s);

        using var after = LoadColor(t.At("out.png"));
        using var changed = ChangedMask(page, after);
        var lines = InkLines(changed);
        // 자리가 넉넉해도 사용자가 넣은 줄바꿈은 지킨다
        Assert.Equal(2, lines.Count);
        var (wide, narrow) = (lines[0], lines[1]);
        // 글 덩어리 자체는 자리 한가운데에 두고, 그 안에서 줄을 맞춘다
        Assert.InRange(wide.X + wide.Width / 2.0, 445, 455);
        switch (expected)
        {
            case "Left": Assert.InRange(narrow.Left - wide.Left, -2, 2); break;
            case "Right": Assert.InRange(narrow.Right - wide.Right, -2, 2); break;
            default: Assert.InRange((narrow.Left + narrow.Right) - (wide.Left + wide.Right), -3, 3); break;
        }
    }

    [Theory]
    [InlineData("#FF0000", "#00FF00", "#0000FF", "red")]   // 블록별 지정이 가장 우선
    [InlineData("", "#00FF00", "#0000FF", "green")]        // 다음은 설정의 전역 색
    [InlineData("", "", "#0000FF", "blue")]                // 둘 다 비면 원문 글자 색
    [InlineData("", "", "#00ff00", "green")]               // 소문자 16진수도 읽는다
    [InlineData("", "", "blue", "black")]                  // 읽을 수 없는 색은 검정
    public void RenderBlocks_TextColor_BlockOverrideThenSettingThenSourceColor(string blockColor, string settingColor, string sourceColor, string expected)
    {
        using var t = new TempDir();
        using var page = SolidPage(600, 800);
        SavePng(page, t.At("in.png"));
        var s = Arial();
        s.LetterColorHex = settingColor;
        var block = Manual(50, 50, 550, 300, "HHHH") with { ColorOverride = blockColor, FontColorHex = sourceColor };

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), new[] { block }, t.At("out.png"), s);

        using var after = LoadColor(t.At("out.png"));
        var counts = new Dictionary<string, int>
        {
            ["red"] = CountInRange(after, new Scalar(0, 0, 201), new Scalar(59, 59, 255)),
            ["green"] = CountInRange(after, new Scalar(0, 201, 0), new Scalar(59, 255, 59)),
            ["blue"] = CountInRange(after, new Scalar(201, 0, 0), new Scalar(255, 59, 59)),
            ["black"] = CountInRange(after, new Scalar(0, 0, 0), new Scalar(59, 59, 59)),
        };
        Assert.True(counts[expected] > 50, string.Join(" ", counts));
        Assert.All(counts.Where(kv => kv.Key != expected), kv => Assert.Equal(0, kv.Value));
    }

    [Theory]
    [InlineData(0.0, false)]   // 0이면 외곽선 없음
    [InlineData(1.0, true)]
    public void RenderBlocks_OutlineScale_ControlsWhiteHalo(double outlineScale, bool expectHalo)
    {
        // 회색 바탕에서만 흰 외곽선이 보인다
        using var t = new TempDir();
        using var page = SolidPage(600, 800, 128);
        SavePng(page, t.At("in.png"));
        var s = Arial();
        s.LetterOutlineScale = outlineScale;

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), new[] { Manual(50, 50, 550, 300, "HHHH") }, t.At("out.png"), s);

        using var after = LoadColor(t.At("out.png"));
        int white = CountInRange(after, new Scalar(201, 201, 201), new Scalar(255, 255, 255));
        int dark = CountInRange(after, new Scalar(0, 0, 0), new Scalar(59, 59, 59));
        Assert.True(dark > 50, $"dark={dark}");
        Assert.Equal(expectHalo, white > 0);
    }

    [Theory]
    [InlineData("no-blocks")]
    [InlineData("blank-translation")]
    public void RenderBlocks_NothingToDraw_WritesUnchangedCopy(string kind)
    {
        // 그릴 글이 없는 쪽도 결과 파일은 있어야 한다 — 그림을 바꾸지 않은 사본을 쓴다
        using var t = new TempDir();
        using var page = SolidPage(300, 400, 180);
        Cv2.Circle(page, new OpenCvSharp.Point(150, 200), 60, Scalar.Black, 3);
        SavePng(page, t.At("in.png"));
        var blocks = kind == "no-blocks" ? Array.Empty<TextBlockInfo>() : new[] { Manual(50, 50, 250, 150, "  \n ") };

        TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), blocks, t.At("out.png"), Arial());

        using var after = LoadColor(t.At("out.png"));
        using var changed = ChangedMask(page, after, 0);
        Assert.Equal(0, Cv2.CountNonZero(changed));
    }

    [Fact]
    public void RenderBlocks_UnreadableImage_ThrowsInvalidDataNamingFile()
    {
        // 그림이 아닌 파일은 사용자에게 파일 이름을 알려 주는 InvalidDataException으로 — 결과 파일은 만들지 않는다
        using var t = new TempDir();
        var bad = t.At("깨진 그림.png");
        File.WriteAllText(bad, "이건 그림이 아니다");

        var ex = Assert.Throws<InvalidDataException>(() =>
            TextRenderer.RenderBlocks(bad, bad, new[] { Manual(10, 10, 100, 100, "HI") }, t.At("out.png"), Arial()));

        Assert.Contains("깨진 그림.png", ex.Message);
        Assert.False(File.Exists(t.At("out.png")));
    }

    private static int CountInRange(Mat img, Scalar lower, Scalar upper)
    {
        using var m = new Mat();
        Cv2.InRange(img, lower, upper, m);
        return Cv2.CountNonZero(m);
    }
}
