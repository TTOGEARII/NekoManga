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

    [Theory(Skip = "알려진 결함: 가운뎃점이 네 개 이상 이어지면 앞의 세 개만 마침표로 바뀌고 나머지 가운뎃점이 남는다 (\"・・・・\" → \"...・\")")]
    [InlineData("그건・・・・")]
    [InlineData("그건・・・・・")]
    public void PolishTranslation_MiddleDotEllipsisLongerThanThree_LeavesNoMiddleDot(string text)
    {
        // 가운뎃점 말줄임은 가로 식자에서 점이 띄엄띄엄 그려진다 — 세 개보다 길게 이어져도 가운뎃점이 하나라도 남으면 같은 문제가 생긴다
        Assert.DoesNotContain("・", EmbeddedTranslator.PolishTranslation(text, "ko"));
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
    // 그 밖의 언어(또는 언어 코드 없음)는 문자(Letter)면 된다
    [InlineData("Hello", "en", true)]
    [InlineData("12345", "en", false)]
    [InlineData("Hello", null, true)]
    public void LooksLikeSource_MixedScripts_TrueWhenHalfOrMoreLettersAreSourceScript(string text, string? lang, bool expected)
    {
        Assert.Equal(expected, EmbeddedTranslator.LooksLikeSource(text, lang!));
    }
}
