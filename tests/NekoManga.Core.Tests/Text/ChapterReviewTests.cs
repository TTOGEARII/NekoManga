using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>화 전체 검수(2026-11-28 — 사용자의 Carrot 규칙 B1 "넓은 문맥으로 번역하고, 전체 판독 뒤 일관성을 확인한다"): 쪽마다 따로 번역한 한 화의 원문·번역을 통째로 번역기에 보내
/// 이름·용어의 표기와 말투가 흔들린 줄만 고친다. 여기서는 물음을 짓고, 답에서 쓸 것만 고르고, 식자 정보에 적용하는 것을 본다(모델은 가짜 — 실제 답의 품질은 notes/translate-letter.md).
/// 지키는 것: 사용자가 고친 줄은 건드리지 않는다 · 고친 줄에는 표시와 전의 글이 남는다 · 모델이 거의 다 고치려 들면 하나도 쓰지 않는다.</summary>
public class ChapterReviewTests
{
    private static TextBlockInfo B(int x, int y, string src, string tr, string? machine = null) =>
        new(x, y, x + 100, y + 100, src, tr, 20, false, "#000000", Machine: machine ?? tr);

    private static ChapterReview.Line L(int page, int index, string src, string tr, bool locked = false) => new(page, index, src, tr, locked);

    /// <summary>부를 때마다 다음 답을 내는 가짜 모델 — 받은 물음을 적어 둔다.</summary>
    private sealed class Model(params string[] answers) : IChatModel
    {
        public readonly List<(string System, string User)> Asked = [];
        /// <summary>답하기 직전에 부른다 — 답을 기다리는 사이에 사용자가 한 일을 흉내 낸다.</summary>
        public Action? WhileWaiting;
        public Task<string> AskAsync(string system, string user, int maxTokens, CancellationToken ct)
        {
            Asked.Add((system, user));
            WhileWaiting?.Invoke();
            return Task.FromResult(answers[Math.Min(Asked.Count, answers.Length) - 1]);
        }
    }

    private static AppSettings Settings() => new() { LetterFontFamily = "Arial", TranslateSource = "ja", TranslateTarget = "ko" };

    // ───────── 줄 모으기 ─────────

    [Fact]
    public void LinesOf_ListsTheDialogue_PageByPage_InReadingOrder()
    {
        // 일본 만화: 같은 높이면 오른쪽 풍선이 먼저다. 효과음·숨긴 블록·[새 글 넣기]로 넣은 글(원문 없음)·번역이 빈 블록은 보내지 않는다
        var first = new List<TextBlockInfo>
        {
            B(100, 0, "左", "왼쪽"), B(600, 10, "右", "오른쪽"),
            B(300, 300, "ドン", "쾅") with { Sfx = true }, B(300, 450, "隠", "숨김") with { Hidden = true },
            B(300, 500, "", "넣은 글") with { Added = true }, B(300, 600, "下", "아래"), B(300, 800, "空", " "),
        };
        var second = new List<TextBlockInfo> { B(0, 0, "次", "다음") };

        var lines = ChapterReview.LinesOf([first, second], rightToLeft: true);

        Assert.Equal([(0, 1, "右"), (0, 0, "左"), (0, 5, "下"), (1, 0, "次")], lines.Select(l => (l.Page, l.Index, l.Source)));
        Assert.Equal([(0, 0, "左"), (0, 1, "右"), (0, 5, "下"), (1, 0, "次")],
            ChapterReview.LinesOf([first, second], rightToLeft: false).Select(l => (l.Page, l.Index, l.Source)));
    }

    [Fact]
    public void LinesOf_LocksWhatTheUserTouched_AndWhatItCannotTell()
    {
        // 사용자가 고친 글·직접 나눈 줄은 고치지 않는다(기준으로만 보낸다). 번역기의 글을 모르는 옛 결과의 줄도 잠근다 — 고친 글인지 알 수 없다
        var blocks = new List<TextBlockInfo>
        {
            B(0, 0, "一", "하나"),
            B(0, 200, "二", "둘이야", machine: "둘"),
            B(0, 400, "三", "셋") with { ManualLines = true },
            B(0, 600, "四", "넷", machine: ""),
        };

        Assert.Equal([false, true, true, true], ChapterReview.LinesOf([blocks], rightToLeft: true).Select(l => l.Locked));
    }

    // ───────── 물음 ─────────

