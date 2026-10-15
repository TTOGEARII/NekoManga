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
            // 같은 페이지에서 AI가 놓친 '같은 규격' 막대 보강 — 모델은 얇거나 가장자리에 걸친
            // 막대를 더러 놓친다 (실측 15p: 막대 후보 15개 중 5개 미검출, conf를 낮춰도 그대로).
            // AI가 이미 찾은 막대의 두께를 기준 삼아 그와 비슷한 규칙 기반 후보만 더한다 —
            // 효과음 붓획은 두께·비율이 딴판이라 이 조건에서 걸러진다.
            AugmentBars(imgBgr, dets, w, isWhite: true, exclude);
            AugmentBars(imgBgr, dets, b, isWhite: false, exclude);

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
    /// <summary>AI가 찾은 막대와 '같은 규격'인 단색 덩어리를 마스크에 보강한다.
    /// 기준: 짧은 변이 AI 막대 중앙값의 0.5~2배, 길쭉하고 꽉 찬 사각형, 텍스트 영역 밖.
    /// 모델이 얇거나 가장자리에 걸친 막대를 놓치는 걸 메운다 (실측 15p: 15개 중 5개 미검출,
    /// conf를 0.2까지 낮춰도 그대로 — 신뢰도 문제가 아니라 아예 못 보는 것).</summary>
    private static void AugmentBars(Mat imgBgr, List<Detection> dets, Mat mask, bool isWhite,
        IReadOnlyList<Rect>? exclude)
    {
        int cls = isWhite ? 0 : 1;
        var found = dets.Where(d => d.Cls == cls).ToList();
        if (found.Count < 2) return;   // 기준 삼을 표본이 부족하면 보강하지 않는다
        var shorts = found.Select(d => (double)Math.Min(d.Box.Width, d.Box.Height)).OrderBy(x => x).ToList();
        double medShort = shorts[shorts.Count / 2];
        if (medShort < 4) return;

        using var gray = new Mat();
        Cv2.CvtColor(imgBgr, gray, ColorConversionCodes.BGR2GRAY);
        using var bin = new Mat();
        if (isWhite) Cv2.Threshold(gray, bin, 244, 255, ThresholdTypes.Binary);
        else Cv2.Threshold(gray, bin, 25, 255, ThresholdTypes.BinaryInv);
        using var bin0 = bin.Clone();   // 침식 전 원본 (되돌릴 때 경계 제한용)
        // 침식으로 얇은 연결을 끊는다 — 막대가 패널 테두리(검은 선)와 붙어 한 덩어리가 되면
        // 모양 조건(길쭉·꽉 참)에서 탈락해 통째로 놓친다 (실측 15p 우측 세로 패널).
        // 테두리는 몇 px라 끊기고, 두꺼운 막대는 남는다. 채택 후 같은 양만큼 되돌린다.
        int er = 3;
        using var ker = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(er * 2 + 1, er * 2 + 1));
        Cv2.Erode(bin, bin, ker);

        using var lab = new Mat();
        using var st = new Mat();
        using var ce = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, lab, st, ce);
        for (int i = 1; i < n; i++)
        {
            int area = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < 400) continue;   // 침식으로 줄어든 면적 기준
            var br = new Rect(
                st.At<int>(i, (int)ConnectedComponentsTypes.Left),
                st.At<int>(i, (int)ConnectedComponentsTypes.Top),
                st.At<int>(i, (int)ConnectedComponentsTypes.Width),
                st.At<int>(i, (int)ConnectedComponentsTypes.Height));
            int shortSide = Math.Min(br.Width, br.Height), longSide = Math.Max(br.Width, br.Height);
            // 규격 범위를 좁게 잡으면(0.5~2배) 같은 페이지의 조금 굵은 막대가 탈락한다 (실측 15p
            // 우측 패널 막대가 중앙값의 2.2배라 빠짐) — 0.4~3배로 넓히되 모양 조건으로 거른다.
            if (shortSide < medShort * 0.4 || shortSide > medShort * 3.0) continue;
            if (longSide / (double)Math.Max(1, shortSide) < 1.4) continue;           // 길쭉해야 막대
            if ((double)area / (br.Width * br.Height) < 0.75) continue;              // 꽉 찬 사각형
            if (exclude != null && exclude.Any(e => (e & br).Width > 0 && (e & br).Height > 0)) continue;
            using var comp = new Mat();
            Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
            Cv2.Dilate(comp, comp, ker);          // 침식한 만큼 되돌린다
            Cv2.BitwiseAnd(comp, bin0, comp);     // 원래 단색 영역 밖으로는 번지지 않게
            Cv2.BitwiseOr(mask, comp, mask);
        }
    }

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
