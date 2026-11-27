using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>식자 걸음(2차의 끝 — <see cref="PipelineRunner.LetterAndSave"/>, 2026-11-28 속도): 식자 전 원고와 식자 정보를 남기고 번역 글을 그려 결과물을 쓴다.
/// 전에는 쪽마다 원본을 크기만 보려고 두 번 디코드하고(112ms씩), 글자 지운 그림을 한 번 더 읽고, 식자 전 원고를 다시 인코딩했다(162ms) — 실제 38쪽에 36초 가운데 절반.
/// 여기서는 결과가 전과 같은지를 본다: 결과물·식자 전 원고·식자 정보가 전의 걸음들이 만든 것과 같고, 못 믿는 경우(이 작업이 쓴 png가 아닌 작업 그림·크기를 모를 때)는 전과 같은 길로 간다.</summary>
public class LetterAndSaveTests
{
    private static AppSettings Settings() => new() { LetterFontFamily = "Arial", TranslateTarget = "ko" };

    private static List<TextBlockInfo> Blocks() =>
    [
        new(40, 40, 260, 160, "こんにちは", "HELLO THERE", 20, false, "#000000", Machine: "HELLO THERE"),
        new(300, 300, 520, 420, "さようなら", "GOODBYE", 20, false, "#000000", Machine: "GOODBYE"),
    ];

