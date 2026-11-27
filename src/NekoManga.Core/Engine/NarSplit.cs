namespace NekoManga.Core.Engine;

/// <summary>manga-ocr-nar-small이 한 번에 못 읽는 긴 글을 줄 사이에서 갈라 읽는다.
///
/// 그 모델은 조각 하나에서 65자까지만 낸다. 글이 그보다 길면(후기·작가의 말·안내문처럼 줄이 많은 블록) 끝만 잘리는 것이 아니라 **글 전체가 깨진다**
/// (실측 2026-11-27, 다섯 줄짜리 안내문: "1れトトクククおるのの場本分分分…のののののの" — 65자 안에 끝 표시가 없다). 65자가 안 되어도 글이 조각에 견줘 너무 많으면
/// (떨어진 풍선 여럿이 한 블록 — 조각이 넓어 글자가 패치 하나만 해진다) 같은 글자를 겹쳐 낸다(<see cref="Garbled"/>). 그대로 두면 쓰레기가 번역기로 간다.
/// 그래서 **넘쳤거나 깨진 읽기**는 줄(세로쓰기는 기둥) 사이의 틈에서 둘로 갈라 따로 읽고 읽는 차례대로 잇는다 — 반쪽이 또 그러면 다시 가른다(<see cref="MaxDepth"/>번까지).
/// **제대로 읽힌 글은 길어도 가르지 않는다**(길이로만 가르면 멀쩡하던 글에서 새로 틀린다), 깨졌다고 보고 가른 것은 **나아졌을 때만** 쓴다 — 수치는 notes/translate-letter.md.
/// 틈은 그림만 보고 찾는다(글 감지기는 블록의 줄을 따로 주지 않는다): 글자 픽셀(바탕과 다른 쪽 — 적은 쪽)을 가로·세로로 세어 띠를 찾고,
/// 좁은 틈으로만 떨어진 띠(글자와 후리가나, 가지런한 글자 칸)를 이은 뒤에도 띠가 둘 넘게 남는 쪽을 줄 방향으로 본다.</summary>
internal static class NarSplit
{
    /// <summary>가르기를 되풀이하는 깊이의 상한 — 조각 하나가 많아야 16토막. 갈라도 계속 넘친다는 조각(글이 아닌 그림)이 한없이 돌지 않게.</summary>
    public const int MaxDepth = 4;

    /// <summary>조각을 읽는다. <paramref name="read"/>(RGB, 너비, 높이) = (읽은 글, 넘쳤는가 — 65자 안에 끝 표시가 없다). 넘쳤거나 읽힌 글이 깨졌고(<see cref="Garbled"/>)
    /// 가를 곳이 있으면 둘로 갈라 읽은 것을 읽는 차례대로 잇는다. 가를 곳이 없거나 깊이를 다 쓰면 읽힌 대로 돌려준다.</summary>
    public static string Read(byte[] rgb, int w, int h, Func<byte[], int, int, (string Text, bool Overflow)> read, int depth = 0)
    {
        var (text, overflow) = read(rgb, w, h);
        bool garbled = !overflow && Garbled(text);
        if (!(overflow || garbled) || depth >= MaxDepth || Find(rgb, w, h) is not { } parts) return text;
        var (first, second) = parts;
        string joined = Read(Cut(rgb, w, first), first.W, first.H, read, depth + 1) + Read(Cut(rgb, w, second), second.W, second.H, read, depth + 1);
        if (overflow) return joined;   // 넘친 판은 끝까지 읽히지도 않았다 — 견줄 것 없이 버린다
        // 깨졌다고 보고 가른 것은 나아졌을 때만 쓴다: 겹침이 줄지 않았으면 원래 그렇게 겹치는 말이고("うおおおお女子とゲーム…"을 갈랐더니 お가 하나 늘었다),
        // 글이 절반 넘게 사라졌으면 엉뚱한 데서 갈린 것이다 — 그럴 때는 처음 읽은 것을 둔다
        var (was, wasRepeats) = CountRepeats(text);
        var (now, nowRepeats) = CountRepeats(joined);
        bool better = now * 2 >= was && (long)nowRepeats * was < (long)wasRepeats * now;
        return better ? joined : text;
    }

