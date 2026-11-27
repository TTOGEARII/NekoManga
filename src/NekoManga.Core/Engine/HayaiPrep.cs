namespace NekoManga.Core.Engine;

/// <summary>Hayai OCR(v2.7 Supernova)의 입력 만들기 — 모델 저장소의 inference_onnx.py(_naflex_target_size·_select_best_max_patches·_preprocess·_pos_embed)를 그대로 옮긴 것.
/// 조각을 16px 패치의 격자로 바꾼다: 가로세로 비율을 지키며 패치 한도(128·192·256·384 가운데 조각 모양에 맞는 것) 안에서 가장 크게 맞추고(4배까지),
/// Pillow의 BILINEAR로 리사이즈해 패치마다 768개 값(−1~1)으로 편다. 위치 값은 16×16 표를 패치 격자 크기로 다시 뽑는다.
/// 크기 고르기·리사이즈가 원본과 어긋나면 모델은 학습 때와 다른 입력을 받는다 — 오류 없이 읽기만 나빠지므로 시험(HayaiPrepTests)이 원본의 값으로 붙든다.</summary>
internal static class HayaiPrep
{
    public const int Patch = 16;
    /// <summary>패치 하나의 값 수 (16 × 16 × RGB).</summary>
    public const int PatchDim = Patch * Patch * 3;
    public const int MinPatches = 64;
    public const double MaxScale = 4.0;
    /// <summary>한 묶음의 패치 수는 이 배수로 올림한다(원본 pad_to_multiple_of).</summary>
    public const int PadMultiple = 8;

    private static int Side(int px, double scale) => Math.Max(Patch, (int)Math.Ceiling(px * scale / Patch) * Patch);

    /// <summary>패치 수가 <paramref name="maxPatches"/>를 넘지 않는 가장 큰 배율(4배까지)로 맞춘 크기. 그래도 패치가 64개에 못 미치는 아주 작은 조각은 64개까지 더 키운다.
    /// 배율은 원본처럼 이분 탐색으로 찾는다(같은 배정밀도 연산 — 경계에서 한 패치가 갈리지 않게).</summary>
    public static (int Th, int Tw, int Nh, int Nw) TargetSize(int height, int width, int maxPatches)
    {
        double lo = 1e-5, hi = MaxScale;
        while (hi - lo >= 1e-5)
        {
            double scale = 0.5 * (lo + hi);
            if (Side(height, scale) / Patch * (Side(width, scale) / Patch) <= maxPatches) lo = scale;
            else hi = scale;
        }
        int th = Side(height, lo), tw = Side(width, lo);
        if (th / Patch * (tw / Patch) < MinPatches)
        {
            double sLo = lo, sHi = 100.0;
            int target = Math.Min(MinPatches, maxPatches);
            while (sHi - sLo >= 1e-5)
            {
                double s = 0.5 * (sLo + sHi);
                if (Side(height, s) / Patch * (Side(width, s) / Patch) <= target) sLo = s;
                else sHi = s;
            }
            th = Side(height, sLo);
            tw = Side(width, sLo);
        }
        return (th, tw, th / Patch, tw / Patch);
    }

    /// <summary>조각 모양에 맞는 패치 한도를 고른다(원본의 max_patches="auto") — 작은 한도로도 글자가 충분히 커지면(배율) 작은 것을, 길쭉하거나 큰 조각은 큰 것을.
    /// 돌려주는 것: 한도, 그 한도에서의 패치 수, 시각 토큰 수(인코더가 패치를 2×2로 줄인 뒤의 수 — 묶음을 짤 때 쓴다).</summary>
    public static (int Budget, int Patches, int VisTokens) SelectBudget(int height, int width)
    {
        (int N, double S, int M) Info(int budget)
        {
            var (th, tw, nh, nw) = TargetSize(height, width, budget);
            return (nh * nw, Math.Min(th / (double)Math.Max(1, height), tw / (double)Math.Max(1, width)), (nh + 1) / 2 * ((nw + 1) / 2));
        }
        var (n128, s128, m128) = Info(128);
        var (n192, s192, m192) = Info(192);
        var (n256, s256, m256) = Info(256);
        var (n384, _, m384) = Info(384);
        if (n128 == n384) return (128, n128, m128);

        int h = Math.Max(1, height), w = Math.Max(1, width);
        double aspect = Math.Max(h, w) / (double)Math.Min(h, w), wOverH = w / (double)h;
        bool square = wOverH is >= 0.77 and <= 1.30, horizontal = wOverH > 1.30;
        if (!square)
        {
            if (!horizontal && s128 >= 1.65 && aspect < 3.8) return (128, n128, m128);
            if (horizontal && s128 >= 2.35 && aspect < 3.2) return (128, n128, m128);
        }
        if (n384 > n256
            && (s256 < 1.28 || (square && s256 < 2.20) || (horizontal && s256 < 1.45) || (aspect >= 3.8 && s256 < 1.75) || (aspect >= 5.0 && s256 < 2.20)))
            return (384, n384, m384);
        if (n192 == n128) return (128, n128, m128);
        if (n256 == n192) return (192, n192, m192);
        if (!square)
        {
            if (!horizontal && s192 >= 1.55 && aspect < 3.6) return (192, n192, m192);
            if (horizontal && s192 >= 1.85 && aspect < 3.0) return (192, n192, m192);
        }
        return (256, n256, m256);
    }