    [Fact]
    public void BuildUserText_NumbersTheLines_UnderPageHeads_AndStarsLockedOnes()
    {
        var text = ChapterReview.BuildUserText([L(0, 1, "右\nだ", "오른쪽\n이야"), L(0, 0, "左", "왼쪽", locked: true), L(2, 0, "次", "다음")]);

        Assert.Equal("# page 1\n[1] 右 だ ⇒ 오른쪽 이야\n[2]* 左 ⇒ 왼쪽\n# page 3\n[3] 次 ⇒ 다음", text);
    }

    [Fact]
    public void BuildSystemPrompt_AsksOnlyForChangedLines_AndCarriesTheGuideAndGlossary()
    {
        var guide = new TranslateGuide("Drop", "Natural", "학원물이다", [("유이", "반말, 발랄하다")]);

        var p = ChapterReview.BuildSystemPrompt("Japanese", "Korean", guide, [("結衣", "유이")]);

        Assert.Contains("Japanese", p);
        Assert.Contains("Korean", p);
        Assert.Contains("NONE", p);                 // 고칠 것이 없을 때의 답
        Assert.Contains("[n]", p);                  // 고친 줄만 번호와 함께
        Assert.Contains("結衣 => 유이", p);
        Assert.Contains("유이: 반말, 발랄하다", p);
        Assert.Contains("학원물이다", p);
        Assert.Contains("bare name", p);            // 경칭 지침(Drop)이 번역 때와 같다
        Assert.DoesNotContain("bare name", ChapterReview.BuildSystemPrompt("Japanese", "Korean", TranslateGuide.Default, []));
    }

    // ───────── 답 고르기 ─────────

    [Fact]
    public void Pick_TakesOnlyTheChangesItCanTrust()
    {
        var lines = new[]
        {
            L(0, 0, "田中さん", "다나카 씨"), L(0, 1, "田中くん", "타나카 군"), L(0, 2, "そうだね", "그렇네", locked: true),
            L(1, 0, "行こう", "가자"), L(1, 1, "はい", "네"),
        };
        var answer = string.Join("\n",
            "[2] 다나카 군",                       // 쓴다
            "[3] 그렇군요",                        // 잠긴 줄 — 버린다
            "[4] 가자",                            // 같은 글 — 바뀐 것이 아니다
            "[5] はい！",                          // 원문 글자가 남았다 — 버린다
            "[9] 없는 줄",                         // 없는 번호
            "[1] 田中さん ⇒ 다나카 씨！",          // 원문까지 따라 적은 답 — 화살표 뒤만, 전각 느낌표는 반각으로
            "[2] 또 다른 답");                     // 같은 번호의 둘째 답 — 첫 답이 남는다

        var changes = ChapterReview.Pick(answer, lines, "ko", out int rejected);

        Assert.Equal([new ChapterReview.Change(0, 0, "다나카 씨", "다나카 씨!"), new ChapterReview.Change(0, 1, "타나카 군", "다나카 군")],
            changes.OrderBy(c => c.Index));
        Assert.Equal(2, rejected);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("")]
    [InlineData("고칠 곳이 없습니다.")]
    [InlineData("1. 고칠 곳이 없습니다")]      // 번역 요청은 "1. 글"도 받지만 여기서는 [n] 꼴만 — 말로 한 답이 첫 줄의 번역이 되면 안 된다
    [InlineData("**[1]** 가요")]               // 꾸민 번호도 받지 않는다(받지 못한 줄은 그대로 남는다)
    public void Pick_NothingToFix_IsNoChange(string answer)
    {
        Assert.Empty(ChapterReview.Pick(answer, [L(0, 0, "行こう", "가자")], "ko", out int rejected));
        Assert.Equal(0, rejected);
    }

