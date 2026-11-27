using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>식자가 원문의 글자 꼴을 따라간다(4단계, 2026-11-27 — 사용자 "번역 역식은 carrot와 유사하게": 원문 글자 크기 + 굵기 + 글꼴 분위기).
/// 번역할 때 잰 값(<see cref="TextBlockInfo.SrcEm"/>·SrcStroke·SrcContrast)으로 식자할 때 크기·굵기·글꼴을 정한다. 잰 값이 없는 블록(옛 식자 정보)은 전과 똑같이 그려져야 한다.</summary>
public class TextRendererSourceStyleTests
{
    private static AppSettings Arial() => new() { LetterFontFamily = "Arial", LetterBold = false, LetterSerifFamily = "Times New Roman" };

    private static AppSettings Old() => new() { LetterFontFamily = "Arial", LetterBold = false, LetterSizeMode = "Fit", LetterAutoWeight = false, LetterAutoFamily = false };

    /// <summary>넉넉한 자리(손으로 잡은 블록)의 짧은 글 — 풍선에 맞춘 크기가 커서 원문 크기가 그대로 드러난다.</summary>
    private static TextBlockInfo Roomy(int y, double srcEm = 0, double thick = 0, double contrast = 0, string text = "HHHH") =>
        new(100, y, 500, y + 200, "原文", text, 20, false, "#000000", ManualRect: true, OutlinePx: 0, SrcEm: srcEm, SrcThick: thick, SrcContrast: contrast);

    private static float[] Sizes(IReadOnlyList<TextBlockInfo> blocks, AppSettings? s = null, int width = 600, int height = 1457, double scale = 1)
    {
        using var page = SolidPage(width, height, 150);
        using var img = TextRenderer.Render(page, blocks, scale, scale, s ?? Arial(), out var sizes);
        return sizes;
    }

    private static bool Same(Mat a, Mat b) => a.Size() == b.Size() && Cv2.Norm(a, b, NormTypes.L1) == 0;

    // ── 크기 ──

    [Fact]
    public void BlocksWithoutMeasuredValues_AreDrawnExactlyAsBefore()
    {
        // 옛 식자 정보(잰 값이 없다)를 다시 식자하면 새 기본 설정에서도 전과 한 픽셀도 다르지 않다
        using var page = SolidPage(600, 1457, 150);
        TextBlockInfo[] blocks = [Roomy(200), Roomy(500, text: "HHHH HHHH HHHH"), Roomy(800, text: "HH") with { ManualRect = false }];
        using var now = TextRenderer.Render(page, blocks, 1, 1, Arial(), out var a);
        using var before = TextRenderer.Render(page, blocks, 1, 1, Old(), out var b);
        Assert.Equal(b, a);
        Assert.True(Same(before, now));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(34)]
    public void AMeasuredBlock_IsDrawnAtItsSourceSize(double srcEm)
    {
        // 전에는 풍선에 들어가는 가장 큰 크기(의 쪽 가운뎃값)였다 — 원문이 작게 쓴 글도 풍선을 채웠다
        Assert.Equal(srcEm, Sizes([Roomy(200, srcEm)])[0], 1);
        Assert.Equal(27.7f, Sizes([Roomy(200)])[0], 1);   // 재지 않은 블록은 전처럼 풍선에 맞춘 크기(여기서는 쪽 공통 크기의 상한 — 쪽 높이의 1.9%)
    }

    [Fact]
    public void ASourceSizeAboveThePageCap_IsUsed_WhenItFits()
    {
        // 외치는 큰 글: 쪽 상한(쪽 높이의 2.6% = 38px)에 눌리지 않는다
        var b = Roomy(200, srcEm: 60) with { X2 = 560, Y2 = 600 };
        Assert.Equal(60f, Sizes([b])[0], 1);
    }

    [Fact]
    public void ASourceSizeThatDoesNotFit_IsShrunkToThePlace()
    {
        // 번역 글이 길어 원문 크기로는 안 들어간다 — 전처럼 자리에 맞춘 크기까지 줄인다(넘치지 않는다)
        var b = Roomy(200, srcEm: 36, text: "HHHH HHHH HHHH HHHH HHHH HHHH HHHH HHHH") with { X2 = 300, Y2 = 320 };
        float withSource = Sizes([b])[0], fitted = Sizes([b with { SrcEm = 0 }], Old())[0];
        Assert.True(withSource < 30, $"{withSource}");
        Assert.True(withSource <= fitted + 0.01f, $"{withSource} <= {fitted}");
    }

