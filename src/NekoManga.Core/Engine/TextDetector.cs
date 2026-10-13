using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace NekoManga.Core.Engine;

/// <summary>감지된 텍스트 블록 (원본 이미지 좌표).</summary>
public record DetectedBlock(int X1, int Y1, int X2, int Y2, float Confidence, bool LangJa);

public record DetectionResult(IReadOnlyList<DetectedBlock> Blocks, Mat TextMask);

/// <summary>comictextdetector ONNX로 만화 텍스트를 감지한다.
/// BT의 CTD 파이프라인 이식: letterbox(우/하단 패딩, /255, RGB) → [blk(YOLO), seg(텍스트 마스크), det(DBNet)]
/// 중 blk과 seg만 사용 (라인 단위 DBNet 후처리는 생략 — OCR이 블록 단위라 불필요).</summary>
public class TextDetector(OnnxEnv env)
{
    public const string ModelFile = "ctd.onnx";
    private const int InputSize = 1024;
    private const float ConfThresh = 0.4f;
    private const float NmsThresh = 0.35f;
    private const float MaskThresh = 0.3f;

    public bool IsReady => env.ModelExists(ModelFile);

    /// <summary>img: BGR(Mat). 반환 마스크는 원본 크기의 8UC1 (255=텍스트 획).</summary>
    public DetectionResult Detect(Mat img)
    {
        int imW = img.Width, imH = img.Height;

        // letterbox: 비율 유지 축소 후 우/하단 검은 패딩 (BT letterbox와 동일)
        double r = Math.Min((double)InputSize / imH, (double)InputSize / imW);
        int newW = (int)Math.Round(imW * r), newH = (int)Math.Round(imH * r);
        int dw = InputSize - newW, dh = InputSize - newH;

        using var resized = img.Resize(new Size(newW, newH), interpolation: InterpolationFlags.Linear);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, 0, dh, 0, dw, BorderTypes.Constant, Scalar.Black);

        // HWC BGR byte → NCHW RGB float/255
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        unsafe
        {
            byte* p = (byte*)padded.DataPointer;
            long stride = (long)padded.Step();
            for (int y = 0; y < InputSize; y++)
            {
                byte* row = p + y * stride;
                for (int x = 0; x < InputSize; x++)
                {
                    input[0, 2, y, x] = row[x * 3 + 0] / 255f; // B → ch2
                    input[0, 1, y, x] = row[x * 3 + 1] / 255f; // G
                    input[0, 0, y, x] = row[x * 3 + 2] / 255f; // R → ch0
                }
            }
        }

        var session = env.Get(ModelFile);
        using var results = session.Run(new[]
        {
            Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor("images", input),
        });

        var blk = results.First(v => v.Name == "blk").AsTensor<float>();   // [1, N, 7] cx cy w h obj c0 c1
        var seg = results.First(v => v.Name == "seg").AsTensor<float>();   // [1, 1, 1024, 1024]

        // ── YOLO 후처리: conf 필터 → xyxy → NMS → 원본 좌표 환산 ──
        var boxes = new List<Rect2d>();
        var confs = new List<float>();
        var langs = new List<bool>();
        int n = blk.Dimensions[1];
        for (int i = 0; i < n; i++)
        {
            float obj = blk[0, i, 4];
            if (obj < ConfThresh) continue;
            float c0 = blk[0, i, 5], c1 = blk[0, i, 6];
            float clsConf = Math.Max(c0, c1) * obj;
            if (clsConf < ConfThresh) continue;
            float cx = blk[0, i, 0], cy = blk[0, i, 1], w = blk[0, i, 2], h = blk[0, i, 3];
            boxes.Add(new Rect2d(cx - w / 2, cy - h / 2, w, h));
            confs.Add(clsConf);
            langs.Add(c1 >= c0); // cls1 = ja
        }

        CvDnn.NMSBoxes(boxes, confs, ConfThresh, NmsThresh, out int[] keep);

        // 1024(패딩 제외) → 원본 비율
        double rw = (double)imW / (InputSize - dw), rh = (double)imH / (InputSize - dh);
        var blocks = new List<DetectedBlock>();
        foreach (var k in keep)
        {
            var b = boxes[k];
            int x1 = (int)Math.Clamp(b.X * rw, 0, imW - 1);
            int y1 = (int)Math.Clamp(b.Y * rh, 0, imH - 1);
            int x2 = (int)Math.Clamp((b.X + b.Width) * rw, 0, imW);
            int y2 = (int)Math.Clamp((b.Y + b.Height) * rh, 0, imH);
            if (x2 - x1 < 4 || y2 - y1 < 4) continue;
            blocks.Add(new DetectedBlock(x1, y1, x2, y2, confs[k], langs[k]));
        }
        // 읽기 순서 비슷하게: 위→아래, 오른→왼 (일본 만화)
        blocks.Sort((a, b2) => a.Y1 != b2.Y1 ? a.Y1.CompareTo(b2.Y1) : b2.X1.CompareTo(a.X1));

        // ── 텍스트 마스크: 패딩 잘라내고 원본 크기로 ──
        using var maskSmall = new Mat(InputSize, InputSize, MatType.CV_8UC1);
        unsafe
        {
            byte* mp = (byte*)maskSmall.DataPointer;
            long ms = (long)maskSmall.Step();
            for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
                mp[y * ms + x] = seg[0, 0, y, x] > MaskThresh ? (byte)255 : (byte)0;
        }
        using var maskCropped = new Mat(maskSmall, new Rect(0, 0, InputSize - dw, InputSize - dh));
        var mask = new Mat();
        Cv2.Resize(maskCropped, mask, new Size(imW, imH), interpolation: InterpolationFlags.Linear);
        Cv2.Threshold(mask, mask, 127, 255, ThresholdTypes.Binary);
        // 글자 테두리 잔상까지 덮도록 살짝 확장 (BT의 mask dilate size=3과 동일 효과)
        Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7)));

        return new DetectionResult(blocks, mask);
    }
}
