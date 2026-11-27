using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>manga-ocr-nar-small이 한 번에 못 읽는 긴 글(65자 안에 끝나지 않는 글 — 후기·안내문처럼 줄이 많은 블록)을 줄 사이에서 갈라 읽기(<see cref="NarSplit"/>).
/// 실측(2026-11-27): 넘친 조각은 끝만 잘리는 것이 아니라 글 전체가 깨졌다("のののの…") — 그대로 두면 쓰레기가 번역기로 간다.
/// 그림은 글자 대신 속이 빈 네모를 줄지어 그린 것이다 — 실제 글처럼 글자끼리는 거의 붙고(2px) 줄 사이는 벌어져 있으며(8px), 획은 칸의 일부만 채운다
/// (속을 채운 네모로 그리면 글자가 바탕보다 많아져 '적은 쪽이 글자'가 뒤집힌다).</summary>
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

        /// <summary>글자 하나 — 테 굵기 2px의 속 빈 네모.</summary>
        public Canvas Glyph(int x, int y, int gw, int gh, byte ink = 0, byte paper = 255) => Box(x, y, gw, gh, ink).Box(x + 2, y + 2, gw - 4, gh - 4, paper);
    }

    /// <summary>세로쓰기: 기둥 여럿(기둥 사이 8px, 글자 사이 2px — 글자는 10×10). 기둥의 왼쪽 끝 x들을 준다.</summary>
    private static Canvas Columns(int[] columnX, int glyphs = 6, byte paper = 255, byte ink = 0)
    {
        var c = new Canvas(columnX[^1] + 10 + 6, 6 + glyphs * 12 + 4, paper);
        foreach (int x in columnX)
            for (int g = 0; g < glyphs; g++) c.Glyph(x, 6 + g * 12, 10, 10, ink, paper);
        return c;
    }

    /// <summary>가로쓰기: 줄 여럿(줄 사이 8px, 글자 사이 2px — 글자는 10×10). 줄의 위쪽 끝 y들을 준다.</summary>
    private static Canvas Lines(int[] lineY, int glyphs = 8, byte paper = 255, byte ink = 0)
    {
        var c = new Canvas(6 + glyphs * 12 + 4, lineY[^1] + 10 + 6, paper);
        foreach (int y in lineY)
            for (int g = 0; g < glyphs; g++) c.Glyph(6 + g * 12, y, 10, 10, ink, paper);
        return c;
    }

    [Fact]
    public void Find_VerticalText_SplitsBetweenColumns_RightHalfFirst()
    {
        // 기둥 넷(x = 6·24·42·60, 폭 10) — 가운데 틈(34~42)에서 가르고, 세로쓰기는 오른쪽 기둥부터 읽는다
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
        // 붉은 바탕의 흰 글(실제 넘친 조각이 이랬다) — 글자는 '적은 쪽'이다. 속을 채운 글자로 그린다: 속 빈 네모로 그리면 바탕을 글자로 잘못 골라도
        // (온 조각에 걸친 바탕 덩어리는 '긴 선'으로 빠지고) 네모 속에 갇힌 바탕이 글자처럼 남아 같은 답이 나온다
        static Canvas Solid(byte paper, byte ink)
        {
            var c = new Canvas(106, 76, paper);
            foreach (int y in new[] { 6, 24, 42, 60 })
                for (int g = 0; g < 8; g++) c.Box(6 + g * 12, y, 10, 10, ink);
            return c;
        }
        var dark = Solid(paper: 60, ink: 250);
        var light = Solid(paper: 255, ink: 0);

        var parts = NarSplit.Find(dark.Rgb, dark.W, dark.H);

        Assert.NotNull(parts);
        Assert.Equal(NarSplit.Find(light.Rgb, light.W, light.H), parts);
    }

    [Fact]
    public void Find_TextInsideAThickFrame_SplitsBetweenTheLines_NotAtTheFrame()
    {
        // 굵은 테(6px)를 두른 글상자 — 테와 글 사이(20px)가 줄 사이(8px)보다 훨씬 넓다. 테를 줄로 세면 가장 넓은 틈이 테와 글 사이가 되어
        // 테 옆에서 가르게 된다(글은 그대로 넘친다). 조각을 가로지르는 이어진 덩어리(테·칸 테두리)는 글자가 아니다
        var c = new Canvas(6 + 20 + 94 + 20 + 6, 6 + 20 + 64 + 20 + 6);
        c.Box(0, 0, c.W, 6).Box(0, c.H - 6, c.W, 6).Box(0, 0, 6, c.H).Box(c.W - 6, 0, 6, c.H);
        for (int line = 0; line < 4; line++)
            for (int g = 0; g < 8; g++) c.Glyph(26 + g * 12, 26 + line * 18, 10, 10);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Equal((0, c.W, 0), (parts.Value.First.X, parts.Value.First.W, parts.Value.First.Y));
        Assert.InRange(parts.Value.First.H, 55, 61);   // 둘째 줄(54 끝)과 셋째 줄(62 시작) 사이
    }

    [Fact]
    public void Find_SmallMarksBetweenLines_AreNotLines()
    {
        // 줄 사이에 낀 가는 것(후리가나 줄·밑줄 — 높이 2px, 글자 줄만큼 짙다)은 줄이 아니다: 그것을 줄로 세면 가르는 자리가 그 위아래로 밀린다
        var plain = Lines([6, 24, 42, 60]);
        var marked = Lines([6, 24, 42, 60]);
        marked.Box(20, 37, 60, 2);

        Assert.Equal(NarSplit.Find(plain.Rgb, plain.W, plain.H), NarSplit.Find(marked.Rgb, marked.W, marked.H));
    }

    [Fact]
    public void Find_VerticalTextWithFurigana_CutsBetweenColumns_NotBetweenACharacterAndItsFurigana()
    {
        // 기둥 셋(x = 6·30·54, 폭 10)마다 오른쪽에 후리가나 기둥(폭 5, 1px 떨어짐)이 붙어 있다. 한가운데(38)에 가장 가까운 틈은 둘째 기둥과 그 후리가나 사이(40~41)다 —
        // 거기서 가르면 후리가나만 옆 토막으로 넘어가 본문처럼 읽힌다. 줄 사이의 넓은 틈(22~30, 46~54)에서만 가른다
        var c = new Canvas(76, 82);
        foreach (int x in new[] { 6, 30, 54 })
            for (int g = 0; g < 6; g++)
            {
                c.Glyph(x, 6 + g * 12, 10, 10);
                c.Box(x + 11, 6 + g * 12, 5, 4);
            }

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        var (first, second) = parts.Value;
        Assert.Equal((0, c.H), (first.Y, first.H));          // 기둥 사이에서 갈랐다(줄 사이가 아니라)
        Assert.Contains(first.X, new[] { 26, 50 });          // 넓은 틈의 한복판
        Assert.Equal(c.W, first.X + first.W);                // 오른쪽 반이 먼저
        Assert.Equal((0, first.X), (second.X, second.W));
    }

    [Fact]
    public void Find_ARuleLineLeavesAFewPixelsInEveryColumn_TheColumnGapsAreStillFound()
    {
        // 실측(잡지 인터뷰 단): 위아래의 가는 괘선이 조각 폭의 8할쯤만 걸쳐 있어 '긴 선'으로 빠지지도 않고, 걸친 열마다 몇 픽셀씩 남겼다 —
        // '픽셀 0인 열'만 틈으로 보면 기둥 사이가 하나도 안 비어 보인다. 틈은 글 자리보다 훨씬 낮은 자리다(0이 아니어도)
        var c = Columns([6, 24, 42, 60, 78]);
        c.Box(8, 2, c.W - 20, 2).Box(8, c.H - 3, c.W - 20, 2);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Equal((0, c.H), (parts.Value.First.Y, parts.Value.First.H));   // 기둥 사이에서 갈랐다
        Assert.Contains(parts.Value.First.X, new[] { 38, 56 });               // 가운데(47)에 가장 가까운 기둥 틈의 한복판
    }

    [Fact]
    public void Find_NarrowGapNearTheMiddle_CutsAtAWideGap()
    {
        // 기둥 넷 가운데 둘째와 셋째가 바짝 붙어 있다(틈 3px — 이어질 만큼 좁지는 않다: 큰 후리가나, 붙여 쓴 두 줄). 한가운데에 가장 가까운 틈이 그것이어도
        // 넓은 틈(8px)에서 가른다 — 좁은 틈에서 가르면 붙어 있던 것이 서로 다른 토막으로 간다
        var c = new Canvas(71, 82);
        foreach (int x in new[] { 6, 24, 37, 55 })
            for (int g = 0; g < 6; g++) c.Glyph(x, 6 + g * 12, 10, 10);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Contains(parts.Value.First.X, new[] { 20, 51 });   // 16~24 또는 47~55의 한복판 (34~37의 35가 아니라)
    }

    [Fact]
    public void Find_HorizontalTextWithLooseLetterSpacing_StillSplitsBetweenLines()
    {
        // 실측(잡지의 작은 안내문 다섯 줄): 글자 사이가 벌어진 가로 글은 세로로 세어도 띠가 줄줄이 서고, 그 틈이 줄 사이만큼 벌어져 보인다(0.42 대 0.47).
        // 줄은 굵기가 고르고(글자 높이), 글자 칸의 띠는 들쭉날쭉하다(글자마다 폭이 다르다) — 고른 쪽이 줄이다
        int[] widths = [10, 5, 10, 4, 9, 6, 10, 5];
        var c = new Canvas(6 + widths.Sum() + widths.Length * 5 + 4, 6 + 4 * 15 + 2);
        for (int line = 0; line < 4; line++)
        {
            int x = 6;
            foreach (int gw in widths) { c.Glyph(x, 6 + line * 15, gw, 10); x += gw + 5; }
        }

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        Assert.Equal((0, c.W, 0), (parts.Value.First.X, parts.Value.First.W, parts.Value.First.Y));   // 줄 사이에서 갈랐고 위쪽이 먼저
        Assert.InRange(parts.Value.First.H, 32, 35);                                                    // 둘째 줄(31 끝)과 셋째 줄(36 시작) 사이
    }

    [Fact]
    public void Find_OneHorizontalLineOfSeparatedWords_SplitsBetweenWords_LeftHalfFirst()
    {
        // 가로 한 줄에 낱말 덩어리 셋(덩어리 사이 9px)이 있으면 세로로 센 띠가 '기둥'처럼 보인다 — 줄이 하나뿐인 아주 넓은 조각은 가로 한 줄이다(왼쪽부터)
        var c = new Canvas(6 + 3 * (5 * 12) + 2 * 7 + 4, 22);
        for (int word = 0; word < 3; word++)
            for (int g = 0; g < 5; g++) c.Glyph(6 + word * (5 * 12 + 7) + g * 12, 6, 10, 10);

        var parts = NarSplit.Find(c.Rgb, c.W, c.H);

        Assert.NotNull(parts);
        var (first, second) = parts.Value;
        Assert.Equal((0, 0, c.H), (first.X, first.Y, first.H));
        Assert.Equal((first.W, c.W - first.W), (second.X, second.W));
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

    // ── 깨진 읽기 알아보기 ──

    [Theory]
    // 실제로 나온 깨진 읽기들(2026-11-27): 글이 조각에 견줘 너무 많으면(여러 풍선이 한 블록·작은 글씨의 긴 단) 끝 표시는 있어도 같은 글자가 줄줄이 겹쳐 나온다
    [InlineData("えっやっすふふチうチ店店で名名なんんでですや………っっっっっっっくくくらららられるれれなななななななないいいいいいいただの居酒屋なんですからw")]
    [InlineData("イ…っかけはははワワニニマガジンさんからら開開開のDMををしして頂頂たここでで。")]
    [InlineData("●パソコンスママーフフフなな複数数の末未機かからの応応募は遠慮慮下い((一人人につき回回り))")]
    public void Garbled_ManyDoubledCharacters_InALongReading(string text)
    {
        Assert.True(NarSplit.Garbled(text));
    }

    [Theory]
    // 제대로 읽힌 긴 글 — 우연히 겹친 글자(ここ·いい·ナナメ)가 한둘 있어도 깨진 것이 아니다
    [InlineData("●私もいつかスペースカイマンさんで、写真やイラストの展覧会を開催したいという夢があります。その夢を叶えるために、これからも活動を一生懸命頑張っていき")]
    [InlineData("なんだこいつここら辺じゃ見ない生き物だな…")]
    [InlineData("汗」ってスタッフに聞いたら「連絡先交換だよ」って言われてナナメ上の回答だった")]
    // 짧은 글의 겹친 글자는 원래 그런 말이다(신음·늘인 소리) — 짧으면 따지지 않는다
    [InlineData("ああああああっ")]
    [InlineData("かわいいいい…")]
    // 늘인 소리의 ー는 겹쳐도 글자가 겹친 것이 아니다
    [InlineData("うわーーーーーーーーーーやめてくれーーーーーーーーーーーーーー")]
    [InlineData("")]
    public void Garbled_CleanOrShortReadings_AreNot(string text)
    {
        Assert.False(NarSplit.Garbled(text));
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
    public void Read_OverflowingText_IsNeverKept_EvenWhenTheSplitReadsLittle()
    {
        // 넘친 판은 끝까지 읽히지도 않은 글이다 — 갈라 읽은 것이 짧거나 겹친 글자가 많아 '나아 보이지' 않아도 넘친 판으로 돌아가지 않는다
        // (깨졌다고 보고 가른 것만 처음 읽은 것과 견준다)
        var c = Lines([6, 24, 42, 60]);
        int reads = 0;

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, h) =>
        {
            reads++;
            return h == c.H ? ("あいうえおかきくけこさしすせそたちつてとなにぬねの", true) : reads == 2 ? ("ああ", false) : ("いい", false);
        });

        Assert.Equal("ああいい", text);
    }

    [Fact]
    public void Read_GarbledReading_IsSplitLikeAnOverflow()
    {
        // 끝 표시는 있지만(넘치지 않았다) 같은 글자가 줄줄이 겹친 읽기 → 갈라서 다시 읽은 것을 쓴다
        var c = Lines([6, 24, 42, 60]);
        int reads = 0;

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, h) =>
        {
            reads++;
            return h == c.H ? ("店店で名名なんんでですや………っっっっっっっくくくらららられる", false)
                : reads == 2 ? ("優良店で有名なんですよ", false) : ("ぼったくられる流れかと", false);
        });

        Assert.Equal("優良店で有名なんですよぼったくられる流れかと", text);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void Read_GarbledButSplittingDoesNotReduceTheRepeats_KeepsTheFirstReading()
    {
        // 실측: "うおおおお女子とゲーム…"(원래 겹치는 말, 겹침 15%)을 깨졌다고 보고 갈랐더니 "うおおおおお…"로 한 글자가 늘었다 — 갈라도 겹침이 줄지 않으면 처음 읽은 것이 맞다
        var c = Lines([6, 24, 42, 60]);
        int reads = 0;

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, h) =>
        {
            reads++;
            return h == c.H ? ("うおおおお女子とゲーム女子とゲーム女子とゲーム", false)
                : reads == 2 ? ("うおおおおお女子とゲーム", false) : ("女子とゲーム女子とゲーム", false);
        });

        Assert.Equal("うおおおお女子とゲーム女子とゲーム女子とゲーム", text);
    }

    [Fact]
    public void Read_GarbledAndTheSplitLosesMostOfTheText_KeepsTheFirstReading()
    {
        // 엉뚱한 데서 갈려 반쪽들이 거의 읽히지 않았다 — 겹침은 없어졌지만 글도 없어졌다. 깨진 글이라도 처음 읽은 것을 둔다(번역 검수에서 보이게)
        var c = Lines([6, 24, 42, 60]);
        int reads = 0;

        var text = NarSplit.Read(c.Rgb, c.W, c.H, (_, _, h) =>
        {
            reads++;
            return h == c.H ? ("店店で名名なんんでですや………っっっっっっっくくくらららられる", false) : reads == 2 ? ("優良", false) : ("", false);
        });

        Assert.Equal("店店で名名なんんでですや………っっっっっっっくくくらららられる", text);
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
