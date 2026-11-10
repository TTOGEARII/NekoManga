using NekoManga.Core.Engine;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>CensorNet — 모델 없이 볼 수 있는 것은 준비 여부뿐이다(검출·보강 도우미는 전부 private이다).</summary>
public class CensorNetTests
{
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "censor_seg.onnx" }, true)]    // 구성요소 설치기가 받는 RuntimeRoot\models\censor_seg.onnx
    [InlineData(new[] { "censor_seg.pt" }, false)]     // 이름이 비슷한 다른 파일은 모델이 아니다
    public void IsReady_FollowsInstalledModelFile(string[] files, bool expected)
    {
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root, files);

        Assert.Equal(expected, new CensorNet(env).IsReady);
    }
}
