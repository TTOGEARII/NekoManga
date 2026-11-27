// 이 파일은 OpenCV(opencv_contrib)의 xphoto 모듈 — inpainting.cpp(shiftMapInpaint)·annf.hpp·whs.hpp·photomontage.hpp, 그리고 imgproc의 gcgraph.hpp — 를 C#으로 옮긴 것이다.
// 원 소스의 고지(3-clause BSD License)를 그대로 싣는다:
//
//                           License Agreement
//                For Open Source Computer Vision Library
//                       (3-clause BSD License)
//
// Copyright (C) 2000-2019, Intel Corporation, all rights reserved.
// Copyright (C) 2009-2011, Willow Garage Inc., all rights reserved.
// Copyright (C) 2009-2016, NVIDIA Corporation, all rights reserved.
// Copyright (C) 2010-2013, Advanced Micro Devices, Inc., all rights reserved.
// Copyright (C) 2015-2016, OpenCV Foundation, all rights reserved.
// Copyright (C) 2015-2016, Itseez Inc., all rights reserved.
// Third party copyrights are property of their respective owners.
//
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//
//   * Redistribution's of source code must retain the above copyright notice,
//     this list of conditions and the following disclaimer.
//
//   * Redistribution's in binary form must reproduce the above copyright notice,
//     this list of conditions and the following disclaimer in the documentation
//     and/or other materials provided with the distribution.
//
//   * Neither the names of the copyright holders nor the names of the contributors
//     may be used to endorse or promote products derived from this software
//     without specific prior written permission.
//
// This software is provided by the copyright holders and contributors "as is" and
// any express or implied warranties, including, but not limited to, the implied
// warranties of merchantability and fitness for a particular purpose are disclaimed.
// In no event shall the Intel Corporation or contributors be liable for any direct,
// indirect, incidental, special, exemplary, or consequential damages
// (including, but not limited to, procurement of substitute goods or services;
// loss of use, data, or profits; or business interruption) however caused
// and on any theory of liability, whether in contract, strict liability,
// or tort (including negligence or otherwise) arising in any way out of
// the use of this software, even if advised of the possibility of such damage.

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>shift-map 인페인팅 — 구멍의 픽셀마다 **어디서 옮겨 올지**(옮김 하나)를 골라 그 자리의 픽셀을 그대로 가져온다. 무늬를 그리지 않고 베끼므로 톤의 주기·선의 굵기가 유지된다
/// (쓰는 자리와 고른 까닭은 <see cref="TextureFill"/>). 차례: ① 창의 모든 8×8 조각에서 '가장 닮은 다른 조각까지의 옮김'을 찾아(<see cref="DominantTransforms"/>) 가장 자주 나온 옮김 60개를 고르고
/// ② 구멍(+ 둘레 2px)의 픽셀마다 그 60개와 '제자리' 가운데 하나를, 이웃한 두 픽셀의 이음매가 가장 덜 어긋나게 그래프 컷으로 고른다(α-확장 — <see cref="Measure"/>).
///
/// **OpenCV xphoto의 shift-map을 옮겨 와, 하는 일은 그대로 두고 헛도는 곳을 걷어 냈다**(2026-11-28 사용자 "전체적으로 속도 최적화"). 실제 한 화(40쪽)의 번역 단계 263초 가운데 194초가 xphoto shift-map이었다.
/// · ①의 KD 나무: 재 보니 구멍이 500px뿐이어도 629×929 창은 14.2초(통째로 21.2초), 583×824는 10.4초, 304×682는 1.9초 — 구멍과 상관없이 창 넓이의 제곱에 가깝다.
///   `annf.hpp`가 나무를 가를 때마다 색인 배열 전체를 복사한다(`std::vector&lt;int&gt; _idx(idx.begin(), idx.end()); … idx = _idx;` — O(N²/64)). 게다가 비교기가 `data[idx[x]]`를 봐서
///   (x가 이미 점 번호인데 한 번 더 색인한다) 첫 갈래 말고는 닮은 조각끼리 모이지도 않는다. 여기서는 나무를 제자리에서 제대로 짓고(O(N log N), 닮은 조각이 한 잎에 든다),
///   픽셀마다 짝을 찾는 훑기는 대각선으로 놓인 토막끼리 나란히 한다(차례대로 훑은 것과 결과가 같다).
/// · ②의 차례는 xphoto 그대로다: 한 판에 예순한 가지 옮김으로의 확장을 다 재서 **가장 나은 하나만** 받고, 그것이 2% 넘게 나아지지 않으면 끝. 이음매의 값도 그대로다(셋째 변을 한 픽셀로만 세는 별난 식까지).
///   다만 재는 법이 다르다: ⓐ 그 옮김을 못 쓰는 점은 그래프에 넣지 않고 원천에 붙은 것으로 센다(값이 같다) ⓑ 서로 닿지 않는 덩어리(글자·획 묶음 — 띠의 점이 사이를 막는다)마다 따로 재서 더한다
///   ⓒ 받은 옮김이 건드리지 않은 덩어리는 다음 판에 다시 재지 않는다(그 덩어리의 답은 그대로다). xphoto는 판마다 구멍 전체의 그래프를 예순한 번 새로 짓는다(판이 5~18번 — 그래프 컷 300~1,100번).
///   ⓓ 절단에서 어느 쪽도 아닌 점(양쪽 다 막힌 점)은 옮김을 지킨다 — xphoto는 탐색이 지나간 흔적에 따라 갈렸다(그래프를 어떻게 짓느냐에 따라 달라지는 값이라 덩어리로 나누면 따라갈 수 없다).
///   **해 보고 버린 것**(실제 쪽의 조각을 나란히 보고): 옮김을 차례로 돌며 나아지면 바로 받기(교과서의 α-확장) — 이음매의 값은 더 낮아지는데 그림은 나빠졌다(28쪽의 빗금 위 글에 어두운 톤 덩어리가 통째로 들어왔다).
///   덩어리마다 제 으뜸 옮김을 따로 받기 — 빠르지만 옮김의 가짓수가 늘어 엉뚱한 조각이 들어왔다(05쪽 속도선에 옆 풍선의 테두리, 16쪽 먹칠에 흰 세모).
///   이음매의 값은 픽셀끼리의 색 차뿐이라 '다른 무늬'를 벌하지 못한다 — 구멍 전체에 통하는 옮김 몇 가지만 쓰는 xphoto의 차례가 그것을 막아 주고 있었다.
/// 그 밖에 다른 것: 800×600보다 큰 창을 줄였다 늘리지 않는다(xphoto는 1.05배 같은 어중간한 비율로 줄인 뒤 늘리는 길을 탄다) · 무게를 정수로 센다(원본은 float — 합이 1,600만을 넘으면 뭉개진다) ·
/// 가져올 데가 하나도 없는 구멍 픽셀은 검게 남기지 않는다(몇 개면 옆 픽셀로 채우고, 많으면 null — 호출부가 LaMa로 넘긴다) · 메울 자리가 없는 마스크에 죽지 않는다(xphoto는 프로세스째 죽었다).
/// 결과는 xphoto와 픽셀까지 같지는 않다(닮은 조각을 찾는 나무와 ⓓ가 다르다). 같은 입력에는 늘 같은 결과고, 몇 스레드로 돌든 같다.</summary>
internal static class ShiftMap
{
    /// <summary>고르는 옮김의 수 · 조각 한 변(px) · KD 나무의 잎 하나에 드는 조각 수 · 이 안(px)의 옮김은 세지 않는다(제 옆의 조각은 늘 닮았다) · 조각 하나의 특징 수(채널마다 왈시-아다마르 16·4·4).
    /// 전부 xphoto의 값이다(nTransform·psize·leafNum·zeroThresh).</summary>
    internal const int MaxTransforms = 60, PatchSize = 8, LeafSize = 64, ZeroThresh = 2 * PatchSize, Features = 24;
    private static readonly int[] ChannelFeatures = [16, 4, 4];
    /// <summary>한 판에서 가장 나은 옮김이 지금의 값을 이 비율 넘게 줄여야 받는다 — 아니면 끝(xphoto의 eps).</summary>
    internal const double Eps = 0.02;
    /// <summary>닮은 조각 찾기(<see cref="DominantTransforms"/>)를 나란히 훑을 때 한 토막의 칸 수 — 토막 하나가 0.2ms쯤이라 나눠 주는 값이 묻힌다(창 폭 400~900px이면 한 번에 7~14토막이 나란히 돈다).</summary>
    internal const int ScanChunk = 64;
    /// <summary>가져올 데가 하나도 없는 구멍 픽셀(예순 가지 옮김이 전부 구멍·다른 글자·그림 밖에 떨어진다)이 이만큼(개, 또는 구멍의 1/<see cref="StrandedShare"/>)까지면 바로 옆의 메운 픽셀로 채운다 — 넘으면 못 한 것으로 친다.
    /// 실제 26쪽: 12만 px짜리 글 하나에서 그런 픽셀이 둘이었다(xphoto는 그 둘을 검게 남겼다). 그 둘 때문에 글 전체를 LaMa로 넘기면 무늬가 통째로 뭉개진다.</summary>
    internal const int StrandedFew = 16, StrandedShare = 100;

