using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>무늬 메우기의 속도(2026-11-27 사용자 "속도 최적화"): 구멍 가운데 **가까운 둘레가 평평한 자리**는 그 색으로 칠하고(<see cref="TextureFill.FlatPart"/>),
/// 선·무늬가 닿은 둘레 가까이와 구멍을 건너가는 선의 길만 둘레의 무늬로 메운다 — 메울 자리는 가까운 것끼리 묶어 묶음마다 제 창으로(<see cref="TextureFill.Groups"/>).
/// 실측(실제 다섯 쪽): 빗금이 조금 든 흰 풍선·먹칠한 칸의 굵은 글은 구멍이 8~13만 px라 창 하나에 9~20초가 걸렸고(shift-map의 시간은 구멍 넓이보다 빨리 는다),
/// 평평한 자리까지 무늬로 메우느라 빗금·옆 칸의 그림이 글자 자리로 딸려 들어왔다.</summary>
[Collection(EnvCollection.Name)]   // 진단 덤프 폴더(NEKO_TEXFILL_DBG)를 바꾸는 시험이 있다
public class TextureFillFlatTests
{
    private const int W = 320, H = 300;

    private static Mat Page(Scalar paper) => new(H, W, MatType.CV_8UC3, paper);

    /// <summary>창 하나의 재료 마스크(255 = 재료): 구멍과 <paramref name="otherText"/>(다른 글자)를 뺀 곳.</summary>
    private static Mat Valid(Mat own, Rect? otherText = null)
    {
        var valid = new Mat();
        Cv2.BitwiseNot(own, valid);
        if (otherText is { } r) valid.Rectangle(r, Scalar.Black, -1);
        return valid;
    }

    private static (int, int, int) Bgr(Scalar c) => ((int)c.Val0, (int)c.Val1, (int)c.Val2);

    // ── 칠할 자리 ──

    [Theory]
    [InlineData(255)]   // 흰 풍선
    [InlineData(0)]     // 먹칠한 칸
    [InlineData(200)]   // 옅은 회색 풍선
    public void FlatPart_HoleOnOnePlainColour_IsAllPaintable_InThatColour(int level)
    {
        using var patch = Page(Scalar.All(level));
        var hole = new Rect(110, 90, 90, 110);
        patch.Rectangle(hole, Scalar.All(level > 128 ? 0 : 255), -1);   // 글자 대신 반대색 덩어리
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using var flat = TextureFill.FlatPart(patch, valid, own, out var color);

        Assert.NotNull(flat);
        Assert.Equal(hole.Width * hole.Height, Cv2.CountNonZero(flat));
        Assert.Equal((level, level, level), Bgr(color));
    }

    [Fact]
    public void FlatPart_HatchingTouchingOneSide_LeavesThatSideToTextureFill_AndPaintsTheRest()
    {
        // 흰 풍선 오른쪽에 빗금이 들어와 글자에 닿아 있다(실제 26쪽의 큰 풍선 둘): 빗금이 닿은 쪽 가까이만 무늬로 메우고, 흰 바탕뿐인 쪽은 흰색으로 칠한다
        using var patch = Page(Scalar.White);
        var hole = new Rect(60, 80, 180, 120);
        for (int y = 90; y < 190; y += 8) patch.Line(hole.Right - 6, y, hole.Right + 30, y + 10, Scalar.Black, 1);   // 구멍 오른쪽 끝에 걸친 짧은 빗금들
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using var flat = TextureFill.FlatPart(patch, valid, own, out var color);

        Assert.NotNull(flat);
        Assert.Equal(255, flat.At<byte>(140, hole.X + 10));     // 왼쪽 끝 — 가까운 둘레가 전부 흰 종이다
        Assert.Equal(255, flat.At<byte>(140, hole.X + 90));     // 한가운데도(빗금에서 90px)
        Assert.Equal(0, flat.At<byte>(140, hole.Right - 4));    // 빗금이 닿은 가장자리
        Assert.Equal(0, flat.At<byte>(140, hole.Right - 12));   // 닿은 데서 FlatReachPx 안쪽까지는 무늬로 — 빗금이 글자 밑으로 이어질 자리
        Assert.Equal((255, 255, 255), Bgr(color));
        // 구멍 밖은 칠하지 않는다
        using var outside = new Mat();
        Cv2.BitwiseNot(own, outside);
        Cv2.BitwiseAnd(outside, flat, outside);
        Assert.Equal(0, Cv2.CountNonZero(outside));
    }