    [Theory]
    [InlineData("[1] 어서 가요, 빨리 [2] 네에")]                    // 한 줄에 번호가 둘 — 어디까지가 첫 줄의 글인지 알 수 없다(통째로 첫 줄에 들어갔다)
    [InlineData("[1] 어서 가자, 빨리 → 어서 가요, 빨리")]           // 전 → 후를 같이 적은 답
    [InlineData("[1] 어서 가자, 빨리 -> 어서 가요, 빨리")]
    [InlineData("[1] 어서 가요, 빨리 (말투 통일)")]                 // 원문에 없는 괄호 = 붙인 말
    [InlineData("[1]과 [2]의 말투가 다릅니다")]                     // 번호로 시작하는 말
    [InlineData("[1]번 줄은 말투가 다릅니다")]                      // 번호에 말이 바로 붙었다 — 답의 줄이 아니다
    [InlineData("[1] 변경")]                                       // 전의 글에 견줘 터무니없이 짧다
    [InlineData("[1] 어서 가요, 빨리. 이 줄은 앞 장면의 말투에 맞춰 존댓말로 고쳤고 나머지 줄은 그대로 두었습니다")]   // 터무니없이 길다
    public void Pick_AnAnswerInAShapeItCannotTrust_IsNotApplied(string answer)
    {
        // 검증(목표 언어의 글자가 있다·원문 글자가 없다)만으로는 이런 답이 다 통과해 그대로 식자된다 — ↻ 표시는 달리지만 그림에 틀린 글이 들어간다(검토 2026-11-28)
        var lines = new[] { L(0, 0, "早く行こうよ", "어서 가자, 빨리"), L(0, 1, "はい", "네") };

        Assert.Empty(ChapterReview.Pick(answer, lines, "ko", out _));
    }

    [Theory]
    [InlineData("[1]: 어서 가요, 빨리")]
    [InlineData("[1] **어서 가요, 빨리**")]
    [InlineData("- [1] 어서 가요, 빨리")]
    [InlineData("```\n[1] 어서 가요, 빨리\n```")]
    [InlineData("[1] 어서 가요, 빨리\r")]
    public void Pick_HarmlessDressing_IsTakenOff(string answer)
    {
        var lines = new[] { L(0, 0, "早く行こうよ", "어서 가자, 빨리"), L(0, 1, "はい", "네") };

        Assert.Equal([new ChapterReview.Change(0, 0, "어서 가자, 빨리", "어서 가요, 빨리")], ChapterReview.Pick(answer, lines, "ko", out _));
    }

    // ───────── 적용 ─────────

    [Fact]
    public void Apply_LeavesTheMark_AndTheTextItReplaced()
    {
        var blocks = new List<TextBlockInfo> { B(0, 0, "田中くん", "타나카 군"), B(0, 200, "行こう", "가자") };

        var once = ChapterReview.Apply(blocks, [new(0, 0, "타나카 군", "다나카 군")], out int changed);

        Assert.Equal(1, changed);
        Assert.Equal(("다나카 군", "다나카 군", "타나카 군", ReviewMark.Revised), (once[0].Translation, once[0].Machine, once[0].Prior, once[0].Review));
        Assert.True(once[0].NeedsReview());     // 결과 창에 표시가 뜬다
        Assert.False(once[0].IsEdited());       // 사용자가 고친 글은 아니다 — 다시 돌리면 새로 번역한다
        Assert.Equal(blocks[1], once[1]);

        // 한 번 더 고쳐도 '전의 글'은 맨 처음 번역기의 글이다
        var twice = ChapterReview.Apply(once, [new(0, 0, "다나카 군", "다나카")], out _);
        Assert.Equal(("다나카", "타나카 군"), (twice[0].Translation, twice[0].Prior));
    }

    [Fact]
    public void Apply_SkipsALineThatIsNoLongerWhatWasAsked()
    {
        // 묻는 사이에 그 줄이 바뀌었거나(결과 창에서 고쳤다) 번호가 없는 줄 — 건드리지 않는다
        var blocks = new List<TextBlockInfo> { B(0, 0, "田中くん", "내가 고친 글", machine: "타나카 군") };

        var after = ChapterReview.Apply(blocks, [new(0, 0, "타나카 군", "다나카 군"), new(0, 7, "가", "나")], out int changed);

        Assert.Equal(0, changed);
        Assert.Equal(blocks, after);
    }

    [Fact]
    public void ARevisedLine_IsNamedInTheSheet_AndIsNotCountedAsAFailure()
    {
        var revised = ChapterReview.Apply([B(0, 0, "田中くん", "타나카 군")], [new(0, 0, "타나카 군", "다나카 군")], out _);

        var csv = ReviewSheet.Write([("p.png", revised)]);

        Assert.EndsWith(",\"검수\"", csv.TrimEnd().Split('\n')[1].TrimEnd());
        Assert.Null(PipelineRunner.ReviewWarning(revised));   // 장의 경고는 메운 줄·못 옮긴 줄만 센다
    }

