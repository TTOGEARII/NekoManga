using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NekoManga.Core;

/// <summary>화면 글의 언어(한국어·영어·일본어, 2026-11-16 사용자 요청 — 이슈 #2). **소스에 적힌 한국어 글이 곧 열쇠다**:
/// 번역표(<c>Lang/en.json</c>·<c>ja.json</c>, 이 어셈블리에 내장 — <c>{"한국어 원문": "번역"}</c>)에 있으면 그 번역을, 없으면 한국어를 그대로 낸다.
/// <para>쓰는 법: 고정된 글은 <c>Loc.T("대기")</c>, 값이 끼는 글은 <c>Loc.F($"고친 {n}장")</c> — 열쇠는 컴파일러가 만드는 서식 <c>"고친 {0}장"</c>이고
/// 번역은 자리의 순서를 바꿔도 된다(<c>"{0} edited pages"</c>). XAML에 적힌 글은 App의 UiText가 창을 훑어 바꾼다.</para>
/// <para>표·const처럼 쓰는 자리에서 번역하는 글은 소스에 <c>// loc:key</c>, 화면에 나오지 않는 글(로그·개발용)은 <c>// loc:none</c>을 적는다 —
/// 감싸지 않은 한글 리터럴·번역이 빠진 열쇠는 LocCoverageTests가 잡는다.</para>
/// <para>글을 **견주는 데 쓰지 않는다**(<c>Stage.StartsWith("실패")</c> 같은 것) — 번역되면 어긋난다. 상태는 플래그로 든다.</para></summary>
public static class Loc
{
    /// <summary>고를 수 있는 언어(설정 칸의 순서). "ko"가 소스의 언어다.</summary>
    public static readonly IReadOnlyList<string> Languages = ["ko", "en", "ja"];

    private static volatile string _lang = "ko";
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>?> Tables = new();

    /// <summary>지금 화면 언어("ko"|"en"|"ja"). 앱이 시작하면서 설정으로 정한다(<see cref="Resolve"/> → <see cref="SetLanguage"/>). 정하지 않으면 한국어 — 시험·진단 진입점은 한국어로 돈다.</summary>
    public static string Lang => _lang;

    /// <summary>언어가 바뀌었다 — 열린 창이 글을 다시 번역한다(UiText). 부른 스레드에서 알린다.</summary>
    public static event Action? Changed;

    /// <summary>화면 언어를 바꾼다. 모르는 값은 한국어. 같은 언어면 아무것도 하지 않는다.</summary>
    public static void SetLanguage(string? code)
    {
        var lang = Known(code) ?? "ko";
        if (lang == _lang) return;
        _lang = lang;
        // 듣는 쪽 하나가 던져도 나머지는 알림을 받게 한다(창 하나만 새 언어이고 나머지는 옛 언어로 남지 않게) — 던진 것은 다 알린 뒤에 다시 던진다
        Exception? first = null;
        foreach (var listener in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)listener)(); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    /// <summary>설정 값(<c>AppSettings.UiLanguage</c>)으로 쓸 언어를 정한다. 고른 적이 없으면("" 또는 모르는 값) Windows의 화면 언어를 따른다 —
    /// 한국어·일본어가 아니면 영어(한국어를 못 읽는 사람이 첫 화면에서 막히지 않게).</summary>
    public static string Resolve(string? setting, CultureInfo windows) =>
        Known(setting) ?? windows.TwoLetterISOLanguageName switch { "ko" => "ko", "ja" => "ja", _ => "en" };

    private static string? Known(string? code)
    {
        var c = code?.Trim().ToLowerInvariant();
        return c is "ko" or "en" or "ja" ? c : null;
    }

    /// <summary>언어의 이름 — 그 언어로 적는다(고르는 칸에서 지금 언어를 못 읽어도 제 언어는 찾게).</summary>
    public static string NativeName(string lang) => lang switch { "en" => "English", "ja" => "日本語", _ => "한국어" };   // loc:none — 언어 이름은 번역하지 않는다

    /// <summary>고정된 글을 지금 언어로.</summary>
    public static string T(string ko) => Translate(Table(_lang), ko);

    /// <summary>값이 끼는 글을 지금 언어로 — <c>Loc.F($"고친 {n}장")</c>. 인자가 FormattableString이라 컴파일러가 서식과 값을 따로 넘긴다.</summary>
    public static string F(FormattableString text) => Format(Table(_lang), text);

    /// <summary>나중에 <see cref="T"/>로 번역할 글이라는 표시 — 글은 그대로 돌려준다. 표·정적 목록처럼 만들 때가 아니라 **보여 줄 때** 번역해야 하는 자리에 쓴다
    /// (정적 초기화는 언어가 정해지기 전에 돌 수 있고, 언어를 바꿔도 다시 돌지 않는다). 보여 주는 쪽이 <c>Loc.T(값)</c>으로 번역한다.</summary>
    public static string Key(string ko) => ko;

    /// <summary>표에서 번역을 찾는다. 표가 없거나(한국어) 열쇠가 없거나 번역이 비어 있으면 원문. null은 어느 언어에서든 null 그대로(한국어에서만 되고 다른 언어에서는 던지는 일이 없게).</summary>
    public static string Translate(IReadOnlyDictionary<string, string>? table, string ko) =>
        table != null && ko != null && table.TryGetValue(ko, out var t) && t.Length > 0 ? t : ko!;

    /// <summary>서식을 번역해 값을 끼운다. 번역이 없으면 원문 그대로. 번역의 자리가 원문과 안 맞아 끼우지 못하면(없는 {n}) 원문으로 물러난다 —
    /// 화면 글 하나 때문에 작업이 죽으면 안 된다(그런 번역은 LocCoverageTests가 미리 잡는다).</summary>
    public static string Format(IReadOnlyDictionary<string, string>? table, FormattableString text)
    {
        if (table == null || !table.TryGetValue(text.Format, out var pattern) || pattern.Length == 0) return text.ToString();
        try { return string.Format(CultureInfo.CurrentCulture, pattern, text.GetArguments()); }
        catch (FormatException) { return text.ToString(); }
    }

    /// <summary>그 언어의 번역표 — 한국어(소스의 언어)와 모르는 언어는 null. 처음 한 번만 읽는다.</summary>
    public static IReadOnlyDictionary<string, string>? Table(string lang) => lang == "ko" ? null : Tables.GetOrAdd(lang, Load);

    private static IReadOnlyDictionary<string, string>? Load(string lang)
    {
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"NekoManga.Core.Lang.{lang}.json");
            return s == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s);
        }
        catch { return null; }   // 깨진 표로 앱이 죽지 않게 — 그 언어는 한국어로 보인다(표가 읽히는지는 시험이 본다)
    }
}
