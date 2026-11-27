using System.Text.Json.Nodes;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>번역을 Carrot처럼(3단계, 2026-11-27 — 사용자 "번역도 좀더 자연스럽게", "번역 역식은 carrot와 유사하게"): LLM 번역기에 작품 지침(경칭·톤·메모)·인물 말투·풍선 자리,
/// 고르면 쪽 그림까지 같이 보낸다. 네트워크 없이 — 프롬프트·요청 본문을 짓는 함수와 설정·옵션 짓기만 본다.</summary>
public class TranslatePromptTests
{
    private static string Prompt(TranslateOptions o, string target = "Korean") => LlmTranslator.BuildSystemPrompt("Japanese", target, o);

    // ── 대사 프롬프트의 뼈대 ──

    [Fact]
    public void Dialogue_SaysBubblesContinueAcrossLines_AndLineCountMustStay()
    {
        // 전에는 "한 쪽의 연속한 풍선이니 서로 문맥으로 쓰라"뿐이라 풍선을 넘어 이어지는 문장을 줄마다 따로 옮겼다("몇 번인가" / "본 적")
        var p = Prompt(TranslateOptions.None);
        Assert.Contains("across consecutive bubbles", p);
        Assert.Contains("same number of lines", p);
        Assert.Contains("[n]", p);
    }

    [Fact]
    public void Dialogue_AsksForNativeInterjections_WithExamplesForTheTargetLanguage()
    {
        // "わっ" → "왓", "えっ" → "엣" 같은 글자 옮기기를 막는다 — 보기는 목표 언어에 맞는 것만 든다
        var ko = Prompt(TranslateOptions.None, "Korean");
        Assert.Contains("interjections", ko);
        Assert.Contains("어?", ko);
        var en = Prompt(TranslateOptions.None, "English");
        Assert.Contains("Huh?", en);
        Assert.DoesNotContain("어?", en);
        var zh = Prompt(TranslateOptions.None, "Chinese");
        Assert.Contains("interjections", zh);
        Assert.DoesNotContain("어?", zh);
        Assert.DoesNotContain("Huh?", zh);
    }

    [Fact]
    public void Dialogue_TellsTheModelTheSourceIsOcr_AndNotToInventOrSpellOutScraps()
    {
        // OCR이 틀린 글자·반 토막을 그대로 옮기지 않게. 다만 "문맥으로 알아내라"고만 하면 못 읽는 조각에서 없는 말을 지어내거나 소리대로 적는다(둘째 판에서 봄)
        var p = Prompt(TranslateOptions.None);
        Assert.Contains("OCR", p);
        Assert.Contains("Never invent new content", p);
        Assert.Contains("sound by sound", p);
    }

    [Fact]
    public void Dialogue_TranslatesWordsByMeaning_OnlyNamesAndGlossaryTermsKeepTheirSound()
    {
        // 흔한 낱말을 소리대로 적어 오는 일(설계 때부터 짚은 음차)이 판마다 오락가락했다 — 이름·지명·용어집 말고는 뜻으로 옮기라고 적는다
        var p = Prompt(TranslateOptions.None);
        Assert.Contains("by its meaning", p);
        Assert.Contains("glossary terms", p);
    }

    [Fact]
    public void Dialogue_KeepsTheRegisterOfTheOriginal()
    {
        // "자연스러운 입말"만 시키면 부드러운 말을 거친 속어로 옮기는 판이 나온다(둘째 판의 한 쪽) — 원문보다 거칠게도 점잖게도 하지 않는다
        Assert.Contains("register", Prompt(TranslateOptions.None));
    }

    // ── 경칭·톤 ──

    [Theory]
    [InlineData("Keep", "Keep name honorifics")]
    [InlineData("Natural", "Adapt name honorifics")]
    [InlineData("Drop", "Leave out name honorifics")]
    public void Honorifics_EachPolicyHasItsOwnRule(string policy, string expected)
    {
        var guide = TranslateGuide.Default with { Honorifics = policy };
        var p = Prompt(TranslateOptions.None with { Guide = guide });
        Assert.Contains(expected, p);
        foreach (string other in new[] { "Keep name honorifics", "Adapt name honorifics", "Leave out name honorifics" })
            if (other != expected) Assert.DoesNotContain(other, p);
    }

