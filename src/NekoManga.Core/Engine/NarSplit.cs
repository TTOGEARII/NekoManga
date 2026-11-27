namespace NekoManga.Core.Engine;

/// <summary>manga-ocr-nar-small이 한 번에 못 읽는 긴 글을 줄 사이에서 갈라 읽는다.
///
/// 그 모델은 조각 하나에서 65자까지만 낸다. 글이 그보다 길면(후기·작가의 말·안내문처럼 줄이 많은 블록) 끝만 잘리는 것이 아니라 **글 전체가 깨진다**
/// (실측 2026-11-27, 다섯 줄짜리 안내문: "1れトトクククおるのの場本分分分…のののののの" — 65자 안에 끝 표시가 없다). 그대로 두면 쓰레기가 번역기로 간다.
/// 그래서 끝 표시 없이 65자를 다 채운 조각은 **줄(세로쓰기는 기둥) 사이의 틈에서 둘로 갈라** 따로 읽고 읽는 차례대로 잇는다 — 갈라진 반쪽이 또 넘치면 다시 가른다(<see cref="MaxDepth"/>번까지).
/// 틈은 그림만 보고 찾는다(글 감지기는 블록의 줄을 따로 주지 않는다): 글자 픽셀(바탕과 다른 쪽 — 적은 쪽)을 가로·세로로 세어 빈 띠를 찾고,
/// 줄 사이 틈이 글자 사이 틈보다 넓다는 것으로 글의 방향을 안다.</summary>
internal static class NarSplit
{
    /// <summary>가르기를 되풀이하는 깊이의 상한 — 조각 하나가 많아야 16토막. 갈라도 계속 넘친다는 조각(글이 아닌 그림)이 한없이 돌지 않게.</summary>
    public const int MaxDepth = 4;

    /// <summary>조각을 읽는다. <paramref name="read"/>(RGB, 너비, 높이) = (읽은 글, 넘쳤는가). 넘쳤고 가를 곳이 있으면 둘로 갈라 읽은 것을 읽는 차례대로 잇고(넘친 판의 깨진 글은 버린다),
    /// 가를 곳이 없거나 깊이를 다 쓰면 읽힌 대로 돌려준다.</summary>
    public static string Read(byte[] rgb, int w, int h, Func<byte[], int, int, (string Text, bool Overflow)> read, int depth = 0)
    {
        var (text, overflow) = read(rgb, w, h);
        if (!overflow || depth >= MaxDepth || Find(rgb, w, h) is not { } parts) return text;
        var (first, second) = parts;
        return Read(Cut(rgb, w, first), first.W, first.H, read, depth + 1) + Read(Cut(rgb, w, second), second.W, second.H, read, depth + 1);
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
        // 줄 = 글자 픽셀이 있는 띠. 가로줄은 행을 세어(가로로 누운 띠), 기둥은 열을 세어 찾는다
        var lines = Bands(rows, Math.Max(1, w / 100));
        var columns = Bands(cols, Math.Max(1, h / 100));
        bool byColumns = columns.Count >= 2 && (lines.Count < 2 || MedianGap(columns) > MedianGap(lines));
        if (!byColumns && lines.Count < 2) return null;

        if (byColumns)
        {
            int cut = MiddleGap(columns, w);
            var left = (0, 0, cut, h);
            var right = (cut, 0, w - cut, h);
            // 기둥으로 갈리면 세로쓰기다(오른쪽부터) — 다만 줄이 하나뿐인 아주 넓은 조각은 가로로 쓴 한 줄이고, 그 틈은 글자 사이다(왼쪽부터)
            bool oneHorizontalLine = lines.Count <= 1 && w >= 4 * h;
            return oneHorizontalLine ? (left, right) : (right, left);
        }
        else
        {
            int cut = MiddleGap(lines, h);
            return ((0, 0, w, cut), (0, cut, w, h - cut));
        }
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

    /// <summary>글자 픽셀이 있는 띠들(시작, 끝 — 끝은 포함하지 않는다). 픽셀이 <paramref name="tolerance"/> 이하인 자리는 빈 것으로 보고(얼룩),
    /// 다른 띠들의 가운뎃값의 절반보다 가는 띠는 줄이 아니라고 보고 뺀다(탁점·문장부호만 걸린 띠, 남은 선 조각).</summary>
    private static List<(int Start, int End)> Bands(int[] profile, int tolerance)
    {
        var bands = new List<(int Start, int End)>();
        int start = -1;
        for (int i = 0; i <= profile.Length; i++)
        {
            bool filled = i < profile.Length && profile[i] > tolerance;
            if (filled && start < 0) start = i;
            else if (!filled && start >= 0) { bands.Add((start, i)); start = -1; }
        }
        if (bands.Count == 0) return bands;
        var thickness = bands.Select(b => b.End - b.Start).Order().ToArray();
        double median = thickness[thickness.Length / 2];
        return bands.Where(b => b.End - b.Start >= median / 2).ToList();
    }

    private static double MedianGap(List<(int Start, int End)> bands)
    {
        var gaps = Enumerable.Range(1, bands.Count - 1).Select(i => bands[i].Start - bands[i - 1].End).Order().ToArray();
        return gaps[gaps.Length / 2];
    }

    /// <summary>띠 사이의 틈 가운데 한가운데(<paramref name="size"/> ÷ 2)에 가장 가까운 틈의 한복판.</summary>
    private static int MiddleGap(List<(int Start, int End)> bands, int size)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 1; i < bands.Count; i++)
        {
            int cut = (bands[i - 1].End + bands[i].Start) / 2;
            double distance = Math.Abs(cut - size / 2.0);
            if (distance < bestDistance) { bestDistance = distance; best = cut; }
        }
        return best;
    }
}
