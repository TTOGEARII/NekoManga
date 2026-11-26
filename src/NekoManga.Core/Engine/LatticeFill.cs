using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>되풀이 무늬(줄무늬 톤·줄무늬 풍선·망점·곧은 빗금) 위의 구멍을 **무늬의 격자만큼 옮겨** 메운다 — <see cref="TextureFill"/>이 shift-map보다 먼저 해 본다.
/// 2026-11-27 사용자 "이런 부분은 잘 지워지지도 않았어" · "여기도 마찬가지"(26쪽의 줄무늬 풍선 둘): shift-map은 같은 입력에는 같은 답을 내지만 구멍 모양이 조금만 달라도 답이 크게 바뀌고,
/// 글이 꽉 찬 풍선에서는 풍선 밖의 머리카락·테두리의 호·먹 덩어리를 풍선 안으로 가져왔다(재료를 고르지 않는다). 되풀이 무늬라면 고를 것이 없다:
/// 무늬가 어느 만큼 옮겨야 제 모습과 겹치는지(격자 벡터 둘)를 구멍 둘레에서 재고, 구멍의 픽셀마다 **가장 가까운 격자점의 픽셀**을 그대로 가져온다 — 줄의 굵기·간격·위상이 한 픽셀도 안 틀린다.
/// 하는 일: ① 구멍 둘레 고리에서 으뜸 바탕을 잡는다(둘레 평균 밝기·둘레 표준편차가 고리의 가운뎃값과 닮은 곳 = 재료 후보). 고리의 <see cref="MinRimShare"/> 미만이면 둘레가 한 가지가 아니다 → null.
/// ② 평평하면(표준편차 &lt; <see cref="MinContrast"/>) 무늬가 아니다 → null. ③ 구멍 둘레에서 옮김(±<see cref="ShiftRadius"/>px)마다 '옮긴 것과의 차'를 재서, 보통의 <see cref="GoodShiftRatio"/> 아래인 옮김 가운데
/// 가장 짧은 것과 그것과 나란하지 않은 가장 짧은 것을 격자로 삼는다. 없으면 되풀이가 없다(모래 톤·그림) → null. ④ 재료에서 무늬 아닌 것을 뺀다: 격자만큼 옮겼을 때 달라지는 픽셀(풍선 테두리·칸 테두리의 가장자리).
/// ⑤ 구멍의 픽셀마다 가까운 격자점부터 보아 재료인 첫 자리를 가져온다. <see cref="MaxReachPx"/> 안에 없으면 null.
/// null이면 호출부가 shift-map으로 간다(평평한 바탕을 지나는 가는 선, 둘 이상의 바탕에 걸친 구멍은 그쪽이 낫다).
/// 실측(실제 세 쪽의 무늬 메우기 창 12개): 격자로 간 창 셋 — 줄무늬 풍선 둘(격자 (1,0)·(0,11), 0.4~0.5초 — shift-map 7초)과 살 위의 옅은 망점((4,-4)·(-4,-4), 2.3초 — shift-map 11초). 나머지 아홉은 null(흰 바탕 넷·둘레가 섞인 것 다섯).
/// 알고 둔 것: 구멍을 지나가던 가는 선(몸의 윤곽선)은 잇지 못한다 — 무늬만 깔린다. 얼룩진 메움보다 끊긴 선이 덜 눈에 띈다고 보고 무늬를 고른다.</summary>
internal static class LatticeFill
{
    /// <summary>구멍 둘레 고리 = 구멍 밖 <see cref="RimInnerPx"/>~<see cref="RimOuterPx"/>px. 안쪽을 띄우는 것은 획 가장자리의 흐린 픽셀 때문.</summary>
    internal const int RimInnerPx = 3, RimOuterPx = 12;
    /// <summary>둘레 평균·표준편차를 재는 상자(px) — 줄무늬 간격(실측 11px)보다 커야 줄의 위상에 따라 값이 출렁이지 않는다.</summary>
    internal const int BoxPx = 15;
    /// <summary>재료 후보: 둘레 평균 밝기가 고리의 가운뎃값에서 이 안, 둘레 표준편차가 가운뎃값의 ±(이 비율, 최소 <see cref="StdTolMin"/>) 안. 상자의 재료 비율이 <see cref="MinSupport"/>보다 작은 자리는 재지 않는다.
    /// 실측(줄무늬 풍선): 줄무늬 215 / 풍선 테두리 근처 170 / 칸 사이 흰 여백 255 / 풍선 밖 톤 128.</summary>
    internal const double MeanTol = 28, StdTolRatio = 0.35, StdTolMin = 12, MinSupport = 0.3;
    /// <summary>고리에서 재료 후보가 이 비율은 돼야 '한 가지 무늬 위의 구멍'이다. 실측: 줄무늬 풍선 0.75·0.76, 옅은 망점 0.66 / 섞인 둘레 0.21~0.50.</summary>
    internal const double MinRimShare = 0.6;
    /// <summary>둘레 표준편차의 가운뎃값이 이보다 작으면 평평한 바탕이다. 실측: 흰 바탕 0, 줄무늬 39, 옅은 망점 37.</summary>
    internal const double MinContrast = 12;
    /// <summary>격자를 찾는 옮김의 범위(±px)와, 한 옮김을 재는 데 필요한 겹친 픽셀 수. 자기상관은 구멍 상자 ±<see cref="ProbeMarginPx"/>에서만 잰다.</summary>
    internal const int ShiftRadius = 16, MinOverlapPx = 200, ProbeMarginPx = 48;
    /// <summary>옮긴 것과의 평균 차가 '보통의 옮김'(가운뎃값)의 이 비율 아래면 무늬가 겹치는 옮김이다. 실측: 줄무늬 풍선 보통 48, 겹치는 옮김 0~5.</summary>
    internal const double GoodShiftRatio = 0.3;
    /// <summary>무늬 아닌 것 거르기: 격자 벡터를 이 길이 이상으로 늘려 옮겼을 때 이만큼 넘게 달라지는 픽셀(3×3 평균)은 재료에서 뺀다 — 가는 테두리 선은 6px 옮기면 벗어난다.</summary>
    internal const int MemberStepPx = 6;
    internal const double MemberTol = 48;
    /// <summary>재료를 가장자리에서 깎는 폭(px) — 우연히 값이 맞은 가는 띠(칸 테두리와 흰 여백 사이)를 턴다 · 격자점을 찾는 최대 거리(px).</summary>
    internal const int SourceErodePx = 2, MaxReachPx = 170;
    /// <summary>격자 다듬기: 벡터를 이 배수까지(2·4·8) 늘려 가며 고친다 · 늘린 옮김이 이 거리(px)를 넘으면 그만둔다(겹쳐 잴 픽셀이 모자란다).</summary>
    internal const int RefineSteps = 8, RefineReachPx = 64;
    /// <summary>격자 다듬기에서 받아들이는 차(보통의 옮김에 견준 비율) — 격자를 찾을 때(<see cref="GoodShiftRatio"/>)보다 느슨하다: 간격이 4.2px이면 2배(8.4px)는 어느 정수로도 0.4px 어긋나
    /// 차가 크지만, 거기서 멈추면 4배(16.8 → 17)에서 고칠 기회를 놓친다.</summary>
    internal const double RefineRatio = 0.7;
    /// <summary>격자 다듬기에서 예상한 자리를 버리고 옆 자리로 옮기려면 차가 이만큼(보통의 옮김에 견준 비율)은 작아야 한다. 실측: 줄무늬의 가로 방향은 이웃끼리 0.3/48 = 0.006 차이, 간격 4.2px 망점의 4배 자리는 0.3 넘게 차이.</summary>
    internal const double RefineGain = 0.05;

