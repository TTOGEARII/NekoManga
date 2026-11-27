using System.Text;
using System.Text.RegularExpressions;
using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary>화 전체 검수(2026-11-28 사용자 "2검수해줘" — 사용자의 Carrot 규칙 B1 "넓은 문맥으로 번역하고, 전체 판독 뒤 일관성을 확인한다").
/// 번역은 쪽마다 따로 간다(앞 쪽 서른 줄만 문맥으로 본다) — 그래서 같은 이름이 쪽마다 다르게 적히고, 한 인물의 말투가 쪽마다 바뀌고, 뒤 쪽을 봐야 뜻이 잡히는 줄이 어긋난다.
/// 식자까지 끝난 화의 원문·번역을 **통째로** LLM 번역기(DeepSeek·Grok)에 보내 그런 줄만 고쳐 받고, 고친 장을 다시 식자한다(번역 표 들여오기와 같은 길).
///
/// 지키는 것:
/// · 사용자가 고친 줄(<see cref="TextBlockInfo.IsEdited"/>)·직접 나눈 줄은 고치지 않는다 — 기준으로만 보낸다(B1 "확정한 문자열은 다시 번역하지 않는다").
///   번역기의 글을 모르는 줄(이 기능 전에 만든 결과)도 고치지 않는다: 고친 글인지 알 수 없다.
/// · 조용히 바꾸지 않는다(A4): 고친 줄에는 표시(<see cref="ReviewMark.Revised"/>)와 전의 글(<see cref="TextBlockInfo.Prior"/>)이 남아 결과 창에서 보이고 되돌릴 수 있다.
/// · 일관성만 고치라는 물음에 줄의 대부분을 다시 써 오면(<see cref="MaxChangeShare"/>) 그 답은 통째로 버린다 — 다시 번역한 것이지 검수가 아니다.
/// · 다른 번역기로 넘어가지 않는다. 모델이 못 답하면 그 까닭이 화면에 나오고 결과는 그대로다.</summary>
public static class ChapterReview
{
    /// <summary>번역기에 보내는 줄 하나 — <paramref name="Page"/>는 넘긴 쪽 목록의 차례(0부터), <paramref name="Index"/>는 그 쪽 식자 정보의 블록 번호(0부터).
    /// <paramref name="Locked"/>면 고쳐 와도 버린다. 잠근 까닭은 둘이다: 사용자가 손댄 줄(이름·말투의 기준으로 보낸다)과, 번역기의 글을 몰라 잠근 줄(<paramref name="Unknown"/> —
    /// 이 기능 전에 만든 결과. 기준이 아니다: 그 줄이 흔들린 표기일 수 있다). <paramref name="Declined"/> = 사용자가 되돌리기로 거절한 검수의 글(같은 글로 다시 고쳐 오면 받지 않는다).</summary>
    public sealed record Line(int Page, int Index, string Source, string Translation, bool Locked, bool Unknown = false, string Declined = "");

    /// <summary>고칠 줄 하나 — <paramref name="Before"/>는 물었을 때의 번역이다(적용할 때 그 줄이 아직 그 글인지 본다).</summary>
    public sealed record Change(int Page, int Index, string Before, string After);

    /// <summary>모델에 물은 결과. <paramref name="Asked"/> = 고쳐도 되는 줄 수(잠긴 줄을 뺀 것), <paramref name="Rejected"/> = 받았지만 버린 답(잠긴 줄을 고쳤다·원문 글자가 남았다),
    /// <paramref name="TooMany"/> = 너무 많이 고치려 해서 통째로 버린 묶음이 있다, <paramref name="Unread"/> = 답을 읽지 못한 묶음이 있다(빈 답·[n] 꼴이 아닌 답 — '고칠 줄이 없다'와 다르다).</summary>
    public sealed record Outcome(IReadOnlyList<Change> Changes, int Asked, int Rejected, bool TooMany, bool Unread);