    [Fact]
    public void FlatPart_LinesCrossingTheHole_PaintNothing_EvenFarFromWhereTheyEnter()
    {
        // 속도선 위의 큰 글(실제 05쪽): 선 사이는 희지만 선들이 구멍을 건너간다 — 닿은 데서 멀다고 구멍 한가운데를 칠하면 선이 끊긴 흰 구멍이 남는다.
        // 벗어난 둘레 두 곳을 잇는 곧은 길(= 건너가는 선이 지날 자리)은 칠하지 않는다. 구멍의 왼쪽·오른쪽 둘레는 선과 선 사이라 깨끗하다
        using var patch = Page(Scalar.White);
        for (int x = 4; x < W; x += 14) patch.Line(x, 0, x, H - 1, Scalar.Black, 2);
        var hole = new Rect(90, 70, 140, 160);
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using var flat = TextureFill.FlatPart(patch, valid, own, out _);

        Assert.Null(flat);
    }

    [Fact]
    public void FlatPart_OneLineCrossingTheHole_KeepsItsPathForTextureFill_AndPaintsTheRest()
    {
        // 흰 풍선을 지나가는 머리카락 한 올: 선이 들어온 곳과 나간 곳을 잇는 길은 무늬로 메우고(선을 잇는다), 그 길에서 먼 자리는 칠한다
        using var patch = Page(Scalar.White);
        patch.Line(40, 60, 280, 240, Scalar.Black, 2);   // 기울기 0.75 — (160,150)을 지난다
        var hole = new Rect(110, 90, 100, 120);
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using var flat = TextureFill.FlatPart(patch, valid, own, out _);

        Assert.NotNull(flat);
        Assert.Equal(0, flat.At<byte>(150, 160));      // 선이 지날 길 — 구멍 한가운데, 닿은 데서 60px 넘게 떨어져 있다
        Assert.Equal(0, flat.At<byte>(135, 140));
        Assert.Equal(255, flat.At<byte>(105, 195));    // 길에서 먼 모서리(선은 x=195에서 y=176)
        Assert.Equal(255, flat.At<byte>(195, 125));    // 반대쪽 모서리(선은 x=125에서 y=124)
    }

    [Fact]
    public void FlatPart_OtherTextBesideTheHole_IsNotTheSurround()
    {
        // 바로 옆 줄의 글자(재료가 아닌 자리)는 둘레가 아니다 — 검은 글자를 '선이 닿았다'로 세면 흰 풍선의 줄마다 무늬로 메우게 된다
        using var patch = Page(Scalar.White);
        var hole = new Rect(120, 80, 60, 130);
        var other = new Rect(188, 80, 40, 130);   // 구멍에서 8px 떨어진 옆 줄
        patch.Rectangle(other, Scalar.Black, -1);
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own, other);

        using var flat = TextureFill.FlatPart(patch, valid, own, out _);

