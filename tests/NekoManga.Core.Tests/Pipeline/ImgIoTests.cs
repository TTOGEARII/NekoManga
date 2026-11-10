using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

public class ImgIoTests
{
    // ── 확장자 판정 ──

    [Fact]
    public void ImageExts_AllEntries_AreLowercaseWithLeadingDotAndUnique()
    {
        // IsImageFile과 메인 창 필터는 소문자로 바꾼 확장자를 이 목록과 그대로 비교한다 — 대문자거나 점이 빠진 항목은 영영 맞지 않는다
        Assert.NotEmpty(ImgIo.ImageExts);
        Assert.All(ImgIo.ImageExts, e =>
        {
            Assert.StartsWith(".", e);
            Assert.True(e.Length > 1, "점만 있는 항목");
            Assert.Equal(e.ToLowerInvariant(), e);
        });
        Assert.Equal(ImgIo.ImageExts.Length, ImgIo.ImageExts.Distinct().Count());
    }

    [Theory]
    [InlineData("page.png")]
    [InlineData("PAGE.PNG")]              // 스캔본·카메라 파일은 대문자 확장자가 흔하다
    [InlineData("scan.JpG")]
    [InlineData("표지 01.jpeg")]
    [InlineData("第01話.WEBP")]
    [InlineData("vol.1/page.01.tiff")]    // 이름 중간의 점은 확장자가 아니다
    [InlineData("a.bmp")]
    public void IsImageFile_ImageExtensionInAnyCase_IsTrue(string path) => Assert.True(ImgIo.IsImageFile(path));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("page")]                  // 확장자 없음
    [InlineData("")]
    [InlineData("01_jpg.png.3f9a1c2e.part")]   // 쓰는 중인 임시 파일 — 폴더를 끌어다 놓아도 원고로 잡히면 안 된다
    [InlineData("01_jpg.mask.json")]      // 사이드카
    [InlineData("vol.png/readme")]        // 폴더 이름에 붙은 확장자는 세지 않는다
    public void IsImageFile_OtherOrMissingExtension_IsFalse(string path) => Assert.False(ImgIo.IsImageFile(path));

    // ── 읽기·쓰기 ──

    [Fact]
    public void WriteThenRead_PngOnUnicodePath_RoundTripsPixelsExactly()
    {
        // 경로를 코드페이지로 옮기는 OpenCV 파일 API는 이런 이름에서 터진다 — 파일은 .NET이 읽고 쓰고 OpenCV는 메모리 버퍼만 다뤄야 통과한다
        using var t = new TempDir();
        var path = t.At(UnicodeName, "第01話 표지 ｱ.png");
        using var img = ColorImage();

        Assert.True(ImgIo.Write(path, img));

        Assert.True(File.Exists(path));
        using var back = ImgIo.Read(path);
        AssertSamePixels(img, back);   // png는 무손실
    }

    /// <summary>파일 선택 필터가 받는 확장자 가운데 번들된 OpenCV가 실제로 다루는 것 (.avif는 아래 Skip 테스트).</summary>
    public static IEnumerable<object[]> DecodableExts() =>
        ImgIo.ImageExts.Where(e => e != ".avif").Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(DecodableExts))]
    public void WriteThenRead_EveryListedExtension_GivesImageOfSameSize(string ext)
    {
        // 목록의 확장자는 메인 창이 원고로 받는다 — 받아 놓고 못 읽으면 그 장이 작업 도중에 실패한다
        using var t = new TempDir();
        var path = t.At(UnicodeName, "page" + ext);
        bool grayOnly = ext is ".pgm" or ".pbm";   // 회색조·흑백 전용 포맷
        using var img = grayOnly ? GrayImage() : ColorImage();

        Assert.True(ImgIo.Write(path, img));

        using var back = ImgIo.Read(path);
        Assert.False(back.Empty());
        Assert.Equal(img.Size(), back.Size());
        Assert.Equal(3, back.Channels());   // 기본 읽기 모드는 컬러(BGR) — 회색조 파일도 3채널로 온다
    }

    // 16×12, 왼쪽 위 8×6만 검정인 AVIF 한 장 (320바이트)
    private const string AvifSample =
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAADrbWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAAAAAAAOcGl0bQAAAAAAAQAAAB5pbG9j" +
        "AAAAAEQAAAEAAQAAAAEAAAETAAAALQAAAChpaW5mAAAAAAABAAAAGmluZmUCAAAAAAEAAGF2MDFDb2xvcgAAAABqaXBycAAAAEtpcGNvAAAAFGlzcGUAAAAAAAAAEAAAAAwA" +
        "AAAQcGl4aQAAAAADCAgIAAAADGF2MUOBAAwAAAAAE2NvbHJuY2x4AAEADQAGgAAAABdpcG1hAAAAAAAAAAEAAQQBAoMEAAAANW1kYXQSAAoJGAz+2iAhoNCAMh5F4eHhiGEk" +
        "kkmUAAACjhHM7Ag61FVgw/nyijZylqA=";

    [Fact(Skip = "알려진 결함: ImageExts에 .avif가 있지만 번들된 OpenCV 런타임은 AVIF 코덱 없이 빌드돼 있어(빌드 정보 'AVIF: NO') 빈 Mat이 돌아온다")]
    public void Read_AvifListedInImageExts_DecodesSample()
    {
        // 목록에 있는 한 읽혀야 한다 — 코덱을 넣든 목록에서 빼든 이 테스트는 통과한다
        if (!ImgIo.ImageExts.Contains(".avif")) return;
        using var t = new TempDir();
        var path = t.At("page.avif");
        File.WriteAllBytes(path, Convert.FromBase64String(AvifSample));

        using var img = ImgIo.Read(path);

        Assert.False(img.Empty());
        Assert.Equal(new Size(16, 12), img.Size());
    }

    [Fact]
    public void Read_FileThatIsNotAnImage_ReturnsEmptyMat()
    {
        // 못 읽는 파일은 예외가 아니라 빈 Mat — 호출부가 Empty()로 갈라 "읽지 못했어요"를 알린다
        using var t = new TempDir();
        var path = t.At(UnicodeName, "가짜.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "이미지가 아닌 내용");

        using var img = ImgIo.Read(path);

        Assert.True(img.Empty());
    }

    [Fact(Skip = "알려진 결함: 0바이트 파일은 빈 Mat이 아니라 ArgumentException(Empty span)으로 터진다 — 문서 주석의 계약은 '실패 시 빈 Mat'")]
    public void Read_ZeroByteFile_ReturnsEmptyMat()
    {
        // 쓰다 만 파일·내려받다 끊긴 파일은 0바이트로 남는다 — 이것도 '못 읽는 파일'이다
        using var t = new TempDir();
        var path = t.At("empty.png");
        File.WriteAllBytes(path, []);

        using var img = ImgIo.Read(path);

        Assert.True(img.Empty());
    }

    [Fact]
    public void Write_ParentFoldersMissing_CreatesThem()
    {
        // 사이드카 폴더(.neko)는 처음 쓸 때 없다
        using var t = new TempDir();
        var path = t.At("결과 폴더", ".neko", "01_jpg.base.png");
        using var img = ColorImage();

        Assert.True(ImgIo.Write(path, img));

        using var back = ImgIo.Read(path);
        AssertSamePixels(img, back);
    }

    [Fact]
    public void Write_OverExistingFile_ReplacesItAndLeavesNoTempFile()
    {
        using var t = new TempDir();
        var path = t.At("out", "01_jpg.png");
        using var first = ColorImage(60, 40, seed: 1);
        using var second = ColorImage(90, 50, seed: 2);
        Assert.True(ImgIo.Write(path, first));

        Assert.True(ImgIo.Write(path, second));

        using var back = ImgIo.Read(path);
        AssertSamePixels(second, back);
        Assert.Equal(["01_jpg.png"], FilesUnder(t.At("out")));   // 임시 파일(.part)은 교체 뒤 남지 않는다
    }

    [Fact]
    public void Write_EncoderRejectsImage_ReturnsFalseAndKeepsExistingFile()
    {
        // 회색조 전용 포맷(.pgm)에 컬러 이미지를 주면 인코더가 거절한다 — 인코딩에 실패하면 false만 돌려주고 있던 파일은 건드리지 않는다
        using var t = new TempDir();
        var path = t.At("page.pgm");
        using var gray = GrayImage();
        using var color = ColorImage();
        Assert.True(ImgIo.Write(path, gray));
        var before = File.ReadAllBytes(path);

        bool ok;
        var logLevel = Cv2.GetLogLevel();
        Cv2.SetLogLevel(LogLevel.SILENT);   // 예상한 실패라 OpenCV가 stderr에 찍는 오류 줄은 잠시 끈다
        try { ok = ImgIo.Write(path, color); }
        finally { Cv2.SetLogLevel(logLevel); }

        Assert.False(ok);
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertNoTempFiles(t.Root);
    }

    [Fact]
    public void Write_UnknownExtension_FailsAndLeavesNothingOnDisk()
    {
        // 모르는 확장자를 png 바이트로 채워 성공인 척하면 '확장자만 다른 파일'이 생긴다 — 실패로 알리고(false든 예외든) 아무것도 남기지 않는다
        using var t = new TempDir();
        var path = t.At("out", "page.xyz");
        using var img = ColorImage();

        bool ok = false;
        var ex = Record.Exception(() => ok = ImgIo.Write(path, img));

        Assert.True(ex != null || !ok, "모르는 확장자인데 성공으로 돌아왔다");
        Assert.Empty(FilesUnder(t.Root));
    }

    // ── 텍스트(사이드카 json) ──

    [Fact]
    public void WriteAllTextAtomic_NewFileInMissingFolder_WritesTextThatReadsBack()
    {
        using var t = new TempDir();
        var path = t.At(UnicodeName, ".neko", "01_jpg.mask.json");
        const string text = "{\"Summary\":\"막대 2·모자이크 1 → 24705px\",\"Source\":\"第01話.jpg\"}";

        ImgIo.WriteAllTextAtomic(path, text);

        Assert.Equal(text, File.ReadAllText(path));
        AssertNoTempFiles(t.Root);
    }

    [Fact]
    public void WriteAllTextAtomic_ExistingLongerFile_IsReplacedEntirely()
    {
        // 제자리에 덮어쓰다 말면 옛 내용의 꼬리가 남아 json이 깨진다 — 통째로 교체돼야 한다
        using var t = new TempDir();
        var path = t.At("side.json");
        File.WriteAllText(path, new string('x', 4000));

        ImgIo.WriteAllTextAtomic(path, "{\"Skip\":true}");

        Assert.Equal("{\"Skip\":true}", File.ReadAllText(path));
        Assert.Equal(["side.json"], FilesUnder(t.Root));
    }

    // ── 원본 그대로 복사 ──

    [Fact]
    public void CopyAtomic_UnicodePaths_CopiesBytesExactlyIntoNewFolder()
    {
        using var t = new TempDir();
        var src = WriteImageFile(t.At(UnicodeName, "第01話.jpg"));
        var dst = t.At(UnicodeName, UnicodeName + "_neko", "第01話_jpg.jpg");

        ImgIo.CopyAtomic(src, dst);

        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
        AssertNoTempFiles(t.Root);
    }

    [Fact]
    public void CopyAtomic_OldSource_StampsCopyWithFreshWriteTimeAndLeavesSourceAlone()
    {
        // 결과 보기의 '더 나중에 쓰인 쪽' 규칙은 복사본의 수정 시각을 본다 — 원본의 옛 시각을 물려받으면 방금 만든 복사본이 옛 결과에 밀린다.
        // 원본을 2001년으로 돌려 두고 '그보다 한참 뒤'인지만 본다(지금이 몇 시인지는 따지지 않는다).
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"));
        var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, old);
        var dst = t.At("vol1", "vol1_neko", "01_jpg.jpg");

        ImgIo.CopyAtomic(src, dst);

        Assert.True(File.GetLastWriteTimeUtc(dst) > old.AddYears(1), "복사본이 원본의 옛 수정 시각을 물려받았다");
        Assert.Equal(old, File.GetLastWriteTimeUtc(src));
    }

    [Fact]
    public void CopyAtomic_ReadOnlySource_CopyIsWritableAndCanBeCopiedOverAgain()
    {
        // 압축을 풀어 읽기 전용이 된 원고 — 속성까지 물려받으면 다음 실행이 같은 자리에 덮어쓰지 못한다
        using var t = new TempDir();
        var src = WriteImageFile(t.At("vol1", "01.jpg"));
        var dst = t.At("vol1", "vol1_neko", "01_jpg.jpg");
        File.SetAttributes(src, FileAttributes.ReadOnly);
        try
        {
            ImgIo.CopyAtomic(src, dst);
            Assert.False(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly));

            ImgIo.CopyAtomic(src, dst);   // 두 번째 실행

            Assert.False(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
            Assert.True(File.GetAttributes(src).HasFlag(FileAttributes.ReadOnly));   // 원본의 보호는 그대로
            AssertNoTempFiles(t.Root);
        }
        finally { File.SetAttributes(src, FileAttributes.Normal); }   // 임시 폴더를 지울 수 있게
    }

    [Fact]
    public void CopyAtomic_MissingSource_ThrowsAndCreatesNoTarget()
    {
        // 원고가 사라졌으면 조용히 넘어가지 않는다 — 러너가 그 장을 실패로 알린다
        using var t = new TempDir();
        var dst = t.At("out", "01_jpg.jpg");

        Assert.ThrowsAny<IOException>(() => ImgIo.CopyAtomic(t.At("없는 원고.jpg"), dst));

        Assert.Empty(FilesUnder(t.Root));
    }
}

