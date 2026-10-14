using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>회전된 흰 막대(스트립) 검열 검출 — 연결 성분으로는 못 잡는 형태 전용.
///
/// 기존 WhiteBar 경로(연결 성분)는 막대가 **페이지 여백·흰 체액 그림과 이어져 있으면**
/// 구조적으로 못 본다 (실원고: 상단 여백에서 내려오는 회전 막대 2개, 검출 0%).
/// 여기서는 막대의 '직선 테두리'를 단서로 쓴다 — 테두리는 배경 연결성과 무관하게 존재한다.
///
/// 파이프라인 (0.5배 축소 좌표, 실측으로 튜닝):
///  1) Canny → HoughLinesP 선분
///  2) 측면 중앙값 사전필터: 블러(7) 기준 한쪽 ≥224(막대 내부), 반대쪽 ≤215(그림)
///     — 망점 스크린톤 위 1픽셀 샘플은 흰/검이 번갈아 나와 블러 중앙값으로만 안정된다
///  3) collinear 병합 (Hough가 테두리를 토막내므로)
///  4) 테두리에서 흰쪽으로 '런'을 재서 폭을 직접 얻는다 — 반대편 테두리 페어링은
///     상대가 Hough에 안 잡히면 엉뚱한 평행선과 짝이 되어 실패했다 (실측)
///     런 중앙값=폭, 퍼짐(q80-q20 ≤ max(6, 0.22폭))이 작아야 직사각형
///  5) 내부 행 검증 + 양끝 확장(복도 양옆이 그림인 동안) + 축정렬이면 양끝 열림=거터 제외
///  6) 장식 보호: 연쇄 평행 그룹 ≥4 = 줄무늬 패널, 주변 평행 선분 ≥5 = 창문 같은 반복 구조
///  7) 보수 정책: 기울기 6°~84° + 폭 ≥30px + 길이 ≥60px(원본)만 자동 제거 —
///     이 게이트로 테스트 전 페이지(장식 줄무늬·거터·빌딩 창·패널 여백)에서 오탐 0을 확인했다.
///     살짝 기운 여백(5.6°)·가는 장식 띠(≤26px)는 손 도구(브러시/다각형)로 지우는 걸로 남긴다.</summary>
public static class WhiteStripDetector
{
    private const double Scale = 0.5;
    private const int MaxW = 170;          // 축소 좌표 폭 상한 (원본 340)
    private const int MinWOrig = 30;       // 정책: 최소 폭 (원본)
    private const int MinLOrig = 60;       // 정책: 최소 길이 (원본)
    private const double MinOffAxisDeg = 6;

    private sealed record Seg(double X1, double Y1, double X2, double Y2,
        double Len, double AngDeg, int WhiteSide, double Nx, double Ny);

    public readonly record struct Strip(Point2f Center, Size2f Size, float AngleDeg);

    /// <summary>검출된 스트립들을 mask에 흰색으로 채운다. 채운 픽셀 수를 돌려준다.</summary>
    public static int AddToMask(Mat imgBgr, Mat mask, int grow)
    {
        int added = 0;
        foreach (var s in Detect(imgBgr))
        {
            var rr = new RotatedRect(s.Center, new Size2f(s.Size.Width + grow * 2, s.Size.Height + grow * 2), s.AngleDeg);
            var pts = rr.Points().Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            Cv2.FillConvexPoly(mask, pts, Scalar.White);
            added += (int)(s.Size.Width * s.Size.Height);
        }
        return added;
    }

    public static List<Strip> Detect(Mat imgBgr)
    {
        var result = new List<Strip>();
        using var gray0 = new Mat();
        Cv2.CvtColor(imgBgr, gray0, ColorConversionCodes.BGR2GRAY);
        using var g = new Mat();
        Cv2.Resize(gray0, g, new Size(), Scale, Scale, InterpolationFlags.Area);
        int W = g.Cols, H = g.Rows;
        using var edges = new Mat();
        Cv2.Canny(g, edges, 60, 140);
        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 360, 70, 60, 6);
        if (lines.Length == 0) return result;
        using var gs = new Mat();
        Cv2.GaussianBlur(g, gs, new Size(7, 7), 0);

        int Sample(double x, double y)
        {
            int xi = (int)x, yi = (int)y;
            return xi >= 0 && yi >= 0 && xi < W && yi < H ? gs.At<byte>(yi, xi) : -1;
        }

