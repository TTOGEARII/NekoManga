using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace NekoManga.Core.Tests;

/// <summary>설정·설치 영역 테스트가 함께 쓰는 도우미.</summary>
internal static class RuntimeTestHelpers
{
    /// <summary>빈 파일을 만든다 (없는 상위 폴더까지). 설치 여부는 파일이 있는지만 보므로 내용은 필요 없다.</summary>
    public static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>settings.json에 저장되는 속성 — public으로 읽고 쓰는 인스턴스 속성 전부. ComfyRoot 같은 계산 속성([JsonIgnore])은 빠진다.</summary>
    public static IReadOnlyList<PropertyInfo> PersistedProperties { get; } = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
                    && p.GetIndexParameters().Length == 0
                    && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
        .ToArray();

    /// <summary>값이 서로 다른 저장 속성을 "이름: a ≠ b" 꼴로 모은다 — 전부 같으면 빈 목록. except에 적은 이름은 건너뛴다.</summary>
    public static List<string> Differences(AppSettings a, AppSettings b, params string[] except) =>
        PersistedProperties
            .Where(p => !except.Contains(p.Name) && !Equals(p.GetValue(a), p.GetValue(b)))
            .Select(p => $"{p.Name}: {p.GetValue(a) ?? "(null)"} ≠ {p.GetValue(b) ?? "(null)"}")
            .ToList();

    /// <summary>선택지가 정해진 속성에 넣을, 기본값이 아닌 유효한 값.
    /// Load의 이관 규칙이 허용 목록 밖의 값을 기본값으로 되돌리므로, 아무 문자열이나 넣으면 저장→읽기에서 살아남지 못한다.</summary>
    private static readonly Dictionary<string, string> ValidAlternates = new()
    {
        [nameof(AppSettings.TranslateEngine)] = "DeepSeek",
        [nameof(AppSettings.DecensorMode)] = "ComfyRegen",
        [nameof(AppSettings.DecensorEngine)] = "Legacy",
        [nameof(AppSettings.DecensorLegacyAssist)] = "WhiteMosaic",
        [nameof(AppSettings.DecensorAssistRepair)] = "Lama",
        [nameof(AppSettings.OnnxGpu)] = "Off",
        [nameof(AppSettings.GpuVendor)] = "nvidia-old",
        [nameof(AppSettings.DecensorReviewMode)] = "Window",
        [nameof(AppSettings.LetterAlign)] = "Right",
    };

    /// <summary>모든 저장 속성이 기본값과 다른 설정. 값은 Load의 이관 규칙(허용 목록·범위)을 통과하는 것만 쓴다 —
    /// 선택지는 <see cref="ValidAlternates"/>에서 고르고, 숫자는 범위 안에서 조금만 움직인다.
    /// 새 속성에 선택지나 범위 제한을 걸었다면 여기에도 유효한 값을 만드는 규칙을 더한다.</summary>
    public static AppSettings NonDefaultSettings()
    {
        var s = new AppSettings();
        foreach (var p in PersistedProperties)
        {
            object next = p.GetValue(s) switch
            {
                bool b => !b,
                int i => i + 1,
                long l => l + 1,
                double d => d * 0.75 + 0.001,
                string => ValidAlternates.TryGetValue(p.Name, out var alt) ? alt : $"{p.Name} 값 テスト",
                _ => throw new NotSupportedException($"{p.Name}: {p.PropertyType.Name} 형식의 설정에 넣을 값을 만드는 규칙이 없어요."),
            };
            p.SetValue(s, next);
        }
        return s;
    }

    /// <summary>소수점·천 단위 표기가 실행 PC의 지역 설정을 타지 않게 고정하고 실행한다.</summary>
    public static T InInvariantCulture<T>(Func<T> body)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return body(); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    /// <summary>환경변수를 잠시 바꿔(null이면 지운다) body를 돌리고 원래 값으로 되돌린다.
    /// 환경변수는 프로세스 전체가 같이 쓰므로 이것을 부르는 테스트는 [Collection("Env")]에 둔다.</summary>
    public static T WithEnv<T>(string name, string? value, Func<T> body)
    {
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(name, before); }
    }

    public static void WithEnv(string name, string? value, Action body) =>
        WithEnv(name, value, () => { body(); return 0; });
}