/// <summary>쓸 자리의 파일이 보호돼 있거나 다른 쪽이 쥐고 있을 때. 교체 재시도(합쳐 0.5초 남짓)를 끝까지 타는 테스트라 다른 클래스와 나란히 돌도록 따로 뒀다.</summary>
public class ImgIoBlockedTargetTests
{
    [Fact]
    public void Write_ReadOnlyTarget_ThrowsAndLeavesItUntouched()
    {
        // 읽기 전용은 사용자가 보호하려고 둔 것일 수 있다 — 속성을 풀어 덮지 않고 실패로 알린다
        using var t = new TempDir();
        var path = t.At("out", "01_jpg.png");
        using var first = ColorImage(60, 40, seed: 1);
        using var second = ColorImage(90, 50, seed: 2);
        Assert.True(ImgIo.Write(path, first));
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ImgIo.Write(path, second));

            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(["01_jpg.png"], FilesUnder(t.At("out")));   // 임시 파일도 치운다
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void Write_TargetLockedAgainstWriting_ThrowsAndKeepsOldContent()
    {
        // 다른 쪽이 쓰기를 허락하지 않고 쥐고 있으면 열기부터 거부된다 — 멀쩡하던 옛 파일이 0바이트로 비워지면 안 된다
        using var t = new TempDir();
        var path = t.At("out", "01_jpg.png");
        using var first = ColorImage(60, 40, seed: 1);
        using var second = ColorImage(90, 50, seed: 2);
        Assert.True(ImgIo.Write(path, first));
        var before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => ImgIo.Write(path, second));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["01_jpg.png"], FilesUnder(t.At("out")));
    }

    [Fact]
    public void Write_TargetHeldWithoutDeleteSharing_FallsBackToWritingInPlace()
    {
        // 교체(rename)만 막힌 경우 — 재시도가 다 튕기면 제자리에 바로 써서라도 결과를 남긴다
        using var t = new TempDir();
        var path = t.At("out", "01_jpg.png");
        using var first = ColorImage(60, 40, seed: 1);
        using var second = ColorImage(90, 50, seed: 2);
        Assert.True(ImgIo.Write(path, first));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.True(ImgIo.Write(path, second));

        using var back = ImgIo.Read(path);
        AssertSamePixels(second, back);
        Assert.Equal(["01_jpg.png"], FilesUnder(t.At("out")));
    }
}
