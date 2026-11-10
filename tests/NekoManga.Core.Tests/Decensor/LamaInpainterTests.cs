using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>LamaInpainter의 모델 없이 도는 부분 — 구멍 상자 → LaMa 입력 ROI 병합(MergeRois).</summary>
public class LamaInpainterTests
{
    [Theory]
    [InlineData(200, 200, 20, 10, 1000, 1000, 152, 152, 116, 106)]   // 작은 구멍: 사방 48px 맥락
    [InlineData(300, 300, 100, 20, 1000, 1000, 200, 200, 300, 220)]  // 긴 구멍: 긴 변만큼 맥락 → ROI ≈ 구멍의 3배
    [InlineData(10, 5, 20, 20, 500, 400, 0, 0, 78, 73)]               // 왼쪽 위 구석: 그림 밖은 잘라낸다
    [InlineData(480, 380, 20, 20, 500, 400, 432, 332, 68, 68)]        // 오른쪽 아래 구석
    public void MergeRois_SingleHole_GetsContextPaddingClampedToImage(
        int x, int y, int w, int h, int imW, int imH, int ex, int ey, int ew, int eh)
    {
        // LaMa는 512로 줄여 넣으므로 구멍이 ROI의 1/3 이하여야 주변색으로 잘 메운다.
        var rois = LamaInpainter.MergeRois([new Rect(x, y, w, h)], imW, imH);

        Assert.Equal(new Rect(ex, ey, ew, eh), Assert.Single(rois));
    }

    [Theory]
    [InlineData(160, 1)]   // 맥락 여백끼리 겹치는 가까운 구멍 → ROI 하나
    [InlineData(600, 2)]   // 멀리 떨어진 구멍 → 따로
    public void MergeRois_TwoHoles_MergeOnlyWhenPaddedRoisOverlap(int secondX, int expectedCount)
    {
        var a = new Rect(100, 100, 20, 20);
        var b = new Rect(secondX, 100, 20, 20);

        var rois = LamaInpainter.MergeRois([a, b], 1000, 1000);

        Assert.Equal(expectedCount, rois.Count);
        Assert.All(new[] { a, b }, hole => Assert.Contains(rois, r => (r & hole) == hole));
    }

    [Fact]
    public void MergeRois_MergedRoiCatchingAnotherBox_IsMergedAgain()
    {
        // 겹치는 것끼리 고정점까지 반복 병합한다 — A·B를 합친 ROI가 처음엔 누구와도 안 겹치던 D까지 덮게 되면 D도 합친다.
        var a = new Rect(100, 100, 20, 20);   // 여백 포함 52~168
        var b = new Rect(200, 200, 20, 20);   // 여백 포함 152~268
        var d = new Rect(240, 60, 20, 20);    // 여백 포함 x 192~308, y 12~128 — A와도 B와도 따로는 안 겹친다

        var rois = LamaInpainter.MergeRois([a, b, d], 1000, 1000);

        var roi = Assert.Single(rois);
        Assert.Equal(new Rect(52, 12, 256, 256), roi);
    }

    [Fact]
    public void MergeRois_NoHoles_ReturnsEmpty()
    {
        Assert.Empty(LamaInpainter.MergeRois([], 640, 480));
    }

    [Fact]
    public void MergeRois_ResultRois_AreDisjointAndInsideImage()
    {
        // 결과 ROI는 서로 겹치지 않고(같은 자리를 두 번 칠하지 않게) 그림 안에 있어야 잘라 쓸 수 있다.
        var holes = new List<Rect>
        {
            new(0, 0, 30, 30), new(60, 10, 10, 40), new(300, 300, 50, 10), new(330, 280, 10, 10),
            new(590, 450, 50, 30), new(500, 20, 20, 20), new(200, 420, 5, 5),
        };

        var rois = LamaInpainter.MergeRois(holes.ToList(), 640, 480);

        var image = new Rect(0, 0, 640, 480);
        Assert.All(rois, r => Assert.Equal(r, r & image));
        for (int i = 0; i < rois.Count; i++)
            for (int j = i + 1; j < rois.Count; j++)
                Assert.False(rois[i].IntersectsWith(rois[j]), $"{rois[i]}와 {rois[j]}가 겹친다");
        Assert.All(holes, hole => Assert.Contains(rois, r => (r & hole) == (hole & image)));
    }
}

/// <summary>NEKO_LAMA_MODEL 환경변수를 바꾸는 테스트 — 프로세스 전체에 보이는 값이라 다른 테스트와 동시에 돌리지 않는다.</summary>
[Collection(EnvCollection.Name)]
public class LamaInpainterModelOverrideTests
{
    private const string Var = "NEKO_LAMA_MODEL";

    [Theory]
    [InlineData(null, "lama_manga.onnx")]                 // 기본: 구성요소 설치기가 받는 만화판 모델
    [InlineData("", "lama_manga.onnx")]
    [InlineData("lama_fp32.onnx", "lama_fp32.onnx")]      // A/B 벤치용 옛 모델
    [InlineData("한글 모델 v2.onnx", "한글 모델 v2.onnx")]  // 파일 이름이면 한글·공백도 된다
    [InlineData("sub/lama_fp32.onnx", "lama_manga.onnx")] // 경로가 섞이면 무시 — models 밖을 가리킬 수 없다
    [InlineData("..\\lama_fp32.onnx", "lama_manga.onnx")]
    [InlineData("../../evil.onnx", "lama_manga.onnx")]
    public void ActiveModelFile_EnvOverride_AcceptsBareFileNameOnly(string? value, string expected)
    {
        var saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            Environment.SetEnvironmentVariable(Var, value);
            Assert.Equal(expected, LamaInpainter.ActiveModelFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }
}
