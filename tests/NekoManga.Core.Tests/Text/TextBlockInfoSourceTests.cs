using System.Text.Json;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>결과 창 식자 탭이 번역 글 위에 보여 주는 원문(OCR 글) — 번역을 고칠 때 원문을 보며 고치려고(2026-11-26 사용자 요청).
/// 칸을 접는 기준(빈 글)과 보여 주는 꼴(앞뒤 빈칸 없음)을 지킨다.</summary>
public class TextBlockInfoSourceTests
{
    private static TextBlockInfo Block(string source) =>
        new(10, 10, 50, 80, source, "번역 글", 18, Vertical: true, FontColorHex: "#000000");

    [Fact]
    public void ShownSource_IsTheOcrText_WithoutSurroundingBlanks()
    {
        // OCR이 앞뒤에 붙인 빈칸·줄 바꿈은 칸에서 자리만 차지한다
        Assert.Equal("今日はいい天気ですね", Block(" 今日はいい天気ですね\n").ShownSource());
    }

    [Fact]
    public void ShownSource_IsEmpty_WhenTheOcrTextIsBlankOrMissing()
    {
        // 결과 창은 빈 글이면 원문 칸을 접는다 — [새 글 넣기]로 만든 글(원문 "")과 옛 json에 칸이 없는 경우(null)
        Assert.Equal("", Block("   ").ShownSource());
        Assert.Equal("", Block(null!).ShownSource());
    }

    [Fact]
    public void ShownSource_IsEmpty_WhenAnOldSidecarJsonHasNoSourceText()
    {
        // 실제 경계: 사이드카 json에 SourceText 칸이 없으면 생성자 바인딩이 null을 넣는다 — 레코드에 본문(메서드)을 붙여도 그대로 읽히고, 원문은 빈 글이어야 한다
        var json = """{"X1":1,"Y1":2,"X2":30,"Y2":40,"Translation":"번역 글","FontSize":18,"Vertical":true,"FontColorHex":"#000000"}""";
        var b = JsonSerializer.Deserialize<TextBlockInfo>(json);
        Assert.NotNull(b);
        Assert.Equal("번역 글", b!.Translation);
        Assert.Equal("", b.ShownSource());
    }
}