        Assert.NotNull(flat);
        Assert.Equal(hole.Width * hole.Height, Cv2.CountNonZero(flat));
    }

    [Fact]
    public void FlatPart_ScanNoiseIsStillPlain_AFaintLineIsNot()
    {
        // 스캔 잡음(±6)은 평평한 종이다. 옅은 선(종이보다 25 어둡다)은 선이다
        using var noisy = Page(Scalar.All(244));
        var rng = new Random(7);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = (byte)(244 + rng.Next(-6, 7));
                noisy.Set(y, x, new Vec3b(v, v, v));
            }
        var hole = new Rect(110, 90, 90, 110);
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using (var flat = TextureFill.FlatPart(noisy, valid, own, out _))
        {
            Assert.NotNull(flat);
            Assert.Equal(hole.Width * hole.Height, Cv2.CountNonZero(flat));
        }

        using var lined = Page(Scalar.All(244));
        lined.Line(0, 150, W - 1, 150, Scalar.All(219), 2);
        using var withLine = TextureFill.FlatPart(lined, valid, own, out _);
        Assert.NotNull(withLine);
        Assert.Equal(0, withLine.At<byte>(150, 155));     // 옅은 선이 건너가는 길
        Assert.Equal(255, withLine.At<byte>(100, 155));   // 선에서 50px
    }

    [Fact]
    public void FlatPart_BlurLeftAtTheTextEdge_IsNotALineTouchingTheHole()
    {
        // 먹칠 위 흰 테 글자(실제 04쪽): 구멍 바로 밖 1~3px에 테의 번진 끝자락(회색)이 사방으로 남는다. 그것을 '선이 닿았다'로 세면 구멍 전체가 무늬로 간다 —
        // 둘레는 구멍에서 FlatRimInner(5px) 떨어진 데서부터 잰다
        using var patch = Page(Scalar.Black);
        var hole = new Rect(110, 90, 90, 110);
        patch.Rectangle(new Rect(hole.X - 3, hole.Y - 3, hole.Width + 6, hole.Height + 6), Scalar.All(90), -1);   // 번진 끝자락
        patch.Rectangle(hole, Scalar.White, -1);
        using var own = RectMask(W, H, hole);
        using var valid = Valid(own);

        using var flat = TextureFill.FlatPart(patch, valid, own, out var color);

        Assert.NotNull(flat);
        Assert.Equal(hole.Width * hole.Height, Cv2.CountNonZero(flat));
        Assert.Equal((0, 0, 0), Bgr(color));
    }

    [Fact]
    public void FlatPart_NoSurroundToJudgeBy_ReturnsNull()
    {
        // 둘레가 전부 다른 글자(재료가 아니다)면 무슨 색으로 칠할지 알 수 없다
        using var patch = Page(Scalar.White);
        var hole = new Rect(110, 90, 90, 110);
        using var own = RectMask(W, H, hole);
        using var valid = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);

        Assert.Null(TextureFill.FlatPart(patch, valid, own, out _));
    }

    // ── 메울 자리를 묶음으로 ──

    [Fact]
    public void Groups_TwoPiecesFarApart_GetTheirOwnWindows()
    {
        // 한 블록의 구멍에서 무늬로 메울 자리가 두 군데 떨어져 남았다 — 따로 메운다(shift-map은 구멍이 클수록 훨씬 느리다). 창 = 그 묶음의 상자 + 둘레 ContextPx
        using var hole = new Mat(400, 700, MatType.CV_8UC1, Scalar.Black);
        var a = new Rect(120, 150, 40, 60);
        var b = new Rect(520, 180, 30, 50);
        hole.Rectangle(a, Scalar.White, -1);
        hole.Rectangle(b, Scalar.White, -1);

        var groups = TextureFill.Groups(hole);
        try
        {
            Assert.Equal(2, groups.Count);
            var byX = groups.OrderBy(g => g.Window.X).ToList();
            int c = TextureFill.ContextPx;
            Assert.Equal(new Rect(a.X - c, a.Y - c, a.Width + 2 * c, a.Height + 2 * c), byX[0].Window);
            Assert.Equal(new Rect(b.X - c, b.Y - c, b.Width + 2 * c, b.Height + 2 * c), byX[1].Window);
            // 묶음의 구멍은 제 창 크기의 마스크이고 제 조각만 들어 있다
            Assert.Equal(byX[0].Window.Size, byX[0].Hole.Size());
            Assert.Equal(a.Width * a.Height, Cv2.CountNonZero(byX[0].Hole));
            Assert.Equal(b.Width * b.Height, Cv2.CountNonZero(byX[1].Hole));
        }
        finally { foreach (var g in groups) g.Hole.Dispose(); }
    }

    [Fact]
    public void Groups_PiecesAFewPixelsApart_AreOneGroup_AndWindowsStayInsideTheImage()
    {
        // 글자 사이의 좁은 틈으로 갈린 조각들은 한 묶음이다(따로 메우면 창만 늘고, 틈을 사이에 두고 무늬가 어긋난다). 창은 그림 밖으로 나가지 않는다
        using var hole = new Mat(200, 260, MatType.CV_8UC1, Scalar.Black);
        hole.Rectangle(new Rect(20, 30, 30, 40), Scalar.White, -1);
        hole.Rectangle(new Rect(20 + 30 + 6, 30, 30, 40), Scalar.White, -1);   // 6px 틈

        var groups = TextureFill.Groups(hole);
        try
        {
            var g = Assert.Single(groups);
            Assert.Equal(2 * 30 * 40, Cv2.CountNonZero(g.Hole));
            Assert.Equal(new Rect(0, 0, 20 + 66 + TextureFill.ContextPx, 30 + 40 + TextureFill.ContextPx), g.Window);
        }
        finally { foreach (var g in groups) g.Hole.Dispose(); }
    }

    [Fact]
    public void Groups_NothingToFill_IsEmpty()
    {
        using var hole = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);
        Assert.Empty(TextureFill.Groups(hole));
    }

    // ── 한 창을 통째로 ──

    /// <summary>창 하나를 메우고 진단 덤프의 '방법'을 읽어 온다(NEKO_TEXFILL_DBG — 어떻게 메웠는지는 밖에서 그것으로만 보인다).</summary>
    private static (bool Done, string Method) FillWithMethod(Mat page, Rect area, string tag)
    {
        using var dir = new TempDir();
        string? before = Environment.GetEnvironmentVariable("NEKO_TEXFILL_DBG");
        Environment.SetEnvironmentVariable("NEKO_TEXFILL_DBG", dir.Root);
        try
        {
            using var own = new Mat(area.Height, area.Width, MatType.CV_8UC1, Scalar.White);
            using var avoid = RectMask(page.Width, page.Height, area);
            bool done = TextureFill.FillAll(page, [(area, own)], avoid, tag: tag)[0];
            string file = dir.At($"{tag}_{area.X}_{area.Y}_method.txt");
            return (done, File.Exists(file) ? File.ReadAllText(file) : "");
        }
        finally { Environment.SetEnvironmentVariable("NEKO_TEXFILL_DBG", before); }
    }

    [Fact]
    public void FillAll_TextOnInkBesideAPicture_PaintsTheInk_AndDoesNotDragThePictureIn()
    {
        // 먹칠한 칸의 큰 글, 오른쪽은 옆 칸의 그림(실제 04쪽 — 창 하나에 9초, 그림 조각이 글자 자리로 딸려 들어왔다):
        // 먹뿐인 둘레 쪽은 먹으로 칠하고, 그림에 닿은 가장자리만 무늬로 메운다
        using var page = new Mat(360, 460, MatType.CV_8UC3, Scalar.Black);
        var picture = new Rect(300, 0, 160, 360);
        page.Rectangle(picture, Scalar.All(200), -1);
        for (int y = 0; y < 360; y += 6) page.Line(picture.X, y, 459, y, Scalar.All(90), 2);   // 그림 대신 줄무늬
        var hole = new Rect(110, 110, 186, 140);                                               // 글자 자리(흰 테까지) — 오른쪽 끝이 그림에서 4px
        page.Rectangle(hole, Scalar.White, -1);

        var (done, method) = FillWithMethod(page, hole, "ink");

        Assert.True(done);
        Assert.StartsWith("flat", method);
        Assert.Contains("shiftmap", method);   // 그림에 닿은 가장자리는 무늬로
        // 그림에서 먼 쪽(구멍의 왼쪽 100px)은 전부 먹이다 — 그림의 회색·줄무늬가 들어오지 않았다
        using var left = new Mat(page, new Rect(hole.X, hole.Y, 100, hole.Height));
        using var gray = left.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out _, out double max);
        Assert.Equal(0, max);
    }

    [Fact]
    public void FillAll_TextOnPlainPaperOnly_IsJustPainted()
    {
        // 둘레가 전부 종이면 무늬로 메울 것이 없다(이런 블록은 보통 단색 칠하기·LaMa로 가지만, 여기로 와도 칠하고 끝난다)
        using var page = new Mat(300, 320, MatType.CV_8UC3, Scalar.All(250));
        var hole = new Rect(110, 90, 90, 110);
        page.Rectangle(hole, Scalar.Black, -1);

        var (done, method) = FillWithMethod(page, hole, "plain");

        Assert.True(done);
        Assert.StartsWith("flat", method);
        Assert.DoesNotContain("shiftmap", method);
        using var filled = new Mat(page, hole);
        using var gray = filled.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out double min, out double max);
        Assert.Equal((250, 250), ((int)min, (int)max));
    }

    [Fact]
    public void FillAll_OnlyASmallPartCouldBePainted_IsFilledWholeAsBefore()
    {
        // 무늬 위의 글인데 한쪽 끝만 빈 종이에 걸쳤다 — 칠할 수 있는 자리가 구멍의 절반이 안 되면 칠하지 않고 전처럼 창을 통째로 무늬로 메운다
        // (조금 칠해서는 빨라지지 않고, 칠한 자리 옆의 무늬가 달라져 결과만 흔들린다 — 실제 26쪽에서 본 것)
        using var page = new Mat(300, 460, MatType.CV_8UC3, Scalar.White);
        var rng = new Random(11);
        for (int i = 0; i < 2600; i++) page.Circle(140 + rng.Next(320), rng.Next(300), 1, Scalar.Black, -1);   // x ≥ 140은 점 무늬, 그 왼쪽은 빈 종이
        var hole = new Rect(60, 110, 300, 80);                                                                // 왼쪽 80px만 빈 종이 위다
        page.Rectangle(hole, new Scalar(128, 128, 128), -1);

        var (done, method) = FillWithMethod(page, hole, "mostly");

        Assert.True(done);
        Assert.StartsWith("shiftmap", method);
    }

    [Fact]
    public void FillAll_TwoTexturedPiecesInOneBlock_AreBothFilled()
    {
        // 한 블록의 구멍이 떨어진 두 덩어리(두 기둥)이고 둘 다 줄무늬가 아닌 무늬(바둑판) 위다 — 묶음 둘로 나눠 메워도 둘 다 메워지고, 구멍 밖은 그대로다
        using var original = new Mat(300, 520, MatType.CV_8UC3, Scalar.White);
        var rng = new Random(3);
        for (int i = 0; i < 2600; i++) original.Circle(rng.Next(520), rng.Next(300), 1, Scalar.Black, -1);   // 되풀이가 없는 점 무늬(격자로는 못 메운다)
        using var page = original.Clone();
        var a = new Rect(100, 90, 40, 110);
        var b = new Rect(330, 90, 40, 110);
        page.Rectangle(a, new Scalar(128, 128, 128), -1);
        page.Rectangle(b, new Scalar(128, 128, 128), -1);
        var area = new Rect(a.X, a.Y, b.Right - a.X, a.Height);
        using var own = new Mat(area.Height, area.Width, MatType.CV_8UC1, Scalar.Black);
        own.Rectangle(new Rect(0, 0, a.Width, a.Height), Scalar.White, -1);
        own.Rectangle(new Rect(b.X - a.X, 0, b.Width, b.Height), Scalar.White, -1);
        using var avoid = new Mat(300, 520, MatType.CV_8UC1, Scalar.Black);
        avoid.Rectangle(a, Scalar.White, -1);
        avoid.Rectangle(b, Scalar.White, -1);

        Assert.Equal([true], TextureFill.FillAll(page, [(area, own)], avoid));

        // 회색 덩어리(128)가 남아 있지 않다
        foreach (var r in new[] { a, b })
        {
            using var view = new Mat(page, r);
            using var grayish = new Mat();
            Cv2.InRange(view, new Scalar(120, 120, 120), new Scalar(136, 136, 136), grayish);
            Assert.Equal(0, Cv2.CountNonZero(grayish));
        }
        // 두 덩어리 사이(구멍이 아닌 자리)는 손대지 않았다
        var between = new Rect(a.Right + 4, a.Y, b.X - a.Right - 8, a.Height);
        using var x = new Mat(page, between); using var y = new Mat(original, between);
        using var diff = new Mat();
        Cv2.Absdiff(x, y, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff.CvtColor(ColorConversionCodes.BGR2GRAY)));
    }
}
