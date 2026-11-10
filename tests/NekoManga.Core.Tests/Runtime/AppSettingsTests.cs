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
    [InlineData(nameof(AppSettings.DecensorEngine), "\"Unknown\"")]
    [InlineData(nameof(AppSettings.DecensorLegacyAssist), "\"Black\"")]
    [InlineData(nameof(AppSettings.DecensorAssistRepair), "\"PConv\"")]
    [InlineData(nameof(AppSettings.OnnxGpu), "\"Maybe\"")]
    [InlineData(nameof(AppSettings.GpuVendor), "\"intel\"")]
    [InlineData(nameof(AppSettings.DecensorReviewMode), "\"Batch\"")]
    [InlineData(nameof(AppSettings.LetterAlign), "\"Justify\"")]
    [InlineData(nameof(AppSettings.RegenSampler), "\"   \"")]             // 빈 샘플러 이름은 워크플로를 터뜨린다
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
    [InlineData(nameof(AppSettings.DecensorMode), "ComfyLama")]
    [InlineData(nameof(AppSettings.DecensorMode), "ComfyRegen")]
    [InlineData(nameof(AppSettings.DecensorMode), "ComfyCustom")]
    [InlineData(nameof(AppSettings.DecensorEngine), "Legacy")]
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
    [InlineData(nameof(AppSettings.LamaMaskGrow), "-1", 0.0)]
    [InlineData(nameof(AppSettings.LamaMaskBlur), "65", 64.0)]
    [InlineData(nameof(AppSettings.RegenSteps), "0", 1.0)]
    [InlineData(nameof(AppSettings.RegenSteps), "1000", 150.0)]
    [InlineData(nameof(AppSettings.RegenCfg), "-2", 0.0)]
    [InlineData(nameof(AppSettings.RegenCfg), "99", 30.0)]
    [InlineData(nameof(AppSettings.RegenDenoise), "0", 0.05)]
    [InlineData(nameof(AppSettings.RegenDenoise), "1.5", 1.0)]
    [InlineData(nameof(AppSettings.RegenCnStrength), "9", 2.0)]
    [InlineData(nameof(AppSettings.RegenCnEnd), "1.2", 1.0)]
    [InlineData(nameof(AppSettings.RegenPreprocResolution), "100", 256.0)]
    [InlineData(nameof(AppSettings.RegenPreprocResolution), "99999", 4096.0)]
    [InlineData(nameof(AppSettings.RegenLamaGrow), "65", 64.0)]
    [InlineData(nameof(AppSettings.RegenEdgeGrow), "500", 128.0)]
    [InlineData(nameof(AppSettings.RegenCoreBlur), "-1", 0.0)]
    [InlineData(nameof(AppSettings.RegenMegapixels), "-1", 0.0)]          // 0 = 원본 크기
    [InlineData(nameof(AppSettings.RegenMegapixels), "20", 8.0)]
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
    public void Load_OutOfRangeNumber_ClampedToNearestBound(string property, string json, double expected)
    {
        using var t = new TempDir();

        // 손으로 고치다 깨진 값이 워크플로·학습 인자를 터뜨리지 않게 허용 범위의 가까운 끝으로 맞춘다
        var s = LoadJson(t, $"{{\"{property}\": {json}}}");

        Assert.Equal(expected, Convert.ToDouble(Value(s, property)));
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
}
