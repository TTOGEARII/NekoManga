using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;

namespace NekoManga.Core.Tests;

/// <summary>사용자가 고친 번역은 다시 돌려도 지킨다(2026-11-27 — 사용자의 Carrot 규칙 B1 "사용자가 확정한 문자열은 다시 번역하지 않는다").
/// 전에는 [작업 시작]으로 같은 원고를 다시 돌리면 결과 창·검수 표로 고친 글이 새 기계 번역으로 덮였다.</summary>
public class CarryEditsTests
{
    private static TextBlockInfo B(int x, int y, string src, string tr, string machine = "", bool sfx = false, bool manualLines = false, bool added = false) =>
        new(x, y, x + 100, y + 200, src, tr, 20, true, "#000000", Sfx: sfx, ManualLines: manualLines, Added: added, Machine: machine);

    [Fact]
    public void AnEditedLine_IsKept_AndStaysEditedForTheNextRun()
    {
        var old = new[] { B(100, 100, "するよ", "내가 할게", machine: "할 거야"), B(400, 100, "私たち", "우리들", machine: "우리들") };
        var fresh = new[] { B(102, 98, "するよ", "한다", machine: "한다"), B(400, 100, "私たち", "우리", machine: "우리") };

        var r = TextBlockInfo.CarryEdits(fresh, old, out int kept);

        Assert.Equal(1, kept);
        Assert.Equal("내가 할게", r[0].Translation);
        Assert.Equal("한다", r[0].Machine);           // 번역기가 이번에 낸 글 — 다음에 돌려도 '고친 글'로 남는다
        Assert.True(r[0].IsEdited());
        Assert.Equal("우리", r[1].Translation);        // 고치지 않은 줄은 새 번역
    }

    [Fact]
    public void TheUsersLineBreaks_ComeAlong()
    {
        var old = new[] { B(100, 100, "するよ", "내가\n할게", machine: "내가 할게", manualLines: true) };
        var r = TextBlockInfo.CarryEdits([B(100, 100, "するよ", "한다", "한다")], old, out int kept);
        Assert.Equal(1, kept);
        Assert.Equal("내가\n할게", r[0].Translation);
        Assert.True(r[0].ManualLines);
    }

    [Fact]
    public void AnOldSidecarWithoutMachineText_KeepsNothing()
    {
        // 옛 json은 번역기의 글을 모른다 — 고쳤는지 알 수 없으니 전처럼 새 번역으로 간다
        var old = new[] { B(100, 100, "するよ", "내가 할게") };
        var r = TextBlockInfo.CarryEdits([B(100, 100, "するよ", "한다", "한다")], old, out int kept);
        Assert.Equal(0, kept);
        Assert.Equal("한다", r[0].Translation);
    }

    [Fact]
    public void TheSameSourceTwiceOnAPage_GoesToTheBlockInTheSamePlace_AndEachOldLineIsUsedOnce()
    {
        // 한 쪽에 같은 말이 둘 — 자리가 맞는 것끼리만 옮긴다(위의 것을 고쳤는데 아래 것이 바뀌면 안 된다)
        var old = new[] { B(100, 100, "だめ", "안 된다니까", machine: "안 돼"), B(100, 900, "だめ", "안 돼", machine: "안 돼") };
        var fresh = new[] { B(100, 900, "だめ", "안돼", "안돼"), B(100, 100, "だめ", "안돼", "안돼") };

        var r = TextBlockInfo.CarryEdits(fresh, old, out int kept);

        Assert.Equal(1, kept);
        Assert.Equal("안돼", r[0].Translation);
        Assert.Equal("안 된다니까", r[1].Translation);
    }

    [Fact]
    public void OneOldLine_IsNeverGivenToTwoNewBlocks()
    {
        // 다시 감지하며 한 풍선이 겹친 두 블록으로 갈린 드문 경우 — 고친 글 하나가 두 풍선에 똑같이 들어가면 안 된다(더 겹치는 쪽 하나만)
        var old = new[] { B(100, 100, "だめ", "안 된다니까", machine: "안 돼") };
        var fresh = new[] { B(100, 100, "だめ", "안돼", "안돼"), B(110, 105, "だめ", "안돼", "안돼") };

        var r = TextBlockInfo.CarryEdits(fresh, old, out int kept);

        Assert.Equal(1, kept);
        Assert.Equal(["안 된다니까", "안돼"], r.Select(b => b.Translation));
    }

    [Fact]
    public void AMovedBox_StillMatches_WhenTheSourceIsUniqueOnThePage()
    {
        // 결과 창에서 상자를 옮겨 놓은 블록 — 자리는 안 겹쳐도 원문이 그 쪽에 하나뿐이면 같은 풍선이다
        var old = new[] { B(700, 700, "するよ", "내가 할게", machine: "할 거야") };
        var r = TextBlockInfo.CarryEdits([B(100, 100, "するよ", "한다", "한다")], old, out int kept);
        Assert.Equal(1, kept);
        Assert.Equal("내가 할게", r[0].Translation);
    }

