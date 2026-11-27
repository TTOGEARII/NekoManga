using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>직접 옮겨 짠 shift-map 인페인팅(<see cref="ShiftMap"/>, 2026-11-28 속도 — OpenCV xphoto의 것을 옮기고 KD 나무의 색인 복사만 없앴다).
/// 여기서는 부품을 본다: 최소 절단이 맞는가(모든 절단과 견줌), 되풀이 무늬에서 자주 나오는 옮김이 그 주기인가, 구멍을 둘레의 무늬로 잇는가, 구멍 밖은 그대로인가,
/// 못 하는 입력(재료 없음·가져올 데 없음·형이 다름)에 죽거나 검게 칠하지 않고 null인가. 실제 쪽에서의 전후(시간·모양)는 notes/perf.md.</summary>
public class ShiftMapTests
{
    // ───────── 최소 절단 ─────────

    [Fact]
    public void Graph_MaxFlow_IsTheCheapestCut_OnSmallRandomGraphs()
    {
        // 점마다 원천·수채와의 무게, 점 사이에 양방향 변 — 점이 여덟 개 이하면 모든 가르기(2^n)의 값을 다 세어 볼 수 있다. 흐름의 값과 남긴 가르기의 값이 둘 다 그 최소여야 한다
        var rng = new Random(20261128);
        var graph = new ShiftMap.Graph();
        for (int round = 0; round < 300; round++)
        {
            int n = rng.Next(2, 9);
            var source = new int[n]; var sink = new int[n];
            var edges = new List<(int A, int B, int Forward, int Back)>();
            for (int i = 0; i < n; i++)
            {
                if (rng.Next(3) > 0) source[i] = rng.Next(0, 30);
                if (rng.Next(3) > 0) sink[i] = rng.Next(0, 30);
            }
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                    if (rng.Next(3) == 0) edges.Add((a, b, rng.Next(0, 20), rng.Next(0, 20)));

            long Cost(int inSink)
            {
                long cost = 0;
                for (int i = 0; i < n; i++) cost += (inSink >> i & 1) != 0 ? source[i] : sink[i];
                foreach (var (a, b, forward, back) in edges)
                {
                    bool sa = (inSink >> a & 1) != 0, sb = (inSink >> b & 1) != 0;
                    if (!sa && sb) cost += forward;
                    if (sa && !sb) cost += back;
                }
                return cost;
            }
            long cheapest = Enumerable.Range(0, 1 << n).Min(Cost);

            graph.Clear();   // 같은 그래프를 비워 다시 쓴다(앞 판의 것이 남으면 여기서 어긋난다)
            for (int i = 0; i < n; i++)
            {
                // 무게는 두 번에 나눠 건다 — 실제로도 한 점에 여러 이웃의 무게가 쌓인다(못 바꾸는 이웃마다 한 번씩)
                int v = graph.AddVertex(), s1 = rng.Next(0, source[i] + 1), t1 = rng.Next(0, sink[i] + 1);
                graph.AddTerminal(v, s1, t1);
                graph.AddTerminal(v, source[i] - s1, sink[i] - t1);
            }
            foreach (var (a, b, forward, back) in edges) graph.AddEdges(a, b, forward, back);
            long flow = graph.MaxFlow();

            int kept = 0;
            for (int i = 0; i < n; i++) if (graph.InSink(i)) kept |= 1 << i;
            Assert.Equal(cheapest, flow);
            Assert.Equal(cheapest, Cost(kept));
        }
    }

