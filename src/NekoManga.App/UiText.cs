using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using NekoManga.Core;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;

namespace NekoManga.App;

/// <summary>XAML에 적힌 한국어 글을 지금 화면 언어로 바꾼다 — XAML은 한국어 그대로 두고(열쇠), 창을 훑어 번역표(<see cref="Loc"/>)에 있는 글만 바꿔 넣는다.
/// <para>보는 것: 요소에 **직접 적힌** 문자열 속성(Text·Content·Header·ToolTip·Title·PlaceholderText … — 로컬 값이 문자열이고 한글이 든 것).
/// 바인딩으로 오는 글과 코드가 넣는 글은 그 글을 만드는 자리에서 <c>Loc.T</c>/<c>Loc.F</c>로 번역한다.</para>
/// <para>언제: 창 생성자에서 <see cref="Apply"/>(뜨기 전에 다 바꿔 한국어가 비치지 않게) + 요소가 화면에 올라올 때(<c>Loaded</c> 클래스 처리기 —
/// 나중에 만든 요소·열릴 때 만들어지는 메뉴). 바꾼 자리는 원문을 기억해, 언어가 바뀌면(<see cref="Loc.Changed"/>) 열린 창을 원문에서 다시 번역한다.
/// 요소에 **직접 적힌 값**만 본다 — 스타일의 Setter나 템플릿이 넣는 글은 로컬 값이 아니라 보이지 않는다(그런 곳에 한국어 글을 적지 않는다).</para>
/// <para>코드가 지어 넣는 글(값이 끼는 글, 상태에 따라 바뀌는 단추 이름)은 <see cref="Bind"/>로 넣으면 언어가 바뀔 때 다시 지어진다 — 번역된 글에는 한글이 없어 원문을 되찾을 수 없다.</para>
/// <para>보는 속성은 이름으로 정해 둔다(<see cref="DisplayProperties"/>) — 문자열인 로컬 값을 전부 바꾸면 안 된다: <c>ComboBox.Text</c>(고른 항목의 글이 로컬 값으로 남는다)를
/// 번역해 넣자 아직 한국어인 항목들과 맞지 않아 **선택이 풀렸다**(결과 창의 모델이 '재생성'에서 벗어남 — 하네스 lang 모드에서 잡음). <c>DataContext</c>·<c>SelectedValue</c>도 문자열일 수 있다.
/// 건드리지 않는 것: <c>Tag</c>·<c>Name</c>(코드가 견주는 값), <c>TextBox.Text</c>·<c>ComboBox.Text</c>(사용자가 적거나 고른 글).</para></summary>
public static class UiText
{
    /// <summary>바꿔 놓은 자리의 원문과 우리가 넣은 글 — 지금 값이 우리가 넣은 글 그대로일 때만 원문에서 다시 번역한다(그 사이 코드가 다른 글을 넣었으면 그 글이 새 원문).</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, (string Source, string Shown)>> Sources = new();
    /// <summary><see cref="Bind"/>로 넣은 글을 짓는 함수 — 언어가 바뀌면 다시 불러 넣는다.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Func<string>>> Makers = new();
    private static bool _hooked;

    /// <summary>이 요소의 글은 번역하지 않는다 — 언어 이름처럼 제 글 그대로 보여야 하는 것(「한국어」가 번역표의 "Korean"으로 바뀌면 안 된다).</summary>
    public static readonly DependencyProperty KeepProperty =
        DependencyProperty.RegisterAttached("Keep", typeof(bool), typeof(UiText), new PropertyMetadata(false));
    public static void SetKeep(DependencyObject d, bool value) => d.SetValue(KeepProperty, value);
    public static bool GetKeep(DependencyObject d) => (bool)d.GetValue(KeepProperty);

    /// <summary>우리가 번역해 넣은 자리의 원문(한국어) — 그 뒤 코드가 다른 글을 넣었거나 번역한 적이 없으면 null.
    /// 보이는 글의 일부만 바꾸는 코드(펼침 단추의 화살표)가 번역된 글을 잘라 붙이지 않고 원문에서 다시 만들게.</summary>
    public static string? SourceOf(DependencyObject d, DependencyProperty p) =>
        Sources.TryGetValue(d, out var known) && known.TryGetValue(p, out var was) && d.ReadLocalValue(p) is string now && now == was.Shown ? was.Source : null;

