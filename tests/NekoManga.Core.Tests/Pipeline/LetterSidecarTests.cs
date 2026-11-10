using System.Text.Json;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>식자 사이드카(.neko\&lt;이름&gt;.json·.base.png) — 결과 창이 번역부터 다시 돌리지 않고 다시 식자하는 근거.</summary>
public class LetterSidecarTests
{
    /// <summary>세로쓰기 대사 하나와, 결과 창에서 '이 풍선만' 스타일을 바꾼 효과음 하나.</summary>
    private static readonly TextBlockInfo[] Blocks =
    [
        new(10, 12, 50, 80, "日本語のセリフ", "한국어 대사 — \"따옴표\"", 18.5, Vertical: true, FontColorHex: "#000000"),
        new(60, 5, 120, 40, "ｶﾞｯ", "쾅", 24, Vertical: false, FontColorHex: "#FFFFFF",
            ManualRect: true, StyleScale: 1.25, Align: "Right", BoldOverride: 1, Italic: true, LineSpacing: 1.4, ColorOverride: "#FF0000", RotationDeg: -12.5),
    ];

    private static LetterSidecar Stored(string source, long srcLen, string basePath = "") =>
        new(source, 80, 100, basePath, [.. Blocks], srcLen);

    // ── 저장과 다시 읽기 ──

    [Fact]
    public void SaveThenLoad_UnicodeTextAndPaths_RoundTripsEveryField()
    {
        // 블록별 스타일 덮어쓰기까지 그대로 돌아와야 다시 식자할 때 사용자가 손본 풍선이 되살아난다
        using var t = new TempDir();
        var src = WriteImageFile(t.At(UnicodeName, "第01話.jpg"), seed: 1, width: 80, height: 100);
        var outPath = t.At(UnicodeName, UnicodeName + "_neko", "第01話_jpg.png");
        var basePath = WriteImageFile(LetterSidecar.BasePathFor(outPath), seed: 2, width: 80, height: 100);
        var sc = Stored(src, new FileInfo(src).Length, basePath);

        sc.Save(outPath);
        var back = LetterSidecar.Load(outPath);

        Assert.Equal(t.At(UnicodeName, UnicodeName + "_neko", ".neko", "第01話_jpg.json"), LetterSidecar.JsonPathFor(outPath));
        Assert.Equal(t.At(UnicodeName, UnicodeName + "_neko", ".neko", "第01話_jpg.base.png"), basePath);
        Assert.NotNull(back);
        Assert.Equal((sc.Source, sc.SrcW, sc.SrcH, sc.BasePath, sc.SrcLen), (back.Source, back.SrcW, back.SrcH, back.BasePath, back.SrcLen));
        Assert.Equal(Blocks, back.Blocks);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("baseMissing")]   // 식자 전 원고가 없으면 다시 식자할 수 없다 — 식자 탭을 열지 않게 '없음'
    [InlineData("jsonCut")]       // 반쯤 쓰인 json — 예외 대신 '없음'
    public void Load_IncompleteSidecar_ReturnsNull(string state)
    {
        using var t = new TempDir();
        var outPath = t.At("vol1_neko", "01_jpg.png");
        if (state != "nothing")
        {
            var basePath = WriteImageFile(LetterSidecar.BasePathFor(outPath));
            Stored(t.At("vol1", "01.jpg"), 100, basePath).Save(outPath);
            if (state == "baseMissing") File.Delete(basePath);
            else
            {
                var json = File.ReadAllText(LetterSidecar.JsonPathFor(outPath));
                File.WriteAllText(LetterSidecar.JsonPathFor(outPath), json[..(json.Length / 2)]);
            }
        }

        Assert.Null(LetterSidecar.Load(outPath));
    }

    [Fact]
    public void Load_SidecarWrittenBeforeNewerFields_FillsDefaults()
    {
        // 블록별 스타일·원고 길이가 생기기 전의 json — 새 필드가 기본값(페이지 설정 따름·배율 1)으로 읽혀야 옛 결과물도 다시 식자할 수 있다
        using var t = new TempDir();
        var outPath = t.At("vol1_neko", "01.png");
        var basePath = WriteImageFile(LetterSidecar.BasePathFor(outPath));
        var json = $$"""
            {"Source":{{JsonSerializer.Serialize(t.At("vol1", "01.png"))}},"SrcW":60,"SrcH":40,"BasePath":{{JsonSerializer.Serialize(basePath)}},
             "Blocks":[{"X1":1,"Y1":2,"X2":30,"Y2":20,"SourceText":"あ","Translation":"아","FontSize":16,"Vertical":true,"FontColorHex":"#000000"}]}
            """;
        File.WriteAllText(LetterSidecar.JsonPathFor(outPath), json);

        var sc = LetterSidecar.Load(outPath);

        Assert.NotNull(sc);
        Assert.Equal(0, sc.SrcLen);   // 길이를 모르는 옛 사이드카 — 같은 원고인지는 원고 크기로 가린다
        var block = Assert.Single(sc.Blocks);
        Assert.Equal(new TextBlockInfo(1, 2, 30, 20, "あ", "아", 16, true, "#000000"), block);
    }

