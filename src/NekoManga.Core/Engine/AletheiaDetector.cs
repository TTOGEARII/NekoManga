using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>Aletheia Lens(https://github.com/Cec1c/Aletheia-Lens, GPL-3.0)의 검열 감지기 이식 — hent-AI의 Matterport Mask R-CNN
/// (ResNet101+FPN, classes: 1=bar 막대·단색 덮기, 2=mosaic)을 tf2onnx로 내보낸 `models/mrcnn/weights.onnx`(254MB)를 그대로 쓴다.
/// 원본 `detector.py`의 전처리·후처리를 줄 단위로 옮겼다 (2026-11-09):
///   - 전처리: 긴 변 1024 정사각(짧은 변 800까지 확대, 긴 변 1024 초과면 축소) → 0 패딩(가운데) → RGB − MEAN_PIXEL,
///     image_meta 15칸, 정규화 앵커 261,888개(5단 피라미드 × 비율 3).
///   - 후처리: 검출 [100,6](y1,x1,y2,x2,class,score; 그래프 안에서 conf 0.75·NMS 0.3 적용 완료) → 창(window) 기준으로 원본 좌표 환산 →
///     28×28 마스크를 상자 크기로 쌍선형 확대해 0.5 이상을 채택.
/// 페이지 전체를 한 번에 넣는다(타일 없음 — 원본과 동일).
///
/// **단계 실행 (같은 날 후속, 사용자 "원본처럼 빠르게")**: 모델을 통째로 돌리면 CPU에서 쪽당 ~8.5초인데, 그중 ~5.5초가 ROI 정렬 Loop
/// (tf2onnx가 crop_and_resize를 ROI 1000개마다 특징 맵 전체(64MB)를 Slice하는 Loop로 풀어 놓음)이고, 그 Loop의 Resize는 DirectML에서 틀린 값을 낸다
/// (백본·RPN 출력은 DirectML에서도 CPU와 1e-5 안으로 맞는데 헤드 출력이 통째로 어긋난다). 그래서 모델 파일을 네 조각으로 잘라
/// (<see cref="OnnxGraphFile"/>, 첫 실행 때 `aletheia_mrcnn.split\`에 생성) ROI 정렬만 C#으로 계산한다:
///   a 백본+FPN+RPN [GPU] → r 제안(ROI)·수준별 상자 [CPU] → **C# ROI 정렬 7×7** → c 분류 헤드 [GPU] → d 검출층·마스크용 상자 [CPU]
///   → **C# ROI 정렬 14×14** → m 마스크 헤드 [GPU].
/// ROI 정렬은 ONNX Runtime의 Resize(linear, tf_crop_and_resize, extrapolation 0)와 같은 식·같은 연산 순서로 계산한다.
/// 실측(RTX 2070 SUPER, 11쪽): 통짜 CPU 8.5초 → 단계 실행 CPU 3.2초(원본과 비트 동일) → GPU **0.24초**(상자 동일, 점수 차 2e-6, 마스크 128만 px 중 1px).
/// GPU는 프로세스마다 첫 장에서 CPU 결과와 한 번 대조한 뒤에만 쓴다(<see cref="_gpuVerified"/>) — 어긋나거나 오류가 나면 CPU로 실행한다.
/// 자르기에 실패하면(그래프 구조가 예상과 다름) 통짜 모델 경로로 돌아간다. `NEKO_ALETHEIA_SINGLE=1` = 통짜 경로 강제, `NEKO_ALETHEIA_CPU=1` = 전 단계 CPU.</summary>
public class AletheiaDetector(OnnxEnv env)
{
    public const string ModelFile = "aletheia_mrcnn.onnx";
    public const int ClassBar = 1, ClassMosaic = 2;

    private const int MinDim = 800, MaxDim = 1024;
    private const int MaxInstances = 100, MaskSize = 28, NumClasses = 3, MetaSize = 15;
    private static readonly double[] MeanPixel = [123.7, 116.8, 103.9];   // RGB — 원본처럼 float64로 뺀 뒤 float32로 내린다
    private static readonly int[] BackboneStrides = [4, 8, 16, 32, 64];
    private static readonly int[] AnchorScales = [32, 64, 128, 256, 512];
    private static readonly double[] AnchorRatios = [0.5, 1, 2];

    public bool IsReady => env.ModelExists(ModelFile);

    /// <summary>검출 하나. Mask는 Box 크기의 CV_8UC1(255=검열) — Box는 이미지 안으로 잘린 상자.</summary>
    public sealed record Instance(int Cls, float Score, Rect Box, Mat Mask) : IDisposable
    {
        public void Dispose() => Mask.Dispose();
    }

    /// <summary>img(BGR 8UC3)에서 막대·모자이크 인스턴스를 찾는다. 돌려준 인스턴스(의 Mask)는 호출자가 해제한다.</summary>
    public List<Instance> Detect(Mat imgBgr)
    {
        int h = imgBgr.Rows, w = imgBgr.Cols;

        // ── mold_inputs: resize_image(min_dim 800, max_dim 1024, mode "square") ──
        double scale = Math.Max(1.0, (double)MinDim / Math.Min(h, w));
        if (Math.Round(Math.Max(h, w) * scale) > MaxDim) scale = (double)MaxDim / Math.Max(h, w);
        Mat resized = imgBgr;
        if (scale != 1.0)
        {
            resized = new Mat();
            Cv2.Resize(imgBgr, resized, new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale)), 0, 0, InterpolationFlags.Linear);
        }
        int rh = resized.Rows, rw = resized.Cols;
        int top = (MaxDim - rh) / 2, left = (MaxDim - rw) / 2;

        // 패딩(0)도 평균을 빼므로 빈자리는 −MEAN_PIXEL이다
        var image = new float[MaxDim * MaxDim * 3];
        float pr = (float)-MeanPixel[0], pg = (float)-MeanPixel[1], pb = (float)-MeanPixel[2];
        for (int i = 0; i < image.Length; i += 3)
        {
            image[i] = pr; image[i + 1] = pg; image[i + 2] = pb;
        }
        unsafe
        {
            byte* ps = (byte*)resized.DataPointer; long step = resized.Step();
            for (int y = 0; y < rh; y++)
            {
                byte* row = ps + y * step;
                int o = ((y + top) * MaxDim + left) * 3;
                for (int x = 0; x < rw; x++, o += 3)
                {
                    image[o] = (float)(row[x * 3 + 2] - MeanPixel[0]);       // R
                    image[o + 1] = (float)(row[x * 3 + 1] - MeanPixel[1]);   // G
                    image[o + 2] = (float)(row[x * 3] - MeanPixel[2]);       // B
                }
            }
        }
        if (!ReferenceEquals(resized, imgBgr)) resized.Dispose();

        // compose_image_meta: [id, 원본 (h,w,3), 입력 (1024,1024,3), window(y1,x1,y2,x2), scale, active_class_ids×3]
        var meta = new float[MetaSize];
        meta[1] = h; meta[2] = w; meta[3] = 3;
        meta[4] = MaxDim; meta[5] = MaxDim; meta[6] = 3;
        meta[7] = top; meta[8] = left; meta[9] = top + rh; meta[10] = left + rw;
        meta[11] = (float)scale;

        var plan = Environment.GetEnvironmentVariable("NEKO_ALETHEIA_SINGLE") == "1" ? null : EnsureSplit();
        float[] det, masks;
        if (plan == null) (det, masks) = RunSingle(image, meta);
        else
        {
            try { (det, masks) = RunStaged(plan, image, meta); }
            catch (Exception ex) when (ex is OnnxRuntimeException or InvalidDataException or KeyNotFoundException)
            {
                // 단계 파일이 깨졌거나(백신·디스크 오류) 예상과 다른 그래프 — 다음 실행 때 다시 만들게 표식을 지우고, 이번에는 통짜 모델로
                InvalidateSplit(ex);
                (det, masks) = RunSingle(image, meta);
            }
        }

        // ── unmold_detections ──
        int n = MaxInstances;
        for (int i = 0; i < MaxInstances; i++)
            if (det[i * 6 + 4] == 0) { n = i; break; }

        // window를 입력(1024²) 기준으로 정규화 — norm_boxes는 float32를 돌려준다 (원본과 같은 정밀도로 계산)
        float wy1 = (float)(top / (double)(MaxDim - 1)), wx1 = (float)(left / (double)(MaxDim - 1));
        float wy2 = (float)((top + rh - 1) / (double)(MaxDim - 1)), wx2 = (float)((left + rw - 1) / (double)(MaxDim - 1));
        float wh = wy2 - wy1, ww = wx2 - wx1;

        var list = new List<Instance>();
        var page = new Rect(0, 0, w, h);
        var buf = new float[MaskSize * MaskSize];
        for (int i = 0; i < n; i++)
        {
            int cls = (int)det[i * 6 + 4];
            float score = det[i * 6 + 5];
            float ny1 = (det[i * 6] - wy1) / wh, nx1 = (det[i * 6 + 1] - wx1) / ww;
            float ny2 = (det[i * 6 + 2] - wy1) / wh, nx2 = (det[i * 6 + 3] - wx1) / ww;
            // denorm_boxes: around(box * (h-1, w-1, h-1, w-1) + (0,0,1,1)) — np.around는 짝수 반올림(Math.Round 기본값과 같다)
            int y1 = (int)Math.Round((double)ny1 * (h - 1)), x1 = (int)Math.Round((double)nx1 * (w - 1));
            int y2 = (int)Math.Round((double)ny2 * (h - 1) + 1), x2 = (int)Math.Round((double)nx2 * (w - 1) + 1);
            if ((y2 - y1) * (x2 - x1) <= 0 || y2 <= y1) continue;
            if (cls <= 0 || cls >= NumClasses) continue;

            // unmold_mask: 28×28 → 상자 크기(쌍선형) → 0.5 이상
            for (int k = 0; k < buf.Length; k++) buf[k] = masks[(i * MaskSize * MaskSize + k) * NumClasses + cls];
            using var small = new Mat(MaskSize, MaskSize, MatType.CV_32FC1);
            Marshal.Copy(buf, 0, small.Data, buf.Length);
            int bw = x2 - x1, bh = y2 - y1;
            using var big = new Mat();
            Cv2.Resize(small, big, new Size(bw, bh), 0, 0, InterpolationFlags.Linear);

            // 원본은 상자가 이미지 안이라고 가정한다(검출층이 window로 자른다) — 반올림으로 1px 넘는 경우에 대비해 잘라서 담는다
            var box = new Rect(x1, y1, bw, bh) & page;
            if (box.Width <= 0 || box.Height <= 0) continue;
            var m = new Mat(box.Height, box.Width, MatType.CV_8UC1);
            unsafe
            {
                for (int y = 0; y < box.Height; y++)
                {
                    float* src = (float*)big.Ptr(y + box.Y - y1) + (box.X - x1);
                    byte* dst = (byte*)m.Ptr(y);
                    for (int x = 0; x < box.Width; x++) dst[x] = src[x] >= 0.5f ? (byte)255 : (byte)0;
                }
            }
            list.Add(new Instance(cls, score, box, m));
        }
        return list;
    }

    // ── 통짜 모델 경로 (자르기에 실패했을 때의 폴백 — 느리지만 원본 그대로) ──
    private (float[] Det, float[] Masks) RunSingle(float[] image, float[] meta)
    {
        var anchors = Anchors.Value;
        var session = env.Get(ModelFile, cpuOnly: true);   // 통짜 그래프는 DirectML에서 헤드 출력이 틀린다 — CPU 고정
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_image", new DenseTensor<float>(image, new[] { 1, MaxDim, MaxDim, 3 })),
            NamedOnnxValue.CreateFromTensor("input_image_meta", new DenseTensor<float>(meta, new[] { 1, MetaSize })),
            NamedOnnxValue.CreateFromTensor("input_anchors", new DenseTensor<float>(anchors, new[] { 1, anchors.Length / 4, 4 })),
        }, new[] { "mrcnn_detection", "mrcnn_mask" });
        // 평탄 버퍼로 읽는다 (LamaInpainter와 같은 이유 — 인덱서가 strided 접근을 보장하지 않는다)
        return (results.First(r => r.Name == "mrcnn_detection").AsEnumerable<float>().ToArray(),    // [1,100,6]
                results.First(r => r.Name == "mrcnn_mask").AsEnumerable<float>().ToArray());        // [1,100,28,28,3]
    }

    // ── 단계 실행 ──
    private const string SplitDir = "aletheia_mrcnn.split";
    private const int SplitVersion = 3;
    private const int ClassifierPool = 7, MaskPool = 14;   // Matterport POOL_SIZE / MASK_POOL_SIZE

    /// <summary>ROI 정렬 Loop 하나 자리: 어느 특징 맵에서 어느 상자들을 잘라 어느 텐서 이름으로 내놓는가.</summary>
    private sealed record RoiLoop(bool Mask, string Feature, string Boxes, string Output);
    private sealed record SplitPlan(RoiLoop[] Loops);

    private static readonly object SplitLock = new();
    private static SplitPlan? _plan;
    private static string? _planFor;       // 어느 원본 파일(경로|길이|수정 시각) 기준으로 만든 계획인지
    private static bool _splitFailed;

    /// <summary>단계별 모델 파일이 원본과 맞게 준비돼 있는지 보고, 없으면 만든다(원본 254MB를 읽어 네 파일 ~250MB를 쓴다 — 첫 실행 한 번, 몇 초).</summary>
    private SplitPlan? EnsureSplit()
    {
        lock (SplitLock)
        {
            var src = env.ModelPath(ModelFile);
            var fi = new FileInfo(src);
            string stamp = $"{src}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|v{SplitVersion}";
            if (_plan != null && _planFor == stamp) return _plan;
            if (_splitFailed && _planFor == stamp) return null;
            _planFor = stamp; _plan = null; _splitFailed = false;

            string dir = env.ModelPath(SplitDir), manifest = Path.Combine(dir, "manifest.tsv");
            try
            {
                if (File.Exists(manifest))
                {
                    var lines = File.ReadAllLines(manifest);
                    if (lines.Length > 0 && lines[0] == "src\t" + stamp && "arcdm".All(c => File.Exists(Path.Combine(dir, c + ".onnx"))))
                    {
                        var loops = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p.Length == 5 && p[0] == "loop")
                                         .Select(p => new RoiLoop(p[1] == "mask", p[2], p[3], p[4])).ToArray();
                        if (loops.Length == 8) return _plan = new SplitPlan(loops);
                    }
                }

                var g = OnnxGraphFile.Load(src);
                var found = new List<RoiLoop>();
                foreach (var nd in g.Nodes.Where(nd => nd.OpType == "Loop" && nd.Name.Contains("/CropAndResize")))
                {
                    bool mask = nd.Name.Contains("roi_align_mask");
                    if (!mask && !nd.Name.Contains("roi_align_classifier")) throw new InvalidDataException("알 수 없는 ROI 정렬 Loop: " + nd.Name);
                    found.Add(new RoiLoop(mask,
                        nd.Implicit.Single(t => t.Contains("/fpn_p") && t.EndsWith("/BiasAdd:0")),
                        nd.Implicit.Single(t => t.Contains("/GatherNd")),
                        nd.Outputs.Single()));
                }
                if (found.Count(l => !l.Mask) != 4 || found.Count(l => l.Mask) != 4)
                    throw new InvalidDataException($"ROI 정렬 Loop 수가 예상(분류 4·마스크 4)과 달라요: {found.Count}");
                var features = found.Select(l => l.Feature).Distinct().ToArray();
                var cls = found.Where(l => !l.Mask).ToArray(); var msk = found.Where(l => l.Mask).ToArray();

                Directory.CreateDirectory(dir);
                File.Delete(manifest);   // 쓰다 멈추면 다음에 처음부터
                g.Extract(Path.Combine(dir, "a.onnx"), [.. features, "rpn_class", "rpn_bbox"], []);
                g.Extract(Path.Combine(dir, "r.onnx"), ["ROI", .. cls.Select(l => l.Boxes)], ["rpn_class", "rpn_bbox"]);
                // 특징 맵도 멈춤 목록에 넣는다 — Loop 출력을 다시 모양 잡는 Reshape가 특징 맵의 Shape를 참조해서, 빼면 백본 전체가 뒤 단계에 딸려 들어가
                // 단계마다 다시 계산된다(실측: 뒤 두 조각이 239MB·194MB, 쪽당 5.9초).
                // 분류 헤드(c)와 검출층(d)을 따로 둔다 — 검출층(클래스별 NMS Loop)은 DirectML에서 검출 0개 + 접근 위반으로 죽는다(실측), 분류 헤드는 합성곱이라 GPU 후보.
                g.Extract(Path.Combine(dir, "c.onnx"), ["mrcnn_class", "mrcnn_bbox"], ["ROI", .. cls.Select(l => l.Output), .. features]);
                g.Extract(Path.Combine(dir, "d.onnx"), ["mrcnn_detection", .. msk.Select(l => l.Boxes)], ["ROI", "mrcnn_class", "mrcnn_bbox", .. features]);
                g.Extract(Path.Combine(dir, "m.onnx"), ["mrcnn_mask"], ["mrcnn_detection", .. msk.Select(l => l.Output), .. features]);
                File.WriteAllLines(manifest, new[] { "src\t" + stamp }.Concat(found.Select(l => $"loop\t{(l.Mask ? "mask" : "classifier")}\t{l.Feature}\t{l.Boxes}\t{l.Output}")));
                return _plan = new SplitPlan(found.ToArray());
            }
            catch (Exception ex)
            {
                _splitFailed = true;
                if (Environment.GetEnvironmentVariable("NEKO_ONNX_DBG") == "1")
                    Console.Error.WriteLine($"[aletheia] 단계 모델 준비 실패 — 통짜 모델로 돌아가요: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>GPU(DirectML)에 올리는 단계 — 합성곱뿐인 백본(a)·분류 헤드(c)·마스크 헤드(m). 제안(r)·검출층(d)은 TopK·NMS·Loop라 CPU에 둔다
    /// (검출층은 DirectML에서 검출 0개 + 접근 위반으로 죽는 것을 실측).</summary>
    private static readonly string[] GpuStages = ["a", "c", "m"];

    private static readonly object GpuLock = new();
    /// <summary>이 PC의 GPU 결과를 믿어도 되는가 — null = 아직 확인 전, true = CPU와 맞음, false = GPU 불가·불일치·오류(이후 CPU만).
    /// DirectML은 그래픽카드·드라이버에 따라 같은 그래프가 다른 값을 낼 수 있어(통짜 그래프가 실제로 그랬다) 프로세스마다 첫 장에서 한 번 대조한다.</summary>
    private static bool? _gpuVerified;

    private static void Dbg(string message)
    {
        if (Environment.GetEnvironmentVariable("NEKO_ONNX_DBG") == "1") Console.Error.WriteLine("[aletheia] " + message);
    }

    private void InvalidateSplit(Exception ex)
    {
        lock (SplitLock)
        {
            _plan = null; _splitFailed = true;
            try { File.Delete(Path.Combine(env.ModelPath(SplitDir), "manifest.tsv")); } catch { }
        }
        Dbg("단계 실행 실패 — 통짜 모델로 돌아가요: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
    }

    /// <summary>GPU를 못 쓰게 됐을 때 DirectML 세션을 닫는다 (백본 180MB 등이 VRAM에 남지 않게). 감지기만 쓰는 세션이라 바로 닫아도 된다.</summary>
    private void DropGpuSessions()
    {
        foreach (var s in GpuStages) env.Release($"{SplitDir}/{s}.onnx", cpuOnly: false);
    }

    private (float[] Det, float[] Masks) RunStaged(SplitPlan plan, float[] image, float[] meta)
    {
        // GpuAllowed = 설정(OnnxGpu)·NEKO_FORCE_CPU — ComfyUI를 쓰는 작업에서는 false라 이번 실행만 CPU로 돈다(판정 _gpuVerified는 건드리지 않는다)
        bool wantGpu = Environment.GetEnvironmentVariable("NEKO_ALETHEIA_CPU") != "1" && env.GpuAllowed;
        while (true)
        {
            if (!wantGpu || _gpuVerified == false) { var r = RunStagedOn(plan, image, meta, gpu: false); return (r.Det, r.Masks); }
            if (_gpuVerified == true)
            {
                try { var r = RunStagedOn(plan, image, meta, gpu: true); return (r.Det, r.Masks); }
                catch (OnnxRuntimeException ex)
                {
                    _gpuVerified = false;   // 실행 중 GPU 오류(드라이버 재시작·VRAM 부족 등) — 이후 CPU로
                    Dbg("GPU 실행 실패 — CPU로 돌아가요: " + ex.Message.Split('\n')[0]);
                    DropGpuSessions();
                    continue;
                }
            }
            lock (GpuLock)
            {
                if (_gpuVerified != null) continue;
                if (!GpuStages.All(s => env.UsesGpu($"{SplitDir}/{s}.onnx"))) { _gpuVerified = false; Dbg("DirectML 세션을 만들 수 없어 CPU로 실행해요"); continue; }
                // 첫 장: CPU와 GPU로 한 번씩 돌려 대조한다. 이 장의 결과는 CPU 것을 쓴다.
                var cpu = RunStagedOn(plan, image, meta, gpu: false);
                try
                {
                    var gpu = RunStagedOn(plan, image, meta, gpu: true);
                    _gpuVerified = Agree(cpu, gpu, out string why);
                    Dbg(_gpuVerified == true ? "GPU 결과가 CPU와 일치 — 이후 GPU로 실행해요 (" + why + ")" : "GPU 결과가 CPU와 달라 CPU로 실행해요 (" + why + ")");
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException)
                {
                    _gpuVerified = false;
                    Dbg("GPU 실행 실패 — CPU로 실행해요: " + ex.Message.Split('\n')[0]);
                }
                if (_gpuVerified == true)
                    foreach (var s in GpuStages) env.Release($"{SplitDir}/{s}.onnx", cpuOnly: true);   // 대조에만 쓴 CPU 세션(백본 180MB 등)을 닫는다
                else DropGpuSessions();
                return (cpu.Det, cpu.Masks);
            }
        }
    }

    /// <summary>GPU 결과가 CPU 결과와 같은 검출인가: 분류 확률(ROI 1000개 전부 — 검출이 없는 장에서도 비교된다) 최대 차 0.02 이하,
    /// 검출 수·클래스 동일, 상자 좌표 차 2px(1024 기준) 이하, 점수 차 0.02 이하, 마스크 평균 차 0.01 이하.
    /// 실측(RTX 2070 SUPER, 11쪽): 상자 동일·점수 차 2e-6·마스크 128만 px 중 1px.</summary>
    private static bool Agree(StageResult cpu, StageResult gpu, out string why)
    {
        double maxProb = 0;
        if (cpu.ClassProbs.Length != gpu.ClassProbs.Length) { why = "분류 출력 크기가 다름"; return false; }
        for (int i = 0; i < cpu.ClassProbs.Length; i++) maxProb = Math.Max(maxProb, Math.Abs(cpu.ClassProbs[i] - gpu.ClassProbs[i]));
        if (!(maxProb <= 0.02)) { why = $"분류 확률 최대 차 {maxProb:G3}"; return false; }

        static int Count(float[] d) { int n = 0; while (n < MaxInstances && d[n * 6 + 4] != 0) n++; return n; }
        int nc = Count(cpu.Det), ng = Count(gpu.Det);
        if (nc != ng) { why = $"검출 수 {nc} vs {ng}"; return false; }
        double maxBox = 0, maxScore = 0, maskSum = 0;
        for (int i = 0; i < nc; i++)
        {
            if (cpu.Det[i * 6 + 4] != gpu.Det[i * 6 + 4]) { why = $"검출 {i}의 클래스가 다름"; return false; }
            for (int k = 0; k < 4; k++) maxBox = Math.Max(maxBox, Math.Abs(cpu.Det[i * 6 + k] - gpu.Det[i * 6 + k]));
            maxScore = Math.Max(maxScore, Math.Abs(cpu.Det[i * 6 + 5] - gpu.Det[i * 6 + 5]));
            int o = i * MaskSize * MaskSize * NumClasses, len = MaskSize * MaskSize * NumClasses;
            for (int k = 0; k < len; k++) maskSum += Math.Abs(cpu.Masks[o + k] - gpu.Masks[o + k]);
        }
        double maskMean = nc > 0 ? maskSum / ((double)nc * MaskSize * MaskSize * NumClasses) : 0;
        why = $"검출 {nc}, 분류 확률 차 {maxProb:G3}, 상자 차 {maxBox:G3}, 점수 차 {maxScore:G3}, 마스크 평균 차 {maskMean:G3}";
        return maxBox <= 2.0 / MaxDim && maxScore <= 0.02 && maskMean <= 0.01;
    }

    private sealed record StageResult(float[] Det, float[] Masks, float[] ClassProbs);

    private StageResult RunStagedOn(SplitPlan plan, float[] image, float[] meta, bool gpu)
    {
        // GPU 세션 실행은 이 환경의 다른 GPU 추론(LaMa·CensorNet 등, OnnxEnv.Guard)과 겹치지 않게 한 줄로 세운다
        if (!gpu) return RunStagedCore(plan, image, meta, gpu: false);
        lock (env.GpuSync) return RunStagedCore(plan, image, meta, gpu: true);
    }

    private StageResult RunStagedCore(SplitPlan plan, float[] image, float[] meta, bool gpu)
    {
        var sA = env.Get(SplitDir + "/a.onnx", cpuOnly: !gpu);
        var sR = env.Get(SplitDir + "/r.onnx", cpuOnly: true);
        var sC = env.Get(SplitDir + "/c.onnx", cpuOnly: !gpu);
        var sD = env.Get(SplitDir + "/d.onnx", cpuOnly: true);
        var sM = env.Get(SplitDir + "/m.onnx", cpuOnly: !gpu);

        var anchors = Anchors.Value;
        var pool = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
        var owned = new List<IDisposable>();
        var nhwc = new Dictionary<string, (float[] Data, int H, int W, int C)>(StringComparer.Ordinal);
        using var runOptions = new RunOptions();
        try
        {
            void Feed(string name, float[] data, long[] shape)
            {
                // 빈 텐서(그 수준에 ROI가 하나도 없을 때)는 고정할 배열이 없으므로 ORT가 할당하게 한다
                var v = data.Length > 0 ? OrtValue.CreateTensorValueFromMemory(data, shape)
                                        : OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, shape);
                owned.Add(v); pool[name] = v;
            }
            void Run(InferenceSession s, string[] outputs)
            {
                var names = s.InputNames.ToArray();
                var res = s.Run(runOptions, names, names.Select(nm => pool[nm]).ToArray(), outputs);
                owned.Add(res);
                for (int i = 0; i < outputs.Length; i++) pool[outputs[i]] = res[i];
            }
            void Align(bool mask)
            {
                foreach (var loop in plan.Loops.Where(l => l.Mask == mask))
                {
                    int crop = mask ? MaskPool : ClassifierPool;
                    var boxes = pool[loop.Boxes];
                    int nBoxes = (int)boxes.GetTensorTypeAndShape().Shape[0];
                    if (!nhwc.TryGetValue(loop.Feature, out var f) && nBoxes > 0) nhwc[loop.Feature] = f = ToNhwc(pool[loop.Feature]);
                    int ch = nBoxes > 0 ? f.C : (int)pool[loop.Feature].GetTensorTypeAndShape().Shape[1];
                    var pooled = nBoxes > 0 ? CropAndResize(f.Data, f.H, f.W, f.C, boxes.GetTensorDataAsSpan<float>(), nBoxes, crop) : [];
                    Feed(loop.Output, pooled, [nBoxes, crop, crop, ch]);
                }
            }

            Feed("input_image", image, [1, MaxDim, MaxDim, 3]);
            Feed("input_image_meta", meta, [1, MetaSize]);
            Feed("input_anchors", anchors, [1, anchors.Length / 4, 4]);

            var cls = plan.Loops.Where(l => !l.Mask).ToArray(); var msk = plan.Loops.Where(l => l.Mask).ToArray();
            Run(sA, [.. plan.Loops.Select(l => l.Feature).Distinct(), "rpn_class", "rpn_bbox"]);
            Run(sR, ["ROI", .. cls.Select(l => l.Boxes)]);
            Align(mask: false);
            Run(sC, ["mrcnn_class", "mrcnn_bbox"]);
            Run(sD, ["mrcnn_detection", .. msk.Select(l => l.Boxes)]);
            Align(mask: true);
            Run(sM, ["mrcnn_mask"]);

            return new StageResult(pool["mrcnn_detection"].GetTensorDataAsSpan<float>().ToArray(), pool["mrcnn_mask"].GetTensorDataAsSpan<float>().ToArray(),
                                   pool["mrcnn_class"].GetTensorDataAsSpan<float>().ToArray());
        }
        finally { foreach (var d in owned) d.Dispose(); }
    }

    /// <summary>특징 맵 [1,C,H,W] → [H,W,C] 사본. ROI 정렬이 한 위치의 C개 채널을 연달아 읽으므로 채널을 붙여 둬야 캐시를 탄다.</summary>
    private static (float[] Data, int H, int W, int C) ToNhwc(OrtValue feature)
    {
        var shape = feature.GetTensorTypeAndShape().Shape;
        int c = (int)shape[1], h = (int)shape[2], w = (int)shape[3], plane = h * w;
        var dst = new float[plane * c];
        var src = feature.GetTensorDataAsSpan<float>();
        unsafe
        {
            fixed (float* ps0 = src) fixed (float* pd0 = dst)
            {
                nint ps = (nint)ps0, pd = (nint)pd0;
                Parallel.For(0, h, y =>
                {
                    float* s = (float*)ps; float* d = (float*)pd + (long)y * w * c;
                    for (int k = 0; k < c; k++)
                    {
                        float* sp = s + (long)k * plane + (long)y * w;
                        for (int x = 0; x < w; x++) d[(long)x * c + k] = sp[x];
                    }
                });
            }
        }
        return (dst, h, w, c);
    }

    /// <summary>tf.image.crop_and_resize(쌍선형, extrapolation 0)를 ONNX Runtime의 Resize(linear, tf_crop_and_resize)와 같은 식으로 계산한다.
    /// feature = [H,W,C], boxes = [n,4](y1,x1,y2,x2 정규화) → [n,crop,crop,C]. 원래 좌표 = start·(L−1) + j·(end−start)·(L−1)/(crop−1)를 float로 구하고,
    /// 범위(0…L−1) 밖이면 0, 안이면 dx2·dy2·X11 + dx1·dy2·X21 + dx2·dy1·X12 + dx1·dy1·X22 (두 이웃이 같은 칸이면 가중 0.5씩 — ORT와 같다).</summary>
    private static float[] CropAndResize(float[] feature, int fh, int fw, int c, ReadOnlySpan<float> boxes, int n, int crop)
    {
        var result = new float[(long)n * crop * crop * c];
        var bx = boxes.ToArray();
        unsafe
        {
            fixed (float* pf0 = feature) fixed (float* pr0 = result)
            {
                nint pf = (nint)pf0, prr = (nint)pr0;
                Parallel.For(0, n, i =>
                {
                    Span<int> y1 = stackalloc int[crop], y2 = stackalloc int[crop], x1 = stackalloc int[crop], x2 = stackalloc int[crop];
                    Span<float> dy1 = stackalloc float[crop], dy2 = stackalloc float[crop], dx1 = stackalloc float[crop], dx2 = stackalloc float[crop];
                    Span<bool> oy = stackalloc bool[crop], ox = stackalloc bool[crop];
                    Axis(bx[i * 4], bx[i * 4 + 2], fh, crop, y1, y2, dy1, dy2, oy);
                    Axis(bx[i * 4 + 1], bx[i * 4 + 3], fw, crop, x1, x2, dx1, dx2, ox);
                    float* f = (float*)pf; float* dst = (float*)prr + (long)i * crop * crop * c;
                    int vec = Vector<float>.Count;
                    for (int y = 0; y < crop; y++)
                    for (int x = 0; x < crop; x++, dst += c)
                    {
                        if (oy[y] || ox[x]) continue;   // 배열은 0으로 초기화돼 있다 = extrapolation 0
                        float w11 = dx2[x] * dy2[y], w21 = dx1[x] * dy2[y], w12 = dx2[x] * dy1[y], w22 = dx1[x] * dy1[y];
                        float* p11 = f + ((long)y1[y] * fw + x1[x]) * c, p21 = f + ((long)y1[y] * fw + x2[x]) * c;
                        float* p12 = f + ((long)y2[y] * fw + x1[x]) * c, p22 = f + ((long)y2[y] * fw + x2[x]) * c;
                        int k = 0;
                        var v11 = new Vector<float>(w11); var v21 = new Vector<float>(w21); var v12 = new Vector<float>(w12); var v22 = new Vector<float>(w22);
                        for (; k <= c - vec; k += vec)
                        {
                            var r = v11 * Unsafe.Read(p11 + k) + v21 * Unsafe.Read(p21 + k) + v12 * Unsafe.Read(p12 + k) + v22 * Unsafe.Read(p22 + k);
                            Unsafe.Write(dst + k, r);
                        }
                        for (; k < c; k++) dst[k] = w11 * p11[k] + w21 * p21[k] + w12 * p12[k] + w22 * p22[k];
                    }
                });
            }
        }
        return result;
    }

    /// <summary>한 축의 표본 위치·가중치 (ORT SetupUpsampleBilinear + tf_crop_and_resize 좌표 변환).</summary>
    private static void Axis(float start, float end, int length, int crop, Span<int> i1, Span<int> i2, Span<float> d1, Span<float> d2, Span<bool> outside)
    {
        for (int j = 0; j < crop; j++)
        {
            float orig = crop == length ? j
                : crop > 1 ? start * (length - 1) + (j * (end - start) * (length - 1)) / (crop - 1)
                : (float)(0.5 * (start + end) * (length - 1));
            outside[j] = orig < 0 || orig > length - 1;
            float v = Math.Max(0.0f, Math.Min(orig, length - 1));
            int a = Math.Min((int)v, length - 1), b = Math.Min(a + 1, length - 1);
            i1[j] = a; i2[j] = b;
            d1[j] = Math.Abs(v - a); d2[j] = Math.Abs(v - b);
            if (a == b) { d1[j] = 0.5f; d2[j] = 0.5f; }
        }
    }

    private static class Unsafe
    {
        public static unsafe Vector<float> Read(float* p) => System.Runtime.CompilerServices.Unsafe.ReadUnaligned<Vector<float>>(p);
        public static unsafe void Write(float* p, Vector<float> v) => System.Runtime.CompilerServices.Unsafe.WriteUnaligned(p, v);
    }

    /// <summary>1024×1024 입력의 앵커 피라미드(정규화 좌표, y1 x1 y2 x2) — generate_pyramid_anchors + norm_boxes. 크기가 고정이라 한 번만 만든다.</summary>
    private static readonly Lazy<float[]> Anchors = new(() =>
    {
        var list = new List<float>(261_888 * 4);
        for (int lv = 0; lv < BackboneStrides.Length; lv++)
        {
            int stride = BackboneStrides[lv];
            int fs = (int)Math.Ceiling((double)MaxDim / stride);
            double s = AnchorScales[lv];
            // 순서: 위치(행 → 열)마다 비율 3개
            for (int y = 0; y < fs; y++)
            for (int x = 0; x < fs; x++)
            foreach (double r in AnchorRatios)
            {
                double ah = s / Math.Sqrt(r), aw = s * Math.Sqrt(r);
                double cy = y * stride, cx = x * stride;
                // norm_boxes: (box − (0,0,1,1)) / (1023)
                list.Add((float)((cy - 0.5 * ah) / (MaxDim - 1)));
                list.Add((float)((cx - 0.5 * aw) / (MaxDim - 1)));
                list.Add((float)((cy + 0.5 * ah - 1) / (MaxDim - 1)));
                list.Add((float)((cx + 0.5 * aw - 1) / (MaxDim - 1)));
            }
        }
        return list.ToArray();
    });
}
