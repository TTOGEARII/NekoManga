using System.Text.Json;

namespace NekoManga.Core.Tests;

/// <summary>AppSettings — 기본값, settings.json 저장·읽기, 옛 값 이관(Load 안의 MigrateLegacy).
/// Load/Save는 NEKO_SETTINGS로 임시 폴더의 파일을 가리킨 상태에서만 부른다 — 사용자의 %AppData% 설정을 건드리지 않게.</summary>
[Collection("Env")]
public class AppSettingsTests
{
    private static AppSettings LoadFrom(string path) => RuntimeTestHelpers.WithEnv("NEKO_SETTINGS", path, AppSettings.Load);

    private static void SaveTo(AppSettings s, string path) => RuntimeTestHelpers.WithEnv("NEKO_SETTINGS", path, s.Save);

    /// <summary>사용자가 settings.json을 손으로 고친 것처럼 JSON을 그대로 써 두고 읽는다.</summary>
    private static AppSettings LoadJson(TempDir t, string json)
    {
        var path = t.At("settings.json");
        File.WriteAllText(path, json);
        return LoadFrom(path);
    }

    private static object? Value(AppSettings s, string property) => typeof(AppSettings).GetProperty(property)!.GetValue(s);

    // RuntimeRoot 기본값은 만들 때마다 여유 공간이 가장 큰 드라이브를 고르므로, 따로 만든 두 기본 설정을 비교할 때는 뺀다
    private const string MachineDependent = nameof(AppSettings.RuntimeRoot);

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithoutCreatingIt()
    {
        using var t = new TempDir();
        var path = t.At("아직 없는 설정.json");

        var s = LoadFrom(path);

        // 첫 실행은 기본값으로 시작하고, 읽기만으로는 파일을 만들지 않는다
        Assert.Empty(RuntimeTestHelpers.Differences(new AppSettings(), s, MachineDependent));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("설정 파일 아님")]
    [InlineData("{\"ColorSteps\": \"많이\"}")]
    public void Load_CorruptFile_FallsBackToDefaults(string content)
    {
        using var t = new TempDir();

        var s = LoadJson(t, content);

        // 손상된 설정 때문에 앱이 켜지지 않는 일이 없게 — 통째로 기본값
        Assert.Empty(RuntimeTestHelpers.Differences(new AppSettings(), s, MachineDependent));
    }

    [Fact]
    public void SaveThenLoad_DefaultSettings_ComeBackUnchanged()
    {
        using var t = new TempDir();
        var path = t.At("settings.json");
        var d = new AppSettings();

        SaveTo(d, path);

        // 기본값이 Load의 허용 목록·범위 안에 있어야 한다 — 아니면 처음 저장한 설정이 다음 실행에서 다른 값으로 바뀐다
        Assert.Empty(RuntimeTestHelpers.Differences(d, LoadFrom(path)));
    }

    [Fact]
    public void SaveThenLoad_EveryPersistedValue_RoundTrips()
    {
        using var t = new TempDir();
        var path = t.At("settings.json");
        var s = RuntimeTestHelpers.NonDefaultSettings();
        // 전부 기본값과 다르게 채워야 '저장되지 않고 기본값으로 돌아온 속성'을 잡을 수 있다
        Assert.Equal(RuntimeTestHelpers.PersistedProperties.Count, RuntimeTestHelpers.Differences(new AppSettings(), s).Count);

        SaveTo(s, path);

        Assert.Empty(RuntimeTestHelpers.Differences(s, LoadFrom(path)));
    }

    [Theory]
    [InlineData(nameof(AppSettings.TranslateSource), "日本語", "ja")]
    [InlineData(nameof(AppSettings.TranslateSource), "English", "en")]
    [InlineData(nameof(AppSettings.TranslateSource), "简体中文", "zh")]
    [InlineData(nameof(AppSettings.TranslateTarget), "한국어", "ko")]
    [InlineData(nameof(AppSettings.TranslateTarget), "English", "en")]
    public void Load_LegacyLanguageName_MigratedToCode(string property, string legacy, string code)
    {
        using var t = new TempDir();

        // BallonsTranslator 연동 시절(v0.1.x)에는 언어를 표시 이름으로 저장했다 — 내장 엔진은 언어 코드를 쓴다
        var s = LoadJson(t, $"{{\"{property}\": \"{legacy}\"}}");

        Assert.Equal(code, Value(s, property));
    }