    [Fact]
    public void Tone_LiteralAddsItsRule_NaturalDoesNot()
    {
        Assert.DoesNotContain("Stay close to the wording", Prompt(TranslateOptions.None));
        Assert.Contains("Stay close to the wording", Prompt(TranslateOptions.None with { Guide = TranslateGuide.Default with { Tone = "Literal" } }));
    }

    // ── 인물·작품 메모 ──

    [Fact]
    public void Characters_AndNotes_AreListedInTheirOwnSections()
    {
        var guide = TranslateGuide.Default with
        {
            Characters = TranslateGuide.ParseCharacters("미쿠=반말, 장난스럽다\n# 주석\n선배\t존댓말\n=말투만\n이름만"),
            Notes = "학원물. 욕은 순화한다.",
        };
        var p = Prompt(TranslateOptions.None with { Guide = guide });
        Assert.Contains("Characters", p);
        Assert.Contains("미쿠: 반말, 장난스럽다", p);
        Assert.Contains("선배: 존댓말", p);
        Assert.DoesNotContain("주석", p);
        Assert.DoesNotContain("말투만", p);
        Assert.Contains("Notes for this work", p);
        Assert.Contains("학원물. 욕은 순화한다.", p);
    }

    [Fact]
    public void Characters_AndNotes_AreCutAtTheirLimits()
    {
        // 설정 칸에 얼마든지 적을 수 있다 — 프롬프트는 상한에서 자른다(요청이 한없이 커지지 않게)
        var many = string.Join("\n", Enumerable.Range(0, TranslateGuide.MaxCharacters + 25).Select(i => $"인물{i:D3}=말투"));
        var guide = TranslateGuide.Default with { Characters = TranslateGuide.ParseCharacters(many), Notes = new string('메', TranslateGuide.MaxNotesChars + 500) };
        var p = Prompt(TranslateOptions.None with { Guide = guide });
        Assert.Contains($"인물{TranslateGuide.MaxCharacters - 1:D3}:", p);
        Assert.DoesNotContain($"인물{TranslateGuide.MaxCharacters:D3}:", p);
        Assert.Equal(TranslateGuide.MaxNotesChars, p.Count(c => c == '메'));
    }

    [Fact]
    public void NoGuide_HasNoCharacterOrNotesSections()
    {
        var p = Prompt(TranslateOptions.None);
        Assert.DoesNotContain("Characters", p);
        Assert.DoesNotContain("Notes for this work", p);
    }

    // ── 풍선 자리 ──

    [Fact]
    public void Places_AreExplained_AndPrefixedToEachLine()
    {
        // 감지기의 블록 차례는 읽는 차례와 어긋날 때가 있다(실제 04쪽의 내레이션 네 줄) — 그림을 못 보는 번역기에는 풍선의 자리라도 준다
        var o = TranslateOptions.None with { Places = [(82, 10), (15, 60)] };
        var p = Prompt(o);
        Assert.Contains("<x,y>", p);
        Assert.Contains("right to left", p);
        Assert.Equal("[1] <82,10> 첫 줄\n[2] <15,60> 둘째 줄", LlmTranslator.BuildUserText(["첫 줄", "둘째\n줄"], o.Places));
    }

    [Fact]
    public void Places_NoneOrWrongCount_LinesAreSentPlain()
    {
        Assert.DoesNotContain("<x,y>", Prompt(TranslateOptions.None));
        Assert.Equal("[1] 가\n[2] 나", LlmTranslator.BuildUserText(["가", "나"], null));
        // 자리의 수가 줄 수와 다르면 붙이지 않는다 — 엉뚱한 줄에 엉뚱한 자리가 붙는 것보다 없는 것이 낫다
        Assert.Equal("[1] 가\n[2] 나", LlmTranslator.BuildUserText(["가", "나"], [(1, 2)]));
    }

    [Fact]
    public void ParseNumbered_DropsAPositionTagTheModelEchoedBack()
    {
        Assert.Equal(["안녕", "잘 가"], LlmTranslator.ParseNumbered("[1] <82,10> 안녕\n[2] <15, 60>잘 가", 2));
    }

    // ── 효과음 요청에는 대사용 지침이 섞이지 않는다 ──

