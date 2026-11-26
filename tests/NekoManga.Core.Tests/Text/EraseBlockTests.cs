using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>블록 하나의 글자 지우기(EmbeddedTranslator.EraseBlock) — 단색이면 칠하고, 아니면 흰 테를 더해 LaMa 마스크와(켜져 있으면) SDXL로 다시 그릴 마스크에 모은다.
/// 번역 쪽 루프(TranslatePageAsync)는 모델이 있어야 돌아서, 블록마다의 판단만 떼어 합성 쪽으로 붙잡는다(검수 2026-11-26).</summary>
public class EraseBlockTests
{
    private static void DrawStripes(Mat page, int pitch)
    {
        int rows = page.Rows, right = page.Cols - 1;
        for (int y = 0; y < rows; y += pitch)
            Cv2.Line(page, new Point(0, y), new Point(right, y), Scalar.Black, 1);
    }

    private static bool Same(Mat a, Mat b)
    {
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        using var flat = diff.Reshape(1);   // CountNonZero는 한 채널만 받는다
        return Cv2.CountNonZero(flat) == 0;
    }

    [Fact]
    public void EraseBlock_SolidBlock_IsJudgedOnTheStrokeMask_BeforeAnyHaloIsAdded()
    {
        // 흰 바탕의 글 옆(12px)에 먹칠한 면이 있다 — 테 판정의 바깥 띠가 덜 희어서 AddTextHalo는 둘레의 흰 픽셀을 마스크에 더하려 든다.
        // 단색 판정은 v1.18.0처럼 **획 마스크로 먼저** 한다: 불린 마스크로 판정하면 풍선 테두리에 닿아 단색으로 칠하던 풍선이 LaMa로(켜져 있으면 SDXL로) 넘어간다(검수 2026-11-26)
        using var page = SolidPage(500, 400);
        using var mask = EmptyMask(page);
        var glyphs = DrawGlyphs(page, mask, 150, 150, 4, 3);
        Cv2.Rectangle(page, new Rect(glyphs.Right + 12, glyphs.Y - 40, 120, glyphs.Height + 80), new Scalar(60, 60, 60), -1);
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(glyphs, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);
        using var regen = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regen, [rect]);

