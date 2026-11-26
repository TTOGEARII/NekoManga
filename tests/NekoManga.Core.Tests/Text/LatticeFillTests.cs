using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>되풀이 무늬(줄무늬·망점·빗금) 위의 구멍을 무늬의 격자만큼 옮겨 메우기(<see cref="LatticeFill"/>, 2026-11-27 사용자 "이런 부분은 잘 지워지지도 않았어" · "여기도 마찬가지" — 줄무늬 풍선 둘).
/// shift-map은 글이 꽉 찬 줄무늬 풍선에서 풍선 밖의 머리카락·테두리를 가져왔다. 되풀이 무늬는 격자점의 픽셀을 그대로 가져오면 한 픽셀도 틀리지 않는다 — 합성 무늬로 '정확히 같다'까지 본다.</summary>
public class LatticeFillTests
{
    private static Mat Stripes(int width, int height, int pitch = 6)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < height; y += pitch) img.Line(0, y, width - 1, y, Scalar.Black, 1);
        return img;
    }

    /// <summary>엇갈린 망점 톤 — 줄마다 반 칸씩 밀린 2×2 점.</summary>
    private static Mat DotTone(int width, int height, int pitch = 6)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int row = 0, y = 0; y < height; y += pitch, row++)
            for (int x = row % 2 == 0 ? 0 : pitch / 2; x < width; x += pitch)
                img.Rectangle(new Rect(x, y, 2, 2), Scalar.Black, -1);
        return img;
    }

    /// <summary>45° 빗금 — x − y가 pitch의 배수인 픽셀이 검다.</summary>
    private static Mat Hatching(int width, int height, int pitch = 8)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (((x - y) % pitch + pitch) % pitch == 0) img.Set(y, x, new Vec3b(0, 0, 0));
        return img;
    }

    private static Mat MaskOf(Mat like, Rect r)
    {
        var m = new Mat(like.Rows, like.Cols, MatType.CV_8UC1, Scalar.Black);
        m.Rectangle(r, Scalar.White, -1);
        return m;
    }

    private static Mat Not(Mat mask)
    {
        var m = new Mat();
        Cv2.BitwiseNot(mask, m);
        return m;
    }

    /// <summary>rect 안에서 두 그림이 다른 픽셀의 수.</summary>
    private static int Differing(Mat a, Mat b, Rect rect)
    {
        using var ra = new Mat(a, rect); using var rb = new Mat(b, rect);
        using var diff = new Mat(); using var gray = new Mat();
        Cv2.Absdiff(ra, rb, diff);
        Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
        return Cv2.CountNonZero(gray);
    }

    private static int DifferingOutside(Mat a, Mat b, Rect except)
    {
        using var diff = new Mat(); using var gray = new Mat();
        Cv2.Absdiff(a, b, diff);
        Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
        if (except.Width > 0) gray.Rectangle(except, Scalar.Black, -1);
        return Cv2.CountNonZero(gray);
    }

    public static TheoryData<string> Patterns => new() { "stripes", "dots", "hatching" };

    private static Mat Pattern(string kind, int w, int h) => kind switch
    {
        "stripes" => Stripes(w, h),
        "dots" => DotTone(w, h),
        _ => Hatching(w, h),
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void TryFill_HoleInAPeriodicPattern_ContinuesItExactly(string kind)
    {
        using var original = Pattern(kind, 300, 240);
        using var patch = original.Clone();
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);   // 글자 대신 회색 덩어리가 무늬를 가리고 있다
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, original, hole));          // 한 픽셀도 틀리지 않는다
        Assert.Equal(0, DifferingOutside(filled!, patch, hole));      // 구멍 밖은 그대로
    }

    /// <summary>인쇄 망점처럼 간격이 정수가 아닌(pitch px) 부드러운 점 톤 — 점마다 가운데가 어둡고 가장자리가 흐리다.</summary>
    private static Mat SoftDotTone(int width, int height, double pitch)
    {
        var img = new Mat(height, width, MatType.CV_8UC3);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double fx = x - Math.Round(x / pitch) * pitch, fy = y - Math.Round(y / pitch) * pitch;   // 가장 가까운 점까지
                byte v = (byte)Math.Round(255 - 215 * Math.Exp(-(fx * fx + fy * fy) / 2.0));
                img.Set(y, x, new Vec3b(v, v, v));
            }
        return img;
    }

    [Fact]
    public void TryFill_ToneWithANonIntegerPitch_StaysInStepAcrossTheHole()
    {
        // 간격 4.2px의 망점: 가장 짧게 겹치는 옮김은 4px이지만 그대로 곱해 가면 열 칸 뒤에는 2px(반 칸) 어긋난다 — 구멍 한가운데의 점들이 둘레와 엇갈려 물결무늬가 된다(실제 13쪽).
        // 격자 벡터를 멀리 옮겨 보며 소수로 고치면(4.25) 구멍 끝까지 반 픽셀 안에서 맞는다
        using var original = SoftDotTone(320, 260, 4.2);
        using var patch = original.Clone();
        var hole = new Rect(120, 70, 80, 110);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        using var a = new Mat(filled!, hole); using var b = new Mat(original, hole);
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        double mean = Cv2.Mean(diff).Val0;
        Assert.True(mean < 30, $"구멍 안의 점이 둘레와 어긋났다: 평균 차 {mean:f1}");
    }

    [Fact]
    public void TryFill_SlightlyUnevenStripes_StillReachesAcrossAWideHole()
    {
        // 스캔한 줄무늬는 가로로도 조금씩 다르다(가로로 짧게 옮길수록 차가 조금 작다). 격자 다듬기가 그 작은 차를 따라가면 가로 벡터가 쪼그라들어 구멍 너머의 재료에 닿지 못하고,
        // 세로 배수로 메우다 줄이 겹친다(실제 26쪽). 폭 120px의 구멍도 가로로 가져와 줄이 그대로 이어져야 한다
        using var original = Stripes(360, 240);
        var rng = new Random(7);
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 360; x++)
            {
                var p = original.At<Vec3b>(y, x);
                byte v = (byte)Math.Clamp(p.Item0 + (int)(8 * Math.Sin(x / 9.0)) + rng.Next(-3, 4), 0, 255);   // 가로로 느리게 출렁이는 밝기 + 잡음
                original.Set(y, x, new Vec3b(v, v, v));
            }
        using var patch = original.Clone();
        var hole = new Rect(120, 70, 120, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask, out string note);

        Assert.NotNull(filled);
        Assert.Matches(@"\((-?1),0\)→\(\1,0\)", note);   // 가로 벡터는 다듬은 뒤에도 그대로 한 칸이다(진단 글: "격자 (-1,0)→(-1,0) …")
        // 줄의 자리가 맞는가: 구멍 안에서 어두운(줄) 픽셀과 밝은 픽셀이 원본과 같은 자리에 있다
        using var a = new Mat(filled!, hole); using var b = new Mat(original, hole);
        using var ga = a.CvtColor(ColorConversionCodes.BGR2GRAY); using var gb = b.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var ba = new Mat(); using var bb = new Mat(); using var diff = new Mat();
        Cv2.Threshold(ga, ba, 128, 255, ThresholdTypes.Binary);
        Cv2.Threshold(gb, bb, 128, 255, ThresholdTypes.Binary);
        Cv2.Absdiff(ba, bb, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    [Fact]
    public void TryFill_FlatBackground_IsNotAPattern()
    {
        // 흰 종이·먹칠은 무늬가 아니다 — null을 돌려주면 호출부(TextureFill)가 shift-map으로 간다(가는 선을 잇는 것은 그쪽이 한다)
        using var patch = new Mat(240, 300, MatType.CV_8UC3, Scalar.White);
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.Null(filled);
    }

    [Fact]
    public void TryFill_FaintRegularGrainOnWhite_IsStillFlat()
    {
        // 흰 바탕에 아주 옅은 규칙적인 결(압축 블록 경계, 8px마다 6단계 어두운 줄): 되풀이는 있지만 바탕은 평평하다 — 격자로 메우면 그 위를 지나던 가는 선(머리카락)을 잇지 못한다.
        // 평평한 바탕은 shift-map에 맡긴다(표준편차로 가른다)
        using var patch = new Mat(240, 300, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < 240; y += 8) patch.Line(0, y, 299, y, new Scalar(249, 249, 249), 1);
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.Null(filled);
    }

    [Fact]
    public void TryFill_HoleAcrossTwoMaterials_IsLeftToTheOtherFill()
    {
        // 먹칠과 줄무늬에 걸친 구멍: 둘레가 한 가지 무늬가 아니다 — 어디까지가 먹칠인지는 격자로 알 수 없다
        using var patch = Stripes(300, 240);
        patch.Rectangle(new Rect(0, 0, 300, 108), Scalar.Black, -1);   // 구멍(y 70..159)의 위 38줄은 먹칠, 아래 52줄은 줄무늬 — 고리의 으뜸은 줄무늬지만 60%가 안 된다
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.Null(filled);
    }

    [Fact]
    public void TryFill_RandomSpeckle_IsNotAPattern()
    {
        // 되풀이가 없는 거친 바탕(모래 톤·잡음): 어느 만큼 옮겨도 맞지 않는다 — 격자로 메우면 이음매가 온통 드러난다
        using var patch = new Mat(240, 300, MatType.CV_8UC3, Scalar.White);
        var rng = new Random(20261127);
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 300; x++)
                if (rng.NextDouble() < 0.3) patch.Set(y, x, new Vec3b(0, 0, 0));
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.Null(filled);
    }

    [Fact]
    public void TryFill_SmoothShadingWithoutAnyRepeat_IsNotAPattern()
    {
        // 부드러운 명암(그러데이션 톤·흐린 그림자): 한두 픽셀 옮겨도 거의 같아 '겹치는 옮김'이 (1,0)·(0,1)로 잡힌다 — 되풀이가 아니라 매끈할 뿐이다.
        // 그것을 격자로 삼으면 가장 가까운 픽셀 베끼기가 되고, 격자점이 9만 개라 큰 구멍에서는 한참 돈다(검토 2026-11-27). 격자 한 칸의 넓이가 1이면 무늬가 아니다
        // 흐리게 번진 얼룩(난수를 σ 3으로 흐린 것): 어디나 고르게 얼룩져 있어 둘레는 한 바탕으로 읽히지만, 무늬는 되풀이되지 않는다
        using var noise = new Mat(240, 300, MatType.CV_32FC1);
        var rng = new Random(7);
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 300; x++)
                noise.Set(y, x, (float)(rng.NextDouble() * 255));
        Cv2.GaussianBlur(noise, noise, new Size(0, 0), 3);
        using var gray = new Mat();
        noise.ConvertTo(gray, MatType.CV_8UC1, 6, 128 - 6 * 127.5);   // 흐리면 줄어든 진폭을 되살린다
        using var patch = new Mat();
        Cv2.CvtColor(gray, patch, ColorConversionCodes.GRAY2BGR);
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask, out string note);

        Assert.Null(filled);
        Assert.Contains("매끈한", note);   // 격자점을 훑기 전에 여기서 끝난다(진단 글 — 화면 글이 아니다)
    }

    [Fact]
    public void TryFill_StripedBubbleFullOfText_BringsNothingInFromOutsideTheBubble()
    {
        // 실제 26쪽: 글이 풍선을 꽉 채워 구멍 바로 옆이 풍선 테두리와 그 밖의 그림(먹칠)이다. shift-map은 밖의 덩어리를 풍선 안으로 가져왔다 — 재료는 구멍 둘레와 같은 무늬인 곳뿐이다
        using var original = new Mat(260, 300, MatType.CV_8UC3, new Scalar(20, 20, 20));
        using (var stripes = Stripes(300, 260))
        using (var inside = new Mat(260, 300, MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.Ellipse(original, new Point(150, 130), new Size(63, 103), 0, 0, 360, Scalar.Black, -1);   // 테두리 3px
            Cv2.Ellipse(inside, new Point(150, 130), new Size(60, 100), 0, 0, 360, Scalar.White, -1);
            stripes.CopyTo(original, inside);
        }
        using var patch = original.Clone();
        var hole = new Rect(122, 55, 56, 150);   // 모서리가 테두리에서 6px쯤
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, original, hole));
    }

    [Fact]
    public void TryFill_ThinLineRunningBesideTheHole_IsNotCopiedIn()
    {
        // 줄무늬 위를 지나는 가는 선(풍선 테두리의 호, 머리카락)이 구멍 바로 옆에 있다: 둘레 평균으로는 줄무늬와 구별이 안 되지만 격자만큼 옮기면 달라지는 픽셀이다 — 재료에서 뺀다.
        // 빼지 않으면 가장 가까운 격자점이 그 선 위에 떨어진 픽셀들이 선의 조각을 구멍 안에 찍는다(실제 26쪽에서 풍선 테두리의 호가 풍선 안에 들어왔다)
        using var stripes = Stripes(300, 240);
        using var patch = stripes.Clone();
        var hole = new Rect(110, 70, 60, 90);
        Cv2.Line(patch, new Point(176, 60), new Point(184, 170), Scalar.Black, 2);   // 구멍 오른쪽 6~14px을 비스듬히 내려가는 선
        using var withLine = patch.Clone();
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, stripes, hole));              // 구멍 안은 줄무늬뿐
        Assert.Equal(0, DifferingOutside(filled!, withLine, hole));      // 선은 제자리에 그대로
    }

    [Fact]
    public void TryFill_OutlineHuggingTheHole_IsNotCopiedIn()
    {
        // 구멍에 바짝 붙은(3px) 가는 테두리 — 깎고 남은 재료의 첫 줄이 바로 그 선이다. 둘레 평균·표준편차로는 줄무늬와 못 가른다: 격자만큼 옮기면 달라지는 픽셀이라서 재료에서 빠진다
        using var stripes = Stripes(300, 240);
        using var patch = stripes.Clone();
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(new Rect(hole.X - 3, hole.Y - 3, hole.Width + 6, hole.Height + 6), Scalar.Black, 1);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, stripes, hole));
    }

    [Fact]
    public void TryFill_WhiteAreaBesideALightTone_IsNotUsedAsMaterial()
    {
        // 옅은 망점(평균 227) 옆의 흰 면(살의 하이라이트, 글 뒤의 흰 판): 평균 밝기는 닮았고 옮겨도 그대로라 '무늬에 든 픽셀' 검사도 통과한다 — 둘레 표준편차(톤 80 / 흰 면 0)로 가른다.
        // 가르지 않으면 구멍의 흰 면 쪽이 허옇게 메워진다(실제 13쪽)
        using var original = DotTone(300, 240);
        original.Rectangle(new Rect(176, 0, 124, 240), Scalar.White, -1);   // 구멍 오른쪽 6px부터 흰 면
        using var patch = original.Clone();
        var hole = new Rect(110, 70, 60, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, original, hole));
    }

    [Fact]
    public void TryFill_HoleBesideAPanelBorderAndTheGutter_UsesThePatternOnly()
    {
        // 줄무늬 | 굵은 칸 테두리 | 흰 여백: 테두리와 여백 사이에는 둘레 평균이 우연히 줄무늬와 같은 가는 띠가 생긴다(실제 26쪽 — 그 띠가 재료가 되어 구멍 왼쪽이 허옇게 메워졌다)
        using var original = Stripes(300, 240);
        original.Rectangle(new Rect(0, 0, 80, 240), Scalar.White, -1);     // 여백
        original.Rectangle(new Rect(80, 0, 14, 240), Scalar.Black, -1);    // 테두리 x 80..93
        using var patch = original.Clone();
        var hole = new Rect(96, 70, 50, 90);                               // 테두리에서 2px
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, original, hole));
    }

    [Fact]
    public void TryFill_OtherTextInTheWindow_IsNeverUsedAsMaterial()
    {
        // 창 안의 다른 글자(재료로 못 쓰는 자리 — usable 0)는 가져오지 않는다. 구멍 바로 옆 8px에 있어 가장 가까운 격자점이 그 안에 떨어진다
        using var original = Stripes(300, 240);
        using var patch = original.Clone();
        var hole = new Rect(110, 70, 60, 90); var other = new Rect(178, 70, 40, 90);
        patch.Rectangle(hole, new Scalar(128, 128, 128), -1);
        patch.Rectangle(other, new Scalar(128, 128, 128), -1);
        using var holeMask = MaskOf(patch, hole);
        using var usable = Not(holeMask);
        usable.Rectangle(other, Scalar.Black, -1);

        using var filled = LatticeFill.TryFill(patch, usable, holeMask);

        Assert.NotNull(filled);
        Assert.Equal(0, Differing(filled!, original, hole));
        Assert.Equal(new Vec3b(128, 128, 128), filled!.At<Vec3b>(other.Y + 40, other.X + 20));   // 남의 자리는 메우지 않는다
    }

    [Fact]
    public void TryFill_NothingToFill_OrNothingToBorrow_ReturnsNull()
    {
        using var patch = Stripes(200, 160);
        using var none = new Mat(160, 200, MatType.CV_8UC1, Scalar.Black);
        using var all = new Mat(160, 200, MatType.CV_8UC1, Scalar.White);

        using var a = LatticeFill.TryFill(patch, all, none);    // 메울 자리가 없다
        using var b = LatticeFill.TryFill(patch, none, all);    // 재료가 없다
        Assert.Null(a);
        Assert.Null(b);
    }
}