        static double Median(List<int> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

        // 2) 측면 중앙값 사전필터
        var segs = new List<Seg>();
        foreach (var l in lines)
        {
            double x1 = l.P1.X, y1 = l.P1.Y, x2 = l.P2.X, y2 = l.P2.Y;
            double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (len < 1) continue;
            double nx = -(y2 - y1) / len, ny = (x2 - x1) / len;
            // 그림쪽은 5px·8px 두 거리를 본다 — 막대끼리 3~5px 틈으로 붙어 있으면
            // 8px 샘플이 틈을 건너뛰어 이웃 막대(흰색)에 떨어진다 (실측: 가운데 막대 미검출).
            List<int> av = [], bv = [], av5 = [], bv5 = [];
            for (int k = 0; k < 9; k++)
            {
                double t = 0.1 + 0.8 * k / 8.0;
                double px = x1 + (x2 - x1) * t, py = y1 + (y2 - y1) * t;
                int a = Sample(px + nx * 8, py + ny * 8), b = Sample(px - nx * 8, py - ny * 8);
                int a5 = Sample(px + nx * 5, py + ny * 5), b5 = Sample(px - nx * 5, py - ny * 5);
                if (a >= 0) av.Add(a);
                if (b >= 0) bv.Add(b);
                if (a5 >= 0) av5.Add(a5);
                if (b5 >= 0) bv5.Add(b5);
            }
            if (av.Count < 6 || bv.Count < 6) continue;
            // 그림쪽 상한 222: 밝은 회색 벽(~205-215) 위에 선 막대의 테두리도 통과해야 한다
            double amed = Median(av), bmed = Median(bv);
            double aArt = Math.Min(amed, av5.Count >= 6 ? Median(av5) : amed);
            double bArt = Math.Min(bmed, bv5.Count >= 6 ? Median(bv5) : bmed);
            bool okAB = amed >= 227 && bArt <= 222 && amed - bArt >= 12;
            bool okBA = bmed >= 227 && aArt <= 222 && bmed - aArt >= 12;
            if (!okAB && !okBA) continue;
            double ang = (Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI + 180) % 180;
            segs.Add(new Seg(x1, y1, x2, y2, len, ang, okAB ? 1 : -1, nx, ny));
        }
        segs = segs.OrderByDescending(s => s.Len).Take(400).ToList();

        // 3) collinear 병합
        Seg? TryMerge(Seg a, Seg b)
        {
            double d = Math.Abs(a.AngDeg - b.AngDeg); d = Math.Min(d, 180 - d);
            if (d > 2.5) return null;
            double dx = (a.X2 - a.X1) / a.Len, dy = (a.Y2 - a.Y1) / a.Len;
            double nx = -dy, ny = dx;
            double off = Math.Abs((b.X1 + b.X2) / 2 * nx + (b.Y1 + b.Y2) / 2 * ny - (a.X1 * nx + a.Y1 * ny));
            if (off > 4) return null;
            bool sameN = a.Nx * b.Nx + a.Ny * b.Ny >= 0;
            if (a.WhiteSide != (sameN ? b.WhiteSide : -b.WhiteSide)) return null;
            double a0 = Math.Min(a.X1 * dx + a.Y1 * dy, a.X2 * dx + a.Y2 * dy);
            double a1 = Math.Max(a.X1 * dx + a.Y1 * dy, a.X2 * dx + a.Y2 * dy);
            double b0 = Math.Min(b.X1 * dx + b.Y1 * dy, b.X2 * dx + b.Y2 * dy);
            double b1 = Math.Max(b.X1 * dx + b.Y1 * dy, b.X2 * dx + b.Y2 * dy);
            if (Math.Max(a0, b0) - Math.Min(a1, b1) > 50) return null;
            double c = a.X1 * nx + a.Y1 * ny;
            double u0 = Math.Min(a0, b0), u1 = Math.Max(a1, b1);
            return new Seg(u0 * dx + c * nx, u0 * dy + c * ny, u1 * dx + c * nx, u1 * dy + c * ny,
                u1 - u0, a.AngDeg, a.WhiteSide, nx, ny);
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            var outSegs = new List<Seg>();
            var used = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;
                var cur = segs[i];
                for (int j = i + 1; j < segs.Count; j++)
                {
                    if (used[j]) continue;
                    var m = TryMerge(cur, segs[j]);
                    if (m != null) { cur = m; used[j] = true; changed = true; }
                }
                outSegs.Add(cur);
            }
            segs = outSegs;
        }

