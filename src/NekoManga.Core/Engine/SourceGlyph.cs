using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>원문 글자를 픽셀에서 잰 값 — 식자가 원문의 글자 꼴을 따라갈 때 쓰는 근거다(<see cref="TextBlockInfo.SrcEm"/>·SrcStroke·SrcContrast에 그대로 남긴다).</summary>
/// <param name="Em">글자 한 칸의 크기(원본 그림의 px) — 번역 글의 글자 크기로 쓴다. 0 = 못 쟀다(지어내지 않는다).</param>
/// <param name="Stroke">고른 획 굵기 ÷ Em(2 × 넓이 ÷ 둘레) — 실측: 본문 0.05~0.085, 굵은 고딕 0.096~0.107, 아주 굵은 글 0.15. 가는 글꼴과 보통 본문은 이것으로 못 가른다(아래 Thick).</param>
/// <param name="Thick">굵은 획의 굵기 ÷ Em(획 한가운데에서 잰 굵기들의 위 1할) — 식자의 굵기 판정이 보는 값. 실측: 가는 둥근 고딕 0.082~0.104 · 보통 본문(앤티크) 0.126~0.152 · 굵은 고딕 0.139~0.159 · 아주 굵은 글 0.22.
/// 같은 식으로 잰 맑은 고딕은 보통 0.08~0.095 · 굵게 0.125~0.155다. 0 = 못 쟀다.</param>
/// <param name="Contrast">세로 획의 굵기 ÷ 가로 획의 굵기 — 명조는 가로 획이 가늘어 2를 넘고 고딕은 1쯤이다. 0 = 곧은 획이 모자라 못 쟀다.</param>
internal readonly record struct GlyphStats(double Em, double Stroke, double Thick, double Contrast);

/// <summary>원문 글자의 크기·획 굵기·획 대비를 블록의 픽셀에서 잰다(4단계, 2026-11-27 — 사용자 "번역 역식은 carrot와 유사하게", 식자 = 원문 글자 크기 + 굵기 + 글꼴 분위기. 규칙 A8·A9).
/// 전에는 번역 글의 글자 수로 어림한 크기(<c>EstimateFontSize</c>)뿐이었고 식자는 그것도 쓰지 않았다(풍선에 들어가는 가장 큰 크기의 쪽 가운뎃값).
///
/// 재는 법: 블록에서 글자 픽셀(잉크)을 가려 줄(세로쓰기면 기둥)로 나누고 둘을 잰다 —
///  · T = 줄의 굵기(가운뎃값). 후리가나처럼 가는 띠(가장 굵은 띠의 58% 미만)는 줄로 세지 않는다.
///  · P = 줄들의 잉크 길이를 다 더해 글자 칸 수로 나눈 것(글자 한 칸의 길이). 칸 수는 읽은 글에서 센다(<see cref="Cells"/>).
/// 둘이 맞으면(P ÷ T가 0.85~1.40) 크기 = min(P, 1.12 × T) — 글자를 벌려 놓은 글은 칸이 글자보다 크므로 굵기 쪽을, 줄이 후리가나와 붙어 굵게 재진 글은 칸 쪽을 따른다.
/// 안 맞으면(글자 수를 틀리게 읽었다·줄이 서로 붙었다·바탕 무늬가 잉크로 들어왔다) **포기한다** — 그 블록은 식자할 때 쪽의 다른 블록에서 잰 값을 따른다.
///
/// 시제품의 실측(합성 504조각 — 글꼴 여섯·18~64px, 정답을 아는 것): 오차 가운뎃값 −1.8%(10~90%: −3.1~−0.7%), T ÷ em = 0.865.
/// 실제 다섯 쪽 55블록: P ÷ T = 0.90~1.35(가운데 1.07), 본문 42~57px·외치는 글 90~107px·작은 글 20~34px, 포기 5 + 글자를 못 가린 것 7.
/// 문턱을 고치면 하네스 <c>glyph</c> 모드로 실제 쪽의 값을 다시 뽑는다(<c>.claude/rules/lettering-translate.md</c>).</summary>
internal static class SourceGlyph
{
    /// <summary>가장 굵은 띠의 이 비율에 못 미치는 띠는 줄이 아니다(후리가나·점 — 사용자의 Carrot 규칙 A8의 58%).</summary>
    internal const double FuriganaShare = 0.58;
    /// <summary>칸으로 잰 값 ÷ 줄 굵기가 이 범위 안일 때만 믿는다. 실제 55블록의 바른 값은 0.90~1.35, 틀린 읽기·붙은 줄은 0.06~0.56이었다.</summary>
    internal const double AgreeMin = 0.85, AgreeMax = 1.40;
    /// <summary>줄 굵기에서 본 글자 크기의 상한 — 글자를 벌려 놓은 글(P ÷ T = 1.25)에서 칸이 아니라 글자의 크기를 따르게.</summary>
    internal const double FaceOfPitch = 1.12;
    /// <summary>줄 굵기 ÷ 글자 칸(합성 0.865) — 한두 글자뿐이라 칸을 셀 수 없을 때 굵기에서 크기를 낸다.</summary>
    internal const double ThickOfEm = 0.86;
    /// <summary>줄의 잉크 길이가 n칸보다 모자란 양(칸) — 첫·끝 글자의 칸 여백.</summary>
    internal const double EndGap = 0.10;
    /// <summary>줄의 길이가 굵기의 이 배가 안 되면 글자 하나짜리 띠다 — 그런 띠만 있고 블록이 세로로 길면 기둥 하나를 가로로 본 것이다.</summary>
    internal const double SingleGlyph = 1.5;
    /// <summary>획(마스크의 속)과 그 둘레(마스크의 가장자리)의 밝기 차가 이보다 작으면 글자를 가릴 수 없다.</summary>
    internal const int MinDifference = 25;