        Assert.Equal(EraseKind.Solid, r.Kind);
        Assert.False(r.RegenWanted);
        Assert.True(Same(erase, mask), "단색으로 칠한 블록에는 흰 테를 더하지 않는다");
        Assert.Equal(0, Cv2.CountNonZero(lama));
        Assert.Equal(0, Cv2.CountNonZero(texture));
        Assert.Equal(0, Cv2.CountNonZero(regen));
        Assert.True(page.At<Vec3b>(glyphs.Y + 7, glyphs.X + 7).Item0 > 200);   // 글자는 칠해졌다
    }

    /// <summary>망점 톤(4px 간격) 위에, 글 둘레를 margin px만큼 희게 비운 글 — margin 6은 획에 두른 흰 테(フチ), 10은 글 뒤에 깐 흰 판.</summary>
    private static (Mat Page, Mat Mask, Rect Glyphs) TextOnTone(int margin)
    {
        var page = SolidPage(500, 400);
        for (int y = 0; y < 400; y += 4)
            for (int x = 0; x < 500; x += 4)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 4 * 14 + 3 * 4, 3 * 14 + 2 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - margin, glyphs.Y - margin, glyphs.Width + 2 * margin, glyphs.Height + 2 * margin), Scalar.White, -1);
        DrawGlyphs(page, mask, 200, 150, 4, 3);
        DilateLikeDetector(mask);
        return (page, mask, glyphs);
    }

    [Fact]
    public void EraseBlock_ThinWhiteOutlineOnTone_GetsTheHalo_AndIsCollectedForLama()
    {
        // 획에 두른 흰 테(6px): 감지기 마스크(획 + 3px) 밖에 테가 3px 남는다 — 단색이 아니고(둘레가 톤), 테까지 메울 자리에 들어가야 흰 글자 모양 얼룩이 안 남는다
        var (page, mask, glyphs) = TextOnTone(margin: 6);
        using (page) using (mask)
        {
            var rect = RectOf(BlockOf(glyphs, 4));
            using var before = page.Clone();
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

            Assert.Equal(EraseKind.Texture, r.Kind);   // 둘레가 망점 톤이다 — 무늬로 메울 자리
            Assert.True(Same(page, before));   // 단색이 아닌 블록은 여기서 그림을 건드리지 않는다(메우기는 호출부가 뒤에 한다)
            Assert.True(Cv2.CountNonZero(erase) > Cv2.CountNonZero(mask));   // 테가 지울 마스크에 들어갔다
            Assert.Equal(0, mask.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));
            Assert.Equal(255, texture.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));   // 테의 바깥 끝(글에서 6px)까지 메울 자리
            Assert.Equal(0, Cv2.CountNonZero(lama));
        }
    }

    [Fact]
    public void EraseBlock_AnotherTextWithItsOwnWhiteRimNearby_IsNeitherRimmedNorCollected()
    {
        // 이 블록의 창(1.15배) 밖, 흰 테를 재는 창 안에 다른 글(상자로 안 잡힌 효과음)이 있다: 그 글의 흰 테까지 지울 마스크에 더하고 구멍을 모으는 창을 거기까지 넓히면
        // 남의 글이 반쯤 지워진다(실제 05쪽 — 칸 테두리 너머 다른 칸의 "ん"이 반 토막 났다, 2026-11-27). 흰 테는 제 획에만 두르고, 창 밖에서는 제 흰 테만 모은다
        var (page, mask, glyphs) = TextOnTone(margin: 6);
        using (page) using (mask)
        {
            // 옆 글: 글에서 22px 오른쪽의 세로 한 줄(흰 테 6px — 글에서 16px부터) — 감지기 마스크에만 있다. 이 블록의 창은 글에서 9px, 제 흰 테는 11px까지, 흰 테를 재는 창은 30px까지다
            var other = new Rect(glyphs.Right + 22, glyphs.Y, 14, 3 * 14 + 2 * 4);
            Cv2.Rectangle(page, new Rect(other.X - 6, other.Y - 6, other.Width + 12, other.Height + 12), Scalar.White, -1);
            using (var otherMask = EmptyMask(page))
            {
                DrawGlyphs(page, otherMask, other.X, other.Y, 1, 3);
                DilateLikeDetector(otherMask);
                Cv2.BitwiseOr(mask, otherMask, mask);
            }
            var rect = RectOf(BlockOf(glyphs, 4));
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

            Assert.Equal(EraseKind.Texture, r.Kind);
            Assert.Equal(255, texture.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));   // 제 흰 테는 전처럼 든다
            var beside = new Rect(other.X - 6, 0, page.Cols - (other.X - 6), page.Rows);       // 옆 글과 그 흰 테의 자리
            using var eraseBeside = new Mat(erase, beside); using var maskBeside = new Mat(mask, beside);
            Assert.True(Same(eraseBeside, maskBeside), "옆 글에 흰 테를 둘렀다");
            using var textureBeside = new Mat(texture, beside); using var lamaBeside = new Mat(lama, beside);
            Assert.Equal(0, Cv2.CountNonZero(textureBeside));
            Assert.Equal(0, Cv2.CountNonZero(lamaBeside));
        }
    }

    [Fact]
    public void EraseBlock_ForeignStrokeInsideTheWidenedArea_IsNotCollected()
    {
        // 흰 테에 붙은 흰 틈이 창(1.15배) 밖으로 26px 나간다 — 구멍을 모으는 자리가 그만큼 넓어진다. 넓어진 자리(네모)에 든 남의 획(상자로 안 잡힌 효과음)은 이 블록의 구멍이 아니다:
        // 창 밖에서는 제 흰 테·틈만 모은다(넓어진 네모를 통째로 모으면 옆의 효과음이 걸린 만큼 지워진다, 2026-11-27)
        var (page, mask, glyphs) = TextOnTone(margin: 6);
        using (page) using (mask)
        {
            var bulge = new Rect(glyphs.Right + 6, glyphs.Y + 15, 21, 20);   // 흰 테에 이어진 흰 틈(글에서 26px까지)
            page.Rectangle(bulge, Scalar.White, -1);
            var foreign = new Rect(glyphs.Right + 15, glyphs.Y + 40, 10, 10);   // 톤 위의 남의 획 — 창(글에서 9px) 밖, 넓어진 자리 안
            page.Rectangle(foreign, Scalar.Black, -1);
            using (var foreignMask = EmptyMask(page))
            {
                foreignMask.Rectangle(foreign, Scalar.White, -1);
                DilateLikeDetector(foreignMask);
                Cv2.BitwiseOr(mask, foreignMask, mask);
            }
            var rect = RectOf(BlockOf(glyphs, 4));
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);
            var jobs = new List<(Rect Area, Mat Own)>();
            try
            {
                var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect], jobs);

                Assert.Equal(EraseKind.Texture, r.Kind);
                Assert.Equal(255, texture.At<byte>(bulge.Y + 10, bulge.Right - 4));   // 창 밖의 제 흰 틈은 든다
                var around = new Rect(foreign.X - 3, foreign.Y - 3, foreign.Width + 6, foreign.Height + 6);
                using var textureThere = new Mat(texture, around); using var lamaThere = new Mat(lama, around);
                Assert.Equal(0, Cv2.CountNonZero(textureThere));
                Assert.Equal(0, Cv2.CountNonZero(lamaThere));
                var (area, own) = Assert.Single(jobs);
                Assert.True(area.Contains(new Point(foreign.X + 5, foreign.Y + 5)), "시험이 보려는 자리가 넓어진 자리 안이 아니다");
                Assert.Equal(0, own.At<byte>(foreign.Y + 5 - area.Y, foreign.X + 5 - area.X));
            }
            finally { foreach (var j in jobs) j.Own.Dispose(); }
        }
    }

    [Fact]
    public void EraseBlock_WhiteUnderANeighbouringTextsMask_IsLeftToThatText()
    {
        // 옆 글이 바짝 붙어 있다(글에서 13px — 그 글의 마스크는 10px부터): 이 블록의 흰 테가 닿는 자리(획에서 8px = 글에서 11px)에 옆 글 마스크 밑의 흰 픽셀이 있다.
        // 그것까지 제 흰 테로 모으면 옆 글의 가장자리가 이 블록의 구멍에 들어가 메워진다 — 남의 글자 자리는 그 글의 것이다
        var (page, mask, glyphs) = TextOnTone(margin: 6);
        using (page) using (mask)
        {
            var other = new Rect(glyphs.Right + 13, glyphs.Y, 14, 3 * 14 + 2 * 4);
            Cv2.Rectangle(page, new Rect(other.X - 6, other.Y - 6, other.Width + 12, other.Height + 12), Scalar.White, -1);
            using var otherMask = EmptyMask(page);
            DrawGlyphs(page, otherMask, other.X, other.Y, 1, 3);
            DilateLikeDetector(otherMask);
            Cv2.BitwiseOr(mask, otherMask, mask);
            var rect = RectOf(BlockOf(glyphs, 4));
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

            Assert.Equal(EraseKind.Texture, r.Kind);
            Assert.Equal(255, texture.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));   // 제 흰 테는 든다
            using var overlap = new Mat();
            Cv2.BitwiseAnd(texture, otherMask, overlap);
            Assert.Equal(0, Cv2.CountNonZero(overlap));
        }
    }

    [Fact]
    public void EraseBlock_StrokesOfANeighbouringBlockInsideTheWindow_GetNoRimFromThisBlock()
    {
        // 옆 블록(흰 판 위의 글)의 획이 이 블록의 창(1.15배)에 걸쳐 들어와 있다: 그 획까지 '제 획'으로 보고 흰 테를 두르면, 옆 블록의 흰 판이 이 블록의 구멍에 들어가
        // 톤으로 메워진다(옆 블록의 상자 밖 부분 — 상자 안은 EraseBlock이 뺀다). 흰 테는 다른 블록의 상자에 든 획에는 두르지 않는다(검토 2026-11-27)
        // 이 블록: 톤 위의 큰 글 덩어리(176×104, 흰 테 6px) — 창(1.15배)은 글에서 좌우 17px. 옆 블록: 글에서 15px 오른쪽, 테두리 있는 흰 풍선 안의 세로 한 줄(마스크는 12px부터 — 창 안)
        var page = SolidPage(700, 500);
        for (int y = 0; y < 500; y += 4)
            for (int x = 0; x < 700; x += 4)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        var mask = EmptyMask(page);
        var glyphs = new Rect(200, 150, 10 * 14 + 9 * 4, 6 * 14 + 5 * 4);
        Cv2.Rectangle(page, new Rect(glyphs.X - 6, glyphs.Y - 6, glyphs.Width + 12, glyphs.Height + 12), Scalar.White, -1);
        using (page) using (mask)
        {
            var other = new Rect(glyphs.Right + 15, glyphs.Y + 20, 14, 3 * 14 + 2 * 4);
            DrawBox(page, new Rect(other.X - 4, other.Y - 30, other.Width + 34, other.Height + 60), border: 2);   // 옆 블록의 흰 풍선(안쪽이 글에서 11px부터)
            DrawGlyphs(page, mask, glyphs.X, glyphs.Y, 10, 6);
            DrawGlyphs(page, mask, other.X, other.Y, 1, 3);
            DilateLikeDetector(mask);
            var rect = RectOf(BlockOf(glyphs, 4)); var otherRect = RectOf(BlockOf(other, 4));
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect, otherRect]);

            Assert.Equal(EraseKind.Texture, r.Kind);
            Assert.Equal(255, texture.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));   // 제 흰 테는 든다
            // 제 획에서 8px(= 글에서 11px) 너머에는 이 블록의 구멍이 없다 — 옆 블록의 흰 판은 그 블록의 일이다
            var beyond = new Rect(glyphs.Right + 12, 0, page.Cols - (glyphs.Right + 12), page.Rows);
            using var textureBeyond = new Mat(texture, beyond);
            Assert.Equal(0, Cv2.CountNonZero(textureBeyond));
        }
    }

    [Fact]
    public void EraseBlock_AnotherBlocksBoxOverlappingThisOne_StillRimsItsOwnStrokesThere()
    {
        // 옆 블록의 상자가 이 블록의 상자와 겹친다(감지기 상자는 겹치기도 한다): 겹친 자리의 획은 이 블록의 것이기도 하다 — 남의 것으로 치면 그쪽 흰 테가 빠져 한쪽에만 테가 남는다
        var (page, mask, glyphs) = TextOnTone(margin: 6);
        using (page) using (mask)
        {
            var rect = RectOf(BlockOf(glyphs, 4));
            var overlapping = new Rect(glyphs.Right - 20, glyphs.Y - 30, 120, glyphs.Height + 60);   // 오른쪽 글자 한 줄을 덮는 옆 블록의 상자
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect, overlapping]);

            Assert.Equal(EraseKind.Texture, r.Kind);
            Assert.Equal(255, erase.At<byte>(glyphs.Y + glyphs.Height / 2, glyphs.X - 6));
            // 오른쪽 글자 줄의 획에서 8px 안(글에서 7~10px — 흰 판 6px의 바깥, 톤의 흰 틈)도 지울 자리에 들었다. 그 줄을 남의 획으로 치면 여기는 닿지 않는다
            // (흰 판의 가장자리 자체는 '갇힌 흰 틈'으로도 들어오므로 그 바깥을 본다)
            using var beyondThePlate = new Mat(erase, new Rect(glyphs.Right + 7, glyphs.Y, 4, glyphs.Height));
            Assert.True(Cv2.CountNonZero(beyondThePlate) > 0);
        }
    }

    [Fact]
    public void EraseBlock_InkOfNeighbouringTextInTheSurround_IsNotReadAsAWhiteRimEnding()
    {
        // 흰 종이 위의 글(둘레 띠에 옅은 선이 걸려 단색은 아니다) 양옆에 다른 글이 있다: 흰 테를 재는 띠에 그 글의 검은 획이 들어와 '바로 밖은 희고 그 너머는 덜 희다'가 된다 —
        // 흰 테가 아니다(둘레가 다 흰 종이다). 다른 글의 자리는 세지 않는다. 세면 획 둘레 8px의 흰 종이가 구멍에 들어간다(LaMa 구멍만 커진다)
        using var page = SolidPage(600, 400);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 250, 150, 4, 3);
        Cv2.Line(page, new Point(a.X - 20, a.Y - 14), new Point(a.Right + 20, a.Y - 14), new Scalar(120, 120, 120), 2);
        Cv2.Line(page, new Point(a.X - 20, a.Bottom + 14), new Point(a.Right + 20, a.Bottom + 14), new Scalar(120, 120, 120), 2);
        DrawGlyphs(page, mask, a.Right + 19, a.Y - 10, 1, 2, glyph: 34, gap: 2);   // 오른쪽의 큰 글자 둘(글에서 19px)
        DrawGlyphs(page, mask, a.X - 19 - 34, a.Y - 10, 1, 2, glyph: 34, gap: 2);  // 왼쪽에도
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(a, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

        Assert.Equal(EraseKind.Lama, r.Kind);
        Assert.True(Same(erase, mask), "흰 종이를 흰 테로 더했다");
    }

    [Fact]
    public void EraseBlock_TextOnAWhitePlate_IsSolidFilled_AndThePlateStays()
    {
        // 글 뒤에 깐 흰 판(테두리 없는 흰 말풍선·내레이션 판, 여백 10px): 판 안이 단색이라 획만 칠한다 — 판은 그림의 일부다.
        // 흰 테를 먼저 더하던 순서에서는 판 전체가 지울 마스크에 들어가 LaMa가 판을 톤으로 메웠다(바깥 띠가 톤이라 '테'로 읽힌다)
        var (page, mask, glyphs) = TextOnTone(margin: 10);
        using (page) using (mask)
        {
            var rect = RectOf(BlockOf(glyphs, 4));
            using var erase = mask.Clone();
            using var lama = EmptyMask(page);
            using var texture = EmptyMask(page);
            using var regen = EmptyMask(page);

            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regen, [rect]);

            Assert.Equal(EraseKind.Solid, r.Kind);
            Assert.True(Same(erase, mask));
            Assert.Equal(0, Cv2.CountNonZero(lama));
            Assert.Equal(0, Cv2.CountNonZero(texture));
            Assert.Equal(0, Cv2.CountNonZero(regen));
            Assert.Equal(new Vec3b(255, 255, 255), page.At<Vec3b>(glyphs.Y - 6, glyphs.X - 6));   // 판은 그대로 희다
            Assert.True(page.At<Vec3b>(glyphs.Y + 7, glyphs.X + 7).Item0 > 200);                  // 글자는 칠해졌다
        }
    }

    [Fact]
    public void EraseBlock_WantedBlock_KeepsTheNeighboursStrokesOutOfTheRegenMask()
    {
        // 블록 창(1.15배)에는 옆 블록의 획이 걸쳐 들어온다 — LaMa 마스크는 전처럼 그것까지 모으지만(기존 동작), SDXL로 다시 그릴 구멍에는 이 블록의 획만 넣는다:
        // 옆 블록이 단색으로 칠한 흰 풍선이면 그 풍선의 한 줄이 디노이즈 1.0으로 다시 그려진다(검수 2026-11-26). 판정(RegenStats)이 남의 글자를 빼고 재는 것과 같은 기준
        using var page = SolidPage(500, 400);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 150, 150, 4, 3);
        var b = DrawGlyphs(page, mask, a.Right + 8, 150, 4, 3);
        DilateLikeDetector(mask);
        var rectA = RectOf(BlockOf(a, 4)); var rectB = RectOf(BlockOf(b, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);
        using var regen = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rectA, lama, texture, regen, [rectA, rectB]);

        Assert.Equal(EraseKind.Texture, r.Kind);
        Assert.True(r.RegenWanted);
        Assert.Equal(255, regen.At<byte>(a.Y + 7, a.X + 7));     // 제 획은 들어 있다
        Assert.Equal(255, texture.At<byte>(a.Y + 7, a.X + 7));
        using (var inB = new Mat(regen, rectB)) Assert.Equal(0, Cv2.CountNonZero(inB));
        using (var inB = new Mat(texture, rectB)) Assert.Equal(0, Cv2.CountNonZero(inB));   // 무늬로 메울 자리도 제 획만 — 옆 블록은 제 차례에 지운다
        Assert.Equal(0, Cv2.CountNonZero(lama));
    }

    [Fact]
    public void EraseBlock_TextureFillOff_CollectsTheWholeWindowForLama_AsBefore()
    {
        // 무늬 메우기를 끈 설정(EraseTextureFill=false): 전처럼 블록 창 안의 획을 전부(옆 블록 것까지) LaMa 마스크에 모은다 — v1.18.0과 같은 동작
        using var page = SolidPage(500, 400);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 150, 150, 4, 3);
        var b = DrawGlyphs(page, mask, a.Right + 8, 150, 4, 3);
        DilateLikeDetector(mask);
        var rectA = RectOf(BlockOf(a, 4)); var rectB = RectOf(BlockOf(b, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rectA, lama, textureMask: null, regenMask: null, [rectA, rectB]);

        Assert.Equal(EraseKind.Lama, r.Kind);
        Assert.Equal(255, lama.At<byte>(a.Y + 7, a.X + 7));
        using var lamaInB = new Mat(lama, rectB);
        Assert.True(Cv2.CountNonZero(lamaInB) > 0);
    }

    [Fact]
    public void EraseBlock_NotSolidButFlatAroundTheHole_GoesToLama()
    {
        // 단색 판정의 띠(15px)에는 옅은 선이 걸리지만 구멍 바로 밖(2~8px)은 흰 종이다 — 무늬가 없으니 LaMa가 맡는다(무늬 메우기는 가져올 무늬가 있을 때만)
        using var page = SolidPage(500, 400);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 150, 150, 4, 3);
        Cv2.Line(page, new Point(a.X - 20, a.Y - 14), new Point(a.Right + 20, a.Y - 14), new Scalar(120, 120, 120), 2);
        Cv2.Line(page, new Point(a.X - 20, a.Bottom + 14), new Point(a.Right + 20, a.Bottom + 14), new Scalar(120, 120, 120), 2);
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(a, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

        Assert.Equal(EraseKind.Lama, r.Kind);
        Assert.False(r.RegenWanted);
        Assert.Equal(255, lama.At<byte>(a.Y + 7, a.X + 7));
        Assert.Equal(0, Cv2.CountNonZero(texture));
    }

    [Fact]
    public void EraseBlock_TextCrowdingAnOutlinedWhiteBubble_GoesToLama_WithTheStrokesOnly()
    {
        // 글이 테두리에 닿을 만큼 꽉 찬 흰 풍선: 윤곽이 마스크를 다 못 덮어 단색 칠하기는 못 한다(v1.18.0도 LaMa로 갔다). 그 뒤가 달라지면 안 된다 —
        // 흰 테로 읽어 풍선 안쪽을 구멍에 넣지 않고, 테두리가 고리에 들어왔다고 무늬 메우기·SDXL로 보내지도 않는다(검토 2026-11-27)
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 46, 35);   // 안쪽 92×70
        var glyphs = DrawGlyphs(page, mask, 276, 225, 4, 3);   // 오른쪽으로 치우쳐 마스크가 테두리에 걸친다(오른쪽 끝 x=343, 테두리 안쪽 끝 x=345)
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(glyphs, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);
        using var regen = EmptyMask(page);
        using var before = page.Clone();

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regen, [rect]);

        Assert.Equal(EraseKind.Lama, r.Kind);
        Assert.False(r.RegenWanted);
        Assert.True(Same(erase, mask), "풍선 안쪽의 흰 자리를 지울 자리에 더했다");
        Assert.True(Same(lama, mask), "LaMa 마스크는 획뿐이어야 한다");
        Assert.Equal(0, Cv2.CountNonZero(texture));
        Assert.Equal(0, Cv2.CountNonZero(regen));
        Assert.True(Same(page, before));
    }

    [Fact]
    public void EraseBlock_TextOverflowingAnOutlinedWhiteBubble_GoesToLama_WithTheStrokesOnly()
    {
        // 글이 풍선의 오른쪽 테두리 밖으로 넘쳤다: 넘친 자리의 마스크는 풍선 밖의 바탕에 닿는다 — 그래도 풍선이다(흰 테로 읽지 않고, 구멍 둘레도 풍선 안쪽에서만 잰다).
        // 양옆으로 다 넘쳐 글 바로 밖 띠의 절반 넘게가 풍선 밖인 경우는 BubbleSolverTests의 DrawnOutlineInterior_TextWiderThanTheBubble_StillFindsTheInterior
        using var page = SolidPage(600, 500, 128);
        using var mask = EmptyMask(page);
        DrawBubble(page, 300, 250, 46, 35);
        var glyphs = DrawGlyphs(page, mask, 288, 225, 4, 3);   // 오른쪽 끝 355 — 풍선 테두리(346~349) 밖으로 나간다
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(glyphs, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

        Assert.True(Same(erase, mask), "풍선 안쪽의 흰 자리를 지울 자리에 더했다");
    }

    [Fact]
    public void EraseBlock_NarrowOutlinedColumn_TakesTheWholeRimIntoTheHole()
    {
        // 폭 36px의 세로 한 줄(감지기 상자는 글에서 2px 밖): 1.15배 창은 좌우로 3px뿐이라, 획에서 8px까지인 흰 테의 바깥쪽이 창 밖에 남았다 —
        // 지울 마스크에는 들었는데(메울 재료에서 빠진다) 구멍에는 안 들어 글 양옆에 1~3px짜리 흰 띠가 남는다(검토 2026-11-27). 구멍을 모으는 창은 흰 테가 닿는 데까지 넓힌다
        using var page = SolidPage(400, 500);
        for (int y = 0; y < 500; y += 4)
            for (int x = 0; x < 400; x += 4)
                Cv2.Circle(page, new Point(x, y), 1, new Scalar(70, 70, 70), -1);
        using var mask = EmptyMask(page);
        var glyphs = new Rect(180, 120, 36, 5 * 36 + 4 * 22);
        for (int i = 0; i < 5; i++)   // 글자마다 두른 흰 테 8px — 글자 사이(22px)에는 톤이 6px 보인다(한 덩어리의 흰 판이면 단색 풍선으로 칠해진다)
            Cv2.Rectangle(page, new Rect(glyphs.X - 8, glyphs.Y + i * 58 - 8, 36 + 16, 36 + 16), Scalar.White, -1);
        DrawGlyphs(page, mask, glyphs.X, glyphs.Y, 1, 5, glyph: 36, gap: 22);
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(glyphs, 2));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);
        var jobs = new List<(Rect Area, Mat Own)>();
        try
        {
            var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect], jobs);

            Assert.Equal(EraseKind.Texture, r.Kind);
            int cy = glyphs.Y + glyphs.Height / 2;
            foreach (int x in new[] { glyphs.X - 7, glyphs.X - 5, glyphs.Right + 4, glyphs.Right + 6 })
                Assert.Equal(255, texture.At<byte>(cy, x));
            Assert.True(Same(texture, erase), "지울 마스크에 든 흰 테가 전부 구멍에 들어야 한다");
            // 메울 일감에도 같은 구멍이 든다(창 = 구멍이 닿는 데까지)
            var (area, own) = Assert.Single(jobs);
            Assert.True(area.Contains(new Point(glyphs.X - 7, cy)) && area.Contains(new Point(glyphs.Right + 6, cy)));
            using var whole = EmptyMask(page);
            using (var dst = new Mat(whole, area)) own.CopyTo(dst);
            Assert.True(Same(whole, texture));
        }
        finally { foreach (var j in jobs) j.Own.Dispose(); }
    }

    [Fact]
    public void EraseBlock_OverlappingBlocks_KeepTheirOwnRect()
    {
        // 다른 블록의 자리와 겹친 부분도 제 자리 안이면 제 획이다 — 겹쳤다고 둘 다에서 빠지면 그 획은 어디에서도 다시 그려지지 않는다
        using var page = SolidPage(500, 400);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 150, 150, 4, 3);
        DilateLikeDetector(mask);
        var rectA = RectOf(BlockOf(a, 4));
        var overlapping = new Rect(rectA.X + rectA.Width / 2, rectA.Y, rectA.Width, rectA.Height);
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);
        using var regen = EmptyMask(page);

        EmbeddedTranslator.EraseBlock(page, mask, erase, rectA, lama, texture, regen, [rectA, overlapping]);

        Assert.Equal(255, regen.At<byte>(a.Y + 7, a.Right - 7));   // 겹친 쪽(오른쪽 반)의 제 획
    }

    [Fact]
    public void EraseBlock_ReportsTheRimNumbers_EvenWithoutARegenMask()
    {
        // 둘레 수치는 단색이 아닌 블록마다 잰다(무늬로 메울지 LaMa로 갈지를 그것으로 가른다) — NEKO_TEXTREGEN_DBG가 그대로 적는다
        using var page = SolidPage(500, 400);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        var a = DrawGlyphs(page, mask, 150, 150, 4, 3);
        DilateLikeDetector(mask);
        var rect = RectOf(BlockOf(a, 4));
        using var erase = mask.Clone();
        using var lama = EmptyMask(page);
        using var texture = EmptyMask(page);

        var r = EmbeddedTranslator.EraseBlock(page, mask, erase, rect, lama, texture, regenMask: null, [rect]);

        Assert.True(r.RegenWanted);
        Assert.InRange(r.White, 0.6, 0.85);
    }

    // ── 쪽 단위로 잇는 두 대목(번역 쪽 루프에서 떼어 낸 것 — 모델 없이 본다, 검토 2026-11-27) ──

    [Fact]
    public void PrepareStrokes_ClearsTheKeptBlocks_ThenAddsWhatTheDetectorMissedInTheErasedOnes()
    {
        using var page = SolidPage(400, 300);
        DrawStripes(page, 4);
        using var mask = EmptyMask(page);
        // 지울 블록: 세로 한 줄 여섯 글자 가운데 위의 셋만 마스크에 있다
        var cells = new List<Rect>();
        for (int i = 0; i < 6; i++)
        {
            var cell = new Rect(140, 60 + i * 18, 14, 14);
            page.Rectangle(cell, Scalar.Black, -1);
            if (i < 3) mask.Rectangle(cell, Scalar.White, -1);
            cells.Add(cell);
        }
        var erased = new Rect(134, 54, 26, 116);
        // 그대로 둘 블록(못 옮긴 효과음): 마스크에 통째로 있다
        var keptGlyphs = DrawGlyphs(page, mask, 260, 80, 2, 2);
        var kept = RectOf(BlockOf(keptGlyphs, 4));
        DilateLikeDetector(mask);
        using var maskBefore = mask.Clone();

        using var strokes = EmbeddedTranslator.PrepareStrokes(page, mask, [kept], [erased]);

        Assert.True(Same(mask, maskBefore));   // 감지기 마스크는 그대로다(글자 색은 이것으로 잰다)
        using (var keptPart = new Mat(strokes, kept)) Assert.Equal(0, Cv2.CountNonZero(keptPart));
        foreach (var c in cells) Assert.Equal(255, strokes.At<byte>(c.Y + 7, c.X + 7));
    }

    [Fact]
    public void FillTextures_JobThatCannotBeFilled_HandsOnlyItsOwnStrokesToLama()
    {
        // 못 메운 일감(구멍이 너무 크다)은 LaMa로 넘긴다 — 제 구멍만. 창에 걸쳐 들어온 옆 블록의 획은 그 블록이 제 일감에서 이미 메웠다
        using var page = new Mat(700, 900, MatType.CV_8UC3, Scalar.White);
        for (int y = 0; y < 700; y += 6) page.Line(0, y, 899, y, Scalar.Black, 1);
        page.Rectangle(new Rect(0, 0, 900, 200), Scalar.Black, -1);
        using var original = page.Clone();
        var big = new Rect(150, 100, 500, 420);     // 21만 px — 못 메운다
        var small = new Rect(700, 300, 60, 80);     // 줄무늬 위의 작은 구멍
        page.Rectangle(big, new Scalar(128, 128, 128), -1);
        page.Rectangle(small, new Scalar(128, 128, 128), -1);
        using var avoid = EmptyMask(page);
        avoid.Rectangle(big, Scalar.White, -1); avoid.Rectangle(small, Scalar.White, -1);
        var areaBig = new Rect(100, 50, 700, 520);   // 작은 블록의 획까지 품는 창
        using var ownBig = new Mat(areaBig.Height, areaBig.Width, MatType.CV_8UC1, Scalar.Black);
        ownBig.Rectangle(new Rect(big.X - areaBig.X, big.Y - areaBig.Y, big.Width, big.Height), Scalar.White, -1);
        var areaSmall = new Rect(690, 290, 80, 100);
        using var ownSmall = new Mat(areaSmall.Height, areaSmall.Width, MatType.CV_8UC1, Scalar.Black);
        ownSmall.Rectangle(new Rect(small.X - areaSmall.X, small.Y - areaSmall.Y, small.Width, small.Height), Scalar.White, -1);
        using var lama = EmptyMask(page);

        bool needLama = EmbeddedTranslator.FillTextures(page, [(areaBig, ownBig), (areaSmall, ownSmall)], avoid, lama);

        Assert.True(needLama);
        using var expected = EmptyMask(page);
        expected.Rectangle(big, Scalar.White, -1);
        Assert.True(Same(lama, expected), "LaMa 마스크에는 못 메운 블록의 구멍만 든다");
        using var smallNow = new Mat(page, small); using var smallThen = new Mat(original, small);
        Assert.True(Same(smallNow, smallThen), "작은 블록은 줄무늬로 메워졌다");
    }
}
