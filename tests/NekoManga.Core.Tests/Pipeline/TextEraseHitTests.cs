using NekoManga.Core.Decensor;
using NekoManga.Core.Pipeline;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>글을 지운 자리가 검열로 잡힌 것을 걷어낸다(PipelineRunner.DropTextEraseHits).
/// 톤으로 채운 풍선·그림 위의 글은 LaMa로 지우는데, 그 자리가 뭉개져 감지기가 모자이크로 본다 — 검열 감지는 글을 지운 뒤의 원고에서 돈다.</summary>
public class TextEraseHitTests
{
    private static (Mat Mask, Mat Kinds) Maps(params (Rect Area, byte Kind)[] hits)
    {
        var mask = new Mat(600, 800, MatType.CV_8UC1, Scalar.Black);
        var kinds = new Mat(600, 800, MatType.CV_8UC1, Scalar.Black);
        foreach (var (area, kind) in hits)
        {
            Cv2.Rectangle(mask, area, Scalar.White, -1);
            Cv2.Rectangle(kinds, area, new Scalar(kind), -1);
        }
        return (mask, kinds);
    }

    [Fact]
    public void DropTextEraseHits_HitSittingOnErasedTextBlock_IsRemovedFromMaskAndKinds()
    {
        // 실측(2026-11-16): 글 블록 136×253에 잡힌 모자이크 161×274 — 검출의 87%가 블록 안, 블록의 94%를 덮음
        var block = new Rect(300, 200, 136, 253);
        var real = new Rect(600, 400, 30, 80);
        var (mask, kinds) = Maps((new Rect(288, 190, 161, 274), CensorKind.Mosaic), (real, CensorKind.Bar));
        using (mask) using (kinds)
        {
            int dropped = PipelineRunner.DropTextEraseHits(mask, kinds, new[] { block });

            Assert.Equal(1, dropped);
            Assert.Equal(real.Width * real.Height, Cv2.CountNonZero(mask));
            Assert.Equal(real.Width * real.Height, Cv2.CountNonZero(kinds));
            Assert.Equal(255, mask.At<byte>(440, 615));
        }
    }

    [Theory]
    [InlineData("bar-across-text")]    // 글 블록을 가로지르는 가는 막대 — 블록의 일부만 덮는다
    [InlineData("big-around-text")]    // 글 블록을 품은 넓은 검출 — 검출의 대부분이 블록 밖이다
    [InlineData("beside-text")]        // 글 블록 옆
    public void DropTextEraseHits_RealCensorNearText_IsKept(string layout)
    {
        var block = new Rect(300, 200, 136, 253);
        var hit = layout switch
        {
            "bar-across-text" => new Rect(280, 300, 180, 40),
            "big-around-text" => new Rect(200, 100, 400, 450),
            _ => new Rect(450, 200, 120, 250),
        };
        var (mask, kinds) = Maps((hit, CensorKind.Bar));
        using (mask) using (kinds)
        {
            Assert.Equal(0, PipelineRunner.DropTextEraseHits(mask, kinds, new[] { block }));
            Assert.Equal(hit.Width * hit.Height, Cv2.CountNonZero(mask));
        }
    }

    [Fact]
    public void DropTextEraseHits_NoBlocksOrNoKinds_LeavesMaskAlone()
    {
        var (mask, kinds) = Maps((new Rect(288, 190, 161, 274), CensorKind.Mosaic));
        using (mask) using (kinds)
        {
            Assert.Equal(0, PipelineRunner.DropTextEraseHits(mask, kinds, Array.Empty<Rect>()));
            Assert.Equal(1, PipelineRunner.DropTextEraseHits(mask, null, new[] { new Rect(300, 200, 136, 253) }));   // 종류 지도가 없어도 마스크는 걷는다
            Assert.Equal(0, Cv2.CountNonZero(mask));
        }
    }
}