    // ───────── 모델에 묻기 ─────────

    [Fact]
    public async Task RunAsync_AsksOnceForAChapter_AndReturnsTheChanges()
    {
        var lines = new[] { L(0, 0, "田中さん", "다나카 씨"), L(0, 1, "そうだね", "그렇네", locked: true), L(1, 0, "田中くん", "타나카 군") };
        var model = new Model("[3] 다나카 군");

        var outcome = await ChapterReview.RunAsync(model, lines, Settings(), CancellationToken.None);

        var asked = Assert.Single(model.Asked);
        Assert.Equal(ChapterReview.BuildUserText(lines), asked.User);
        Assert.Equal([new ChapterReview.Change(1, 0, "타나카 군", "다나카 군")], outcome.Changes);
        Assert.Equal((2, 0, false), (outcome.Asked, outcome.Rejected, outcome.TooMany));   // 물은 줄 = 잠기지 않은 줄
    }

    [Fact]
    public async Task RunAsync_ALongChapter_IsAskedInGroupsOfWholePages()
    {
        // 줄 수가 한 번에 묻는 상한을 넘으면 쪽 경계에서 나눈다 — 번호는 묶음마다 1부터, 바꿀 줄은 화 전체의 쪽·블록 번호로 돌아온다
        int per = ChapterReview.MaxLinesPerRequest * 3 / 8;   // 세 쪽이면 상한을 넘는다(둘 + 하나)
        var lines = Enumerable.Range(0, 3).SelectMany(p => Enumerable.Range(0, per).Select(i => L(p, i, "行こう", "가자"))).ToArray();
        var model = new Model("[1] 가요", "[2] 갑시다");

        var outcome = await ChapterReview.RunAsync(model, lines, Settings(), CancellationToken.None);

        Assert.Equal(2, model.Asked.Count);
        Assert.StartsWith("# page 1\n[1] ", model.Asked[0].User);
        Assert.Contains($"\n# page 2\n[{per + 1}] ", model.Asked[0].User);
        Assert.StartsWith("# page 3\n[1] ", model.Asked[1].User);
        Assert.Equal([new ChapterReview.Change(0, 0, "가자", "가요"), new ChapterReview.Change(2, 1, "가자", "갑시다")], outcome.Changes);
        Assert.Equal(per * 3, outcome.Asked);
    }

    [Fact]
    public async Task RunAsync_AModelThatRewritesMostLines_IsNotUsed()
    {
        // 일관성만 고치라는 물음에 줄의 대부분을 다시 써 왔다 — 다시 번역한 것이다. 하나도 쓰지 않고 그렇게 알린다
        var lines = Enumerable.Range(0, 20).Select(i => L(0, i, "行こう", "가자")).ToArray();
        const int allowed = 7;   // 스무 줄의 35%
        string Answer(int n) => string.Join("\n", Enumerable.Range(1, n).Select(k => $"[{k}] 가요"));

        var tooMany = await ChapterReview.RunAsync(new Model(Answer(allowed + 1)), lines, Settings(), CancellationToken.None);
        var fine = await ChapterReview.RunAsync(new Model(Answer(allowed)), lines, Settings(), CancellationToken.None);

        Assert.True(tooMany.TooMany);
        Assert.Empty(tooMany.Changes);
        Assert.False(fine.TooMany);
        Assert.Equal(allowed, fine.Changes.Count);

        // 고치려 든 줄을 센다 — 쓸 수 없어 버린 답도 다시 쓰려던 것이다(검토 2026-11-28: 스무 줄을 다 다시 썼는데 열다섯이 원문 글자가 남아 버려지면 남은 다섯이 적용됐다)
        var mostlyBad = string.Join("\n", Enumerable.Range(1, 20).Select(k => k <= 5 ? $"[{k}] 가요" : $"[{k}] 行こう"));
        var rewritten = await ChapterReview.RunAsync(new Model(mostlyBad), lines, Settings(), CancellationToken.None);
        Assert.True(rewritten.TooMany);
        Assert.Empty(rewritten.Changes);

        // 줄이 적은 화에서는 비율이 뜻이 없다(네 줄 가운데 넷) — 몇 줄까지는 받는다
        var small = await ChapterReview.RunAsync(new Model(Answer(4)), lines.Take(4).ToArray(), Settings(), CancellationToken.None);
        Assert.False(small.TooMany);
        Assert.Equal(4, small.Changes.Count);
    }