    [Fact]
    public void Graph_AVertexCutOffFromBothSides_IsNotCountedAsSwitched()
    {
        // 원천 3 → C — B — A → 수채 3: 흐름 3이 양 끝을 다 막는다. 그러고 나면 세 점 모두 어느 쪽에도 닿지 않는다(어느 쪽에 둬도 절단의 값은 같다) — 그런 점은 옮김을 지킨다.
        // OpenCV의 것은 탐색이 한때 수채 쪽에 달았던 흔적으로 A·B를 '바뀐다'로 돌려줬다: 그래프를 어떻게 짓느냐에 따라 달라지는 값이라, 덩어리로 나눠 재면 통째로 잴 때와 답이 달라진다
        var graph = new ShiftMap.Graph();
        int a = graph.AddVertex(), b = graph.AddVertex(), c = graph.AddVertex();
        graph.AddTerminal(a, 0, 3);
        graph.AddTerminal(c, 3, 0);
        graph.AddEdges(a, b, 5, 5);
        graph.AddEdges(b, c, 5, 5);

        Assert.Equal(3, graph.MaxFlow());
        Assert.Equal([false, false, false], new[] { a, b, c }.Select(graph.InSink));

        // 수채로 가는 길이 남은 점은 바뀐다
        graph.Clear();
        a = graph.AddVertex(); b = graph.AddVertex();
        graph.AddTerminal(a, 0, 9);
        graph.AddTerminal(b, 2, 0);
        graph.AddEdges(a, b, 5, 5);
        Assert.Equal(2, graph.MaxFlow());
        Assert.Equal([true, true], new[] { a, b }.Select(graph.InSink));
    }

    // ───────── 자주 나오는 옮김 ─────────