    [Theory]
    [InlineData(nameof(AppSettings.TranslateEngine), "\"Papago\"")]       // 옛 BallonsTranslator 엔진
    [InlineData(nameof(AppSettings.TranslateEngine), "null")]
    [InlineData(nameof(AppSettings.DecensorMode), "\"Strong\"")]          // 폐기된 SDXL+LoRA 모드
    [InlineData(nameof(AppSettings.DecensorMode), "\"DeepCream\"")]       // 폐기된 DeepCreamPy 모드
    [InlineData(nameof(AppSettings.DecensorLegacyAssist), "\"Black\"")]
    [InlineData(nameof(AppSettings.DecensorAssistRepair), "\"PConv\"")]
    [InlineData(nameof(AppSettings.OnnxGpu), "\"Maybe\"")]
    [InlineData(nameof(AppSettings.GpuVendor), "\"intel\"")]
    [InlineData(nameof(AppSettings.DecensorReviewMode), "\"Batch\"")]
    [InlineData(nameof(AppSettings.LetterAlign), "\"Justify\"")]
    [InlineData(nameof(AppSettings.RegenSampler), "\"   \"")]             // 빈 샘플러 이름은 워크플로를 터뜨린다
    [InlineData(nameof(AppSettings.RegenInpaintControl), "\"Inpaint\"")]
    [InlineData(nameof(AppSettings.RegenScheduler), "\"\"")]
    [InlineData(nameof(AppSettings.LetterWordSpacing), "0")]              // 0 이하는 '좁게'가 아니라 깨진 값
    [InlineData(nameof(AppSettings.LetterWordSpacing), "-1.5")]
    public void Load_InvalidValue_FallsBackToDefault(string property, string json)
    {
        using var t = new TempDir();

        var s = LoadJson(t, $"{{\"{property}\": {json}}}");

        // 되돌리는 값은 그 속성의 기본값과 같아야 한다 — 기본값을 바꾸면서 이관 규칙의 대체값을 그대로 두면 여기서 어긋난다
        Assert.Equal(Value(new AppSettings(), property), Value(s, property));
    }

    [Theory]
    [InlineData(nameof(AppSettings.TranslateEngine), "DeepL")]
    [InlineData(nameof(AppSettings.TranslateEngine), "DeepSeek")]
    [InlineData(nameof(AppSettings.TranslateEngine), "Grok")]
    [InlineData(nameof(AppSettings.DecensorMode), "ComfyRegen")]
    [InlineData(nameof(AppSettings.DecensorMode), "ComfyCustom")]
    [InlineData(nameof(AppSettings.RegenInpaintControl), "Repaint")]
    [InlineData(nameof(AppSettings.DecensorLegacyAssist), "WhiteMosaic")]
    [InlineData(nameof(AppSettings.DecensorLegacyAssist), "All")]
    [InlineData(nameof(AppSettings.DecensorLegacyAssist), "None")]
    [InlineData(nameof(AppSettings.DecensorAssistRepair), "Lama")]
    [InlineData(nameof(AppSettings.OnnxGpu), "On")]
    [InlineData(nameof(AppSettings.OnnxGpu), "Off")]
    [InlineData(nameof(AppSettings.GpuVendor), "nvidia")]
    [InlineData(nameof(AppSettings.GpuVendor), "nvidia-old")]
    [InlineData(nameof(AppSettings.GpuVendor), "amd")]
    [InlineData(nameof(AppSettings.DecensorReviewMode), "Window")]
    [InlineData(nameof(AppSettings.LetterAlign), "Left")]
    [InlineData(nameof(AppSettings.LetterAlign), "Right")]
    public void Load_DocumentedChoice_IsKept(string property, string choice)
    {
        using var t = new TempDir();

        // 문서 주석에 적힌 선택지를 이관 규칙이 지우면 사용자가 고른 값이 다음 실행에서 사라진다
        var s = LoadJson(t, $"{{\"{property}\": \"{choice}\"}}");

        Assert.Equal(choice, Value(s, property));
    }

