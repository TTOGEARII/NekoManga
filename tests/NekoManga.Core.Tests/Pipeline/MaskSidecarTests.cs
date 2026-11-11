using NekoManga.Core.Decensor;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>검열 마스크 사이드카(.neko\&lt;이름&gt;.mask.json·.mask.png·.auto.png·.pre.png·.kind.png) — 고친 마스크가 재실행에도 유지되는 근거.</summary>
public class MaskSidecarTests
{
    private const int W = 64, H = 48;

    /// <summary>원고 폴더 vol1의 01.jpg와 그 결과 경로(기본 출력 폴더 규칙의 이름).</summary>
    private static (string Src, string OutPath) Page(TempDir t, int seed = 1)
    {
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed, W, H);
        return (src, t.At("vol1", "vol1_neko", "01_jpg.png"));
    }

    private static MaskSidecar Stored(string source, long srcLen) =>
        new(source, W, H, "", Skip: false, Edited: false, Confirmed: false, SavedAt: new DateTime(2026, 1, 1, 9, 0, 0), SrcLen: srcLen);

    // ── 자리 ──

    [Fact]
    public void PathsFor_ResultPath_SitInNekoFolderUnderResultName()
    {
        // 결과 창·러너가 이 자리로 서로의 파일을 찾는다 — 바뀌면 이미 만들어 둔 사이드카를 못 찾는다
        var outPath = Path.Combine("vol1_neko", "01_jpg.png");
        var neko = Path.Combine("vol1_neko", ".neko");

        Assert.Equal(Path.Combine(neko, "01_jpg.mask.json"), MaskSidecar.JsonPathFor(outPath));
        Assert.Equal(Path.Combine(neko, "01_jpg.mask.png"), MaskSidecar.MaskPathFor(outPath));
        Assert.Equal(Path.Combine(neko, "01_jpg.auto.png"), MaskSidecar.AutoPathFor(outPath));
        Assert.Equal(Path.Combine(neko, "01_jpg.pre.png"), MaskSidecar.PrePathFor(outPath));
        Assert.Equal(Path.Combine(neko, "01_jpg.kind.png"), MaskSidecar.KindPathFor(outPath));
        // 결과 창은 보던 파일('원본 그대로' 복사본 jpg)을 결과 경로로 넘기기도 한다 — 사이드카는 확장자와 상관없이 같다
        Assert.Equal(MaskSidecar.JsonPathFor(outPath), MaskSidecar.JsonPathFor(Path.ChangeExtension(outPath, ".jpg")));
    }

    // ── 저장과 다시 읽기 ──

    [Fact]
    public void WriteThenLoad_UnicodePaths_RoundTripsRecordAndImages()
    {
        // 원고·결과 폴더에 일본어·한글·공백이 있어도 저장한 것이 그대로 돌아와야 '재실행에도 고친 마스크 유지'가 지켜진다
        using var t = new TempDir();
        var src = WriteImageFile(t.At(UnicodeName, "第01話 표지.jpg"), seed: 1, width: W, height: H);
        var outPath = t.At(UnicodeName, UnicodeName + "_neko", "第01話 표지_jpg.png");
        using var mask = RectMask(W, H, new Rect(5, 6, 20, 10));
        using var auto = RectMask(W, H, new Rect(30, 20, 8, 8));
        using var pre = ColorImage(W, H, seed: 3);

        var written = MaskSidecar.Write(outPath, src, mask, auto, pre, "막대 2·모자이크 1 → 24705px", skip: false, edited: true, confirmed: false);

        var loaded = MaskSidecar.Load(outPath);
        Assert.Equal(written, loaded);   // 저장 시각까지 레코드 전체가 그대로
        Assert.Equal(src, loaded!.Source);
        Assert.Equal((W, H), (loaded.SrcW, loaded.SrcH));            // 원고 크기 = 마스크 크기
        Assert.Equal(new FileInfo(src).Length, loaded.SrcLen);       // 폴더를 옮겨도 같은 원고인지 가릴 근거
        Assert.Equal("막대 2·모자이크 1 → 24705px", loaded.Summary);
        Assert.True(loaded.Edited);
        Assert.False(loaded.Confirmed);
        Assert.False(loaded.Skip);
        using var maskBack = MaskSidecar.LoadMask(outPath);
        using var autoBack = MaskSidecar.LoadAuto(outPath);
        using var preBack = ImgIo.Read(MaskSidecar.PrePathFor(outPath));
        AssertSamePixels(mask, maskBack);
        AssertSamePixels(auto, autoBack);
        Assert.True(MaskSidecar.HasPre(outPath));
        AssertSamePixels(pre, preBack);
    }

    [Fact]
    public void Write_MaskOnlyResave_KeepsStoredAutoAndPre()
    {
        // 결과 창의 '마스크 수정' 저장은 마스크만 넘긴다 — '자동 감지로' 되돌리기용 사본과 검열 제거 전 원고는 그대로 남아야 한다
        using var t = new TempDir();
        var (src, outPath) = Page(t);
        using var auto = RectMask(W, H, new Rect(30, 20, 8, 8));
        using var pre = ColorImage(W, H, seed: 3);
        MaskSidecar.Write(outPath, src, auto, auto, pre, "감지", skip: false, edited: false, confirmed: false);
        var autoBytes = File.ReadAllBytes(MaskSidecar.AutoPathFor(outPath));
        var preBytes = File.ReadAllBytes(MaskSidecar.PrePathFor(outPath));
        using var edited = RectMask(W, H, new Rect(2, 2, 40, 30));

        MaskSidecar.Write(outPath, src, edited, null, null, "감지", skip: false, edited: true, confirmed: false);

        Assert.Equal(autoBytes, File.ReadAllBytes(MaskSidecar.AutoPathFor(outPath)));
        Assert.Equal(preBytes, File.ReadAllBytes(MaskSidecar.PrePathFor(outPath)));
        using var maskBack = MaskSidecar.LoadMask(outPath);
        AssertSamePixels(edited, maskBack);
        Assert.True(MaskSidecar.Load(outPath)!.Edited);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_WithoutKindMap_DropsOldMapOnlyForFreshDetection(bool freshDetection)
    {
        // 새 감지가 종류 지도 없이 끝났는데 옛 지도가 남으면 지난번 엔진 표시가 이번 마스크에 엉뚱하게 적용된다.
        // 결과 창의 마스크 저장(새 감지 아님)은 지도를 넘기지 않아도 그대로 둔다
        using var t = new TempDir();
        var (src, outPath) = Page(t);
        using var mask = RectMask(W, H, new Rect(5, 6, 20, 10));
        using var kinds = new Mat(H, W, MatType.CV_8UC1, new Scalar(CensorKind.Bar));
        MaskSidecar.Write(outPath, src, mask, null, null, "", false, false, false, kinds, freshDetection: true);
        Assert.True(File.Exists(MaskSidecar.KindPathFor(outPath)));

        MaskSidecar.Write(outPath, src, mask, null, null, "", false, false, false, kinds: null, freshDetection: freshDetection);

        Assert.Equal(!freshDetection, File.Exists(MaskSidecar.KindPathFor(outPath)));
    }

    [Fact]
    public void LoadKinds_StoredMap_KeepsEachKindValue()
    {
        // 종류 지도의 값은 엔진 번호(막대 1·모자이크 2·기존 방식 3)다 — 마스크처럼 0/255로 이진화되면 제거 단계가 엔진을 못 고른다
        using var t = new TempDir();
        var (src, outPath) = Page(t);
        using var mask = RectMask(W, H, new Rect(0, 0, 60, 10));
        using var kinds = new Mat(H, W, MatType.CV_8UC1, new Scalar(CensorKind.None));
        kinds.Rectangle(new Rect(0, 0, 10, 10), new Scalar(CensorKind.Bar), -1);
        kinds.Rectangle(new Rect(20, 0, 10, 10), new Scalar(CensorKind.Mosaic), -1);
        kinds.Rectangle(new Rect(40, 0, 10, 10), new Scalar(CensorKind.Legacy), -1);
        MaskSidecar.Write(outPath, src, mask, null, null, "", false, false, false, kinds, freshDetection: true);

        using var back = MaskSidecar.LoadKinds(outPath);

        AssertSamePixels(kinds, back);
    }

    [Fact]
    public void LoadMask_SoftEdgedMask_ComesBackBinary()
    {
        // 계약은 255=지울 곳인 이진 마스크다 — 붓 가장자리 같은 중간값이 섞인 PNG도 0/255로만 돌아와야 제거 단계가 경계를 헷갈리지 않는다
        using var t = new TempDir();
        var (_, outPath) = Page(t);
        using var soft = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        soft.Rectangle(new Rect(0, 0, 16, H), new Scalar(30), -1);     // 옅은 칠
        soft.Rectangle(new Rect(16, 0, 16, H), new Scalar(220), -1);   // 짙은 칠
        soft.Rectangle(new Rect(32, 0, 16, H), new Scalar(255), -1);
        Assert.True(ImgIo.Write(MaskSidecar.MaskPathFor(outPath), soft));

        using var m = MaskSidecar.LoadMask(outPath);

        Assert.NotNull(m);
        Assert.Equal(MatType.CV_8UC1, m.Type());
        Assert.Equal(new byte[] { 0, 255, 255, 0 }, new[] { m.At<byte>(10, 8), m.At<byte>(10, 24), m.At<byte>(10, 40), m.At<byte>(10, 56) });
        using var between = new Mat();
        Cv2.InRange(m, new Scalar(1), new Scalar(254), between);
        Assert.Equal(0, Cv2.CountNonZero(between));
    }

    // ── 없거나 망가진 사이드카 ──

    [Theory]
    [InlineData("nothing")]
    [InlineData("maskPngMissing")]   // json만 남았다 — 마스크 없이 '저장된 마스크 사용'으로 가면 안 된다
    [InlineData("jsonCut")]          // 반쯤 쓰인 json — 예외 대신 '없음'
    public void Load_IncompleteSidecar_ReturnsNull(string state)
    {
        using var t = new TempDir();
        var (src, outPath) = Page(t);
        if (state != "nothing")
        {
            using var mask = RectMask(W, H, new Rect(5, 6, 20, 10));
            MaskSidecar.Write(outPath, src, mask, null, null, "", false, false, false);
            if (state == "maskPngMissing") File.Delete(MaskSidecar.MaskPathFor(outPath));
            else
            {
                var json = File.ReadAllText(MaskSidecar.JsonPathFor(outPath));
                File.WriteAllText(MaskSidecar.JsonPathFor(outPath), json[..(json.Length / 2)]);
            }
        }

        Assert.Null(MaskSidecar.Load(outPath));
    }

    [Fact]
    public void Loaders_ImageFileThatIsNotAnImage_ReturnNull()
    {
        // 못 읽는 PNG는 '없음'과 같다 — 재실행이 예외로 그 장을 실패시키지 않고 새로 감지하게
        using var t = new TempDir();
        var (_, outPath) = Page(t);
        Directory.CreateDirectory(Path.GetDirectoryName(MaskSidecar.MaskPathFor(outPath))!);
        foreach (var p in new[] { MaskSidecar.MaskPathFor(outPath), MaskSidecar.AutoPathFor(outPath), MaskSidecar.KindPathFor(outPath) })
            File.WriteAllText(p, "PNG 아님");

        Assert.Null(MaskSidecar.LoadMask(outPath));
        Assert.Null(MaskSidecar.LoadAuto(outPath));
        Assert.Null(MaskSidecar.LoadKinds(outPath));
    }

    [Fact]
    public void LoadMask_ZeroByteMaskPng_ReturnsNull()
    {
        // 쓰다 끊겨 0바이트로 남은 마스크 — 재실행(1차)이 이 예외로 그 장을 매번 실패시키지 말고 새로 감지해야 한다
        using var t = new TempDir();
        var (_, outPath) = Page(t);
        Directory.CreateDirectory(Path.GetDirectoryName(MaskSidecar.MaskPathFor(outPath))!);
        File.WriteAllBytes(MaskSidecar.MaskPathFor(outPath), []);
        File.WriteAllBytes(MaskSidecar.AutoPathFor(outPath), []);

        Assert.Null(MaskSidecar.LoadMask(outPath));
        Assert.Null(MaskSidecar.LoadAuto(outPath));
    }

    [Fact]
    public void LoadMask_MaskPngLockedAgainstReading_ThrowsInsteadOfNull()
    {
        // 잠긴 마스크는 '없음'이 아니다 — null로 넘기면 재실행이 사용자가 고친 마스크를 두고 새로 감지해(검토 흐름에서는 사이드카까지 덮어) 고친 것이 사라진다.
        // 예외로 알리면(재실행 1차에서는 그 장의 실패) 잠금이 풀린 뒤 다시 돌릴 때 고친 마스크를 그대로 쓴다
        using var t = new TempDir();
        var (src, outPath) = Page(t);
        using var mask = RectMask(W, H, new Rect(5, 6, 20, 10));
        MaskSidecar.Write(outPath, src, mask, null, null, "", skip: false, edited: true, confirmed: true);

        using (new FileStream(MaskSidecar.MaskPathFor(outPath), FileMode.Open, FileAccess.Read, FileShare.None))   // 다른 프로그램이 읽기까지 막고 쥐고 있다
            Assert.ThrowsAny<IOException>(() => MaskSidecar.LoadMask(outPath));

        using var back = MaskSidecar.LoadMask(outPath);
        AssertSamePixels(mask, back);
    }

    // ── 어느 원고의 사이드카인가 ──

    [Fact]
    public void MatchesSource_LegacySidecarWithoutLength_MatchesOnlyTheSamePath()
    {
        // 길이를 모르는 옛 사이드카는 경로만 근거다 — 표기만 다른 같은 경로는 같은 원고, 다른 폴더의 같은 이름·같은 바이트 파일은 남의 원고
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("vol1", "001.jpg"), 1000);
        var copy = WriteBytesFile(t.At("vol2", "001.jpg"), 1000);
        var sc = Stored(src, srcLen: 0);

        Assert.True(sc.MatchesSource(src.ToUpperInvariant()));
        Assert.True(sc.MatchesSource(t.At("vol1", ".", "001.jpg").Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.False(sc.MatchesSource(copy));
    }

    [Fact]
    public void MatchesSource_MovedFolder_NeedsSameNameAndSameLength()
    {
        // 폴더를 옮기면 경로가 달라진다 — 이름과 바이트 길이가 둘 다 같아야 같은 원고. 1화·2화의 001.jpg처럼 이름만 같은 원고는 길이로 걸러진다
        using var t = new TempDir();
        var sc = Stored(t.At("옛 폴더", "001.jpg"), srcLen: 1234);   // 원래 자리는 이제 비어 있다

        Assert.True(sc.MatchesSource(WriteBytesFile(t.At("새 폴더", "001.jpg"), 1234)));
        Assert.True(sc.MatchesSource(WriteBytesFile(t.At("새 폴더 2", "001.JPG"), 1234)));    // 이름 대소문자는 가리지 않는다
        Assert.False(sc.MatchesSource(WriteBytesFile(t.At("2화", "001.jpg"), 1300)));       // 이름만 같다
        Assert.False(sc.MatchesSource(WriteBytesFile(t.At("새 폴더", "002.jpg"), 1234)));    // 길이만 같다
        Assert.False(sc.MatchesSource(t.At("없는 폴더", "001.jpg")));                         // 원고가 없으면 같은 원고라는 근거가 없다
    }

    [Fact]
    public void SourceIs_SameNameAndLengthInAnotherFolder_IsFalse()
    {
        // 다른 이름 밑의 사이드카를 옮겨 올 근거는 경로뿐이다 — 이름·길이만 같은 복제 원고가 가져가면 원래 원고가 마스크를 잃는다
        using var t = new TempDir();
        var src = WriteBytesFile(t.At("vol1", "001.jpg"), 500);
        var clone = WriteBytesFile(t.At("vol1 사본", "001.jpg"), 500);
        var sc = Stored(src, srcLen: 500);

        Assert.True(sc.MatchesSource(clone));   // 마스크를 빌려 쓰는 데는 같은 원고로 보지만
        Assert.False(sc.SourceIs(clone));       // 옮겨 올 근거로는 쓰지 않는다
        Assert.True(sc.SourceIs(src.ToUpperInvariant()));
    }

    // ── 옛 이름(v1.5.5까지)에서 옮기기 ──

    [Fact]
    public void MoveLegacy_StoppedHalfway_LeavesJsonBehindSoNextCallFinishes()
    {
        // json을 마지막에 옮겨야 도중에 멈춰도 새 자리에 json이 없어 다음 실행이 나머지를 이어서 옮긴다
        // (json이 먼저 가면 '이미 옮겼음'으로 보고 남은 마스크·원고 사본을 옛 이름 밑에 버려 둔다)
        using var t = new TempDir();
        var (src, newOut) = Page(t);
        var legacyOut = t.At("vol1", "vol1_neko", "01.png");
        using var mask = RectMask(W, H, new Rect(5, 6, 20, 10));
        using var auto = RectMask(W, H, new Rect(30, 20, 8, 8));
        using var pre = ColorImage(W, H, seed: 3);
        var written = MaskSidecar.Write(legacyOut, src, mask, auto, pre, "옛 이름", skip: false, edited: true, confirmed: true);

        using (new FileStream(MaskSidecar.PrePathFor(legacyOut), FileMode.Open, FileAccess.Read, FileShare.Read))   // 결과 창이 원고 사본을 쥐고 있다
            Assert.ThrowsAny<IOException>(() => MaskSidecar.MoveLegacy(legacyOut, newOut));
        Assert.False(File.Exists(MaskSidecar.JsonPathFor(newOut)));
        Assert.True(File.Exists(MaskSidecar.JsonPathFor(legacyOut)));

        MaskSidecar.MoveLegacy(legacyOut, newOut);

        Assert.Equal(written, MaskSidecar.Load(newOut));
        using var maskBack = MaskSidecar.LoadMask(newOut);
        using var autoBack = MaskSidecar.LoadAuto(newOut);
        AssertSamePixels(mask, maskBack);
        AssertSamePixels(auto, autoBack);
        Assert.Equal(["01_jpg.auto.png", "01_jpg.mask.json", "01_jpg.mask.png", "01_jpg.pre.png"], FilesUnder(t.At("vol1", "vol1_neko", ".neko")));
    }

    [Fact]
    public void MoveLegacy_NewNameAlreadyHasSidecar_LeavesBothAsTheyAre()
    {
        // 새 이름 자리의 사이드카는 지금 규칙으로 만든 최신 것 — 옛 것으로 덮지 않는다
        using var t = new TempDir();
        var (src, newOut) = Page(t);
        var legacyOut = t.At("vol1", "vol1_neko", "01.png");
        using var oldMask = RectMask(W, H, new Rect(5, 6, 20, 10));
        using var newMask = RectMask(W, H, new Rect(30, 20, 8, 8));
        MaskSidecar.Write(legacyOut, src, oldMask, null, null, "옛 것", skip: false, edited: false, confirmed: false);
        MaskSidecar.Write(newOut, src, newMask, null, null, "새 것", skip: false, edited: true, confirmed: true);

        MaskSidecar.MoveLegacy(legacyOut, newOut);

        Assert.Equal("새 것", MaskSidecar.Load(newOut)!.Summary);
        using var newBack = MaskSidecar.LoadMask(newOut);
        AssertSamePixels(newMask, newBack);
        Assert.Equal("옛 것", MaskSidecar.Load(legacyOut)!.Summary);
    }
}