    [Theory]
    [InlineData("NONE", false)]
    [InlineData("none.", false)]
    [InlineData("[1] 가요", false)]
    [InlineData("<think>볼 것이 없다</think>\nNONE", false)]
    [InlineData("", true)]                       // 빈 답(추론만 하다 끝난 모델)
    [InlineData("고칠 곳이 없습니다.", true)]     // 말로 한 답 — 읽지 못한 것이다
    [InlineData("[7] 없는 번호", true)]
    public async Task RunAsync_AnAnswerItCannotRead_IsToldApartFromNothingToFix(string answer, bool unread)
    {
        // '고칠 줄이 없다'와 '답을 못 읽었다'는 다르다 — 못 읽은 것을 없다고 알리면 검수가 된 것처럼 보인다
        var outcome = await ChapterReview.RunAsync(new Model(answer), [L(0, 0, "行こう", "가자")], Settings(), CancellationToken.None);

        Assert.Equal(unread, outcome.Unread);
    }

    [Fact]
    public async Task RunAsync_NothingOpenToChange_DoesNotAsk()
    {
        var model = new Model("[1] 가요");

        var outcome = await ChapterReview.RunAsync(model, [L(0, 0, "行こう", "가자", locked: true)], Settings(), CancellationToken.None);

        Assert.Empty(model.Asked);
        Assert.Equal((0, 0), (outcome.Changes.Count, outcome.Asked));
    }

    // ───────── 결과에 적용 ─────────

    /// <summary>식자까지 끝난 장 하나를 임시 폴더에 만든다: 흰 원고(식자 전) + 식자 정보 + 식자한 결과물.</summary>
    private static string MakePage(TempDir t, string name, AppSettings s, string target, params TextBlockInfo[] blocks)
    {
        var outPath = t.At(name + ".png");
        var basePath = LetterSidecar.BasePathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        using (var white = new Mat(800, 600, MatType.CV_8UC3, Scalar.All(255))) ImgIo.Write(basePath, white);
        new LetterSidecar(t.At(name + "-src.png"), 600, 800, basePath, blocks.ToList(), Target: target).SaveAndRender(outPath, s);
        return outPath;
    }

    [Fact]
    public async Task RunOnResults_RewritesTheChangedPages_AndLeavesTheRest()
    {
        using var t = new TempDir();
        var s = Settings();
        var one = MakePage(t, "p1", s, "ko", B(100, 100, "田中さん", "다나카 씨"));
        var two = MakePage(t, "p2", s, "ko", B(100, 100, "田中くん", "타나카 군"), B(100, 400, "行こう", "가자"));
        var other = MakePage(t, "p3", s, "en", B(100, 100, "田中", "Tanaka"));   // 다른 언어로 번역한 장 — 섞어 보내지 않는다
        var before = new[] { one, two, other }.Select(File.ReadAllBytes).ToArray();
        var model = new Model("[2] 다나카 군");
        var said = new List<string>();

        var report = await ChapterReview.RunOnResultsAsync(model, [one, two, other, t.At("없는 장.png")], s, new Progress(said), CancellationToken.None);

        Assert.Equal((2, 3, 1, 1), (report.Pages, report.Lines, report.Changed, report.Skipped));
        Assert.Empty(report.Failures);
        Assert.DoesNotContain("Tanaka", Assert.Single(model.Asked).User);
        var saved = LetterSidecar.Load(two)!.Blocks;
        Assert.Equal(("다나카 군", "타나카 군", ReviewMark.Revised), (saved[0].Translation, saved[0].Prior, saved[0].Review));
        Assert.Equal("가자", saved[1].Translation);
        Assert.NotEqual(before[1], File.ReadAllBytes(two));     // 고친 장은 다시 식자됐다
        Assert.Equal(before[0], File.ReadAllBytes(one));        // 안 고친 장은 그대로
        Assert.Equal(before[2], File.ReadAllBytes(other));
        Assert.NotEmpty(said);
    }

    [Fact]
    public async Task RunOnResults_APageThatCannotBeWritten_IsReported_AndTheOthersStillChange()
    {
        using var t = new TempDir();
        var s = Settings();
        var one = MakePage(t, "p1", s, "ko", B(100, 100, "田中さん", "타나카 씨"));
        var two = MakePage(t, "p2", s, "ko", B(100, 100, "田中くん", "타나카 군"));
        var model = new Model("[1] 다나카 씨\n[2] 다나카 군");

        ChapterReview.Report report;
        using (new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.None))   // 다른 프로그램이 결과물을 쥐고 있다
            report = await ChapterReview.RunOnResultsAsync(model, [one, two], s, null, CancellationToken.None);

