using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>번역 용어집·앞 쪽 문맥·효과음 지시문(2026-11-26, CarrotMangaTranslator 참고 — 사용자 "번역 용어집 + 문맥 이어 보내기")과 번역문 검증.
/// 네트워크 없이 — 프롬프트를 짓는 함수, 원문 치환, 검증 함수만 본다.</summary>
public class TranslationOptionsTests
{
    // ── 용어집 ──

    [Fact]
    public void Glossary_Parse_AcceptsEqualsAndTab_SkipsBlankCommentsAndHalfLines()
    {
        var g = Glossary.Parse("ミク=미쿠\n# 주석\n\n先輩\t선배\n=없는원문\n원문만\n  空白 = 공백  ");
        Assert.Equal<(string, string)>([("ミク", "미쿠"), ("先輩", "선배"), ("空白", "공백")], g);
    }

    [Fact]
    public void Glossary_Parse_NullOrBlank_IsEmpty()
    {
        Assert.Empty(Glossary.Parse(null));
        Assert.Empty(Glossary.Parse("   \n\n"));
    }

    [Fact]
    public void Glossary_ApplySource_ReplacesLongerTermsFirst()
    {
        // "ミク" 안의 "ク"가 먼저 바뀌면 "ミク"를 못 찾는다 — 긴 원문부터 바꾼다
        var g = Glossary.Parse("ク=쿠\nミク=미쿠");
        Assert.Equal("미쿠ちゃん、쿠", Glossary.ApplySource("ミクちゃん、ク", g));
        Assert.Equal("그대로", Glossary.ApplySource("그대로", g));
    }

    // ── LLM 프롬프트 ──

    [Fact]
    public void BuildSystemPrompt_Glossary_ListsEveryTerm()
    {
        var o = new TranslateOptions(Glossary.Parse("ミク=미쿠\n先輩=선배"), [], Sfx: false);
        var p = LlmTranslator.BuildSystemPrompt("Japanese", "Korean", o);
        Assert.Contains("ミク", p);
        Assert.Contains("미쿠", p);
        Assert.Contains("先輩", p);
        Assert.Contains("선배", p);
    }

