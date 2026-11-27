using System.Text.Json;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>번역을 확인할 줄의 표시(2026-11-27 — 사용자의 Carrot 규칙 A4 "실패는 검사 가능한 실패로 남긴다"): 검증에 걸린 줄을 다른 번역기로 메웠거나 끝내 못 옮겼으면
/// 그 블록에 표시를 남긴다. 전에는 메운 줄도 못 옮긴 줄(원문이 그대로 식자된다)도 아무 흔적 없이 '완료'였다. 사용자가 그 글을 고치면 표시는 저절로 꺼진다.</summary>
public class TranslateReviewTests
{
    /// <summary>부를 때마다 다음 답을 내는 가짜 LLM(다시 물으면 답이 달라질 수 있다).</summary>
    private sealed class Llm(params Func<IReadOnlyList<string>, IReadOnlyList<string>>[] answers) : ITranslator
    {
        public int Calls;
        public bool AnswersVary => true;
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answers[Math.Min(Calls, answers.Length) - 1](texts));
        }
    }

    private sealed class Other(Func<IReadOnlyList<string>, IReadOnlyList<string>> answer) : ITranslator
    {
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
            => Task.FromResult(answer(texts));
    }

    private static Task<string[]> Checked(ITranslator tr, string[] texts, ITranslator fallback, string[] marks) =>
        EmbeddedTranslator.TranslateCheckedAsync(tr, texts, TranslateOptions.None, "ja", "ko", CancellationToken.None, fallback, marks);

    [Fact]
    public async Task ALineFilledByTheOtherTranslator_IsMarkedFilled()
    {
        // 둘째 줄은 두 번 물어도 가나가 남는다 → 다른 번역기(Google)의 답으로 메운다 — 그 줄만 표시한다
        var llm = new Llm(_ => ["우리들", "할 거야かな"]);
        var marks = new string[2];

        var r = await Checked(llm, ["私たち", "するよ"], new Other(t => t.Select(_ => "할게").ToList()), marks);

        Assert.Equal(["우리들", "할게"], r);
        Assert.Equal(["", ReviewMark.Filled], marks.Select(m => m ?? ""));
    }

    [Fact]
    public async Task ALineNobodyCouldTranslate_IsMarkedFailed()
    {
        // 다른 번역기도 못 옮겼다(빈 답) — 처음 답이 남고 '못 옮김'으로 표시한다
        var llm = new Llm(_ => ["우리들", "할 거야かな"]);
        var marks = new string[2];

        var r = await Checked(llm, ["私たち", "するよ"], new Other(t => t.Select(_ => "").ToList()), marks);

        Assert.Equal("할 거야かな", r[1]);
        Assert.Equal(["", ReviewMark.Failed], marks.Select(m => m ?? ""));
    }

    [Fact]
    public async Task ALineFixedByAskingTheSameTranslatorAgain_HasNoMark()
    {
        // 같은 번역기가 다시 낸 통과한 답은 표시하지 않는다 — 그 번역기의 번역이다
        var llm = new Llm(_ => ["우리들", "할 거야かな"], _ => ["우리는", "할 거야"]);
        var marks = new string[2];

        var r = await Checked(llm, ["私たち", "するよ"], new Other(t => t.Select(_ => "메움").ToList()), marks);

        Assert.Equal(["우리들", "할 거야"], r);
        Assert.All(marks, m => Assert.True(string.IsNullOrEmpty(m)));
    }

    [Fact]
    public async Task WithoutAMarkArray_TheAnswersAreTheSame()
    {
        // 표시를 받을 곳을 주지 않은 호출(효과음·시험)은 전과 같다
        var llm = new Llm(_ => ["우리들", "할 거야かな"]);
        var r = await EmbeddedTranslator.TranslateCheckedAsync(llm, ["私たち", "するよ"], TranslateOptions.None, "ja", "ko", CancellationToken.None,
            new Other(t => t.Select(_ => "할게").ToList()));
        Assert.Equal(["우리들", "할게"], r);
    }

    [Fact]
    public async Task InReadingOrder_TheMarksComeBackInTheBlockOrder()
    {
        // 번역기에는 읽는 차례(오른쪽 풍선 먼저)로 나간다 — 표시는 답과 같이 블록의 차례로 돌아와야 한다(어긋나면 멀쩡한 풍선에 ⚠가 붙는다)
        Rect[] boxes = [new(100, 100, 100, 300), new(600, 100, 100, 300)];   // 블록 0 = 왼쪽, 1 = 오른쪽 → 읽는 차례는 1, 0
        string[] texts = ["するよ", "私たち"];
        var llm = new Llm(t => t.Select(s => s == "するよ" ? "할 거야かな" : "우리들").ToList());
        var marks = new string[2];

        var r = await EmbeddedTranslator.TranslateInReadingOrderAsync(llm, texts, boxes, TranslateOptions.None, "ja", "ko", CancellationToken.None,
            new Other(t => t.Select(_ => "할게").ToList()), marks);

        Assert.Equal(["할게", "우리들"], r);
        Assert.Equal([ReviewMark.Filled, ""], marks.Select(m => m ?? ""));
    }

    // ── 블록 기록 ──

    private static TextBlockInfo Block(string tr, string machine = "", string review = "") =>
        new(10, 10, 50, 80, "原文", tr, 18, Vertical: true, FontColorHex: "#000000", Machine: machine, Review: review);

    [Fact]
    public void NeedsReview_OnlyWhileTheMachineTextIsUntouched()
    {
        // 표시는 번역기의 글 그대로일 때만 보인다 — 사용자가 글을 고치면 확인한 것이다(고치는 자리마다 표시를 끄지 않아도 된다)
        Assert.True(Block("할게", machine: "할게", review: ReviewMark.Filled).NeedsReview());
        Assert.False(Block("내가 할게", machine: "할게", review: ReviewMark.Filled).NeedsReview());
        Assert.False(Block("할게", machine: "할게").NeedsReview());
    }

    [Fact]
    public void IsEdited_WhenTheTextDiffersFromWhatTheTranslatorGave()
    {
        Assert.True(Block("내가 할게", machine: "할게").IsEdited());
        Assert.False(Block("할게", machine: "할게").IsEdited());
        // 줄을 직접 나눈 것도 고친 것이다(줄바꿈이 글에 박힌다)
        Assert.True(Block("내가\n할게", machine: "내가 할게").IsEdited());
    }

    [Fact]
    public void AnOldSidecarBlock_IsNeitherEditedNorMarked()
    {
        // 옛 json에는 번역기의 글이 없다 — 고쳤는지 알 수 없으니 고친 것으로 보지 않는다(다시 돌리면 전처럼 새 번역으로 바뀐다). 계산한 값이 json에 섞여 나가지도 않는다
        var json = """{"X1":1,"Y1":2,"X2":30,"Y2":40,"SourceText":"原文","Translation":"번역 글","FontSize":18,"Vertical":true,"FontColorHex":"#000000"}""";
        var b = JsonSerializer.Deserialize<TextBlockInfo>(json)!;
        Assert.Equal("", b.Machine);
        Assert.Equal("", b.Review);
        Assert.False(b.IsEdited());
        Assert.False(b.NeedsReview());
        var written = JsonSerializer.Serialize(Block("할게", "할게", ReviewMark.Filled));
        Assert.DoesNotContain("NeedsReview", written);
        Assert.DoesNotContain("IsEdited", written);
        Assert.Equal(ReviewMark.Filled, JsonSerializer.Deserialize<TextBlockInfo>(written)!.Review);
    }

    // ── 검수 표 ──

    [Fact]
    public void ReviewSheet_HasACheckColumn_ForLinesThatNeedReview()
    {
        const string outPath = @"C:\x\01.png";
        var pages = new List<(string OutPath, IReadOnlyList<TextBlockInfo> Blocks)>
        {
            (outPath, [Block("안녕", "안녕"), Block("할게", "할게", ReviewMark.Filled), Block("するよ", "するよ", ReviewMark.Failed), Block("내가 할게", "할게", ReviewMark.Filled)]),
        };

        var csv = ReviewSheet.Write(pages);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("결과 파일,번호,원문,번역,확인", lines[0]);
        Assert.EndsWith(",\"\"", lines[1]);
        Assert.EndsWith(",\"메움\"", lines[2]);
        Assert.EndsWith(",\"못 옮김\"", lines[3]);
        Assert.EndsWith(",\"\"", lines[4]);   // 사용자가 이미 고친 줄
        // 들여올 때는 넷째 칸까지만 읽는다 — 다섯째 칸이 있어도 줄은 같다
        var rows = ReviewSheet.Parse(csv);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new ReviewSheet.Row(outPath, 2, "原文", "할게"), rows[1]);
    }

    // ── 러너의 경고 ──

    [Fact]
    public void ReviewWarning_CountsFilledAndFailedLines_AndIsNullWhenNothingNeedsReview()
    {
        // 확인할 줄이 없으면(표시가 없거나 사용자가 이미 고쳤으면) 경고도 없다
        Assert.Null(PipelineRunner.ReviewWarning([Block("안녕", "안녕"), Block("내가 할게", "할게", ReviewMark.Filled)]));
        var w = PipelineRunner.ReviewWarning([Block("할게", "할게", ReviewMark.Filled), Block("するよ", "するよ", ReviewMark.Failed), Block("까", "까", ReviewMark.Failed)]);
        Assert.NotNull(w);
        Assert.Contains("3", w);   // 모두
        Assert.Contains("1", w);   // 메움
        Assert.Contains("2", w);   // 못 옮김
    }
}