    // ── Pillow BILINEAR (ImagingResample: 삼각 필터, 줄일 때는 배율만큼 넓힌다 + 22비트 고정소수점) ──
    // OpenCV INTER_LINEAR는 줄일 때 필터를 넓히지 않아(이웃 두 픽셀만 본다) 큰 조각을 줄이면 획이 끊긴다 — 원본과 같은 그림이 들어가게 Pillow의 계산을 옮긴다.
    // (BICUBIC판은 AletheiaInpainter.cs의 PilResample에 있다 — 검열 제거 쪽 파일이라 건드리지 않고 여기 따로 둔다.)

    private const int PrecisionBits = 32 - 8 - 2;

    private static (int[] Bounds, int[] Kk, int Ksize) Coeffs(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = 1.0 * filterscale;
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
                double d = Math.Abs((x + xmin - center + 0.5) * ss);
                double w = d < 1.0 ? 1.0 - d : 0.0;
                k[x] = w;
                ww += w;
            }
            for (int x = 0; x < xmax; x++)
            {
                double v = ww != 0.0 ? k[x] / ww : k[x];
                kk[xx * ksize + x] = v < 0 ? (int)(-0.5 + v * (1 << PrecisionBits)) : (int)(0.5 + v * (1 << PrecisionBits));
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kk, ksize);
    }

    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>RGB 8비트(행 우선, 픽셀당 3바이트)를 Pillow의 `Image.resize(BILINEAR)`와 같은 바이트로 리사이즈한다 — 가로 패스 뒤 세로 패스, 중간값도 8비트.</summary>
    public static byte[] Bilinear(byte[] src, int w, int h, int outW, int outH)
    {
        byte[] cur = src;
        int cw = w;
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
                    s0 += cur[p] * c;
                    s1 += cur[p + 1] * c;
                    s2 += cur[p + 2] * c;
                }
                int o = (y * outW + xx) * 3;
                tmp[o] = Clip8(s0);
                tmp[o + 1] = Clip8(s1);
                tmp[o + 2] = Clip8(s2);
            }
            cur = tmp;
            cw = outW;
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
                        s0 += cur[p] * c;
                        s1 += cur[p + 1] * c;
                        s2 += cur[p + 2] * c;
                    }
                    int o = (yy * cw + x) * 3;
                    tmp[o] = Clip8(s0);
                    tmp[o + 1] = Clip8(s1);
                    tmp[o + 2] = Clip8(s2);
                }
            }
            cur = tmp;
        }
        return ReferenceEquals(cur, src) ? (byte[])src.Clone() : cur;
    }

    /// <summary>목표 크기의 RGB 그림(가로세로가 16의 배수)을 패치로 편다 — 패치는 왼쪽 위부터 줄 차례, 패치 안도 줄 → 칸 → R·G·B 차례, 값은 v × 2/255 − 1.</summary>
    public static void WritePatches(byte[] rgb, int tw, int th, Span<float> dest)
    {
        int nh = th / Patch, nw = tw / Patch, o = 0;
        for (int pr = 0; pr < nh; pr++)
        for (int pc = 0; pc < nw; pc++)
        for (int y = 0; y < Patch; y++)
        {
            int p = ((pr * Patch + y) * tw + pc * Patch) * 3;
            for (int x = 0; x < Patch * 3; x++) dest[o++] = rgb[p + x] * (2f / 255f) - 1f;
        }
    }

    /// <summary>길이 <paramref name="inSize"/>의 축을 <paramref name="outSize"/>로 다시 뽑는 가중치(out × in, 줄마다 합 1) — 삼각 필터, 줄일 때는 배율만큼 넓힌다(원본 _aa_1d).</summary>
    public static float[] AaWeights(int outSize, int inSize)
    {
        float scale = (float)((double)inSize / outSize);
        float invscale = scale > 1f ? (float)(1.0 / ((double)inSize / outSize)) : 1f;
        var weights = new float[outSize * inSize];
        for (int i = 0; i < outSize; i++)
        {
            float center = scale * (i + 0.5f), sum = 0f;
            for (int j = 0; j < inSize; j++)
            {
                float v = Math.Max(0f, 1f - Math.Abs(j + 0.5f - center) * invscale);
                weights[i * inSize + j] = v;
                sum += v;
            }
            sum = Math.Max(sum, 1e-12f);
            for (int j = 0; j < inSize; j++) weights[i * inSize + j] /= sum;
        }
        return weights;
    }

    /// <summary>위치 표(side × side × dim, 행 우선)를 패치 격자(nh × nw)로 다시 뽑는다 → nh·nw × dim. 세로·가로를 따로 줄이거나 늘린다(원본의 einsum "ih,jw,hwd->ijd").</summary>
    public static float[] PosEmbed(float[] grid, int side, int dim, int nh, int nw)
    {
        var wh = AaWeights(nh, side);
        var ww = AaWeights(nw, side);
        // 세로 먼저: rows[i, w, d] = Σ_h wh[i, h] · grid[h, w, d]
        var rows = new float[nh * side * dim];
        for (int i = 0; i < nh; i++)
        for (int hh = 0; hh < side; hh++)
        {
            float a = wh[i * side + hh];
            if (a == 0f) continue;
            int src = hh * side * dim, dst = i * side * dim;
            for (int k = 0; k < side * dim; k++) rows[dst + k] += a * grid[src + k];
        }
        var result = new float[nh * nw * dim];
        for (int i = 0; i < nh; i++)
        for (int j = 0; j < nw; j++)
        for (int w = 0; w < side; w++)
        {
            float a = ww[j * side + w];
            if (a == 0f) continue;
            int src = (i * side + w) * dim, dst = (i * nw + j) * dim;
            for (int d = 0; d < dim; d++) result[dst + d] += a * rows[src + d];
        }
        return result;
    }
}