    [Theory]
    [InlineData(nameof(AppSettings.CustomDecensorReach), "-5", 0.0)]
    [InlineData(nameof(AppSettings.CustomDecensorReach), "9999", 512.0)]
    [InlineData(nameof(AppSettings.SelfTrainEpochs), "0", 1.0)]
    [InlineData(nameof(AppSettings.SelfTrainEpochs), "500", 50.0)]
    [InlineData(nameof(AppSettings.SelfTrainLr), "0", 0.00001)]
    [InlineData(nameof(AppSettings.SelfTrainLr), "1", 0.01)]
    [InlineData(nameof(AppSettings.SelfTrainFreeze), "-3", 0.0)]
    [InlineData(nameof(AppSettings.SelfTrainFreeze), "99", 22.0)]
    [InlineData(nameof(AppSettings.SelfTrainMinPages), "1", 5.0)]
    [InlineData(nameof(AppSettings.SelfTrainMinPages), "100000", 500.0)]
    [InlineData(nameof(AppSettings.RegenSteps), "0", 1.0)]
    [InlineData(nameof(AppSettings.RegenSteps), "1000", 150.0)]
    [InlineData(nameof(AppSettings.RegenCfg), "-2", 0.0)]
    [InlineData(nameof(AppSettings.RegenCfg), "99", 30.0)]
    [InlineData(nameof(AppSettings.RegenDenoise), "0", 0.05)]
    [InlineData(nameof(AppSettings.RegenDenoise), "1.5", 1.0)]
    [InlineData(nameof(AppSettings.RegenCnStrength), "9", 2.0)]
    [InlineData(nameof(AppSettings.RegenInpaintCnStrength), "-1", 0.0)]   // 클램프 하한(기본값 0.25와 다르다)
    [InlineData(nameof(AppSettings.RegenCnEnd), "1.2", 1.0)]
    [InlineData(nameof(AppSettings.RegenPreprocResolution), "100", 256.0)]
    [InlineData(nameof(AppSettings.RegenPreprocResolution), "99999", 4096.0)]
    [InlineData(nameof(AppSettings.RegenEdgeGrow), "500", 128.0)]
    [InlineData(nameof(AppSettings.RegenCoreBlur), "-1", 0.0)]
    [InlineData(nameof(AppSettings.CnLineartStrength), "3", 2.0)]
    [InlineData(nameof(AppSettings.CnLineartEnd), "-0.5", 0.0)]           // 적용 구간은 0~1
    [InlineData(nameof(AppSettings.CnScribbleStart), "2", 1.0)]
    [InlineData(nameof(AppSettings.PreprocResolution), "64", 256.0)]
    [InlineData(nameof(AppSettings.PreprocResolution), "8192", 4096.0)]
    [InlineData(nameof(AppSettings.ScribbleXdogThreshold), "0", 1.0)]
    [InlineData(nameof(AppSettings.ScribbleXdogThreshold), "999", 128.0)]
    [InlineData(nameof(AppSettings.IpaEndAt), "1.5", 1.0)]                // 적용 끝 지점은 0~1
    [InlineData(nameof(AppSettings.LetterLineSpacing), "0.1", 0.6)]
    [InlineData(nameof(AppSettings.LetterLineSpacing), "10", 3.0)]
    [InlineData(nameof(AppSettings.LetterWordSpacing), "0.1", 0.3)]
    [InlineData(nameof(AppSettings.LetterWordSpacing), "5", 2.0)]
    [InlineData(nameof(AppSettings.RegenRegionConf), "5", 0.9)]           // 부위 감지 임계 — 재생성 영역(2026-11-12)
    [InlineData(nameof(AppSettings.RegenRegionConf), "0", 0.01)]
    [InlineData(nameof(AppSettings.RegenRegionPad), "-1", 0.0)]
    [InlineData(nameof(AppSettings.RegenRegionPad), "3", 1.0)]
    [InlineData(nameof(AppSettings.RegenRegionGrow), "99", 3.0)]
    [InlineData(nameof(AppSettings.RegenRegionGrow), "-2", 0.0)]
    [InlineData(nameof(AppSettings.RegenCropSize), "100", 512.0)]            // 재생성 v3 자를 크기 (2026-11-12)
    [InlineData(nameof(AppSettings.RegenCropSize), "9999", 2048.0)]
    [InlineData(nameof(AppSettings.RegenInpaintGrow), "-1", 0.0)]
    [InlineData(nameof(AppSettings.RegenInpaintGrow), "99", 64.0)]
    public void Load_OutOfRangeNumber_ClampedToNearestBound(string property, string json, double expected)
    {
        using var t = new TempDir();

        // 손으로 고치다 깨진 값이 워크플로·학습 인자를 터뜨리지 않게 허용 범위의 가까운 끝으로 맞춘다
        var s = LoadJson(t, $"{{\"{property}\": {json}}}");

        Assert.Equal(expected, Convert.ToDouble(Value(s, property)));
    }


    [Fact]
    public void Defaults_ResultGuideOpen_IsFalse_SoTheGuideCardStartsFolded()
    {
        // 결과 창의 안내 카드는 접힌 채로 시작한다(2026-11-20 사용자 "접었다 펼 수 있는 기능으로") — 펼치면 설정에 남아 다음 창도 펼쳐진다
        Assert.False(new AppSettings().ResultGuideOpen);
    }

    [Fact]
    public void Defaults_RegenRegion_WholeRegionOnWithEvaluatedValues()
    {
        // 재생성·커스텀의 부위 통째 다시 그리기(2026-11-12): 라벨 원고 144쪽 평가에서 고른 값 — 임계 0.12(검은 94%·모자이크 79%), 여백 10%, 조각 확장 0.6배
        var s = new AppSettings();
        Assert.True(s.RegenWholeRegion);
        Assert.Equal(0.12, s.RegenRegionConf);
        Assert.Equal(0.10, s.RegenRegionPad);
        Assert.Equal(0.6, s.RegenRegionGrow);
    }

    // ── 설정 판(SettingsVersion, 2026-11-20 사용자 "다른 사람들이 릴리스 파일을 설치했을 때 기본값이 0.25가 되게") ──
    // 기본값을 바꿔도 업데이트한 사람의 settings.json에는 옛 기본값이 그대로 적혀 있다. 값만 보고는 '옛 기본값'과 '사용자가 고른 값'을 가를 수 없어,
    // 저장할 때 앱의 설정 판을 파일에 찍고, 판이 없는(옛) 파일에서 옛 기본값과 똑같은 값만 새 기본값으로 옮긴다.

