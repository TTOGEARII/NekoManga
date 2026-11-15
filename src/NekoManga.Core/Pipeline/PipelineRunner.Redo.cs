using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

// 결과 창 [지우기] — 한 장만 검열 제거·식자를 다시 하는 길의 선택지와 순수 함수 (RedoPageAsync 본체는 Review.cs), 고친 장 묶음 다시 렌더링(RedoPagesAsync)
// 사용자 결정(2026-11-11): 수정 창은 채색을 돌리지 않는다. 그래서 채색된 장은 base(채색 뒤·식자 전)에서, 아니면 pre(검열 제거 전 원고)에서 지운다.
public partial class PipelineRunner
{
    /// <summary>RedoPageAsync의 선택지.
    /// <paramref name="DecensorAndLetterOnly"/>: 채색 없이 검열 제거·식자만 — 채색된 장은 채색 뒤 그림(base, 글자 없는 장은 결과물)에서, 아니면 pre에서 (<see cref="IsColorized"/>)
    /// (채색 생략 자체는 호출자가 <c>EnableAutoColor=false</c> 설정으로 러너를 만들어 정한다).
    /// <paramref name="Grow"/>: 저장 마스크를 이만큼(px) 부풀려 지운다. 0이면 파이프라인과 같은 마스크.</summary>
    public sealed record RedoPageOptions(bool DecensorAndLetterOnly = false, int Grow = 0);

    /// <summary>[고친 장 모두 다시 렌더링](2026-11-13 사용자 요청) — 장들을 차례로 다시 렌더링한다(RedoPageAsync). 한 장이 실패하면 그 장의 끝 줄(Error)로 알리고
    /// 나머지를 계속한다(실패한 장의 지난 결과물은 그대로다 — 결과물은 2차 끝에서만 쓴다). 취소는 묶음 전체를 멈춘다: 끝난 장은 끝난 대로, 남은 장은 손대지 않는다.
    /// 준비(모델 확인·ComfyUI 깨우기)는 묶음에 한 번만 한다 — 장마다 하면 엔진이 안 뜰 때 장 수만큼 기동 한도(4분)를 기다리고, 모델이 없으면 같은 실패가 장 수만큼 쌓인다(2026-11-13 검수).
    /// 준비에서 실패하면 어느 장도 손대지 않고 그 예외 하나가 올라간다. 작업 폴더도 묶음에 하나. 돌려주는 값은 성공·실패한 장 수.</summary>
    public async Task<(int Done, int Failed)> RedoPagesAsync(IReadOnlyList<(string File, string OutPath)> pages, IProgress<PageProgress> progress,
        CancellationToken ct, RedoPageOptions? options = null)
    {
        if (pages.Count == 0) return (0, 0);
        var cx = await PrepareAsync(pages.Select(p => p.File).ToList(), progress, needTranslator: false, ct);
        int done = 0, failed = 0;
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var (file, outPath) = pages[i];
                ct.ThrowIfCancellationRequested();
                try { await RedoPageAsync(file, progress, ct, outPath, options, cx, i); done++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed++; progress.Report(new(file, "실패", 100, Done: true, Error: ex.Message)); }
            }
        }
        finally { WorkFolders.Remove(cx.WorkDir); }   // 묶음의 작업 폴더 — 끝났든 멈췄든 중간 그림은 더 쓸 데가 없다
        return (done, failed);
    }

    /// <summary>채색 뒤 그림 후보(<paramref name="colored"/> — base, 글자 없는 장은 결과물)가 정말 채색된 그림인가. 2차가 사이드카에 적어 둔 값(<see cref="MaskSidecar.Colorized"/>)이
    /// 있으면 그대로 따르고, 적힌 것이 없는 옛 결과(v1.9.1까지)만 그림으로 가린다(<see cref="LooksColorized"/>). 다시 렌더링(RedoPageAsync)과 결과 창 미리보기가 같이 쓴다.</summary>
    public static bool IsColorized(MaskSidecar? sc, Mat pre, Mat colored, Mat? mask) => sc?.Colorized ?? LooksColorized(pre, colored, mask);

    /// <summary>채색으로 보는 채도 증가의 한계 — 실측(2026-11-13): 재생성 결과 6쪽 0.03~0.21, 재생성 조각 89개 최대 0.65, 채색 결과 17장 최소 12.1.</summary>
    internal const double ColorizedChromaGain = 3.0;

    /// <summary>그림이 pre(검열 제거 전 원고)에 색을 입힌 것으로 보이는가 — 크기가 다르거나, 마스크(255=지운 곳) **바깥**의 평균 채도(픽셀마다 B·G·R의 최대−최소)가
    /// pre보다 <see cref="ColorizedChromaGain"/> 넘게 늘었으면 그렇다. 채색 여부가 사이드카에 적히지 않은 옛 결과에만 쓴다(<see cref="IsColorized"/>).
    /// 2026-11-13까지는 바깥의 BGR 평균 절대차(한계 2.0)로 봤다: "검열 제거는 마스크 안만 바꾼다"는 전제였는데, 부위 통째 재생성은 마스크보다 넓게 다시 그려서
    /// 실제 재생성 결과 4쪽 중 3쪽(절대차 3.89·2.88·1.10·3.40)이 채색으로 읽혔다 — 그런 장을 다시 렌더링하면 이미 재생성된 그림 위에서 또 지웠다.
    /// 색을 입혔는지는 밝기 차이가 아니라 채도가 말해 준다. 원래 컬러인 원고는 pre도 채도가 높아 늘지 않는다.</summary>
    public static bool LooksColorized(Mat pre, Mat img, Mat? mask)
    {
        if (pre.Size() != img.Size() || pre.Channels() != img.Channels()) return true;
        if (pre.Channels() < 3) return false;   // 둘 다 한 채널 — 색이 없다
        Mat? outside = null;
        try
        {
            if (mask != null)
            {
                outside = new Mat();
                if (mask.Size() == pre.Size()) Cv2.BitwiseNot(mask, outside);
                else { using var rs = new Mat(); Cv2.Resize(mask, rs, pre.Size(), 0, 0, InterpolationFlags.Nearest); Cv2.BitwiseNot(rs, outside); }
            }
            return MeanChroma(img, outside) - MeanChroma(pre, outside) > ColorizedChromaGain;
        }
        finally { outside?.Dispose(); }
    }

    /// <summary>픽셀마다 B·G·R의 최대−최소(채도의 대용)를 <paramref name="where"/>(0 아닌 곳, null이면 전체)에서 평균한 값.</summary>
    private static double MeanChroma(Mat bgr, Mat? where)
    {
        var ch = Cv2.Split(bgr);
        try
        {
            using var hi = new Mat(); using var lo = new Mat(); using var chroma = new Mat();
            Cv2.Max(ch[0], ch[1], hi); Cv2.Max(hi, ch[2], hi);
            Cv2.Min(ch[0], ch[1], lo); Cv2.Min(lo, ch[2], lo);
            Cv2.Subtract(hi, lo, chroma);
            return where != null ? Cv2.Mean(chroma, where).Val0 : Cv2.Mean(chroma).Val0;
        }
        finally { foreach (var c in ch) c.Dispose(); }
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