    // ── 어느 원고의 사이드카인가 ──

    [Fact]
    public void BelongsTo_MovedFolder_NeedsSameNameAndSameLength()
    {
        // 경로가 같으면 그 원고, 폴더를 옮겼으면 이름과 바이트 길이가 둘 다 같아야 같은 원고 — 이름만 같은 다른 화의 블록을 가져오면 엉뚱한 대사가 박힌다
        using var t = new TempDir();
        var sc = Stored(t.At("옛 폴더", "001.jpg"), srcLen: 1234);

        Assert.True(sc.BelongsTo(t.At("옛 폴더", "001.JPG")));
        Assert.True(sc.BelongsTo(WriteBytesFile(t.At("새 폴더", "001.jpg"), 1234)));
        Assert.False(sc.BelongsTo(WriteBytesFile(t.At("2화", "001.jpg"), 1300)));       // 이름만 같다
        Assert.False(sc.BelongsTo(WriteBytesFile(t.At("새 폴더", "002.jpg"), 1234)));    // 길이만 같다
    }

    [Theory]
    [InlineData(80, 100, true)]     // 폴더만 옮긴 같은 원고
    [InlineData(80, 101, false)]    // 이름만 같은 다른 원고 — 크기가 다르다
    [InlineData(-1, -1, false)]     // 크기도 모르면 근거가 없다
    public void BelongsTo_LegacySidecarWithoutLength_FallsBackToImageSize(int width, int height, bool expected)
    {
        using var t = new TempDir();
        var sc = Stored(t.At("옛 폴더", "001.jpg"), srcLen: 0);
        var moved = WriteBytesFile(t.At("새 폴더", "001.jpg"), 500);
        (int, int)? dims = width < 0 ? null : (width, height);

        Assert.Equal(expected, sc.BelongsTo(moved, dims));
    }

    // ── 옛 이름(v1.5.5까지)에서 옮기기 ──

    [Fact]
    public void MoveLegacy_OwnBase_IsMovedAndJsonPointsAtNewPlace()
    {
        // json의 BasePath는 절대 경로다 — 식자 전 원고를 새 이름으로 옮기면서 json도 새 자리를 가리키게 고쳐 써야 한다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"));
        var legacyOut = t.At("vol1", "vol1_neko", "01.png");
        var newOut = t.At("vol1", "vol1_neko", "01_jpg.png");
        var legacyBase = WriteImageFile(LetterSidecar.BasePathFor(legacyOut), seed: 5);
        var baseBytes = File.ReadAllBytes(legacyBase);
        Stored(src, new FileInfo(src).Length, legacyBase).Save(legacyOut);

        LetterSidecar.MoveLegacy(legacyOut, newOut);

        var moved = LetterSidecar.Load(newOut);
        Assert.NotNull(moved);
        Assert.Equal(LetterSidecar.BasePathFor(newOut), moved.BasePath);
        Assert.Equal(baseBytes, File.ReadAllBytes(moved.BasePath));
        Assert.Equal(Blocks, moved.Blocks);
        Assert.Equal(["01_jpg.base.png", "01_jpg.json"], FilesUnder(t.At("vol1", "vol1_neko", ".neko")));
    }

    [Fact]
    public void MoveLegacy_BaseInFolderCopiedFrom_CopiesItAndLeavesOriginal()
    {
        // 결과 폴더를 통째로 복사해 왔으면 json의 BasePath는 원래 폴더를 가리킨다 — 그쪽 파일은 원래 폴더의 것이니 복사만 한다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"));
        var legacyOut = t.At("vol1", "vol1_neko", "01.png");
        var newOut = t.At("vol1", "vol1_neko", "01_jpg.png");
        var otherBase = WriteImageFile(t.At("원래 결과 폴더", ".neko", "01.base.png"), seed: 6);
        Stored(src, new FileInfo(src).Length, otherBase).Save(legacyOut);

        LetterSidecar.MoveLegacy(legacyOut, newOut);

        var moved = LetterSidecar.Load(newOut);
        Assert.NotNull(moved);
        Assert.Equal(LetterSidecar.BasePathFor(newOut), moved.BasePath);
        Assert.True(File.Exists(otherBase));
        Assert.Equal(File.ReadAllBytes(otherBase), File.ReadAllBytes(moved.BasePath));
        Assert.Null(LetterSidecar.Load(legacyOut));
    }
}
