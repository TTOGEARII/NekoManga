using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>결과 이름·폴더 규칙 — 메인 창·결과 창이 러너 없이 부르는 PipelineRunner의 static 멤버.</summary>
public class PipelineRunnerNamingTests
{
    private static AppSettings WithOutputRoot(string outputRoot) => new() { OutputRoot = outputRoot };

    public enum Spelling { Exact, TrailingSeparator, UpperCase }

    public enum OutMode { NekoFolder, FixedRoot, SourceFolder }

    public enum Present { Nothing, PngOnly, CopyOnly }

    /// <summary>윈도에서 같은 파일을 가리키는 경로가 하나도 없는지 (대소문자 무시한 전체 경로로).</summary>
    private static void AssertDistinctFiles(IEnumerable<string> paths)
    {
        var full = paths.Select(Path.GetFullPath).ToList();
        Assert.Equal(full.Count, full.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── 결과 경로 ──

    [Theory]
    [InlineData("", "01.png", "01.png")]                       // png 원고는 이름 그대로 — 결과도 png라 붙일 게 없다
    [InlineData("", "01.PNG", "01.png")]                       // 확장자 대소문자는 가리지 않는다
    [InlineData("", "01.jpg", "01_jpg.png")]                   // 그 밖의 원고는 '이름_확장자' — 같은 폴더 01.png의 결과와 갈린다
    [InlineData("", "01.JPG", "01_jpg.png")]
    [InlineData("", "page.01.webp", "page.01_webp.png")]       // 이름 중간의 점은 이름의 일부
    [InlineData("", "표지 第1話.jpeg", "표지 第1話_jpeg.png")]
    [InlineData("", "01", "01_src.png")]                       // 확장자 없는 원고도 01.png 원고의 결과와 겹치지 않는다
    [InlineData("   ", "01.jpg", "01_jpg.png")]                // 공백뿐인 출력 폴더는 비워 둔 것과 같다
    public void OutputPathFor_NoOutputRoot_GoesToNekoFolderBesideSourceFolder(string outputRoot, string fileName, string expectedName)
    {
        // 출력 폴더를 비워 두면 원고 폴더 옆 '<폴더 이름>_neko'에 모은다
        using var t = new TempDir();
        var s = WithOutputRoot(outputRoot);
        var src = t.At("1화 テスト", fileName);

        Assert.Equal(t.At("1화 テスト", "1화 テスト_neko"), PipelineRunner.OutputDirFor(s, src));
        Assert.Equal(t.At("1화 テスト", "1화 テスト_neko", expectedName), PipelineRunner.OutputPathFor(s, src));
    }

    [Theory]
    [InlineData("vol1", "001.jpg", "vol1_001_jpg.png")]
    [InlineData("vol2", "001.jpg", "vol2_001_jpg.png")]        // 여러 화의 001.jpg가 한 폴더로 모여도 화마다 이름이 다르다
    [InlineData("vol1", "001.png", "vol1_001.png")]
    [InlineData("第1話 표지", "001.webp", "第1話 표지_001_webp.png")]
    public void OutputPathFor_FixedOutputRoot_PrefixesSourceFolderName(string folder, string fileName, string expectedName)
    {
        using var t = new TempDir();
        var s = WithOutputRoot(t.At("결과 모음"));
        var src = t.At("원고", folder, fileName);

        Assert.Equal(t.At("결과 모음"), PipelineRunner.OutputDirFor(s, src));
        Assert.Equal(t.At("결과 모음", expectedName), PipelineRunner.OutputPathFor(s, src));
    }

    [Theory]
    [InlineData(Spelling.Exact)]
    [InlineData(Spelling.TrailingSeparator)]
    [InlineData(Spelling.UpperCase)]   // 윈도 경로는 대소문자를 가리지 않는다
    public void OutputRootIsSourceFolder_ResultGetsOwnNameAndUntouchedCopyIsSourceItself(Spelling spelling)
    {
        // 출력 폴더를 원고 폴더로 정해도 원본을 덮지 않는다 — png 원고도 '_png'를 붙이고, '원본 그대로'는 복사하지 않고 원본 자신을 가리킨다
        using var t = new TempDir();
        var vol = t.At("vol1");
        var s = WithOutputRoot(spelling switch
        {
            Spelling.TrailingSeparator => vol + Path.DirectorySeparatorChar,
            Spelling.UpperCase => vol.ToUpperInvariant(),
            _ => vol,
        });
        var png = Path.Combine(vol, "01.png");
        var jpg = Path.Combine(vol, "01.jpg");

        var pngOut = PipelineRunner.OutputPathFor(s, png);
        var jpgOut = PipelineRunner.OutputPathFor(s, jpg);

        Assert.Equal("vol1_01_png.png", Path.GetFileName(pngOut));
        Assert.Equal("vol1_01_jpg.png", Path.GetFileName(jpgOut));
        Assert.Equal(png, PipelineRunner.AsIsPathFor(pngOut, png));
        Assert.Equal(jpg, PipelineRunner.AsIsPathFor(jpgOut, jpg));
    }

    [Theory]
    [InlineData(OutMode.NekoFolder)]
    [InlineData(OutMode.FixedRoot)]
    [InlineData(OutMode.SourceFolder)]
    public void OutputPathFor_SameNameDifferentExtensionsInOneFolder_NeverShareResultOrSidecar(OutMode mode)
    {
        // 결과는 늘 .png다 — 이름을 원고 하나만 보고 정하면서도 같은 폴더의 01.jpg·01.png·01.webp가 서로의 결과·사이드카를 덮지 않아야 한다
        using var t = new TempDir();
        var vol = t.At("vol1");
        var s = WithOutputRoot(mode switch { OutMode.FixedRoot => t.At("out"), OutMode.SourceFolder => vol, _ => "" });
        string[] sources = [.. new[] { "01.png", "01.jpg", "01.jpeg", "01.webp", "01.bmp", "01" }.Select(n => Path.Combine(vol, n))];

        var outs = sources.Select(f => PipelineRunner.OutputPathFor(s, f)).ToArray();

        Assert.All(outs, o => Assert.Equal(".png", Path.GetExtension(o)));
        AssertDistinctFiles(outs);
        AssertDistinctFiles(outs.SelectMany(o => new[]
        {
            MaskSidecar.JsonPathFor(o), MaskSidecar.MaskPathFor(o), MaskSidecar.AutoPathFor(o), MaskSidecar.PrePathFor(o), MaskSidecar.KindPathFor(o),
            LetterSidecar.JsonPathFor(o), LetterSidecar.BasePathFor(o), ResultOwner.PathFor(o),
        }));
        AssertDistinctFiles([.. sources, .. outs]);   // 어느 결과도 원고 자리가 아니다
    }

    // ── 절대 경로의 원고: 결과 폴더·이름은 v1.8.2와 글자 하나까지 같다 ──

    /// <summary>v1.8.2(main)의 OutputDirFor·OutputStemFor·OutputPathFor를 그대로 옮긴 것 — 지금 코드가 절대 경로에서 이것과 같은 값을 내는지 대조한다.</summary>
    private static string V182OutputPath(string outputRoot, string inputFile)
    {
        static string NormDirKey(string d)
        {
            try { d = Path.GetFullPath(string.IsNullOrEmpty(d) ? "." : d); } catch { }
            return d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        var dir = !string.IsNullOrWhiteSpace(outputRoot)
            ? outputRoot
            : Path.Combine(Path.GetDirectoryName(inputFile)!, Path.GetFileName(Path.GetDirectoryName(inputFile)!) + "_neko");
        var stem = Path.GetFileNameWithoutExtension(inputFile);
        var ext = Path.GetExtension(inputFile).TrimStart('.').ToLowerInvariant();
        bool sameDir = string.Equals(NormDirKey(dir), NormDirKey(Path.GetDirectoryName(inputFile) ?? ""), StringComparison.Ordinal);
        if (ext != "png" || sameDir) stem += "_" + (ext.Length > 0 ? ext : "src");
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            string parent; try { parent = Path.GetFileName(Path.GetFullPath(Path.GetDirectoryName(inputFile) ?? "")); } catch { parent = ""; }
            if (parent.Length > 0) stem = parent + "_" + stem;
        }
        return Path.Combine(dir, stem + ".png");
    }

    [Theory]
    //          원고                               결과 폴더(출력 폴더 비움)                결과 이름         출력 폴더 고정(Q:\out)일 때의 이름
    [InlineData(@"Q:\vol1\01.jpg",                 @"Q:\vol1\vol1_neko",                    "01_jpg.png",     "vol1_01_jpg.png")]
    [InlineData(@"Q:\vol1\01.png",                 @"Q:\vol1\vol1_neko",                    "01.png",         "vol1_01.png")]
    [InlineData(@"Q:\a\b c\1화 テスト\001.webp",   @"Q:\a\b c\1화 テスト\1화 テスト_neko",  "001_webp.png",   "1화 テスト_001_webp.png")]   // 여러 겹 폴더 — 바로 위 폴더 이름만 쓴다
    [InlineData(@"Q:\01.jpg",                      @"Q:\_neko",                             "01_jpg.png",     "01_jpg.png")]        // 드라이브 루트 — 폴더 이름이 없어 이름 없는 _neko, 앞머리도 없다
    [InlineData(@"q:\표지.png",                    @"q:\_neko",                             "표지.png",       "표지.png")]
    [InlineData(@"\\srv\share\01.png",             @"\\srv\share\_neko",                    "01.png",         "01.png")]            // 공유 루트도 드라이브 루트와 같다
    [InlineData(@"\\srv\share\vol1\01.jpg",        @"\\srv\share\vol1\vol1_neko",           "01_jpg.png",     "vol1_01_jpg.png")]
    [InlineData(@"\\?\Q:\01.png",                  @"\\?\Q:\_neko",                         "01.png",         "01.png")]
    [InlineData(@"Q:/vol1/01.png",                 @"Q:\vol1\vol1_neko",                    "01.png",         "vol1_01.png")]       // 슬래시로 쓴 경로
    [InlineData(@"Q:\vol1.\01.png",                @"Q:\vol1.\vol1._neko",                  "01.png",         "vol1_01.png")]       // 끝에 점이 붙은 폴더 표기 — 받은 글자 그대로(전체 경로로 풀면 점이 떨어진다)
    [InlineData(@"Q:\vol1 \01.png",                @"Q:\vol1 \vol1 _neko",                  "01.png",         "vol1_01.png")]       // 끝에 공백이 붙은 폴더 표기
    [InlineData(@"Q:\sub\..\01.png",               @"Q:\sub\..\.._neko",                    "01.png",         "01.png")]            // 풀지 않은 '..' — 어색한 이름이지만 v1.8.2가 만든 그대로다
    [InlineData(@"Q:\_neko\01.png",                @"Q:\_neko\_neko_neko",                  "01.png",         "_neko_01.png")]      // 결과 폴더 안의 그림을 원고로 올린 경우
    public void OutputPathFor_AbsoluteSourcePath_IsExactlyWhatV182Produced(string source, string expectedDir, string expectedName, string expectedFixedName)
    {
        // 결과와 .neko 사이드카(고친 마스크·식자 자료)는 이 이름으로 다시 찾는다 — 이름이 한 글자라도 달라지면 이미 만든 것이 버려진다.
        // 드라이브 루트를 드라이브 글자로 이름 짓던 것(Q:\Q_neko)은 걷어냈다: 외장 드라이브가 다른 글자로 붙으면 지난 결과를 못 찾았다
        var blank = WithOutputRoot("");
        var fixedRoot = WithOutputRoot(@"Q:\out");

        Assert.Equal(expectedDir, PipelineRunner.OutputDirFor(blank, source));
        Assert.Equal(expectedDir, PipelineRunner.DefaultOutputDirFor(source));
        Assert.Equal(Path.Combine(expectedDir, expectedName), PipelineRunner.OutputPathFor(blank, source));
        Assert.Equal(Path.Combine(@"Q:\out", expectedFixedName), PipelineRunner.OutputPathFor(fixedRoot, source));
        Assert.Equal(V182OutputPath("", source), PipelineRunner.OutputPathFor(blank, source));
        Assert.Equal(V182OutputPath(@"Q:\out", source), PipelineRunner.OutputPathFor(fixedRoot, source));
    }

    [Fact]
    public void OutputDirFor_SourceAtDriveRoot_FolderNameDoesNotDependOnDriveLetter()
    {
        // 외장 드라이브가 E:로 붙었다가 F:로 붙어도 그 안의 결과 폴더 이름은 같다 — 지난 결과·사이드카를 그대로 찾는다
        var s = WithOutputRoot("");

        var onE = PipelineRunner.OutputPathFor(s, @"E:\01.jpg");
        var onF = PipelineRunner.OutputPathFor(s, @"F:\01.jpg");

        Assert.Equal(@"E:\_neko\01_jpg.png", onE);
        Assert.Equal(onE[2..], onF[2..]);
    }

    // ── '원본 그대로' 복사본 ──

    [Theory]
    [InlineData("01.jpg", "01_jpg.jpg")]
    [InlineData("01.webp", "01_webp.webp")]
    [InlineData("01.JPG", "01_jpg.JPG")]      // 원본 확장자를 그대로 쓴다
    [InlineData("01.png", "01.png")]          // png 원고는 결과(png)와 같은 자리 — 결과는 둘 중 하나만 남는다
    [InlineData("01", "01_src")]              // 확장자 없는 원고는 "01_src."이 아니라 "01_src"
    public void AsIsPathFor_SeparateOutputFolder_IsResultNameWithSourceExtension(string fileName, string expectedName)
    {
        // 손댈 게 없는 장은 다시 인코딩하지 않고 원본을 확장자까지 그대로 내보낸다 — 이름은 결과물과 같아 보기 창이 같은 규칙으로 찾는다
        using var t = new TempDir();
        var src = t.At("vol1", fileName);
        var outPath = PipelineRunner.OutputPathFor(WithOutputRoot(""), src);

        Assert.Equal(t.At("vol1", "vol1_neko", expectedName), PipelineRunner.AsIsPathFor(outPath, src));
    }

    // ── 보여 줄 결과 고르기 ──

    [Theory]
    [InlineData(Present.Nothing)]
    [InlineData(Present.PngOnly)]
    [InlineData(Present.CopyOnly)]
    public void ResolveResultPath_AtMostOneResultForm_ShowsItOrFallsBackToSource(Present present)
    {
        // 결과물(.png) → '원본 그대로' 복사본 → 원본 순 — 아직 안 돌린 장은 원본을 보여 준다
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("vol1", "01.jpg"), 100);
        var outPath = PipelineRunner.OutputPathFor(WithOutputRoot(""), src);
        var copy = PipelineRunner.AsIsPathFor(outPath, src);
        if (present == Present.PngOnly) WriteBytesFile(outPath, 10);
        if (present == Present.CopyOnly) WriteBytesFile(copy, 20);

        var expected = present switch { Present.PngOnly => outPath, Present.CopyOnly => copy, _ => src };
        Assert.Equal(expected, PipelineRunner.ResolveResultPath(outPath, src));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveResultPath_BothFormsLeftOver_LaterWrittenOneWins(bool pngIsNewer)
    {
        // 2차는 한쪽을 쓰면 다른 쪽을 지우지만, 지우기가 막혔으면 둘 다 남는다 — 그때는 마지막 실행이 쓴 쪽을 보여 준다
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("vol1", "01.jpg"), 100);
        var outPath = WriteBytesFile(PipelineRunner.OutputPathFor(WithOutputRoot(""), src), 10);
        var copy = WriteBytesFile(PipelineRunner.AsIsPathFor(outPath, src), 20);
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(outPath, pngIsNewer ? older.AddHours(1) : older);
        File.SetLastWriteTimeUtc(copy, pngIsNewer ? older : older.AddHours(1));

        Assert.Equal(pngIsNewer ? outPath : copy, PipelineRunner.ResolveResultPath(outPath, src));
    }

    [Fact]
    public void ResolveResultPath_OutputFolderIsSourceFolder_SourceNeverOutranksResult()
    {
        // 출력 폴더가 원고 폴더면 '원본 그대로'는 원고 자신이다 — 결과를 만든 뒤 원고를 고쳐 저장했다고 결과 대신 원고를 보여 주면 안 된다
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("vol1", "01.jpg"), 100);
        var outPath = WriteBytesFile(PipelineRunner.OutputPathFor(WithOutputRoot(t.At("vol1")), src), 10);
        File.SetLastWriteTimeUtc(outPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(src, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(outPath, PipelineRunner.ResolveResultPath(outPath, src));
    }

    // ── 경로 키 ──

    [Fact]
    public void FileKey_SpellingsOfOneFile_ShareKeyWhileOtherFilesDiffer()
    {
        // 메인 창 행·이번 실행의 원고 목록·결과 자리 충돌 검사가 모두 이 키로 같은 파일을 가린다
        using var t = new TempDir();
        var key = PipelineRunner.FileKey(t.At("1화", "01.jpg"));

        Assert.All(new[]
        {
            t.At("1화", "01.jpg").ToUpperInvariant(),
            t.At("1화", ".", "01.jpg"),
            t.At("1화", "sub", "..", "01.jpg"),
            t.At("1화", "01.jpg").Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        }, p => Assert.Equal(key, PipelineRunner.FileKey(p)));
        Assert.All(new[]
        {
            t.At("1화", "01.png"),
            t.At("2화", "01.jpg"),
            t.At("1화", "01.jpg.part"),
        }, p => Assert.NotEqual(key, PipelineRunner.FileKey(p)));
    }

    // ── 검토 페이지 ──

    [Fact]
    public void ReviewPage_DisposeMats_ReleasesEveryMatAndIsSafeToRepeat()
    {
        // 러너가 실행 끝(실패·취소 포함)에 모든 장의 Mat을 해제한다 — 다시 불려도 터지지 않고, 해제한 Mat을 다시 내주지 않는다
        using var mask = new Mat(4, 4, MatType.CV_8UC1, Scalar.White);
        using var auto = mask.Clone();
        using var kinds = new Mat(4, 4, MatType.CV_8UC1, Scalar.Black);
        var pg = new PipelineRunner.ReviewPage(Path.Combine("vol1", "01.jpg")) { Mask = mask, AutoMask = auto, Kinds = kinds };

        pg.DisposeMats();

        Assert.True(mask.IsDisposed);
        Assert.True(auto.IsDisposed);
        Assert.True(kinds.IsDisposed);
        Assert.Null(pg.Mask);
        Assert.Null(pg.AutoMask);
        Assert.Null(pg.Kinds);
        pg.DisposeMats();
    }
}

/// <summary>상대 경로로 받은 원고(<c>NekoManga.exe --run 01.png</c>)의 결과 폴더·이름 — 현재 폴더를 바꾸므로 다른 테스트와 동시에 돌지 않는다.</summary>
[Collection(EnvCollection.Name)]
public class PipelineRunnerRelativePathTests
{
    /// <summary>현재 폴더를 잠깐 바꾼다.</summary>
    private sealed class Cwd : IDisposable
    {
        private readonly string _before = Environment.CurrentDirectory;
        public Cwd(string dir) { Directory.CreateDirectory(dir); Environment.CurrentDirectory = dir; }
        public void Dispose() => Environment.CurrentDirectory = _before;
    }

    [Theory]
    [InlineData("")]         // 출력 폴더를 비움 — 원고 폴더 안의 <폴더 이름>_neko
    [InlineData("fixed")]    // 출력 폴더 고정 — 이름 앞에 원고의 상위 폴더 이름
    public void OutputPathFor_RelativeSourcePath_IsTheSameAsForItsFullPath(string outputRoot)
    {
        // 폴더 이름을 받은 경로 그대로에서 얻으면 이름만 준 원고(01.jpg)는 폴더 부분이 비어 결과 폴더가 '_neko'가 되고(..\01.jpg는 '.._neko'),
        // 고정 출력 폴더에서는 이름 앞머리가 빠졌다 — 같은 원고가 경로를 쓴 꼴에 따라 다른 자리로 갔다
        using var t = new TempDir();
        var vol = t.At("원고", "1화 テスト");
        Directory.CreateDirectory(Path.Combine(vol, "sub"));
        var s = new AppSettings { OutputRoot = outputRoot.Length == 0 ? "" : t.At("결과") };
        using var cwd = new Cwd(vol);

        foreach (var rel in new[] { "01.jpg", Path.Combine(".", "01.jpg"), Path.Combine("sub", "..", "01.jpg"), Path.Combine("..", "2화", "01.jpg"), Path.Combine("sub", "01.jpg") })
        {
            var full = Path.GetFullPath(rel);
            Assert.Equal(PipelineRunner.OutputPathFor(s, full), PipelineRunner.OutputPathFor(s, rel));
            Assert.Equal(PipelineRunner.OutputDirFor(s, full), PipelineRunner.OutputDirFor(s, rel));
        }
        var outPath = PipelineRunner.OutputPathFor(s, "01.jpg");
        if (outputRoot.Length == 0)
        {
            Assert.Equal("1화 テスト_neko", Path.GetFileName(Path.GetDirectoryName(outPath)));
            Assert.Equal("01_jpg.png", Path.GetFileName(outPath));
            Assert.Equal("2화_neko", Path.GetFileName(PipelineRunner.OutputDirFor(s, Path.Combine("..", "2화", "01.jpg"))));
        }
        else Assert.Equal("1화 テスト_01_jpg.png", Path.GetFileName(outPath));
    }

    [Fact]
    public async Task RunAsync_RelativeSourcePath_WritesIntoNamedNekoFolderAndRecordsFullPath()
    {
        using var t = new TempDir();
        var vol = t.At("vol1");
        var full = PipelineTestHelpers.WriteImageFile(Path.Combine(vol, "01.jpg"), seed: 1);
        // 옛(v1.5.5) 이름으로 남은 이 원고의 결과 — 기본 결과 폴더이고 그 이름의 원고가 하나뿐이라 넘겨받는다. 폴더 판정이 결과 폴더 규칙과 어긋나면 그대로 남는다
        PipelineTestHelpers.WriteImageFile(Path.Combine(vol, "vol1_neko", "01.png"), seed: 7);
        var s = PipelineTestHelpers.EnginelessSettings(t);
        var log = new PipelineTestHelpers.ProgressLog();

        using (new Cwd(vol))
        using (var runner = new PipelineRunner(s))
            await runner.RunAsync(["01.jpg"], log, CancellationToken.None);

        Assert.Null(log.FinalOf("01.jpg").Error);   // 진행 보고의 키는 받은 경로 그대로다
        Assert.Equal([Path.Combine("vol1_neko", ".neko", "01_jpg.owner"), Path.Combine("vol1_neko", "01_jpg.jpg")],
            PipelineTestHelpers.FilesUnder(vol).Where(f => f != "01.jpg").ToArray());
        var owner = ResultOwner.Load(PipelineRunner.OutputPathFor(s, full));
        Assert.NotNull(owner);
        Assert.True(Path.IsPathFullyQualified(owner.Source));   // 다음 실행의 현재 폴더와 무관하게 같은 원고로 가려진다
        Assert.True(owner.MatchesSource(full));

        using var runner2 = new PipelineRunner(s);
        await runner2.RunAsync([full], log = new PipelineTestHelpers.ProgressLog(), CancellationToken.None);
        Assert.Null(log.FinalOf(full).Error);   // 전체 경로로 다시 돌려도 같은 자리, 같은 원고
    }
}