    /// <summary>결과물에 적용한 결과. <paramref name="Pages"/>·<paramref name="Lines"/> = 본 장과 줄, <paramref name="Open"/> = 그 가운데 고쳐도 되는 줄, <paramref name="Changed"/> = 고쳐서 다시 식자한 줄,
    /// <paramref name="Skipped"/> = 다른 언어로 번역돼 있어 뺀 장, <paramref name="Failures"/> = 다시 식자하지 못한 장(파일 이름: 까닭),
    /// <paramref name="Stopped"/> = 다시 식자하는 도중에 멈췄다(<paramref name="Changed"/>는 그 전에 끝낸 줄), <paramref name="Unreadable"/> = 식자 정보가 있는데 쓰지 못해 뺀 장(json이 잠겼거나 깨졌다 · 식자 전 원고가 없다).</summary>
    public sealed record Report(int Pages, int Lines, int Open, int Changed, int Rejected, bool TooMany, bool Unread, int Skipped, IReadOnlyList<string> Failures,
        bool Stopped = false, int Unreadable = 0);

    /// <summary>한 번에 묻는 줄 수의 상한 — 넘으면 쪽 경계에서 묶음으로 나눠 묻는다(묶음 사이의 일관성은 보지 못한다).
    /// 실제 한 화(서른다섯 쪽 안팎)는 이백~삼백 줄이라 한 번에 간다. 줄 하나가 원문·번역 합쳐 60토큰쯤이다.</summary>
    internal const int MaxLinesPerRequest = 400;
    /// <summary>한 묶음에서 고쳐 오는 줄이 고쳐도 되는 줄의 이 비율을 넘으면 그 답은 쓰지 않는다 — 일관성을 맞춘 것이 아니라 다시 번역한 것이다.</summary>
    internal const double MaxChangeShare = 0.35;
    /// <summary>줄이 적은 화에서는 비율이 뜻이 없다(여섯 줄 가운데 셋) — 이만큼까지는 받는다.</summary>
    internal const int MinChangesAllowed = 6;

    internal static int Allowed(int open) => Math.Max(MinChangesAllowed, (int)Math.Ceiling(MaxChangeShare * open));

    /// <summary>쪽들의 식자 정보에서 번역기에 보낼 대사를 모은다 — 쪽 차례대로, 쪽 안에서는 읽는 차례로(<see cref="EmbeddedTranslator.ReadingOrder"/> — 번역할 때 보낸 차례와 같다).
    /// 효과음·숨긴 블록·[새 글 넣기]로 넣은 글(원문이 없다)·번역이 빈 블록은 보내지 않는다.</summary>
    public static List<Line> LinesOf(IReadOnlyList<IReadOnlyList<TextBlockInfo>> pages, bool rightToLeft)
    {
        var lines = new List<Line>();
        for (int p = 0; p < pages.Count; p++)
        {
            var blocks = pages[p];
            var sent = Enumerable.Range(0, blocks.Count).Where(i => !blocks[i].Sfx && !blocks[i].Hidden
                && !string.IsNullOrWhiteSpace(blocks[i].SourceText) && !string.IsNullOrWhiteSpace(blocks[i].Translation)).ToList();
            var boxes = sent.Select(i => new Rect(blocks[i].X1, blocks[i].Y1, blocks[i].X2 - blocks[i].X1, blocks[i].Y2 - blocks[i].Y1)).ToList();
            foreach (int k in EmbeddedTranslator.ReadingOrder(boxes, rightToLeft))
            {
                var b = blocks[sent[k]];
                lines.Add(new Line(p, sent[k], b.SourceText, b.Translation, Locked(b), Unknown(b), b.Declined ?? ""));
            }
        }
        return lines;
    }

    /// <summary>고치지 않는 줄인가 — 사용자가 고친 글·직접 나눈 줄·번역기의 글을 모르는 줄(이 기능 전의 결과: 고친 글인지 알 수 없다).</summary>
    internal static bool Locked(TextBlockInfo b) => b.ManualLines || string.IsNullOrEmpty(b.Machine) || b.IsEdited();

