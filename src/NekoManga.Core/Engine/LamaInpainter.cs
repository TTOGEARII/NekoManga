using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>LaMa(ONNX)로 마스크 영역을 주변과 어울리게 메운다. 입력 0–1, 출력 0–255 (실측 검증).
/// 전체 이미지를 512로 줄이면 디테일이 뭉개지므로, 마스크 영역을 품는 ROI를 잘라
/// 512×512로 처리한 뒤 마스크 부분만 원본에 되붙인다.</summary>
public class LamaInpainter(OnnxEnv env)
{
    public const string ModelFile = "lama_fp32.onnx";
    private const int Size = 512;

    public bool IsReady => env.ModelExists(ModelFile);

    /// <summary>img(BGR)·mask(8UC1, 255=지울 영역)를 받아 img를 제자리 수정한다.</summary>
    public void Inpaint(Mat img, Mat mask)
    {
        // FindContours는 입력 마스크를 파괴하므로 복제본에서 컨투어를 찾고,
        // 원본 mask는 ROI 복원 단계에서 그대로 쓴다.
        using var forContours = mask.Clone();
        Cv2.FindContours(forContours, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return;

        var rois = MergeRois(contours.Select(Cv2.BoundingRect).ToList(), img.Width, img.Height);
        foreach (var roi in rois)
        {
            // 큰 ROI를 통째로 512에 욱여넣으면 복원 패치가 업스케일로 뭉개져
            // 주변의 또렷한 망점과 티가 난다 (수기로 넓게 지운 영역에서 실측).
            // 512 타일(겹침 64)로 나눠 원본 해상도에 가깝게 복원한다.
            if (Math.Max(roi.Width, roi.Height) <= 560) { InpaintRoi(img, mask, roi); continue; }
            const int T = 512, Stride = 448;
            for (int ty = roi.Y; ty < roi.Bottom; ty += Stride)
            for (int tx = roi.X; tx < roi.Right; tx += Stride)
            {
                int x = tx, y = ty;
                if (x + T > roi.Right) x = Math.Max(roi.X, roi.Right - T);
                if (y + T > roi.Bottom) y = Math.Max(roi.Y, roi.Bottom - T);
                var tile = new Rect(x, y, Math.Min(T, img.Width - x), Math.Min(T, img.Height - y));
                using var mt = new Mat(mask, tile);
                if (Cv2.CountNonZero(mt) == 0) continue;
                InpaintRoi(img, mask, tile);
            }
        }
    }

    private void InpaintRoi(Mat img, Mat mask, Rect roi)
    {
        using var crop = new Mat(img, roi);
        using var mcrop = new Mat(mask, roi);

        var image = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var maskT = new DenseTensor<float>(new[] { 1, 1, Size, Size });
        using (var ci = crop.Resize(new Size(Size, Size), interpolation: InterpolationFlags.Linear))
        using (var cm = mcrop.Resize(new Size(Size, Size), interpolation: InterpolationFlags.Nearest))
        {
            unsafe
            {
                byte* pi = (byte*)ci.DataPointer; long si = (long)ci.Step();
                byte* pm = (byte*)cm.DataPointer; long sm = (long)cm.Step();
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte* px = pi + y * si + x * 3;
                    image[0, 0, y, x] = px[2] / 255f;
                    image[0, 1, y, x] = px[1] / 255f;
                    image[0, 2, y, x] = px[0] / 255f;
                    maskT[0, 0, y, x] = pm[y * sm + x] > 127 ? 1f : 0f;
                }
            }
        }

        var session = env.Get(ModelFile);
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("mask", maskT),
        });
        // 평탄 버퍼로 읽는다 — ONNX가 돌려주는 Tensor는 DenseTensor가 아닐 수 있어
        // [0,c,y,x] 인덱서가 strided 접근을 보장하지 않는다 (실제로 틀린 값이 나왔다).
        var output = results.First().AsEnumerable<float>().ToArray(); // [1,3,512,512] RGB 0–255, row-major
        int plane = Size * Size;

        using var outMat = new Mat(Size, Size, MatType.CV_8UC3);
        unsafe
        {
            byte* po = (byte*)outMat.DataPointer; long so = (long)outMat.Step();
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int idx = y * Size + x;
                byte* px = po + y * so + x * 3;
                px[2] = (byte)Math.Clamp(output[idx], 0, 255);              // R (plane 0)
                px[1] = (byte)Math.Clamp(output[plane + idx], 0, 255);      // G
                px[0] = (byte)Math.Clamp(output[2 * plane + idx], 0, 255);  // B
            }
        }

        // 원본 크기로 되돌려 마스크 부분만 교체 — 하드 마스크 대신 **페더 블렌딩**:
        // 경계를 안쪽으로 몇 px 부드럽게 섞어 '덧붙인 티'를 없앤다 (수기 제거가 부자연하다는
        // 피드백). 마스크를 살짝 침식한 뒤 블러 → 알파가 마스크 밖으로 번지지 않아
        // 바깥의 멀쩡한 그림에는 손대지 않는다.
        // new Mat(img, roi)는 데이터 공유 뷰라 여기에 쓰면 img 원본이 바뀐다.
        using var outFull = outMat.Resize(new Size(roi.Width, roi.Height), interpolation: InterpolationFlags.Linear);
        using var alpha = new Mat();
        Cv2.Erode(mcrop, alpha, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)));
        alpha.ConvertTo(alpha, MatType.CV_32F, 1.0 / 255);
        Cv2.GaussianBlur(alpha, alpha, new Size(7, 7), 0);
        using var a3 = new Mat();
        Cv2.Merge([alpha, alpha, alpha], a3);
        using var cropF = new Mat();
        crop.ConvertTo(cropF, MatType.CV_32FC3);
        using var outF = new Mat();
        outFull.ConvertTo(outF, MatType.CV_32FC3);
        using var ones = new Mat(a3.Size(), a3.Type(), Scalar.All(1));
        using var inv = new Mat();
        Cv2.Subtract(ones, a3, inv);
        using var p1 = cropF.Mul(inv).ToMat();
        using var p2 = outF.Mul(a3).ToMat();
        using var blended = new Mat();
        Cv2.Add(p1, p2, blended);
        blended.ConvertTo(crop, MatType.CV_8UC3);
    }

    /// <summary>겹치거나 가까운 박스를 합치고, 모델 처리에 적당하도록 사방 여백을 준 ROI 목록.
    /// (강력 검열제거의 SDXL 크롭 인페인트도 같은 로직을 쓴다.)</summary>
    public static List<Rect> MergeRois(List<Rect> boxes, int imW, int imH)
    {
        // 여백: 512로 리사이즈해 넣으므로 구멍이 ROI의 1/3 이하여야 LaMa가 주변색으로 잘 메운다.
        // 각 방향으로 구멍 크기만큼 패딩 → ROI ≈ 구멍의 3배 (검증: 이 비율에서 흰 풍선 복원 성공).
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            int pad = Math.Max(48, Math.Max(b.Width, b.Height));
            boxes[i] = ClampRect(new Rect(b.X - pad, b.Y - pad, b.Width + pad * 2, b.Height + pad * 2), imW, imH);
        }
        // 겹치는 것끼리 병합 (고정점 반복)
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < boxes.Count && !merged; i++)
            for (int j = i + 1; j < boxes.Count && !merged; j++)
                if (boxes[i].IntersectsWith(boxes[j]))
                {
                    boxes[i] = boxes[i].Union(boxes[j]);
                    boxes.RemoveAt(j);
                    merged = true;
                }
        }
        return boxes;
    }

    private static Rect ClampRect(Rect r, int w, int h)
    {
        int x1 = Math.Max(0, r.X), y1 = Math.Max(0, r.Y);
        int x2 = Math.Min(w, r.X + r.Width), y2 = Math.Min(h, r.Y + r.Height);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }
}
