using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NekoManga.Core.Tests;

/// <summary>번역표(Core의 Lang/en.json·ja.json)가 화면 글을 다 덮는지 — **소스를 훑어** 본다(LocSourceScan).
/// 화면 글을 더하거나 고치면 여기서 걸린다: 한국어 글을 <c>Loc.T</c>/<c>Loc.F</c>로 감싸고(화면 글이 아니면 그 줄에 <c>// loc:none</c>) 두 표에 번역을 넣는다.
/// XAML의 글은 감쌀 것이 없다 — 속성에 적힌 한국어 글이 그대로 열쇠다(App의 UiText가 창을 훑어 바꾼다).</summary>
public class LocCoverageTests
{
    private static readonly string Root = LocSourceScan.FindRoot();
    private static readonly Lazy<(List<LocSourceScan.Use> Keys, List<string> Unwrapped, List<string> XamlUntranslatable)> Scan = new(() =>
    {
        var keys = new List<LocSourceScan.Use>();
        var unwrapped = new List<string>();
        var xamlBad = new List<string>();
        foreach (var file in LocSourceScan.SourceFiles(Root))
        {
            if (LocSourceScan.DevOnlyFiles.Contains(Path.GetFileName(file))) continue;
            var (k, u) = LocSourceScan.ScanCSharp(file, Root);
            keys.AddRange(k);
            unwrapped.AddRange(u);
        }
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(Root, "src", "NekoManga.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (xaml.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || xaml.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var (k, bad) = LocSourceScan.ScanXaml(xaml, Root);
            keys.AddRange(k);
            xamlBad.AddRange(bad);
        }
        return (keys, unwrapped, xamlBad);
    });

    private static string Show(IEnumerable<string> lines, int max = 25)
    {
        var list = lines.ToList();
        return $"{list.Count}건\n" + string.Join("\n", list.Take(max)) + (list.Count > max ? $"\n… 외 {list.Count - max}건" : "");
    }

    private static string Short(string s) => (s.Length > 90 ? s[..90] + "…" : s).Replace("\n", "⏎");

    [Fact]
    public void SourceScan_FindsTheScreenText()
    {
        // 훑기가 조용히 아무것도 못 찾으면 아래 시험이 전부 빈 채로 통과한다 — 규모를 먼저 본다(2026-11-16: 코드 630개·XAML 410개쯤)
        var keys = Scan.Value.Keys;
        Assert.True(keys.Count(k => k.Kind == "xaml") > 300, "XAML의 글을 거의 못 찾았어요");
        Assert.True(keys.Count(k => k.Kind is "T" or "Key") > 300, "Loc.T의 글을 거의 못 찾았어요");
        Assert.True(keys.Count(k => k.Kind == "F") > 100, "Loc.F의 글을 거의 못 찾았어요");
    }

    [Fact]
    public void EveryKoreanLiteral_IsWrappedInLoc_OrMarked()
    {
        // 감싸지 않은 한국어 글은 다른 언어에서도 한국어로 나온다. 화면에 안 나오는 글(로그·진단·견주는 낱말)이면 그 줄에 // loc:none
        var left = Scan.Value.Unwrapped;
        Assert.True(left.Count == 0, "Loc.T(\"…\")/Loc.F($\"…\")로 감싸지 않았고 // loc:none 표시도 없는 한글 리터럴 " + Show(left));
    }

