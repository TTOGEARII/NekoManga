using NekoManga.Core.Decensor;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>사용자가 검열 조각에 고른 성별의 기억 (2026-11-15, "앞으로 작업하는 모든 페이지가 남/여 구별되서 들어가야해" — 부위 감지가 못 잡는 조각은 직접 고른다).
/// 결과 창이 그 장의 마스크 사이드카에 적고(RegenSex), 2차가 재생성 직전에 읽어 영역 계획에 넘긴다. 시드의 기록·고정과 같은 자리·같은 규칙이다.
/// ComfyUI 없이 볼 수 있는 것만 본다 — 사이드카의 값, 읽어서 환산하기, 계획에 닿는지.</summary>
public class RegenSexMarkTests
{
    private const int W = 60, H = 40;

    private static (string Src, string OutPath) Slot(TempDir t, string? sex)
    {
        var s = EnginelessSettings(t);
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));
        var sc = MaskSidecar.Write(outPath, src, mask, null, null, "감지", skip: false, edited: false, confirmed: true);
        if (sex != null) (sc with { RegenSex = sex }).Save(outPath);
        return (src, outPath);
    }

    [Fact]
    public void Sidecar_SexMarks_RoundTrip_AndOldJsonReadsAsNone()
    {
        using var t = new TempDir();
        var (_, outPath) = Slot(t, "10,12,M;40,20,F");
        Assert.Equal("10,12,M;40,20,F", MaskSidecar.Load(outPath)!.RegenSex);

        // 이 칸이 없는 옛 사이드카도 그대로 읽힌다 — 표시 없음
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(MaskSidecar.JsonPathFor(outPath)))!.AsObject();
        Assert.True(json.Remove("RegenSex"));
        File.WriteAllText(MaskSidecar.JsonPathFor(outPath), json.ToJsonString());
        Assert.Null(MaskSidecar.Load(outPath)!.RegenSex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_OverTheSameSource_KeepsTheSexMarks(bool freshDetection)
    {
        // 마스크를 다시 쓰는 길(검토 확인·새 감지·표시를 넘기지 않는 저장)은 새 레코드를 만든다 — 사용자가 고른 성별이 따라 지워지면 다음 재생성이 말없이 자동 판단으로 돌아간다
        using var t = new TempDir();
        var (src, outPath) = Slot(t, "10,12,M");

        using var again = RectMask(W, H, new Rect(1, 1, 3, 3));
        var written = MaskSidecar.Write(outPath, src, again, null, null, "다시", skip: false, edited: true, confirmed: true, freshDetection: freshDetection);

        Assert.Equal("10,12,M", written.RegenSex);
        Assert.Equal(written, MaskSidecar.Load(outPath));
    }

    [Fact]
    public void Write_GivenMarks_ReplaceTheOldOnes_AndAnEmptyTextClearsThem()
    {
        // 결과 창의 저장은 지금 화면의 표시를 넘긴다: 바꾼 것은 바뀌고, 전부 '자동'으로 돌린 것("")은 지워진다
        using var t = new TempDir();
        var (src, outPath) = Slot(t, "10,12,M");
        using var mask = RectMask(W, H, new Rect(5, 5, 10, 10));

        var changed = MaskSidecar.Write(outPath, src, mask, null, null, "감지", skip: false, edited: false, confirmed: true, regenSex: "10,12,F;30,30,B");
        Assert.Equal("10,12,F;30,30,B", changed.RegenSex);
        Assert.Equal("10,12,F;30,30,B", MaskSidecar.Load(outPath)!.RegenSex);

        var cleared = MaskSidecar.Write(outPath, src, mask, null, null, "감지", skip: false, edited: false, confirmed: true, regenSex: "");
        Assert.Null(cleared.RegenSex);
        Assert.Null(MaskSidecar.Load(outPath)!.RegenSex);
    }

    [Fact]
    public void Write_ForAnotherSourceInTheSameSlot_DropsTheSexMarks()
    {
        using var t = new TempDir();
        var (_, outPath) = Slot(t, "10,12,M");
        var other = WriteImageFile(t.At("vol2", "01.jpg"), seed: 2, width: W + 4);
        using var mask = RectMask(W + 4, H, new Rect(5, 5, 10, 10));

        var written = MaskSidecar.Write(outPath, other, mask, null, null, "감지", skip: false, edited: false, confirmed: true);

        Assert.Null(written.RegenSex);   // 다른 원고의 조각에 남의 표시를 얹지 않는다
    }

    [Fact]
    public void SexMarksFor_ReadsThatSourcesMarks_AtTheSizeBeingDrawn()
    {
        using var t = new TempDir();
        var (src, outPath) = Slot(t, "10,12,M;40,20,F");

        Assert.Equal([new SexMark(new Point(10, 12), RegenKind.Male), new SexMark(new Point(40, 20), RegenKind.Female)],
            PipelineRunner.SexMarksFor(outPath, src, new Size(W, H)));
        // 채색된 장의 2차는 더 큰 그림에서 돈다 — 표시는 마스크(원본) 좌표로 적혀 있어 그 그림의 좌표로 옮긴다
        Assert.Equal([new SexMark(new Point(20, 36), RegenKind.Male), new SexMark(new Point(80, 60), RegenKind.Female)],
            PipelineRunner.SexMarksFor(outPath, src, new Size(W * 2, H * 3)));

        var other = WriteImageFile(t.At("vol2", "01.jpg"), seed: 2, width: W + 4);
        Assert.Empty(PipelineRunner.SexMarksFor(outPath, other, new Size(W, H)));                    // 그 자리의 사이드카가 다른 원고 것
        Assert.Empty(PipelineRunner.SexMarksFor(t.At("vol1", "none.png"), src, new Size(W, H)));     // 사이드카가 없는 장

        (MaskSidecar.Load(outPath)! with { RegenSex = null }).Save(outPath);
        Assert.Empty(PipelineRunner.SexMarksFor(outPath, src, new Size(W, H)));                      // 표시가 없는 장
    }

    [Fact]
    public void SexMarksFor_JsonHeldThroughout_ThrowsInsteadOfPretendingThereAreNoMarks()
    {
        // 잠긴 json을 '표시 없음'으로 읽으면 사용자가 남성으로 고른 조각이 말없이 남녀 태그로 그려진다 — 고정한 시드와 같은 규칙(그 장의 실패)
        using var t = new TempDir();
        var (src, outPath) = Slot(t, "10,12,M");
        using var held = new FileStream(MaskSidecar.JsonPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<IOException>(() => PipelineRunner.SexMarksFor(outPath, src, new Size(W, H)));
        Assert.Contains("잠시 뒤", ex.Message);
    }

    [Fact]
    public void FinishedRecord_AndStaleCleared_LeaveTheSexMarksAlone()
    {
        var sc = new MaskSidecar("C:/x/01.jpg", W, H, "감지", Skip: false, Edited: true, Confirmed: true, new DateTime(2026, 11, 15, 10, 0, 0), SrcLen: 9,
            Pending: true, Colorized: null, RegenSeeds: "5", SeedLock: false, RegenSex: "10,12,M");
        Assert.Equal("10,12,M", PipelineRunner.FinishedRecord(sc, colorized: true, sc.SavedAt, [7]).RegenSex);
        Assert.Equal("10,12,M", PipelineRunner.StaleCleared(sc, colorized: true, [7]).RegenSex);
    }

    [Fact]
    public void RegenPlanFor_GivenMarks_DecidesTheTagsOfThatPiece()
    {
        // 부위 감지 모델 없이도(감지기 null) 사용자가 고른 조각은 그 성별로 간다 — 표시가 없으면 모르는 조각이라 남녀를 같이
        using var t = new TempDir();
        using var runner = new PipelineRunner(EnginelessSettings(t));
        using var img = ColorImage(400, 300, seed: 3);
        using var mask = RectMask(400, 300, new Rect(100, 100, 60, 20));

        var plain = runner.RegenPlanFor(img, mask, null);
        var marked = runner.RegenPlanFor(img, mask, null, marks: [new SexMark(new Point(120, 110), RegenKind.Male)]);
        try
        {
            Assert.Equal(RegenKind.Both, Assert.Single(plain).Sex);
            Assert.Equal(RegenKind.Male, Assert.Single(marked).Sex);
            Assert.Equal(Assert.Single(plain).Bounds, Assert.Single(marked).Bounds);
        }
        finally { foreach (var r in plain.Concat(marked)) r.Dispose(); }
    }
}