    [Fact]
    public void ALargeSourceSize_LeavesRoomInItsPlace()
    {
        // 원문이 아주 크게 쓴 짧은 글: 풍선에 '들어가는 가장 큰 크기'까지 키우면 뾰족한 풍선에서 테두리에 닿는다(실제 06쪽 170px, 04쪽 67px) — 그 8할까지만 키운다
        using var page = SolidPage(600, 1457, 150);
        var b = Roomy(200, srcEm: 400, text: "HHHHHH");               // 여섯 자: 자리의 폭이 크기를 정한다(더 짧으면 큰 글의 상한이 먼저 걸린다)
        using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out var sizes);
        using var dark = new Mat();
        Cv2.InRange(img, new Scalar(0, 0, 0), new Scalar(60, 60, 60), dark);
        var ink = Cv2.BoundingRect(dark);
        Assert.True(sizes[0] > 50, $"{sizes[0]}");                    // 전의 크기(27.7)보다는 훨씬 크게
        Assert.InRange(ink.Width, 200, (int)(360 * 0.86));            // 자리(폭 360)에 꽉 차지는 않는다(여유 없이 맞추면 370px쯤)
    }

    [Fact]
    public void AnUnmeasuredBlock_FollowsThePageMedian_AndHidingABlockDoesNotMoveIt()
    {
        // 못 잰 블록(글자 수를 틀리게 읽었다·무늬 위의 글)은 쪽에서 잰 값의 가운뎃값을 따른다 — 혼자 풍선을 가득 채우지 않는다
        TextBlockInfo[] blocks = [Roomy(100, 18), Roomy(350, 22), Roomy(600, 30), Roomy(850)];
        Assert.Equal(22f, Sizes(blocks)[3], 1);
        // 결과 창에서 풍선 하나를 숨겨도 다른 풍선의 크기는 그대로다(가운뎃값은 숨긴 블록도 넣어 센다 — 빼고 세면 18이 된다)
        blocks[2] = blocks[2] with { Hidden = true };
        var sizes = Sizes(blocks);
        Assert.Equal(22f, sizes[3], 1);
        Assert.Equal(0f, sizes[2]);
    }

    [Fact]
    public void AnUnmeasuredBlock_NextToAShout_StaysAtTheBodySize()
    {
        // 본문 하나 + 외치는 글 하나만 재진 쪽: 못 잰 풍선이 외치는 글의 크기를 따라 풍선을 가득 채우면 안 된다(검토 2026-11-28 — 가운뎃값을 큰 쪽으로 골랐다) — 짝수면 작은 쪽
        var shout = Roomy(350, 60) with { X2 = 560 };
        Assert.Equal(22f, Sizes([Roomy(100, 22), shout, Roomy(700)])[2], 1);
        // 잰 것이 외치는 글 하나뿐이면 전의 방식이 쓰던 가장 큰 크기(쪽 높이의 1.9% = 27.7px)까지만
        Assert.Equal(27.7f, Sizes([shout, Roomy(700)])[1], 1);
    }

    [Fact]
    public void MarkingABubbleAsASoundEffect_DoesNotMoveTheOthers()
    {
        // 결과 창에서 풍선 하나를 효과음으로 바꿔도(잰 값은 블록에 남아 있다) 다른 풍선의 크기는 그대로다 — 쪽의 가운뎃값은 잰 값이 있는 블록 전부로 센다
        TextBlockInfo[] blocks = [Roomy(100, 18), Roomy(350, 22), Roomy(600, 30), Roomy(850)];
        Assert.Equal(22f, Sizes(blocks)[3], 1);
        blocks[2] = blocks[2] with { Sfx = true };
        Assert.Equal(22f, Sizes(blocks)[3], 1);
    }

    [Fact]
    public void TheSourceSize_IsInSourcePixels_AndScalesWithTheResultImage()
    {
        // 채색으로 결과 그림이 원본의 두 배면 글자도 두 배(잰 값은 원본의 px다)
        Assert.Equal(40f, Sizes([Roomy(200, 20)], width: 1200, height: 2914, scale: 2)[0], 1);
    }

    [Fact]
    public void TheSizeScales_StillMultiply()
    {
        var s = Arial(); s.LetterSizeScale = 1.5;
        Assert.Equal(30f, Sizes([Roomy(200, 20)], s)[0], 1);
        Assert.Equal(40f, Sizes([Roomy(200, 20) with { StyleScale = 2 }])[0], 1);
        Assert.Equal(50f, Sizes([Roomy(200, 20) with { SizePx = 50 }])[0], 1);   // 블록에 적은 크기가 이긴다
    }

    [Fact]
    public void FitMode_IgnoresTheMeasuredSize()
    {
        var s = Arial(); s.LetterSizeMode = "Fit";
        Assert.Equal(Sizes([Roomy(200)], s)[0], Sizes([Roomy(200, 18)], s)[0]);
    }

    [Fact]
    public void SoundEffects_KeepTheirOwnSizing()
    {
        // 효과음은 제 자리에 맞춘 크기다 — 원문 크기 규칙 밖이다(잰 값이 있어도 제 크기는 그대로)
        var sfx = Roomy(200, srcEm: 12) with { Sfx = true };
        Assert.Equal(Sizes([sfx with { SrcEm = 0 }])[0], Sizes([sfx])[0]);
        // 번역 때부터 효과음인 블록은 재지 않는다(잰 값 0) — 못 잰 대사가 따르는 쪽의 가운뎃값에 끼지 않고, 그 값을 받지도 않는다
        var fromPipeline = sfx with { SrcEm = 0 };
        var sizes = Sizes([Roomy(100, 22), fromPipeline, Roomy(600)]);
        Assert.Equal(22f, sizes[2], 1);
        Assert.Equal(Sizes([fromPipeline])[0], sizes[1]);
    }

    // ── 굵기 ──

    private static int[] Bold(params TextBlockInfo[] blocks) => TextRenderer.WithSourceStyle(blocks, Arial()).Select(b => b.BoldOverride).ToArray();

    [Fact]
    public void Weight_FollowsClearlyThinAndClearlyThickStrokes()
    {
        // 같은 식으로 잰 맑은 고딕: 보통 0.08~0.095 · 굵게 0.125~0.155 — 가는 글꼴(0.08~0.10)은 보통, 만화 본문·굵은 고딕(0.13~0.16)·아주 굵은 글(0.22)은 굵게
        Assert.Equal([0, 1, 1], Bold(Roomy(0, 40, 0.090), Roomy(0, 40, 0.135), Roomy(0, 90, 0.220)));
    }

    [Fact]
    public void Weight_InBetweenAndUnmeasuredBlocks_FollowTheClearMajorityOfThePage()
    {
        // 문턱 사이(0.107~0.118)의 글이 풍선마다 굵게·보통으로 갈리면 한 쪽이 얼룩진다 — 뚜렷한 블록들의 다수를 따른다
        Assert.Equal([0, 0, 0, 0], Bold(Roomy(0, 40, 0.085), Roomy(0, 40, 0.100), Roomy(0, 40, 0.112), Roomy(0)));
        Assert.Equal([1, 1, 1, 1], Bold(Roomy(0, 40, 0.130), Roomy(0, 40, 0.150), Roomy(0, 40, 0.112), Roomy(0)));
        // 다수가 없으면(뚜렷한 블록이 없다·동수) 쪽 설정 그대로(-1)
        Assert.Equal([-1, -1], Bold(Roomy(0, 40, 0.112), Roomy(0)));
        Assert.Equal([0, 1, -1], Bold(Roomy(0, 40, 0.090), Roomy(0, 40, 0.135), Roomy(0, 40, 0.112)));
    }

    [Fact]
    public void Weight_SmallText_IsNotJudgedByItsStrokes()
    {
        // 28px 아래의 글은 획이 2~3px뿐이라 잰 굵기가 실제보다 굵게 나온다(실제 04쪽: 20px 글) — 제 값으로 정하지 않고 쪽의 다수를 따른다
        Assert.Equal([0, 0, 0], Bold(Roomy(0, 40, 0.090), Roomy(0, 40, 0.095), Roomy(0, 20, 0.200)));
    }

    [Fact]
    public void Weight_TheUsersChoiceAndSoundEffects_AreLeftAlone()
    {
        var chosen = Roomy(0, 40, 0.090) with { BoldOverride = 1 };
        var sfx = Roomy(0, 40, 0.220) with { Sfx = true };
        Assert.Equal([1, -1, 0], Bold(chosen, sfx, Roomy(0, 40, 0.090)));   // 효과음은 굵기 규칙이 따로 있다(제 굵기는 안 바뀐다)
        // 다만 잰 값이 있는 블록(결과 창에서 효과음으로 바꾼 대사)은 쪽의 다수에는 그대로 든다 — 풍선 하나를 효과음으로 바꿨다고 다른 풍선의 굵기가 바뀌지 않게.
        // 번역 때부터 효과음인 블록은 재지 않아(잰 값 0) 다수에 끼지 않는다
        Assert.Equal([-1, 1], Bold(sfx, Roomy(0, 40, 0.112)));
        Assert.Equal([-1, -1], Bold(sfx with { SrcThick = 0, SrcEm = 0 }, Roomy(0, 40, 0.112)));
    }

    [Fact]
    public void Weight_SwitchedOff_ChangesNothing()
    {
        var s = Arial(); s.LetterAutoWeight = false; s.LetterAutoFamily = false;
        TextBlockInfo[] blocks = [Roomy(0, 40, 0.090, 2.5), Roomy(0, 40, 0.135)];
        Assert.Same(blocks, TextRenderer.WithSourceStyle(blocks, s));
    }

    [Fact]
    public void Weight_IsApplyingTwice_TheSameAsOnce()
    {
        // 식자의 들머리가 여럿이라(그리기·자리 재기·줄 얻기) 두 번 걸릴 수 있다 — 다수는 잰 값으로 세므로 같아야 한다
        TextBlockInfo[] blocks = [Roomy(0, 40, 0.130), Roomy(0, 40, 0.150), Roomy(0, 40, 0.090), Roomy(0)];
        var once = TextRenderer.WithSourceStyle(blocks, Arial());
        Assert.Equal(once, TextRenderer.WithSourceStyle(once, Arial()));
    }

    [Fact]
    public void Render_DrawsThickSourceTextBolder_ThanThinSourceText()
    {
        static int InkPixels(TextBlockInfo b)
        {
            using var page = SolidPage(600, 1457, 150);
            using var img = TextRenderer.Render(page, [b], 1, 1, Arial(), out _);
            using var dark = new Mat();
            Cv2.InRange(img, new Scalar(0, 0, 0), new Scalar(60, 60, 60), dark);
            return Cv2.CountNonZero(dark);
        }
        int thin = InkPixels(Roomy(200, 40, 0.090)), thick = InkPixels(Roomy(200, 40, 0.135));
        Assert.True(thick > thin * 1.2, $"{thick} > {thin}");
    }

    // ── 글꼴 ──

    private static string[] Family(AppSettings s, params TextBlockInfo[] blocks) => TextRenderer.WithSourceStyle(blocks, s).Select(b => b.FontFamily).ToArray();

    [Fact]
    public void Family_HighStrokeContrast_GetsTheSerifFamily()
    {
        // 명조(가로 획이 가늘다 — 실제 13쪽 2.04·2.20)는 명조 계열 글꼴로, 고딕(1쯤)은 쪽 글꼴 그대로
        Assert.Equal(["Times New Roman", "", ""], Family(Arial(), Roomy(0, 40, 0.12, 2.2), Roomy(0, 40, 0.12, 1.2), Roomy(0, 40, 0.12)));
    }

    [Fact]
    public void Family_SmallText_AMissingFont_TheUsersChoice_AndSoundEffects_AreLeftAlone()
    {
        var serifish = Roomy(0, 40, 0.12, 2.5);
        Assert.Equal([""], Family(Arial(), serifish with { SrcEm = 20 }));                       // 작은 글의 대비는 믿지 않는다
        var missing = Arial(); missing.LetterSerifFamily = "No Such Font 0123";
        Assert.Equal([""], Family(missing, serifish));                                            // 그 글꼴이 PC에 없으면 쪽 글꼴로(던지지 않는다)
        Assert.Equal(["Verdana"], Family(Arial(), serifish with { FontFamily = "Verdana" }));    // 블록에 적은 글꼴이 이긴다
        Assert.Equal([""], Family(Arial(), serifish with { Sfx = true }));
        var off = Arial(); off.LetterAutoFamily = false;
        Assert.Equal([""], Family(off, serifish));
    }

    // ── 그리는 차례 ──

    [Fact]
    public void Render_ANeighboursOutline_DoesNotCoverTheInk()
    {
        // 블록마다 외곽선 → 글자 차례로 그리면 뒤 블록의 외곽선이 앞 블록의 글자를 덮는다(붙은 풍선의 두 글) — 외곽선을 다 그린 뒤에 글자를 그린다(규칙 A10)
        static TextBlockInfo Red(int y) => new(100, y, 500, y + 60, "", "HHHH", 20, false, "#000000", ManualRect: true,
            SizePx: 48, OutlinePx: 12, ColorOverride: "#FF0000", OutlineColor: "#0000FF");
        static (Mat Img, Rect Ink) Draw(params TextBlockInfo[] blocks)
        {
            using var page = SolidPage(600, 1457, 150);
            var img = TextRenderer.Render(page, blocks, 1, 1, Arial(), out _);
            using var red = new Mat();
            Cv2.InRange(img, new Scalar(0, 0, 200), new Scalar(60, 60, 255), red);
            return (img, Cv2.BoundingRect(red));
        }
        static int RedIn(Mat img, Rect area)
        {
            using var roi = new Mat(img, area);
            using var red = new Mat();
            Cv2.InRange(roi, new Scalar(0, 0, 200), new Scalar(60, 60, 255), red);
            return Cv2.CountNonZero(red);
        }

        var (alone, ink) = Draw(Red(400));
        // 둘째 글을 바로 아래에 — 글자끼리는 4px 떨어지고, 둘째의 외곽선(12px)이 첫 글의 아랫부분을 지난다
        var (both, _) = Draw(Red(400), Red(400 + ink.Height + 4));
        using (alone) using (both)
            Assert.Equal(RedIn(alone, ink), RedIn(both, ink));
    }
}
