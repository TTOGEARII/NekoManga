using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>Aletheia Lens(https://github.com/Cec1c/Aletheia-Lens, GPL-3.0)의 복원기 이식 — DeepCreamPy 계열 256×256 모델 두 개
/// (`models/deepcreampy/bar.onnx` 막대용, `mosaic.onnx` 모자이크용, 각 14.6MB)를 원본 `decensor.py`의 **명시 마스크 경로**
/// (`decensor(ori, ori, is_mosaic, repair_mask=…)`) 그대로 돌린다 (2026-11-09):
///   1. 마스크를 4-연결 성분으로 나눈다. 성분마다 상자 = 성분 bbox 긴 변의 1.5배 정사각형(최소 256, 이미지 크기로 자름 — manual_region_bounds).
///   2. 상자를 256×256으로 NEAREST 축소(원고·마스크 둘 다). 막대 모드는 구멍 픽셀을 0으로 지운 뒤, 모자이크 모드는 그대로 [-1,1]로 정규화.
///   3. 모델(입력 Placeholder:0·Placeholder_1:0 = 그림, Placeholder_2:0 = 마스크(1 보존·0 구멍), 출력 add:0)로 예측.
///   4. 예측을 상자 크기로 BICUBIC 확대해 **그 성분의 픽셀만** 원고에 복사한다(마스크 밖은 바이트 그대로).
/// 모든 성분은 **수정 전 원고**에서 잘라 예측한다(원본과 같다 — 먼저 고친 이웃이 다음 성분의 입력에 섞이지 않는다).
/// 리사이즈는 Pillow와 같은 결과가 나오게 직접 구현했다(<see cref="PilResample"/>) — OpenCV의 NEAREST·CUBIC은 표본 위치·계수가 달라 모델 입력이 달라진다.
///
/// 세션은 CPU 고정: 원본도 이 두 모델을 CPU에 묶어 둔다(CUDA 12/cuDNN 9에서 non-finite 출력).</summary>
public class AletheiaInpainter(OnnxEnv env)
{
    public const string BarModelFile = "aletheia_bar.onnx";
    public const string MosaicModelFile = "aletheia_mosaic.onnx";
    private const int Size = 256;

    public bool IsReady => env.ModelExists(BarModelFile) && env.ModelExists(MosaicModelFile);

    /// <summary>원본의 `(image_to_array(pred) * 255.0).astype(uint8)` — 일부 값은 v/255.0*255.0이 v보다 살짝 작아 1 내려간다(원본과 같은 바이트를 내려고 그대로 둔다).</summary>
    private static readonly byte[] RoundTrip = Enumerable.Range(0, 256).Select(v => (byte)(v / 255.0 * 255.0)).ToArray();

    /// <summary>img(BGR 8UC3)의 mask(8UC1, 0이 아닌 곳 = 고칠 곳) 영역을 제자리에서 복원한다. 고친 성분 수를 돌려준다.</summary>
    /// <param name="mosaic">true = 모자이크 모델(가려진 픽셀을 힌트로 쓴다), false = 막대 모델(구멍을 비우고 주변으로 채운다).</param>
    public int Repair(Mat img, Mat mask, bool mosaic)
    {
        // 아래는 포인터로 직접 읽고 쓴다 — 형식·크기가 어긋나면 메모리를 망가뜨리므로 먼저 막는다
        if (img.Type() != MatType.CV_8UC3) throw new ArgumentException("복원할 그림은 BGR 8비트(CV_8UC3)여야 해요.", nameof(img));
        if (mask.Type() != MatType.CV_8UC1 || mask.Size() != img.Size()) throw new ArgumentException("마스크는 그림과 같은 크기의 8비트 한 채널이어야 해요.", nameof(mask));
        int W = img.Cols, H = img.Rows;
        using var bin = new Mat();
        Cv2.Threshold(mask, bin, 0, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cents, PixelConnectivity.Connectivity4, MatType.CV_32S);
        if (n <= 1) return 0;

        using var ori = img.Clone();   // 모든 성분의 입력은 수정 전 원고
        var session = env.Get(mosaic ? MosaicModelFile : BarModelFile, cpuOnly: true);
        var image = new float[Size * Size * 3];
        var maskT = new float[Size * Size * 3];
        var pred = new byte[Size * Size * 3];

        for (int li = 1; li < n; li++)
        {
            int rx = stats.At<int>(li, (int)ConnectedComponentsTypes.Left), ry = stats.At<int>(li, (int)ConnectedComponentsTypes.Top);
            int rw = stats.At<int>(li, (int)ConnectedComponentsTypes.Width), rh = stats.At<int>(li, (int)ConnectedComponentsTypes.Height);

            // manual_region_bounds: 모든 선택 픽셀을 품는 상자 (얇은 이미지·가장자리 포함)
            int side = Math.Max(Size, (3 * Math.Max(rw, rh) + 1) / 2);   // ceil(긴 변 × 1.5)
            int bw = Math.Min(side, W), bh = Math.Min(side, H);
            int left = Math.Max(0, Math.Min(FloorDiv(rx + rx + rw - bw, 2), W - bw));
            int top = Math.Max(0, Math.Min(FloorDiv(ry + ry + rh - bh, 2), H - bh));

            // 상자 → 256×256 NEAREST (Pillow 표본 위치). 마스크는 "이 성분"이 아니라 전체 마스크를 자른다 — 상자 안의 다른 구멍도 구멍이다
            var xs = PilResample.NearestMap(bw, Size);
            var ys = PilResample.NearestMap(bh, Size);
            unsafe
            {
                for (int y = 0; y < Size; y++)
                {
                    byte* srow = (byte*)ori.Ptr(top + ys[y]);
                    byte* mrow = (byte*)bin.Ptr(top + ys[y]);
                    for (int x = 0; x < Size; x++)
                    {
                        int sx = left + xs[x], o = (y * Size + x) * 3;
                        bool hole = mrow[sx] != 0;
                        float keep = hole ? 0f : 1f;
                        maskT[o] = keep; maskT[o + 1] = keep; maskT[o + 2] = keep;
                        if (hole && !mosaic)
                        {
                            image[o] = -1f; image[o + 1] = -1f; image[o + 2] = -1f;   // 0으로 지운 뒤 ×2−1
                        }
                        else
                        {
                            image[o] = (float)(srow[sx * 3 + 2] / 255.0 * 2.0 - 1);       // R
                            image[o + 1] = (float)(srow[sx * 3 + 1] / 255.0 * 2.0 - 1);   // G
                            image[o + 2] = (float)(srow[sx * 3] / 255.0 * 2.0 - 1);       // B
                        }
                    }
                }
            }

            var dims = new[] { 1, Size, Size, 3 };
            var imageTensor = new DenseTensor<float>(image, dims);
            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("Placeholder:0", imageTensor),
                NamedOnnxValue.CreateFromTensor("Placeholder_1:0", imageTensor),
                NamedOnnxValue.CreateFromTensor("Placeholder_2:0", new DenseTensor<float>(maskT, dims)),
            }, new[] { "add:0" });
            var output = results.First().AsEnumerable<float>().ToArray();   // [1,256,256,3] RGB, −1…1

            for (int k = 0; k < output.Length; k++)
            {
                float v = output[k];
                if (!float.IsFinite(v))
                    throw new InvalidOperationException("복원 모델이 잘못된 값을 돌려줬어요 (non-finite) — 다시 시도해 주세요.");
                // 원본: (255.0 * ((pred + 1.0) / 2.0)).astype(uint8) — float32 계산 후 버림. 범위를 벗어난 값은 원본에선 래핑되지만 여기서는 자른다
                float f = 255.0f * ((v + 1.0f) / 2.0f);
                pred[k] = f <= 0f ? (byte)0 : f >= 255f ? (byte)255 : (byte)f;
            }

            // 예측을 상자 크기로 BICUBIC 확대해 이 성분의 픽셀만 되붙인다
            var full = PilResample.Bicubic(pred, Size, Size, bw, bh);
            unsafe
            {
                for (int y = ry; y < ry + rh; y++)
                {
                    int* lrow = (int*)labels.Ptr(y);
                    byte* drow = (byte*)img.Ptr(y);
                    int po = (y - top) * bw;
                    for (int x = rx; x < rx + rw; x++)
                    {
                        if (lrow[x] != li) continue;
                        int p = (po + x - left) * 3;
                        drow[x * 3 + 2] = RoundTrip[full[p]];
                        drow[x * 3 + 1] = RoundTrip[full[p + 1]];
                        drow[x * 3] = RoundTrip[full[p + 2]];
                    }
                }
            }
        }
        return n - 1;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}

