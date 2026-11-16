using System.Globalization;

namespace NekoManga.Core.Tests;

/// <summary>Loc — 화면 글의 번역(소스의 한국어 글이 열쇠). 여기서는 표를 직접 주는 순수 함수만 본다 —
/// 실제 번역표(Lang/*.json)가 소스의 글을 다 덮는지는 LocCoverageTests가 본다.</summary>
public class LocTests
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["대기"] = "Waiting",
        ["빈 번역"] = "",
        ["고친 {0}장 다시 렌더링"] = "Rerender {0} edited pages",
        ["{0}쪽 중 {1}쪽"] = "page {1} of {0}",
        ["{0:0.0}초 걸렸어요"] = "took {0:0.0} s",
        ["자리가 모자란 번역 {0}"] = "broken {0} {1}",
        ["중괄호 {{그대로}} {0}"] = "braces {{kept}} {0}",
    };

    [Fact]
    public void Translate_NoTable_ReturnsTheSource() => Assert.Equal("대기", Loc.Translate((IReadOnlyDictionary<string, string>?)null, "대기"));

    [Fact]
    public void Translate_KnownKey_ReturnsTheTranslation() => Assert.Equal("Waiting", Loc.Translate(En, "대기"));

    [Fact]
    public void Translate_UnknownKey_ReturnsTheSource() => Assert.Equal("표에 없는 글", Loc.Translate(En, "표에 없는 글"));

    [Fact]
    public void Translate_EmptyTranslation_ReturnsTheSource()
    {
        // 비워 둔 번역은 '아직 없음'이다 — 빈 글을 화면에 내면 버튼이 사라진 것처럼 보인다
        Assert.Equal("빈 번역", Loc.Translate(En, "빈 번역"));
    }

    [Fact]
    public void Translate_Null_StaysNull_WithOrWithoutATable()
    {
        // 한국어(표 없음)에서는 null이 그대로 나가는데 다른 언어에서만 던지면, 한국어로만 돌려 본 코드가 영어 화면에서 죽는다
        Assert.Null(Loc.Translate(En, null!));
        Assert.Null(Loc.Translate((IReadOnlyDictionary<string, string>?)null, null!));
    }

    [Fact]
    public void Format_UsesTheTranslatedPattern()
    {
        int n = 3;
        Assert.Equal("Rerender 3 edited pages", Loc.Format(En, $"고친 {n}장 다시 렌더링"));
    }

    [Fact]
    public void Format_TranslationMayReorderTheArguments()
    {
        int total = 12, at = 5;
        Assert.Equal("page 5 of 12", Loc.Format(En, $"{total}쪽 중 {at}쪽"));
    }

    [Fact]
    public void Format_KeepsFormatSpecifiers()
    {
        double sec = 1.5;
        Assert.Equal($"took {sec.ToString("0.0")} s", Loc.Format(En, $"{sec:0.0}초 걸렸어요"));   // 소수점은 PC의 지역 설정을 따른다(쉼표인 곳도 있다)
    }

    [Fact]
    public void Format_EscapedBraces_StayLiteral()
    {
        int n = 7;
        Assert.Equal("braces {kept} 7", Loc.Format(En, $"중괄호 {{그대로}} {n}"));
    }

    [Fact]
    public void Format_UnknownPattern_ReturnsTheSourceText()
    {
        int n = 2;
        Assert.Equal("표에 없는 2번 글", Loc.Format(En, $"표에 없는 {n}번 글"));
    }

    [Fact]
    public void Format_NoTable_ReturnsTheSourceText()
    {
        int n = 2;
        Assert.Equal("고친 2장 다시 렌더링", Loc.Format((IReadOnlyDictionary<string, string>?)null, $"고친 {n}장 다시 렌더링"));
    }

    [Fact]
    public void Format_TranslationAsksForAMissingArgument_FallsBackToTheSourceText()
    {
        // 번역이 원문에 없는 자리({1})를 쓰면 string.Format이 던진다 — 화면 글 하나 때문에 작업이 죽으면 안 된다
        int n = 4;
        Assert.Equal("자리가 모자란 번역 4", Loc.Format(En, $"자리가 모자란 번역 {n}"));
    }

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("EN", "en")]
    [InlineData(" ja ", "ja")]
    public void Resolve_ChosenLanguage_Wins(string setting, string expected) =>
        Assert.Equal(expected, Loc.Resolve(setting, CultureInfo.GetCultureInfo("fr-FR")));

    [Theory]
    [InlineData("ko-KR", "ko")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("zh-CN", "en")]
    [InlineData("fr-FR", "en")]
    public void Resolve_NothingChosen_FollowsTheWindowsLanguage(string culture, string expected)
    {
        // 한국어·일본어 Windows가 아니면 영어 — 한국어를 못 읽는 사람이 첫 화면에서 막히지 않게
        Assert.Equal(expected, Loc.Resolve("", CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, Loc.Resolve(null, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Resolve_UnknownSetting_FollowsTheWindowsLanguage() =>
        Assert.Equal("ja", Loc.Resolve("fr", CultureInfo.GetCultureInfo("ja-JP")));

    [Fact]
    public void Languages_AreKoreanEnglishJapanese_InThatOrder() =>
        Assert.Equal(new[] { "ko", "en", "ja" }, Loc.Languages);

    [Theory]
    [InlineData("ko", "한국어")]
    [InlineData("en", "English")]
    [InlineData("ja", "日本語")]
    public void NativeName_IsWrittenInThatLanguage(string lang, string expected) => Assert.Equal(expected, Loc.NativeName(lang));

    [Fact]
    public void Table_Korean_IsEmpty_TheSourceIsKorean() => Assert.Null(Loc.Table("ko"));

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Table_ShippedLanguages_AreLoadedFromTheAssembly(string lang)
    {
        var table = Loc.Table(lang);

        Assert.NotNull(table);
        Assert.NotEmpty(table);
        Assert.Same(table, Loc.Table(lang));   // 한 번만 읽는다
    }

    [Fact]
    public void Table_UnknownLanguage_IsNull() => Assert.Null(Loc.Table("fr"));
}

/// <summary>Loc의 현재 언어는 프로세스 전체가 같이 쓴다 — 다른 시험(한국어 글을 그대로 견주는 시험들)과 겹치지 않게 따로 돈다.</summary>
[Collection(EnvCollection.Name)]
public class LocCurrentLanguageTests
{
    [Fact]
    public void DefaultLanguage_IsKorean_AndTReturnsTheSource()
    {
        Assert.Equal("ko", Loc.Lang);
        Assert.Equal("검열 제거 차례 대기", Loc.T("검열 제거 차례 대기"));
        int n = 3;
        Assert.Equal("고친 3장", Loc.F($"고친 {n}장"));
    }

    [Fact]
    public void SetLanguage_ChangesLang_AndTellsListenersOnce()
    {
        int told = 0;
        void OnChanged() => told++;
        Loc.Changed += OnChanged;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("en", Loc.Lang);
            Assert.Equal(1, told);

            Loc.SetLanguage("en");          // 같은 언어 — 다시 알리지 않는다
            Assert.Equal(1, told);

            Loc.SetLanguage("없는 언어");     // 모르는 값은 한국어로
            Assert.Equal("ko", Loc.Lang);
            Assert.Equal(2, told);
        }
        finally
        {
            Loc.Changed -= OnChanged;
            Loc.SetLanguage("ko");
        }
    }

    [Fact]
    public void SetLanguage_OneListenerThrows_TheOthersAreStillTold_AndTheErrorSurfaces()
    {
        int told = 0;
        void Bad() => throw new InvalidOperationException("듣는 쪽이 터졌다");
        void Good() => told++;
        Loc.Changed += Bad;
        Loc.Changed += Good;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Loc.SetLanguage("ja"));

            Assert.Equal("듣는 쪽이 터졌다", ex.Message);
            Assert.Equal(1, told);            // 뒤에 선 창도 새 언어를 들었다
            Assert.Equal("ja", Loc.Lang);     // 언어는 바뀌었다
        }
        finally
        {
            Loc.Changed -= Bad;
            Loc.Changed -= Good;
            Loc.SetLanguage("ko");
        }
    }

    [Fact]
    public void Key_ReturnsTheSource_EvenInAnotherLanguage()
    {
        // 표시만 하는 것이다 — 번역은 보여 줄 때 Loc.T가 한다(정적 목록은 언어가 정해지기 전에 만들어질 수 있다)
        var (ko, _) = Loc.Table("en")!.First(p => p.Value.Length > 0 && !p.Key.Contains('{'));
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal(ko, Loc.Key(ko));
        }
        finally { Loc.SetLanguage("ko"); }
    }

    [Fact]
    public void T_UsesTheCurrentLanguageTable()
    {
        // 표에 실제로 든 글 하나로 — 현재 언어가 바뀌면 T가 그 표를 본다
        var en = Loc.Table("en")!;
        var (ko, translated) = en.First(p => p.Value.Length > 0 && !p.Key.Contains('{'));
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal(translated, Loc.T(ko));
        }
        finally { Loc.SetLanguage("ko"); }
        Assert.Equal(ko, Loc.T(ko));
    }
}
