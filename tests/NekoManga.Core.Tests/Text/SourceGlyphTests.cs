using System.Text.Json;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>원문 글자 재기(4단계, 2026-11-27 — 사용자 "번역 역식은 carrot와 유사하게", 식자 = 원문 글자 크기 + 굵기 + 글꼴 분위기): 블록의 픽셀에서 글자 크기(em)·획 굵기·획 대비를 잰다.
/// 글꼴에 기대지 않게 격자에 그린 합성 글자(네모 테 + 열십자 — 가로 획과 세로 획이 다 있다)로 본다. 정답(칸 크기·획 굵기)은 그릴 때 정한 값이다.</summary>
public class SourceGlyphTests
{
    /// <summary>합성 쪽: 칸(em)마다 글자 하나 — 폭·높이가 칸의 86%인 네모 테와 그 안의 열십자. 세로쓰기면 기둥을 오른쪽부터, 가로쓰기면 줄을 위부터 놓는다.</summary>
    private sealed class Page : IDisposable
    {
        public readonly Mat Img, Mask;
        public Rect Block;
        public Page(int em, int[] cellsPerLine, bool vertical = true, int strokeH = 4, int strokeV = 4, bool furigana = false,
            Scalar? paper = null, Scalar? ink = null, int halo = 0, Scalar? haloColor = null, int faceShare = 86)
        {
            int gap = em * 45 / 100, lines = cellsPerLine.Length, longest = cellsPerLine.Max();
            int across = lines * em + (lines - 1) * gap + (furigana ? em / 2 : 0), along = longest * em;
            int w = (vertical ? across : along) + 80, h = (vertical ? along : across) + 80;
            Img = new Mat(h, w, MatType.CV_8UC3, paper ?? Scalar.White);
            using var strokes = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
            int face = em * faceShare / 100, inset = (em - face) / 2;
            for (int li = 0; li < lines; li++)
            for (int ci = 0; ci < cellsPerLine[li]; ci++)
            {
                int x = vertical ? w - 40 - (furigana ? em / 2 : 0) - (li + 1) * em - li * gap : 40 + ci * em;
                int y = vertical ? 40 + ci * em : 40 + li * (em + gap);
                var cell = new Rect(x + inset, y + inset, face, face);
                // 가로 획 셋(위·가운데·아래), 세로 획 셋(왼쪽·가운데·오른쪽)
                foreach (int yy in new[] { cell.Top, cell.Top + (face - strokeH) / 2, cell.Bottom - strokeH })
                    Cv2.Rectangle(strokes, new Rect(cell.X, yy, face, strokeH), Scalar.White, -1);
                foreach (int xx in new[] { cell.Left, cell.Left + (face - strokeV) / 2, cell.Right - strokeV })
                    Cv2.Rectangle(strokes, new Rect(xx, cell.Y, strokeV, face), Scalar.White, -1);
                if (furigana && vertical)   // 본문 글자 오른쪽에 반 크기 글자 둘(2px 띄움)
                    for (int k = 0; k < 2; k++)
                        Cv2.Rectangle(strokes, new Rect(x + em + 2, y + k * (em / 2) + 2, em * 3 / 10, em * 3 / 10), Scalar.White, 2);
            }
            if (halo > 0)
            {
                using var around = new Mat();
                Cv2.Dilate(strokes, around, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * halo + 1, 2 * halo + 1)));
                Img.SetTo(haloColor ?? Scalar.White, around);
            }
            Img.SetTo(ink ?? Scalar.Black, strokes);
            // 감지기의 글 마스크 = 획 + 7×7 불림
            Mask = new Mat();
            Cv2.Dilate(strokes, Mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7)));
            Block = Cv2.BoundingRect(Mask);
        }
        public GlyphStats Measure(string text) => SourceGlyph.Measure(Img, Mask, Block, text);
        public void Dispose() { Img.Dispose(); Mask.Dispose(); }
    }

    private static string Text(int cells) => new('字', cells);

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(actual / expected - 1) <= tolerance, $"기대 {expected:0.0} ± {tolerance:P0}, 잰 값 {actual:0.0}");

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(64)]
    public void VerticalColumns_GiveTheCellSize(int em)
    {
        using var page = new Page(em, [6, 6, 4]);
        Near(em, page.Measure(Text(16)).Em, 0.08);
    }

    [Fact]
    public void AShortLastColumn_IsStillCounted()
    {
        // 글자 하나짜리 끝 기둥: 잉크 수의 윤곽이 다른 기둥의 1/7이라 골을 가르는 문턱(15%) 아래다 — 띠를 잉크가 있는 데까지 넓히지 않으면 그 기둥이 빠져 칸이 작게 재진다
        using var page = new Page(50, [7, 7, 1]);
        Near(50, page.Measure(Text(15)).Em, 0.05);
    }

    [Fact]
    public void LooselySetText_IsSizedByItsGlyphs_NotByTheCells()
    {
        // 글자를 벌려 놓은 글(실제 04쪽: 칸 56px에 글자 45px): 칸의 크기로 식자하면 원문 글자보다 커진다 — 줄 굵기에서 본 글자 크기(굵기 × 1.12)를 넘지 않는다
        using var page = new Page(60, [6, 6], faceShare: 75);
        double em = page.Measure(Text(12)).Em;
        Near(60 * 0.75 * 1.12, em, 0.05);
        Assert.True(em < 60 * 0.9, $"{em:0.0}");
    }

    [Fact]
    public void ASingleColumn_GivesTheCellSize()
    {
        // 기둥 하나: 글자 사이가 벌어져 가로로 보면 '줄'이 여럿으로 보인다 — 줄마다 글자 하나뿐이고 세로로 길면 기둥이다
        using var page = new Page(40, [7]);
        Near(40, page.Measure(Text(7)).Em, 0.08);
    }

    [Fact]
    public void HorizontalLines_GiveTheCellSize()
    {
        using var page = new Page(40, [8, 8, 5], vertical: false);
        Near(40, page.Measure(Text(21)).Em, 0.08);
    }

    [Fact]
    public void Furigana_DoesNotChangeTheSize()
    {
        // 후리가나(반 크기 글자의 좁은 띠)는 줄로 세지 않는다 — 세면 줄 굵기의 가운뎃값이 내려가거나 본문 줄이 굵어 보인다
        using var plain = new Page(48, [6, 6]);
        using var ruby = new Page(48, [6, 6], furigana: true);
        Near(plain.Measure(Text(12)).Em, ruby.Measure(Text(12)).Em, 0.05);
        Near(48, ruby.Measure(Text(12)).Em, 0.08);
        // 획도 본문 줄의 것만 잰다 — 후리가나의 가는 획이 섞이면 본문이 실제보다 가늘게 재진다
        Near(plain.Measure(Text(12)).Stroke, ruby.Measure(Text(12)).Stroke, 0.08);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OneOrTwoGlyphs_AreSizedFromTheGlyphItself(int cells)
    {
        using var page = new Page(50, [cells]);
        Near(50, page.Measure(Text(cells)).Em, 0.10);
    }

    [Fact]
    public void TwoGlyphsWithALongDotLeader_AreNotSizedByTheirSpread()
    {
        // "あ…"처럼 글자 하나 뒤에 길게 늘어선 점 — 칸 수(2)로 길이를 나누면 글자가 두 배로 재진다(실제 05쪽: 52px 글자가 89px로)
        using var page = new Page(50, [1]);
        for (int k = 0; k < 6; k++) Cv2.Circle(page.Img, new Point(page.Block.X + page.Block.Width / 2, page.Block.Bottom + 20 + k * 28), 4, Scalar.Black, -1);
        using var dots = new Mat(page.Img.Rows, page.Img.Cols, MatType.CV_8UC1, Scalar.Black);
        for (int k = 0; k < 6; k++) Cv2.Circle(dots, new Point(page.Block.X + page.Block.Width / 2, page.Block.Bottom + 20 + k * 28), 7, Scalar.White, -1);
        Cv2.BitwiseOr(page.Mask, dots, page.Mask);
        var block = Cv2.BoundingRect(page.Mask);
        Near(50, SourceGlyph.Measure(page.Img, page.Mask, block, "字…").Em, 0.12);
    }

    [Fact]
    public void WhiteTextOnBlack_IsMeasuredTheSame()
    {
        using var dark = new Page(40, [6, 6], paper: Scalar.Black, ink: Scalar.White);
        using var light = new Page(40, [6, 6]);
        var d = dark.Measure(Text(12));
        var l = light.Measure(Text(12));
        Near(40, d.Em, 0.08);
        // 획도 같은 값이어야 한다 — 글자 쪽을 거꾸로 고르면(검은 둘레를 글자로) 크기는 비슷하게 나와도 획은 딴판이다
        Near(l.Stroke, d.Stroke, 0.15);
        Near(l.Thick, d.Thick, 0.15);
    }

    [Fact]
    public void TextWithAWhiteRim_OnAToneBackground_IsMeasuredFromTheStrokes()
    {
        // 톤·그림 위의 흰 테 두른 글: 마스크 밖 바탕(회색)을 '종이'로 삼으면 흰 테가 글자로 읽혀 줄이 굵게 재진다(실제 26쪽 둘) — 마스크의 속(획)과 가장자리(테)를 견준다
        using var page = new Page(48, [5, 5], paper: new Scalar(120, 120, 120), halo: 6);
        var s = page.Measure(Text(10));
        Near(48, s.Em, 0.08);
        Assert.InRange(s.Stroke, 0.05, 0.13);   // 그린 획 4px ÷ 48 = 0.083 — 테(12px 넘는 덩어리)를 쟀다면 훨씬 크다
    }

    [Fact]
    public void AWrongCharacterCount_MakesItAbstain()
    {
        // 글자 읽기가 글자 수를 크게 틀렸다(같은 글자를 줄줄이 지어냈다) — 칸으로 잰 값과 줄 굵기가 어긋난다. 지어내지 않고 포기한다(쪽의 다른 블록 값을 따르게)
        using var page = new Page(40, [6, 6]);
        Assert.Equal(0, page.Measure(Text(24)).Em);
        Assert.Equal(0, page.Measure(Text(5)).Em);
        Assert.True(page.Measure(Text(12)).Em > 0);
    }

    [Fact]
    public void TextThatDoesNotStandOutFromItsSurround_IsNotMeasured()
    {
        using var faint = new Page(40, [6, 6], paper: new Scalar(128, 128, 128), ink: new Scalar(118, 118, 118));
        Assert.Equal(default, faint.Measure(Text(12)));
    }

    [Fact]
    public void AnEmptyTextOrAnEmptyMask_IsNotMeasured()
    {
        using var page = new Page(40, [6]);
        Assert.Equal(default, page.Measure(""));
        using var none = new Mat(page.Img.Rows, page.Img.Cols, MatType.CV_8UC1, Scalar.Black);
        Assert.Equal(default, SourceGlyph.Measure(page.Img, none, page.Block, Text(6)));
        // 그림 밖으로 걸친 상자도 던지지 않는다
        Assert.Equal(default, SourceGlyph.Measure(page.Img, none, new Rect(-50, -50, 10, 10), Text(6)));
    }

    [Fact]
    public void ThickerStrokes_GiveAHigherStrokeRatio()
    {
        using var thin = new Page(48, [5, 5], strokeH: 2, strokeV: 2);
        using var mid = new Page(48, [5, 5], strokeH: 4, strokeV: 4);
        using var thick = new Page(48, [5, 5], strokeH: 8, strokeV: 8);
        double a = thin.Measure(Text(10)).Stroke, b = mid.Measure(Text(10)).Stroke, c = thick.Measure(Text(10)).Stroke;
        Assert.True(a < b && b < c, $"{a:0.000} < {b:0.000} < {c:0.000}");
        Near(2.0 / 48, a, 0.35);
        Near(4.0 / 48, b, 0.35);
        Near(8.0 / 48, c, 0.35);
        // 굵은 획의 굵기(획 한가운데에서 잰 값의 위 1할)도 같은 차례다 — 획이 고른 글자에서는 고른 굵기와 같은 값
        double ta = thin.Measure(Text(10)).Thick, tb = mid.Measure(Text(10)).Thick, tc = thick.Measure(Text(10)).Thick;
        Assert.True(ta < tb && tb < tc, $"{ta:0.000} < {tb:0.000} < {tc:0.000}");
        Near(4.0 / 48, tb, 0.35);
        Near(8.0 / 48, tc, 0.35);
    }

    [Fact]
    public void Thick_SeesTheHeavyStrokes_WhereTheEvenStrokeWidthDoesNot()
    {
        // 굵기가 변하는 글꼴: 굵은 획 몇 줄 + 가는 획 여럿. 고른 굵기(넓이 ÷ 둘레)는 가는 획에 끌려 내려가지만 굵은 획의 굵기는 굵은 줄을 본다 —
        // 실제 쪽에서 만화 본문(굵기가 변하는 가나)과 가는 둥근 고딕을 가른 것이 이 값이다
        using var even = new Page(56, [5, 5], strokeH: 3, strokeV: 3);
        using var varied = new Page(56, [5, 5], strokeH: 3, strokeV: 9);
        var e = even.Measure(Text(10));
        var v = varied.Measure(Text(10));
        Assert.True(v.Thick > e.Thick * 2, $"{v.Thick:0.000} > 2 × {e.Thick:0.000}");
        Assert.True(v.Thick > v.Stroke * 1.3, $"{v.Thick:0.000} > 1.3 × {v.Stroke:0.000}");
    }

    [Fact]
    public void ThinHorizontalStrokes_ShowAsHighContrast_AndEvenStrokesDoNot()
    {
        // 명조: 가로 획이 가늘고 세로 획이 굵다. 고딕: 같다
        using var serif = new Page(56, [5, 5], strokeH: 2, strokeV: 7);
        using var sans = new Page(56, [5, 5], strokeH: 5, strokeV: 5);
        Assert.True(serif.Measure(Text(10)).Contrast >= 2.5, $"{serif.Measure(Text(10)).Contrast:0.00}");
        Assert.InRange(sans.Measure(Text(10)).Contrast, 0.8, 1.25);
    }

    [Theory]
    [InlineData("あいう", 3)]
    [InlineData("あ い\nう", 3)]          // 빈칸·줄바꿈은 칸이 아니다
    [InlineData("だめ!!", 3)]             // 이어진 !?는 둘에 한 칸(세로쓰기는 가로로 묶어 놓는다)
    [InlineData("え!?!", 3)]
    [InlineData("あと13日", 4)]           // 두 자리 수는 한 칸에 눕힌다
    [InlineData("100点", 4)]              // 세 자리부터는 자리마다
    [InlineData("…", 1)]
    [InlineData("", 0)]
    public void Cells_CountTheGridCellsTheTextOccupies(string text, int cells) => Assert.Equal(cells, SourceGlyph.Cells(text));

    // ── 식자 정보 ──

    [Fact]
    public void AnOldSidecarBlock_HasNoMeasuredValues()
    {
        var json = """{"X1":1,"Y1":2,"X2":30,"Y2":40,"SourceText":"原文","Translation":"번역 글","FontSize":18,"Vertical":true,"FontColorHex":"#000000"}""";
        var b = JsonSerializer.Deserialize<TextBlockInfo>(json)!;
        Assert.Equal((0, 0, 0, 0), (b.SrcEm, b.SrcStroke, b.SrcThick, b.SrcContrast));
        var round = JsonSerializer.Deserialize<TextBlockInfo>(JsonSerializer.Serialize(b with { SrcEm = 41.5, SrcStroke = 0.071, SrcThick = 0.13, SrcContrast = 2.2 }))!;
        Assert.Equal((41.5, 0.071, 0.13, 2.2), (round.SrcEm, round.SrcStroke, round.SrcThick, round.SrcContrast));
    }
}