    [Theory]
    [InlineData("0", 0.25)]     // v1.14.0~v1.14.1의 기본값
    [InlineData("0.5", 0.25)]   // v1.9.0~v1.13.x의 기본값
    [InlineData("0.3", 0.3)]    // 기본값이 아니었던 값은 사용자가 고른 것 — 그대로
    [InlineData("1", 1.0)]
    public void Load_FileWithoutSettingsVersion_MovesTheOldCnDefaultsTo025_KeepsChosenValues(string json, double expected)
    {
        using var t = new TempDir();
        var s = LoadJson(t, $"{{\"RegenInpaintCnStrength\": {json}}}");
        Assert.Equal(expected, s.RegenInpaintCnStrength);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.5", 0.5)]
    public void Load_FileWithTheCurrentSettingsVersion_KeepsZeroAndHalf(string json, double expected)
    {
        // 판이 찍힌 뒤에 0이나 0.5로 돌려 둔 것은 사용자의 선택이다 — 읽을 때마다 옮기지 않는다
        using var t = new TempDir();
        var s = LoadJson(t, $"{{\"SettingsVersion\": {AppSettings.CurrentSettingsVersion}, \"RegenInpaintCnStrength\": {json}}}");
        Assert.Equal(expected, s.RegenInpaintCnStrength);
    }

    [Fact]
    public void Save_StampsTheCurrentSettingsVersion()
    {
        // 처음 저장(첫 실행)부터 판이 찍혀야 그 뒤 사용자가 0으로 바꾼 값이 다음 실행에서 0.25로 돌아가지 않는다
        using var t = new TempDir();
        var path = t.At("settings.json");
        var s = new AppSettings();
        Assert.Equal(0, s.SettingsVersion);   // 아직 저장하지 않은 새 설정 = 옛 파일과 같은 0

        SaveTo(s, path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(AppSettings.CurrentSettingsVersion, doc.RootElement.GetProperty(nameof(AppSettings.SettingsVersion)).GetInt32());
        Assert.Equal(AppSettings.CurrentSettingsVersion, LoadFrom(path).SettingsVersion);
    }

    [Fact]
    public void Load_OldFile_ThenTheUserPicksZero_StaysZeroOnTheNextLoad()
    {
        // 옛 파일을 읽으면 0 → 0.25. 저장하면 판이 찍히므로, 그 뒤 사용자가 0으로 돌려 저장한 것은 다음 실행에서도 0
        using var t = new TempDir();
        var path = t.At("settings.json");
        var s = LoadJson(t, "{\"RegenInpaintCnStrength\": 0}");
        Assert.Equal(0.25, s.RegenInpaintCnStrength);

        s.RegenInpaintCnStrength = 0;
        SaveTo(s, path);

        Assert.Equal(0, LoadFrom(path).RegenInpaintCnStrength);
    }


    // ── 재생성 v3 (2026-11-12): 종류별 태그·자를 크기·인페인트 확장, 옛 한 줄 태그 이관 ──

    [Theory]
    [InlineData("clitoris, pussy, vulva, cum in pussy", "clitoris, pussy, vulva", "cum in pussy")]   // v1.10.0까지의 기본값
    [InlineData("cum_in_pussy,  Cum In Pussy , labia", "labia", "cum in pussy")]
    [InlineData("pussy,vulva", "pussy,vulva", "")]   // 사용자가 그 태그를 이미 빼 두었다 — 사정 칸을 비워 자동 판단으로 다시 넣지 않는다(여성 줄은 손대지 않는다)
    [InlineData("", "", "")]
    public void Load_JsonFromBeforeTheCumBox_TakesCumInPussyOutOfTheFemaleTags(string female, string expected, string cumBox)
    {
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTagsFemale\": \"" + female + "\"}");
        Assert.Equal(expected, s.RegenBodyTagsFemale);
        Assert.Equal(cumBox, s.RegenBodyTagsCum);
    }

    [Fact]
    public void Load_JsonWithoutAnyTagBoxes_KeepsTheNewDefaults()
    {
        // 여성 칸도 없던 json(기본값이 들어온다) — 사용자가 뺀 것이 아니다
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenSteps\": 20}");
        Assert.Equal("clitoris, pussy, vulva", s.RegenBodyTagsFemale);
        Assert.Equal("cum in pussy", s.RegenBodyTagsCum);
    }