    /// <summary><paramref name="patch"/>(8UC3)에서 <paramref name="material"/>(8UC1, 0 = 메울 자리, 그 밖 = 재료)의 구멍을 메운 새 그림을 돌려준다(호출자가 해제).
    /// 구멍에서 2px 안의 둘레는 이음매를 맞추느라 바뀔 수 있다(옮김을 고를 수 있는 자리다 — 호출부 <see cref="TextureFill"/>는 구멍의 픽셀만 가져다 쓴다). 그 밖은 그대로다.
    /// 못 하면 null: 형이 다르다 · 한 변이 32,767px을 넘는다 · 재료가 없다 · 가져올 데가 없는 구멍 픽셀이 많다. 메울 자리가 없으면 그림 그대로의 사본. 취소는 던진다.
    /// <paramref name="trace"/>: 진단 — 단계별로 걸린 시간과 크기를 한 줄로 받는다(NEKO_TEXFILL_DBG의 pieces.txt).</summary>
    internal static Mat? Inpaint(Mat patch, Mat material, CancellationToken ct = default, Action<string>? trace = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (patch.Empty() || patch.Type() != MatType.CV_8UC3 || material.Type() != MatType.CV_8UC1 || patch.Size() != material.Size()) return null;
        int w = patch.Cols, h = patch.Rows, n = w * h;
        if (w > short.MaxValue || h > short.MaxValue) return null;   // 조각의 자리를 16비트로 든다(DominantTransforms) — 넘치면 엉뚱한 짝을 고른다. 창은 블록 하나 + 둘레라 수백 px이다
        byte[] src = Bytes(patch, 3), mask = Bytes(material, 1);
        // 구멍의 픽셀은 0으로 — 닮은 조각을 찾을 때도, 옮겨 올 때도 구멍 안의 것은 쓰지 않는다
        var img = (byte[])src.Clone();
        int holes = 0;
        for (int p = 0; p < n; p++)
            if (mask[p] == 0) { holes++; img[3 * p] = img[3 * p + 1] = img[3 * p + 2] = 0; }
        if (holes == 0) return patch.Clone();
        if (holes == n) return null;

        // 고를 자리 = 구멍을 4px 넓힌 것. 구멍에서 2px 안(near)은 옮김을 고를 수 있고, 그 밖의 2px 띠는 제자리만 — 띠가 둘레의 그림과 이음매를 맞대는 경계 조건이다
        byte[] near, band;
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
        using (var d = new Mat())
        using (var dd = new Mat())
        {
            Cv2.Erode(material, d, kernel, iterations: 2);
            Cv2.Erode(d, dd, kernel, iterations: 2);
            near = Bytes(d, 1);
            band = Bytes(dd, 1);
        }
        var backref = new int[n];
        int count = 0;
        for (int p = 0; p < n; p++) backref[p] = band[p] == 0 ? count++ : -1;
        var px = new int[count];
        var py = new int[count];
        for (int p = 0; p < n; p++)
            if (backref[p] >= 0) { px[backref[p]] = p % w; py[backref[p]] = p / w; }

        if ((long)count * (MaxTransforms + 1) * 3 > int.MaxValue) return null;   // 점마다 옮김마다 세 바이트 — 배열 하나에 안 든다(짝 찾기를 하기 전에 본다)
        long prepared = clock.ElapsedMilliseconds;
        string searchDetail = "";
        var shifts = DominantTransforms(img, w, h, ct, trace == null ? null : note => searchDetail = " (" + note + ")");
        shifts.Add((0, 0));   // 마지막은 '제자리'
        long searched = clock.ElapsedMilliseconds;
        int labels = shifts.Count, identity = labels - 1;

        // 점마다: 옮김별로 가져올 픽셀(vals), 그 옮김을 쓸 수 있는가(valid — 가져올 자리가 그림 안의 재료일 때. 띠의 점은 제자리만), 처음 고른 옮김(쓸 수 있는 것 가운데 마지막 — 원본과 같다)
        var vals = new byte[count * labels * 3];
        var valid = new ulong[count];
        var label = new int[count];
        var free = new bool[count];
        int[] up = new int[count], down = new int[count], left = new int[count], right = new int[count];
        for (int i = 0; i < count; i++)
        {
            int x = px[i], y = py[i];
            free[i] = near[y * w + x] == 0;
            for (int j = 0; j < labels; j++)
            {
                int ux = x + shifts[j].Dx, uy = y + shifts[j].Dy;
                if (ux < 0 || ux >= w || uy < 0 || uy >= h) continue;   // 그림 밖: 값 0, 못 쓴다
                int u = uy * w + ux, at = (i * labels + j) * 3;
                vals[at] = img[3 * u]; vals[at + 1] = img[3 * u + 1]; vals[at + 2] = img[3 * u + 2];
                if ((free[i] || j == identity) && mask[u] != 0) { valid[i] |= 1UL << j; label[i] = j; }
            }
            up[i] = y > 0 ? backref[(y - 1) * w + x] : -1;
            down[i] = y + 1 < h ? backref[(y + 1) * w + x] : -1;
            left[i] = x > 0 ? backref[y * w + x - 1] : -1;
            right[i] = x + 1 < w ? backref[y * w + x + 1] : -1;
        }

        // 고를 수 있는 점끼리 이어진 덩어리 — 띠의 점(늘 제자리)이 사이를 막아, 한 옮김으로의 확장은 덩어리마다 따로 풀린다(값은 덩어리들의 합). 큰 덩어리부터 나란히 잰다
        var groups = Components(free, up, down, left, right).OrderByDescending(g => g.Length).Select(g => new Group(g, labels)).ToList();
        var local = new int[count];
        Array.Fill(local, -1);
        foreach (var g in groups)
        {
            for (int k = 0; k < g.Points.Length; k++) local[g.Points[k]] = k;
            // 이음매(이웃한 두 점, 앞의 것이 위·왼쪽) — 아래·오른쪽은 늘, 위·왼쪽은 그 이웃이 띠의 점일 때만(덩어리 안의 이웃은 그쪽의 아래·오른쪽으로 이미 들어 있다)
            var links = new List<(int A, int B)>(g.Points.Length * 2);
            foreach (int i in g.Points)
            {
                if (down[i] >= 0) links.Add((i, down[i]));
                if (right[i] >= 0) links.Add((i, right[i]));
                if (up[i] >= 0 && !free[up[i]]) links.Add((up[i], i));
                if (left[i] >= 0 && !free[left[i]]) links.Add((left[i], i));
            }
            g.Links = links.ToArray();
        }
        var pool = new ConcurrentBag<Scratch>();
        var options = new ParallelOptions { CancellationToken = ct };
        int cuts = 0, rounds = 0;
        void MeasureAll(List<Group> which)
        {
            var work = new List<(Group Group, int Alpha)>(which.Count * labels);
            foreach (var g in which)
                for (int alpha = 0; alpha < labels; alpha++) work.Add((g, alpha));
            cuts += work.Count;
            Parallel.ForEach(Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering), options, item =>
            {
                if (!pool.TryTake(out var scratch)) scratch = new Scratch();
                try { (item.Group.Flow[item.Alpha], item.Group.Moved[item.Alpha]) = Measure(item.Group, item.Alpha, identity, scratch, labels, vals, valid, label, local); }
                finally { pool.Add(scratch); }
            });
        }
        MeasureAll(groups);
        // 한 판: 옮김마다의 값(덩어리들의 합)이 가장 작은 하나를 받는다 — 지금까지의 값보다 Eps 넘게 작을 때만. 받은 옮김이 바꾼 덩어리만 다시 잰다
        double best = double.MaxValue;
        var total = new long[labels];
        while (groups.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            rounds++;
            Array.Clear(total);
            foreach (var g in groups)
                for (int alpha = 0; alpha < labels; alpha++) total[alpha] += g.Flow[alpha];
            int pick = 0;
            for (int alpha = 1; alpha < labels; alpha++) if (total[alpha] < total[pick]) pick = alpha;
            if (!(total[pick] < (1.0 - Eps) * best)) break;
            best = total[pick];
            var changed = new List<Group>();
            foreach (var g in groups)
            {
                if (g.Moved[pick].Length == 0) continue;
                foreach (int k in g.Moved[pick]) label[g.Points[k]] = pick;
                changed.Add(g);
            }
            if (changed.Count == 0) break;
            MeasureAll(changed);
        }