    /// <summary>코드가 지어 넣는 글을 '지금 언어로 다시 지을 수 있게' 넣는다 — <c>UiText.Bind(BtnUpdate, ContentProperty, () => Loc.F($"🎀 v{v} 업데이트!"))</c>.
    /// 지금 한 번 넣고, 화면 언어가 바뀔 때마다 make를 다시 불러 넣는다. 그냥 <c>e.Text = Loc.F(…)</c>로 넣은 글은 넣을 때의 언어로 남는다
    /// (금방 바뀌는 상태 줄은 그래도 된다 — 오래 떠 있는 창의 이름표·요약에 쓴다). 그 속성에 글을 넣는 곳은 **전부** Bind로 한다: 직접 넣은 글은 다음 언어 전환 때 make의 글로 덮인다.</summary>
    public static void Bind(DependencyObject d, DependencyProperty p, Func<string> make)
    {
        if (!Makers.TryGetValue(d, out var makers)) { makers = new(); Makers.Add(d, makers); }
        makers[p] = make;
        d.SetValue(p, make());
        if (Sources.TryGetValue(d, out var known)) known.Remove(p);   // 이제 이 자리의 글은 make가 맡는다
    }

    /// <summary>언어 고르는 칸을 채우고 지금 언어를 고른다 — 한국어 / English / 日本語(제 언어로 적은 이름이라 지금 화면 언어를 못 읽어도 찾는다).
    /// 고르면 <see cref="ApplyPicked"/>.</summary>
    public static void FillLanguagePicker(ComboBox cmb)
    {
        cmb.Items.Clear();
        foreach (var lang in Loc.Languages)
        {
            var item = new ComboBoxItem { Content = Loc.NativeName(lang), Tag = lang };
            SetKeep(item, true);
            cmb.Items.Add(item);
            if (lang == Loc.Lang) cmb.SelectedItem = item;
        }
    }

    /// <summary>칸에서 고른 언어를 설정에 적고 바로 적용한다(열린 창의 글이 바뀐다 — <see cref="Loc.Changed"/>). 바뀌었으면 true.
    /// save=false면 설정 파일에는 쓰지 않는다(UI 확인용 데모 창).</summary>
    public static bool ApplyPicked(ComboBox cmb, bool save)
    {
        if ((cmb.SelectedItem as ComboBoxItem)?.Tag is not string lang || lang == Loc.Lang) return false;
        App.Settings.UiLanguage = lang;
        if (save) { try { App.Settings.Save(); } catch { /* 못 써도 이번 실행은 바뀐다 — 다음에 설정이 저장될 때 같이 적힌다 */ } }
        Loc.SetLanguage(lang);
        return true;
    }