    [Fact]
    public void Load_JsonWithTheCumBox_LeavesTheFemaleTagsAlone()
    {
        // 칸이 생긴 뒤에 사용자가 여성 태그에 다시 적어 넣은 것은 사용자의 값이다 — 읽을 때마다 떼지 않는다. 비워 둔 사정 칸도 그대로(어디에도 넣지 않는다)
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTagsFemale\": \"pussy, cum in pussy\", \"RegenBodyTagsCum\": \"\", \"RegenCumThreshold\": 7}");
        Assert.Equal("pussy, cum in pussy", s.RegenBodyTagsFemale);
        Assert.Equal("", s.CumTags);
        Assert.Equal(0.95, s.RegenCumThreshold);   // 손으로 고친 값은 범위 안으로
    }

    [Fact]
    public void Defaults_RegenV3()
    {
        var s = new AppSettings();
        Assert.Equal("penis, glans", s.RegenBodyTagsMale);
        Assert.Equal("clitoris, pussy, vulva", s.RegenBodyTagsFemale);   // 'cum in pussy'는 사정 태그 칸으로 떼었다(2026-11-16) — 여성 부위마다 늘 들어가지 않게
        Assert.Equal("cum in pussy", s.RegenBodyTagsCum);
        Assert.Equal("cum in pussy", s.CumTags);
        Assert.True(s.RegenCumAuto);
        Assert.Equal(0.35, s.RegenCumThreshold);
        Assert.Equal(1024, s.RegenCropSize);
        Assert.Equal(8, s.RegenInpaintGrow);
        Assert.Equal("", s.RegenBodyTags);   // 옛 칸 — 이관 뒤 비어 있다
    }

    // ── 재생성 모양 (2026-11-13 사용자 판정): 원고 4쪽 × 후보 4개(지금 / 막대만 지우기 / 막대만+남녀 태그 / 남녀 태그만)를 나란히 보고 "막대만+남녀 태그가 제일 잘 나왔다" ──

    [Fact]
    public void Defaults_RegenShape_BarOnlyErase_TagsSplitBySexWhenSure()
    {
        var s = new AppSettings();
        Assert.True(s.RegenEraseTight);          // 1단계는 검은 막대 픽셀만 지운다
        Assert.True(s.RegenTagsBySex);           // 한쪽 성별로 확정된 영역에는 그쪽 태그만(2026-11-15) — 나머지는 남녀를 같이
        Assert.False(s.RegenTagsPerPiece);       // 자리별 태그는 그리는 방식이 바뀐다 — 사용자가 전/후를 보고 정하기 전까지 끔
        Assert.False(s.RegenSplitRegions);       // 떨어진 자리를 따로 그리기도 결과가 바뀐다 — 사용자가 전/후를 보고 정하기 전까지 끔(2026-11-16)
    }

    [Fact]
    public void Load_RegenShape_MissingKeysTakeNewDefaults_ExplicitChoiceKept()
    {
        using var t = new TempDir();
        // v1.9.1까지의 settings.json에는 이 키들이 없다 → 새 기본으로 읽힌다
        var old = LoadJson(t, """{"RegenWholeRegion": true}""");
        Assert.True(old.RegenEraseTight);
        Assert.True(old.RegenTagsBySex);
        // 옛 동작으로 돌려 둔 값은 그대로 남는다
        var chosen = LoadJson(t, """{"RegenEraseTight": false, "RegenTagsBySex": false}""");
        Assert.False(chosen.RegenEraseTight);
        Assert.False(chosen.RegenTagsBySex);
        // 2026-11-13~14 빌드가 남긴 옛 칸(RegenBodyTagsByKind — '감지한 쪽만'의 켬/끔)은 읽지 않는다: 그 빌드의 기본(끔 = 늘 같이)이 새 규칙을 끄지 않게
        Assert.True(LoadJson(t, """{"RegenBodyTagsByKind": false}""").RegenTagsBySex);
        Assert.True(LoadJson(t, """{"RegenBodyTagsByKind": true}""").RegenTagsBySex);
    }

    [Fact]
    public void Load_RemovedEngineAndComfyLamaKeys_AreIgnored()
    {
        // 2026-11-12 정리(사용자 선택): '이전 방식' 감지 엔진(DecensorEngine)과 엔진 LaMa 복원(ComfyLama·LamaMask*), 안 쓰던 재생성 값(RegenMegapixels·RegenLama*)을 뺐다
        // — 옛 settings.json의 키는 무시되고, ComfyLama는 기본으로 돌아간다
        using var t = new TempDir();
        var s = LoadJson(t, "{\"DecensorEngine\": \"Legacy\", \"DecensorMode\": \"ComfyLama\", \"LamaMaskGrow\": 13}");
        Assert.Equal("Basic", s.DecensorMode);
        Assert.DoesNotContain(RuntimeTestHelpers.PersistedProperties,
            p => p.Name is "DecensorEngine" or "LamaMaskGrow" or "LamaMaskBlur" or "RegenMegapixels" or "RegenLamaGrow" or "RegenLamaBlur");
    }

    [Fact]
    public void Load_LegacyBodyTags_SplitIntoMaleAndFemale()
    {
        // v1.8.3까지의 한 줄 태그(사용자가 고쳤을 수 있다)를 penis/glans(glains 오타 포함)는 남성, 나머지는 여성으로 나눠 옮기고 옛 칸은 비운다
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTags\": \"penis, glains, clitoris, pussy, vulva, cum in pussy\"}");
        Assert.Equal("penis, glans", s.RegenBodyTagsMale);
        Assert.Equal("clitoris, pussy, vulva", s.RegenBodyTagsFemale);   // 나눈 뒤에 'cum in pussy'를 뗀다(2026-11-16) — 먼저 떼면 나누면서 다시 들어간다(검수)
        Assert.Equal("cum in pussy", s.RegenBodyTagsCum);
        Assert.Equal("", s.RegenBodyTags);
    }

    [Fact]
    public void Load_LegacyBodyTags_WithoutCumInPussy_LeavesTheCumBoxEmpty()
    {
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTags\": \"penis, pussy\"}");
        Assert.Equal(("penis", "pussy", ""), (s.RegenBodyTagsMale, s.RegenBodyTagsFemale, s.RegenBodyTagsCum));
    }

    [Fact]
    public void Load_LegacyBodyTags_OnlyFemaleWords_MaleKeepsDefault()
    {
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTags\": \"cum in pussy, vulva\"}");
        Assert.Equal("penis, glans", s.RegenBodyTagsMale);
        Assert.Equal("vulva", s.RegenBodyTagsFemale);
        Assert.Equal("cum in pussy", s.RegenBodyTagsCum);
    }

    [Fact]
    public void Load_NewTagsPresent_LegacyIgnored()
    {
        // 이미 나뉜 설정에 옛 칸이 비어 있으면 아무것도 안 바꾼다
        using var t = new TempDir();
        var s = LoadJson(t, "{\"RegenBodyTags\": \"\", \"RegenBodyTagsMale\": \"penis\", \"RegenBodyTagsFemale\": \"pussy\"}");
        Assert.Equal("penis", s.RegenBodyTagsMale);
        Assert.Equal("pussy", s.RegenBodyTagsFemale);
    }

    [Fact]
    public void Load_NullStrings_NeverStayNull()
    {
        using var t = new TempDir();
        var strings = RuntimeTestHelpers.PersistedProperties.Where(p => p.PropertyType == typeof(string)).ToList();
        var json = "{" + string.Join(", ", strings.Select(p => $"\"{p.Name}\": null")) + "}";

        var s = LoadJson(t, json);

        // "…": null이 그대로 남으면 .Trim()·.Contains()에서 앱이 죽는다
        Assert.All(strings, p => Assert.NotNull(p.GetValue(s)));
        // 자유 입력 칸은 빈 값 — 출력 폴더가 비면 입력 파일 옆에 저장한다
        Assert.Equal("", s.OutputRoot);
        Assert.Equal("", s.LinkedComfyRoot);
        Assert.Equal("", s.DeepLApiKey);
        // 선택지 칸은 빈 값이 아니라 기본값으로
        var d = new AppSettings();
        Assert.Equal(d.TranslateEngine, s.TranslateEngine);
        Assert.Equal(d.DecensorMode, s.DecensorMode);
        Assert.Equal(d.RegenSampler, s.RegenSampler);
    }

    [Fact]
    public void Load_ObsoleteKeys_IgnoredAndOtherValuesKept()
    {
        using var t = new TempDir();

        // 폐기된 '강력(SDXL)' 워크플로 설정이 남은 옛 settings.json — 모르는 키 때문에 나머지 설정을 잃으면 안 된다
        var s = LoadJson(t, """
            {
              "DecensorStrongSteps": 30,
              "DecensorLoraName": "old_lora.safetensors",
              "DecensorCnStrength": 0.7,
              "DecensorSeed": 42,
              "DecensorCompositeGrow": 16,
              "ColorSteps": 31,
              "TranslateTarget": "en"
            }
            """);

        Assert.Equal(31, s.ColorSteps);
        Assert.Equal("en", s.TranslateTarget);
    }

    // ── 설정을 못 읽을 때: 기본값으로 덮지 않는다 (2026-11-13 점검 11번) ──
    // 전에는 못 읽은 설정을 기본값으로 치고, 앱이 시작하면서 그 기본값을 저장해 API 키·프롬프트·ComfyUI 연결이 백업 없이 사라졌다. 저장도 제자리 쓰기라 쓰다 꺼지면 잘린 파일이 남았다

    private static string[] Leftovers(TempDir t) => [.. Directory.GetFiles(t.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void Load_ReadableFile_KeepsACopyAsBackup_AndReportsNoProblem()
    {
        using var t = new TempDir();
        var path = t.At("settings.json");
        SaveTo(new AppSettings { ColorSteps = 33, DeepLApiKey = "키-1" }, path);

        var s = LoadFrom(path);

        Assert.Equal(33, s.ColorSteps);
        Assert.Null(AppSettings.LoadProblem);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(path + ".bak"));
    }

    [Theory]
    [InlineData("cut")]      // 쓰다 끊긴 파일
    [InlineData("empty")]    // 0바이트
    [InlineData("null")]     // 내용이 null 한 낱말
    public void Load_UnreadableFile_ComesBackFromTheBackup_AndTheBrokenFileIsKept(string how)
    {
        using var t = new TempDir();
        var path = t.At("settings.json");
        SaveTo(new AppSettings { ColorSteps = 33, DeepLApiKey = "키-1", RegenPrompt = "내 프롬프트" }, path);
        LoadFrom(path);   // 읽힌 설정의 사본이 남는다
        var good = File.ReadAllText(path);
        var broken = how == "cut" ? good[..(good.Length / 2)] : how == "empty" ? "" : "null";
        File.WriteAllText(path, broken);

        var s = LoadFrom(path);

        Assert.Equal((33, "키-1", "내 프롬프트"), (s.ColorSteps, s.DeepLApiKey, s.RegenPrompt));
        Assert.NotNull(AppSettings.LoadProblem);
        Assert.Contains("백업", AppSettings.LoadProblem);
        var kept = Assert.Single(Directory.GetFiles(t.Root, "settings.json.broken-*"));
        Assert.Equal(broken, File.ReadAllText(kept));
        Assert.Contains(Path.GetFileName(kept), AppSettings.LoadProblem);   // 어디에 남겼는지 알려 준다
        Assert.Equal(good, File.ReadAllText(path + ".bak"));                // 못 읽은 파일이 백업을 덮지 않았다

        // 앱은 시작하면서 읽은 설정을 다시 저장한다 — 그 뒤에는 멀쩡한 파일이고, 다시 읽으면 문제가 없다
        SaveTo(s, path);
        Assert.Equal(33, LoadFrom(path).ColorSteps);
        Assert.Null(AppSettings.LoadProblem);
    }

    [Fact]
    public void Load_UnreadableFileWithoutBackup_StartsFromDefaults_ButKeepsTheBrokenFile()
    {
        using var t = new TempDir();
        var path = t.At("settings.json");
        File.WriteAllText(path, "{ \"ColorSteps\": 33, \"DeepLApiKey\": \"키-");   // 백업이 생기기 전에 깨진 파일 — 손으로 고치면 되살릴 수 있는 내용이 들어 있다

        var s = LoadFrom(path);

        Assert.Equal(new AppSettings().ColorSteps, s.ColorSteps);
        Assert.NotNull(AppSettings.LoadProblem);
        Assert.Contains("기본값", AppSettings.LoadProblem);
        var kept = Assert.Single(Directory.GetFiles(t.Root, "settings.json.broken-*"));
        Assert.Contains("키-", File.ReadAllText(kept));
        Assert.False(File.Exists(path + ".bak"));   // 깨진 파일을 백업으로 삼지 않는다
    }

    [Fact]
    public void Load_FileHeldByAnotherProgram_IsNotTreatedAsBroken_AndIsNeverOverwrittenInThatRun()
    {
        // 잠깐 못 읽은 것은 깨진 것이 아니다 — '깨짐'으로 다루면 백업(지난 세션을 시작할 때의 값)으로 되살린 뒤 앱이 시작하면서 저장해, 잠금이 풀린 멀쩡한 원본을 옛 값으로 덮는다(2026-11-14 검수).
        // 그 실행에서는 저장하지 않는다
        using var t = new TempDir();
        var path = t.At("settings.json");
        SaveTo(new AppSettings { ColorSteps = 33 }, path);
        LoadFrom(path);                                         // 백업 = ColorSteps 33
        SaveTo(new AppSettings { ColorSteps = 44 }, path);      // 그 뒤에 바꾼 설정(백업에는 없다)
        var current = File.ReadAllBytes(path);

        AppSettings s;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            s = LoadFrom(path);

        Assert.Equal(33, s.ColorSteps);                          // 이번 실행은 백업으로 돈다
        Assert.NotNull(AppSettings.LoadProblem);
        Assert.Contains("쥐고", AppSettings.LoadProblem);
        Assert.Empty(Directory.GetFiles(t.Root, "settings.json.broken-*"));
        SaveTo(s, path);                                         // 앱이 시작하면서 하는 저장
        Assert.Equal(current, File.ReadAllBytes(path));          // 원본은 그대로다

        Assert.Equal(44, LoadFrom(path).ColorSteps);             // 다음 실행(잠금이 풀림)은 원본을 읽고
        Assert.Null(AppSettings.LoadProblem);
        SaveTo(new AppSettings { ColorSteps = 55 }, path);       // 저장도 다시 된다
        Assert.Equal(55, LoadFrom(path).ColorSteps);
    }

    [Fact]
    public void Load_MissingFile_IsNotAProblem()
    {
        using var t = new TempDir();
        LoadFrom(t.At("settings.json"));
        Assert.Null(AppSettings.LoadProblem);
        Assert.Empty(Leftovers(t));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Save_ReplacesTheFileInsteadOfRewritingItInPlace()
    {
        // 제자리 쓰기는 파일을 비운 뒤 채운다 — 그 사이에 앱이 꺼지면 잘린 설정이 남는다. 임시 파일에 다 쓴 뒤 바꿔치기하면 옛 파일은 끝까지 온전하다.
        // 바꿔치기인지는 하드 링크로 본다: 저장 전의 파일에 이름을 하나 더 걸어 두면, 제자리 쓰기는 그 이름으로도 새 내용이 읽히고 바꿔치기는 옛 내용이 그대로 읽힌다
        using var t = new TempDir();
        var path = t.At("settings.json");
        SaveTo(new AppSettings { ColorManualTags = "옛 값" }, path);
        var before = File.ReadAllText(path);
        var oldFile = t.At("저장 전의 파일");
        Assert.True(CreateHardLink(oldFile, path, IntPtr.Zero), "전제: 임시 폴더가 하드 링크를 지원한다(NTFS)");

        SaveTo(new AppSettings { ColorManualTags = "새 값" }, path);

        Assert.Equal(before, File.ReadAllText(oldFile));
        Assert.Equal("새 값", LoadFrom(path).ColorManualTags);
        Assert.DoesNotContain(Leftovers(t), f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_MissingFolders_CreatedBeforeWriting()
    {
        using var t = new TempDir();
        var path = t.At("설정 폴더", "하위 テスト", "settings copy.json");

        SaveTo(new AppSettings { ColorManualTags = "금발, 파란 눈" }, path);

        Assert.True(File.Exists(path));
        Assert.Equal("금발, 파란 눈", LoadFrom(path).ColorManualTags);
    }

    [Fact]
    public void Save_RelativeSettingsPath_WrittenUnderCurrentFolder()
    {
        using var t = new TempDir();
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = t.Root;
        try
        {
            // 폴더 없는 상대 파일 이름 — 경로의 폴더 부분이 빈 문자열이어도 저장이 던지지 않아야 한다
            SaveTo(new AppSettings { ColorSteps = 33 }, "neko settings.json");

            Assert.True(File.Exists(t.At("neko settings.json")));
            Assert.Equal(33, LoadFrom("neko settings.json").ColorSteps);
        }
        finally { Environment.CurrentDirectory = cwd; }
    }

    [Fact]
    public void Save_WritesHandEditableJsonWithoutComputedValues()
    {
        using var t = new TempDir();
        var path = t.At("settings.json");

        SaveTo(new AppSettings { ColorManualTags = "금발, 파란 눈, 制服" }, path);

        var text = File.ReadAllText(path);
        // 사용자가 직접 열어 고치는 파일이라 한글·일본어를 \uXXXX로 바꾸지 않는다
        Assert.Contains("금발, 파란 눈, 制服", text);
        // 저장 키 = 저장 속성 — ComfyRoot·ComfyAvailable 같은 계산값은 적지 않는다
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(RuntimeTestHelpers.PersistedProperties.Select(p => p.Name).Order(StringComparer.Ordinal),
                     doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ShallowClone_CopiesValuesAndStaysIndependent()
    {
        var original = RuntimeTestHelpers.NonDefaultSettings();
        var snapshot = RuntimeTestHelpers.NonDefaultSettings();   // 같은 규칙으로 만든 비교용 사본

        var clone = original.ShallowClone();

        Assert.NotSame(original, clone);
        Assert.Empty(RuntimeTestHelpers.Differences(original, clone));
        // 결과 창이 '이 페이지만' 바꾸는 값들 — 전역 설정으로 새면 안 된다
        clone.RegenPrompt = "이 페이지만 다른 프롬프트";
        clone.RegenSteps += 10;
        clone.RegenDenoise = 0.3;
        clone.EnableDecensor = !clone.EnableDecensor;
        clone.OutputRoot = "다른 출력 폴더";
        Assert.Empty(RuntimeTestHelpers.Differences(snapshot, original));
    }

    [Fact]
    public void Defaults_TranslateBubbleOnly_On()
    {
        // 2026-11-18 사용자: 번역은 말풍선 안의 글만 — 그림 위의 효과음·손글씨는 그대로 둔다
        Assert.True(new AppSettings().TranslateBubbleOnly);
    }

    // ── 식자 글자 색 ──

    [Fact]
    public void Defaults_LetterColor_PureBlack()
    {
        // 2026-11-18 사용자: 기본은 완전 검정 — 원문 색 자동(획 픽셀 평균)은 짙은 회색이 나와 기본에서 뺐다
        Assert.Equal("#000000", new AppSettings().LetterColorHex);
    }

    [Theory]
    [InlineData("\"\"", "#000000")]          // 옛 기본값(비움 = 원문 색 자동)은 검정으로
    [InlineData("\"   \"", "#000000")]
    [InlineData("null", "#000000")]
    [InlineData("\"auto\"", "auto")]         // 원문 색을 따라가려면 auto라고 적는다
    [InlineData("\" Auto \"", " Auto ")]     // 대소문자·공백은 IsAutoColor가 받으므로 그대로 둔다
    [InlineData("\"#FF0000\"", "#FF0000")]   // 고른 색은 그대로
    [InlineData("\"#ff00aa\"", "#ff00aa")]
    [InlineData("\"red\"", "#000000")]       // 16진수도 auto도 아닌 값은 다른 설정처럼 유효값으로 — 렌더러는 어차피 검정으로 떨어졌다(검수 2026-11-19)
    [InlineData("\"#12345\"", "#000000")]
    [InlineData("\"#FF000000\"", "#000000")]   // 알파는 받지 않는다
    public void Load_LetterColor_EmptyBecomesBlack_AutoAndHexKept(string json, string expected)
    {
        using var t = new TempDir();

        var s = LoadJson(t, "{\"LetterColorHex\": " + json + "}");

        Assert.Equal(expected, s.LetterColorHex);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData(" auto ", true)]
    [InlineData("", false)]
    [InlineData("#000000", false)]
    public void IsAutoColor_AutoKeywordOnly(string value, bool expected) =>
        Assert.Equal(expected, AppSettings.IsAutoColor(value));

    // ── 화면 언어 ──

    [Fact]
    public void Defaults_UiLanguage_NotChosenYet()
    {
        // "" = 아직 안 고름 — 앱이 Windows의 화면 언어를 따른다(Loc.Resolve)
        Assert.Equal("", new AppSettings().UiLanguage);
    }

    [Theory]
    [InlineData("\"ko\"", "ko")]
    [InlineData("\"en\"", "en")]
    [InlineData("\"ja\"", "ja")]
    [InlineData("\"fr\"", "")]
    [InlineData("\"English\"", "")]
    [InlineData("null", "")]
    public void Load_UiLanguage_KeepsTheThreeCodes_ClearsAnythingElse(string json, string expected)
    {
        using var t = new TempDir();

        var s = LoadJson(t, "{\"UiLanguage\": " + json + "}");

        Assert.Equal(expected, s.UiLanguage);
    }
}