    /// <summary>세로 줄무늬(주기 <paramref name="period"/>px, 절반이 어둡다) — BGR 바이트.</summary>
    private static byte[] Stripes(int w, int h, int period)
    {
        var img = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(x % period < period / 2 ? 40 : 220);
                img[(y * w + x) * 3] = img[(y * w + x) * 3 + 1] = img[(y * w + x) * 3 + 2] = v;
            }
        return img;
    }

    private static Mat ToMat(byte[] bgr, int w, int h)
    {
        var mat = new Mat(h, w, MatType.CV_8UC3);
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(bgr, y * w * 3, mat.Ptr(y), w * 3);
        return mat;
    }

    [Fact]
    public void DominantTransforms_OfARepeatingPattern_AreWholePeriods()
    {
        // 줄무늬에서 '가장 닮은 다른 조각'은 가로로 주기의 배수만큼 떨어져 있다 — 그 밖의 옮김은 줄이 어긋난다
        var shifts = ShiftMap.DominantTransforms(Stripes(120, 100, 6), 120, 100);

        Assert.InRange(shifts.Count, 10, ShiftMap.MaxTransforms);
        Assert.All(shifts.Take(10), s => Assert.Equal(0, s.Dx % 6));
        Assert.DoesNotContain((0, 0), shifts);                                                 // 제자리는 옮김이 아니다
        Assert.All(shifts.Take(10), s => Assert.True(Math.Abs(s.Dx) >= ShiftMap.ZeroThresh || Math.Abs(s.Dy) >= ShiftMap.ZeroThresh));   // 제 옆의 조각은 세지 않는다
    }

    // ───────── 메우기 ─────────

    private static Mat HoleMask(int w, int h, Rect hole)
    {
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
        Cv2.Rectangle(mask, hole, Scalar.Black, -1);
        return mask;
    }

    [Fact]
    public void Inpaint_AHoleInStripes_IsFilledWithTheStripes_AndTheRestIsUntouched()
    {
        const int w = 160, h = 140;
        var truth = Stripes(w, h, 6);
        var hole = new Rect(60, 50, 36, 30);
        var broken = (byte[])truth.Clone();
        for (int y = hole.Top; y < hole.Bottom; y++)
            for (int x = hole.Left; x < hole.Right; x++)
                broken[(y * w + x) * 3] = broken[(y * w + x) * 3 + 1] = broken[(y * w + x) * 3 + 2] = 128;   // 구멍 안에 무엇이 있었든 쓰지 않는다
        using var patch = ToMat(broken, w, h);
        using var mask = HoleMask(w, h, hole);

        using var filled = ShiftMap.Inpaint(patch, mask);

        Assert.NotNull(filled);
        Assert.Equal((patch.Size(), patch.Type()), (filled!.Size(), filled.Type()));
        filled.GetArray(out Vec3b[] got);
        int wrongInside = 0, changedOutside = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                bool inside = hole.Contains(new Point(x, y));
                if (inside && got[p].Item0 != truth[p * 3]) wrongInside++;
                if (!inside && got[p].Item0 != broken[p * 3]) changedOutside++;
            }
        Assert.Equal(0, changedOutside);
        Assert.Equal(0, wrongInside);   // 주기만큼 옮겨 가져오면 한 픽셀도 안 틀린다
    }

    [Fact]
    public void Inpaint_ChangesNothingFartherThanTwoPixelsFromTheHole()
    {
        // 옮김을 고를 수 있는 것은 구멍과 그 둘레 2px뿐이다(이음매를 둘레의 그림에 맞추는 자리) — 그 밖 2px 띠는 제자리로 묶여 경계 조건이 되고, 더 밖은 건드리지 않는다.
        // 되풀이 없는 그림(잡음)으로 본다: 줄무늬는 어디서 옮겨 와도 같은 값이라 바뀐 것이 드러나지 않는다
        const int w = 150, h = 130;
        var rng = new Random(3);
        var img = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++) img[3 * p] = img[3 * p + 1] = img[3 * p + 2] = (byte)rng.Next(256);
        var hole = new Rect(55, 45, 36, 40);
        using var patch = ToMat(img, w, h);
        using var mask = HoleMask(w, h, hole);

        using var filled = ShiftMap.Inpaint(patch, mask);

        Assert.NotNull(filled);
        filled!.GetArray(out Vec3b[] got);
        var near = Rect.Inflate(hole, 2, 2);
        int changedFar = 0, changedInside = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool changed = got[y * w + x].Item0 != img[(y * w + x) * 3];
                if (changed && !near.Contains(new Point(x, y))) changedFar++;
                if (changed && hole.Contains(new Point(x, y))) changedInside++;
            }
        Assert.Equal(0, changedFar);
        Assert.InRange(changedInside, hole.Width * hole.Height * 9 / 10, hole.Width * hole.Height);   // 구멍은 메워졌다(잡음이라 우연히 같은 값은 드물다)
    }

    [Fact]
    public void Inpaint_TheSameInput_GivesTheSameResult()
    {
        // 옮김마다의 확장을 나란히 돌린다 — 몇 스레드로, 어떤 차례로 끝나든 결과가 같아야 한다
        const int w = 150, h = 130;
        var rng = new Random(7);
        var img = Stripes(w, h, 10);
        for (int k = 0; k < 400; k++) { int p = rng.Next(w * h) * 3; img[p] = img[p + 1] = img[p + 2] = (byte)rng.Next(256); }   // 무늬가 딱 맞지 않게 잡음을 섞는다
        using var patch = ToMat(img, w, h);
        using var mask = HoleMask(w, h, new Rect(50, 40, 40, 44));

        using var first = ShiftMap.Inpaint(patch, mask);
        using var second = ShiftMap.Inpaint(patch, mask);

        Assert.NotNull(first);
        first!.GetArray(out Vec3b[] a);
        second!.GetArray(out Vec3b[] b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Inpaint_NothingToFill_ReturnsTheImageAsItIs()
    {
        // xphoto는 메울 자리가 없는 마스크에 프로세스째 죽었다
        using var patch = ToMat(Stripes(64, 48, 6), 64, 48);
        using var mask = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(255));

        using var same = ShiftMap.Inpaint(patch, mask);

        Assert.NotNull(same);
        same!.GetArray(out Vec3b[] a);
        patch.GetArray(out Vec3b[] b);
        Assert.Equal(b, a);
        Assert.NotEqual(patch.Data, same.Data);   // 사본이다 — 호출자가 따로 해제한다
    }

    [Fact]
    public void Inpaint_WhatItCannotDo_IsNull_NotACrash()
    {
        using var patch = ToMat(Stripes(64, 48, 6), 64, 48);
        using var nothingLeft = new Mat(48, 64, MatType.CV_8UC1, Scalar.Black);                 // 재료가 없다
        using var gray = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(100));
        using var otherSize = new Mat(40, 64, MatType.CV_8UC1, Scalar.All(255));
        using var empty = new Mat();

        Assert.Null(ShiftMap.Inpaint(patch, nothingLeft));
        Assert.Null(ShiftMap.Inpaint(gray, nothingLeft));       // 한 채널 그림
        Assert.Null(ShiftMap.Inpaint(patch, otherSize));
        Assert.Null(ShiftMap.Inpaint(empty, nothingLeft));
    }

    [Fact]
    public void Inpaint_AHoleWithNowhereToCopyFrom_IsNotPaintedBlack()
    {
        // 재료가 가장자리 세 줄뿐이다: 구멍 한가운데의 픽셀은 어느 옮김으로도 재료에 닿지 못한다(닿는 옮김은 16px 넘게 떨어진 조각 사이의 것뿐인데 가운데서는 다 구멍이나 그림 밖이다).
        // xphoto는 그런 픽셀을 0(검정)으로 채워 돌려줬다 — 검은 얼룩을 남기느니 못 한 것으로 쳐서 호출부가 LaMa로 넘기게 한다
        const int w = 44, h = 44;
        using var patch = ToMat(Stripes(w, h, 6), w, h);
        using var mask = HoleMask(w, h, new Rect(3, 3, w - 6, h - 6));

        using var result = ShiftMap.Inpaint(patch, mask);

        if (result != null)
        {
            // 메웠다면 구멍 안에 '가져올 데가 없어 생긴 검정'(0)은 없어야 한다 — 줄무늬의 값은 40과 220뿐이다
            result.GetArray(out Vec3b[] got);
            Assert.DoesNotContain(got, v => v.Item0 == 0);
        }
    }

    [Fact]
    public void SpreadInto_FillsTheEmptyPixels_FromTheFilledOnesBeside_LayerByLayer()
    {
        // 5×3 그림의 가운데 줄 세 칸이 비었다: 양 끝 칸은 위의 색을(위가 먼저다), 가운데 칸도 위의 색을 받는다. 같은 겹에서 채운 이웃의 색을 물려받지 않는다
        const int w = 5, h = 3;
        var bgr = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++) bgr[3 * p] = bgr[3 * p + 1] = bgr[3 * p + 2] = (byte)(10 * (p % w) + 100 * (p / w));   // 줄마다·칸마다 다른 색
        var empty = new List<int> { w + 1, w + 2, w + 3 };
        foreach (int p in empty) bgr[3 * p] = bgr[3 * p + 1] = bgr[3 * p + 2] = 255;

        Assert.True(ShiftMap.SpreadInto(bgr, w, h, empty));

        Assert.Equal([10, 20, 30], empty.Select(p => (int)bgr[3 * p]));   // 바로 위 칸(0번 줄)의 색
    }

    [Fact]
    public void SpreadInto_DoesNotInheritFromAPixelFilledInTheSameLayer()
    {
        // 4×3 그림에서 (1,1)·(2,1)·(2,0)이 비었다. (2,1)의 위·왼쪽은 같은 겹에서 채워지는 칸이다 — 그 색을 물려받지 않고 찬 칸인 오른쪽 (3,1)에서 가져온다(훑는 차례에 따라 결과가 달라지지 않게)
        const int w = 4, h = 3;
        var bgr = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++) bgr[3 * p] = bgr[3 * p + 1] = bgr[3 * p + 2] = (byte)(10 * (p % w) + 100 * (p / w));
        var empty = new List<int> { 1 * w + 1, 1 * w + 2, 0 * w + 2 };

        Assert.True(ShiftMap.SpreadInto(bgr, w, h, empty));

        Assert.Equal([10, 130, 10], empty.Select(p => (int)bgr[3 * p]));   // (1,1) ← 위 (1,0) · (2,1) ← 오른쪽 (3,1) · (2,0) ← 왼쪽 (1,0)
    }

    [Theory]
    [InlineData(16, 100, false)]      // 열여섯 개까지는 구멍이 작아도 채운다
    [InlineData(17, 100, true)]
    [InlineData(200, 20_000, false)]  // 큰 구멍은 1%까지
    [InlineData(201, 20_000, true)]
    [InlineData(0, 0, false)]
    public void TooManyStranded_AFewAreFilledFromBeside_ManyAreAFailure(int stranded, int holePixels, bool tooMany) =>
        Assert.Equal(tooMany, ShiftMap.TooManyStranded(stranded, holePixels));

    [Fact]
    public void SpreadInto_APixelWalledInByEmptyOnes_IsReachedInALaterLayer_AndNothingAtAll_IsAFailure()
    {
        const int w = 5, h = 5;
        var bgr = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++) bgr[3 * p] = bgr[3 * p + 1] = bgr[3 * p + 2] = 77;
        var plus = new List<int> { 2 * w + 2, 1 * w + 2, 2 * w + 1, 2 * w + 3, 3 * w + 2 };   // 가운데 칸과 그 네 이웃 — 가운데는 둘째 겹에서야 닿는다
        foreach (int p in plus) bgr[3 * p] = bgr[3 * p + 1] = bgr[3 * p + 2] = 0;

        Assert.True(ShiftMap.SpreadInto(bgr, w, h, plus));
        Assert.All(plus, p => Assert.Equal(77, bgr[3 * p]));

        // 전부 비었으면 가져올 데가 없다
        Assert.False(ShiftMap.SpreadInto(new byte[2 * 2 * 3], 2, 2, [0, 1, 2, 3]));
    }

    [Fact]
    public void Inpaint_AWindowWiderThanItsCoordinatesCanHold_IsNull()
    {
        // 조각의 자리를 16비트로 든다(메모리 — 창은 블록 하나 + 둘레라 수백 px이다). 3만 px이 넘는 그림을 통째로 주면 자리가 넘쳐 엉뚱한 짝을 고르게 된다 — 하지 않는다
        using var wide = new Mat(20, short.MaxValue + 1, MatType.CV_8UC3, Scalar.All(200));
        using var mask = HoleMask(wide.Cols, wide.Rows, new Rect(100, 6, 8, 8));
        using var tall = new Mat(short.MaxValue + 1, 20, MatType.CV_8UC3, Scalar.All(200));
        using var tallMask = HoleMask(tall.Cols, tall.Rows, new Rect(6, 100, 8, 8));

        Assert.Null(ShiftMap.Inpaint(wide, mask));
        Assert.Null(ShiftMap.Inpaint(tall, tallMask));
    }

    [Fact]
    public void Inpaint_ATinyWindow_DoesNotCrash()
    {
        foreach (var (w, h) in new[] { (4, 4), (9, 17), (17, 9), (20, 20) })
        {
            using var patch = ToMat(Stripes(w, h, 4), w, h);
            using var mask = HoleMask(w, h, new Rect(w / 2 - 1, h / 2 - 1, 2, 2));

            using var result = ShiftMap.Inpaint(patch, mask);

            if (result != null) Assert.Equal(patch.Size(), result.Size());
        }
    }

    [Fact]
    public void Inpaint_SeparateHoles_AreEachFilledFromTheirOwnSurroundings()
    {
        // 서로 닿지 않는 구멍 둘(덩어리 둘): 왼쪽 반은 세로 줄무늬, 오른쪽 반은 가로 줄무늬 — 구멍마다 제 둘레의 무늬로 이어져야 한다(덩어리마다 따로 재서 더한 값으로 옮김을 고른다)
        const int w = 240, h = 120;
        var truth = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)((x < w / 2 ? x : y) % 6 < 3 ? 40 : 220);
                truth[(y * w + x) * 3] = truth[(y * w + x) * 3 + 1] = truth[(y * w + x) * 3 + 2] = v;
            }
        var holes = new[] { new Rect(40, 45, 30, 24), new Rect(165, 45, 30, 24) };
        using var patch = ToMat(truth, w, h);
        using var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
        foreach (var hole in holes) Cv2.Rectangle(mask, hole, Scalar.Black, -1);

        using var filled = ShiftMap.Inpaint(patch, mask);

        Assert.NotNull(filled);
        filled!.GetArray(out Vec3b[] got);
        foreach (var hole in holes)
        {
            int wrong = 0;
            for (int y = hole.Top; y < hole.Bottom; y++)
                for (int x = hole.Left; x < hole.Right; x++)
                    if (got[y * w + x].Item0 != truth[(y * w + x) * 3]) wrong++;
            Assert.InRange(wrong, 0, hole.Width * hole.Height / 50);   // 2% 안쪽 — 무늬가 바뀌는 가운데 금에서 먼 구멍이다
        }
    }

    [Fact]
    public void Inpaint_WhenStopped_Throws()
    {
        using var patch = ToMat(Stripes(160, 140, 6), 160, 140);
        using var mask = HoleMask(160, 140, new Rect(60, 50, 36, 30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ShiftMap.Inpaint(patch, mask, cts.Token));
    }
}
