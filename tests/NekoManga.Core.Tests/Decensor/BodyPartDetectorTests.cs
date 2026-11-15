using NekoManga.Core.Engine;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>BodyPartDetector(deepghs anime_censor_detection, YOLOv8 detect)의 모델 없는 부분 — letterbox, 출력 디코드, NMS. 모델 추론은 테스트하지 않는다(규칙).</summary>
public class BodyPartDetectorTests
{
    /// <summary>[7, n] 채널 우선 출력(cx,cy,w,h,cls0,cls1,cls2)을 만든다 — 모델 출력 [1,7,8400]을 평면으로 편 꼴.</summary>
    private static float[] Tensor(params (float cx, float cy, float w, float h, float c0, float c1, float c2)[] rows)
    {
        int n = rows.Length; var t = new float[7 * n];
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            t[0 * n + i] = r.cx; t[1 * n + i] = r.cy; t[2 * n + i] = r.w; t[3 * n + i] = r.h;
            t[4 * n + i] = r.c0; t[5 * n + i] = r.c1; t[6 * n + i] = r.c2;
        }
        return t;
    }

    [Fact]
    public void Decode_SyntheticTensor_ReturnsBoxesInImageCoords()
    {
        // 640 입력 좌표 (cx 320, cy 200, w 100, h 60) — 세로 긴 그림을 scale 0.5, padY 80으로 넣었다면 원본 좌표는 (540, 180, 200, 120)... scale 0.5: x = (320-50-0)/0.5 = 540, y = (200-30-80)/0.5 = 180
        var t = Tensor(
            (320, 200, 100, 60, 0.01f, 0.5f, 0.02f),    // penis 0.5 → 남는다
            (100, 100, 20, 20, 0.01f, 0.05f, 0.03f),    // 최고 0.05 < 임계 → 버린다
            (500, 500, 40, 40, 0.9f, 0.01f, 0.01f));    // nipple_f 0.9 → 디코드는 돌려준다(호출자가 거른다)

        var dets = BodyPartDetector.Decode(t, n: 3, conf: 0.12f, scale: 0.5, padX: 0, padY: 80, imgW: 1280, imgH: 960);

        Assert.Equal(2, dets.Count);
        var penis = Assert.Single(dets, d => d.Cls == BodyPartDetector.ClsPenis);
        Assert.Equal(0.5f, penis.Conf, 3);
        Assert.Equal(new Rect(540, 180, 200, 120), penis.Box);
        Assert.Contains(dets, d => d.Cls == 0);
    }

    [Fact]
    public void Decode_BoxBeyondImage_ClampedInside()
    {
        var t = Tensor((5, 5, 40, 40, 0f, 0.8f, 0f));   // 왼쪽 위 밖으로 걸친 상자
        var dets = BodyPartDetector.Decode(t, 1, 0.12f, scale: 1, padX: 0, padY: 0, imgW: 640, imgH: 640);
        var d = Assert.Single(dets);
        Assert.Equal(0, d.Box.X); Assert.Equal(0, d.Box.Y);
        Assert.Equal(25, d.Box.Width); Assert.Equal(25, d.Box.Height);
    }

    [Fact]
    public void Nms_OverlappingSameClass_KeepsHigher()
    {
        var boxes = new[] { new Rect(100, 100, 100, 100), new Rect(105, 105, 100, 100) };
        var keep = BodyPartDetector.Nms(boxes, new[] { 0.3f, 0.9f }, 0.7f);
        Assert.Equal(new[] { 1 }, keep);
    }

    [Fact]
    public void Nms_DifferentClass_BothKept()
    {
        // 클래스별 NMS — Decode가 클래스마다 따로 부른다. 여기서는 겹치지 않는 상자 둘이 둘 다 남는 것만 본다
        var boxes = new[] { new Rect(0, 0, 50, 50), new Rect(300, 300, 50, 50) };
        var keep = BodyPartDetector.Nms(boxes, new[] { 0.5f, 0.6f }, 0.7f);
        Assert.Equal(2, keep.Count);
    }

    [Fact]
    public void Decode_SameClassOverlap_Suppressed_DifferentClassKept()
    {
        var t = Tensor(
            (320, 320, 100, 100, 0f, 0.9f, 0f),
            (322, 322, 100, 100, 0f, 0.6f, 0f),   // penis 겹침 → 눌린다
            (320, 320, 100, 100, 0f, 0f, 0.7f));  // pussy 같은 자리 → 다른 클래스라 남는다
        var dets = BodyPartDetector.Decode(t, 3, 0.12f, 1, 0, 0, 640, 640);
        Assert.Equal(2, dets.Count);
        Assert.Single(dets, d => d.Cls == BodyPartDetector.ClsPenis);
        Assert.Single(dets, d => d.Cls == BodyPartDetector.ClsPussy);
    }

    [Fact]
    public void Decode_LowerThreshold_DoesNotChangeWhichStrongBoxesSurvive()
    {
        // 재생성은 성별의 근거로 약한 상자(0.05)까지 받는다(RegenRegion.WeakConf) — 그래도 임계(0.12) 이상의 상자는 전과 똑같아야 한다(영역의 모양이 바뀌면 복원 결과가 바뀐다).
        // 점수 내림차순 NMS라 낮은 상자는 높은 상자를 지우지 못한다: 뚜렷한 상자에 거의 겹친 약한 같은 클래스 상자, 겹친 약한 다른 클래스 상자를 넣어 본다
        var t = Tensor(
            (300, 300, 100, 100, 0f, 0.70f, 0f),    // penis 0.70
            (302, 300, 100, 100, 0f, 0.08f, 0f),    // 거의 같은 자리의 penis 0.08 (IoU 0.96)
            (300, 300, 100, 100, 0f, 0f, 0.07f),    // 같은 자리의 pussy 0.07
            (500, 500, 80, 80, 0f, 0f, 0.30f),      // pussy 0.30
            (100, 500, 60, 60, 0f, 0.06f, 0f));     // 떨어진 penis 0.06
        var strong = BodyPartDetector.Decode(t, 5, 0.12f, 1, 0, 0, 640, 640);
        var all = BodyPartDetector.Decode(t, 5, 0.05f, 1, 0, 0, 640, 640);

        Assert.Equal(strong.OrderBy(d => d.Conf), all.Where(d => d.Conf >= 0.12f).OrderBy(d => d.Conf));
        Assert.Equal(2, strong.Count);
        Assert.Contains(all, d => d.Cls == BodyPartDetector.ClsPussy && d.Conf < 0.12f);    // 약한 상자도 돌아온다(근거로 쓴다)
        Assert.Contains(all, d => d.Cls == BodyPartDetector.ClsPenis && Math.Abs(d.Conf - 0.06f) < 1e-6);
    }

    [Fact]
    public void Letterbox_PortraitPage_PadsLeftRight()
    {
        using var page = new Mat(800, 400, MatType.CV_8UC3, new Scalar(10, 20, 30));   // 400×800 (B,G,R)
        var (input, scale, padX, padY) = BodyPartDetector.Letterbox(page);
        using (input)
        {
            Assert.Equal(0.8, scale, 6);
            Assert.Equal(160, padX); Assert.Equal(0, padY);
            Assert.Equal(new Size(640, 640), input.Size());
            Assert.Equal(MatType.CV_32FC3, input.Type());
            var mid = input.At<Vec3f>(320, 320);          // 그림 안: RGB 순서, /255
            Assert.Equal(30 / 255f, mid.Item0, 3); Assert.Equal(10 / 255f, mid.Item2, 3);
            var pad = input.At<Vec3f>(320, 10);           // 패딩: 114/255
            Assert.Equal(114 / 255f, pad.Item0, 3);
        }
    }
}
