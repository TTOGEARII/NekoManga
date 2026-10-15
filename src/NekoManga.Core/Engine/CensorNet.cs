using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>NekoManga 맞춤 검열 검출 모델 (YOLOv8s-seg 파인튜닝, ONNX).
///
/// 규칙 기반(기하학) 검출은 장식 줄무늬·효과음 붓획과 원리적으로 구분이 안 되는 한계가
/// 실측으로 확인돼(2026-10-15), 무검열 원고에 검열(흰/검 막대·흰 박스·단색 밀기·모자이크)을
/// 합성해 라벨 노이즈 없이 학습한 전용 모델로 교체했다. classes: 0=white, 1=black, 2=mosaic.
///
/// 입력 1024 타일(겹침 128)로 페이지를 훑고, 인스턴스 마스크를 페이지 좌표로 합성한다.
/// 출력 텐서(yolov8-seg, nms 없음): output0 [1, 4+3+32, N], output1(proto) [1, 32, 256, 256].</summary>
public class CensorNet(OnnxEnv env)
{
    public const string ModelFile = "censor_seg.onnx";
    private const int Size = 1024;
    private const int Stride = 896;
    private const int NumClasses = 3;
    private const int ProtoC = 32, ProtoS = 256;

    public bool IsReady => env.ModelExists(ModelFile);

    public sealed record Detection(int Cls, float Conf, Rect Box);