    /// <summary><paramref name="patch"/>(BGR 창)에서 <paramref name="hole"/>(255 = 메울 자리)을 메운 그림을 돌려준다(구멍 밖은 그대로) — 되풀이 무늬 위가 아니면 null.
    /// <paramref name="usable"/>: 255 = 재료로 써도 되는 자리(구멍·글자 밖).</summary>
    internal static Mat? TryFill(Mat patch, Mat usable, Mat hole) => TryFill(patch, usable, hole, out _);

    /// <summary><see cref="TryFill(Mat, Mat, Mat)"/> + 진단 글(<paramref name="note"/>): 찾은 격자 벡터(다듬은 값)나 그만둔 까닭 — NEKO_TEXFILL_DBG가 창마다 적는다.</summary>
    internal static Mat? TryFill(Mat patch, Mat usable, Mat hole, out string note)
    {
        note = "입력";   // loc:none
        if (patch.Type() != MatType.CV_8UC3 || usable.Size() != patch.Size() || hole.Size() != patch.Size()) return null;
        int w = patch.Cols, h = patch.Rows, n = w * h;
        if (Cv2.CountNonZero(hole) == 0 || Cv2.CountNonZero(usable) == 0) return null;

        // 둘레 평균·표준편차 — 재료인 픽셀만으로(글자·구멍이 값을 끌어내리지 않게 무게를 준 평균)
        using var gray = patch.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var g = new Mat(); gray.ConvertTo(g, MatType.CV_32F);
        using var weight = new Mat(); usable.ConvertTo(weight, MatType.CV_32F, 1.0 / 255);
        var box = new Size(BoxPx, BoxPx);
        using var support = new Mat(); Cv2.Blur(weight, support, box);
        using var gw = new Mat(); Cv2.Multiply(g, weight, gw);
        using var mean = new Mat(); Cv2.Blur(gw, mean, box);
        using var sq = new Mat(); Cv2.Multiply(gw, g, sq); Cv2.Blur(sq, sq, box);
        using var rimMat = new Mat();
        using (var outer = new Mat())
        using (var kOuter = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * RimOuterPx + 1, 2 * RimOuterPx + 1)))
        using (var kInner = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * RimInnerPx + 1, 2 * RimInnerPx + 1)))
        {
            Cv2.Dilate(hole, outer, kOuter);
            Cv2.Dilate(hole, rimMat, kInner);
            Cv2.Subtract(outer, rimMat, rimMat);
        }
        float[] G = Floats(g), Sup = Floats(support), Mean = Floats(mean), Sq = Floats(sq);
        byte[] U = Bytes(usable), H = Bytes(hole), Rim = Bytes(rimMat);
        var sd = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (Sup[i] <= MinSupport) continue;
            Mean[i] /= Sup[i];
            sd[i] = MathF.Sqrt(MathF.Max(Sq[i] / Sup[i] - Mean[i] * Mean[i], 0));
        }

        // ① 으뜸 바탕: 고리의 가운뎃값과 닮은 곳
        var rimMeans = new List<float>();
        for (int i = 0; i < n; i++)
            if (Rim[i] != 0 && U[i] != 0 && Sup[i] > MinSupport) rimMeans.Add(Mean[i]);
        note = "고리가 작다";   // loc:none
        if (rimMeans.Count < 50) return null;
        float m = Median(rimMeans);
        var cons = new bool[n];
        var rimStds = new List<float>();
        for (int i = 0; i < n; i++)
        {
            if (U[i] == 0 || Sup[i] <= MinSupport || MathF.Abs(Mean[i] - m) > MeanTol) continue;
            cons[i] = true;
            if (Rim[i] != 0) rimStds.Add(sd[i]);
        }
        if (rimStds.Count < 50) return null;
        float sdm = Median(rimStds);
        note = $"평평하다(둘레 표준편차 {sdm:0})";   // loc:none
        if (sdm < MinContrast) return null;   // ② 평평한 바탕
        float sdTol = MathF.Max((float)StdTolMin, (float)StdTolRatio * sdm);
        int rimCons = 0;
        for (int i = 0; i < n; i++)
        {
            if (!cons[i]) continue;
            if (MathF.Abs(sd[i] - sdm) > sdTol) cons[i] = false;
            else if (Rim[i] != 0) rimCons++;
        }
        note = $"둘레가 섞였다(으뜸 바탕 {rimCons / (double)rimMeans.Count:0.00})";   // loc:none
        if (rimCons < MinRimShare * rimMeans.Count) return null;
        note = "되풀이가 없다";   // loc:none

        // ③ 격자: 옮긴 것과의 평균 차(반평면만 재고 맞은편에 같은 값을 넣는다)
        using var holePoints = new Mat();
        Cv2.FindNonZero(hole, holePoints);
        var hb = Cv2.BoundingRect(holePoints);
        var probe = new Rect(hb.X - ProbeMarginPx, hb.Y - ProbeMarginPx, hb.Width + 2 * ProbeMarginPx, hb.Height + 2 * ProbeMarginPx) & new Rect(0, 0, w, h);
        // 한 옮김의 '옮긴 것과의 평균 차'(둘 다 재료인 픽셀만) — 겹친 픽셀이 모자라면 NaN. (dx,dy)와 (-dx,-dy)는 같은 값이다
        double ShiftError(int dx, int dy)
        {
            if (dy < 0 || (dy == 0 && dx < 0)) (dx, dy) = (-dx, -dy);
            int y0 = probe.Top, y1 = Math.Min(probe.Bottom, h - dy);
            int x0 = Math.Max(probe.Left, -dx), x1 = Math.Min(probe.Right, w - dx);
            double sum = 0; int count = 0;
            for (int y = y0; y < y1; y++)
            {
                int a = y * w, b = (y + dy) * w + dx;
                for (int x = x0; x < x1; x++)
                    if (cons[a + x] && cons[b + x]) { sum += MathF.Abs(G[a + x] - G[b + x]); count++; }
            }
            return count < MinOverlapPx ? double.NaN : sum / count;
        }
        int R = ShiftRadius, side = 2 * R + 1;
        var err = new double[side * side];
        Array.Fill(err, double.NaN);
        for (int dy = 0; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                if (dy == 0 && dx <= 0) continue;
                err[(dy + R) * side + dx + R] = err[(-dy + R) * side + (-dx + R)] = ShiftError(dx, dy);
            }
        var measured = err.Where(e => !double.IsNaN(e)).OrderBy(e => e).ToList();
        if (measured.Count == 0) return null;
        double typical = measured[measured.Count / 2];
        if (typical <= 0) return null;
        var good = new List<(int Dx, int Dy, double Err)>();
        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                double e = err[(dy + R) * side + dx + R];
                if ((dx != 0 || dy != 0) && !double.IsNaN(e) && e <= GoodShiftRatio * typical) good.Add((dx, dy, e));
            }
        if (good.Count == 0) return null;
        good.Sort((p, q) =>
        {
            int c = (p.Dx * p.Dx + p.Dy * p.Dy).CompareTo(q.Dx * q.Dx + q.Dy * q.Dy);
            if (c != 0) return c;
            c = p.Err.CompareTo(q.Err);
            if (c != 0) return c;
            c = p.Dy.CompareTo(q.Dy);   // 같은 길이·같은 차면 늘 같은 것을 고른다(결과가 실행마다 같게)
            return c != 0 ? c : p.Dx.CompareTo(q.Dx);
        });
        var v1 = good[0];
        int second = good.FindIndex(c => v1.Dx * c.Dy - v1.Dy * c.Dx != 0);
        if (second < 0) return null;
        var v2 = good[second];
        // 격자 한 칸의 넓이가 1이면(두 벡터가 한 칸씩 — 어느 쪽으로 한두 픽셀 옮겨도 겹친다) 되풀이가 아니라 매끈한 명암이다: 격자로 삼으면 가장 가까운 픽셀 베끼기가 되고
        // 격자점이 9만 개라 큰 구멍에서는 한참 돈다(검토 2026-11-27). 줄무늬는 (1,0)·(0,간격)이라 넓이가 간격이다
        note = "되풀이가 없다(매끈한 명암)";   // loc:none
        if (Math.Abs(v1.Dx * v2.Dy - v1.Dy * v2.Dx) <= 1) return null;
        // 격자 다듬기: 인쇄 망점의 간격은 정수가 아니다(4px로 잰 것이 실제로는 4.2px) — 정수 벡터를 그대로 곱해 가면 몇 칸 뒤에는 점이 어긋나 메운 자리 한가운데에 물결무늬가 생긴다(실제 13쪽).
        // 2·4·8배 옮긴 자리 둘레 ±1px에서 가장 잘 겹치는 옮김을 찾아 벡터를 소수로 고친다. **예상한 자리보다 뚜렷이(RefineGain) 나을 때만** 옮긴다:
        // 줄무늬의 가로 방향처럼 어디로 옮겨도 겹치는 무늬는 짧게 옮길수록 차가 조금씩 작아서, 조금이라도 나은 쪽을 따라가면 벡터가 (-1,0) → (-0.13,0)으로 쪼그라들고
        // 가로로 22px 너머의 재료에 닿지 못해 세로 배수로 메우게 된다(실제 26쪽의 줄무늬 풍선에서 줄이 군데군데 겹쳐 굵어졌다)
        (double X, double Y) Refine((int Dx, int Dy, double Err) v)
        {
            double vx = v.Dx, vy = v.Dy;
            for (int k = 2; k <= RefineSteps; k *= 2)
            {
                int px = (int)Math.Round(vx * k), py = (int)Math.Round(vy * k);
                if (Math.Max(Math.Abs(px), Math.Abs(py)) > RefineReachPx) break;
                double best = ShiftError(px, py);
                int bx = px, by = py;
                if (double.IsNaN(best)) break;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        double e = ShiftError(px + ox, py + oy);
                        if (!double.IsNaN(e) && e < best - RefineGain * typical) (best, bx, by) = (e, px + ox, py + oy);
                    }
                if (best > RefineRatio * typical) break;   // 여기서부터는 무늬가 겹치지 않는다 — 더 멀리는 고치지 않는다
                (vx, vy) = (bx / (double)k, by / (double)k);
            }
            return (vx, vy);
        }
        var (r1x, r1y) = Refine(v1);
        var (r2x, r2y) = Refine(v2);
        note = $"격자 ({v1.Dx},{v1.Dy})→({r1x:0.##},{r1y:0.##}) ({v2.Dx},{v2.Dy})→({r2x:0.##},{r2y:0.##}) 차 {v1.Err:0.0}·{v2.Err:0.0}/보통 {typical:0.0}";   // loc:none

        // ④ 재료에서 무늬 아닌 것을 뺀다: 격자만큼 옮겼을 때 달라지는 픽셀
        var steps = new (int Dx, int Dy)[4];
        (steps[0], steps[2]) = (Stretch(v1.Dx, v1.Dy), Stretch(v2.Dx, v2.Dy));
        (steps[1], steps[3]) = ((-steps[0].Dx, -steps[0].Dy), (-steps[2].Dx, -steps[2].Dy));
        var change = new float[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!cons[i]) continue;
                float worst = 0;
                foreach (var (sx, sy) in steps)
                {
                    int x2 = x + sx, y2 = y + sy;
                    if (x2 < 0 || y2 < 0 || x2 >= w || y2 >= h) continue;
                    int j = y2 * w + x2;
                    if (cons[j]) worst = MathF.Max(worst, MathF.Abs(G[i] - G[j]));   // 옮긴 자리가 재료가 아니면 견주지 않는다
                }
                change[i] = worst;
            }
        using var changeMat = new Mat(h, w, MatType.CV_32F);
        changeMat.SetArray(change);
        Cv2.Blur(changeMat, changeMat, new Size(3, 3));
        float[] smooth = Floats(changeMat);
        var srcBytes = new byte[n];
        for (int i = 0; i < n; i++) if (cons[i] && smooth[i] <= MemberTol) srcBytes[i] = 255;
        using var srcMat = new Mat(h, w, MatType.CV_8UC1);
        srcMat.SetArray(srcBytes);
        using (var kErode = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * SourceErodePx + 1, 2 * SourceErodePx + 1)))
            Cv2.Erode(srcMat, srcMat, kErode, borderType: BorderTypes.Constant, borderValue: Scalar.White);   // 그림 가장자리는 깎지 않는다
        byte[] S = Bytes(srcMat);

        // ⑤ 격자점(가까운 것부터)을 돌며 재료인 첫 자리를 가져온다
        var seen = new HashSet<(int Dx, int Dy)>();
        int reach2 = MaxReachPx * MaxReachPx;
        for (int k1 = -MaxReachPx; k1 <= MaxReachPx; k1++)
            for (int k2 = -MaxReachPx; k2 <= MaxReachPx; k2++)
            {
                int dx = (int)Math.Round(k1 * r1x + k2 * r2x), dy = (int)Math.Round(k1 * r1y + k2 * r2y);
                if ((dx != 0 || dy != 0) && dx * dx + dy * dy <= reach2) seen.Add((dx, dy));
            }
        var points = seen.ToList();
        points.Sort((p, q) =>
        {
            int c = (p.Dx * p.Dx + p.Dy * p.Dy).CompareTo(q.Dx * q.Dx + q.Dy * q.Dy);
            if (c != 0) return c;
            c = p.Dy.CompareTo(q.Dy);
            return c != 0 ? c : p.Dx.CompareTo(q.Dx);
        });
        using var continuous = patch.IsContinuous() ? null : patch.Clone();
        (continuous ?? patch).GetArray(out Vec3b[] pixels);
        var filled = (Vec3b[])pixels.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (H[i] == 0) continue;
                bool found = false;
                foreach (var (dx, dy) in points)
                {
                    int x2 = x + dx, y2 = y + dy;
                    if (x2 < 0 || y2 < 0 || x2 >= w || y2 >= h) continue;
                    int j = y2 * w + x2;
                    if (S[j] == 0) continue;
                    filled[i] = pixels[j];
                    found = true;
                    break;
                }
                if (!found) { note += " — 닿는 거리 안에 재료가 없다"; return null; }   // loc:none
            }
        var result = new Mat(h, w, MatType.CV_8UC3);
        result.SetArray(filled);
        return result;
    }

    /// <summary>격자 벡터를 <see cref="MemberStepPx"/> 이상이 되는 가장 작은 배수로 늘린다.</summary>
    private static (int Dx, int Dy) Stretch(int dx, int dy)
    {
        int k = 1;
        while ((dx * k) * (dx * k) + (dy * k) * (dy * k) < MemberStepPx * MemberStepPx) k++;
        return (dx * k, dy * k);
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static float[] Floats(Mat m)
    {
        using var c = m.IsContinuous() ? null : m.Clone();
        (c ?? m).GetArray(out float[] data);
        return data;
    }

    private static byte[] Bytes(Mat m)
    {
        using var c = m.IsContinuous() ? null : m.Clone();
        (c ?? m).GetArray(out byte[] data);
        return data;
    }
}
