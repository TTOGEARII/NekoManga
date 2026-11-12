using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>부위 검출 상자 — Cls는 모델 클래스 번호(0 nipple_f, 1 penis, 2 pussy), Box는 원본 그림 좌표.</summary>
public sealed record BodyPart(int Cls, float Conf, Rect Box);

/// <summary>부위 감지기 — deepghs/anime_censor_detection v1.0_s(MIT, YOLOv8s detect, 만화·일러스트 학습)의 ONNX.
/// 재생성·커스텀 ComfyUI 검열 제거에서 검열 조각이 덮은 **부위 전체**를 다시 그리게, 조각과 겹치는 부위 상자를 찾는다(RegenRegion).
///
/// 입력: 쪽 전체를 640×640 letterbox(비율 유지, 회색 114 패딩, 가운데), RGB /255. 타일로 확대해 넣으면 오히려 나빴다(라벨 평가: 검은 막대 92%→46%) —
/// 모델이 인물 전체 맥락을 본다. 출력: [1, 4+3, 8400] (cx,cy,w,h, 클래스 점수 3 — 시그모이드 적용된 값, NMS 없음) → 클래스별 NMS IoU 0.7.
/// 가슴(nipple_f)은 쓰지 않는다(사용자 결정 2026-11-12) — Detect가 penis/pussy만 돌려준다.
/// 라벨 원고 144쪽 평가(2026-11-11, scratchpad censor_model/eval_detector.py): 임계 0.12에서 부위 상자가 검은 막대 94%·모자이크 79%·흰선 36%를 덮고, 상자 넓이는 조각의 중앙값 4.5배.</summary>
public class BodyPartDetector(OnnxEnv env)
{
    public const string ModelFile = "bodyparts.onnx";
    public const int ClsNipple = 0, ClsPenis = 1, ClsPussy = 2;
    private const int Size = 640;
    private const int NumClasses = 3;
    private const float NmsIou = 0.7f;
    private const byte PadGray = 114;

    public bool IsReady => env.ModelExists(ModelFile);

