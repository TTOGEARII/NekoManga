using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>LLM 응답 파싱(LlmTranslator.ParseNumbered)과 번역기 고르기(TranslatorFactory).
/// 네트워크는 쓰지 않는다 — 번역기는 형식만 보고, 실제 번역 호출은 요청이 필요 없는 입력으로만 부른다.</summary>
public class TranslatorsTests
{
    [Fact]
    public void ParseNumbered_ShuffledAndMissingNumbers_LandInTheirOwnSlots()
    {
        // 순서가 뒤섞여도 번호가 자리를 정하고, 빠진 번호의 자리는 비워 둔다 — 호출부는 빈 자리만 Google로 메운다
        var r = LlmTranslator.ParseNumbered("[3] 셋\n[1] 하나", 3);

        Assert.Equal(3, r.Length);
        Assert.Equal("하나", r[0]);
        Assert.True(string.IsNullOrWhiteSpace(r[1]));
        Assert.Equal("셋", r[2]);
    }

    [Fact]
    public void ParseNumbered_OutOfRangeAndRepeatedNumbers_AreIgnored()
    {
        // 0번·범위 밖·int를 넘는 번호는 버리고, 같은 번호가 또 오면 처음 답을 지킨다
        var r = LlmTranslator.ParseNumbered("[0] 영\n[1] 하나\n[1] 다시 하나\n[3] 셋\n[99999999999] 큰 수\n[2] 둘", 2);

        Assert.Equal(new[] { "하나", "둘" }, r);
    }

    [Fact]
    public void ParseNumbered_ThinkBlock_IsDiscarded()
    {
        // 추론 모델이 본문에 끼운 <think> 안의 번호 줄을 답으로 잡으면, 처음 답을 지키는 규칙 때문에 진짜 답이 버려진다
        const string content = "<think>\n[1] 틀린 초안\n[2] 틀린 초안\n</think>\n[1] 하나\n[2] 둘";

        Assert.Equal(new[] { "하나", "둘" }, LlmTranslator.ParseNumbered(content, 2));
    }

    [Fact]
    public void ParseNumbered_SeveralItemsOnOneLine_AreSplitByNumber()
    {
        // 일부 모델은 여러 줄을 한 줄에 이어 붙인다 (" [1] … [2] …")
        Assert.Equal(new[] { "하나", "둘", "셋" }, LlmTranslator.ParseNumbered(" [1] 하나 [2] 둘 [3] 셋", 3));
    }