    /// <summary>파이프라인용 최종 마스크 — 검출 + 말풍선 오탐 제거 + grow까지 한 곳에서.
    /// 흰 검출은 **성분 단위로** 텍스트 블록(exclude)과 20% 이상 겹치면 통째로 버린다 —
    /// 모델이 흰 말풍선을 흰 박스로 오인할 때 사각형 빼기만으로는 풍선 고리가 남았다 (실측 22p).
    /// 요약 문자열(흰 n·검 n·모자이크 n)을 함께 돌려준다. 충분한 검출이 없으면 null.</summary>
    public Mat? BuildMask(Mat imgBgr, IReadOnlyList<Rect>? exclude, int grow, out string summary)
    {
        var (w, b, m, dets) = Detect(imgBgr);
        using (w) using (b) using (m)
        {
            // 흰: 성분 단위 말풍선 필터 — 텍스트 블록을 1.8배 부풀려 '풍선' 근사로 쓴다.
            // 모델의 풍선 오탐 마스크는 글자 박스보다 넓게 풍선 전체를 덮으므로(실측 22p 고리 잔존),
            // 부풀린 블록과 15% 이상 겹치는 흰 성분은 통째로 버린다.
            if (exclude is { Count: > 0 })
            {
                var bubbles = exclude.Select(ex =>
                {
                    int iw = (int)(ex.Width * 0.4), ih = (int)(ex.Height * 0.4);
                    return new Rect(ex.X - iw, ex.Y - ih, ex.Width + iw * 2, ex.Height + ih * 2);
                }).ToList();
                using var lab = new Mat();
                using var st = new Mat();
                using var ce = new Mat();
                int n = Cv2.ConnectedComponentsWithStats(w, lab, st, ce);
                for (int i = 1; i < n; i++)
                {
                    var br = new Rect(
                        st.At<int>(i, (int)ConnectedComponentsTypes.Left),
                        st.At<int>(i, (int)ConnectedComponentsTypes.Top),
                        st.At<int>(i, (int)ConnectedComponentsTypes.Width),
                        st.At<int>(i, (int)ConnectedComponentsTypes.Height));
                    double area = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    double inter = 0;
                    foreach (var ex in bubbles)
                    {
                        var r = ex & br;
                        if (r.Width > 0 && r.Height > 0) inter += (double)r.Width * r.Height;
                    }
                    if (inter >= area * 0.15)
                    {
                        using var comp = new Mat();
                        Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
                        w.SetTo(Scalar.Black, comp);
                    }
                }
                // 검/모자이크는 텍스트 블록 사각형만 빼준다 (글자 획 보호)
                foreach (var ex in exclude)
                {
                    var r = ex & new Rect(0, 0, imgBgr.Cols, imgBgr.Rows);
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    b[r].SetTo(Scalar.Black);
                    m[r].SetTo(Scalar.Black);
                }
            }
            int pw = Cv2.CountNonZero(w), pb = Cv2.CountNonZero(b), pm = Cv2.CountNonZero(m);
            summary = $"AI(흰 {dets.Count(d => d.Cls == 0)}·검 {dets.Count(d => d.Cls == 1)}·모자이크 {dets.Count(d => d.Cls == 2)} → {pw + pb + pm}px)";
            if (pw + pb + pm < 300) return null;
            var mask = new Mat(imgBgr.Rows, imgBgr.Cols, MatType.CV_8UC1, Scalar.Black);
            Cv2.BitwiseOr(mask, w, mask);
            Cv2.BitwiseOr(mask, b, mask);
            Cv2.BitwiseOr(mask, m, mask);
            if (grow > 0)
                Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse,
                    new Size(grow * 2 + 1, grow * 2 + 1)));
            return mask;
        }
    }

    /// <summary>페이지 전체를 추론해 클래스별 마스크(8UC1, 페이지 크기)와 검출 목록을 돌려준다.</summary>
    public (Mat White, Mat Black, Mat Mosaic, List<Detection> Dets) Detect(Mat imgBgr, float conf = 0.45f)
    {
        int W = imgBgr.Cols, H = imgBgr.Rows;
        var white = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var black = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var mosaic = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var dets = new List<Detection>();

        foreach (var (tx, ty) in Tiles(W, H))
        {
            int tw = Math.Min(Size, W - tx), th = Math.Min(Size, H - ty);
            using var tile = new Mat(Size, Size, MatType.CV_8UC3, new Scalar(255, 255, 255));
            using (var src = new Mat(imgBgr, new Rect(tx, ty, tw, th)))
            using (var dst = new Mat(tile, new Rect(0, 0, tw, th)))
                src.CopyTo(dst);

            foreach (var d in RunTile(tile, conf))
            {
                // 타일 겹침 영역의 중복은 마스크 OR라 자연히 합쳐진다
                using var m = d.Mask;
                var roiW = new Mat(d.Cls == 0 ? white : d.Cls == 1 ? black : mosaic,
                    new Rect(tx, ty, tw, th));
                using (roiW)
                using (var mCrop = new Mat(m, new Rect(0, 0, tw, th)))
                    Cv2.BitwiseOr(roiW, mCrop, roiW);
                dets.Add(new Detection(d.Cls, d.Conf,
                    new Rect(d.Box.X + tx, d.Box.Y + ty, d.Box.Width, d.Box.Height)));
            }
        }
        return (white, black, mosaic, dets);
    }

    private static IEnumerable<(int X, int Y)> Tiles(int W, int H)
    {
        var xs = new List<int>(); var ys = new List<int>();
        for (int x = 0; ; x += Stride) { xs.Add(Math.Max(0, Math.Min(x, W - Size))); if (x + Size >= W) break; }
        for (int y = 0; ; y += Stride) { ys.Add(Math.Max(0, Math.Min(y, H - Size))); if (y + Size >= H) break; }
        foreach (var y in ys.Distinct())
            foreach (var x in xs.Distinct())
                yield return (x, y);
    }

    private sealed record TileDet(int Cls, float Conf, Rect Box, Mat Mask);

    private List<TileDet> RunTile(Mat tile, float conf)
    {
        var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        unsafe
        {
            byte* p = (byte*)tile.DataPointer; long step = (long)tile.Step();
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte* px = p + y * step + x * 3;
                input[0, 0, y, x] = px[2] / 255f;
                input[0, 1, y, x] = px[1] / 255f;
                input[0, 2, y, x] = px[0] / 255f;
            }
        }

        var session = env.Get(ModelFile);
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(
            session.InputMetadata.Keys.First(), input) });
        var outs = results.ToList();
        // output0: [1, 39, N], output1(proto): [1, 32, 256, 256] — 이름이 아니라 모양으로 찾는다
        var flat0 = outs.Select(o => o.AsTensor<float>()).First(t => t.Dimensions.Length == 3 && t.Dimensions[1] == 4 + NumClasses + ProtoC);
        var proto = outs.Select(o => o.AsTensor<float>()).First(t => t.Dimensions.Length == 4 && t.Dimensions[1] == ProtoC);
        int N = flat0.Dimensions[2];
        var det0 = flat0.ToArray();     // row-major [39, N] (배치 1)
        float At(int c, int i) => det0[c * N + i];

        // conf 필터 + 클래스별 수집
        var cand = new List<(float Conf, int Cls, float Cx, float Cy, float W, float H, int Idx)>();
        for (int i = 0; i < N; i++)
        {
            int bestC = -1; float bestS = 0;
            for (int c = 0; c < NumClasses; c++)
            {
                float s = At(4 + c, i);
                if (s > bestS) { bestS = s; bestC = c; }
            }
            if (bestS < conf) continue;
            cand.Add((bestS, bestC, At(0, i), At(1, i), At(2, i), At(3, i), i));
        }
        cand.Sort((a, b) => b.Conf.CompareTo(a.Conf));

        // NMS (IoU 0.5, 클래스 무관하지 않게 클래스별)
        var kept = new List<(float Conf, int Cls, Rect Box, int Idx)>();
        foreach (var c in cand)
        {
            int bx = Math.Clamp((int)(c.Cx - c.W / 2), 0, Size - 1);
            int by = Math.Clamp((int)(c.Cy - c.H / 2), 0, Size - 1);
            var box = new Rect(bx, by,
                Math.Clamp((int)c.W, 1, Size - bx), Math.Clamp((int)c.H, 1, Size - by));
            if (box.Width < 6 || box.Height < 6) continue;
            bool drop = false;
            foreach (var k in kept)
            {
                if (k.Cls != c.Cls) continue;
                var inter = k.Box & box;
                double ia = (double)inter.Width * inter.Height;
                double ua = (double)k.Box.Width * k.Box.Height + (double)box.Width * box.Height - ia;
                if (ua > 0 && ia / ua > 0.5) { drop = true; break; }
            }
            if (!drop) kept.Add((c.Conf, c.Cls, box, c.Idx));
            if (kept.Count >= 60) break;
        }

        // 마스크 합성: sigmoid(coeff · proto) → 박스로 크롭 → 타일 크기로 업샘플
        var protoArr = proto.ToArray();   // [32, 256, 256]
        var res = new List<TileDet>();
        foreach (var k in kept)
        {
            var coef = new float[ProtoC];
            for (int c = 0; c < ProtoC; c++) coef[c] = At(4 + NumClasses + c, k.Idx);
            using var mSmall = new Mat(ProtoS, ProtoS, MatType.CV_8UC1, Scalar.Black);
            unsafe
            {
                byte* mp = (byte*)mSmall.DataPointer; long ms = (long)mSmall.Step();
                int plane = ProtoS * ProtoS;
                // 박스 영역만 계산해도 되지만 256²×32는 싸다 — 전체 계산 후 크롭
                for (int y = 0; y < ProtoS; y++)
                for (int x = 0; x < ProtoS; x++)
                {
                    float v = 0;
                    int off = y * ProtoS + x;
                    for (int c = 0; c < ProtoC; c++) v += coef[c] * protoArr[c * plane + off];
                    mp[y * ms + x] = v > 0 ? (byte)255 : (byte)0;   // sigmoid(v)>0.5 ⇔ v>0
                }
            }
            using var mFull = new Mat();
            Cv2.Resize(mSmall, mFull, new Size(Size, Size), interpolation: InterpolationFlags.Linear);
            Cv2.Threshold(mFull, mFull, 127, 255, ThresholdTypes.Binary);
            var mBox = new Mat(Size, Size, MatType.CV_8UC1, Scalar.Black);
            using (var src = new Mat(mFull, k.Box))
            using (var dst = new Mat(mBox, k.Box))
                src.CopyTo(dst);
            res.Add(new TileDet(k.Cls, k.Conf, k.Box, mBox));
        }
        return res;
    }
}
