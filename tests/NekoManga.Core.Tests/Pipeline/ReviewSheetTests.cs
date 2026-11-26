using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;

namespace NekoManga.Core.Tests;

/// <summary>번역 검수 표(CSV, 2026-11-26 사용자 "csv로 내보내서 한글을 고친 다음 다시 넣으면 일괄 수정") — 내보낸 표를 엑셀에서 고쳐 들여오면
/// 결과 파일·번호로 블록을 찾아 번역만 바꾼다. 쉼표·따옴표·줄바꿈이 든 글과 엑셀이 붙이는 BOM·CRLF를 그대로 견뎌야 한다.</summary>
public class ReviewSheetTests
{
    private static TextBlockInfo B(string src, string tr, bool sfx = false, bool hidden = false) =>
        new(0, 0, 10, 10, src, tr, 20, false, "#000000", Sfx: sfx, Hidden: hidden);

    private const string Out1 = @"C:\만화\vol1_neko\01_jpg.png";
    private const string Out2 = @"C:\a,b\02.png";   // 경로에 쉼표

    [Fact]
    public void Write_ThenParse_RoundTripsCommasQuotesNewlinesAndOrder()
    {
        var pages = new List<(string OutPath, IReadOnlyList<TextBlockInfo> Blocks)>
        {
            (Out1, [B("こんにちは、世界", "안녕, \"세계\""), B("二行目\nです", "둘째 줄\n이다")]),
            (Out2, [B("ドン", "쿵", sfx: true)]),
        };

        var csv = ReviewSheet.Write(pages);
        var rows = ReviewSheet.Parse("\uFEFF" + csv);   // 엑셀이 붙이는 BOM도 받는다

        Assert.StartsWith("결과 파일,번호,원문,번역", csv);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new ReviewSheet.Row(Out1, 1, "こんにちは、世界", "안녕, \"세계\""), rows[0]);
        Assert.Equal(new ReviewSheet.Row(Out1, 2, "二行目\nです", "둘째 줄\n이다"), rows[1]);
        Assert.Equal(new ReviewSheet.Row(Out2, 1, "ドン", "쿵"), rows[2]);
    }

    [Fact]
    public void Parse_ExcelStyle_CrlfRecordsAndCrlfInsideQuotes_AreNormalized()
    {
        const string csv = "결과 파일,번호,원문,번역\r\n\"C:\\x\\01.png\",1,\"원\",\"첫\r\n둘\"\r\n\"C:\\x\\01.png\",2,원2,번2\r\n";
        var rows = ReviewSheet.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("첫\n둘", rows[0].Translation);   // 칸 안의 줄바꿈은 LF로
        Assert.Equal(new ReviewSheet.Row(@"C:\x\01.png", 2, "원2", "번2"), rows[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("결과 파일,번호,원문,번역\r\n")]
    [InlineData("결과 파일,번호,원문,번역\n\n\n")]
    public void Parse_HeaderOnlyOrBlank_IsEmpty(string csv)
    {
        Assert.Empty(ReviewSheet.Parse(csv));
    }

    [Fact]
    public void Parse_SkipsRowsWithoutANumberOrTooFewColumns()
    {
        const string csv = "결과 파일,번호,원문,번역\nC:\\x\\01.png,abc,원,번\nC:\\x\\01.png,2\nC:\\x\\01.png,3,원3,번3\n";
        var rows = ReviewSheet.Parse(csv);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Index);
    }

    [Fact]
    public void Apply_ChangesOnlyListedTranslations_KeepsEveryOtherField()
    {
        var blocks = new List<TextBlockInfo> { B("a", "가", sfx: true), B("b", "나", hidden: true), B("c", "다") };
        var rows = new[]
        {
            new ReviewSheet.Row(Out1, 1, "a", "가 고침"),
            new ReviewSheet.Row(Out1, 2, "b", "나"),        // 같은 글 — 안 바뀐 것으로 센다
            new ReviewSheet.Row(Out1, 3, "c", "   "),       // 빈 번역은 무시(실수로 지운 칸이 글을 없애지 않게)
            new ReviewSheet.Row(Out1, 9, "?", "없는 번호"),
        };

        var (applied, changed, mismatched) = ReviewSheet.Apply(blocks, rows);

        Assert.Equal(1, changed);
        Assert.Equal(0, mismatched);
        Assert.Equal(3, applied.Count);
        Assert.Equal("가 고침", applied[0].Translation);
        Assert.True(applied[0].Sfx);                  // 효과음 표시 그대로
        Assert.Equal(blocks[1], applied[1]);          // 숨김 포함 그대로
        Assert.Equal("다", applied[2].Translation);
    }

    [Fact]
    public void Apply_NormalizesWindowsNewlines_AndTrimsEnds()
    {
        var (applied, changed, _) = ReviewSheet.Apply([B("a", "가")], [new ReviewSheet.Row(Out1, 1, "a", " 첫\r\n둘 ")]);
        Assert.Equal(1, changed);
        Assert.Equal("첫\n둘", applied[0].Translation);
    }

    [Fact]
    public void Apply_SkipsRowsWhoseSourceDoesNotMatchTheBlock()
    {
        // 결과 창에서 블록을 넣고 뺀 뒤에는 번호가 밀린다 — 원문이 다르면 엉뚱한 풍선을 덮지 않는다(검수 2026-11-26). 표의 원문이 비면(새 글) 번호만 본다
        var blocks = new List<TextBlockInfo> { B("a", "가"), B("b", "나") };
        var (applied, changed, mismatched) = ReviewSheet.Apply(blocks, [new ReviewSheet.Row(Out1, 1, "b", "고침"), new ReviewSheet.Row(Out1, 2, "", "원문 없는 줄은 맞춘다")]);
        Assert.Equal(1, mismatched);
        Assert.Equal(1, changed);
        Assert.Equal("가", applied[0].Translation);
        Assert.Equal("원문 없는 줄은 맞춘다", applied[1].Translation);
    }

    [Fact]
    public void Apply_SidecarWithCrlf_UnchangedRowIsNotCounted()
    {
        var (applied, changed, _) = ReviewSheet.Apply([B("a", "첫\r\n둘")], [new ReviewSheet.Row(Out1, 1, "a", "첫\n둘")]);
        Assert.Equal(0, changed);
        Assert.Equal("첫\r\n둘", applied[0].Translation);   // 손대지 않는다
    }

    [Fact]
    public void Parse_HeaderlessOrCrOnly_StillReadsRows()
    {
        Assert.Single(ReviewSheet.Parse("C:\\x\\01.png,1,원,번\n"));   // 머리 줄을 지운 표
        var rows = ReviewSheet.Parse("결과 파일,번호,원문,번역\rC:\\x\\01.png,1,원,번\rC:\\x\\01.png,2,원2,번2\r");   // CR만 쓰는 옛 맥 꼴
        Assert.Equal(2, rows.Count);
        Assert.Equal("번2", rows[1].Translation);
    }
}