    [Theory]
    [InlineData("[1] 하나", "하나")]
    [InlineData("1. 하나", "하나")]
    [InlineData("1) 하나", "하나")]
    [InlineData("1: 하나", "하나")]
    // 앞뒤 공백과 Windows 줄끝(\r)은 걷어 낸다
    [InlineData("  [1]   하나  \r\n", "하나")]
    // 숫자로 시작하는 번역문은 번호로 오인하지 않는다 — 대괄호나 구분 기호가 있어야 번호다
    [InlineData("[1] 3명이 왔다", "3명이 왔다")]
    [InlineData("1명이 왔다", null)]
    [InlineData("1 하나", null)]
    public void ParseNumbered_LineForms_OnlyMarkedNumbersCount(string content, string? expected)
    {
        var r = LlmTranslator.ParseNumbered(content, 1);

        if (expected == null) Assert.True(string.IsNullOrWhiteSpace(r[0]));
        else Assert.Equal(expected, r[0]);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    [InlineData("번호 없는 답만 왔어요", 2)]
    [InlineData("[1] 하나", 0)]
    public void ParseNumbered_NothingUsable_ReturnsRequestedNumberOfEmptySlots(string? content, int count)
    {
        // 본문이 비거나(추론만 하다 끊김) 번호가 없으면 자리 수만 맞춘 빈 배열 — 호출부가 길이를 믿고 인덱스로 읽는다
        var r = LlmTranslator.ParseNumbered(content!, count);

        Assert.Equal(count, r.Length);
        Assert.All(r, s => Assert.True(string.IsNullOrWhiteSpace(s)));
    }

    [Theory]
    [InlineData("Google", "", "", typeof(GoogleTranslator), "Google")]
    [InlineData("DeepL", "DeepL", "dl-key", typeof(DeepLTranslator), "DeepL")]
    [InlineData("DeepSeek", "DeepSeek", "ds-key", typeof(LlmTranslator), "DeepSeek")]
    [InlineData("Grok", "Grok", "xai-key", typeof(LlmTranslator), "Grok")]
    // 키가 없거나 공백뿐이면 무료 Google로 떨어진다
    [InlineData("DeepL", "", "", typeof(GoogleTranslator), "Google")]
    [InlineData("DeepSeek", "DeepSeek", "   ", typeof(GoogleTranslator), "Google")]
    // 다른 엔진의 키는 소용없다
    [InlineData("Grok", "DeepSeek", "ds-key", typeof(GoogleTranslator), "Google")]
    // 모르는 엔진 이름(구 BT의 Papago 등)도 Google
    [InlineData("Papago", "DeepL", "dl-key", typeof(GoogleTranslator), "Google")]
    public void Create_EngineWithOrWithoutKey_PicksMatchingTranslatorOrGoogle(string engine, string keyOwner, string key, Type expectedType, string expectedLabelHead)
    {
        var s = Settings(engine, keyOwner, key);

        Assert.IsType(expectedType, TranslatorFactory.Create(s));
        // 진행 상태에 보이는 엔진 이름도 실제로 쓰는 번역기와 같아야 한다
        Assert.Equal(expectedLabelHead, TranslatorFactory.EngineLabel(s).Split(' ')[0]);
    }

    [Theory]
    // 모델을 비워 두면 기본 모델, 적어 두면 앞뒤 공백을 뗀 그 이름
    [InlineData("DeepSeek", "", "DeepSeek " + TranslatorFactory.DeepSeekDefaultModel)]
    [InlineData("DeepSeek", "  deepseek-pro  ", "DeepSeek deepseek-pro")]
    [InlineData("Grok", "   ", "Grok " + TranslatorFactory.GrokDefaultModel)]
    [InlineData("Grok", "grok-4.7", "Grok grok-4.7")]
    public void EngineLabel_ModelSetting_ShowsTrimmedOrDefaultModel(string engine, string model, string expected)
    {
        var s = Settings(engine, engine, "key");
        s.DeepSeekModel = model;
        s.GrokModel = model;

        Assert.Equal(expected, TranslatorFactory.EngineLabel(s));
    }

    [Theory]
    [InlineData("Google", 0)]
    [InlineData("Google", 3)]
    [InlineData("Llm", 0)]
    [InlineData("Llm", 3)]
    public async Task TranslateAsync_OnlyBlankTexts_ReturnsBlanksWithoutAnyRequest(string kind, int count)
    {
        // 빈 말풍선은 보낼 것이 없다 — 같은 개수의 빈 번역문을 돌려준다.
        // 이미 취소된 토큰을 준다: 요청을 보내려 하면 그 자리에서 취소 예외가 나므로(연결도 하지 않는다) 통과하면 요청이 없었다는 뜻이다.
        ITranslator translator = kind == "Google"
            ? new GoogleTranslator()
            : new LlmTranslator("http://127.0.0.1:9/v1/chat/completions", "key", "model", "Test");
        var texts = new[] { "", "   ", "\n" }.Take(count).ToList();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await translator.TranslateAsync(texts, "ja", "ko", cancelled.Token);

        Assert.Equal(count, result.Count);
        Assert.All(result, r => Assert.Equal("", r));
    }

    /// <summary>엔진과, 키를 넣을 엔진(keyOwner)을 정한 설정. 설정 파일은 읽지 않는다.</summary>
    private static AppSettings Settings(string engine, string keyOwner, string key) => new()
    {
        TranslateEngine = engine,
        DeepLApiKey = keyOwner == "DeepL" ? key : "",
        DeepSeekApiKey = keyOwner == "DeepSeek" ? key : "",
        GrokApiKey = keyOwner == "Grok" ? key : "",
    };
}