        Assert.Equal(1, report.Changed);
        Assert.Contains("p1.png", Assert.Single(report.Failures));
        Assert.Equal("다나카 군", LetterSidecar.Load(two)!.Blocks[0].Translation);
        // 결과물을 못 바꾼 장은 식자 정보도 전의 것이어야 한다 — 식자 정보만 새 글이면 결과 창의 글과 그림이 어긋난 채 남는다(검토 2026-11-28)
        var left = LetterSidecar.Load(one)!.Blocks[0];
        Assert.Equal(("타나카 씨", "", ""), (left.Translation, left.Review, left.Prior));
    }

    [Fact]
    public async Task RunOnResults_WhatTheUserChangedWhileWaiting_IsKept()
    {
        // 답을 기다리는 사이(길면 몇 분) 결과 창에서 그 장을 고쳐 저장했다: 글 하나를 고치고 다른 풍선을 옮겼다.
        // 물을 때 읽어 둔 식자 정보에 적용해 저장하면 그 수정이 통째로 사라진다(검토 2026-11-28) — 적용 직전에 다시 읽고, 그사이 고친 줄은 건드리지 않는다
        using var t = new TempDir();
        var s = Settings();
        var one = MakePage(t, "p1", s, "ko", B(100, 100, "田中さん", "다나카 씨"));
        var two = MakePage(t, "p2", s, "ko", B(100, 100, "田中くん", "타나카 군"), B(100, 250, "行こう", "가자"), B(100, 400, "待って", "기다려"), B(100, 550, "早く", "빨리"));
        var model = new Model("[2] 다나카 군\n[3] 갑시다\n[4] 기다려요\n[5] 빨리요")
        {
            WhileWaiting = () =>
            {
                var sc = LetterSidecar.Load(two)!;
                var edited = sc.Blocks.ToList();
                edited[0] = edited[0] with { Translation = "내가 고친 글" };          // 글을 고쳤다
                edited[1] = edited[1] with { X1 = 300, X2 = 400 };                    // 풍선을 옮겼다(글은 그대로)
                edited[2] = edited[2] with { ManualLines = true };                    // 줄을 직접 나누기로 했다(글은 그대로)
                edited[3] = edited[3] with { SourceText = "遅い" };                   // 그 번호가 다른 글이 됐다(번역은 우연히 같다)
                (sc with { Blocks = edited }).Save(two);
            },
        };

        var report = await ChapterReview.RunOnResultsAsync(model, [one, two], s, null, CancellationToken.None);

        var saved = LetterSidecar.Load(two)!.Blocks;
        Assert.Equal(("내가 고친 글", ""), (saved[0].Translation, saved[0].Review));          // 고친 글은 그대로
        Assert.Equal(("갑시다", ReviewMark.Revised, 300), (saved[1].Translation, saved[1].Review, saved[1].X1));   // 다른 줄의 고침은 옮긴 자리 위에 적용
        Assert.Equal(("기다려", true), (saved[2].Translation, saved[2].ManualLines));         // 그사이 잠긴 줄
        Assert.Equal(("빨리", "遅い"), (saved[3].Translation, saved[3].SourceText));          // 물은 줄이 아니다
        Assert.Equal(1, report.Changed);
    }

    [Fact]
    public async Task RunOnResults_TheSameResultListedTwice_IsReadOnce()
    {
        // 목록의 두 행이 같은 결과 자리로 떨어질 수 있다(출력 폴더 고정 + 같은 이름) — 두 번 읽으면 줄이 두 번 가고, 둘째 저장이 첫째의 고침을 덮는다
        using var t = new TempDir();
        var s = Settings();
        var one = MakePage(t, "p1", s, "ko", B(100, 100, "田中くん", "타나카 군"));

        var report = await ChapterReview.RunOnResultsAsync(new Model("NONE"), [one, one.ToUpperInvariant()], s, null, CancellationToken.None);

        Assert.Equal((1, 1), (report.Pages, report.Lines));
    }

    private sealed class Progress(List<string> said) : IProgress<string>
    {
        public void Report(string value) => said.Add(value);
    }
}
