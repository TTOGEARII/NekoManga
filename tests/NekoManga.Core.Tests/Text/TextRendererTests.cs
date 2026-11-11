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

    [Theory]
    [InlineData("widen", 903, 157, 259, 119)]   // 900px 원본을 903px로 채색 — 흰 구간 119px의 86%가 블록 폭(102.34px)보다 부동소수점 한 칸만 크다
    [InlineData("widen", 946, 150, 249, 121)]
    [InlineData("widen", 978, 361, 490, 163)]
    [InlineData("heighten", 903, 157, 259, 119)]
    [InlineData("heighten", 946, 150, 249, 121)]
    [InlineData("heighten", 978, 361, 490, 163)]
    public void WidenOrHeightenToBubble_RunBarelyWiderThanBlockAtFractionalScale_KeepsBlockWithoutThrowing(string direction, int size, int x1, int x2, int run)
    {
        // 채색으로 결과 크기가 바뀌면 블록 좌표가 소수가 된다. 잰 흰 구간의 86%가 블록 폭보다 아주 조금만 크면 반올림으로 r.Right - newW가 r.X보다
        // 커져 Math.Clamp가 최솟값 > 최댓값으로 던졌다('157.52335' cannot be greater than 157.52333) — 이미지 안 블록 하나로 쪽 전체 식자가 멈췄다.
        // 넓힐 것이 거의 없으니 원래 자리에서 시작하고, 원래 블록은 안에 남는다.
        double s = size / 900.0;
        using var page = SolidPage(size, size, 40);
        var block = new RectangleF((float)(x1 * s), (float)(300 * s), (float)((x2 - x1) * s), (float)(40 * s));   // PlanLayout이 환산하는 식 그대로
        DrawWhiteBand(page, block.X + block.Width / 2, run, rows: false);

        var r = Grow(direction, page, block);

        Assert.Equal(block.Y, r.Y);
        Assert.Equal(block.Height, r.Height);
        Assert.True(r.Left <= block.Left && r.Right >= block.Right, $"{r} vs {block}");
        Assert.True(r.Width < block.Width + 0.01f, $"{r} vs {block}");
    }

    /// <summary>어두운 쪽에 흰 띠를 끝에서 끝까지 긋는다 — center(픽셀로 내림)에서 재면 흰 구간이 꼭 run px(띠 폭 run+1px)이 되게.
    /// rows면 가로 띠(행), 아니면 세로 띠(열).</summary>
    private static void DrawWhiteBand(Mat page, float center, int run, bool rows)
    {
        int start = (int)center - run / 2;
        Cv2.Rectangle(page, rows ? new Rect(0, start, page.Cols, run + 1) : new Rect(start, 0, run + 1, page.Rows), Scalar.White, -1);
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

    [Fact]
    public void PlanLayout_AutoBlockCrossingImageEdge_DoesNotThrow()
    {
        // 원본 크기 참조 파일이 없어 비율 1로 환산했는데 결과 이미지가 원본보다 작으면 블록이 이미지 밖으로 나간다.
        // 그런 블록 하나 때문에 쪽 전체의 식자가 예외로 멈추면 안 된다.
        using var page = SolidPage(300, 450);
        foreach (var block in new[] { Auto(100, 440, 200, 560, "HELLO WORLD"), Auto(280, 100, 420, 200, "HELLO WORLD") })
            Assert.Null(Record.Exception(() => TextRenderer.PlanLayout(page, new[] { block }, 1, 1, Arial())));
    }

    [Theory]
    [InlineData(100, 440, 200, 560, false)]   // 아래로 걸침 — 안에 남는 것은 높이 10px
    [InlineData(280, 100, 420, 200, false)]   // 오른쪽으로 걸침 — 폭 20px
    [InlineData(-60, 200, 40, 260, false)]    // 왼쪽
    [InlineData(120, -50, 220, 30, false)]    // 위
    // 세로쓰기도 보이는 부분에서 잰다. 예전에는 블록 전체로 재서(LimitGrowth를 거치지 않아 던지지는 않았다) 자리가 이미지 밖으로 나갔다:
    [InlineData(280, 250, 600, 420, true)]    // 대부분 오른쪽 밖 — 넓히지 못한 블록 폭 그대로 오른쪽으로 280px 넘게 나갔다
    [InlineData(100, 400, 160, 900, true)]    // 대부분 아래 밖 — 자리가 이미지 아래로 400px 넘게 나갔다
    [InlineData(-200, 100, 20, 300, true)]    // 대부분 왼쪽 밖 — 폭이 음수인 자리
    public void PlanLayout_AutoBlockCrossingImageEdge_IsPlacedOnVisiblePart(int x1, int y1, int x2, int y2, bool vertical)
    {
        // 이미지 밖으로 걸친 감지 블록은 이미지 안쪽 부분을 원래 블록으로 삼는다 — 자리는 이미지 안에, 보이는 부분 위에 잡힌다
        using var page = SolidPage(300, 450);
        var visible = RectangleF.Intersect(RectangleF.FromLTRB(x1, y1, x2, y2), new RectangleF(0, 0, 300, 450));

        var l = Assert.Single(TextRenderer.PlanLayout(page, new[] { Auto(x1, y1, x2, y2, "HELLO WORLD", vertical) }, 1, 1, Arial()));

        Assert.True(l.Size > 0);
        Assert.True(l.Rect.Width > 0 && l.Rect.Height > 0, $"{l.Rect}");
        Assert.True(l.Rect.Left >= 0 && l.Rect.Top >= 0 && l.Rect.Right <= 300 && l.Rect.Bottom <= 450, $"{l.Rect}");
        Assert.True(l.Rect.Contains(visible.X + visible.Width / 2, visible.Y + visible.Height / 2), $"{l.Rect} vs {visible}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanLayout_AutoBlockCompletelyOutsideImage_IsNotDrawnAndLeavesOtherBlocksAlone(bool vertical)
    {
        // 완전히 밖에 있는 블록은 그리지 않는다 — 빈 자리나 음수 크기 자리를 내지 않고, 쪽 공통 크기(중앙값)도 끌어내리지 않는다.
        // (예전에는 가로쓰기면 쪽 전체가 예외로 멈췄고, 세로쓰기면 이미지 가장자리의 좁은 띠에 작은 크기로 그려 중앙값에 끼었다 —
        // 이 쪽에서는 안쪽 블록의 글자가 28.5px에서 9.5px로 줄었다)
        using var page = SolidPage(1000, 1500);
        var inside = Auto(300, 200, 700, 320, "HELLO THERE");
        var right = Auto(1100, 900, 1200, 1100, "HELLO THERE", vertical);
        var below = Auto(200, 1600, 260, 1800, "HELLO THERE", vertical);

        var alone = TextRenderer.PlanLayout(page, new[] { inside }, 1, 1, Arial());
        var with = TextRenderer.PlanLayout(page, new[] { inside, right, below }, 1, 1, Arial());

        Assert.Equal(3, with.Count);
        Assert.All(with.Skip(1), l =>
        {
            Assert.Equal(0, l.Size);
            Assert.True(l.Rect.Width > 0 && l.Rect.Height > 0, $"{l.Rect}");
        });
        Assert.Equal(alone[0], with[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanLayout_AutoBlocksBarelyOnPage_DoNotShrinkBlocksInsideImage(bool vertical)
    {
        // 아래 가장자리에 5~6px만 보이는 감지 블록은 그 좁은 띠에 맞춘 작은 크기가 된다. 그 크기가 쪽 공통 크기(중앙값)에 끼어
        // 이미지 안 블록의 글자까지 28.5px에서 10.3px(세로쓰기)·9px(가로쓰기)로 줄었다 — 공통 크기는 대부분 밖인 블록을 빼고 구한다.
        using var page = SolidPage(1000, 1500);
        var inside = Auto(300, 200, 700, 320, "HELLO THERE");
        var a = Auto(100, 1495, 300, 1700, "HELLO THERE", vertical);
        var b = Auto(500, 1494, 700, 1650, "HELLO THERE", vertical);

        var alone = TextRenderer.PlanLayout(page, new[] { inside }, 1, 1, Arial());
        var with = TextRenderer.PlanLayout(page, new[] { inside, a, b }, 1, 1, Arial());

        Assert.Equal(alone[0], with[0]);
        // 걸친 블록도 그리되, 좁은 띠에 맞춘 제 크기를 넘지 않는다
        Assert.All(with.Skip(1), l => Assert.True(l.Size > 0 && l.Size < with[0].Size, $"{l.Size} vs {with[0].Size}"));
    }

    [Fact]
    public void PlanLayout_MostlyOutsideRoomyBlock_GetsNoLargerThanCommonSizeOfBlocksInsideImage()
    {
        // 걸친 블록의 크기도 다른 블록처럼 자기 맞춤 크기와 쪽 공통 크기 가운데 작은 쪽이다. 대부분 밖인 블록은 공통 크기를 구할 때 빠지므로,
        // 보이는 부분이 넉넉한 그런 블록 하나가 공통 크기를 제 크기로 끌어올려 혼자 크게 그려지지 않는다.
        using var page = SolidPage(1000, 1500);
        var crowded = Auto(400, 300, 600, 340, "THIS IS A MUCH LONGER SENTENCE THAT MUST SHRINK");
        var roomy = Auto(300, 1300, 700, 1900, "HI");   // 600px 가운데 200px만 보인다

        var alone = TextRenderer.PlanLayout(page, new[] { crowded }, 1, 1, Arial());
        var with = TextRenderer.PlanLayout(page, new[] { crowded, roomy }, 1, 1, Arial());

        Assert.Equal(alone[0], with[0]);
        Assert.Equal(with[0].Size, with[1].Size);
    }

    [Fact]
    public void PlanLayout_OnlyMostlyOutsideBlocks_CommonSizeComesFromThem()
    {
        // 대부분 밖인 블록뿐인 쪽은 그 블록들로 공통 크기를 구한다 — 보이는 부분만 한 이미지 안 블록과 자리·크기가 같다
        // (공통 크기를 구할 블록이 없다고 기본값으로 떨어지면 넉넉한 풍선의 글자가 작아진다).
        using var page = SolidPage(1000, 1500);

        var clipped = Assert.Single(TextRenderer.PlanLayout(page, new[] { Auto(300, 1300, 700, 1900, "HELLO THERE") }, 1, 1, Arial()));
        var inImage = Assert.Single(TextRenderer.PlanLayout(page, new[] { Auto(300, 1300, 700, 1500, "HELLO THERE") }, 1, 1, Arial()));

        Assert.Equal(inImage, clipped);
    }

    [Fact]
    public void PlanLayout_AutoBlockSlightlyOverEdge_CountsTowardCommonSizeLikeBlockInsideImage()
    {
        // 조금만 걸친 블록은 공통 크기에 그대로 넣는다 — 결과 창 편집 미리보기는 두 축에 한 비율을 써서, 채색으로 가로세로 비가 조금 바뀐 쪽의
        // 가장자리 블록이 몇 px씩 걸친다. 그런 블록까지 빼면 미리보기의 글자 크기가 최종 식자와 달라진다.
        using var page = SolidPage(1000, 1500);
        var crowded = Auto(400, 300, 600, 340, "THIS IS A MUCH LONGER SENTENCE THAT MUST SHRINK");

        var over = TextRenderer.PlanLayout(page, new[] { crowded, Auto(300, 1300, 700, 1503, "HI") }, 1, 1, Arial());     // 아래로 3px 걸침
        var inside = TextRenderer.PlanLayout(page, new[] { crowded, Auto(300, 1300, 700, 1500, "HI") }, 1, 1, Arial());

        Assert.Equal(inside, over);
        Assert.True(over[1].Size > over[0].Size, $"{over[1].Size} vs {over[0].Size}");
    }

    [Theory]
    [InlineData(201, 281, 1.2)]       // 1000px 원본을 1200px로 채색
    [InlineData(200, 360, 1.024)]     // 1000px → 1024px
    public void PlanLayout_AutoBlockNotWidenedAtFractionalScale_DoesNotThrow(int x1, int x2, double scale)
    {
        // 채색으로 결과 크기가 바뀌면 블록 좌표가 소수가 된다. 어두운 그림 위라 넓히지 못한 가로쓰기 블록은 LimitGrowth에서
        // (X + W) - W가 반올림으로 X보다 아주 조금 커져 Math.Clamp가 최솟값 > 최댓값으로 던졌다 — 이미지 안의 블록인데도 쪽 전체가 멈췄다.
        // 넓히지 못했으니 자리는 블록 그대로(테두리 여유만큼 안쪽)다.
        using var page = SolidPage((int)Math.Round(1000 * scale), (int)Math.Round(1000 * scale), 40);
        var block = Auto(x1, 300, x2, 340, "HELLO THERE");

        var l = Assert.Single(TextRenderer.PlanLayout(page, new[] { block }, scale, scale, Arial()));

        var scaled = new RectangleF((float)(x1 * scale), (float)(300 * scale), (float)((x2 - x1) * scale), (float)(40 * scale));
        Assert.True(l.Size > 0);
        Assert.False(l.Round);
        Assert.True(RectangleF.Inflate(scaled, 0.01f, 0.01f).Contains(l.Rect), $"{l.Rect} vs {scaled}");
        Assert.InRange(l.Rect.Width, scaled.Width * 0.89f, scaled.Width * 0.91f);
        Assert.InRange(l.Rect.X + l.Rect.Width / 2, scaled.X + scaled.Width / 2 - 0.01f, scaled.X + scaled.Width / 2 + 0.01f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // 같은 장면을 전치 — 가로 흰 띠에 선 세로 블록에서는 HeightenToBubble이 던졌다
    public void PlanLayout_AutoBlockBarelyWidenedAtFractionalScale_DoesNotThrow(bool transposed)
    {
        // 900px 원본을 903px로 채색한 쪽. 어두운 그림을 가로지르는 흰 띠(폭 120px) 안에 블록이 있어, 흰 구간 119px의 86%가 블록 폭보다
        // 부동소수점 한 칸만 크다 — WidenToBubble(전치하면 HeightenToBubble)의 Math.Clamp가 반올림으로 최솟값 > 최댓값이 되어 던졌고,
        // 이미지 안 블록 하나로 쪽 전체 식자가 멈췄다.
        const double s = 903 / 900.0;
        using var page = SolidPage(903, 903, 40);
        var block = transposed ? Auto(300, 157, 340, 259, "HELLO THERE", true) : Auto(157, 300, 259, 340, "HELLO THERE");
        var scaled = new RectangleF((float)(block.X1 * s), (float)(block.Y1 * s), (float)((block.X2 - block.X1) * s), (float)((block.Y2 - block.Y1) * s));
        float center = transposed ? scaled.Y + scaled.Height / 2 : scaled.X + scaled.Width / 2;
        DrawWhiteBand(page, center, 119, rows: transposed);
        int bandStart = (int)center - 119 / 2;

        var l = Assert.Single(TextRenderer.PlanLayout(page, new[] { block }, s, s, Arial()));

        Assert.True(l.Size > 0);
        Assert.False(l.Round);
        Assert.True(l.Rect.Contains(scaled.X + scaled.Width / 2, scaled.Y + scaled.Height / 2), $"{l.Rect} vs {scaled}");
        // 글은 흰 띠 위에 놓인다 — 띠를 가로지르는 방향으로 띠 밖에 나가지 않는다
        var (near, far) = transposed ? (l.Rect.Top, l.Rect.Bottom) : (l.Rect.Left, l.Rect.Right);
        Assert.True(near >= bandStart && far <= bandStart + 120, $"{l.Rect} vs band {bandStart}..{bandStart + 120}");
    }

    [Fact]
    public void PlanLayout_RandomAutoBlocks_NeverThrowAndDrawOnlyOnPage()
    {
        // 무작위 쪽: 채색으로 크기가 바뀐 결과(소수 비율)이거나, 원본 크기를 몰라 비율 1로 환산했는데 원고가 더 작은 경우.
        // 감지 블록은 안쪽·가장자리에 걸친 것·완전히 밖인 것이 섞인다. 어느 쪽도 예외로 멈추면 안 되고(예전에는 LimitGrowth가 던졌다),
        // 그리는 블록의 자리는 이미지 안이며, 완전히 밖인 블록은 그리지 않는다. 자리 계산은 글꼴과 상관없어 어느 PC에서나 같은 결과다.
        var rng = new Random(20261110);
        for (int p = 0; p < 60; p++)
        {
            int srcW = rng.Next(300, 1200), srcH = rng.Next(400, 1600);
            bool unknownSource = rng.Next(3) == 0;
            int imgW = unknownSource ? srcW * rng.Next(50, 100) / 100 : (int)Math.Round(srcW * (0.6 + rng.NextDouble()));
            int imgH = unknownSource ? srcH * rng.Next(50, 100) / 100 : (int)Math.Round(srcH * (0.6 + rng.NextDouble()));
            double sx = unknownSource ? 1 : (double)imgW / srcW, sy = unknownSource ? 1 : (double)imgH / srcH;
            using var page = SolidPage(imgW, imgH, new[] { 255, 232, 150, 40 }[rng.Next(4)]);
            for (int k = rng.Next(0, 4); k > 0; k--)
                DrawBubble(page, rng.Next(imgW), rng.Next(imgH), rng.Next(30, 200), rng.Next(30, 200));
            var blocks = new List<TextBlockInfo>();
            for (int k = rng.Next(1, 7); k > 0; k--)
            {
                int bw = rng.Next(8, 300), bh = rng.Next(8, 300);
                int x1 = rng.Next(-bw, srcW + 100), y1 = rng.Next(-bh, srcH + 100);
                blocks.Add(Auto(x1, y1, x1 + bw, y1 + bh, "HELLO THERE FRIEND", rng.Next(2) == 0));
            }

            var layouts = TextRenderer.PlanLayout(page, blocks, sx, sy, Arial());

            Assert.Equal(blocks.Count, layouts.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var scaled = RectangleF.FromLTRB((float)(b.X1 * sx), (float)(b.Y1 * sy), (float)(b.X2 * sx), (float)(b.Y2 * sy));
                var r = layouts[i].Rect;
                if (!scaled.IntersectsWith(new RectangleF(0, 0, imgW, imgH)))
                    Assert.Equal(0, layouts[i].Size);
                else if (layouts[i].Size > 0)
                    Assert.True(r.Left >= -0.5f && r.Top >= -0.5f && r.Right <= imgW + 0.5f && r.Bottom <= imgH + 0.5f, $"쪽 {p} 블록 {i}: {r} / {imgW}x{imgH}");
            }
        }
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

    [Theory]
    [InlineData("missing")]      // 원고 폴더를 옮겼거나 원본을 지웠다
    [InlineData("unreadable")]   // 그 자리에 그림이 아닌 파일
    [InlineData("empty")]        // 0바이트 (복사하다 끊김)
    [InlineData("resized")]      // 같은 자리의 원본이 다른 해상도로 바뀌었다 — 블록 좌표는 번역할 때의 원본 기준이라 박제한 크기를 믿는다
    public void RenderBlocks_SourceUnavailable_ConvertsWithStoredSourceSize(string reference)
    {
        // 다시 식자(결과 창 [다시 식자], --reletter)는 블록 좌표를 원본 크기로 환산한다. 원본을 읽지 못하면 식자 사이드카에 박제한 원본 크기로
        // 환산해 원본이 있을 때와 똑같이 그려야 한다 — 예전에는 비율 1로 환산해서, 채색으로 원고가 원본의 절반이 된 이 쪽에서 첫 블록은
        // 엉뚱한 자리에 그리고 둘째 블록은 이미지 밖으로 나가 아무 알림 없이 빠뜨렸다.
        using var t = new TempDir();
        using var colored = SolidPage(500, 750);
        using var source = SolidPage(1000, 1500);
        SavePng(colored, t.At("채색.png"));
        SavePng(source, t.At("원본.png"));
        var gone = t.At("옮긴 원본.png");
        if (reference == "unreadable") File.WriteAllText(gone, "이건 그림이 아니다");
        if (reference == "empty") File.WriteAllBytes(gone, []);
        if (reference == "resized") { using var bigger = SolidPage(2000, 3000); SavePng(bigger, gone); }
        var blocks = new[] { Auto(100, 100, 400, 200, "HELLO WORLD"), Auto(600, 1000, 900, 1100, "HELLO THERE") };

        TextRenderer.RenderBlocks(t.At("채색.png"), t.At("원본.png"), blocks, t.At("with-source.png"), Arial());
        TextRenderer.RenderBlocks(t.At("채색.png"), gone, 1000, 1500, blocks, t.At("stored-size.png"), Arial());

        using var expected = LoadColor(t.At("with-source.png"));
        using var actual = LoadColor(t.At("stored-size.png"));
        using var differs = ChangedMask(expected, actual, 0);
        Assert.Equal(0, Cv2.CountNonZero(differs));
        // 두 블록 다 원본 좌표의 절반 자리에 그려졌다
        using var ink = ChangedMask(colored, actual);
        foreach (var place in new[] { new Rect(25, 25, 200, 100), new Rect(275, 475, 200, 100) })
        {
            using var part = new Mat(ink, place);
            Assert.True(Cv2.CountNonZero(part) > 50, $"{place}");
        }
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

/// <summary>현재 폴더를 바꾸는 식자 테스트 — 프로세스 전체가 같이 쓰는 상태라 다른 테스트와 동시에 돌리지 않는다.</summary>
[Collection(EnvCollection.Name)]
public class TextRendererCurrentFolderTests
{
    [Fact]
    public void RenderBlocks_BareOutputFileName_WritesIntoCurrentFolder()
    {
        // --reletter <결과.png> <출력.png>에 폴더 없이 파일 이름만 주면 현재 폴더에 써야 한다.
        // 예전에는 Path.GetDirectoryName이 ""라 Directory.CreateDirectory("")가 그림을 다 그린 뒤 저장 직전에 던졌다.
        using var t = new TempDir();
        using var page = SolidPage(600, 800);
        SavePng(page, t.At("in.png"));
        var block = new TextBlockInfo(150, 300, 450, 500, "", "HELLO", 20, false, "#000000", ManualRect: true);
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = t.Root;
        try
        {
            TextRenderer.RenderBlocks(t.At("in.png"), t.At("in.png"), new[] { block }, "out.png", new AppSettings { LetterFontFamily = "Arial" });
        }
        finally { Environment.CurrentDirectory = cwd; }

        Assert.True(File.Exists(t.At("out.png")));
        using var after = LoadColor(t.At("out.png"));
        Assert.Equal(new OpenCvSharp.Size(600, 800), after.Size());
        using var changed = ChangedMask(page, after);
        Assert.NotNull(Bounds(changed));   // 글자를 그린 결과가 저장됐다
    }
}
