using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.TextTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>내장 번역 파이프라인의 정적 판정·뒷손질(EmbeddedTranslator) — 모델 없이 문자열과 합성 조각만으로 본다.</summary>
public class EmbeddedTranslatorTests
{
    [Theory]
    // 장음 기호·물결표는 가로 식자에서 긴 막대로 그려지므로 ~로 바꾼다
    [InlineData("또ー!?", "ko", "또~!?")]
    [InlineData("에〜", "ko", "에~")]
    [InlineData("에～", "en", "에~")]
    // 전각 느낌표·물음표는 반각으로
    [InlineData("뭐！？", "ko", "뭐!?")]
    // 가운뎃점·두 점 말줄임은 마침표로 — 가로 식자에서 점이 띄엄띄엄 그려진다
    [InlineData("그건・・・", "ko", "그건...")]
    [InlineData("음‥", "ko", "음..")]
    // 목표 언어가 비었거나 없어도 일본어가 아니므로 다듬는다
    [InlineData("또ー！", "", "또~!")]
    [InlineData("또ー！", null, "또~!")]
    // 다듬을 것이 없는 글·빈 글은 그대로
    [InlineData("안녕하세요. 반가워요!", "ko", "안녕하세요. 반가워요!")]
    [InlineData("", "ko", "")]
    public void PolishTranslation_NonJapaneseTarget_ReplacesMarksMeantForJapanese(string text, string? target, string expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(text, target!));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("JA")]
    public void PolishTranslation_JapaneseTarget_LeavesTextAsIs(string target)
    {
        // 일본어로 옮길 때는 ー·！·・・・가 맞는 표기다 — 언어 코드의 대소문자와 상관없이 손대지 않는다
        const string text = "またー！？そう・・・〜‥";
        Assert.Equal(text, EmbeddedTranslator.PolishTranslation(text, target));
    }

    [Theory]
    [InlineData("그건・・・・")]
    [InlineData("그건・・・・・")]
    public void PolishTranslation_MiddleDotEllipsisLongerThanThree_LeavesNoMiddleDot(string text)
    {
        // 가운뎃점 말줄임은 가로 식자에서 점이 띄엄띄엄 그려진다 — 세 개보다 길게 이어져도 가운뎃점이 하나라도 남으면 같은 문제가 생긴다
        Assert.DoesNotContain("・", EmbeddedTranslator.PolishTranslation(text, "ko"));
    }

    [Theory]
    // 셋 이상 이어진 가운뎃점은 개수만큼 마침표로 — 세 개·여섯 개는 예전 결과 그대로다
    [InlineData("그건・・・", "그건...")]
    [InlineData("그건・・・・", "그건....")]
    [InlineData("그건・・・・・", "그건.....")]
    [InlineData("그건・・・・・・", "그건......")]
    [InlineData("음・・・・・・・ 그래・・・・", "음....... 그래....")]
    // 하나는 말줄임이 아니다 (열거의 가운뎃점)
    [InlineData("빨강・파랑", "빨강・파랑")]
    public void PolishTranslation_MiddleDotRun_BecomesSameNumberOfPeriods(string text, string expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(text, "ko"));
    }

    [Theory]
    // 중국어 조판은 전각 느낌표·물음표가 표준이다 — 목표 언어 코드가 어떤 꼴이든 그대로 둔다
    [InlineData("什么！？", "zh", "什么！？")]
    [InlineData("什么！？", "ZH", "什么！？")]
    [InlineData("什么！？", "zh-CN", "什么！？")]
    [InlineData("什么！？", "zh-TW", "什么！？")]
    [InlineData("什么！？", "zh-Hant", "什么！？")]
    // 일본어 장음 기호·가운뎃점 말줄임은 중국어로 옮길 때도 바꾼다 — 다만 반각 ~·마침표가 아니라 중국어의 전각 물결표·줄임표로
    [InlineData("又ー！？", "zh", "又～！？")]
    [InlineData("那个・・・・", "zh", "那个……")]
    // 한국어·영어는 예전처럼 반각으로
    [InlineData("什么！？", "ko", "什么!?")]
    [InlineData("What！？", "en", "What!?")]
    public void PolishTranslation_ChineseTarget_KeepsFullWidthExclamationAndQuestionMarks(string text, string target, string expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(text, target));
    }

    [Theory]
    // 바꾸기마다 중국어 목표에서 어떻게 되는지 — 중국어 조판은 전각 부호가 표준이다
    // ① 장음 기호 ー: 중국어 글자가 아니다 → 전각 물결표
    [InlineData("啊ー", "啊～")]
    // ② 일본어 물결표 〜(U+301C) → 중국어가 쓰는 ～(U+FF5E)로 모은다
    [InlineData("诶〜", "诶～")]
    // ③ 전각 물결표 ～(U+FF5E)는 맞는 표기 — 반각 ~로 바꾸지 않는다
    [InlineData("好～", "好～")]
    // ④ 전각 느낌표·물음표는 그대로
    [InlineData("真的！？", "真的！？")]
    // ⑤ 가운뎃점 말줄임 → 여섯 점 줄임표 ……(U+2026 둘). 여섯 개까지는 하나, 그보다 길면 여섯 개마다 하나 더
    [InlineData("那个・・・", "那个……")]
    [InlineData("那个・・・・・・", "那个……")]
    [InlineData("那个・・・・・・・", "那个…………")]
    [InlineData("嗯・・・ 是吗・・・・", "嗯…… 是吗……")]
    // 가운뎃점 하나·둘은 말줄임이 아니다 (한국어와 같은 기준 — 열거의 가운뎃점일 수 있다)
    [InlineData("红・蓝", "红・蓝")]
    // ⑥ 두 점 줄임표 ‥ → … (둘이 이어지면 표준 줄임표 ……)
    [InlineData("嗯‥", "嗯…")]
    [InlineData("嗯‥‥", "嗯……")]
    // 이 함수가 원래 손대지 않는 전각 부호·전각 공백·줄임표·반각 부호는 중국어에서도 그대로다 (반각 물결표 ~만 아래 시험대로 ～가 된다)
    [InlineData("你好，世界。「真的」：（是）；『对』、嗯　好……", "你好，世界。「真的」：（是）；『对』、嗯　好……")]
    [InlineData("OK 好的!?...", "OK 好的!?...")]
    [InlineData("", "")]
    public void PolishTranslation_ChineseTarget_EachReplacementFollowsChineseTypography(string text, string expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(text, "zh"));
    }

    [Theory]
    // LLM 프롬프트는 늘인 소리를 반각 ~로 쓰라고 하고 뒷손질은 ー·〜를 전각 ～로 바꾼다 — 반각을 그대로 두면 한 쪽에 두 표기가 섞였다
    [InlineData("啊~ 啊ー 啊〜 啊～", "啊～ 啊～ 啊～ 啊～")]
    [InlineData("啊~", "啊～")]
    [InlineData("啊~~~", "啊～～～")]
    [InlineData("好~!", "好～!")]
    [InlineData("~啊", "～啊")]
    [InlineData("~", "～")]
    [InlineData("诶~ー", "诶～～")]
    // 로마자 뒤라도 글 끝·공백·문장부호 앞이면 늘인 소리다
    [InlineData("OK~ 好的", "OK～ 好的")]
    [InlineData("Hi~", "Hi～")]
    [InlineData("3~", "3～")]
    // 한자 뒤에 로마자가 오는 것은 범위가 아니다
    [InlineData("啊~OK", "啊～OK")]
    // 이어진 ~~는 숫자 사이라도 범위 표시가 아니다
    [InlineData("1~~2", "1～～2")]
    // 띄어 쓴 범위는 ～가 된다(중국어에서는 범위도 ～로 쓴다)
    [InlineData("1 ~ 2", "1 ～ 2")]
    public void PolishTranslation_ChineseTarget_HalfWidthTildeForStretchedSound_BecomesFullWidth(string text, string expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(text, "zh"));
        Assert.Equal(expected, EmbeddedTranslator.PolishTranslation(expected, "zh"));   // 한 번 더 해도 같다
    }

    [Theory]
    // 늘인 소리가 아닌 것이 분명한 ~는 그대로 둔다: 하나짜리 ~ 바로 뒤가 숫자(범위·어림), 또는 앞뒤가 모두 로마자·숫자
    [InlineData("1~2")]
    [InlineData("10~20个")]
    [InlineData("~10")]
    [InlineData("约~10个")]
    [InlineData("１~２")]
    [InlineData("A~Z")]
    [InlineData("v1~v2")]
    [InlineData("3~A")]
    public void PolishTranslation_ChineseTarget_HalfWidthTildeAsRangeOrApproximation_IsKept(string text)
    {
        Assert.Equal(text, EmbeddedTranslator.PolishTranslation(text, "zh"));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    [InlineData("")]
    public void PolishTranslation_NonChineseTarget_HalfWidthTildeStaysHalfWidth(string target)
    {
        // 한국어·영어는 예전 그대로 반각 ~다 — 중국어용 바꾸기가 새어 나오지 않는다
        Assert.Equal("아~ OK~ 1~2 ~10 또~~", EmbeddedTranslator.PolishTranslation("아~ OK~ 1~2 ~10 또ー～", target));
    }

    [Theory]
    // 중국어를 가리키는 코드는 어떤 꼴이든 같은 결과를 낸다 — 구분자 -·_, 지역·문자 꼬리표, 대소문자, ISO 639-2/3 세 글자 코드
    [InlineData("zh")]
    [InlineData("ZH")]
    [InlineData("zh-CN")]
    [InlineData("zh-cn")]
    [InlineData("ZH-CN")]
    [InlineData("zh_CN")]
    [InlineData("zh_cn")]
    [InlineData("zh-TW")]
    [InlineData("zh_TW")]
    [InlineData("zh-HK")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans-CN")]
    [InlineData("zh_Hant_TW")]
    [InlineData("zho")]
    [InlineData("ZHO")]
    [InlineData("chi")]
    [InlineData("cmn")]
    [InlineData("cmn-Hans")]
    [InlineData(" zh ")]
    public void PolishTranslation_AnyChineseCode_PolishesAsChinese(string target)
    {
        Assert.Equal("又～！？那个…… 嗯…", EmbeddedTranslator.PolishTranslation("又ー！？那个・・・ 嗯‥", target));
    }

    [Theory]
    // 한국어·영어·그 밖의 언어·빈 코드는 예전 그대로 반각으로 — 중국어처럼 보이지만 아닌 코드(zha 좡어, zu 줄루어)도 여기다
    [InlineData("ko")]
    [InlineData("KO")]
    [InlineData("ko-KR")]
    [InlineData("kor")]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("en_GB")]
    [InlineData("eng")]
    [InlineData("fr")]
    [InlineData("zha")]
    [InlineData("zu")]
    [InlineData("z")]
    [InlineData("chinese")]
    [InlineData("")]
    [InlineData(null)]
    public void PolishTranslation_NonChineseNonJapaneseCode_PolishesToHalfWidthAsBefore(string? target)
    {
        Assert.Equal("또~~~!? 그건.... 음..", EmbeddedTranslator.PolishTranslation("또ー〜～！？ 그건・・・・ 음‥", target!));
    }

    [Theory]
    // 일본어를 가리키는 코드는 꼴이 달라도 손대지 않는다 — ja-JP를 일본어로 못 알아보면 "ラーメン"이 "ラ~メン"이 된다
    [InlineData("ja-JP")]
    [InlineData("ja_JP")]
    [InlineData("JA-jp")]
    [InlineData("jpn")]
    [InlineData("JPN")]
    public void PolishTranslation_JapaneseCodeVariants_LeaveTextAsIs(string target)
    {
        const string text = "ラーメン！？そう・・・〜‥";
        Assert.Equal(text, EmbeddedTranslator.PolishTranslation(text, target));
    }

    [Theory]
    // 글자가 있을 수 없는 조각: 비었거나, 한 가지 색이거나, 검은 쪽의 옅은 노이즈뿐
    [InlineData("empty", true)]
    [InlineData("white", true)]
    [InlineData("black-with-faint-noise", true)]
    [InlineData("gray-single-channel", true)]
    // 획이 있는 조각은 밝기가 크게 흩어진다 — 흰 바탕의 검은 글자, 검은 바탕의 흰 글자, 회색조(1채널) 조각 모두
    [InlineData("black-glyphs-on-white", false)]
    [InlineData("white-glyphs-on-black", false)]
    [InlineData("glyphs-single-channel", false)]
    public void IsFlatCrop_UniformOrStrokedCrop_FlatOnlyWithoutStrokes(string kind, bool expectedFlat)
    {
        using var crop = MakeCrop(kind);
        Assert.Equal(expectedFlat, EmbeddedTranslator.IsFlatCrop(crop));
    }

    private static Mat MakeCrop(string kind)
    {
        switch (kind)
        {
            case "empty":
                return new Mat();
            case "white":
                return new Mat(60, 80, MatType.CV_8UC3, Scalar.White);
            case "black-with-faint-noise":
            {
                // 밝기 0과 4가 바둑판으로 섞인 검은 조각 (표준편차 2)
                var m = new Mat(60, 80, MatType.CV_8UC3, Scalar.Black);
                for (int y = 0; y < 60; y++)
                    for (int x = y % 2; x < 80; x += 2)
                        m.Set(y, x, new Vec3b(4, 4, 4));
                return m;
            }
            case "gray-single-channel":
                return new Mat(60, 80, MatType.CV_8UC1, new Scalar(128));
            case "black-glyphs-on-white":
            {
                var m = new Mat(60, 80, MatType.CV_8UC3, Scalar.White);
                DrawGlyphs(m, null, 8, 10, 3, 2);
                return m;
            }
            case "white-glyphs-on-black":
            {
                var m = new Mat(60, 80, MatType.CV_8UC3, Scalar.Black);
                DrawGlyphs(m, null, 8, 10, 3, 2, ink: 255);
                return m;
            }
            case "glyphs-single-channel":
            {
                var m = new Mat(60, 80, MatType.CV_8UC1, new Scalar(255));
                DrawGlyphs(m, null, 8, 10, 3, 2);
                return m;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    [Theory]
    // 일본어: 가나(반각 가타카나 포함)·한자
    [InlineData("こんにちは", "ja", true)]
    [InlineData("カタカナ", "ja", true)]
    [InlineData("ｶﾀｶﾅ", "ja", true)]
    [InlineData("漢字", "ja", true)]
    [InlineData("ええっ!?", "JA", true)]
    // 영문 손글씨·환각: 가나가 한 글자 섞여도 절반이 안 되면 원문이 아니다 (문서 주석의 'Ekゅ')
    [InlineData("END", "ja", false)]
    [InlineData("Ekゅ", "ja", false)]
    [InlineData("あEND", "ja", false)]
    // 정확히 절반이면 받아들인다 ("절반 이상")
    [InlineData("あいEN", "ja", true)]
    // 문장부호·공백·숫자만 있으면 번역할 글이 없다
    [InlineData("！？…　", "ja", false)]
    [InlineData("123", "ja", false)]
    [InlineData("", "ja", false)]
    // 한국어는 한글·한자, 중국어는 한자만
    [InlineData("안녕하세요", "ko", true)]
    [InlineData("漢字", "ko", true)]
    [InlineData("こんにちは", "ko", false)]
    [InlineData("你好", "zh", true)]
    [InlineData("你好", "zh-TW", true)]
    [InlineData("こんにちは", "zh", false)]
    // 언어 코드의 꼴이 달라도 같은 판정이다 (번역문 뒷손질과 같은 LangCode) — 예전에는 zh·zh-cn·zh-tw 말고는 '그 밖의 언어'로 떨어져 가나도 받았다
    [InlineData("你好", "ZH-CN", true)]
    [InlineData("你好", "zh_CN", true)]
    [InlineData("你好", "zh-Hant", true)]
    [InlineData("你好", "zho", true)]
    [InlineData("こんにちは", "zh-Hant", false)]
    [InlineData("こんにちは", "zh_CN", false)]
    [InlineData("こんにちは", "chi", false)]
    [InlineData("こんにちは", "ja-JP", true)]
    [InlineData("END", "ja-JP", false)]
    [InlineData("END", "jpn", false)]
    [InlineData("안녕하세요", "ko-KR", true)]
    [InlineData("こんにちは", "ko_KR", false)]
    // 그 밖의 언어(또는 언어 코드 없음)는 문자(Letter)면 된다
    [InlineData("Hello", "en", true)]
    [InlineData("12345", "en", false)]
    [InlineData("Hello", null, true)]
    public void LooksLikeSource_MixedScripts_TrueWhenHalfOrMoreLettersAreSourceScript(string text, string? lang, bool expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.LooksLikeSource(text, lang!));
    }
}
