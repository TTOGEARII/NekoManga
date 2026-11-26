using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>내장 의성어·의태어 사전(ja→ko, 2026-11-26 효과음 식자 시험 기능) — 번역기에 보내기 전에 흔한 효과음은 여기서 바로 옮긴다.
/// 찾기는 NFKC·가타카나→히라가나·채움 글자 떼기로 맞추고, 되풀이("ドキドキドキ")는 단위로 접어 그만큼 되풀이한다.</summary>
public class SfxDictionaryTests
{
    [Theory]
    [InlineData("ドキドキ", "두근두근")]
    [InlineData("どきどき", "두근두근")]            // 히라가나도 같은 항목
    [InlineData("ドキドキドキ", "두근두근두근")]     // 되풀이 단위("ドキ")를 접고 그만큼 되풀이
    [InlineData("ドキドキ！！", "두근두근")]         // 문장부호는 뗀다
    [InlineData("ザワザワ", "웅성웅성")]
    [InlineData("ゴゴゴゴ", "고고고고")]             // 사전에는 셋짜리("ゴゴゴ")만 있어도 넷을 만든다
    [InlineData("ニコッ", "씨익")]
    [InlineData("ｶﾞﾀｯ", "덜컥")]                     // 반각 가타카나(NFKC)
    public void Lookup_CommonSounds_InEitherKana(string source, string expected)
    {
        Assert.Equal(expected, SfxDictionary.Lookup(source, "ko"));
    }

    [Theory]
    [InlineData("知らない言葉")]
    [InlineData("")]
    [InlineData("…")]
    public void Lookup_Unknown_IsNull(string source)
    {
        Assert.Null(SfxDictionary.Lookup(source, "ko"));
    }

    [Fact]
    public void Lookup_OtherTargetLanguage_IsNull()
    {
        // 표는 한국어뿐이다 — 영어 목표면 번역기로 간다
        Assert.Null(SfxDictionary.Lookup("ドキドキ", "en"));
    }

    [Fact]
    public void Normalize_FoldsKanaWidthScriptAndFillers()
    {
        Assert.Equal("どきどき", SfxDictionary.Normalize("ドキドキ！？…"));
        Assert.Equal("がーん", SfxDictionary.Normalize("ガーーーン"));   // 장음은 하나로 접되 남긴다(ガン과 다른 소리)
        Assert.Equal("がーん", SfxDictionary.Normalize("ガ～ン"));       // 전각 물결표도 장음(NFKC가 ~로 바꾼다 — 검수 2026-11-26)
        Assert.Equal("がたっ", SfxDictionary.Normalize("ｶﾞﾀｯ"));
    }
}
