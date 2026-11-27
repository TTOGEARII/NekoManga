using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>manga-ocr-nar-small이 한 번에 못 읽는 긴 글(65자 안에 끝나지 않는 글 — 후기·안내문처럼 줄이 많은 블록)을 줄 사이에서 갈라 읽기(<see cref="NarSplit"/>).
/// 실측(2026-11-27): 넘친 조각은 끝만 잘리는 것이 아니라 글 전체가 깨졌다("のののの…") — 그대로 두면 쓰레기가 번역기로 간다.
/// 그림은 글자 대신 네모 칸을 줄지어 그린 것이다(줄 사이 틈이 글자 사이 틈보다 넓다 — 세로쓰기는 기둥, 가로쓰기는 줄).</summary>
public class NarSplitTests
{
    private sealed class Canvas(int w, int h, byte paper = 255)
    {
        public readonly int W = w, H = h;
        public readonly byte[] Rgb = Enumerable.Repeat(paper, w * h * 3).ToArray();

        public Canvas Box(int x, int y, int bw, int bh, byte ink = 0)
        {
            for (int yy = y; yy < y + bh; yy++)
            for (int xx = x; xx < x + bw; xx++)
                Rgb[(yy * W + xx) * 3] = Rgb[(yy * W + xx) * 3 + 1] = Rgb[(yy * W + xx) * 3 + 2] = ink;
            return this;
        }
    }

    /// <summary>세로쓰기: 기둥 여럿(기둥 사이 8px, 글자 사이 6px — 글자는 10×6 네모, 실제 글처럼 칸의 3할쯤만 채운다). 기둥의 왼쪽 끝 x들을 준다.</summary>
    private static Canvas Columns(int[] columnX, int glyphs = 6, byte paper = 255, byte ink = 0)
    {
        var c = new Canvas(columnX[^1] + 10 + 6, 6 + glyphs * 12 + 4, paper);
        foreach (int x in columnX)
            for (int g = 0; g < glyphs; g++) c.Box(x, 6 + g * 12, 10, 6, ink);
        return c;
    }

    /// <summary>가로쓰기: 줄 여럿(줄 사이 8px, 글자 사이 6px — 글자는 6×10 네모). 줄의 위쪽 끝 y들을 준다.</summary>
    private static Canvas Lines(int[] lineY, int glyphs = 8, byte paper = 255, byte ink = 0)
    {
        var c = new Canvas(6 + glyphs * 12 + 4, lineY[^1] + 10 + 6, paper);
        foreach (int y in lineY)
            for (int g = 0; g < glyphs; g++) c.Box(6 + g * 12, y, 6, 10, ink);
        return c;
    }

    [Fact]
    public void Find_VerticalText_SplitsBetweenColumns_RightHalfFirst()
    {
        // 기둥 넷(x = 6·24·42·60, 폭 10) — 가운데 틈(34~41)에서 가르고, 세로쓰기는 오른쪽 기둥부터 읽는다
        var c = Columns([6, 24, 42, 60]);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        var (first, second) = parts.Value;
        Assert.Equal((0, c.H, 0, c.H), (first.Y, first.H, second.Y, second.H));   // 위아래는 그대로
        Assert.InRange(first.X, 35, 41);                                            // 오른쪽 반이 먼저
        Assert.Equal(c.W, first.X + first.W);
        Assert.Equal((0, first.X), (second.X, second.W));
    }

    [Fact]
    public void Find_HorizontalText_SplitsBetweenLines_TopHalfFirst()
    {
        var c = Lines([6, 24, 42, 60]);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        var (first, second) = parts.Value;
        Assert.Equal((0, c.W, 0, c.W), (first.X, first.W, second.X, second.W));
        Assert.Equal(0, first.Y);                                                   // 위쪽 반이 먼저
        Assert.InRange(first.H, 35, 41);
        Assert.Equal((first.H, c.H - first.H), (second.Y, second.H));
    }

    [Fact]
    public void Find_LightTextOnDarkPaper_SplitsTheSameWay()
    {
        // 붉은 바탕의 흰 글(실제 넘친 조각이 이랬다) — 글자는 '적은 쪽'이다
        var dark = Lines([6, 24, 42, 60], paper: 60, ink: 250);
        var light = Lines([6, 24, 42, 60]);

        Assert.Equal(NarSplit.Find(light.Rgb, light.W, light.H), NarSplit.Find(dark.Rgb, dark.W, dark.H));
    }

    [Fact]
    public void Find_TextInsideAFrame_StillFindsTheLineGaps()
    {
        // 둘레에 테(굵기 3px)가 있으면 어느 줄에나 테의 픽셀이 있다 — 테만큼은 빼고 틈을 본다. 테 자체는 줄이 아니다
        var c = Lines([10, 28, 46, 64]);
        c.Box(0, 0, 3, c.H).Box(c.W - 3, 0, 3, c.H).Box(0, 0, c.W, 3).Box(0, c.H - 3, c.W, 3);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Equal(0, parts.Value.First.Y);
        Assert.InRange(parts.Value.First.H, 39, 45);   // 둘째 줄(38 끝)과 셋째 줄(46 시작) 사이
    }