    public static GlyphStats Measure(Mat img, Mat textMask, Rect block, string? text)
    {
        int cells = Cells(text);
        if (cells == 0) return default;
        var area = block.Intersect(new Rect(0, 0, Math.Min(img.Cols, textMask.Cols), Math.Min(img.Rows, textMask.Rows)));
        if (area.Width < 3 || area.Height < 3) return default;
        if (InkOf(img, textMask, area) is not { } ink) return default;
        int w = area.Width, h = area.Height;

        var cols = new int[w];
        var rows = new int[h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (ink[y * w + x]) { rows[y]++; cols[x]++; }
        var columnLines = Lines(cols);
        var rowLines = Lines(rows);
        if (columnLines.Count == 0 || rowLines.Count == 0) return default;

        // 줄 방향(NarSplit.Find와 같은 생각): 좁은 틈으로만 떨어진 띠를 이은 뒤에도 띠가 둘 넘게 남는 쪽, 둘 다면 더 줄다운 쪽.
        // 한쪽만 그렇더라도 그 띠들이 글자 하나짜리면(기둥 하나의 글자들·가로 한 줄의 글자들) 블록의 긴 쪽을 따른다
        var mc = Merge(columnLines);
        var mr = Merge(rowLines);
        bool vertical;
        if (mc.Count >= 2 && mr.Count >= 2) vertical = LineLikeness(mc) > LineLikeness(mr);
        else if (mc.Count >= 2) vertical = Lengthy(ink, w, h, mc, columns: true) >= SingleGlyph || h >= w;
        else if (mr.Count >= 2) vertical = Lengthy(ink, w, h, mr, columns: false) < SingleGlyph && h >= w;
        else vertical = h >= w;
        var lines = vertical ? columnLines : rowLines;

        double thickness = Median(lines.Select(b => (double)(b.End - b.Start)));
        // 줄마다 잉크가 걸친 길이(줄 방향)와 가장 긴 글자(끊기지 않은 구간)
        int total = 0, longest = 0;
        foreach (var (start, end) in lines)
        {
            int first = -1, last = -1, run = 0;
            int length = vertical ? h : w;
            for (int i = 0; i < length; i++)
            {
                bool filled = false;
                for (int k = start; k < end && !filled; k++) filled = vertical ? ink[i * w + k] : ink[k * w + i];
                if (filled)
                {
                    if (first < 0) first = i;
                    last = i;
                    longest = Math.Max(longest, ++run);
                }
                else run = 0;
            }
            if (first >= 0) total += last - first + 1;
        }
        double pitch = total / Math.Max(0.5, cells - EndGap * lines.Count);

        double em;
        if (cells >= 3)
        {
            double ratio = pitch / thickness;
            // 칸으로 잰 값과 줄 굵기가 어긋나면 포기한다 — 글자 수가 틀렸거나(읽기가 글자를 지어냈다·빠뜨렸다) 줄이 붙었거나 바탕이 잉크로 들어온 것이다
            if (ratio < AgreeMin || ratio > AgreeMax) return default;
            em = Math.Min(pitch, FaceOfPitch * thickness);
        }
        else
            // 한두 글자: 칸을 셀 수 없다("あ…"는 두 칸으로 세지만 점이 길게 늘어선다) — 줄 굵기와 가장 긴 글자(굵기의 1.3배까지) 가운데 큰 쪽에서
            em = Math.Max(thickness, Math.Min(longest, 1.3 * thickness)) / ThickOfEm;
        if (em < 4) return default;

        // 획 굵기·대비는 본문 줄의 잉크에서만(후리가나·줄 밖의 점은 뺀다)
        var inLine = new bool[vertical ? w : h];
        foreach (var (start, end) in lines)
            for (int k = start; k < end; k++) inLine[k] = true;
        var body = new bool[ink.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (ink[y * w + x] && inLine[vertical ? x : y]) body[y * w + x] = true;
        return new GlyphStats(em, StrokeWidth(body, w, h) / em, ThickStroke(body, w, h) / em, Contrast(body, w, h, Math.Max(5, (int)Math.Round(em * 0.3))));
    }

    /// <summary>글이 차지하는 칸 수 — 빈칸은 빼고, 이어진 !?는 둘에 한 칸(세로쓰기는 가로로 묶어 놓는다), 두 자리 수는 한 칸(눕혀 놓는다), 그 밖은 글자마다 한 칸.
    /// 낱말을 가리지 않는 조판 규칙이다. 실제 13쪽: 두 자리 수를 두 칸으로 세면 아홉 칸 글이 열 칸이 되어 칸이 10% 작게 재졌다.</summary>
    internal static int Cells(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        static bool Bang(char c) => c is '!' or '?' or '！' or '？';
        static bool Digit(char c) => c is (>= '0' and <= '9') or (>= '０' and <= '９');
        int cells = 0;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            int j = i + 1;
            if (char.IsWhiteSpace(c)) { i = j; continue; }
            if (Bang(c)) { while (j < text.Length && Bang(text[j])) j++; cells += (j - i + 1) / 2; }
            else if (Digit(c)) { while (j < text.Length && Digit(text[j])) j++; cells += j - i == 2 ? 1 : j - i; }
            else { if (char.IsHighSurrogate(c) && j < text.Length) j++; cells++; }
            i = j;
        }
        return cells;
    }

    /// <summary>블록 안의 글자 픽셀. 감지기의 글 마스크는 획에 3px쯤 불린 것이라, 마스크를 도로 깎은 속(획)과 가장자리(획 바로 밖)의 밝기를 견줘 글자 쪽을 정하고
    /// 마스크 안의 밝기를 둘로 갈라(Otsu) 그쪽을 고른다. 마스크 밖 바탕을 '종이'로 삼지 않는다 — 톤·그림 위의 흰 테 두른 글에서 흰 테가 글자로 읽혔다(실제 26쪽).</summary>
    private static bool[]? InkOf(Mat img, Mat textMask, Rect area)
    {
        using var crop = new Mat(img, area);
        using var gray = crop.Channels() == 1 ? crop.Clone() : crop.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var maskRoi = new Mat(textMask, area);
        using var mask = maskRoi.Clone();
        using var core = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.Erode(mask, core, kernel);
        if (!gray.GetArray(out byte[] g) || !mask.GetArray(out byte[] m) || !core.GetArray(out byte[] c)) return null;

        long coreSum = 0, rimSum = 0;
        int coreCount = 0, rimCount = 0;
        var histogram = new int[256];
        for (int i = 0; i < g.Length; i++)
        {
            if (m[i] == 0) continue;
            histogram[g[i]]++;
            if (c[i] != 0) { coreSum += g[i]; coreCount++; }
            else { rimSum += g[i]; rimCount++; }
        }
        if (coreCount + rimCount < 30 || coreCount < 10 || rimCount < 10) return null;
        double coreMean = (double)coreSum / coreCount, rimMean = (double)rimSum / rimCount;
        if (Math.Abs(coreMean - rimMean) < MinDifference) return null;

        int threshold = Otsu(histogram, coreCount + rimCount);
        bool dark = coreMean < rimMean;
        var ink = new bool[g.Length];
        int count = 0;
        for (int i = 0; i < g.Length; i++)
            if (m[i] != 0 && (dark ? g[i] <= threshold : g[i] > threshold)) { ink[i] = true; count++; }
        return count >= 30 ? ink : null;
    }

    /// <summary>두 무리 사이의 분산이 가장 큰 문턱(Otsu) — 문턱 이하가 어두운 무리.</summary>
    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (int v = 0; v < 256; v++) sum += (double)v * histogram[v];
        double sumBelow = 0, best = -1;
        int below = 0, threshold = 127;
        for (int v = 0; v < 256; v++)
        {
            below += histogram[v];
            if (below == 0) continue;
            int above = total - below;
            if (above == 0) break;
            sumBelow += (double)v * histogram[v];
            double meanBelow = sumBelow / below, meanAbove = (sum - sumBelow) / above;
            double between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best) { best = between; threshold = v; }
        }
        return threshold;
    }

    /// <summary>잉크 수를 센 한 방향의 윤곽에서 본문 줄을 찾는다: 골(빈 자리)로 띠를 가르고 → 띠를 잉크가 있는 데까지 넓혀(글자가 적은 줄은 윤곽이 낮아 가장자리가 문턱 아래다 —
    /// 안 넓히면 두 글자짜리 줄이 가늘게 재져 후리가나로 빠진다) 맞닿은 것은 잇고 → 가장 굵은 띠의 <see cref="FuriganaShare"/>에 못 미치는 띠를 뺀다.</summary>
    private static List<(int Start, int End)> Lines(int[] profile)
    {
        var all = profile.Order().ToArray();
        var filledOnes = all.Where(v => v > 0).ToArray();
        var lines = new List<(int Start, int End)>();
        if (filledOnes.Length == 0) return lines;
        // 빈 자리의 높이(아래 1할)와 글 자리의 높이(글자가 있는 자리들의 위 사분위) 사이 15% — NarSplit.Bands와 같은 문턱. 골이 없으면(빈 자리가 글 자리의 35%를 넘는다) 띠 하나
        double low = all[all.Length / 10], high = filledOnes[filledOnes.Length * 3 / 4];
        double threshold = low < high * 0.35 ? low + (high - low) * 0.15 : -1;
        int start = -1;
        for (int i = 0; i <= profile.Length; i++)
        {
            bool filled = i < profile.Length && profile[i] > threshold;
            if (filled && start < 0) start = i;
            else if (!filled && start >= 0)
            {
                int s = start, e = i;
                while (s > 0 && profile[s - 1] > 0) s--;
                while (e < profile.Length && profile[e] > 0) e++;
                if (lines.Count > 0 && s <= lines[^1].End) lines[^1] = (lines[^1].Start, Math.Max(e, lines[^1].End));
                else lines.Add((s, e));
                start = -1;
                i = Math.Max(i, e - 1);   // 넓힌 데까지는 이 띠다
            }
        }
        int widest = lines.Max(b => b.End - b.Start);
        return lines.Where(b => b.End - b.Start >= FuriganaShare * widest).ToList();
    }

    /// <summary>좁은 틈(굵기 가운뎃값의 ¼ 미만)으로만 떨어진 띠를 잇는다 — 가지런한 글자 칸의 글자 사이 틈이 이렇게 이어진다(줄 사이 틈은 넓어 남는다).</summary>
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

    /// <summary>띠들이 얼마나 줄다운가 — 틈이 넓고 굵기가 고를수록 크다(NarSplit.LineLikeness와 같은 식).</summary>
    private static double LineLikeness(List<(int Start, int End)> bands)
    {
        var thickness = bands.Select(b => (double)(b.End - b.Start)).ToList();
        double median = Median(thickness);
        double spread = Median(thickness.Select(v => Math.Abs(v - median))) / median;
        double gap = Median(Enumerable.Range(0, bands.Count - 1).Select(i => (double)(bands[i + 1].Start - bands[i].End)));
        return gap / median / (1 + 4 * spread);
    }

    /// <summary>띠마다 (줄 방향으로 잉크가 걸친 길이 ÷ 굵기)의 가운뎃값 — 줄이면 굵기보다 훨씬 길고, 글자 하나씩이면 1쯤이다.</summary>
    private static double Lengthy(bool[] ink, int w, int h, List<(int Start, int End)> bands, bool columns)
    {
        var ratios = new List<double>();
        foreach (var (start, end) in bands)
        {
            int first = -1, last = -1, length = columns ? h : w;
            for (int i = 0; i < length; i++)
            {
                bool filled = false;
                for (int k = start; k < end && !filled; k++) filled = columns ? ink[i * w + k] : ink[k * w + i];
                if (!filled) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first >= 0) ratios.Add((last - first + 1) / (double)Math.Max(1, end - start));
        }
        return ratios.Count == 0 ? 0 : Median(ratios);
    }

    /// <summary>획 굵기(px) = 2 × 넓이 ÷ 둘레 — 길이 L·굵기 t인 획은 넓이 L·t, 둘레 2L쯤이다. 둘레 = 이웃(여덟 방향) 가운데 잉크 아닌 것이 있는 잉크 픽셀 수.</summary>
    private static double StrokeWidth(bool[] ink, int w, int h)
    {
        int area = 0, edge = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!ink[y * w + x]) continue;
            area++;
            bool inner = true;
            for (int dy = -1; dy <= 1 && inner; dy++)
            for (int dx = -1; dx <= 1 && inner; dx++)
            {
                int yy = y + dy, xx = x + dx;
                if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;   // 블록 밖은 잉크로 친다(가장자리에서 잘린 획을 둘레로 세지 않는다)
                inner = ink[yy * w + xx];
            }
            if (!inner) edge++;
        }
        return 2.0 * area / Math.Max(1, edge);
    }

    /// <summary>굵은 획의 굵기(px) = 획 한가운데(거리 변환의 능선 — 이웃 여덟보다 얕지 않은 잉크 픽셀)에서 잰 굵기들의 위 1할.
    /// 붓 자국처럼 굵기가 변하는 글꼴(만화 본문의 앤티크 가나)은 고른 굵기(<see cref="StrokeWidth"/>)로는 가는 둥근 고딕과 5%밖에 차이 나지 않았지만 굵은 데끼리 견주면 21% 넘게 갈렸다
    /// (실제 다섯 쪽, 눈으로 가른 가는 글 7·보통 7블록). 능선 픽셀이 여덟 개 안 되면 0.</summary>
    private static double ThickStroke(bool[] ink, int w, int h)
    {
        var bytes = new byte[ink.Length];
        for (int i = 0; i < ink.Length; i++) if (ink[i]) bytes[i] = 255;
        using var mat = new Mat(h, w, MatType.CV_8UC1);
        mat.SetArray(bytes);
        using var depth = new Mat();
        Cv2.DistanceTransform(mat, depth, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        using var peak = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(depth, peak, kernel);
        if (!depth.GetArray(out float[] d) || !peak.GetArray(out float[] p)) return 0;
        var ridge = new List<float>();
        for (int i = 0; i < d.Length; i++)
            if (ink[i] && d[i] >= p[i] - 1e-6f) ridge.Add(d[i]);
        if (ridge.Count < 8) return 0;
        ridge.Sort();
        // 위 1할(90번째 백분위, 사이는 곧게 잇는다)
        double at = 0.9 * (ridge.Count - 1);
        int lo = (int)Math.Floor(at), hi = Math.Min(ridge.Count - 1, lo + 1);
        return 2.0 * (ridge[lo] + (ridge[hi] - ridge[lo]) * (at - lo));
    }

    /// <summary>획 대비 = 세로 획의 굵기 ÷ 가로 획의 굵기. 가로 획 = 가로로 <paramref name="straight"/>px 넘게 이어진 잉크(그 세로 두께를 잰다), 세로 획도 같은 식.
    /// 곧은 획이 모자라면(구간이 여덟 개 안 된다 — 가나 몇 자뿐인 글) 0.</summary>
    private static double Contrast(bool[] ink, int w, int h, int straight)
    {
        var horizontal = new bool[ink.Length];
        var verticalStrokes = new bool[ink.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0, run = 0; x <= w; x++)
            {
                if (x < w && ink[y * w + x]) { run++; continue; }
                if (run >= straight) for (int k = x - run; k < x; k++) horizontal[y * w + k] = true;
                run = 0;
            }
        for (int x = 0; x < w; x++)
            for (int y = 0, run = 0; y <= h; y++)
            {
                if (y < h && ink[y * w + x]) { run++; continue; }
                if (run >= straight) for (int k = y - run; k < y; k++) verticalStrokes[k * w + x] = true;
                run = 0;
            }
        // 가로 획의 두께 = 가로 획 지도를 세로로 훑은 구간의 길이, 세로 획의 두께 = 세로 획 지도를 가로로 훑은 구간의 길이
        long across = 0, down = 0;
        int acrossRuns = 0, downRuns = 0;
        for (int x = 0; x < w; x++)
            for (int y = 0, run = 0; y <= h; y++)
            {
                if (y < h && horizontal[y * w + x]) { run++; continue; }
                if (run > 0) { down += run; downRuns++; }
                run = 0;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0, run = 0; x <= w; x++)
            {
                if (x < w && verticalStrokes[y * w + x]) { run++; continue; }
                if (run > 0) { across += run; acrossRuns++; }
                run = 0;
            }
        if (downRuns < 8 || acrossRuns < 8) return 0;
        return ((double)across / acrossRuns) / ((double)down / downRuns);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
