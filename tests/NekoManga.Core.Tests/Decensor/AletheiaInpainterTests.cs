using NekoManga.Core.Engine;
using OpenCvSharp;
using static NekoManga.Core.Tests.DecensorTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>AletheiaInpainter.Repair의 모델 앞단 — 입력 검사와 '고칠 곳 없음'. models 폴더는 비어 있다(모델 없이 끝나야 한다).</summary>
public class AletheiaInpainterTests
{
    [Theory]
    [InlineData("grayImage", "img")]     // 그림이 BGR 8비트가 아니다
    [InlineData("smallerMask", "mask")]  // 마스크가 그림보다 작다
    [InlineData("colorMask", "mask")]    // 마스크가 한 채널이 아니다
    [InlineData("floatMask", "mask")]
    public void Repair_InvalidInput_IsRejectedBeforePointerAccess(string kind, string param)
    {
        // 복원은 포인터로 직접 읽고 쓴다 — 형식·크기가 어긋나면 메모리를 망가뜨리므로 먼저 막아야 한다.
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root);
        var inpainter = new AletheiaInpainter(env);
        using var img = kind == "grayImage" ? new Mat(64, 64, MatType.CV_8UC1, Scalar.All(128)) : SolidPage(64, 64, gray: 128);
        using var mask = kind switch
        {
            "smallerMask" => new Mat(32, 32, MatType.CV_8UC1, Scalar.All(255)),
            "colorMask" => new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255)),
            "floatMask" => new Mat(64, 64, MatType.CV_32FC1, Scalar.All(1)),
            _ => new Mat(64, 64, MatType.CV_8UC1, Scalar.All(255)),
        };

        var ex = Assert.Throws<ArgumentException>(() => inpainter.Repair(img, mask, mosaic: false));

        Assert.Equal(param, ex.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repair_EmptyMask_ReturnsZeroWithoutLoadingModelOrTouchingImage(bool mosaic)
    {
        // 고칠 성분이 없으면 모델을 열 필요도 없다 — 모델이 설치되지 않은 환경에서도 그대로 돌아온다.
        using var t = new TempDir();
        using var env = EnvWithModelFiles(t.Root);
        var inpainter = new AletheiaInpainter(env);
        using var img = SolidPage(64, 48, gray: 90);
        Cv2.Rectangle(img, new Rect(5, 5, 20, 10), new Scalar(10, 200, 30), -1);
        using var before = img.Clone();
        using var mask = EmptyMask(img);

        int fixedCount = inpainter.Repair(img, mask, mosaic);

        Assert.Equal(0, fixedCount);
        using var diff = new Mat();
        Cv2.Absdiff(img, before, diff);
        using var diffFlat = diff.Reshape(1);
        Assert.Equal(0, Cv2.CountNonZero(diffFlat));
    }
}
