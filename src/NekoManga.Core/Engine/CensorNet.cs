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

    /// <summary>검출 임계 조정용 비상구 (환경변수 NEKO_CENSOR_CONF). 기본 0.40 —
    /// 0.45에서 내렸다: 실측(eval 94장)에서 진짜 검열 2장을 더 찾고 새 오탐은 0이었다.
    /// 특정 작품이 유난히 안 잡히거나 과하게 잡힐 때 재빌드 없이 돌려 보기 위한 것이다.</summary>
    private static readonly float? ConfOverride =
        float.TryParse(Environment.GetEnvironmentVariable("NEKO_CENSOR_CONF"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var cv) ? cv : null;

    public sealed record Detection(int Cls, float Conf, Rect Box);

    /// <summary>파이프라인용 최종 마스크 — 검출 + 말풍선 오탐 제거 + grow까지 한 곳에서.
    /// 흰 검출은 **성분 단위로** 텍스트 블록(exclude)과 20% 이상 겹치면 통째로 버린다 —
    /// 모델이 흰 말풍선을 흰 박스로 오인할 때 사각형 빼기만으로는 풍선 고리가 남았다 (실측 22p).
    /// 요약 문자열(흰 n·검 n·모자이크 n)을 함께 돌려준다. 충분한 검출이 없으면 null.</summary>
    /// <param name="bubbleFilter">false면 흰 성분의 말풍선 필터를 건너뛴다(검/모자이크의 글자 상자 빼기는 유지). 2026-10-20 실측: CTD가 흰 검열 막대를 글자로
    /// 잡아 필터가 진짜 흰선을 지웠다(Confusion 실라벨 78개 중 15% 기준 11개·60% 기준 6개 손실) → 설정 DecensorBubbleFilter로 끌 수 있게.</param>
    public Mat? BuildMask(Mat imgBgr, IReadOnlyList<Rect>? exclude, int grow, out string summary, bool bubbleFilter = true)
    {
        float strongConf = ConfOverride ?? 0.40f;
        var (w, b, m, dets) = Detect(imgBgr, strongConf);
        using (w) using (b) using (m)
        {
            // 흰: 성분 단위 말풍선 필터 — 텍스트 블록을 1.8배 부풀려 '풍선' 근사로 쓴다.
            // 모델의 풍선 오탐 마스크는 글자 박스보다 넓게 풍선 전체를 덮으므로(실측 22p 고리 잔존),
            // 부풀린 블록과 BubbleOverlap(기본 0.6 — 2026-10-20에 0.15에서 올림) 이상 겹치는 흰 성분은 통째로 버린다.
            if (exclude is { Count: > 0 } && bubbleFilter)
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
                    // 겹침 비율 기준은 실험용 환경변수 NEKO_BUBBLE_OVERLAP(기본 0.15)로 바꿔 볼 수 있다 — 2026-10-20 실측: 0.15는 말풍선 곁 진짜 흰 검열까지
                    // 통째로 버렸다(Confusion 실라벨 적중 49/17 → 42/13, 라벨 밖 발화는 162k → 93k).
                    if (inter >= area * BubbleOverlap)
                    {
                        using var comp = new Mat();
                        Cv2.InRange(lab, new Scalar(i), new Scalar(i), comp);
                        w.SetTo(Scalar.Black, comp);
                    }
                }
            }
            if (exclude is { Count: > 0 })
            {
                // 검/모자이크는 텍스트 블록 사각형만 빼준다 (글자 획 보호) — 말풍선 필터와 무관하게 늘 적용
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
            // 흰 보강의 글자 블록 제외도 말풍선 필터와 함께 끈다(2026-10-20 실측: 제외만으로도 17-P2(144×282)·17-P3·22-P5 흰선을 잃었다).
            AugmentBars(imgBgr, dets, w, isWhite: true, bubbleFilter ? exclude : null, strongConf);
            AugmentBars(imgBgr, dets, b, isWhite: false, exclude, strongConf);

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

    /// <summary>진단 로그 — 환경변수 NEKO_AUGDBG에 경로를 주면 검출·보강 판정 과정을 적는다.
    /// 어떤 막대가 왜 빠졌는지는 이 로그 없이는 추적이 거의 불가능하다 (실측으로 두 번 구제받음).</summary>
    private static readonly string? DbgPath = Environment.GetEnvironmentVariable("NEKO_AUGDBG");
    private static void Dbg(string line)
    {
        if (!string.IsNullOrEmpty(DbgPath))
            try { File.AppendAllText(DbgPath, line + Environment.NewLine); } catch { }
    }

    /// <summary>AI가 찾은 막대를 **기준 삼아** 같은 검열 뭉치에서 놓친 막대를 마스크에 보강한다.
    /// 2단 임계(<see cref="Detect"/>)가 대부분을 해결하므로 이건 모델이 아예 못 본 경우의 안전망이다.
    ///
    /// **회전 사각형(MinAreaRect)으로 재야 한다** — 예전 구현은 축정렬 bbox 채움비 ≥0.75를 썼는데,
    /// 대각선 막대는 축정렬 bbox가 0.24~0.39밖에 안 차서 **구조적으로 전부 탈락**했다 (실측 18p).
    /// 비스듬한 막대는 만화 검열에서 오히려 흔하다.
    ///
    /// 세 갈래로 더한다:
    ///  ⓐ **스냅** — 기준 막대(마스크와 30%↑ 겹침)를 검은 영역 전체로 넓힌다. AI 마스크는 proto
    ///     256²를 키운 거라 가장자리가 모자라 막대 끝이 가늘게 남는다.
    ///  ⓑ **구역** — 기준 막대들을 두께의 1.5배 부풀린 '검열 구역' 안(60%↑)의 단색 덩어리는
    ///     모양을 안 따지고 받는다. 막대 둘이 모서리에서 만난 갈고리(⌐) 모양은 채움비 0.34에
    ///     두 팔 다 비율 2.2/2.4라 어떤 모양 규칙으로도 못 잡는다 (실측 18p).
    ///  ⓒ **규격** — 구역 밖이면 '같은 두께(0.5~2배)·같은 각도(±25°)·가까움·꽉 참'을 전부 요구한다.
    ///     머리카락·먹칠·패널 테두리는 이 넷을 동시에 만족하지 못한다.
    /// 각도는 길쭉할 때(비율≥2)만 믿는다 — 뭉툭한 조각은 MinAreaRect 방향이 흔들린다.</summary>
    private static void AugmentBars(Mat imgBgr, List<Detection> dets, Mat mask, bool isWhite,
        IReadOnlyList<Rect>? exclude, float strongConf = 0.40f)
    {
        int cls = isWhite ? 0 : 1;
        if (!dets.Any(d => d.Cls == cls)) return;   // 이 종류 검출이 없으면 기준이 없다
        if (Cv2.CountNonZero(mask) < 200) return;

        using var gray = new Mat();
        Cv2.CvtColor(imgBgr, gray, ColorConversionCodes.BGR2GRAY);
        using var bin = new Mat();
        if (isWhite) Cv2.Threshold(gray, bin, 244, 255, ThresholdTypes.Binary);
        else Cv2.Threshold(gray, bin, 25, 255, ThresholdTypes.BinaryInv);
        using var bin0 = bin.Clone();   // 침식 전 원본 (되돌릴 때 경계 제한용)
        // 침식으로 얇은 연결을 끊는다 — 막대가 패널 테두리(검은 선)와 붙어 한 덩어리가 되면
        // 모양 조건에서 탈락해 통째로 놓친다 (실측 15p 우측 세로 패널).
        // 테두리는 몇 px라 끊기고, 두꺼운 막대는 남는다. 채택 후 같은 양만큼 되돌린다.
        int er = 3;
        using var ker = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(er * 2 + 1, er * 2 + 1));
        Cv2.Erode(bin, bin, ker);

        using var lab = new Mat();
        using var st = new Mat();
        using var ce = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, lab, st, ce);

        // **기준 막대의 두께 상한** — AI가 그린 상자의 짧은 변을 기준으로 삼는다. 흰 막대가 흰 살결·배경과
        // 한 덩어리로 이어진 페이지에서는 마스크와 30% 겹치는 성분이 '막대'가 아니라 **페이지 절반짜리 흰
        // 면**이 된다 (실측 mashiro Confusion 11p: refThick 347, 구역이 페이지 대부분을 덮어 흰 성분 84개를
        // 전부 TAKE → 마스크 50k→427k px, 30p도 359k). 상자보다 2.5배 이상 두꺼운 성분은 AI가 본 막대일 수
        // 없으니 기준에서 뺀다. 기준이 하나도 안 남으면 보강 자체를 하지 않는다 (AI 마스크는 그대로).
        // 검출 두께는 **상자 안 마스크 면적 / 상자 긴 변**으로 잰다 — 축정렬 상자의 짧은 변은 대각선 막대에서 (L+t)/√2로
        // 부풀어 상한이 무력화된다(45° 300×30 막대 → 상자 233², 상한 583). 마스크 면적으로 재면 45°에서도 ≈√2·t.
        // 기준은 **강한 검출만** — 약한 검출(0.18~0.40)은 막대 끝 조각이라 중앙값을 끌어내려 보강을 통째로 끈다.
        double DetThick(Detection d)
        {
            var r = d.Box & new Rect(0, 0, mask.Cols, mask.Rows);
            if (r.Width <= 0 || r.Height <= 0) return Math.Min(d.Box.Width, d.Box.Height);
            using var mb = new Mat(mask, r);
            double a = Cv2.CountNonZero(mb);
            return a > 0 ? a / Math.Max(r.Width, r.Height) : Math.Min(d.Box.Width, d.Box.Height);   // 풍선 필터로 지워진 검출은 상자값
        }
        var shorts = dets.Where(d => d.Cls == cls && d.Conf >= strongConf).Select(DetThick).OrderBy(x => x).ToList();
        if (shorts.Count == 0) shorts = dets.Where(d => d.Cls == cls).Select(DetThick).OrderBy(x => x).ToList();
        double detShort = shorts[shorts.Count / 2];
        double maxRefThick = Math.Max(24, detShort * 2.5);

        // ── 1단계: 성분별 회전 사각형 기하 + 기존 마스크와의 겹침을 잰다 ──
        // 겹침(ov)으로 셋으로 나뉜다: 기준(≥30%, AI가 제대로 잡음) / 부분(2~30%, AI가 모서리만
        // 스쳐 **잔존의 주범**) / 미검출(≤2%). 부분·미검출 둘 다 후보로 올려 규격을 본다.
        var refs = new List<(int Label, Point2f C, double Thick, double Ang, double Aspect, double Fill)>();
        var cands = new List<(int Label, Point2f C, double Thick, double Ang, double Aspect, double Fill, double Ov, Rect Br, int Area, double Stroke)>();
        for (int i = 1; i < n; i++)
        {
            int area = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < 250) continue;   // 침식으로 줄어든 면적 기준
            var br = new Rect(
                st.At<int>(i, (int)ConnectedComponentsTypes.Left),
                st.At<int>(i, (int)ConnectedComponentsTypes.Top),
                st.At<int>(i, (int)ConnectedComponentsTypes.Width),
                st.At<int>(i, (int)ConnectedComponentsTypes.Height));

            using var compRoi = new Mat();
            using (var labRoi = new Mat(lab, br))
                Cv2.InRange(labRoi, new Scalar(i), new Scalar(i), compRoi);
            using var pts = new Mat();
            Cv2.FindNonZero(compRoi, pts);
            if (pts.Empty()) continue;
            var rr = Cv2.MinAreaRect(pts);
            double w = rr.Size.Width, h = rr.Size.Height;
            if (w < 2 || h < 2) continue;
            double thick = Math.Min(w, h), len = Math.Max(w, h);
            double fill = area / (w * h), aspect = len / thick;
            // 획 두께 추정 = 면적/긴 변. 갈고리(⌐)·십자는 MinAreaRect 짧은 변이 '팔 길이'라 두께가 아니다 — 두 팔 20px짜리
            // 갈고리가 53×53으로 재진다(검토 실측). 면적/긴 변은 막대·볼록 덩어리에선 짧은 변과 같고 갈고리에선 ≤2×획이다.
            double stroke = area / Math.Max(1.0, len);
            // 긴 축의 방향 (0~180°)
            double ang = rr.Angle;
            if (w < h) ang += 90;
            ang = ((ang % 180) + 180) % 180;
            var center = new Point2f(rr.Center.X + br.X, rr.Center.Y + br.Y);

            // 기존(AI) 마스크와 얼마나 겹치나
            using var mRoi = new Mat(mask, br);
            using var inter = new Mat();
            Cv2.BitwiseAnd(compRoi, mRoi, inter);
            double ov = Cv2.CountNonZero(inter) / (double)area;

            Dbg($"   comp{i} area={area} thick={thick:F1} len={len:F1} asp={aspect:F2} fill={fill:F2} ang={ang:F0} ov={ov:F2} br={br.Width}x{br.Height}@({br.X},{br.Y})");
            if (ov >= 0.30)
            {
                // 기준: 마스크와 겹치고, AI 상자 규격에 맞는 두께여야 한다. 너무 두꺼운 건 흰 면/배경이다.
                if (thick <= maxRefThick && fill >= 0.45) { refs.Add((i, center, thick, ang, aspect, fill)); continue; }
                // 기준에서 빠져도 후보로는 남긴다 — 맞닿은/교차한 막대 쌍(X)은 짧은 변이 팔 길이라 여기서 빠지는데,
                // 다른 기준의 구역 안에 있으면 획 두께 기준으로 아래에서 받을 수 있다.
                Dbg($"   ref-reject thick={thick:F1} (max {maxRefThick:F0}) fill={fill:F2} → 후보");
            }
            if (exclude != null && exclude.Any(e => (e & br).Width > 0 && (e & br).Height > 0)) continue;
            cands.Add((i, center, thick, ang, aspect, fill, ov, br, area, stroke));
        }
        Dbg($"[{(isWhite ? "W" : "B")}] refs={refs.Count} cands={cands.Count}");
        if (refs.Count == 0) return;

        double refThick = refs.Select(r => r.Thick).OrderBy(x => x).ElementAt(refs.Count / 2);
        Dbg($"   refThick={refThick:F1}");
        if (refThick < 3) return;

        // 채택한 성분을 마스크에 더한다 (침식한 만큼 되돌리고 원래 단색 영역 안으로 제한).
        void Take(int label)
        {
            using var comp = new Mat();
            Cv2.InRange(lab, new Scalar(label), new Scalar(label), comp);
            Cv2.Dilate(comp, comp, ker);
            Cv2.BitwiseAnd(comp, bin0, comp);
            Cv2.BitwiseOr(mask, comp, mask);
        }

        // ── 2단계-a: 기준 막대를 '검은 영역 전체'로 넓힌다 (스냅) ──
        // AI 마스크는 proto 256²를 키운 거라 가장자리가 모자라 막대 끝이 가늘게 남는다.
        // 막대 모양이 확실한 기준 성분은 통째로 넣어 꼬리까지 지운다.
        foreach (var r in refs)
            if (r.Fill >= 0.70 && r.Aspect >= 1.25 && r.Thick <= refThick * 2.0)
                Take(r.Label);

        // ── 2단계-b: '검열 구역' 안의 단색 덩어리는 전부 검열로 본다 ──
        // 검열은 한 자리(성기 등)에 막대 여러 개를 몰아 덮는다. 확인된 막대들을 두께의 1.5배만큼
        // 부풀린 구역은 그 검열 뭉치의 영역이고, 그 안의 단색 덩어리는 모양이 어떻든 막대다.
        // 모양 규칙만으로는 못 잡는 것들이 여기서 잡힌다 — 실측 18p의 **갈고리(⌐) 모양**
        // (막대 둘이 모서리에서 만나 fill 0.34, 두 팔 다 비율 2.2/2.4라 띠 분해도 실패).
        using var zone = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.Black);
        foreach (var r in refs)
        {
            using var comp = new Mat();
            Cv2.InRange(lab, new Scalar(r.Label), new Scalar(r.Label), comp);
            Cv2.BitwiseOr(zone, comp, zone);
        }
        // 구역 반지름은 60px에서 막는다 — 두꺼운 기준(≤상한)이라도 구역이 페이지로 번지면 안 된다.
        int zr = Math.Max(4, Math.Min(60, (int)(refThick * 1.5)));
        Cv2.Dilate(zone, zone, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(zr * 2 + 1, zr * 2 + 1)));

        foreach (var c in cands)
        {
            using var compRoi = new Mat();
            using (var labRoi = new Mat(lab, c.Br))
                Cv2.InRange(labRoi, new Scalar(c.Label), new Scalar(c.Label), compRoi);
            using var zRoi = new Mat(zone, c.Br);
            using var inZone = new Mat();
            Cv2.BitwiseAnd(compRoi, zRoi, inZone);
            double inside = Cv2.CountNonZero(inZone) / (double)c.Area;

            // 구역 안(60% 이상)이면 모양을 따지지 않는다 — 단 **획 두께**(면적/긴 변)만은 기준의 3배 안이어야 한다
            // (구역에 걸친 흰 면·말풍선이 통째로 들어오는 것 방지). 짧은 변으로 재면 갈고리·십자가 팔 길이에 걸려
            // 떨어지므로 획 두께를 쓴다. 바깥이면 '같은 규격·방향·근처'를 요구한다.
            bool ok = inside >= 0.60 && c.Stroke <= refThick * 3.0;
            if (!ok && c.Fill >= 0.70 && c.Aspect >= 1.25
                && c.Thick >= refThick * 0.5 && c.Thick <= refThick * 2.0)
                foreach (var r in refs)
                {
                    // 각도는 길쭉할 때만 믿는다 — 뭉툭한 조각(비율<2)은 MinAreaRect 방향이 흔들린다
                    // (실측: 같은 뭉치의 짧은 막대가 9°로 재져 53°짜리 기준과 44° 어긋났다).
                    if (c.Aspect >= 2.0)
                    {
                        double da = Math.Abs(c.Ang - r.Ang);
                        if (da > 90) da = 180 - da;               // 각도는 180° 주기
                        if (da > 25) continue;
                    }
                    double dx = c.C.X - r.C.X, dy = c.C.Y - r.C.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > Math.Max(80.0, refThick * 8)) continue;
                    ok = true; break;
                }
            Dbg($"   {(ok ? "TAKE" : "drop")} thick={c.Thick:F1} stroke={c.Stroke:F1} ang={c.Ang:F0} asp={c.Aspect:F1} ov={c.Ov:F2} in={inside:F2} c=({c.C.X:F0},{c.C.Y:F0})");
            if (ok) Take(c.Label);
        }
    }

    /// <summary>페이지를 타일로 훑어 클래스별 마스크와 검출 목록을 만든다.
    ///
    /// **2단 임계(conf / weakConf)** — 검열 막대는 한 자리에 여러 개가 몰려 있는데 모델은 그중
    /// 일부만 자신 있게 잡는다. 실측(Chipa 18p): 막대 6개 중 3개가 0.93~0.76인데 나머지는
    /// 0.43·0.40·0.18이라 단일 임계 0.45에서 잘려 **결과에 통째로 남았다**(사용자 보고 '검은선 잔존').
    /// 그렇다고 임계를 통째로 내리면 깨끗한 페이지의 오탐이 늘어난다.
    ///
    /// 그래서 **강한 검출(≥conf)이 그 페이지에 censorship이 있다는 근거**가 되고, 그 **곁에 있는
    /// 약한 검출(≥weakConf)만** 추가로 받는다. 모델이 이미 그 자리를 '막대'라고 본 것이라
    /// 기하학적 추측보다 안전하고, 마스크도 모델이 그린 그대로 쓴다.
    /// 근접 판정: 강한 검출 상자를 자기 긴 변만큼 사방으로 부풀려 겹치면 같은 뭉치로 본다.</summary>
    public (Mat White, Mat Black, Mat Mosaic, List<Detection> Dets) Detect(Mat imgBgr,
        float conf = 0.40f, float weakConf = 0.18f)
    {
        int W = imgBgr.Cols, H = imgBgr.Rows;
        var white = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var black = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var mosaic = new Mat(H, W, MatType.CV_8UC1, Scalar.Black);
        var dets = new List<Detection>();
        float lo = Math.Min(conf, weakConf);
        var weak = new List<(int Cls, float Conf, Rect Box, Mat Crop)>();   // Box·Crop은 페이지 좌표

        // **다중 배율(2026-10-20)**: 모델은 1024 타일을 원본 배율로 훑는데, 큰 페이지(Chipa 1998×2880)에서는 막대가 학습 배율보다
        // 커져 거의 못 본다(실측 c2: 원본 32쪽 중 1쪽, 폭 1200 축소본 5쪽, 폭 800 축소본 7쪽 — 셋이 서로 다른 페이지를 잡는다).
        // 그래서 축소본에서도 훑어 마스크를 페이지 좌표로 합친다. 배율 목록은 NEKO_MS_SCALES(기본 "1" = 원본만, 측정 뒤 결정).
        foreach (float sc in Scales(W, H))
        {
            Mat img = imgBgr;
            if (sc != 1f)
            {
                img = new Mat();
                Cv2.Resize(imgBgr, img, new Size(Math.Max(1, (int)Math.Round(W * sc)), Math.Max(1, (int)Math.Round(H * sc))), interpolation: InterpolationFlags.Area);
            }
            int Ws = img.Cols, Hs = img.Rows;
            // sc==1이면 페이지 마스크에 바로 쓴다(해제 금지). 축소 배율은 자기 크기 마스크에 모았다가 끝에 키워 합친다.
            Mat sW = sc == 1f ? white : new Mat(Hs, Ws, MatType.CV_8UC1, Scalar.Black);
            Mat sB = sc == 1f ? black : new Mat(Hs, Ws, MatType.CV_8UC1, Scalar.Black);
            Mat sM = sc == 1f ? mosaic : new Mat(Hs, Ws, MatType.CV_8UC1, Scalar.Black);
            foreach (var (tx, ty) in Tiles(Ws, Hs))
            {
                int tw = Math.Min(Size, Ws - tx), th = Math.Min(Size, Hs - ty);
                using var tile = new Mat(Size, Size, MatType.CV_8UC3, new Scalar(255, 255, 255));
                using (var src = new Mat(img, new Rect(tx, ty, tw, th)))
                using (var dst = new Mat(tile, new Rect(0, 0, tw, th)))
                    src.CopyTo(dst);

                foreach (var d in RunTile(tile, lo))
                {
                    using var m = d.Mask;
                    var rawBox = new Rect(d.Box.X + tx, d.Box.Y + ty, d.Box.Width, d.Box.Height);
                    var sBox = rawBox & new Rect(0, 0, Ws, Hs);
                    if (sBox.Width <= 0 || sBox.Height <= 0) continue;

                    // **타일 흰색 패딩이 만드는 유령 검출을 버린다.** 페이지가 1024보다 작으면 타일의
                    // 남는 자리를 흰색으로 채우는데, 모델이 그 경계를 '흰 막대'로 본다 (실측 mashiro
                    // 60p: 816px 폭 페이지에서 190x361 상자가 잡혀 잘린 뒤 3x361 쪽자만 남았다).
                    // 상자의 절반 이상이 페이지 밖이면 패딩을 본 것이고, 잘린 뒤 너무 얇아도 마찬가지다.
                    double inPage = (double)sBox.Width * sBox.Height / Math.Max(1, rawBox.Width * rawBox.Height);
                    if (inPage < 0.5 || Math.Min(sBox.Width, sBox.Height) < 6)
                    {
                        Dbg($"   padding-ghost drop cls{d.Cls} conf{d.Conf:F2} s={sc:F2} raw={rawBox.Width}x{rawBox.Height} → {sBox.Width}x{sBox.Height}@({sBox.X},{sBox.Y}) inPage={inPage:F2}");
                        continue;
                    }
                    // 페이지 좌표 상자
                    var pageBox = sc == 1f ? sBox : new Rect((int)Math.Round(sBox.X / sc), (int)Math.Round(sBox.Y / sc),
                        Math.Max(1, (int)Math.Round(sBox.Width / sc)), Math.Max(1, (int)Math.Round(sBox.Height / sc))) & new Rect(0, 0, W, H);
                    if (pageBox.Width <= 0 || pageBox.Height <= 0) continue;

                    // 모자이크는 보통 한 자리에 한 덩어리라 2단 임계의 '강한 앵커'가 없다 → 클래스별 임계(NEKO_CENSOR_CONF_MOSAIC, 기본 = conf)로 실험(2026-10-20).
                    float clsConf = d.Cls == 2 && MosaicConf is { } mc ? mc : d.Cls == 1 && BlackConf is { } bc ? bc : conf;
                    if (d.Conf < clsConf)
                    {
                        // 약한 검출은 상자 크기의 마스크 조각만 들고 있다가 나중에 판정한다 (페이지 배율로 맞춰 둔다)
                        var tileBox = new Rect(sBox.X - tx, sBox.Y - ty, sBox.Width, sBox.Height);
                        var crop = new Mat(m, tileBox).Clone();
                        if (sc != 1f)
                        {
                            var up = new Mat();
                            Cv2.Resize(crop, up, new Size(pageBox.Width, pageBox.Height), interpolation: InterpolationFlags.Linear);
                            Cv2.Threshold(up, up, 127, 255, ThresholdTypes.Binary);
                            crop.Dispose(); crop = up;
                        }
                        weak.Add((d.Cls, d.Conf, pageBox, crop));
                        continue;
                    }
                    // 타일 겹침 영역의 중복은 마스크 OR라 자연히 합쳐진다
                    using (var roiW = new Mat(d.Cls == 0 ? sW : d.Cls == 1 ? sB : sM, new Rect(tx, ty, tw, th)))
                    using (var mCrop = new Mat(m, new Rect(0, 0, tw, th)))
                        Cv2.BitwiseOr(roiW, mCrop, roiW);
                    dets.Add(new Detection(d.Cls, d.Conf, pageBox));
                }
            }
            if (sc != 1f)
            {
                foreach (var (src, dst) in new[] { (sW, white), (sB, black), (sM, mosaic) })
                {
                    using var up = new Mat();
                    Cv2.Resize(src, up, new Size(W, H), interpolation: InterpolationFlags.Linear);
                    Cv2.Threshold(up, up, 127, 255, ThresholdTypes.Binary);
                    Cv2.BitwiseOr(dst, up, dst);
                }
                sW.Dispose(); sB.Dispose(); sM.Dispose(); img.Dispose();
            }
        }

        // 약한 검출 채택: 같은 클래스의 강한 검출 뭉치 곁에 있는 것만.
        // 기준은 **강한 검출만** 쓴다 (스냅샷) — 방금 받은 약한 것을 다시 기준으로 삼으면
        // 꼬리를 물고 번져 엉뚱한 데까지 이어진다.
        var strong = dets.ToList();
        static bool Touches(Rect a, Rect b)
        {
            int pad = Math.Max(a.Width, a.Height);
            var grown = new Rect(a.X - pad, a.Y - pad, a.Width + pad * 2, a.Height + pad * 2);
            var ix = grown & b;
            return ix.Width > 0 && ix.Height > 0;
        }
        // **약한 뭉치(2026-10-20, 실험 — 환경변수 NEKO_WEAK_CLUSTER="<최소 개수>:<최고 conf>" 로 켠다, 기본 꺼짐)**:
        // 강한 앵커가 하나도 없는 페이지라도 **검은 막대(cls1)** 약한 검출이 서로 곁에 ≥min개 모여 있고 그중 최고 conf가 ≥top이면
        // 검열로 본다. 실측(eval 검은선 Dere-kumo 11·15, Chipa 17): 막대 2~6개가 0.29·0.24·0.19… 로 모여 있는데 0.40 앵커가 없어
        // 통째로 빠졌다(사용자 "파란선으로 잡힌 것도 마스크에 들어가야"). 흰/모자이크는 오탐 위험이 커 제외.
        bool[] accepted = new bool[weak.Count];
        if (WeakCluster is { } wcfg)
        {
            for (int i = 0; i < weak.Count; i++)
            {
                if (weak[i].Cls != 1 || strong.Any(s => s.Cls == 1 && Touches(s.Box, weak[i].Box))) continue;
                int n = 1; float top = weak[i].Conf;
                for (int j = 0; j < weak.Count; j++)
                {
                    if (j == i || weak[j].Cls != 1) continue;
                    if (Touches(weak[i].Box, weak[j].Box) || Touches(weak[j].Box, weak[i].Box)) { n++; top = Math.Max(top, weak[j].Conf); }
                }
                if (n >= wcfg.Min && top >= wcfg.Top) accepted[i] = true;
            }
        }
        for (int i = 0; i < weak.Count; i++)
        {
            var (cls, wc, box, crop) = weak[i];
            using (crop)
            {
                bool near = strong.Any(s => s.Cls == cls && Touches(s.Box, box));
                bool cluster = !near && accepted[i];
                Dbg($"   weak cls{cls} conf{wc:F2} {box.Width}x{box.Height}@({box.X},{box.Y}) → {(near ? "ACCEPT" : cluster ? "ACCEPT(cluster)" : "reject")}");
                if (!near && !cluster) continue;
                using (var roi = new Mat(cls == 0 ? white : cls == 1 ? black : mosaic, box))
                    Cv2.BitwiseOr(roi, crop, roi);
                dets.Add(new Detection(cls, wc, box));
            }
        }
        return (white, black, mosaic, dets);
    }

    private static readonly double BubbleOverlap =
        double.TryParse(Environment.GetEnvironmentVariable("NEKO_BUBBLE_OVERLAP"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bo) ? bo : 0.6;

    /// <summary>검은 막대(cls1) 전용 강한 임계 — NEKO_CENSOR_CONF_BLACK (실험, 2026-10-20: c10은 검 리콜이 포화(107/107)라 임계를 올려 오탐만 깎을 수 있는지).</summary>
    private static readonly float? BlackConf =
        float.TryParse(Environment.GetEnvironmentVariable("NEKO_CENSOR_CONF_BLACK"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bcv) ? bcv : null;

    /// <summary>모자이크(cls2) 전용 강한 임계 — NEKO_CENSOR_CONF_MOSAIC. 없으면 공통 conf.</summary>
    private static readonly float? MosaicConf =
        float.TryParse(Environment.GetEnvironmentVariable("NEKO_CENSOR_CONF_MOSAIC"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mcv) ? mcv : null;

    /// <summary>약한 뭉치 채택 설정 (NEKO_WEAK_CLUSTER="2:0.25" → 최소 2개·최고 conf 0.25). 없으면 꺼짐.</summary>
    private static readonly (int Min, float Top)? WeakCluster = ParseWeakCluster(Environment.GetEnvironmentVariable("NEKO_WEAK_CLUSTER"));
    private static (int Min, float Top)? ParseWeakCluster(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(':');
        int min = p.Length > 0 && int.TryParse(p[0], out var m) ? m : 2;
        float top = p.Length > 1 && float.TryParse(p[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.25f;
        return (Math.Max(1, min), top);
    }

    /// <summary>훑을 배율 목록 — NEKO_MS_SCALES="1,0.6,0.4" (기본 "1"). 축소본의 짧은 변이 400px 아래로 내려가는 배율은 건너뛴다.</summary>
    private static readonly float[] ScaleList = (Environment.GetEnvironmentVariable("NEKO_MS_SCALES") ?? "1")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1f)
        .Where(v => v > 0.05f && v <= 1f).Distinct().OrderByDescending(v => v).ToArray();
    private static IEnumerable<float> Scales(int W, int H)
    {
        bool any = false;
        foreach (var s in ScaleList)
        {
            if (s != 1f && Math.Min(W, H) * s < 400) continue;
            any = true; yield return s;
        }
        if (!any) yield return 1f;
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