    /// <summary>쪽 전체에서 penis/pussy 상자(원본 좌표). conf 이하는 버린다. 모델이 없으면 예외(호출자가 IsReady를 먼저 본다).</summary>
    public IReadOnlyList<BodyPart> Detect(Mat bgr, float conf)
    {
        var (input, scale, padX, padY) = Letterbox(bgr);
        using (input)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, Size, Size });
            unsafe
            {
                float* p = (float*)input.DataPointer; long step = (long)input.Step() / sizeof(float);
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float* px = p + y * step + x * 3;   // Letterbox가 이미 RGB 순서·/255로 만들었다
                    tensor[0, 0, y, x] = px[0]; tensor[0, 1, y, x] = px[1]; tensor[0, 2, y, x] = px[2];
                }
            }
            // CPU 고정 — 0.3초짜리 모델이고, 이 모델의 DirectML 수치는 검증한 적이 없다(Aletheia 감지기는 DirectML에서 틀린 값을 냈다, decensor.md).
            // Guard를 쓰지 않는다: Guard는 먼저 GPU 세션을 만들어 버린다
            var session = env.Get(ModelFile, cpuOnly: true);
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor) });
            var outT = results.Select(o => o.AsTensor<float>()).First(t => t.Dimensions.Length == 3 && t.Dimensions[1] == 4 + NumClasses);
            int n = outT.Dimensions[2];
            var flat = outT.ToArray();   // row-major [7, n] (배치 1)
            return Decode(flat, n, conf, scale, padX, padY, bgr.Cols, bgr.Rows)
                .Where(d => d.Cls is ClsPenis or ClsPussy).ToList();
        }
    }

    /// <summary>비율을 지켜 640 안에 넣고 남는 곳은 회색(114)으로 가운데 패딩 — ultralytics letterbox와 같다. 결과는 CV_32FC3, RGB 순서, 0~1.</summary>
    public static (Mat Input, double Scale, int PadX, int PadY) Letterbox(Mat bgr)   // public: --bodytest 전처리 대조 덤프가 쓴다
    {
        double scale = Math.Min((double)Size / bgr.Cols, (double)Size / bgr.Rows);
        int nw = (int)Math.Round(bgr.Cols * scale), nh = (int)Math.Round(bgr.Rows * scale);
        int padX = (Size - nw) / 2, padY = (Size - nh) / 2;
        // 축소는 Area(안티에일리어싱) — 파이썬 평가(PIL bilinear는 축소 때 필터 폭을 넓힌다)와 맞추기 위해. 단순 Linear는 모자이크 같은 고주파 무늬가 깨져
        // 검출이 떨어졌다(C# 대조 2026-11-12: 모자이크 47% vs 파이썬 79%)
        using var resized = bgr.Resize(new Size(nw, nh), interpolation: scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, padY, Size - nh - padY, padX, Size - nw - padX, BorderTypes.Constant, new Scalar(PadGray, PadGray, PadGray));
        using var rgb = new Mat();
        Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);
        var input = new Mat();
        rgb.ConvertTo(input, MatType.CV_32FC3, 1.0 / 255);
        return (input, scale, padX, padY);
    }

    /// <summary>[7, n] 평면 출력 → 그림 좌표 상자. 클래스는 최고 점수, conf 미만은 버림, 패딩·배율 되돌림, 그림 안으로 자름, 클래스별 NMS.</summary>
    internal static List<BodyPart> Decode(ReadOnlySpan<float> output, int n, float conf, double scale, int padX, int padY, int imgW, int imgH)
    {
        var cand = new List<BodyPart>();
        for (int i = 0; i < n; i++)
        {
            int best = 0; float bestConf = output[4 * n + i];
            for (int c = 1; c < NumClasses; c++)
            {
                float v = output[(4 + c) * n + i];
                if (v > bestConf) { bestConf = v; best = c; }
            }
            if (bestConf < conf) continue;
            float cx = output[i], cy = output[n + i], w = output[2 * n + i], h = output[3 * n + i];
            double x1 = (cx - w / 2 - padX) / scale, y1 = (cy - h / 2 - padY) / scale;
            double x2 = (cx + w / 2 - padX) / scale, y2 = (cy + h / 2 - padY) / scale;
            int ix1 = Math.Clamp((int)Math.Round(x1), 0, imgW), iy1 = Math.Clamp((int)Math.Round(y1), 0, imgH);
            int ix2 = Math.Clamp((int)Math.Round(x2), 0, imgW), iy2 = Math.Clamp((int)Math.Round(y2), 0, imgH);
            if (ix2 - ix1 < 2 || iy2 - iy1 < 2) continue;
            cand.Add(new BodyPart(best, bestConf, new Rect(ix1, iy1, ix2 - ix1, iy2 - iy1)));
        }
        var result = new List<BodyPart>();
        foreach (var group in cand.GroupBy(d => d.Cls))
        {
            var list = group.ToList();
            foreach (int k in Nms(list.Select(d => d.Box).ToList(), list.Select(d => d.Conf).ToList(), NmsIou))
                result.Add(list[k]);
        }
        return result;
    }

    /// <summary>점수 내림차순 그리디 NMS — IoU가 iou 이상인 낮은 상자를 버린다. 남는 인덱스를 점수 순으로.</summary>
    internal static List<int> Nms(IReadOnlyList<Rect> boxes, IReadOnlyList<float> scores, float iou)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => scores[i]).ToList();
        var keep = new List<int>();
        var dead = new bool[boxes.Count];
        foreach (int i in order)
        {
            if (dead[i]) continue;
            keep.Add(i);
            foreach (int j in order)
            {
                if (dead[j] || j == i) continue;
                var inter = boxes[i] & boxes[j];
                double ia = inter.Width > 0 && inter.Height > 0 ? (double)inter.Width * inter.Height : 0;
                double union = (double)boxes[i].Width * boxes[i].Height + (double)boxes[j].Width * boxes[j].Height - ia;
                if (union > 0 && ia / union >= iou) dead[j] = true;
            }
        }
        return keep;
    }
}