    /// <summary>잠갔지만 사용자가 손댄 줄은 아닌가 — 번역기의 글을 모를 뿐인 줄(옛 결과). 직접 나눈 줄은 손댄 줄이다.
    /// 전에는 잠근 줄을 전부 "사람이 고친 줄 — 이름과 말투의 기준"으로 보내, 옛 결과와 새 결과가 섞인 화에서 옛 결과의 흔들린 표기가 기준이 됐다(검토 2026-11-28).</summary>
    internal static bool Unknown(TextBlockInfo b) => !b.ManualLines && string.IsNullOrEmpty(b.Machine);

    /// <summary>검수 지시문. 번역 때의 지침(경칭·인물·작품 메모·용어집)을 그대로 붙인다 — 검수가 번역 때와 다른 기준으로 고치면 안 된다.
    /// 낱말별 보기는 넣지 않는다(사용자의 규칙 B3). 답은 **고친 줄만** 받는다: 전부 다시 받으면 안 고친 줄도 조금씩 달라져 온다.
    /// <paramref name="unknownLines"/>: 번역기의 글을 몰라 잠근 줄(<c>[n]**</c>)이 섞여 있다 — 그때만 그 표시의 뜻을 한 줄 덧붙인다. 그런 줄이 없는 화의 지시문은 실제 한 화로 재 본 그대로다.
    /// 표시를 별 둘로 한 까닭(실측 2026-11-28, DeepSeek·실제 한 화의 앞 열 쪽 75줄을 그런 줄로): 처음에 <c>[n]=</c>로 보냈더니 모델이 그것을 답의 꼴로 읽어 고친 줄을 <c>[n]= 글</c>로 적었고,
    /// 한 번은 고칠 수 있는 235줄을 전부 따라 적었다(답 14초). 별 하나(<c>[n]*</c>)는 두 번 다 따라 적지 않고 고친 줄만 왔다(9·13줄, 1~2초).</summary>
    internal static string BuildSystemPrompt(string srcName, string tgtName, TranslateGuide guide, IReadOnlyList<(string Source, string Target)> glossary, bool unknownLines = false)
    {
        var sb = new StringBuilder();
        sb.Append($"You are the editor checking a finished {tgtName} translation of one manga chapter. It was translated from {srcName} one page at a time, so the pages may disagree with each other. ")
          .Append("The user message lists every line of the chapter in reading order, grouped by page, as \"[n] source ⇒ translation\".\n\n")
          .Append("Read the whole chapter first. Then correct ONLY the lines that have one of these problems:\n")
          .Append("- The same name, nickname, form of address (a name with its honorific), title or recurring term is written differently in different lines. Check every occurrence, including the ones inside longer sentences, ")
          .Append("and use one form for the whole chapter: the glossary's if it is listed, otherwise the form that follows the rules below, otherwise the form the chapter uses most.\n")
          .Append("- A character's way of speaking to the same person (formal or casual speech, how they address that person) flips between lines although the source does not change. Make it follow the source and stay consistent.\n")
          .Append("- A line was translated without its context and now contradicts the lines around it: the wrong subject or referent, or a sentence that runs across several bubbles but reads as unrelated fragments. Retranslate that line so the passage reads as one conversation.\n")
          .Append($"- A line was left untranslated or still contains {srcName} text.\n\n")
          .Append("Rules:\n")
          .Append("- This is a consistency check, not a rewrite. Leave every other line exactly as it is: do not polish style, swap synonyms or change punctuation on a line that is already correct. Most lines need no change.\n")
          .Append("- A line whose number is followed by * was corrected by a human. Never change it, and take it as the reference for names and tone.\n")
          .Append(unknownLines ? "- A line whose number is followed by ** cannot be changed either, but it is not a reference: nobody checked it, and it may itself be the inconsistent one.\n" : "")
          .Append("- A corrected line translates its own source line only: never move words between lines, and never merge, split, add or drop lines.\n")
          .Append($"- Write each corrected line entirely in natural spoken {tgtName}, with no {srcName} characters, and keep its register: never make it cruder, more explicit, more formal or more polite than the source.\n")
          .Append(LlmTranslator.HonorificRule(guide.Honorifics, tgtName))
          .Append("\nOutput ONLY the lines you change, one per line, each as its number in [n] form followed by the corrected translation — no source text, no notes, no explanations. ")
          .Append("If no line needs a change, output exactly: NONE");
        if (guide.Characters.Count > 0)
        {
            sb.Append("\n\nCharacters (who they are and how they speak — keep it consistent):");
            foreach (var (name, voice) in guide.Characters.Take(TranslateGuide.MaxCharacters)) sb.Append('\n').Append(name).Append(": ").Append(voice);
        }
        if (!string.IsNullOrWhiteSpace(guide.Notes))
            sb.Append("\n\nNotes for this work (follow them):\n").Append(guide.Notes.Length > TranslateGuide.MaxNotesChars ? guide.Notes[..TranslateGuide.MaxNotesChars] : guide.Notes);
        if (glossary.Count > 0)
        {
            sb.Append("\n\nGlossary (these terms are always translated exactly as given):");
            foreach (var (src, tgt) in glossary) sb.Append('\n').Append(src).Append(" => ").Append(tgt);
        }
        return sb.ToString();
    }