        // 4~5) 런 측정 → 검증 → 확장
        var cand = new List<(Point2f C, Size2f S, float Ang)>();
        foreach (var s in segs)
        {
            if (s.Len < 36) continue;
            double dx = (s.X2 - s.X1) / s.Len, dy = (s.Y2 - s.Y1) / s.Len;
            double wx = s.Nx * s.WhiteSide, wy = s.Ny * s.WhiteSide;

            var meas = new List<int>();
            for (int k = 0; k < 11; k++)
            {
                double t = 0.08 + 0.84 * k / 10.0;
                double px = s.X1 + (s.X2 - s.X1) * t, py = s.Y1 + (s.Y2 - s.Y1) * t;
                int run = -1;
                for (int dcur = 5; dcur <= MaxW; dcur += 2)
                {
                    int v = Sample(px + wx * dcur, py + wy * dcur);
                    if (v < 0) break;
                    if (v < 218) { run = dcur; break; }
                }
                if (run > 0) meas.Add(run);
            }
            if (meas.Count < 8) continue;
            var sorted = meas.OrderBy(x => x).ToList();
            double med = sorted[sorted.Count / 2];
            if (med < 10 || med > MaxW) continue;
            double q20 = sorted[(int)(0.2 * (sorted.Count - 1))];
            double q80 = sorted[(int)(0.8 * (sorted.Count - 1))];
            if (q80 - q20 > Math.Max(6, 0.22 * med)) continue;
            double sh = med;

            bool okRows = true;
            for (int r = 0; r < 5 && okRows; r++)
            {
                double v = 4 + (sh - 8) * r / 4.0;
                int n = 0, ok = 0;
                for (int k = 0; k < 13; k++)
                {
                    double t = 0.08 + 0.84 * k / 12.0;
                    int z = Sample(s.X1 + (s.X2 - s.X1) * t + wx * v, s.Y1 + (s.Y2 - s.Y1) * t + wy * v);
                    if (z >= 0) { n++; if (z >= 222) ok++; }
                }
                if (n < 4 || ok < n * 0.85) okRows = false;
            }
            if (!okRows) continue;

            bool ColumnOk(double u)
            {
                double px = s.X1 + dx * u, py = s.Y1 + dy * u;
                int n = 0, ok = 0;
                for (int r = 0; r < 5; r++)
                {
                    double v = 4 + (sh - 8) * r / 4.0;
                    int z = Sample(px + wx * v, py + wy * v);
                    if (z >= 0) { n++; if (z >= 222) ok++; }
                }
                if (n < 4 || ok < n * 0.8) return false;
                int o1 = Sample(px - wx * 6, py - wy * 6);
                int o2 = Sample(px + wx * (sh + 6), py + wy * (sh + 6));
                return !(o1 >= 224 && o2 >= 224);   // 양옆 다 흰색 = 여백으로 나감
            }
            double extL = 0, extR = 0;
            while (extL < s.Len * 0.8 && ColumnOk(-(extL + 8))) extL += 8;
            while (extR < s.Len * 0.8 && ColumnOk(s.Len + extR + 8)) extR += 8;
            double nx1 = s.X1 - dx * extL, ny1 = s.Y1 - dy * extL;
            double nx2 = s.X2 + dx * extR, ny2 = s.Y2 + dy * extR;
            double lf = s.Len + extL + extR;

            double angDeg = Math.Atan2(dy, dx) * 180 / Math.PI;
            double angMod = Math.Abs(angDeg) % 90;
            bool axisAligned = angMod < 4 || angMod > 86;
            if (axisAligned)
            {
                int endOpen = 0;
                foreach (var (sgn, ex, ey) in new[] { (-1.0, nx1, ny1), (1.0, nx2, ny2) })
                {
                    var vals = new List<int>();
                    for (int r = 0; r < 5; r++)
                    {
                        double v = 3 + (sh - 6) * r / 4.0;
                        int z = Sample(ex + sgn * dx * 10 + wx * v, ey + sgn * dy * 10 + wy * v);
                        if (z >= 0) vals.Add(z);
                    }
                    if (vals.Count == 0 || Median(vals) >= 224) endOpen++;   // 이미지 밖 = 열림
                }
                if (endOpen == 2) continue;   // 양끝 열림 = 패널 거터/페이지 여백
            }
            if (lf / sh < 1.3) continue;
            cand.Add((new Point2f((float)((nx1 + nx2) / 2 + wx * sh / 2), (float)((ny1 + ny2) / 2 + wy * sh / 2)),
                      new Size2f((float)lf, (float)sh), (float)angDeg));
        }