/// <summary>Pillow(PIL)의 리사이즈를 같은 정수 연산으로 옮긴 것 — Aletheia Lens/DeepCreamPy가 `Image.resize(NEAREST)`·`Image.resize(BICUBIC)`로
/// 모델 입력·출력을 다루므로, 같은 그림이 나오려면 표본 위치와 계수가 같아야 한다.
/// (OpenCV INTER_NEAREST는 floor(x·scale), Pillow는 floor((x+0.5)·scale); OpenCV INTER_CUBIC은 a=−0.75, Pillow는 a=−0.5 + 22비트 고정소수점.)</summary>
internal static class PilResample
{
    /// <summary>NEAREST 축소·확대의 표본 열 번호 (Pillow ImagingScaleAffine: xo = scale/2 에서 시작해 scale씩 누적).</summary>
    public static int[] NearestMap(int inSize, int outSize)
    {
        var map = new int[outSize];
        double a = (double)inSize / outSize, o = a * 0.5;
        for (int i = 0; i < outSize; i++, o += a)
            map[i] = Math.Clamp((int)o, 0, inSize - 1);
        return map;
    }

    private const int PrecisionBits = 32 - 8 - 2;

    private static double BicubicFilter(double x)
    {
        const double a = -0.5;
        if (x < 0.0) x = -x;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    /// <summary>Pillow precompute_coeffs + normalize_coeffs_8bpc (BICUBIC, support 2).</summary>
    private static (int[] Bounds, int[] Kk, int Ksize) Coeffs(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = 2.0 * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var kk = new int[outSize * ksize];
        var bounds = new int[outSize * 2];
        var k = new double[ksize];
        double ss = 1.0 / filterscale;
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale, ww = 0.0;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            for (int x = 0; x < xmax; x++)
            {
                double w = BicubicFilter((x + xmin - center + 0.5) * ss);
                k[x] = w; ww += w;
            }
            for (int x = 0; x < xmax; x++)
            {
                double v = ww != 0.0 ? k[x] / ww : k[x];
                kk[xx * ksize + x] = v < 0 ? (int)(-0.5 + v * (1 << PrecisionBits)) : (int)(0.5 + v * (1 << PrecisionBits));
            }
            bounds[xx * 2] = xmin; bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kk, ksize);
    }

    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>RGB 8비트(행 우선, 픽셀당 3바이트)를 BICUBIC으로 리사이즈한다 — 가로 패스 뒤 세로 패스(Pillow ImagingResample과 같은 순서·중간 8비트).</summary>
    public static byte[] Bicubic(byte[] src, int w, int h, int outW, int outH)
    {
        byte[] cur = src; int cw = w;
        if (outW != w)
        {
            var (bounds, kk, ksize) = Coeffs(w, outW);
            var tmp = new byte[h * outW * 3];
            for (int y = 0; y < h; y++)
            for (int xx = 0; xx < outW; xx++)
            {
                int xmin = bounds[xx * 2], cnt = bounds[xx * 2 + 1], ko = xx * ksize;
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                for (int x = 0; x < cnt; x++)
                {
                    int p = (y * w + x + xmin) * 3, c = kk[ko + x];
                    s0 += cur[p] * c; s1 += cur[p + 1] * c; s2 += cur[p + 2] * c;
                }
                int o = (y * outW + xx) * 3;
                tmp[o] = Clip8(s0); tmp[o + 1] = Clip8(s1); tmp[o + 2] = Clip8(s2);
            }
            cur = tmp; cw = outW;
        }
        if (outH != h)
        {
            var (bounds, kk, ksize) = Coeffs(h, outH);
            var tmp = new byte[outH * cw * 3];
            for (int yy = 0; yy < outH; yy++)
            {
                int ymin = bounds[yy * 2], cnt = bounds[yy * 2 + 1], ko = yy * ksize;
                for (int x = 0; x < cw; x++)
                {
                    int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                    for (int y = 0; y < cnt; y++)
                    {
                        int p = ((y + ymin) * cw + x) * 3, c = kk[ko + y];
                        s0 += cur[p] * c; s1 += cur[p + 1] * c; s2 += cur[p + 2] * c;
                    }
                    int o = (yy * cw + x) * 3;
                    tmp[o] = Clip8(s0); tmp[o + 1] = Clip8(s1); tmp[o + 2] = Clip8(s2);
                }
            }
            cur = tmp;
        }
        return ReferenceEquals(cur, src) ? (byte[])src.Clone() : cur;
    }
}