    [Fact]
    public void Find_SmallMarksBetweenLines_AreNotLines()
    {
        // 줄 사이에 낀 작은 것(후리가나·탁점·얼룩 — 높이 2px)은 줄이 아니다: 그것을 줄로 세면 가르는 자리가 그 위아래로 밀린다
        var plain = Lines([6, 24, 42, 60]);
        var marked = Lines([6, 24, 42, 60]);
        marked.Box(30, 37, 6, 2);

        Assert.Equal(NarSplit.Find(plain.Rgb, plain.W, plain.H), NarSplit.Find(marked.Rgb, marked.W, marked.H));
    }

    [Fact]
    public void Find_OneLongHorizontalLine_SplitsBetweenGlyphs_LeftHalfFirst()
    {
        // 줄 하나짜리 가로 글(아주 넓다)은 글자 사이에서 가르고 왼쪽부터 읽는다 — 기둥으로 보고 오른쪽부터 읽으면 앞뒤가 바뀐다
        var c = Lines([6], glyphs: 20);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        var (first, second) = parts.Value;
        Assert.Equal(0, first.X);
        Assert.InRange(first.W, c.W / 2 - 8, c.W / 2 + 8);
        Assert.Equal((first.W, c.W - first.W), (second.X, second.W));
    }

    [Fact]
    public void Find_OneVerticalColumn_SplitsBetweenGlyphs_TopHalfFirst()
    {
        var c = Columns([6], glyphs: 20);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Equal(0, parts.Value.First.Y);
        Assert.InRange(parts.Value.First.H, c.H / 2 - 8, c.H / 2 + 8);
    }

    [Fact]
    public void Find_NothingToSplit_ReturnsNull()
    {
        Assert.Null(NarSplit.Find(new Canvas(40, 40).Rgb, 40, 40));                       // 빈 종이
        Assert.Null(NarSplit.Find(new Canvas(40, 40).Box(8, 8, 24, 24).Rgb, 40, 40));     // 덩어리 하나
    }

    [Fact]
    public void Cut_TakesTheRectangleOutOfTheImage()
    {
        var c = new Canvas(6, 4);
        for (int i = 0; i < c.Rgb.Length; i++) c.Rgb[i] = (byte)i;

        var cut = NarSplit.Cut(c.Rgb, c.W, (2, 1, 3, 2));

        // (2,1)~(4,2): 줄 1의 칸 2·3·4, 줄 2의 칸 2·3·4 — 픽셀마다 3바이트
        Assert.Equal(new byte[] { 24, 25, 26, 27, 28, 29, 30, 31, 32, 42, 43, 44, 45, 46, 47, 48, 49, 50 }, cut);
    }

    // ── 넘친 글 다시 읽기 ──

    [Fact]
    public void Read_TextThatFits_IsReadOnce()
    {
        int reads = 0;
        var c = Lines([6, 24]);

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, _) => { reads++; return ("짧은 글", false); });

        Assert.Equal("짧은 글", text);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Read_OverflowingText_IsSplitAndJoinedInReadingOrder()
    {
        // 네 줄짜리 글이 넘쳤다 → 위 두 줄·아래 두 줄로 갈라 읽고 위쪽 것을 앞에 잇는다. 넘친 판의 깨진 글은 버린다
        var c = Lines([6, 24, 42, 60]);
        var seen = new List<int>();

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, h) =>
        {
            seen.Add(h);
            return h == c.H ? ("のののののの", true) : seen.Count == 2 ? ("위쪽", false) : ("아래쪽", false);
        });

        Assert.Equal("위쪽아래쪽", text);
        Assert.Equal(3, seen.Count);
        Assert.Equal(c.H, seen[1] + seen[2]);
    }

    [Fact]
    public void Read_OverflowWithNowhereToSplit_KeepsWhatWasRead()
    {
        var c = new Canvas(40, 40).Box(8, 8, 24, 24);

        Assert.Equal("못 가른 글", NarSplit.Read(c.Rgb, c.W, c.H, (_, _, _) => ("못 가른 글", true)));
    }

    [Fact]
    public void Read_StopsSplittingAtTheDepthLimit()
    {
        // 갈라도 갈라도 넘친다고 하는 조각(글이 아닌 그림) — 한없이 가르지 않는다: 깊이 MaxDepth까지, 읽기는 2^(MaxDepth+1) − 1번을 넘지 않는다
        var c = Lines(Enumerable.Range(0, 40).Select(i => 6 + i * 18).ToArray(), glyphs: 40);
        int reads = 0;

        NarSplit.Read(c.Rgb, c.W, c.H, (_, _, _) => { reads++; return ("x", true); });

        Assert.InRange(reads, 3, (1 << (NarSplit.MaxDepth + 1)) - 1);
    }
}