    /// <summary>보내는 글 — 쪽마다 머리(<c># page k</c>, 넘긴 쪽 목록의 차례)를 두고 줄마다 <c>[n] 원문 ⇒ 번역</c>. 번호는 이 묶음 안에서 1부터다. 사용자가 손댄 줄은 <c>[n]*</c>, 번역기의 글을 몰라 잠근 줄은 <c>[n]**</c>.</summary>
    internal static string BuildUserText(IReadOnlyList<Line> lines)
    {
        var sb = new StringBuilder();
        int page = -1;
        for (int k = 0; k < lines.Count; k++)
        {
            var l = lines[k];
            if (sb.Length > 0) sb.Append('\n');
            if (l.Page != page) { page = l.Page; sb.Append("# page ").Append(page + 1).Append('\n'); }
            sb.Append('[').Append(k + 1).Append(']').Append(!l.Locked ? " " : l.Unknown ? "** " : "* ").Append(OneLine(l.Source)).Append(" ⇒ ").Append(OneLine(l.Translation));
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => WhiteRun.Replace(s ?? "", " ").Trim();

    private static readonly Regex WhiteRun = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Think = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);
    /// <summary>답의 줄 — <c>[n] 글</c> 꼴만 받는다(번호 뒤에 빈칸이나 쌍점). 번역 요청은 <c>1. 글</c>도 받지만 여기서는 받지 않는다: "1. 고칠 곳이 없습니다" 같은 말이 첫 줄의 번역으로 들어간다.
    /// "[3]과 [7]의 이름이 다릅니다"처럼 번호에 말이 바로 붙은 줄도 아니다.</summary>
    private static readonly Regex Answered = new(@"^\s*(?:[-•]\s*)?\[(\d+)\]\*{0,2}(?:\s*[:：]\s*|\s+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberTag = new(@"\[\d+\]", RegexOptions.Compiled);
    /// <summary>전의 글에 견준 새 글의 길이가 이 범위를 벗어나면 고침이 아니라 딴 글이다("변경"·덧붙인 설명) — 전의 글이 <see cref="LengthCheckFrom"/>자 이상일 때만 본다(짧은 글은 한두 자로 배가 된다).
    /// 실제 한 화에서 받은 맞는 고침 11가지는 0.8~1.3배였다.</summary>
    internal const double MinLengthShare = 0.4, MaxLengthShare = 2.5;
    internal const int LengthCheckFrom = 5;
    private static readonly Regex Nothing = new(@"^\s*NONE[\s.]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>읽을 수 있는 답인가 — "NONE"이거나, 이 묶음의 번호를 단 줄이 하나라도 있다. 빈 답(추론만 하다 끝난 모델)·말로 한 답은 아니다:
    /// 그런 답을 '고칠 줄이 없다'로 치면 검수가 된 것처럼 보인다(사용자의 규칙 A4).</summary>
    internal static bool Readable(string answer, int count)
    {
        var text = Think.Replace(answer ?? "", "");
        if (Nothing.IsMatch(text)) return true;
        foreach (var raw in text.Split('\n'))
            if (Answered.Match(raw.TrimEnd('\r')) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= count) return true;
        return false;
    }

    /// <summary>모델의 답에서 쓸 수 있는 바꿈만 고른다. 버리는 것: 없는 번호 · 같은 번호의 둘째 답 · 지금 글과 같은 답(바뀐 것이 아니다) ·
    /// 잠긴 줄을 고친 답 · 검증(<see cref="TranslationCheck"/>)에 걸린 답(비었다·원문 글자가 남았다) · 믿을 수 없는 꼴의 답(<see cref="Dubious"/>) — 같은 답을 뺀 나머지는 <paramref name="rejected"/>로 센다.
    /// 글은 번역 때와 같은 뒷손질(<see cref="EmbeddedTranslator.PolishTranslation"/>·<see cref="EmbeddedTranslator.TameRunaway"/>)을 거친다. 원문까지 따라 적은 답은 화살표 뒤만 쓰고, 글을 감싼 꾸밈(**·`)은 벗긴다.
    /// 검증은 글자의 종류만 본다 — 그것만으로는 "전 → 후"·"(호칭 통일)"·"변경" 같은 답이 다 통과해 그대로 식자된다(검토 2026-11-28).</summary>
    internal static List<Change> Pick(string answer, IReadOnlyList<Line> lines, string targetLang, out int rejected)
    {
        rejected = 0;
        var changes = new List<Change>();
        var seen = new bool[lines.Count];
        foreach (var raw in Think.Replace(answer ?? "", "").Split('\n'))
        {
            var m = Answered.Match(raw.TrimEnd('\r'));
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n) || n < 1 || n > lines.Count || seen[n - 1]) continue;
            seen[n - 1] = true;
            var line = lines[n - 1];
            // 한 줄에 번호가 둘이면 어디까지가 이 줄의 글인지 알 수 없다("[1] 가요 [2] 갑시다"가 통째로 첫 줄에 들어갔다) — 나눠 읽지 않고 버린다: 틀린 글이 들어가는 것보다 고침 하나를 잃는 쪽이 낫다
            if (NumberTag.Matches(raw).Count > 1) { rejected++; continue; }
            var text = m.Groups[2].Value;
            int arrow = text.LastIndexOf('⇒');
            if (arrow >= 0) text = text[(arrow + 1)..];
            text = Undress(text.Trim());
            text = EmbeddedTranslator.TameRunaway(EmbeddedTranslator.PolishTranslation(text, targetLang), line.Source).Trim();
            if (OneLine(text) == OneLine(line.Translation)) continue;
            // 사용자가 되돌리기로 거절한 바로 그 글 — 다시 넣지 않는다(버린 답으로 세지도 않는다: 번역기의 잘못이 아니다). 다른 글로 고쳐 오면 새 제안이다
            if (line.Declined.Length > 0 && OneLine(text) == OneLine(line.Declined)) continue;
            if (line.Locked || Dubious(text, line) || !TranslationCheck.IsAcceptable(text, targetLang, sfx: false, line.Source)) { rejected++; continue; }
            changes.Add(new Change(line.Page, line.Index, line.Translation, text));
        }
        return changes;
    }

    /// <summary>글을 통째로 감싼 꾸밈을 벗긴다 — <c>**글**</c>·<c>`글`</c>.</summary>
    private static string Undress(string text)
    {
        foreach (var wrap in new[] { "**", "`" })
            if (text.Length > 2 * wrap.Length && text.StartsWith(wrap, StringComparison.Ordinal) && text.EndsWith(wrap, StringComparison.Ordinal))
                text = text[wrap.Length..^wrap.Length].Trim();
        return text;
    }

    /// <summary>번역으로 쓰기에 믿을 수 없는 답인가 — 글자의 종류가 아니라 꼴로 본다: 전 → 후를 같이 적었다(화살표가 남았다) · 원문에도 전의 글에도 없는 괄호가 붙었다(덧붙인 말) ·
    /// 전의 글에 견줘 터무니없이 짧거나 길다(<see cref="MinLengthShare"/>~<see cref="MaxLengthShare"/>). 버린 줄은 전의 번역 그대로 남는다.</summary>
    internal static bool Dubious(string text, Line line)
    {
        if (text.Contains('→') || text.Contains("->", StringComparison.Ordinal) || text.Contains("=>", StringComparison.Ordinal)) return true;
        static bool Paren(string s) => s.IndexOfAny(['(', '（']) >= 0;
        if (Paren(text) && !Paren(line.Source) && !Paren(line.Translation)) return true;
        int was = OneLine(line.Translation).Length, now = OneLine(text).Length;
        return was >= LengthCheckFrom && (now < MinLengthShare * was || now > MaxLengthShare * was);
    }

    /// <summary>한 쪽의 블록에 그 쪽의 바꿈을 적용한다(<see cref="Change.Index"/>로 찾는다 — <see cref="Change.Page"/>는 부르는 쪽이 가른다). 그 줄이 물었을 때의 글이 아니면 건드리지 않는다
    /// (묻는 사이에 결과 창에서 고쳤다). 바꾼 줄은 번역기의 글(<see cref="TextBlockInfo.Machine"/>)도 새 글로 둔다 — '사용자가 고친 글'이 아니다. 전의 글은 맨 처음 것을 지킨다(두 번 검수해도 번역기의 글이 남는다).</summary>
    public static List<TextBlockInfo> Apply(IReadOnlyList<TextBlockInfo> blocks, IEnumerable<Change> changes, out int changed)
    {
        var list = blocks.ToList();
        changed = 0;
        foreach (var c in changes)
        {
            if (c.Index < 0 || c.Index >= list.Count) continue;
            var b = list[c.Index];
            if (b.Translation != c.Before) continue;
            // 처음 고칠 때만 전의 글과 그때의 확인 표시(메움·못 옮김)를 담는다 — 두 번 검수해도 맨 처음 것이 남는다
            bool first = string.IsNullOrEmpty(b.Prior);
            list[c.Index] = b with
            {
                Translation = c.After, Machine = c.After, Review = ReviewMark.Revised,
                Prior = first ? c.Before : b.Prior,
                PriorReview = first ? (b.Review == ReviewMark.Revised ? "" : b.Review) : b.PriorReview,
            };
            changed++;
        }
        return list;
    }

    /// <summary>검수가 고친 줄을 전의 글로 되돌린다(결과 창 [전의 글로 되돌리기]). 검수가 고친 줄이 아니면 그대로 돌려준다.
    /// 전에 '확인할 줄'이었으면(다른 번역기로 메움·못 옮김) 글·번역기의 글·표시를 전부 전으로 — ⚠가 다시 보인다. 전에는 되돌린 글이 ⚠ 없이 '내가 고친 글'이 되어,
    /// 못 옮긴 글이 다시 번역해도 지켜졌다(검토 2026-11-28). 그 줄은 다음 검수가 다시 고칠 수 있다(확인이 필요한 줄이다) —
    /// 다만 방금 거절한 그 글은 <see cref="TextBlockInfo.Declined"/>에 적어 두어 같은 고침이 말없이 되돌아오지 않게 한다(다른 글로 고쳐 오면 받는다).
    /// 표시가 없던 줄은 전처럼 글만 되돌린다: 번역기의 글(<see cref="TextBlockInfo.Machine"/>)과 달라 '내가 고친 글'이 되고, 다음 검수·다시 번역이 건드리지 않는다.</summary>
    public static TextBlockInfo Restore(TextBlockInfo b)
    {
        if (b.Review != ReviewMark.Revised || string.IsNullOrEmpty(b.Prior)) return b;
        return string.IsNullOrEmpty(b.PriorReview)
            ? b with { Translation = b.Prior }
            : b with { Translation = b.Prior, Machine = b.Prior, Review = b.PriorReview, Prior = "", PriorReview = "", Declined = b.Translation };
    }

    /// <summary>줄들을 한 번에 묻는 묶음으로 나눈다 — 쪽을 가르지 않는다(한 쪽이 상한보다 길면 그 쪽만으로 한 묶음).</summary>
    internal static List<List<Line>> Groups(IReadOnlyList<Line> lines)
    {
        var groups = new List<List<Line>>();
        var cur = new List<Line>();
        foreach (var page in lines.GroupBy(l => l.Page))
        {
            var p = page.ToList();
            if (cur.Count > 0 && cur.Count + p.Count > MaxLinesPerRequest) { groups.Add(cur); cur = []; }
            cur.AddRange(p);
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    /// <summary>줄들을 모델에 묻고 쓸 수 있는 바꿈을 돌려준다. 고쳐도 되는 줄이 없는 묶음은 묻지 않는다. 모델의 오류(HTTP·제한 시간·끊긴 답)는 그대로 던진다 — 받은 데까지 쓰지 않는다.</summary>
    public static async Task<Outcome> RunAsync(IChatModel model, IReadOnlyList<Line> lines, AppSettings s, CancellationToken ct)
    {
        var guide = new TranslateGuide(s.TranslateHonorifics, s.TranslateTone, s.TranslateNotes.Trim(), TranslateGuide.ParseCharacters(s.TranslateCharacters));
        var system = BuildSystemPrompt(LlmTranslator.LangName(s.TranslateSource), LlmTranslator.LangName(s.TranslateTarget), guide, Glossary.Parse(s.TranslateGlossary),
            unknownLines: lines.Any(l => l.Unknown));
        var changes = new List<Change>();
        int asked = 0, rejected = 0;
        bool tooMany = false, unread = false;
        foreach (var group in Groups(lines))
        {
            int open = group.Count(l => !l.Locked);
            if (open == 0) continue;
            ct.ThrowIfCancellationRequested();
            // 답은 고친 줄뿐이라 짧다 — 그래도 상한까지 고쳐 올 때(줄 하나 60토큰쯤)를 받을 만큼은 준다. 모자라 끊기면 모델 쪽이 한 번 키워 다시 묻는다
            var answer = await model.AskAsync(system, BuildUserText(group), Math.Clamp(1024 + Allowed(open) * 60, 2048, 8192), ct);
            var picked = Pick(answer, group, s.TranslateTarget, out int bad);
            asked += open;
            rejected += bad;
            if (!Readable(answer, group.Count)) { unread = true; continue; }
            // 고치려 든 줄을 센다 — 쓸 수 없어 버린 답(원문 글자가 남았다·잠긴 줄)도 다시 쓰려던 것이다. 받아들인 것만 세면 거의 다 다시 쓴 답의 남은 몇 줄이 적용된다
            if (picked.Count + bad > Allowed(open)) { tooMany = true; continue; }
            changes.AddRange(picked);
        }
        return new Outcome(changes, asked, rejected, tooMany, unread);
    }

    private static string FullPath(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

    /// <summary>식자까지 끝난 결과물들(<paramref name="outPaths"/> — 화의 쪽 차례)을 한 화로 보고 검수한 뒤, 고친 장만 지금 설정으로 다시 식자한다.
    /// 식자 정보가 없는 장(번역하지 않았다)은 건너뛰고, 지금 목표 언어와 다른 언어로 번역된 장은 섞어 보내지 않는다(<see cref="Report.Skipped"/>).
    /// 한 장의 다시 식자가 실패해도 나머지는 적용한다(<see cref="Report.Failures"/>). 모델의 오류는 던진다 — 그때는 아무 장도 바뀌지 않았다.
    /// 멈춤(<paramref name="ct"/>): 답을 기다리는 중이면 던지고(아무 장도 안 바뀌었다), 다시 식자하는 중이면 하던 장까지 끝내고 돌려준다(<see cref="Report.Stopped"/> — 끝낸 장은 그대로 둔다).</summary>
    public static async Task<Report> RunOnResultsAsync(IChatModel model, IReadOnlyList<string> outPaths, AppSettings s, IProgress<string>? status, CancellationToken ct)
    {
        var pages = new List<(string OutPath, LetterSidecar Sidecar)>();
        int skipped = 0, unreadable = 0;
        // 목록의 두 행이 같은 결과 자리로 떨어질 수 있다(출력 폴더 고정 + 같은 이름) — 두 번 읽으면 줄이 두 번 가고 둘째 저장이 첫째의 고침을 덮는다
        foreach (var outPath in outPaths.Select(FullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            LetterSidecar? sc = null;
            try { sc = LetterSidecar.Load(outPath); } catch { }
            // 식자 정보가 없는 장은 번역할 글이 없던 쪽이다. 있는데 못 쓰는 장(json이 잠김·깨짐, 식자 전 원고가 없다 — Load는 셋 다 null이다)은 센다 — 말없이 빠지면 그 장만 표기가 다른 채 남은 까닭을 알 수 없다
            if (sc == null) { try { if (File.Exists(LetterSidecar.JsonPathFor(outPath))) unreadable++; } catch { } continue; }
            if (sc.Blocks.Count == 0) continue;
            if (sc.Target.Length > 0 && !string.Equals(sc.Target, s.TranslateTarget, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            pages.Add((outPath, sc));
        }
        var lines = LinesOf(pages.Select(p => (IReadOnlyList<TextBlockInfo>)p.Sidecar.Blocks).ToList(), LangCode.IsJapanese(s.TranslateSource));
        int open = lines.Count(l => !l.Locked);
        if (open == 0) return new Report(pages.Count, lines.Count, 0, 0, 0, false, false, skipped, [], Unreadable: unreadable);

        status?.Report(Loc.F($"화 전체 검수: {pages.Count}장 {lines.Count}줄을 번역기에 보냈다냥 — 답을 기다리는 중이다냥…"));
        var outcome = await RunAsync(model, lines, s, ct);

        int changed = 0, at = 0;
        bool stopped = false;
        var failures = new List<string>();
        var byPage = outcome.Changes.GroupBy(c => c.Page).ToList();
        foreach (var group in byPage)
        {
            // 장마다 멈춤을 본다(전에는 답을 받으면 끝까지 갔다 — 장마다 0.3초쯤, 창을 닫아도 다 끝나야 닫혔다). 하던 장은 끝낸다: 식자 정보와 결과물이 어긋난 채 남지 않게
            if (ct.IsCancellationRequested) { stopped = true; break; }
            status?.Report(Loc.F($"화 전체 검수: 고친 장을 다시 식자하는 중이다냥 ({++at}/{byPage.Count})"));
            var (outPath, asked) = pages[group.Key];
            try
            {
                // 답을 기다리는 사이(길면 몇 분) 결과 창이 이 장을 고쳐 저장했을 수 있다 — 물을 때 읽어 둔 것에 적용해 저장하면 그 수정(글·자리·크기·숨김)이 통째로 사라진다(검토 2026-11-28).
                // 지금의 식자 정보를 다시 읽어 그 위에 적용하고, 그사이 달라진 줄(다른 원문이 된 번호·고친 글)은 건드리지 않는다(글이 물었을 때와 같은지는 Apply가 본다)
                var now = LetterSidecar.Load(outPath) ?? throw new IOException(Loc.T("식자 정보를 다시 읽지 못했다냥"));
                var still = group.Where(c => c.Index < now.Blocks.Count && c.Index < asked.Blocks.Count
                    && now.Blocks[c.Index].SourceText == asked.Blocks[c.Index].SourceText && !Locked(now.Blocks[c.Index]));
                var applied = Apply(now.Blocks, still, out int n);
                if (n == 0) continue;
                (now with { Blocks = applied }).SaveAndRender(outPath, s);
                changed += n;
            }
            catch (Exception ex) { failures.Add(Path.GetFileName(outPath) + ": " + ex.Message); }
        }
        return new Report(pages.Count, lines.Count, open, changed, outcome.Rejected, outcome.TooMany, outcome.Unread, skipped, failures, stopped, unreadable);
    }
}
