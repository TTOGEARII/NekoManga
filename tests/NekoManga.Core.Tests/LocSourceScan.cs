using System.Text;
using System.Xml.Linq;

namespace NekoManga.Core.Tests;

/// <summary>화면 글의 열쇠를 **소스에서** 뽑는 도구(LocCoverageTests용) — C# 파일의 문자열 리터럴과 XAML의 글 속성을 훑는다.
/// 번역표는 소스의 한국어 글을 열쇠로 쓰므로(Loc), 소스에 있는 글과 표에 있는 열쇠가 글자 하나까지 같아야 한다:
/// 보통 문자열은 이스케이프를 푼 값, 보간 문자열은 컴파일러가 FormattableString에 넣는 서식("고친 {0}장").
/// 서식을 컴파일러와 똑같이 뽑는지는 LocCoverageTests.FormatKey_MatchesTheCompiler가 실제 컴파일 결과와 견줘 지킨다.</summary>
internal static class LocSourceScan
{
    /// <summary>소스 안의 문자열 리터럴 하나. Start = 접두사($·@)의 자리, End = 닫는 따옴표 다음.</summary>
    public sealed record Literal(int Start, int End, string Text, bool Interpolated, bool Verbatim, bool Raw);

    public static bool HasHangul(string s)
    {
        foreach (char c in s)
            if (c is (>= '가' and <= '힣') or (>= 'ㄱ' and <= 'ㅣ')) return true;
        return false;
    }

    // ── C# 리터럴 찾기 ──

    /// <summary>소스의 문자열 리터럴 전부 — 주석·문자 리터럴은 건너뛰고, 보간 문자열의 구멍 안에 든 리터럴도 따로 넣는다.</summary>
    public static List<Literal> Literals(string src)
    {
        var found = new List<Literal>();
        Code(src, 0, found, stopAtBrace: false);
        return found;
    }