    [Fact]
    public void Sfx_HasNoneOfTheDialogueGuidance()
    {
        var guide = new TranslateGuide("Natural", "Literal", "작품 메모", TranslateGuide.ParseCharacters("미쿠=반말"));
        var p = Prompt(new TranslateOptions([], [], Sfx: true) { Guide = guide, Places = [(1, 2)] });
        Assert.Contains("sound effect", p, StringComparison.OrdinalIgnoreCase);
        foreach (string dialogueOnly in new[] { "honorifics", "Stay close to the wording", "Characters", "Notes for this work", "<x,y>", "across consecutive bubbles" })
            Assert.DoesNotContain(dialogueOnly, p);
    }

    // ── 요청 본문 ──

    [Fact]
    public void RequestBody_WithoutAnImage_IsTheSameShapeAsBefore()
    {
        var body = LlmTranslator.BuildRequestBody("deepseek-v4-pro", "SYS", "[1] 글", 2048, thinkingOff: true, pageImage: null);
        Assert.Equal("deepseek-v4-pro", body["model"]!.GetValue<string>());
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Equal(2048, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(("system", "SYS"), (messages[0]!["role"]!.GetValue<string>(), messages[0]!["content"]!.GetValue<string>()));
        Assert.Equal(("user", "[1] 글"), (messages[1]!["role"]!.GetValue<string>(), messages[1]!["content"]!.GetValue<string>()));   // 문자열 그대로
        Assert.Null(LlmTranslator.BuildRequestBody("m", "s", "u", 1, thinkingOff: false, pageImage: null)["thinking"]);
    }

    [Fact]
    public void RequestBody_WithAnImage_SendsTextAndImageParts()
    {
        // 그림을 읽는 모델(OpenAI 호환 꼴): content가 [글, 그림] 두 조각이고 그림은 data URL(JPEG)이다
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        var body = LlmTranslator.BuildRequestBody("grok-4", "SYS", "[1] 글", 2048, thinkingOff: false, pageImage: jpeg);
        var parts = body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(2, parts.Count);
        Assert.Equal(("text", "[1] 글"), (parts[0]!["type"]!.GetValue<string>(), parts[0]!["text"]!.GetValue<string>()));
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(jpeg), parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("SYS", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void SystemPrompt_WithAnImage_SaysThePageIsAttached()
    {
        Assert.DoesNotContain("attached", Prompt(TranslateOptions.None));
        Assert.Contains("attached", Prompt(TranslateOptions.None with { PageImage = [1] }));
    }

    // ── 뒷손질 · 다시 묻기 · 읽는 차례 (실제 다섯 쪽을 새 프롬프트로 돌려 보고 더한 것) ──

    [Fact]
    public void Dialogue_TellsTheModelWhatToDoWithACutOffSound()
    {
        // 새 프롬프트의 첫 판은 55줄 가운데 4줄에 끊는 소리 "っ"를 그대로 남겼다("귀여워っ") — 지시문에 적어 준다
        Assert.Contains("small っ", Prompt(TranslateOptions.None));
    }

    [Theory]
    [InlineData("귀여워っ♡", "ko", "귀여워♡")]
    [InlineData("할 거야ッ", "ko", "할 거야")]
    [InlineData("Wait—っ", "en", "Wait—")]
    [InlineData("等一下っ", "zh", "等一下")]
    public void Polish_DropsTheSmallTsuLeftInATranslation(string tr, string target, string expected)
    {
        // 끊는 소리 っ·ッ는 한국어·영어·중국어에 맞는 글자가 없다 — 남으면 '원문 글자가 남았다'로 걸려 그 줄만 다시 물었다. 떼면 된다
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(tr, target));
    }

    [Fact]
    public void Polish_JapaneseTarget_KeepsTheSmallTsu()
    {
        Assert.Equal("まってっ", EmbeddedTranslator.PolishTranslation("まってっ", "ja"));
    }

    /// <summary>다시 물으면 다른 답을 내는 번역기(LLM)의 가짜 — 부를 때마다 다음 답을 낸다. 무엇을 물었는지와 같이 온 옵션을 적어 둔다.</summary>
    private sealed class VaryingTranslator(params Func<IReadOnlyList<string>, IReadOnlyList<string>>[] answers) : ITranslator
    {
        public readonly List<IReadOnlyList<string>> Asked = new();
        public readonly List<TranslateOptions> Options = new();
        public bool AnswersVary => true;
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
            => TranslateAsync(texts, sourceLang, targetLang, TranslateOptions.None, ct);
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, TranslateOptions options, CancellationToken ct)
        {
            Asked.Add(texts.ToList());
            Options.Add(options);
            return Task.FromResult(answers[Math.Min(Asked.Count, answers.Length) - 1](texts));
        }
    }

    private sealed class SilentTranslator : ITranslator
    {
        public int Calls;
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(_ => "").ToList());
        }
    }

    [Fact]
    public async Task TranslateChecked_ARejectedLine_IsAskedAgainWithTheWholePage_AndOnlyThatLineIsReplaced()
    {
        // 걸린 줄만 떼어 다시 물으면 문맥이 없어 반 토막을 엉뚱하게 옮긴다(실제 04쪽: 문장의 끝 조각만 다시 물으니 전혀 다른 말이 왔다) —
        // 쪽 전체를 다시 묻고, 걸린 줄의 답만 바꾼다(통과한 줄은 처음 답 그대로)
        var llm = new VaryingTranslator(
            _ => ["우리들", "할 거야かな"],        // 둘째 줄에 가나가 남았다
            _ => ["우리는", "할 거야"]);
        var google = new SilentTranslator();

        var r = await EmbeddedTranslator.TranslateCheckedAsync(llm, ["私たち", "するよ"], TranslateOptions.None, "ja", "ko", CancellationToken.None, google);

        Assert.Equal(["우리들", "할 거야"], r);
        Assert.Equal(2, llm.Asked.Count);
        Assert.Equal(["私たち", "するよ"], llm.Asked[1]);
        Assert.Equal(0, google.Calls);   // 다시 물어 풀렸으면 대체 번역기까지 가지 않는다
    }

    [Fact]
    public void AnswersVary_OnlyTheLlmTranslatorIsWorthAskingAgain()
    {
        // 다시 물어 다른 답을 기대할 수 있는 것은 LLM뿐이다 — Google·DeepL은 같은 답이 온다(다시 묻지 않고 바로 대체 번역기로 간다)
        Assert.True(((ITranslator)new LlmTranslator("https://example.invalid", "key", "model", "Engine")).AnswersVary);
        Assert.False(((ITranslator)new GoogleTranslator()).AnswersVary);
        Assert.False(((ITranslator)new DeepLTranslator("key")).AnswersVary);
    }

    [Fact]
    public async Task TranslateChecked_NothingRejected_IsAskedOnce()
    {
        var llm = new VaryingTranslator(_ => ["안녕"], _ => ["또 안녕"]);
        var r = await EmbeddedTranslator.TranslateCheckedAsync(llm, ["こんにちは"], TranslateOptions.None, "ja", "ko", CancellationToken.None, new SilentTranslator());
        Assert.Equal(["안녕"], r);
        Assert.Single(llm.Asked);
    }

    [Fact]
    public void ReadingOrder_BubblesSideBySide_GoRightToLeft_RowsGoTopToBottom()
    {
        // 실제 04쪽의 내레이션: 같은 높이에 나란한 두 풍선이 위쪽 끝 몇 px 차이로 왼쪽 것부터 나갔다(위쪽 끝으로만 줄을 세웠다) — 문장의 앞뒤가 바뀐 채 번역기로 갔다
        Rect[] boxes = [new(150, 490, 120, 300), new(440, 460, 120, 300), new(650, 1010, 120, 300), new(860, 1010, 120, 300), new(500, 60, 200, 120)];
        Assert.Equal([4, 1, 0, 3, 2], EmbeddedTranslator.ReadingOrder(boxes, rightToLeft: true));
        Assert.Equal([4, 0, 1, 2, 3], EmbeddedTranslator.ReadingOrder(boxes, rightToLeft: false));   // 가로쓰기 원문은 왼쪽부터
    }

    [Fact]
    public void ReadingOrder_NoneOrOne()
    {
        Assert.Empty(EmbeddedTranslator.ReadingOrder([], rightToLeft: true));
        Assert.Equal([0], EmbeddedTranslator.ReadingOrder([new Rect(1, 2, 3, 4)], rightToLeft: true));
    }

    [Fact]
    public async Task TranslateInReadingOrder_SendsLinesAndPlacesInReadingOrder_AndReturnsThemInTheGivenOrder()
    {
        // 번역기에는 읽는 차례로 보내고(자리도 그 차례로), 받은 답은 블록의 차례로 되돌린다 — 식자·사이드카의 블록 차례는 바뀌지 않는다
        Rect[] boxes = [new(150, 490, 120, 300), new(440, 460, 120, 300), new(500, 60, 200, 120)];
        var llm = new VaryingTranslator(ts => ts.Select(s => "번역:" + s).ToList());
        var opts = TranslateOptions.None with { Places = [(21, 43), (50, 41), (60, 8)] };

        var r = await EmbeddedTranslator.TranslateInReadingOrderAsync(llm, ["왼쪽", "오른쪽", "맨 위"], boxes, opts, "ja", "ko", CancellationToken.None, new SilentTranslator());

        Assert.Equal(["맨 위", "오른쪽", "왼쪽"], llm.Asked[0]);
        Assert.Equal<(int, int)>([(60, 8), (50, 41), (21, 43)], llm.Options[0].Places!);
        Assert.Equal(["번역:왼쪽", "번역:오른쪽", "번역:맨 위"], r);
    }

    // ── 설정 ──

    [Fact]
    public void Settings_Defaults_MeanWhatTheAppDidBefore()
    {
        var s = new AppSettings();
        Assert.Equal(("Keep", "Natural", "", "", false), (s.TranslateHonorifics, s.TranslateTone, s.TranslateCharacters, s.TranslateNotes, s.TranslateSendImage));
    }

    // (손으로 고친 모르는 값이 기본으로 돌아오는 것은 AppSettingsTests의 Load_InvalidValue_FallsBackToDefault·Load_ValidValue_IsKept가 본다)

    // ── 쪽마다 옵션 짓기 ──

    [Fact]
    public void DialogueOptions_CarriesTheGuide_AndThePlaceOfEachBubble()
    {
        var s = new AppSettings { TranslateHonorifics = "Natural", TranslateTone = "Literal", TranslateCharacters = "미쿠=반말", TranslateNotes = "메모", TranslateCarryContext = true };
        Rect[] boxes = [new Rect(800, 100, 100, 200), new Rect(0, 1400, 200, 100), new Rect(950, 1450, 100, 100)];
        var o = EmbeddedTranslator.DialogueOptions(s, [("ミク", "미쿠")], [("前", "앞")], boxes, new Size(1000, 1500), pageImage: [9]);

        Assert.Equal(("Natural", "Literal", "메모"), (o.Guide.Honorifics, o.Guide.Tone, o.Guide.Notes));
        Assert.Equal<(string, string)>([("미쿠", "반말")], o.Guide.Characters);
        Assert.Equal<(int, int)>([(85, 13), (10, 97), (100, 100)], o.Places!);   // 상자 한가운데의 쪽 안 자리(%), 0~100으로 자른다
        Assert.Equal<(string, string)>([("ミク", "미쿠")], o.Glossary);
        Assert.Equal<(string, string)>([("前", "앞")], o.Context);
        Assert.False(o.Sfx);
        Assert.Null(o.PageImage);   // 그림은 설정을 켰을 때만
    }

    [Fact]
    public void DialogueOptions_ContextAndImage_FollowTheirSettings()
    {
        var off = EmbeddedTranslator.DialogueOptions(new AppSettings { TranslateCarryContext = false }, [], [("前", "앞")], [], new Size(10, 10), pageImage: [9]);
        Assert.Empty(off.Context);
        Assert.Null(off.PageImage);
        var on = EmbeddedTranslator.DialogueOptions(new AppSettings { TranslateSendImage = true }, [], [], [], new Size(10, 10), pageImage: [9]);
        Assert.Equal([9], on.PageImage!);
    }

    [Fact]
    public void PageJpeg_IsAJpeg_NoLargerThanTheLimit()
    {
        using var page = new Mat(3000, 2100, MatType.CV_8UC3, Scalar.All(200));
        page.Rectangle(new Rect(200, 300, 400, 500), Scalar.Black, -1);

        byte[] jpeg = EmbeddedTranslator.PageJpeg(page);

        Assert.Equal((0xFF, 0xD8), (jpeg[0], jpeg[1]));
        using var back = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(EmbeddedTranslator.PageImageMaxSide, Math.Max(back.Width, back.Height));
        Assert.Equal(0.7, back.Width / (double)back.Height, 2);   // 비율은 그대로
        // 작은 쪽은 키우지 않는다
        using var small = new Mat(800, 600, MatType.CV_8UC3, Scalar.All(200));
        using var smallBack = Cv2.ImDecode(EmbeddedTranslator.PageJpeg(small), ImreadModes.Color);
        Assert.Equal((600, 800), (smallBack.Width, smallBack.Height));
    }
}