    [Fact]
    public void BuildSystemPrompt_Context_IncludesPreviousLines_AsReferenceOnly()
    {
        var o = new TranslateOptions([], [("おはよう", "좋은 아침")], Sfx: false);
        var p = LlmTranslator.BuildSystemPrompt("Japanese", "Korean", o);
        Assert.Contains("おはよう", p);
        Assert.Contains("좋은 아침", p);
        Assert.Contains("context", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not", p, StringComparison.OrdinalIgnoreCase);   // 출력하지 말라는 당부
    }

    [Fact]
    public void BuildSystemPrompt_NoOptions_HasNoGlossaryOrContextSections()
    {
        var p = LlmTranslator.BuildSystemPrompt("Japanese", "Korean", TranslateOptions.None);
        Assert.DoesNotContain("Glossary", p);
        Assert.DoesNotContain("Context", p);
        Assert.DoesNotContain("sound effect", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Japanese", p);
        Assert.Contains("Korean", p);
    }

    [Fact]
    public void BuildSystemPrompt_Sfx_AsksForShortOnomatopoeiaWithoutExplanation()
    {
        var p = LlmTranslator.BuildSystemPrompt("Japanese", "Korean", TranslateOptions.None with { Sfx = true });
        Assert.Contains("sound effect", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", p);                                            // 길이 상한
        Assert.DoesNotContain("speech bubbles of one page", p);              // 대사용 문장은 빠진다
    }

    // ── 번역문 검증(Carrot: 목표 언어 글자가 있어야 하고 원문 글자가 남으면 거부) ──

    [Theory]
    [InlineData("안녕하세요", "ko", false, true)]
    [InlineData("", "ko", false, false)]
    [InlineData("   ", "ko", false, false)]
    [InlineData("こんにちは", "ko", false, false)]        // 일본어가 그대로 왔다
    [InlineData("안녕 ちゃん", "ko", false, false)]       // 섞여도 안 된다
    [InlineData("안녕 漢字", "ko", false, false)]
    [InlineData("Hello", "ko", false, false)]             // 한글이 없다
    [InlineData("안녕~!?", "ko", false, true)]            // 기호는 상관없다
    [InlineData("Hello", "en", false, true)]
    [InlineData("こんにちは", "en", false, false)]
    [InlineData("こんにちは", "ja", false, true)]         // 일본어가 목표면 그대로
    [InlineData("두근두근", "ko", true, true)]
    [InlineData("두근두근두근두근두근두근두근", "ko", true, false)]   // 효과음은 12자까지
    [InlineData("쾅! (문이 세게 닫히는 소리)", "ko", true, false)]   // 설명이 붙은 효과음
    public void TranslationCheck_IsAcceptable(string tr, string target, bool sfx, bool expected)
    {
        Assert.Equal(expected, TranslationCheck.IsAcceptable(tr, target, sfx));
    }

    [Theory]
    [InlineData("OK", "ko", "OK", true)]            // 원문에 옮길 글자가 없다 — 그대로가 맞다(검수 2026-11-26: 안 그러면 줄마다 다시 묻고 Google까지 부른다)
    [InlineData("3", "ko", "3", true)]
    [InlineData("OK", "ko", "おはよう", false)]      // 원문은 일본어인데 한글이 없다
    [InlineData("OK", "ko", null, false)]
    public void TranslationCheck_KoreanWithoutHangul_PassesOnlyWhenTheSourceHasNothingToTranslate(string tr, string target, string? source, bool expected)
    {
        Assert.Equal(expected, TranslationCheck.IsAcceptable(tr, target, false, source));
    }

    // ── 검증 뒤 재시도·대체(EmbeddedTranslator.TranslateCheckedAsync) — 네트워크 없이 가짜 번역기로 ──

    private sealed class FakeTranslator(Func<IReadOnlyList<string>, IReadOnlyList<string>> answer) : ITranslator
    {
        public int Calls;
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(texts));
        }
    }

    [Fact]
    public async Task TranslateChecked_SfxThatStillFails_BecomesEmpty_DialogueKeepsTheFirstAnswer()
    {
        // Google처럼 다시 물어도 같은 답인 번역기 + 가짜 Google(빈 답): 검증에 걸린 효과음은 ""(호출부가 지우지도 덮지도 않는다), 대사는 처음 답 그대로
        var bad = new FakeTranslator(ts => ts.Select(t => t == "ドン" ? "쾅! (문이 세게 닫히는 소리)" : "こんにちは").ToList());
        var fallback = new FakeTranslator(ts => ts.Select(_ => "").ToList());
        var sfx = await EmbeddedTranslator.TranslateCheckedAsync(bad, ["ドン"], TranslateOptions.None with { Sfx = true }, "ja", "ko", CancellationToken.None, fallback);
        Assert.Equal("", sfx[0]);
        var dlg = await EmbeddedTranslator.TranslateCheckedAsync(bad, ["こんにちは"], TranslateOptions.None, "ja", "ko", CancellationToken.None, fallback);
        Assert.Equal("こんにちは", dlg[0]);
        Assert.Equal(2, fallback.Calls);   // 둘 다 대체 번역기까지 갔다
    }

    [Fact]
    public async Task TranslateChecked_FallbackAnswerIsUsedWhenItPasses_GoodLinesNeverRetried()
    {
        var bad = new FakeTranslator(ts => ts.Select(t => t == "ドン" ? "쾅! (문이 세게 닫히는 소리)" : "안녕").ToList());
        var fallback = new FakeTranslator(ts => ts.Select(_ => "쾅").ToList());
        var r = await EmbeddedTranslator.TranslateCheckedAsync(bad, ["こんにちは", "ドン"], TranslateOptions.None with { Sfx = true }, "ja", "ko", CancellationToken.None, fallback);
        Assert.Equal(["안녕", "쾅"], r);
        Assert.Equal(1, fallback.Calls);

        var good = new FakeTranslator(ts => ts.Select(_ => "안녕").ToList());
        var ok = await EmbeddedTranslator.TranslateCheckedAsync(good, ["こんにちは"], TranslateOptions.None, "ja", "ko", CancellationToken.None, fallback);
        Assert.Equal("안녕", ok[0]);
        Assert.Equal(1, fallback.Calls);   // 통과한 줄은 대체 번역기에 안 간다
    }
}