    /// <summary>읽힌 글이 깨졌는가 — 글자가 16자 넘는 글에서 바로 앞 글자와 같은 글자가 12% 넘게 나온다(문장부호와 늘인 소리 ー는 세지 않는다).
    /// 이 모델은 글이 조각에 견줘 너무 많으면(떨어진 풍선 여럿이 한 블록, 작은 글씨의 긴 단) 끝 표시는 내면서도 글자를 겹쳐 낸다:
    /// "…優良店で有名なん…" → "店店で名名なんんでですや………っっっっっっっ". 실측(읽힌 글 16자 이상 112개): 제대로 읽힌 것은 겹침 0~8%(84개가 0), 깨진 것은 27~71%.
    /// 짧은 글은 따지지 않는다 — "ああああっ"·"かわいい"처럼 원래 겹치는 말이 많다.</summary>
    public static bool Garbled(string text)
    {
        var (letters, repeats) = CountRepeats(text);
        return letters >= 16 && repeats * 100 >= letters * 12;
    }

    /// <summary>(글자 수, 바로 앞 글자와 같은 글자의 수) — 문장부호와 늘인 소리 ー는 세지 않는다("………"·"わーーーー"는 겹친 글자가 아니다).</summary>
    private static (int Letters, int Repeats) CountRepeats(string text)
    {
        int letters = 0, repeats = 0;
        char previous = '\0';
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) || c == 'ー') continue;
            letters++;
            if (c == previous) repeats++;
            previous = c;
        }
        return (letters, repeats);
    }

    /// <summary>RGB 그림(행 우선, 너비 <paramref name="w"/>)에서 사각형을 잘라 낸다.</summary>
    public static byte[] Cut(byte[] rgb, int w, (int X, int Y, int W, int H) r)
    {
        var cut = new byte[r.W * r.H * 3];
        for (int y = 0; y < r.H; y++)
            Buffer.BlockCopy(rgb, ((r.Y + y) * w + r.X) * 3, cut, y * r.W * 3, r.W * 3);
        return cut;
    }

    /// <summary>조각을 줄(기둥) 사이의 틈 가운데 한가운데에 가장 가까운 곳에서 둘로 가른다 — 돌려주는 것은 **읽는 차례대로**(세로쓰기는 오른쪽 반 먼저, 가로쓰기는 위쪽 반 먼저).
    /// 가를 곳이 없으면 null.</summary>
    public static ((int X, int Y, int W, int H) First, (int X, int Y, int W, int H) Second)? Find(byte[] rgb, int w, int h)
    {
        if (w < 8 || h < 8 || InkMask(rgb, w, h) is not { } ink) return null;
        DropLongStrokes(ink, w, h);
        var rows = new int[h];
        var cols = new int[w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (ink[y * w + x]) { rows[y]++; cols[x]++; }
        // 띠 = 글자 픽셀이 있는 자리. 행을 세면 가로로 누운 띠(가로쓰기의 줄), 열을 세면 세로로 선 띠(세로쓰기의 기둥)
        var rowBands = Bands(rows);
        var columnBands = Bands(cols);
        // 줄 = 좁은 틈으로만 떨어진 띠들을 이은 것(글자와 그 후리가나, 글자와 글자). 글자 칸이 가지런한 글은 줄과 엇갈린 방향에도 띠가 줄줄이 서지만(글자 줄) 그 틈은 좁아 다 이어진다
        var lines = Merge(rowBands);
        var columns = Merge(columnBands);

        bool byColumns, glyphGaps = false;
        if (lines.Count >= 2 || columns.Count >= 2)
            // 줄이 둘 넘게 서는 쪽이 줄 방향이다. 둘 다 그러면(글자 사이가 벌어진 글, 떨어진 풍선 둘이 한 블록) 더 줄다운 쪽
            byColumns = columns.Count >= 2 && (lines.Count < 2 || LineLikeness(columns) > LineLikeness(lines));
        else if (w >= h ? columnBands.Count >= 2 : rowBands.Count >= 2)
        {
            // 줄이 하나뿐인 긴 글 — 글자 사이에서 가른다(가로로 긴 조각은 가로 한 줄, 세로로 긴 조각은 기둥 하나)
            byColumns = w >= h;
            glyphGaps = true;
        }
        else return null;

        if (byColumns)
        {
            int cut = MiddleGap(glyphGaps ? columnBands : columns, w);
            var left = (0, 0, cut, h);
            var right = (cut, 0, w - cut, h);
            // 기둥으로 갈리면 세로쓰기다(오른쪽부터). 가로 한 줄(글자 사이에서 갈랐거나, 줄이 하나뿐인 아주 넓은 조각 — 낱말 덩어리가 기둥처럼 보인다)은 왼쪽부터
            bool oneHorizontalLine = glyphGaps || (lines.Count <= 1 && w >= 4 * h);
            return oneHorizontalLine ? (left, right) : (right, left);
        }
        else
        {
            int cut = MiddleGap(glyphGaps ? rowBands : lines, h);
            return ((0, 0, w, cut), (0, cut, w, h - cut));
        }
    }

    /// <summary>좁은 틈(띠 굵기 가운뎃값의 4분의 1 미만)으로만 떨어진 띠들을 하나로 잇는다 — 글자와 그 후리가나, 가지런한 글자 칸 사이.</summary>
    private static List<(int Start, int End)> Merge(List<(int Start, int End)> bands)
    {
        var merged = new List<(int Start, int End)>();
        if (bands.Count == 0) return merged;
        var thickness = bands.Select(b => b.End - b.Start).Order().ToArray();
        double narrow = thickness[thickness.Length / 2] / 4.0;
        foreach (var band in bands)
        {
            if (merged.Count > 0 && band.Start - merged[^1].End < narrow) merged[^1] = (merged[^1].Start, band.End);
            else merged.Add(band);
        }
        return merged;
    }

    /// <summary>띠들이 얼마나 줄다운가 — 굵기에 견준 틈의 넓이(틈의 가운뎃값 ÷ 굵기의 가운뎃값: 줄 사이는 0.3~0.8, 우연한 틈은 띠가 굵어 훨씬 작다)를
    /// 굵기의 들쭉날쭉함으로 깎는다. 줄은 굵기가 고르고(글자 크기), 엇갈린 방향의 띠(글자 칸)는 글자마다 폭이 달라 들쭉날쭉하다 —
    /// 실측(작은 안내문 다섯 줄): 벌어짐만으로는 0.47(줄) 대 0.42(글자 칸)로 거의 같았고, 들쭉날쭉함은 0.03 대 0.35였다.</summary>
    private static double LineLikeness(List<(int Start, int End)> bands)
    {
        var gaps = Gaps(bands).Order().ToArray();
        var thickness = bands.Select(b => b.End - b.Start).Order().ToArray();
        double median = thickness[thickness.Length / 2];
        var deviation = thickness.Select(v => Math.Abs(v - median)).Order().ToArray();
        double spread = deviation[deviation.Length / 2] / median;
        return gaps[gaps.Length / 2] / median / (1 + 4 * spread);
    }

    /// <summary>글자 픽셀의 표 — 밝기를 둘로 갈라(Otsu) 적은 쪽을 글자로 본다(흰 종이의 검은 글도, 짙은 바탕의 흰 글도). 밝기가 한 가지뿐인 조각은 null.</summary>
    private static bool[]? InkMask(byte[] rgb, int w, int h)
    {
        int n = w * h;
        var gray = new byte[n];
        var hist = new int[256];
        for (int i = 0; i < n; i++)
        {
            gray[i] = (byte)((rgb[i * 3] * 299 + rgb[i * 3 + 1] * 587 + rgb[i * 3 + 2] * 114) / 1000);
            hist[gray[i]]++;
        }
        int lo = Array.FindIndex(hist, c => c > 0), hi = Array.FindLastIndex(hist, c => c > 0);
        if (hi - lo < 40) return null;
        // Otsu: 두 무리 사이의 분산이 가장 큰 문턱
        long total = 0;
        for (int v = 0; v < 256; v++) total += (long)v * hist[v];
        long sumBelow = 0;
        int below = 0, threshold = lo;
        double best = -1;
        for (int v = lo; v < hi; v++)
        {
            below += hist[v];
            sumBelow += (long)v * hist[v];
            int above = n - below;
            if (below == 0 || above == 0) continue;
            double diff = (double)sumBelow / below - (double)(total - sumBelow) / above;
            double between = (double)below * above * diff * diff;
            if (between > best) { best = between; threshold = v; }
        }
        int dark = 0;
        for (int v = 0; v <= threshold; v++) dark += hist[v];
        bool inkIsDark = dark * 2 <= n;
        var ink = new bool[n];
        for (int i = 0; i < n; i++) ink[i] = gray[i] <= threshold == inkIsDark;
        return ink;
    }

    /// <summary>조각의 너비나 높이를 거의 다(90%) 가로지르는 이어진 덩어리를 글자 표에서 뺀다 — 둘레의 테·칸 테두리·풍선 테두리의 호.
    /// 그런 선은 어느 줄에나 걸려 있어 빈 띠를 가린다(글자는 줄 하나·기둥 하나를 넘지 않는다).</summary>
    private static void DropLongStrokes(bool[] ink, int w, int h)
    {
        var seen = new bool[ink.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || seen[start]) continue;
            members.Clear();
            int minX = w, maxX = 0, minY = h, maxY = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int at = stack.Pop(), x = at % w, y = at / w;
                members.Add(at);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int next = ny * w + nx;
                    if (!ink[next] || seen[next]) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }
            if ((maxX - minX + 1) * 10 >= w * 9 || (maxY - minY + 1) * 10 >= h * 9)
                foreach (int at in members) ink[at] = false;
        }
    }

    /// <summary>글자 픽셀이 있는 띠들(시작, 끝 — 끝은 포함하지 않는다). 빈 자리는 **상대 높이**로 가른다: 빈 자리의 높이에서 글 자리의 높이 쪽으로 15% 올라온 데까지가 틈이다.
    /// 픽셀 수 0(또는 몇 개)만 틈으로 보면 안 된다 — 실측(잡지 인터뷰 단, 흰 글·붉은 바탕): 위아래의 가는 괘선이 모든 열에 5픽셀씩 걸려 기둥 사이가 '비지 않은' 것으로 읽혔고,
    /// 기둥을 못 찾아 글자 줄 사이(2px)에서 가로로 갈라 읽는 차례가 뒤집혔다. 다른 띠들의 가운뎃값의 절반보다 가는 띠는 줄이 아니라고 보고 뺀다(탁점·문장부호만 걸린 띠, 남은 선 조각).</summary>
    private static List<(int Start, int End)> Bands(int[] profile)
    {
        var bands = new List<(int Start, int End)>();
        var all = profile.Order().ToArray();
        var filledOnes = all.Where(v => v > 0).ToArray();
        if (filledOnes.Length == 0) return bands;
        // 빈 자리의 높이(아래 1할)와 글 자리의 높이(글자가 있는 자리들의 위 사분위)
        double low = all[all.Length / 10], high = filledOnes[filledOnes.Length * 3 / 4];
        // 빈 자리가 글 자리의 35%를 넘으면 이 방향에는 골이 없다(띠 하나) — 띠 안의 옅은 자리를 틈으로 읽지 않게
        double threshold = low < high * 0.35 ? low + (high - low) * 0.15 : -1;
        int start = -1;
        for (int i = 0; i <= profile.Length; i++)
        {
            bool filled = i < profile.Length && profile[i] > threshold;
            if (filled && start < 0) start = i;
            else if (!filled && start >= 0) { bands.Add((start, i)); start = -1; }
        }
        var thickness = bands.Select(b => b.End - b.Start).Order().ToArray();
        double median = thickness[thickness.Length / 2];
        return bands.Where(b => b.End - b.Start >= median / 2).ToList();
    }

    private static IEnumerable<int> Gaps(List<(int Start, int End)> bands) =>
        Enumerable.Range(1, bands.Count - 1).Select(i => bands[i].Start - bands[i - 1].End);

    /// <summary>띠 사이의 **넓은 틈**(가장 넓은 틈의 절반 이상 — 줄 사이) 가운데 한가운데(<paramref name="size"/> ÷ 2)에 가장 가까운 틈의 한복판.
    /// 좁은 틈(글자와 그 후리가나 사이)에서 가르면 후리가나만 옆 토막으로 넘어가 본문처럼 읽힌다.</summary>
    private static int MiddleGap(List<(int Start, int End)> bands, int size)
    {
        int widest = Gaps(bands).Max(), best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 1; i < bands.Count; i++)
        {
            if ((bands[i].Start - bands[i - 1].End) * 2 < widest) continue;   // 글자와 그 후리가나 사이 같은 좁은 틈
            int cut = (bands[i - 1].End + bands[i].Start) / 2;
            double distance = Math.Abs(cut - size / 2.0);
            if (distance < bestDistance) { bestDistance = distance; best = cut; }
        }
        return best;
    }
}
