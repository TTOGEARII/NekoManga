using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

// 결과 창 [지우기] — 한 장만 검열 제거·식자를 다시 하는 길의 선택지와 순수 함수 (RedoPageAsync 본체는 Review.cs)
// 사용자 결정(2026-11-11): 수정 창은 채색을 돌리지 않는다. 그래서 채색된 장은 base(채색 뒤·식자 전)에서, 아니면 pre(검열 제거 전 원고)에서 지운다.
public partial class PipelineRunner
{
    /// <summary>RedoPageAsync의 선택지.
    /// <paramref name="DecensorAndLetterOnly"/>: 채색 없이 검열 제거·식자만 — 입력 그림은 <see cref="LooksColorized"/>로 base/pre 중 고른다
    /// (채색 생략 자체는 호출자가 <c>EnableAutoColor=false</c> 설정으로 러너를 만들어 정한다).
    /// <paramref name="Grow"/>: 저장 마스크를 이만큼(px) 부풀려 지운다. 0이면 파이프라인과 같은 마스크.</summary>
    public sealed record RedoPageOptions(bool DecensorAndLetterOnly = false, int Grow = 0);

    /// <summary>base(채색 뒤·식자 전)가 채색된 그림인가 — 크기가 다르거나, 마스크(255=지운 곳) **바깥**의 BGR 평균 절대차가 2.0을 넘으면 그렇다.
    /// 검열 제거는 마스크 안만 바꾸므로 바깥이 같으면 채색 안 한 장이다. 한계 2.0: 같은 PNG를 다시 읽으면 0, 채색은 거의 모든 픽셀이 바뀌어 수십이다.</summary>
    public static bool LooksColorized(Mat pre, Mat baseImg, Mat? mask)   // public: 결과 창 미리보기가 실제 2차와 같은 그림(pre/base)을 고르는 데 쓴다
    {
        if (pre.Size() != baseImg.Size() || pre.Channels() != baseImg.Channels()) return true;
        using var diff = new Mat();
        Cv2.Absdiff(pre, baseImg, diff);
        Mat? outside = null;
        try
        {
            if (mask != null)
            {
                outside = new Mat();
                if (mask.Size() == pre.Size()) Cv2.BitwiseNot(mask, outside);
                else { using var rs = new Mat(); Cv2.Resize(mask, rs, pre.Size(), 0, 0, InterpolationFlags.Nearest); Cv2.BitwiseNot(rs, outside); }
            }
            var mean = outside != null ? Cv2.Mean(diff, outside) : Cv2.Mean(diff);
            double avg = (mean.Val0 + mean.Val1 + mean.Val2) / Math.Max(1, Math.Min(3, pre.Channels()));
            return avg > 2.0;
        }
        finally { outside?.Dispose(); }
    }

    /// <summary>마스크를 grow(px)만큼 타원 커널로 부풀린 새 Mat (호출자가 해제). grow ≤ 0이면 사본 그대로.</summary>
    public static Mat Grown(Mat mask, int grow)   // 결과 창의 '결과물 제자리 지우기'도 쓴다
    {
        if (grow <= 0) return mask.Clone();
        var g = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1));
        Cv2.Dilate(mask, g, kernel);
        return g;
    }
}