    /// <summary>코드 구간을 훑는다. stopAtBrace면 짝이 맞는 '}'의 자리에서 멈춘다(보간 구멍).</summary>
    private static int Code(string src, int i, List<Literal> found, bool stopAtBrace)
    {
        int depth = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/') { int j = src.IndexOf('\n', i); i = j < 0 ? n : j; continue; }
            if (c == '/' && i + 1 < n && src[i + 1] == '*') { int j = src.IndexOf("*/", i + 2, StringComparison.Ordinal); i = j < 0 ? n : j + 2; continue; }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < n && src[j] != '\'' && src[j] != '\n') { if (src[j] == '\\') j++; j++; }
                i = j + 1;
                continue;
            }
            if (c is '"' or '$' or '@')
            {
                int q = i;
                while (q < n && src[q] is '$' or '@') q++;
                bool partOfName = c != '"' && i > 0 && (char.IsLetterOrDigit(src[i - 1]) || src[i - 1] == '_');
                if (q < n && src[q] == '"' && !partOfName) { i = String(src, i, q, found); continue; }
                if (c != '"') { i++; continue; }
            }
            if (stopAtBrace)
            {
                if (c is '{' or '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}') { if (depth == 0) return i; depth--; }
            }
            i++;
        }
        return n;
    }

    /// <summary>start = 접두사의 자리, q = 여는 따옴표의 자리. 리터럴을 넣고 그 끝 다음 자리를 돌려준다.</summary>
    private static int String(string src, int start, int q, List<Literal> found)
    {
        int n = src.Length;
        string prefix = src[start..q];
        bool interp = prefix.Contains('$'), verbatim = prefix.Contains('@');
        int dollars = prefix.Count(ch => ch == '$');
        int quotes = 0;
        while (q + quotes < n && src[q + quotes] == '"') quotes++;
        int at = found.Count;   // 구멍 안의 리터럴보다 앞에 넣는다(바깥 것이 먼저)
        int j;
        if (quotes >= 3)
        {
            string close = new('"', quotes);
            j = q + quotes;
            while (j < n)
            {
                if (string.CompareOrdinal(src, j, close, 0, quotes) == 0) { j += quotes; break; }
                if (interp && src[j] == '{')
                {
                    int run = 0;
                    while (j + run < n && src[j + run] == '{') run++;
                    if (run >= dollars && run < dollars * 2)
                    {
                        j = Code(src, j + run, found, stopAtBrace: true);
                        while (j < n && src[j] == '}') j++;
                        continue;
                    }
                    j += run;
                    continue;
                }
                j++;
            }
            found.Insert(at, new Literal(start, j, src[start..j], interp, false, true));
            return j;
        }
        j = q + 1;
        while (j < n)
        {
            char ch = src[j];
            if (verbatim)
            {
                if (ch == '"') { if (j + 1 < n && src[j + 1] == '"') { j += 2; continue; } break; }
            }
            else
            {
                if (ch == '\\') { j += 2; continue; }
                if (ch is '"' or '\n') break;
            }
            if (interp && ch == '{')
            {
                if (j + 1 < n && src[j + 1] == '{') { j += 2; continue; }
                j = Code(src, j + 1, found, stopAtBrace: true) + 1;
                continue;
            }
            if (interp && ch == '}' && j + 1 < n && src[j + 1] == '}') { j += 2; continue; }
            j++;
        }
        int end = Math.Min(j + 1, n);
        found.Insert(at, new Literal(start, end, src[start..end], interp, verbatim, false));
        return end;
    }

    // ── 리터럴 → 실행 때의 글 ──

    /// <summary>보통·축자 문자열 리터럴의 값.</summary>
    public static string ValueOf(Literal lit)
    {
        int q = lit.Text.IndexOf('"');
        string body = lit.Text[(q + 1)..^1];
        return lit.Verbatim ? body.Replace("\"\"", "\"") : Unescape(body);
    }

    /// <summary>보간 문자열 리터럴에서 컴파일러가 만드는 서식 — 구멍은 차례대로 {0}·{1}…(정렬·서식 지정은 그대로), 글 속의 중괄호는 겹쳐 둔다.</summary>
    public static string FormatOf(Literal lit)
    {
        int q = lit.Text.IndexOf('"');
        string body = lit.Text[(q + 1)..^1];
        var format = new StringBuilder();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            string s = lit.Verbatim ? text.ToString().Replace("\"\"", "\"") : Unescape(text.ToString());
            format.Append(s);   // 글 조각에는 중괄호가 없다 — 겹중괄호는 아래에서 따로 넣고, 이스케이프(\u007B)로는 보간 문자열에 중괄호를 적을 수 없다(CS8087)
            text.Clear();
        }
        int index = 0, i = 0, n = body.Length;
        while (i < n)
        {
            char c = body[i];
            if (c == '{' && i + 1 < n && body[i + 1] == '{') { Flush(); format.Append("{{"); i += 2; continue; }
            if (c == '}' && i + 1 < n && body[i + 1] == '}') { Flush(); format.Append("}}"); i += 2; continue; }
            if (c == '{')
            {
                Flush();
                int end = Code(body, i + 1, [], stopAtBrace: true);
                var (align, spec) = SplitHole(body[(i + 1)..end]);
                // 정렬은 숫자로 적은 것만 안다 — 상수 이름(,Width)이면 컴파일러는 그 값을 넣고, 제네릭의 쉼표(F<int, string>())는 정렬이 아니다. 둘 다 여기서는 틀린 열쇠가 되므로 멈춘다
                if (align != null && !int.TryParse(align, out _))
                    throw new InvalidDataException($"보간 구멍의 정렬(,{align})을 숫자로 읽지 못했어요 — Loc.F에 넣는 글에서는 정렬을 숫자로 적고, 쉼표가 든 식은 괄호로 감싼다: {lit.Text}");
                format.Append('{').Append(index++);
                if (align != null) format.Append(',').Append(align);
                if (spec != null) format.Append(':').Append(lit.Verbatim ? spec.Replace("\"\"", "\"") : Unescape(spec));   // 서식 지정도 글이다 — {t:hh\\:mm}의 이스케이프를 푼다
                format.Append('}');
                i = end + 1;
                continue;
            }
            if (!lit.Verbatim && c == '\\') { text.Append(body, i, 2); i += 2; continue; }
            text.Append(c);
            i++;
        }
        Flush();
        return format.ToString();
    }

    /// <summary>구멍 안의 식에서 맨 바깥 수준의 정렬(,n)과 서식(:fmt)을 가른다 — 괄호·문자열 안의 쉼표·쌍점은 식의 일부다.</summary>
    private static (string? Align, string? Format) SplitHole(string hole)
    {
        int depth = 0, comma = -1, colon = -1, n = hole.Length;
        for (int i = 0; i < n; i++)
        {
            char c = hole[i];
            if (c is '"' or '$' or '@')
            {
                int q = i;
                while (q < n && hole[q] is '$' or '@') q++;
                if (q < n && hole[q] == '"') { i = String(hole, i, q, []) - 1; continue; }
            }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < n && hole[j] != '\'') { if (hole[j] == '\\') j++; j++; }
                i = j;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == ',' && comma < 0 && colon < 0) comma = i;
            else if (depth == 0 && c == ':' && colon < 0)
            {
                if ((i + 1 < n && hole[i + 1] == ':') || (i > 0 && hole[i - 1] == ':')) { i++; continue; }   // global::X
                colon = i;
            }
        }
        string? spec = colon >= 0 ? hole[(colon + 1)..] : null;
        string head = colon >= 0 ? hole[..colon] : hole;
        string? align = comma >= 0 ? head[(comma + 1)..].Trim() : null;
        return (align, spec);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                case 'U': sb.Append(char.ConvertFromUtf32(Convert.ToInt32(s.Substring(i + 1, 8), 16))); i += 8; break;
                case 'x':
                    int len = 0;
                    while (len < 4 && i + 1 + len < s.Length && Uri.IsHexDigit(s[i + 1 + len])) len++;
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, len), 16));
                    i += len;
                    break;
                default: sb.Append(e); break;   // \\ \" \'
            }
        }
        return sb.ToString();
    }

    // ── 소스 전체에서 열쇠 모으기 ──

    /// <summary>화면 글이 아닌 것만 든 개발용 진입점 — 콘솔·로그에 한국어로 적는다(번역하지 않는다).</summary>
    public static readonly string[] DevOnlyFiles = ["Program.cs", "SelfTest.cs"];

    public sealed record Use(string Key, string Kind, string Where);

    /// <summary>UiText(App)가 번역해 넣는 속성의 이름 — <c>UiText.DisplayProperties</c>와 같아야 한다(시험은 App을 참조하지 않아 여기에 한 번 더 적는다).
    /// XAML에서 이 밖의 속성에 한국어 글을 적으면 번역표에 있어도 화면은 한국어로 남는다.</summary>
    public static readonly string[] XamlTextProperties = ["Text", "Content", "ToolTip", "Header", "Title", "PlaceholderText"];

    /// <summary>src의 C# 파일들(bin·obj 빼고).</summary>
    public static IEnumerable<string> SourceFiles(string root) =>
        new[] { "NekoManga.Core", "NekoManga.App" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>C# 파일 하나에서: Loc.T/Loc.F/Loc.Key에 든 리터럴(열쇠)과, 한글이 들었는데 감싸지도 표시하지도 않은 리터럴(unwrapped).</summary>
    public static (List<Use> Keys, List<string> Unwrapped) ScanCSharp(string file, string root)
    {
        var src = File.ReadAllText(file);
        var lines = src.Split('\n');
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        var keys = new List<Use>();
        var unwrapped = new List<string>();
        foreach (var lit in Literals(src))
        {
            string head = src[Math.Max(0, lit.Start - 8)..lit.Start];
            string? kind = head.EndsWith("Loc.T(") ? "T" : head.EndsWith("Loc.F(") ? "F" : head.EndsWith("Loc.Key(") ? "Key" : null;
            int line = src.AsSpan(0, lit.Start).Count('\n') + 1;
            if (kind != null)
            {
                Assert.True(kind == "F" == lit.Interpolated, $"{rel}:{line}: Loc.T·Loc.Key에는 보통 문자열을, Loc.F에는 보간 문자열($\"…\")을 넣는다");
                Assert.False(lit.Raw, $"{rel}:{line}: 원시 문자열(\"\"\"…\"\"\")은 Loc에 넣지 않는다 — 열쇠를 뽑는 도구가 들여쓰기 규칙을 다루지 않는다");
                // 인자는 리터럴 하나여야 한다 — Loc.T("저장 실패다냥: " + 까닭)은 실행 때 이어 붙인 글을 찾으므로 표에 있을 수 없다(여기서는 리터럴만 열쇠로 보여 통과해 버린다)
                if (!src.AsSpan(lit.End).TrimStart().StartsWith(")"))
                    unwrapped.Add($"{rel}:{line}: Loc.{kind}(…)의 괄호 안에는 리터럴 하나만 — 값은 Loc.F($\"… {{값}}\")로 끼우거나 밖에서 이어 붙인다: {lines[line - 1].Trim()}");
                keys.Add(new Use(kind == "F" ? FormatOf(lit) : ValueOf(lit), kind, $"{rel}:{line}"));
                continue;
            }
            if (!HasHangul(OwnText(lit))) continue;
            string here = lines[line - 1], above = line >= 2 ? lines[line - 2].TrimStart() : "";
            bool Marked(string tag) => here.Contains(tag) || (above.StartsWith("//") && above.Contains(tag));
            // loc:key = 감쌀 수 없는 자리(const)의 글 — 쓰는 쪽이 Loc.T(값)으로 번역하므로 표에 있어야 한다. loc:none = 화면 글이 아니다
            if (Marked("loc:key") && !lit.Interpolated && !lit.Raw) keys.Add(new Use(ValueOf(lit), "Key", $"{rel}:{line}"));
            else if (!Marked("loc:none")) unwrapped.Add($"{rel}:{line}: {here.Trim()}");
        }
        return (keys, unwrapped);
    }

    /// <summary>리터럴의 글에서 보간 구멍({…})을 뺀 것 — 구멍 안의 다른 리터럴의 한글을 이 리터럴의 것으로 세지 않게.</summary>
    private static string OwnText(Literal lit)
    {
        if (!lit.Interpolated || lit.Raw) return lit.Text;
        var sb = new StringBuilder();
        string t = lit.Text;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '{' && i + 1 < t.Length && t[i + 1] != '{')
            {
                i = Code(t, i + 1, [], stopAtBrace: true);
                continue;
            }
            if (t[i] == '{') i++;   // {{
            sb.Append(t[i]);
        }
        return sb.ToString();
    }

    /// <summary>XAML의 글 속성 가운데 한글이 든 것 — App의 UiText가 창을 훑어 번역하는 글이다. 바인딩 같은 마크업 확장({…}), Tag·Name, TextBox의 Text는 UiText도 건드리지 않는다.
    /// XML 파서가 속성 값을 정규화한다(줄 바꿈 → 빈칸) — WPF가 읽는 값과 같다.</summary>
    public static (List<Use> Keys, List<string> Untranslatable) ScanXaml(string file, string root)
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        var keys = new List<Use>();
        var untranslatable = new List<string>();   // 한국어 글이 UiText가 보지 않는 자리에 있다
        foreach (var el in XDocument.Load(file).Descendants())
        {
            foreach (var a in el.Attributes())
            {
                string name = a.Name.LocalName;
                if (name is "Tag" or "Name" or "Uid" or "Class" or "Key" || !HasHangul(a.Value) || a.Value.StartsWith('{')) continue;
                if (el.Name.LocalName == "TextBox" && name == "Text") continue;
                if (!XamlTextProperties.Contains(name)) untranslatable.Add($"{rel}: <{el.Name.LocalName} {name}=\"{a.Value}\"> — UiText가 번역하지 않는 속성");
                keys.Add(new Use(a.Value, "xaml", $"{rel}:{el.Name.LocalName}.{name}"));
            }
            // 요소 사이에 적은 글(<TextBlock>글</TextBlock>)은 공백 처리가 속성과 달라 열쇠를 똑같이 뽑기 어렵다 — 글은 속성에 적는다
            foreach (var text in el.Nodes().OfType<XText>())
                if (HasHangul(text.Value)) untranslatable.Add($"{rel}: <{el.Name.LocalName}> 안에 적은 글 \"{text.Value.Trim()}\" — 속성(Text=\"…\")에 적는다");
        }
        return (keys, untranslatable);
    }

    /// <summary>저장소 뿌리(NekoManga.sln이 있는 폴더) — 시험은 bin 아래에서 돈다.</summary>
    public static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NekoManga.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("NekoManga.sln을 찾지 못했어요 — 이 시험은 소스를 훑으므로 저장소 안에서 빌드한 자리에서 돌려야 한다.");
    }
}