    [Fact]
    public void XamlText_IsOnlyWhereUiTextTranslatesIt()
    {
        // XAML의 글은 UiText가 창을 훑어 바꾼다 — 보는 속성(Text·Content·ToolTip·Header·Title·PlaceholderText)에 적힌 글만. 다른 자리의 글은 번역표에 넣어도 한국어로 나온다
        var bad = Scan.Value.XamlUntranslatable;
        Assert.True(bad.Count == 0, "UiText가 번역하지 못하는 자리의 한국어 글 " + Show(bad));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void FormattedTranslations_CanActuallyBeFormatted(string lang)
    {
        // 자리 번호만 맞아서는 모자라다 — 번역에 짝 없는 중괄호가 섞이면 string.Format이 던지고 Loc.F는 조용히 한국어로 물러난다. Loc.F에 쓰는 열쇠를 전부 실제로 끼워 본다
        var table = Loc.Table(lang)!;
        var bad = new List<string>();
        foreach (var key in Scan.Value.Keys.Where(k => k.Kind == "F").Select(k => k.Key).Distinct())
        {
            if (!table.TryGetValue(key, out var pattern)) continue;   // 빠진 번역은 EveryKey_HasATranslation이 말한다
            var holes = Hole.Matches(key.Replace("{{", "").Replace("}}", ""));
            var args = Enumerable.Repeat((object)"x", holes.Count == 0 ? 0 : holes.Max(m => int.Parse(m.Groups[1].Value)) + 1).ToArray();
            try { _ = string.Format(pattern, args); }
            catch (FormatException ex) { bad.Add($"{Short(key)} → {Short(pattern)} ({ex.Message})"); }
        }
        Assert.True(bad.Count == 0, $"{lang}.json의 번역을 끼울 수 없어요 " + Show(bad));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryKey_HasATranslation(string lang)
    {
        var table = Loc.Table(lang)!;
        var missing = Scan.Value.Keys
            .Where(k => !table.TryGetValue(k.Key, out var t) || t.Length == 0)
            .GroupBy(k => k.Key)
            .Select(g => $"{g.First().Where}: {Short(g.Key)}");
        Assert.True(!missing.Any(), $"src/NekoManga.Core/Lang/{lang}.json에 번역이 없는 화면 글 " + Show(missing));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Tables_HaveNoUnusedKeys(string lang)
    {
        // 소스의 글을 고치면 옛 열쇠가 표에 남는다 — 남은 번역은 아무도 보지 않으니 지운다(고친 글에는 새 번역이 필요하다는 신호이기도 하다)
        var used = Scan.Value.Keys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var unused = Loc.Table(lang)!.Keys.Where(k => !used.Contains(k)).Select(Short);
        Assert.True(!unused.Any(), $"{lang}.json에 있지만 소스에 없는 열쇠 " + Show(unused));
    }

    private static readonly Regex Hole = new(@"\{(\d+)([^{}]*)\}");

    private static List<string> Holes(string s) =>
        Hole.Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Translations_KeepThePlaceholders_AndTheEdges(string lang)
    {
        var bad = new List<string>();
        foreach (var (key, text) in Loc.Table(lang)!)
        {
            // 서식 자리({0}, {1:N0} …)는 원문과 같은 것들이어야 한다(순서는 달라도 된다) — 없는 자리를 쓰면 Loc.F가 원문으로 물러나 한국어가 나온다
            if (!Holes(key).SequenceEqual(Holes(text))) bad.Add($"자리가 다름: {Short(key)} → {Short(text)}");
            // 이어 붙여 쓰는 조각("저장 실패다냥: " + 까닭)은 앞뒤 공백·줄 바꿈이 원문과 같아야 붙는 모양이 같다
            if (Edge(key) != Edge(text)) bad.Add($"앞뒤 공백이 다름: '{Short(key)}' → '{Short(text)}'");
            if (LocSourceScan.HasHangul(text)) bad.Add($"번역에 한글이 남음: {Short(text)}");
        }
        Assert.True(bad.Count == 0, $"{lang}.json " + Show(bad));

        static (string, string) Edge(string s) => (s[..(s.Length - s.TrimStart(' ', '\n').Length)], s[s.TrimEnd(' ', '\n').Length..]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Tables_HaveNoDuplicateKeys(string lang)
    {
        // 같은 열쇠가 두 번 있으면 뒤의 것이 조용히 이긴다 — 파일을 직접 읽어 본다
        var bytes = File.ReadAllBytes(Path.Combine(Root, "src", "NekoManga.Core", "Lang", lang + ".json"));
        var reader = new Utf8JsonReader(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF ? 3 : 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new List<string>();
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && !seen.Add(reader.GetString()!)) twice.Add(Short(reader.GetString()!));
        Assert.True(twice.Count == 0, $"{lang}.json에 두 번 든 열쇠 " + Show(twice));
        Assert.Equal(seen.Count, Loc.Table(lang)!.Count);   // 빌드에 든 표가 이 파일이다
    }

    // ── 열쇠를 컴파일러와 똑같이 뽑는가 ──
    // 아래 Sample(…)·Plain(…)에 적은 리터럴을 이 파일에서 다시 읽어, 뽑은 열쇠가 컴파일러가 실제로 만든 값과 같은지 본다.
    // 보간 문자열의 서식을 한 글자라도 다르게 뽑으면 표에서 못 찾아 그 글만 조용히 한국어로 나온다.

    private static readonly List<string> Compiled = [];
    private static void Sample(FormattableString text) => Compiled.Add(text.Format);
    private static void Plain(string text) => Compiled.Add(text);

    private static void Samples()
    {
        int n = 3; double sec = 1.25; bool on = true; var when = new DateTime(2026, 11, 16, 9, 30, 0);
        var map = new Dictionary<string, int> { ["키"] = 7 };
        string name = "원고.png";
        Sample($"고친 {n}장 다시 렌더링");
        Sample($"{sec:0.0}초 · {n,5}쪽 · {sec,-8:N2}");
        Sample($"중괄호 {{그대로}} {n} 그리고 {{{n}}}");
        Sample($"줄\n바꿈 \"따옴표\" 역슬래시 \\ 탭\t {n}");
        Sample($"{(on ? "켬" : "끔")} 상태 — {(n > 2 ? $"안쪽 {n}" : "")}");
        Sample($"사전 {map["키"]} · 메서드 {name.Replace(",", ", ")} · 범위 {name[..2]}");
        Sample($"날짜 {when:MM-dd HH:mm} · 이름 붙인 인자 {Math.Round(sec, digits: 1)} · 배열 {new[] { "가", "나" }.Length}");
        Sample($@"축자 ""따옴표"" C:\폴더\{name} {{중괄호}}");
        Sample($"구멍 없는 글");
        Sample($"서식 지정 속의 이스케이프 {when:hh\\:mm} 시각");
        Sample($"{n}");
        Sample($"null 처리 {name?.Length ?? 0} · 형 변환 {(int)sec} · 람다 {new[] { 1, 2 }.Count(x => x > 1)}");
        Plain("보통 글 \"따옴표\" 줄\n바꿈 \\ 역슬래시 \u00e9 유니코드");
        Plain(@"축자 ""따옴표"" C:\폴더\파일");
        Plain("중괄호 {0} 그대로");
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [Fact]
    public void FormatKey_MatchesTheCompiler()
    {
        Compiled.Clear();
        Samples();

        var src = File.ReadAllText(ThisFile());
        var extracted = new List<string>();
        foreach (var lit in LocSourceScan.Literals(src))
        {
            string head = src[Math.Max(0, lit.Start - 12)..lit.Start];
            if (head.EndsWith(" Sample(")) extracted.Add(LocSourceScan.FormatOf(lit));
            else if (head.EndsWith(" Plain(")) extracted.Add(LocSourceScan.ValueOf(lit));
        }

        Assert.Equal(Compiled.Count, extracted.Count);
        for (int i = 0; i < Compiled.Count; i++) Assert.Equal(Compiled[i], extracted[i]);
    }

    // ── 훑는 도구가 잡아야 할 것을 잡는가(임시 파일로) ──

    [Fact]
    public void ScanCSharp_ReportsUnwrappedText_AndHonoursTheMarkers()
    {
        using var t = new TempDir();
        var file = t.At("Sample.cs");
        File.WriteAllText(file, string.Join("\n",
            "class Sample {",
            "    string a = Loc.T(\"감싼 글\");",
            "    string b = \"감싸지 않은 글\";",
            "    string c = \"기록에만 남는 글\";   // loc:none",
            "    // loc:none",
            "    string d = \"윗줄 주석으로 표시한 글\";",
            "    const string E = \"쓰는 자리에서 번역하는 글\";   // loc:key",
            "    string f = Loc.T(\"이어 붙인 글: \" + a);",
            "    string g = Loc.F($\"값이 낀 글 {a}\");",
            "    string h = \"English only\";",
            "}"));

        var (keys, unwrapped) = LocSourceScan.ScanCSharp(file, t.Root);

        Assert.Equal(new[] { "감싼 글", "쓰는 자리에서 번역하는 글", "이어 붙인 글: ", "값이 낀 글 {0}" }, keys.Select(k => k.Key));
        Assert.Equal(2, unwrapped.Count);
        Assert.Contains("감싸지 않은 글", unwrapped[0]);
        Assert.Contains("리터럴 하나만", unwrapped[1]);   // Loc.T("…" + a) — 실행 때의 글은 표에 없다
    }

    [Fact]
    public void ScanXaml_FindsTextAttributes_AndFlagsTextUiTextCannotSee()
    {
        using var t = new TempDir();
        var file = t.At("Sample.xaml");
        File.WriteAllText(file, string.Join("\n",
            "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Title=\"창 제목\">",
            "  <StackPanel>",
            "    <TextBlock Text=\"여러 줄에",
            "               걸친 글\" ToolTip=\"풍선 도움말\" Tag=\"꼬리표\" x:Name=\"이름\"/>",
            "    <Button Content=\"{Binding 묶인값}\"/>",
            "    <TextBox Text=\"사용자가 적는 칸\"/>",
            "    <TextBlock>요소 사이의 글</TextBlock>",
            "    <ProgressBar AutomationProperties.HelpText=\"보지 않는 속성\"/>",
            "  </StackPanel>",
            "</Window>"));

        var (keys, untranslatable) = LocSourceScan.ScanXaml(file, t.Root);

        // 속성 값의 줄 바꿈은 XML 규칙대로 빈칸이 된다(WPF가 읽는 값과 같다). Tag·이름·바인딩·TextBox의 글은 열쇠가 아니다
        Assert.Contains("창 제목", keys.Select(k => k.Key));
        Assert.Contains(keys, k => k.Key.StartsWith("여러 줄에 ") && k.Key.EndsWith("걸친 글") && !k.Key.Contains('\n'));
        Assert.Contains("풍선 도움말", keys.Select(k => k.Key));
        Assert.DoesNotContain(keys, k => k.Key is "꼬리표" or "이름" or "사용자가 적는 칸" || k.Key.Contains("묶인값"));
        Assert.Equal(2, untranslatable.Count);
        Assert.Contains(untranslatable, u => u.Contains("요소 사이의 글"));
        Assert.Contains(untranslatable, u => u.Contains("보지 않는 속성"));
    }

    [Fact]
    public void FormatOf_AlignmentThatIsNotANumber_IsRefused()
    {
        // 상수 이름으로 적은 정렬은 컴파일러가 그 값을 넣는다 — 글자 그대로 옮기면 틀린 열쇠가 되므로 멈추고 알린다
        var lit = LocSourceScan.Literals("var s = $\"값 {n,Width} 끝\";").Single();

        Assert.Throws<InvalidDataException>(() => LocSourceScan.FormatOf(lit));
    }

    [Fact]
    public void Literals_SkipCommentsAndChars_AndFindNestedOnes()
    {
        const string src = """"
            // "주석 속 글"
            /* "여러 줄 주석" */
            var a = 'x'; var q = '"'; var b = "바깥 글";
            var c = $"보간 {(on ? "안쪽 글" : $"더 안쪽 {n}")} 끝";
            var d = @"축자 ""글""";
            """";

        var texts = LocSourceScan.Literals(src).Select(l => l.Text).ToList();

        Assert.Equal(new[] { "\"바깥 글\"", "$\"보간 {(on ? \"안쪽 글\" : $\"더 안쪽 {n}\")} 끝\"", "\"안쪽 글\"", "$\"더 안쪽 {n}\"", "@\"축자 \"\"글\"\"\"" }, texts);
    }
}