    /// <summary>앱이 시작할 때 한 번(언어를 정한 뒤, 창을 만들기 전).</summary>
    public static void Init()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
        Loc.Changed += () =>
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) RetranslateOpenWindows(); else d.InvokeAsync(RetranslateOpenWindows);
        };
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        if (sender is Window w) SetLanguageOf(w);
        ApplyOne(d);
    }

    /// <summary>창(또는 그 일부)의 글을 지금 언어로 — 창 생성자에서 <c>InitializeComponent()</c> 바로 뒤에 부른다. 여러 번 불러도 된다.</summary>
    public static void Apply(DependencyObject root)
    {
        if (root is Window w) SetLanguageOf(w);
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            ApplyOne(d);
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject c) stack.Push(c);
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            // 트리에 안 달린 것들: 풍선 도움말·오른쪽 메뉴가 요소일 때, 목록의 열 머리(GridViewColumn은 요소가 아니다)
            if (d is FrameworkElement fe)
            {
                if (fe.ToolTip is DependencyObject tip) stack.Push(tip);
                if (fe.ContextMenu is { } menu) stack.Push(menu);
            }
            if (d is ListView { View: GridView gv })
                foreach (var col in gv.Columns) stack.Push(col);
        }
    }

    /// <summary>언어가 바뀌었을 때 — 열린 창을 전부 다시 훑는다(바꿔 놓은 자리는 원문에서 다시).</summary>
    public static void RetranslateOpenWindows()
    {
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows) Apply(w);
    }

    /// <summary>글꼴 고르기에 쓰이는 언어 표시(xml:lang) — 일본어 화면에서 한자·가나가 일본어 글꼴로 나오게 일본어일 때만 건다.
    /// 한국어·영어에서는 **건드리지 않는다**(WPF의 기본값 그대로): 창에 ko-KR을 걸면 글꼴 대체가 달라져, 한국어 화면에서 일본어 파일 이름·[시작] 단추의 가오모지(ﾉ ヮ ･ ﾟ)가
    /// 전과 다른 글꼴로 나올 수 있다(독립 검수 2026-11-16 — 한국어 화면은 이 기능 전과 똑같아야 한다).</summary>
    private static void SetLanguageOf(Window w)
    {
        if (Loc.Lang == "ja")
        {
            if (w.Language.IetfLanguageTag != "ja-JP") w.Language = XmlLanguage.GetLanguage("ja-JP");
        }
        else if (w.ReadLocalValue(FrameworkElement.LanguageProperty) != DependencyProperty.UnsetValue) w.ClearValue(FrameworkElement.LanguageProperty);
    }

    private static void ApplyOne(DependencyObject d)
    {
        if (GetKeep(d)) return;
        // 코드가 지어 넣는 글(Bind) — 지금 언어로 다시 짓는다
        Makers.TryGetValue(d, out var makers);
        if (makers != null)
            foreach (var (property, make) in makers.ToArray())
            {
                var text = make();
                if (d.ReadLocalValue(property) as string != text) d.SetValue(property, text);
            }

        // 돌면서 값을 바꾸면 열거가 깨진다 — 바꿀 것을 먼저 모은다
        List<(DependencyProperty Property, string Value)>? found = null;
        var e = d.GetLocalValueEnumerator();
        while (e.MoveNext())
        {
            var entry = e.Current;
            if (entry.Value is not string s || s.Length == 0 || entry.Property.ReadOnly || Skipped(d, entry.Property)) continue;
            if (makers != null && makers.ContainsKey(entry.Property)) continue;
            (found ??= []).Add((entry.Property, s));
        }
        if (found == null) return;

        Sources.TryGetValue(d, out var known);
        foreach (var (property, now) in found)
        {
            // 지금 값이 우리가 넣은 글 그대로면 원문에서 다시 번역한다. 다른 글이면(코드가 넣었다) 그 글을 원문으로 보되, 기억은 지우지 않는다 —
            // 코드가 잠깐 다른 글을 보였다가("뽑는 중...") 들고 있던 옛 이름표를 도로 넣으면, 그 이름표를 다시 원문에서 번역할 수 있어야 한다
            bool ours = known != null && known.TryGetValue(property, out var was) && was.Shown == now;
            string source = ours ? known![property].Source : now;
            if (!HasHangul(source)) continue;
            var shown = Loc.T(source);
            if (shown == now) continue;
            d.SetValue(property, shown);
            if (shown == source) { known?.Remove(property); continue; }   // 한국어로 돌아왔다 — 기억할 것이 없다
            if (known == null) { known = new(); Sources.Add(d, known); }
            known[property] = (source, shown);
        }
    }

    /// <summary>화면에 글로 보이는 속성의 이름 — XAML이 글을 적는 곳은 Text·Content·ToolTip·Header·Title뿐이고(2026-11-16 409곳), PlaceholderText는 WPF-UI 입력 칸의 안내 글.
    /// 새 종류의 글 속성을 XAML에 쓰면 여기에 더한다(LocCoverageTests는 XAML의 한글 속성을 이름과 상관없이 열쇠로 세므로, 빠뜨리면 번역은 있는데 화면은 한국어로 남는다 — 하네스 lang 모드의 '남은 한글'에 잡힌다).</summary>
    private static readonly HashSet<string> DisplayProperties = ["Text", "Content", "ToolTip", "Header", "Title", "PlaceholderText"];

    private static bool Skipped(DependencyObject d, DependencyProperty p) =>
        !DisplayProperties.Contains(p.Name)
        || (p.Name == "Text" && d is System.Windows.Controls.Primitives.TextBoxBase or ComboBox);   // 사용자가 적거나 고른 글

    /// <summary>한글 음절·자모가 하나라도 있는가 — 열쇠는 한국어 글이라, 없는 글은 표를 찾아볼 것도 없다.</summary>
    public static bool HasHangul(string s)
    {
        foreach (char c in s)
            if (c is (>= '가' and <= '힣') or (>= 'ㄱ' and <= 'ㅣ')) return true;
        return false;
    }
}
