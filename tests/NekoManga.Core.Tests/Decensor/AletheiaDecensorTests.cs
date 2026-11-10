using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>AletheiaDecensor·CensorKind — 모델을 열기 전에 끝나는 판정(준비 여부·안내 문구·입력 검사)만 본다.</summary>
public class AletheiaDecensorTests
{
    // 구성요소 설치기가 받는 자리(RuntimeRoot\models\<이름>)의 파일 이름 — 엔진이 같은 이름을 찾아야 설치가 '준비됨'이 된다
    private const string DetectorFile = "aletheia_mrcnn.onnx", BarFile = "aletheia_bar.onnx", MosaicFile = "aletheia_mosaic.onnx";

    [Fact]
    public void CensorKind_Values_AreStableBecauseTheyArePersisted()
    {
        // 종류 지도는 값 그대로 사이드카 PNG에 저장된다 — 값이 바뀌면 이전에 저장한 지도를 다른 종류로 읽는다.
        // 0은 '표시 없음'(검출 밖)이어야 지도 바탕을 그대로 쓸 수 있다.
        Assert.Equal(0, CensorKind.None);
        Assert.Equal(1, CensorKind.Bar);
        Assert.Equal(2, CensorKind.Mosaic);
        Assert.Equal(3, CensorKind.Legacy);
    }

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { DetectorFile }, true, false)]
    [InlineData(new[] { BarFile }, false, false)]               // 복원은 막대·모자이크 모델 둘 다 있어야 한다
    [InlineData(new[] { MosaicFile }, false, false)]
    [InlineData(new[] { BarFile, MosaicFile }, false, true)]
    [InlineData(new[] { DetectorFile, BarFile, MosaicFile }, true, true)]
    public void Readiness_FollowsInstalledModelFiles(string[] files, bool detectorReady, bool inpainterReady)
    {
        using var t = new TempDir();
        var root = t.At("런타임 폴더");   // 한글·공백이 든 설치 경로
        using var env = EnvWithModelFiles(root, files);

        var dec = new AletheiaDecensor(new AppSettings { RuntimeRoot = root }, env);

        Assert.Equal(detectorReady, dec.DetectorReady);
        Assert.Equal(inpainterReady, dec.InpainterReady);
    }

    [Fact]
    public void Detect_WithoutDetectorModel_ThrowsInstallGuidance()
    {
        // 모델이 없으면 무엇을 설치해야 하는지 알려 주는 문구로 멈춘다(엉뚱한 ONNX 오류가 아니라).
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root);
        var dec = new AletheiaDecensor(new AppSettings { RuntimeRoot = t.Root }, env);
        using var img = SolidPage(64, 64);

        var ex = Assert.Throws<FileNotFoundException>(() => dec.Detect(img, null, out _, out _));

        Assert.Equal(AletheiaDecensor.DetectorMissing, ex.Message);
    }

    [Fact]
    public void Repair_WithoutInpainterModels_ThrowsInstallGuidance()
    {
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root, DetectorFile);
        var settings = new AppSettings { RuntimeRoot = t.Root };
        var dec = new AletheiaDecensor(settings, env);
        using var img = SolidPage(64, 64);
        using var mask = EmptyMask(img);
        Cv2.Rectangle(mask, new Rect(10, 10, 20, 8), White, -1);

        var ex = Assert.Throws<FileNotFoundException>(() => dec.Repair(img, mask, null, new LamaInpainter(env)));

        Assert.Equal(AletheiaDecensor.InpainterMissing, ex.Message);
    }

    [Theory]
    [InlineData(32, 64, 0)]   // 크기가 다른 마스크
    [InlineData(64, 64, 3)]   // 세 채널 마스크
    public void Repair_MaskNotMatchingImage_IsRejectedBeforeAnyModelRuns(int maskW, int maskH, int maskChannels)
    {
        // 성분 분류는 포인터로 마스크·라벨을 읽는다 — 형식이 다르면 모델을 열기 전에 막아야 한다.
        // (모델 파일은 이름만 있는 빈 자리표시라, 여기서 모델을 열려고 하면 테스트가 실패한다)
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root, BarFile, MosaicFile);
        var dec = new AletheiaDecensor(new AppSettings { RuntimeRoot = t.Root }, env);
        using var img = SolidPage(64, 64);
        using var mask = new Mat(maskH, maskW, maskChannels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC1, Scalar.All(255));

        var ex = Assert.Throws<ArgumentException>(() => dec.Repair(img, mask, null, new LamaInpainter(env)));

        Assert.Equal("mask", ex.ParamName);
    }
}