        trace?.Invoke($"points {count} labels {labels} groups {groups.Count} largest {(groups.Count > 0 ? groups[0].Points.Length : 0)} prepare {prepared}ms search {searched - prepared}ms{searchDetail} rounds {rounds} cuts {cuts} cut {clock.ElapsedMilliseconds - searched}ms");   // loc:none
        var stranded = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int p = py[i] * w + px[i], j = label[i];
            // 쓸 수 있는 옮김이 하나도 없던 구멍 픽셀 — xphoto는 여기를 검게(0) 칠한다
            if (mask[p] == 0 && (valid[i] >> j & 1) == 0) { stranded.Add(p); continue; }
            int at = (i * labels + j) * 3;
            src[3 * p] = vals[at]; src[3 * p + 1] = vals[at + 1]; src[3 * p + 2] = vals[at + 2];
        }
        if (TooManyStranded(stranded.Count, holes) || !SpreadInto(src, w, h, stranded)) return null;
        var result = new Mat(h, w, MatType.CV_8UC3);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(src, y * w * 3, result.Ptr(y), w * 3);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>가져올 데가 없는 구멍 픽셀이 옆 픽셀로 채우기에는 너무 많은가 — <see cref="StrandedFew"/>개와 구멍의 1/<see cref="StrandedShare"/> 가운데 큰 쪽을 넘으면.</summary>
    internal static bool TooManyStranded(int stranded, int holePixels) => stranded > Math.Max(StrandedFew, holePixels / StrandedShare);

    /// <summary>그림(<paramref name="bgr"/>)의 빈 픽셀들(<paramref name="empty"/> — 픽셀 번호)을 바로 옆(위·왼쪽·오른쪽·아래 차례)의 찬 픽셀 색으로 채운다 — 한 겹씩 안쪽으로.
    /// 한 겹도 못 나아가면(찬 이웃이 하나도 없다) false. 가져올 데가 없는 구멍 픽셀 몇 개를 메우는 데 쓴다(<see cref="StrandedFew"/>).</summary>
    internal static bool SpreadInto(byte[] bgr, int w, int h, List<int> empty)
    {
        var open = new HashSet<int>(empty);
        while (open.Count > 0)
        {
            var filled = new List<(int Pixel, int From)>();
            foreach (int p in empty)
            {
                if (!open.Contains(p)) continue;
                int x = p % w, y = p / w;
                foreach (int from in (ReadOnlySpan<int>)[y > 0 ? p - w : -1, x > 0 ? p - 1 : -1, x + 1 < w ? p + 1 : -1, y + 1 < h ? p + w : -1])
                    if (from >= 0 && !open.Contains(from)) { filled.Add((p, from)); break; }
            }
            if (filled.Count == 0) return false;
            // 이번 겹은 한꺼번에 — 같은 겹의 픽셀끼리 서로의 색을 물려받지 않게(훑는 차례에 따라 달라진다)
            foreach (var (p, from) in filled) { bgr[3 * p] = bgr[3 * from]; bgr[3 * p + 1] = bgr[3 * from + 1]; bgr[3 * p + 2] = bgr[3 * from + 2]; }
            foreach (var (p, _) in filled) open.Remove(p);
        }
        return true;
    }

    /// <summary>8비트 그림을 줄마다 배열로 옮긴다(줄 끝의 채움 바이트는 건너뛴다).</summary>
    private static byte[] Bytes(Mat mat, int channels)
    {
        int rows = mat.Rows, rowBytes = mat.Cols * channels;
        var bytes = new byte[rows * rowBytes];
        for (int y = 0; y < rows; y++) Marshal.Copy(mat.Ptr(y), bytes, y * rowBytes, rowBytes);
        return bytes;
    }

    // ───────── ① 자주 나오는 옮김 ─────────

    /// <summary>그림(<paramref name="img"/> — BGR 바이트, 구멍은 0)에서 가장 자주 나오는 '닮은 조각까지의 옮김'을 많아야 <see cref="MaxTransforms"/>개 돌려준다(많이 나온 차례).
    /// 픽셀마다 그 자리의 8×8 조각을 왈시-아다마르 특징 <see cref="Features"/>개로 줄이고(<see cref="WalshHadamard"/>), KD 나무의 잎(닮은 조각 <see cref="LeafSize"/>개)과 위·왼쪽 이웃이 찾은 짝의 옆자리에서
    /// 가장 닮은 조각을 찾는다(가까이 <see cref="ZeroThresh"/> 안은 빼고). 그 옮김들의 도수표를 흐리게 해 봉우리를 센다.</summary>
    internal static List<(int Dx, int Dy)> DominantTransforms(byte[] img, int w, int h, CancellationToken ct = default, Action<string>? trace = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long Lap() { long ms = clock.ElapsedMilliseconds; clock.Restart(); return ms; }
        int n = w * h;
        var feat = new float[n * Features];
        using (var plane = new Mat(h, w, MatType.CV_32FC1))
        {
            var values = new float[n];
            int offset = 0;
            for (int c = 0; c < 3; c++)
            {
                for (int p = 0; p < n; p++) values[p] = img[3 * p + c];
                plane.SetArray(values);
                WalshHadamard(plane, ChannelFeatures[c], feat, offset);
                offset += ChannelFeatures[c];
            }
        }
        ct.ThrowIfCancellationRequested();
        long featMs = Lap();

        // KD 나무: 가장 넓게 퍼진 특징으로 반씩 가른다 — 잎(LeafSize 이하)에는 그 특징들이 비슷한 조각이 모인다. 색인 배열 하나를 제자리에서 가른다
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        int[] leafStart = new int[n], leafEnd = new int[n];
        var todo = new Stack<(int Left, int Right)>();
        todo.Push((0, n));
        while (todo.Count > 0)
        {
            var (left, rightEnd) = todo.Pop();
            if (rightEnd - left <= LeafSize)
            {
                for (int i = left; i < rightEnd; i++) { leafStart[idx[i]] = left; leafEnd[idx[i]] = rightEnd; }
                continue;
            }
            int nth = left + (rightEnd - left) / 2;
            Select(idx, feat, MaxSpread(idx, feat, left, rightEnd), left, rightEnd - 1, nth);
            todo.Push((left, nth + 1));
            todo.Push((nth + 1, rightEnd));
        }
        ct.ThrowIfCancellationRequested();

        // 잎 차례로 다시 늘어놓는다 — 잎 하나를 훑을 때 메모리를 이어서 읽는다(조각 하나에 96바이트, 잎 하나 6KB)
        var ordered = new float[n * Features];
        var at = new int[n];
        short[] cx = new short[n], cy = new short[n];
        for (int k = 0; k < n; k++)
        {
            int p = idx[k];
            at[p] = k;
            cx[k] = (short)(p % w); cy[k] = (short)(p / w);
            Array.Copy(feat, p * Features, ordered, k * Features, Features);
        }
        feat = null!;
        long treeMs = Lap();

        // 픽셀마다 가장 닮은 조각: 제 잎과, 윗칸·왼칸이 찾은 짝의 아래·오른쪽 칸이 든 잎을 훑는다(같은 잎이면 한 번만 — 두 번 훑어도 더 닮은 것은 안 나온다).
        // 칸 (x, y)는 윗칸과 왼칸이 끝나야 볼 수 있다 — 줄을 ScanChunk칸 토막으로 잘라, 같은 대각선에 놓인 토막(줄 번호 + 토막 번호가 같다)끼리 나란히 훑는다:
        // 그 토막들이 기대는 윗토막·왼토막은 앞 대각선에서 끝났다. 차례대로 훑은 것과 한 칸도 다르지 않다
        var annf = new int[n];
        void ScanPiece(int y, int piece)
        {
            for (int x = piece * ScanChunk, end = Math.Min(w, x + ScanChunk); x < end; x++)
            {
                int current = y * w + x, found = annf[current], mine = leafStart[current], above = -1;
                float dist = float.MaxValue;
                Nearest(current, current, x, y, w, h, ordered, at, idx, cx, cy, leafStart, leafEnd, ref found, ref dist);
                if (y > 0 && annf[current - w] + w is var up && up < n && (above = leafStart[up]) != mine)
                    Nearest(up, current, x, y, w, h, ordered, at, idx, cx, cy, leafStart, leafEnd, ref found, ref dist);
                if (x > 0 && annf[current - 1] + 1 is var beside && beside < n && leafStart[beside] != mine && leafStart[beside] != above)
                    Nearest(beside, current, x, y, w, h, ordered, at, idx, cx, cy, leafStart, leafEnd, ref found, ref dist);
                annf[current] = found;
            }
        }
        int pieces = (w + ScanChunk - 1) / ScanChunk;
        for (int diagonal = 0; diagonal < h + pieces - 1; diagonal++)
        {
            if ((diagonal & 63) == 0) ct.ThrowIfCancellationRequested();
            int first = Math.Max(0, diagonal - (pieces - 1)), last = Math.Min(h - 1, diagonal);
            if (first == last) ScanPiece(first, diagonal - first);
            else Parallel.For(first, last + 1, y => ScanPiece(y, diagonal - y));
        }
        long scanMs = Lap();
        // 옮김의 도수표((2h−1)×(2w−1), 가운데가 옮김 0) → 흐리게 → 9×9 안에서 가장 큰 자리가 봉우리
        int hw = 2 * w - 1, hh = 2 * h - 1;
        var counts = new double[hw * hh];
        for (int p = 0; p < n; p++)
            counts[(annf[p] / w - p / w + h - 1) * hw + annf[p] % w - p % w + w - 1]++;
        double[] smooth, peak;
        using (var hist = new Mat(hh, hw, MatType.CV_64FC1))
        using (var dilated = new Mat())
        using (var nine = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9)))
        {
            hist.SetArray(counts);
            Cv2.GaussianBlur(hist, hist, new Size(0, 0), Math.Sqrt(2.0), 0, BorderTypes.Constant);
            Cv2.Dilate(hist, dilated, nine);
            hist.GetArray(out smooth);
            dilated.GetArray(out peak);
        }
        var peaks = new List<(double Amount, int Order, int Dx, int Dy)>();
        for (int i = 0, t = 0; i < hh; i++)
            for (int j = 0; j < hw; j++)
            {
                double v = smooth[i * hw + j];
                if (v != 0 && v == peak[i * hw + j]) peaks.Add((v, t++, j - w + 1, i - h + 1));
            }
        // 많이 나온 차례(같으면 뒤에 찾은 것 먼저 — 원본의 std::greater<pair>와 같다)
        peaks.Sort((a, b) => a.Amount != b.Amount ? b.Amount.CompareTo(a.Amount) : b.Order.CompareTo(a.Order));
        trace?.Invoke($"features {featMs}ms tree {treeMs}ms scan {scanMs}ms peaks {Lap()}ms");   // loc:none
        return peaks.Take(MaxTransforms).Select(p => (p.Dx, p.Dy)).ToList();
    }

    /// <summary>한 채널(<paramref name="plane"/>, 32F)의 픽셀마다 8×8 조각의 왈시-아다마르 계수 <paramref name="count"/>개(낮은 주파수부터 뱀 모양 차례)를 <paramref name="feat"/>의
    /// <paramref name="offset"/>번째 칸부터 적는다. 첫 계수는 조각의 평균(상자 필터)이고, 다음 계수는 앞 계수에서 덧셈·뺄셈 한 번으로 나온다(그레이 코드 커널 — whs.hpp의 nextProjection).</summary>
    private static void WalshHadamard(Mat plane, int count, float[] feat, int offset)
    {
        int w = plane.Cols, h = plane.Rows, pw = w + 2 * PatchSize, ph = h + 2 * PatchSize;
        float[] cur;
        using (var padded = new Mat())
        using (var box = new Mat())
        {
            Cv2.CopyMakeBorder(plane, padded, PatchSize, PatchSize, PatchSize, PatchSize, BorderTypes.Constant, Scalar.All(0));
            Cv2.BoxFilter(padded, box, MatType.CV_32F, new Size(PatchSize, PatchSize), new Point(-1, -1), true, BorderTypes.Reflect);
            box.GetArray(out cur);
        }
        var next = new float[cur.Length];
        var snake = Snake(count);
        for (int k = 0; ; k++)
        {
            for (int y = 0; y < h; y++)
            {
                int from = (y + PatchSize) * pw + PatchSize, to = y * w * Features + offset + k;
                for (int x = 0; x < w; x++) feat[to + x * Features] = cur[from + x];
            }
            if (k + 1 >= count) break;
            var (a, b) = (snake[k], snake[k + 1]);
            bool plus = ((a.X != b.X ? BitOperations.PopCount((uint)(a.X & b.X)) + (b.X > a.X ? 1 : 0) : 0) & 1) != 0
                     || ((a.Y != b.Y ? BitOperations.PopCount((uint)(a.Y & b.Y)) + (b.Y > a.Y ? 1 : 0) : 0) & 1) != 0;
            int bits = BitOperations.PopCount((uint)(PatchSize - 1));
            int dx = a.X != b.X ? 1 << (bits - BitOperations.PopCount((uint)(a.X ^ b.X))) : 0;
            int dy = a.Y != b.Y ? 1 << (bits - BitOperations.PopCount((uint)(a.Y ^ b.Y))) : 0;
            for (int i = 0; i < cur.Length; i++) next[i] = -cur[i];
            for (int y = dy; y < ph; y++)
            {
                int row = y * pw, prev = (y - dy) * pw;
                if (plus) for (int x = dx; x < pw; x++) next[row + x] += cur[prev + x - dx] - next[prev + x - dx];
                else for (int x = dx; x < pw; x++) next[row + x] -= cur[prev + x - dx] - next[prev + x - dx];
            }
            (cur, next) = (next, cur);
        }
    }

    /// <summary>계수의 차례 — (0,0)에서 시작해 낮은 주파수부터 뱀 모양으로 도는 <paramref name="count"/>개의 (x, y)(whs.hpp의 generate_snake).</summary>
    private static List<(int X, int Y)> Snake(int count)
    {
        var snake = new List<(int X, int Y)> { (0, 0) };
        var prev = (X: 0, Y: 0);
        for (int k = 1, num = 1; num <= count; k++)
        {
            var d0 = (X: (k & 1) == 0 ? 1 : 0, Y: k & 1);
            var d1 = (X: -(k & 1), Y: (k & 1) == 0 ? -1 : 0);
            prev = (prev.X - d1.X, prev.Y - d1.Y);
            snake.Add(prev); num++;
            foreach (var d in new[] { d0, d1 })
                for (int j = 0; j < k && num < count; j++)
                {
                    prev = (prev.X + d.X, prev.Y + d.Y);
                    snake.Add(prev); num++;
                }
        }
        return snake;
    }

    /// <summary>색인 구간 [<paramref name="left"/>, <paramref name="right"/>)의 조각들에서 값이 가장 넓게 퍼진 특징의 번호.</summary>
    private static unsafe int MaxSpread(int[] idx, float[] feat, int left, int right)
    {
        int step = Vector<float>.Count;
        Span<float> lo = stackalloc float[Features], hi = stackalloc float[Features];
        fixed (float* f = feat)
        {
            if (Features % step == 0)
            {
                Span<Vector<float>> min = stackalloc Vector<float>[Features / step], max = stackalloc Vector<float>[Features / step];
                for (int k = 0; k < min.Length; k++) min[k] = max[k] = Unsafe.ReadUnaligned<Vector<float>>(f + (long)idx[left] * Features + k * step);
                for (int i = left + 1; i < right; i++)
                {
                    float* row = f + (long)idx[i] * Features;
                    for (int k = 0; k < min.Length; k++)
                    {
                        var v = Unsafe.ReadUnaligned<Vector<float>>(row + k * step);
                        min[k] = Vector.Min(min[k], v); max[k] = Vector.Max(max[k], v);
                    }
                }
                for (int k = 0; k < min.Length; k++) { min[k].CopyTo(lo[(k * step)..]); max[k].CopyTo(hi[(k * step)..]); }
            }
            else
            {
                for (int d = 0; d < Features; d++) lo[d] = hi[d] = f[(long)idx[left] * Features + d];
                for (int i = left + 1; i < right; i++)
                {
                    float* row = f + (long)idx[i] * Features;
                    for (int d = 0; d < Features; d++) { if (row[d] < lo[d]) lo[d] = row[d]; if (row[d] > hi[d]) hi[d] = row[d]; }
                }
            }
        }
        int widest = 0;
        for (int d = 1; d < Features; d++) if (hi[d] - lo[d] > hi[widest] - lo[widest]) widest = d;
        return widest;
    }

    /// <summary>색인 구간 [<paramref name="l"/>, <paramref name="r"/>](양 끝 포함)을 특징 <paramref name="dim"/>의 값으로 갈라 <paramref name="k"/>번째 자리에 그 차례의 조각이 오게 한다
    /// (왼쪽은 그보다 크지 않고 오른쪽은 작지 않다 — 빠른 고르기). 같은 값이 많아도(평평한 바탕의 조각은 특징이 다 같다) 한 번 훑을 때마다 구간이 줄어든다.</summary>
    private static void Select(int[] idx, float[] feat, int dim, int l, int r, int k)
    {
        while (l < r)
        {
            float pivot = feat[idx[k] * Features + dim];
            int i = l, j = r;
            do
            {
                while (feat[idx[i] * Features + dim] < pivot) i++;
                while (pivot < feat[idx[j] * Features + dim]) j--;
                if (i <= j) { (idx[i], idx[j]) = (idx[j], idx[i]); i++; j--; }
            } while (i <= j);
            if (j < k) l = i;
            if (k < i) r = j;
        }
    }

    /// <summary>조각 <paramref name="leafOf"/>가 든 잎을 훑어 조각 <paramref name="current"/>((<paramref name="x"/>, <paramref name="y"/>))와 가장 닮은 것을 찾는다 — 지금까지의 것(<paramref name="dist"/>)보다 닮았을 때만 바꾼다.
    /// 가까이(가로·세로 다 <see cref="ZeroThresh"/> 안) 있는 조각과 그림 가장자리의 조각은 짝으로 치지 않는다.</summary>
    private static unsafe void Nearest(int leafOf, int current, int x, int y, int w, int h, float[] ordered, int[] at, int[] idx, short[] cx, short[] cy,
        int[] leafStart, int[] leafEnd, ref int found, ref float dist)
    {
        if ((uint)leafOf >= (uint)at.Length) return;
        int step = Vector<float>.Count;
        fixed (float* f = ordered)
        {
            float* mine = f + (long)at[current] * Features;
            for (int k = leafStart[leafOf], end = leafEnd[leafOf]; k < end; k++)
            {
                int nx = cx[k], ny = cy[k];
                if (Math.Abs(ny - y) < ZeroThresh && Math.Abs(nx - x) < ZeroThresh) continue;
                if (nx >= w - 1 || nx < 1 || ny >= h - 1 || ny < 1) continue;
                float* other = f + (long)k * Features;
                // 앞의 특징 몇 개(가장 낮은 주파수 — 차이의 대부분이 여기 있다)만으로 이미 지금 것보다 멀면 나머지는 세지 않는다. 더할 것은 0 이상이라 결과는 같다
                var head = Unsafe.ReadUnaligned<Vector<float>>(mine) - Unsafe.ReadUnaligned<Vector<float>>(other);
                float sum = Vector.Dot(head, head);
                if (sum >= dist) continue;
                int d = step;
                for (; d <= Features - step; d += step)
                {
                    var diff = Unsafe.ReadUnaligned<Vector<float>>(mine + d) - Unsafe.ReadUnaligned<Vector<float>>(other + d);
                    sum += Vector.Dot(diff, diff);
                }
                for (; d < Features; d++) { float diff = mine[d] - other[d]; sum += diff * diff; }
                if (sum < dist) { dist = sum; found = idx[k]; }
            }
        }
    }

    // ───────── ② 옮김 고르기 ─────────

    /// <summary>고를 수 있는 점(<paramref name="free"/>)끼리 위·아래·왼쪽·오른쪽으로 이어진 덩어리들 — 덩어리마다 그 점들의 번호(작은 것부터).</summary>
    private static List<int[]> Components(bool[] free, int[] up, int[] down, int[] left, int[] right)
    {
        var groups = new List<int[]>();
        var seen = new bool[free.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < free.Length; start++)
        {
            if (!free[start] || seen[start]) continue;
            members.Clear();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                members.Add(i);
                foreach (int n in (ReadOnlySpan<int>)[up[i], down[i], left[i], right[i]])
                    if (n >= 0 && free[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
            }
            members.Sort();
            groups.Add(members.ToArray());
        }
        return groups;
    }

    /// <summary>고를 수 있는 점끼리 이어진 덩어리 하나 — 점들(번호, 작은 것부터)과 이음매, 그리고 옮김마다 잰 것(절단의 값 · 그 옮김으로 바뀌는 점들의 덩어리 안 차례).</summary>
    private sealed class Group(int[] points, int labels)
    {
        public readonly int[] Points = points;
        public (int A, int B)[] Links = [];
        public readonly long[] Flow = new long[labels];
        public readonly int[][] Moved = new int[labels][];
    }

    /// <summary>확장 하나를 재는 데 쓰는 작업 공간 — 그래프와, 덩어리 안의 차례(0부터)로 찾는 배열들. 재는 일마다 하나씩 빌려 쓴다(나란히 재도 섞이지 않게).</summary>
    private sealed class Scratch
    {
        public readonly Graph Graph = new();
        public int[] Node = [];      // 점 → 그래프의 꼭짓점(없으면 -1)
        public bool[] Stuck = [];    // 그 옮김을 못 쓰는 점(지금 옮김에 묶여 있다)
        public int[] Moved = [];     // 바뀌는 점들(덩어리 안의 차례)
        public void Fit(int size)
        {
            if (Node.Length >= size) return;
            Node = new int[size]; Stuck = new bool[size]; Moved = new int[size];
        }
    }

    /// <summary>덩어리 하나에서 옮김 <paramref name="alpha"/>로의 확장을 잰다: 점마다 '지금 옮김을 지킬까, α로 바꿀까'를 최소 절단으로 정해, 그 절단의 값(작을수록 이음매가 덜 어긋난다)과
    /// α로 바뀌는 점들(덩어리 안의 차례)을 돌려준다. 옮김(<paramref name="label"/>)은 건드리지 않는다.
    /// 값의 짜임은 xphoto(photomontage.hpp의 setWeights) 그대로다 — 이웃한 두 점(앞이 위·왼쪽)의 옮김이 같으면 변 하나(하나만 α가 될 때의 어긋남),
    /// 다르면 사이에 점 하나를 두고: 수채로 지금의 어긋남, 앞 점으로 '앞이 지키고 뒤가 α'의 어긋남, 뒤 점으로는 **앞 점 한 픽셀에서** α와 뒤의 옮김이 가져오는 색의 차의 두 배(원본의 별난 식).
    /// 그래프에는 α를 쓸 수 있고 아직 α가 아닌 점만 넣는다. α를 못 쓰는 점(띠의 점은 제자리만 쓴다)은 원본에서 원천에 아주 굵게 묶여 늘 지키는 쪽이다 — 넣지 않고,
    /// 그 점으로 가던 변의 무게를 이웃의 '원천과의 무게'로 옮긴다(최대 흐름의 값이 같다). 양 끝이 다 그런 사이 점은 값만 더한다. 이미 α인 점으로 가는 변은 원래 무게가 0이다.</summary>
    private static (long Flow, int[] Moved) Measure(Group group, int alpha, int identity, Scratch scratch, int labels, byte[] vals, ulong[] valid, int[] label, int[] local)
    {
        var points = group.Points;
        scratch.Fit(points.Length);
        var graph = scratch.Graph;
        int[] node = scratch.Node;
        bool[] stuck = scratch.Stuck;
        ulong bit = 1UL << alpha;
        graph.Clear();
        for (int k = 0; k < points.Length; k++)
        {
            int i = points[k];
            stuck[k] = (valid[i] & bit) == 0;
            node[k] = !stuck[k] && label[i] != alpha ? graph.AddVertex() : -1;
        }
        bool bandStuck = alpha != identity;
        long constant = 0;
        foreach (var (a, b) in group.Links)
        {
            int ka = local[a], kb = local[b];
            int na = ka >= 0 ? node[ka] : -1, nb = kb >= 0 ? node[kb] : -1;
            int la = label[a], lb = label[b];
            if (la == lb)
            {
                if (la == alpha || (na < 0 && nb < 0)) continue;
                int apart = Seam(vals, labels, a, b, la, alpha);
                if (apart == 0) continue;
                if (na >= 0 && nb >= 0) graph.AddEdges(na, nb, apart, apart);
                else graph.AddTerminal(na >= 0 ? na : nb, apart, 0);   // 다른 쪽은 α를 못 쓰는 점이다(같은 옮김인데 꼭짓점이 아니다)
            }
            else
            {
                bool fa = ka >= 0 ? stuck[ka] : bandStuck, fb = kb >= 0 ? stuck[kb] : bandStuck;
                int now = Seam(vals, labels, a, b, la, lb);
                int first = Seam(vals, labels, a, b, la, alpha), second = 2 * Apart(vals, (a * labels + alpha) * 3, (a * labels + lb) * 3);
                int held = (fa ? first : 0) + (fb ? second : 0);   // 못 바꾸는 끝점에서 들어오는 무게
                bool toA = na >= 0 && first != 0, toB = nb >= 0 && second != 0;
                if (!toA && !toB) { constant += Math.Min(now, held); continue; }
                int between = graph.AddVertex();
                graph.AddTerminal(between, held, now);
                if (toA) graph.AddEdges(na, between, first, first);
                if (toB) graph.AddEdges(between, nb, second, second);
            }
        }
        long flow = constant + graph.MaxFlow();
        int moved = 0;
        for (int k = 0; k < points.Length; k++)
            if (node[k] >= 0 && graph.InSink(node[k])) scratch.Moved[moved++] = k;
        return (flow, moved == 0 ? [] : scratch.Moved.AsSpan(0, moved).ToArray());
    }

    /// <summary>두 점 <paramref name="a"/>·<paramref name="b"/>가 옮김 <paramref name="la"/>와 <paramref name="lb"/>로 갈릴 때의 어긋남 — 두 점 각각에서 두 옮김이 가져오는 색의 차(제곱)의 합.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Seam(byte[] vals, int labels, int a, int b, int la, int lb) =>
        Apart(vals, (a * labels + la) * 3, (a * labels + lb) * 3) + Apart(vals, (b * labels + la) * 3, (b * labels + lb) * 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Apart(byte[] vals, int p, int q)
    {
        int b = vals[p] - vals[q], g = vals[p + 1] - vals[q + 1], r = vals[p + 2] - vals[q + 2];
        return b * b + g * g + r * r;
    }

    /// <summary>최대 흐름 = 최소 절단(Boykov–Kolmogorov) — OpenCV의 GCGraph를 옮긴 것이다(무게는 정수). 점마다 원천·수채와의 무게(<see cref="AddTerminal"/>), 점 사이의 양방향 변(<see cref="AddEdges"/>).
    /// <see cref="MaxFlow"/> 뒤 <see cref="InSink"/>가 그 점이 수채 쪽에 남았는지 알려 준다. <see cref="Clear"/>로 비워 다시 쓴다(배열은 그대로 둔다).</summary>
    internal sealed class Graph
    {
        private struct Vertex { public int Next, Parent, First, Ts, Dist, Weight; public byte T; }
        private struct Edge { public int Dst, Next, Weight; }
        private const int Terminal = -1, Orphan = -2, None = -1, Nil = -2;

        private Vertex[] _v = new Vertex[1024];
        private Edge[] _e = new Edge[4096];
        private int _vertices, _edges = 2;   // 변은 2번부터 — 0은 '없음', 짝은 번호의 맨 아래 비트로 뒤집는다
        private long _flow;
        private readonly Stack<int> _orphans = new();

        public void Clear() { _vertices = 0; _edges = 2; _flow = 0; }

        public int AddVertex()
        {
            if (_vertices == _v.Length) Array.Resize(ref _v, _v.Length * 2);
            _v[_vertices] = new Vertex { Next = None };
            return _vertices++;
        }

        public void AddEdges(int i, int j, int weight, int reverse)
        {
            if (_edges + 2 > _e.Length) Array.Resize(ref _e, _e.Length * 2);
            _e[_edges] = new Edge { Dst = j, Next = _v[i].First, Weight = weight };
            _v[i].First = _edges++;
            _e[_edges] = new Edge { Dst = i, Next = _v[j].First, Weight = reverse };
            _v[j].First = _edges++;
        }

        public void AddTerminal(int i, int source, int sink)
        {
            int had = _v[i].Weight;
            if (had > 0) source += had; else sink -= had;
            _flow += Math.Min(source, sink);
            _v[i].Weight = source - sink;
        }

        /// <summary>수채 쪽 나무에 달려 있는가. 어느 나무에도 없는 점(양쪽 길이 다 막혔다 — 어느 쪽에 둬도 절단의 값이 같다)은 아니다: 한때 수채 쪽에 달렸던 흔적(T)으로 가르지 않는다.</summary>
        public bool InSink(int i) => _v[i].T != 0 && _v[i].Parent != 0;

        public long MaxFlow()
        {
            var v = _v; var e = _e;
            int first = Nil, last = Nil, time = 0, pulling = 0;
            // 원천·수채에 닿은 점에서 두 나무를 기른다
            for (int i = 0; i < _vertices; i++)
            {
                v[i].Ts = 0;
                if (v[i].Weight != 0)
                {
                    if (last == Nil) first = i; else v[last].Next = i;
                    last = i;
                    v[i].Dist = 1; v[i].Parent = Terminal; v[i].T = v[i].Weight < 0 ? (byte)1 : (byte)0;
                    if (v[i].Weight < 0) pulling++;
                }
                else v[i].Parent = 0;
            }
            if (last != Nil) v[last].Next = Nil;
            // 수채에 닿은 점이 하나도 없으면 흘릴 것이 없다 — 전부 원천 쪽이다(원천의 나무를 그래프 끝까지 기를 것 없이 끝낸다)
            if (pulling == 0) return _flow;

            while (true)
            {
                int at, to, e0 = -1, ei = 0, ej;
                // 두 나무를 넓히다가 서로 닿는 변을 찾는다
                while (first != Nil)
                {
                    at = first;
                    if (v[at].Parent != 0)
                    {
                        int side = v[at].T;
                        for (ei = v[at].First; ei != 0; ei = e[ei].Next)
                        {
                            if (e[ei ^ side].Weight == 0) continue;
                            to = e[ei].Dst;
                            if (v[to].Parent == 0)
                            {
                                v[to].T = (byte)side; v[to].Parent = ei ^ 1; v[to].Ts = v[at].Ts; v[to].Dist = v[at].Dist + 1;
                                if (v[to].Next == None) { v[to].Next = Nil; v[last].Next = to; last = to; }
                                continue;
                            }
                            if (v[to].T != side) { e0 = ei ^ side; break; }
                            if (v[to].Dist > v[at].Dist + 1 && v[to].Ts <= v[at].Ts) { v[to].Parent = ei ^ 1; v[to].Ts = v[at].Ts; v[to].Dist = v[at].Dist + 1; }
                        }
                        if (e0 > 0) break;
                    }
                    first = v[first].Next;
                    v[at].Next = None;
                }
                if (e0 <= 0) break;

                // 찾은 길에서 가장 좁은 곳만큼 흘린다
                int room = e[e0].Weight;
                for (int k = 1; k >= 0; k--)
                {
                    for (at = e[e0 ^ k].Dst; ; at = e[ei].Dst)
                    {
                        if ((ei = v[at].Parent) < 0) break;
                        room = Math.Min(room, e[ei ^ k].Weight);
                    }
                    room = Math.Min(room, Math.Abs(v[at].Weight));
                }
                e[e0].Weight -= room;
                e[e0 ^ 1].Weight += room;
                _flow += room;
                for (int k = 1; k >= 0; k--)
                {
                    for (at = e[e0 ^ k].Dst; ; at = e[ei].Dst)
                    {
                        if ((ei = v[at].Parent) < 0) break;
                        e[ei ^ (k ^ 1)].Weight += room;
                        if ((e[ei ^ k].Weight -= room) == 0) { _orphans.Push(at); v[at].Parent = Orphan; }
                    }
                    v[at].Weight += room * (1 - k * 2);
                    if (v[at].Weight == 0) { _orphans.Push(at); v[at].Parent = Orphan; }
                }

                // 끊긴 점들에 새 부모를 찾아 준다
                time++;
                while (_orphans.Count > 0)
                {
                    int lost = _orphans.Pop();
                    int d, nearest = int.MaxValue;
                    int side = v[lost].T;
                    e0 = 0;
                    for (ei = v[lost].First; ei != 0; ei = e[ei].Next)
                    {
                        if (e[ei ^ (side ^ 1)].Weight == 0) continue;
                        to = e[ei].Dst;
                        if (v[to].T != side || v[to].Parent == 0) continue;
                        // 뿌리까지의 거리
                        for (d = 0; ;)
                        {
                            if (v[to].Ts == time) { d += v[to].Dist; break; }
                            ej = v[to].Parent;
                            d++;
                            if (ej < 0)
                            {
                                if (ej == Orphan) d = int.MaxValue - 1;
                                else { v[to].Ts = time; v[to].Dist = 1; }
                                break;
                            }
                            to = e[ej].Dst;
                        }
                        if (++d < int.MaxValue)
                        {
                            if (d < nearest) { nearest = d; e0 = ei; }
                            for (to = e[ei].Dst; v[to].Ts != time; to = e[v[to].Parent].Dst) { v[to].Ts = time; v[to].Dist = --d; }
                        }
                    }
                    if ((v[lost].Parent = e0) > 0) { v[lost].Ts = time; v[lost].Dist = nearest; continue; }

                    // 부모를 못 찾았다 — 나무에서 떨어진다
                    v[lost].Ts = 0;
                    for (ei = v[lost].First; ei != 0; ei = e[ei].Next)
                    {
                        to = e[ei].Dst;
                        ej = v[to].Parent;
                        if (v[to].T != side || ej == 0) continue;
                        if (e[ei ^ (side ^ 1)].Weight != 0 && v[to].Next == None) { v[to].Next = Nil; v[last].Next = to; last = to; }
                        if (ej > 0 && e[ej].Dst == lost) { _orphans.Push(to); v[to].Parent = Orphan; }
                    }
                }
            }
            return _flow;
        }
    }
}