    [Fact]
    public void ADifferentReadingOfTheSameBubble_IsKeptOnlyWhenTheBoxesNearlyCoincide()
    {
        // 글자 읽기가 달라졌어도(엔진을 바꿨다) 같은 자리의 풍선이면 고친 글을 지킨다 — 어설프게 겹친 다른 풍선에는 옮기지 않는다
        var old = new[] { B(100, 100, "するよっ", "내가 할게", machine: "할 거야") };
        var same = TextBlockInfo.CarryEdits([B(103, 102, "するよ", "한다", "한다")], old, out int kept1);
        var other = TextBlockInfo.CarryEdits([B(150, 180, "しないよ", "안 해", "안 해")], old, out int kept2);
        Assert.Equal((1, "내가 할게"), (kept1, same[0].Translation));
        Assert.Equal((0, "안 해"), (kept2, other[0].Translation));
    }

    [Fact]
    public void SfxAndDialogue_AreNotMixed_AndTextTheUserAddedIsNotASource()
    {
        var old = new[] { B(100, 100, "ドン", "쾅!!", machine: "쿵", sfx: true), B(400, 400, "", "내가 넣은 글", machine: "", added: true) };
        var r = TextBlockInfo.CarryEdits([B(100, 100, "ドン", "쿵", "쿵")], old, out int kept);
        Assert.Equal(0, kept);
        Assert.Equal("쿵", r[0].Translation);
    }

    [Fact]
    public void AnUnrelatedTextInTheSameBox_IsNotKept()
    {
        // 같은 자리라도 전혀 다른 글이면 같은 풍선이 아니다(같은 이름의 원고를 다른 그림으로 바꿔 놓았다) — 글자 읽기가 조금 달라진 것만 같은 풍선으로 본다
        var old = new[] { B(100, 100, "するよっ", "내가 할게", machine: "할 거야") };
        var r = TextBlockInfo.CarryEdits([B(100, 100, "だめだってば", "안 된다니까", "안 된다니까")], old, out int kept);
        Assert.Equal((0, "안 된다니까"), (kept, r[0].Translation));
    }

    // ── 러너: 전의 식자 정보에서 블록 꺼내기 ──

    private static (string Src, string OutPath) Saved(TempDir dir, string target = "ko", int srcBytes = 3, long srcLen = 3)
    {
        string src = dir.At("01.jpg"), outPath = dir.At("out", "01_jpg.png");
        File.WriteAllBytes(src, new byte[srcBytes]);
        new LetterSidecar(src, 100, 200, LetterSidecar.BasePathFor(outPath), [B(1, 1, "するよ", "내가 할게", "할 거야")], SrcLen: srcLen, Target: target).Save(outPath);
        return (src, outPath);
    }

    [Fact]
    public void PreviousBlocks_AreReadFromThisSourcesSidecar_Only()
    {
        using var dir = new TempDir();
        var (src, outPath) = Saved(dir);
        string other = dir.At("02.jpg");
        File.WriteAllBytes(other, [1, 2, 3, 4]);

        Assert.Single(PipelineRunner.PreviousBlocks(outPath, src, "ko", out bool unreadable)!);
        Assert.False(unreadable);
        Assert.Null(PipelineRunner.PreviousBlocks(outPath, other, "ko", out _));                       // 그 자리의 사이드카가 다른 원고의 것
        Assert.Null(PipelineRunner.PreviousBlocks(dir.At("out", "none.png"), src, "ko", out _));       // 처음 돌리는 장
    }

    [Fact]
    public void PreviousBlocks_ForAnotherTargetLanguage_AreNotKept()
    {
        // 한국어로 돌려 글을 고친 뒤 목표를 영어로 바꿔 다시 돌린다 — 고친 한국어 글이 영어 결과에 남으면 안 된다(검토 2026-11-27)
        using var dir = new TempDir();
        var (src, outPath) = Saved(dir, target: "ko");
        Assert.Null(PipelineRunner.PreviousBlocks(outPath, src, "en", out _));
        Assert.Single(PipelineRunner.PreviousBlocks(outPath, src, "KO", out _)!);
    }

    [Fact]
    public void PreviousBlocks_WhenTheSourceFileWasReplaced_AreNotKept()
    {
        // 같은 이름·같은 자리의 원고가 다른 그림으로 바뀌었다(길이가 다르다) — 옛 그림의 풍선에 고친 글을 새 그림에 옮기지 않는다
        using var dir = new TempDir();
        var (src, outPath) = Saved(dir, srcBytes: 5, srcLen: 3);
        Assert.Null(PipelineRunner.PreviousBlocks(outPath, src, "ko", out _));
    }

    [Fact]
    public void PreviousBlocks_ABrokenSidecar_IsReported_AndALockedOne_Throws()
    {
        using var dir = new TempDir();
        var (src, outPath) = Saved(dir);
        string json = LetterSidecar.JsonPathFor(outPath);
        // 잠긴 json(백신·동기화가 잠깐 쥠): 없는 것으로 치고 새 번역으로 가면 2차가 그 json을 새 블록으로 덮어 고친 글이 사라진다 — 그 장을 멈춘다(다시 돌리면 된다)
        using (new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => PipelineRunner.PreviousBlocks(outPath, src, "ko", out _));
        // 깨진 json: 읽을 것이 없다 — 멈추면 그 장은 영영 다시 못 돌린다. 알리고 새 번역으로 간다
        File.WriteAllText(json, "{ 깨진");
        Assert.Null(PipelineRunner.PreviousBlocks(outPath, src, "ko", out bool unreadable));
        Assert.True(unreadable);
    }
}