        // 6a) 연쇄 평행 그룹 ≥4 = 장식 줄무늬 패널
        int nC = cand.Count;
        var parent = Enumerable.Range(0, nC).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        double UOverlap(in (Point2f C, Size2f S, float Ang) r1, in (Point2f C, Size2f S, float Ang) r2)
        {
            double th = r1.Ang * Math.PI / 180, dxu = Math.Cos(th), dyu = Math.Sin(th);
            double u1 = r1.C.X * dxu + r1.C.Y * dyu, u2 = r2.C.X * dxu + r2.C.Y * dyu;
            double lo = Math.Max(u1 - r1.S.Width / 2, u2 - r2.S.Width / 2);
            double hi = Math.Min(u1 + r1.S.Width / 2, u2 + r2.S.Width / 2);
            return (hi - lo) / Math.Max(1, Math.Min(r1.S.Width, r2.S.Width));
        }
        bool Neighbors(in (Point2f C, Size2f S, float Ang) r1, in (Point2f C, Size2f S, float Ang) r2)
        {
            double d = Math.Abs(r1.Ang - r2.Ang); d = Math.Min(d, 180 - d);
            if (d > 4) return false;
            double wmax = Math.Max(r1.S.Height, r2.S.Height);
            double th = r1.Ang * Math.PI / 180;
            double nxu = -Math.Sin(th), nyu = Math.Cos(th);
            double off = Math.Abs((r2.C.X - r1.C.X) * nxu + (r2.C.Y - r1.C.Y) * nyu);
            return off <= wmax * 4 && UOverlap(r1, r2) >= 0.3;
        }
        for (int i = 0; i < nC; i++)
            for (int j = i + 1; j < nC; j++)
                if (Neighbors(cand[i], cand[j])) parent[Find(i)] = Find(j);
        var groupSize = new Dictionary<int, int>();
        for (int i = 0; i < nC; i++) { int r = Find(i); groupSize[r] = groupSize.GetValueOrDefault(r) + 1; }
        cand = cand.Where((_, i) => groupSize[Find(i)] < 4).ToList();

        // 6b) 주변 평행 선분 ≥5 = 창문·장식 같은 반복 구조
        bool Repetitive(in (Point2f C, Size2f S, float Ang) r)
        {
            double th = r.Ang * Math.PI / 180;
            double dxu = Math.Cos(th), dyu = Math.Sin(th);
            double nxu = -Math.Sin(th), nyu = Math.Cos(th);
            int cnt = 0;
            foreach (var s2 in segs)
            {
                double aa = ((r.Ang % 180) + 180) % 180;
                double d = Math.Abs(aa - s2.AngDeg); d = Math.Min(d, 180 - d);
                if (d > 5) continue;
                double mx = (s2.X1 + s2.X2) / 2, my = (s2.Y1 + s2.Y2) / 2;
                double off = (mx - r.C.X) * nxu + (my - r.C.Y) * nyu;
                if (Math.Abs(off) > r.S.Height * 4) continue;
                if (Math.Abs(Math.Abs(off) - r.S.Height / 2) < 5) continue;   // 자기 테두리
                double du = (mx - r.C.X) * dxu + (my - r.C.Y) * dyu;
                if (Math.Abs(du) > r.S.Width / 2 + s2.Len / 2) continue;
                cnt++;
            }
            return cnt >= 5;
        }
        cand = cand.Where(c => !Repetitive(c)).ToList();

        // 7) 보수 정책 + NMS → 원본 좌표
        var policy = cand.Where(c =>
        {
            double angMod = Math.Abs(c.Ang) % 90;
            bool rotated = angMod >= MinOffAxisDeg && angMod <= 90 - MinOffAxisDeg;
            return rotated && c.S.Height * 2 >= MinWOrig && c.S.Width * 2 >= MinLOrig;
        }).OrderByDescending(c => c.S.Width * c.S.Height).ToList();

        var keptMasks = new List<(Mat M, int A)>();
        try
        {
            foreach (var c in policy)
            {
                var rr = new RotatedRect(c.C, c.S, c.Ang);
                var pts = rr.Points().Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
                var m = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
                Cv2.FillConvexPoly(m, pts, Scalar.White);
                int a = Cv2.CountNonZero(m);
                bool overlap = false;
                foreach (var (km, ka) in keptMasks)
                {
                    using var inter = new Mat();
                    Cv2.BitwiseAnd(m, km, inter);
                    if (Cv2.CountNonZero(inter) > 0.4 * Math.Min(a, ka)) { overlap = true; break; }
                }
                if (overlap) { m.Dispose(); continue; }
                keptMasks.Add((m, a));
                result.Add(new Strip(new Point2f((float)(c.C.X / Scale), (float)(c.C.Y / Scale)),
                    new Size2f((float)(c.S.Width / Scale), (float)(c.S.Height / Scale)), c.Ang));
                if (result.Count >= 24) break;
            }
        }
        finally { foreach (var (m, _) in keptMasks) m.Dispose(); }
        return result;
    }
}