    /// <summary>작업 폴더에 '이 작업이 쓴 글자 지운 그림'을 만든다(흰 바탕에 회색 띠 — 그림이 통째로 한 색이면 자리가 어긋나도 모른다).</summary>
    private static string MakeWork(TempDir t, int w = 600, int h = 800)
    {
        var path = t.At(Path.Combine("job", "0000_p.inpainted.png"));
        using var img = new Mat(h, w, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(img, new Rect(0, h / 2, w, 20), Scalar.All(120), -1);
        ImgIo.Write(path, img);
        return path;
    }

    private static string MakeSource(TempDir t, int w = 600, int h = 800)
    {
        var path = t.At("p.png");
        using var img = new Mat(h, w, MatType.CV_8UC3, Scalar.All(200));
        ImgIo.Write(path, img);
        return path;
    }

    [Fact]
    public void LetterAndSave_MakesWhatTheOldStepsMade()
    {
        using var t = new TempDir();
        var s = Settings();
        string work = MakeWork(t), src = MakeSource(t), outPath = t.At(Path.Combine("out", "p.png"));
        var blocks = Blocks();

        // 전의 걸음: 식자 전 원고를 디코드해 다시 인코딩 → 식자 정보(원본을 읽어 크기를 적는다) → 식자(작업 그림과 원본을 다시 읽는다)
        var oldOut = t.At(Path.Combine("old", "p.png"));
        using (var baseImg = ImgIo.Read(work))
        using (var srcImg = ImgIo.Read(src))
        {
            ImgIo.Write(LetterSidecar.BasePathFor(oldOut), baseImg);
            new LetterSidecar(src, srcImg.Width, srcImg.Height, LetterSidecar.BasePathFor(oldOut), [.. blocks], SrcLen: MaskSidecar.LengthOf(src), CommonSize: 0, Target: s.TranslateTarget).Save(oldOut);
        }
        TextRenderer.RenderBlocks(work, src, 0, 0, blocks, oldOut, s, 0);

        PipelineRunner.LetterAndSave(work, src, blocks, outPath, s, commonSize: 0, srcW: 600, srcH: 800, workIsOurPng: true);

        Assert.Equal(File.ReadAllBytes(oldOut), File.ReadAllBytes(outPath));
        Assert.Equal(File.ReadAllBytes(LetterSidecar.BasePathFor(oldOut)), File.ReadAllBytes(LetterSidecar.BasePathFor(outPath)));
        var (was, now) = (LetterSidecar.Load(oldOut)!, LetterSidecar.Load(outPath)!);
        Assert.Equal((was.Source, was.SrcW, was.SrcH, was.SrcLen, was.Target, was.CommonSize), (now.Source, now.SrcW, now.SrcH, now.SrcLen, now.Target, now.CommonSize));
        Assert.Equal(was.Blocks, now.Blocks);
    }

    [Fact]
    public void LetterAndSave_WithTheSizeFromTheFirstPass_DoesNotOpenTheSource()
    {
        // 1차가 원본을 읽을 때 크기를 적어 뒀다 — 2차는 원본을 다시 열지 않는다(여기서는 그사이 원본이 없어졌다. 전에는 크기를 보려고 열다가 그 장이 실패했다)
        using var t = new TempDir();
        string work = MakeWork(t), gone = t.At("없는 원본.png"), outPath = t.At(Path.Combine("out", "p.png"));

        PipelineRunner.LetterAndSave(work, gone, Blocks(), outPath, Settings(), commonSize: 0, srcW: 1200, srcH: 1600, workIsOurPng: true);

        var sc = LetterSidecar.Load(outPath)!;
        Assert.Equal((1200, 1600), (sc.SrcW, sc.SrcH));
        // 블록 좌표는 원본(1200×1600) 기준이고 작업 그림은 그 절반이다 — 적어 둔 크기로 환산해 그린 것과 같아야 한다
        var expected = t.At("expected.png");
        TextRenderer.RenderBlocks(work, gone, 1200, 1600, Blocks(), expected, Settings(), 0);
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void LetterAndSave_WithoutTheSize_ReadsItFromTheSource_AsBefore()
    {
        // 다시 렌더링처럼 1차를 돌지 않은 길 — 전처럼 원본에서 크기를 읽는다
        using var t = new TempDir();
        string work = MakeWork(t), src = MakeSource(t, 1200, 1600), outPath = t.At(Path.Combine("out", "p.png"));

        PipelineRunner.LetterAndSave(work, src, Blocks(), outPath, Settings(), commonSize: 0, srcW: 0, srcH: 0, workIsOurPng: true);

        var sc = LetterSidecar.Load(outPath)!;
        Assert.Equal((1200, 1600), (sc.SrcW, sc.SrcH));
        var expected = t.At("expected.png");
        TextRenderer.RenderBlocks(work, src, 1200, 1600, Blocks(), expected, Settings(), 0);
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void LetterAndSave_AWorkImageThisJobDidNotWrite_IsReencodedForTheBase()
    {
        // 채색 결과(ComfyUI가 쓴 png — 워크플로가 통째로 박혀 있고 알파가 있을 수 있다)는 그대로 복사하지 않는다: 전처럼 읽어서 다시 쓴다
        using var t = new TempDir();
        var work = t.At(Path.Combine("job", "0000_p.colored.png"));
        using (var rgba = new Mat(800, 600, MatType.CV_8UC4, new Scalar(255, 255, 255, 128))) ImgIo.Write(work, rgba);
        string src = MakeSource(t), outPath = t.At(Path.Combine("out", "p.png"));

        PipelineRunner.LetterAndSave(work, src, Blocks(), outPath, Settings(), commonSize: 0, srcW: 600, srcH: 800, workIsOurPng: false);

        using var saved = ImgIo.Read(LetterSidecar.BasePathFor(outPath), ImreadModes.Unchanged);
        Assert.Equal(3, saved.Channels());
        Assert.NotEqual(File.ReadAllBytes(work), File.ReadAllBytes(LetterSidecar.BasePathFor(outPath)));
    }

    [Fact]
    public void LetterAndSave_AnUnreadableWorkImage_IsThatPagesFailure_AndLeavesNoSidecar()
    {
        using var t = new TempDir();
        var work = t.At(Path.Combine("job", "0000_p.inpainted.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        File.WriteAllBytes(work, [1, 2, 3]);   // 깨진 그림
        string src = MakeSource(t), outPath = t.At(Path.Combine("out", "p.png"));

        Assert.Throws<InvalidDataException>(() => PipelineRunner.LetterAndSave(work, src, Blocks(), outPath, Settings(), 0, 600, 800, workIsOurPng: true));

        Assert.False(File.Exists(LetterSidecar.JsonPathFor(outPath)));
        Assert.False(File.Exists(LetterSidecar.BasePathFor(outPath)));
        Assert.False(File.Exists(outPath));
    }

    [Theory]
    [InlineData("0003_p.inpainted.png", true)]      // 이 작업의 글자 지운 그림
    [InlineData("0003_p.INPAINTED.PNG", true)]
    [InlineData("0003_p.decensored.png", false)]    // 검열 제거 뒤의 그림 — 전과 같은 길로(검열 제거 쪽의 결과물을 다루는 길은 건드리지 않는다)
    [InlineData("0003_p.colored.png", false)]       // ComfyUI가 쓴 것
    [InlineData("0003_p.png", false)]
    public void IsOurWorkPng_OnlyTheErasedImageInTheJobFolder(string name, bool ours)
    {
        using var t = new TempDir();
        var job = t.At("job");

        Assert.Equal(ours, PipelineRunner.IsOurWorkPng(job, Path.Combine(job, name)));
        Assert.False(PipelineRunner.IsOurWorkPng(job, Path.Combine(t.At("다른 폴더"), name)));   // 같은 이름이라도 작업 폴더 밖이면 아니다(원고 이름이 그렇게 생겼을 수 있다)
    }

    [Fact]
    public void RenderBlocks_FromAnImageAlreadyRead_IsTheSameAsFromItsPath()
    {
        using var t = new TempDir();
        string work = MakeWork(t), src = MakeSource(t, 1200, 1600);
        string fromPath = t.At("a.png"), fromMat = t.At("b.png");

        TextRenderer.RenderBlocks(work, src, 0, 0, Blocks(), fromPath, Settings(), 21);
        using (var img = ImgIo.Read(work, ImreadModes.Color))
            TextRenderer.RenderBlocks(img, Path.GetFileName(work), src, 0, 0, Blocks(), fromMat, Settings(), 21);

        Assert.Equal(File.ReadAllBytes(fromPath), File.ReadAllBytes(fromMat));
        using var empty = new Mat();
        var ex = Assert.Throws<InvalidDataException>(() => TextRenderer.RenderBlocks(empty, "깨진 그림.png", src, 0, 0, Blocks(), t.At("c.png"), Settings()));
        Assert.Contains("깨진 그림.png", ex.Message);
    }
}
