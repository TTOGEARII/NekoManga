using OpenCvSharp;
using OpenCvSharp.XPhoto;

namespace NekoManga.Core.Engine;

/// <summary>무늬 있는 바탕(스크린톤·줄무늬·속도선·빗금) 위의 글자 자리를 **둘레의 무늬를 옮겨** 메운다 — OpenCV xphoto의 shift-map 인페인팅.
/// 2026-11-26 사용자 "복구된 부분이 부자연스러워" · "여기도 뭉개졌어"(줄무늬 풍선, 망점·먹칠에 걸친 글): LaMa 만화판은 가는 선·망점을 잇지 못하고 회색으로 뭉갠다
/// (조각을 원본 해상도로 줄여 넣어도 같았다). 같은 자리를 실제 쪽 열 군데에서 견주니 shift-map은 줄무늬·망점·먹칠 경계를 그대로 이었다(Telea·FSR은 번지거나 흐렸다).
/// 무늬를 '그리는' 것이 아니라 가까운 자리의 픽셀을 그대로 가져오므로 톤의 주기·굵기가 유지된다. 쓰는 자리는 <see cref="EmbeddedTranslator.EraseBlock"/> —
/// 구멍 둘레가 평평하지 않은 블록(<see cref="BubbleSolver.RegenStats"/>)만. 평평한 바탕은 단색 칠하기·LaMa가 맡는다.
/// 알고 둔 것(실측): 같은 입력에는 늘 같은 결과지만(난수 씨앗 셋이 같은 결과) **입력이 조금만 달라도 결과가 크게 달라진다**. 구멍이 풍선 테두리나 그 밖의 그림에 닿으면
/// 밖의 그림 덩어리를 통째로 가져오고(그래서 흰 테는 정말 테일 때만 구멍에 넣는다 — <see cref="BubbleSolver.TextHalo"/>), 둘레 범위를 60px로 줄이면 재료가 모자라 더 자주 틀린다(90이 낫다).
/// 셋 이상의 바탕이 만나는 자리(망점·흰 띠·먹칠)에서는 경계를 엉뚱한 데 긋기도 한다 — LaMa의 뭉개짐보다는 낫지만 완전하지 않다. 직접 짠 예시 기반 메우기(Criminisi)도 견줘 봤으나 실제 쪽에서 더 흔들렸다.
/// **되풀이 무늬(줄무늬·망점) 위의 구멍은 shift-map보다 먼저 <see cref="LatticeFill"/>로 메운다**(2026-11-27): 무늬의 격자만큼 옮겨 가져와 한 픽셀도 안 틀리고, 풍선 밖의 그림을 가져오지 않는다. 격자가 안 되는 창만 shift-map.
/// **구멍 밖으로 길게 이어지는 곧은 먹(칸 테두리·먹칠한 면)은 메우지 않는다**(<see cref="KeepLongLines"/>): 감지기 마스크는 획을 3px쯤 불린 것이라 칸 테두리에 붙은 글에서는 테두리까지 덮는다 — 그대로 메우면 테두리가 무늬에 파먹힌다.</summary>
internal static class TextureFill
{
    /// <summary>구멍 둘레에서 무늬를 가져오는 범위(px). 실측: 60이면 줄무늬 풍선에 밖의 머리카락이 들어오는 일이 잦았고, 130은 느리기만 했다.</summary>
    internal const int ContextPx = 90;
    /// <summary>메우는 범위 = 구멍을 이만큼 넓힌 것 — 획 가장자리의 안티에일리어싱까지.</summary>
    internal const int GrowPx = 2;
    /// <summary>창 하나에서 메울 픽셀의 상한 — 넘으면 하지 않는다(호출부가 LaMa로 넘긴다). 쪽의 반을 덮는 구멍에 분 단위로 매달리지 않게(11만 px에 20초쯤).</summary>
    internal const int MaxHolePx = 200_000;
    /// <summary>한 번에 돌리는 창의 수 — 기본 1(차례로). shift-map은 안에서 이미 여러 코어를 쓴다. 실측(2026-11-27, 6코어 12스레드, 실제 26쪽의 여덟 창):
    /// 앱 안에서(내장 모델이 올라간 채, 여유 메모리 3.7GB) 53초(1) · 71초(2) · 77초(4) — 나란히 돌리면 오히려 느려졌다. 모델 없는 시험 프로세스에서는 50초(1) · 43초(2) · 36초(4)로
    /// 조금 빨라지지만 그 이득보다 앱에서의 손해가 크다(처음에는 코어 수의 절반(최대 4)으로 돌렸다 — 여유 메모리가 넉넉하던 때 33초, 줄어든 뒤 77초로 들쭉날쭉했다). 결과는 몇 개씩 돌리든 같다.
    /// 진단: NEKO_TEXFILL_PAR=&lt;n&gt;으로 바꿔 잰다.</summary>
    internal static int Parallelism =>
        int.TryParse(Environment.GetEnvironmentVariable("NEKO_TEXFILL_PAR"), out int forced) && forced > 0 ? Math.Min(forced, 8) : 1;
    /// <summary><see cref="KeepLongLines"/>: 먹으로 보는 밝기(미만 — 번진 가장자리는 빼려고 낮게) · 곧게 이어진 길이가 이만큼(px)은 돼야 '긴 선' · 그 줄(곧게 이어진 한 구간)의 이 비율 넘게 구멍 밖에 있어야 남의 것(글자의 긴 획은 통째로 구멍 안이다).</summary>
    internal const int LineDark = 16, LineRunPx = 61;
    internal const double LineOutsideShare = 0.5;

    /// <summary>구멍(<paramref name="own"/>, 창 좌표)에서 '구멍 밖으로 길게 이어지는 곧은 먹'을 뺀다 — 가로든 세로든 <see cref="LineRunPx"/> 넘게 곧게 이어진 어두운 픽셀의 줄 가운데 절반 넘게 구멍 밖에 있는 것.
    /// 칸 테두리에 붙은 글(실제 26쪽: 테두리에 왼쪽이 잘린 줄)의 마스크는 테두리 위까지 번져 있다. 먹칠 위 흰 테 글자의 마스크 가장자리도 같다(바탕의 먹이다 — 두어도 된다).
    /// 글자의 획은 흰 테·바탕으로 둘러싸여 밖의 먹과 이어지지 않거나, 이어져도 곧게 <see cref="LineRunPx"/>씩 뻗지 않는다.
    /// 가는 줄(줄무늬 톤의 1px 줄, 가는 칸 테두리)도 남긴다: 글자를 지나 길게 이어진 줄에서 글자 획과 겹친 자리는 어차피 그 줄의 먹이다(남기면 줄이 글자 자리를 지나 그대로 이어진다).
    /// 줄 하나하나(열·행마다 이어진 구간)로 본다: 덩어리로 보면 테두리에 붙은 큰 글자의 긴 획이 테두리와 한 덩어리가 되어 '구멍 안이 더 많은 것' = 글자로 읽힌다.
    /// **남기는 줄의 이웃 픽셀은 남기지 않는다**: 테두리의 흐린 가장자리 1px을 지키려고 이웃까지 남기면, 먹칠 위 글자의 흰 테가 먹에 닿은 한 줄이 남아 글자 모양의 흰 윤곽이 된다(시험 TryFill_HoleInStripes_IsExact가 90px로 잡음).
    /// **새까만 픽셀(&lt; <see cref="LineDark"/> 16)만 먹으로 센다**: 흰 테에 닿은 먹의 가장자리는 번져서 30~110쯤이다 — 그것까지 남기면 먹칠 위 글자를 지운 자리에 글자 모양의 희끄무레한 윤곽이 남는다
    /// (실제 26쪽 "見たこと": 110 미만을 먹으로 셀 때도, 48 미만으로 낮췄을 때도 밝기 30짜리 가장자리 한 줄이 남았다). 먹이 16을 넘는 거친 스캔에서는 이 지키기가 듣지 않는다(전처럼 메운다).</summary>
    internal static void KeepLongLines(Mat patch, Mat own)
    {
        using var gray = patch.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, LineDark - 1, 255, ThresholdTypes.BinaryInv);
        using var down = new Mat(); using var across = new Mat();
        using (var vertical = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, LineRunPx)))
        using (var horizontal = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(LineRunPx, 1)))
        {
            Cv2.MorphologyEx(dark, down, MorphTypes.Open, vertical);
            Cv2.MorphologyEx(dark, across, MorphTypes.Open, horizontal);
        }
        using var touching = new Mat();
        Cv2.BitwiseOr(down, across, touching);
        Cv2.BitwiseAnd(touching, own, touching);
        if (Cv2.CountNonZero(touching) == 0) return;

        // 곧은 줄 하나하나: 세로는 열마다, 가로는 행마다 이어진 구간
        int w = own.Cols, h = own.Rows;
        own.GetArray(out byte[] hole);
        down.GetArray(out byte[] v);
        across.GetArray(out byte[] a);
        var keepBytes = new byte[w * h];
        void Runs(byte[] line, int count, int length, Func<int, int, int> at)
        {
            for (int k = 0; k < count; k++)
                for (int p = 0; p < length;)
                {
                    if (line[at(k, p)] == 0) { p++; continue; }
                    int start = p, inHole = 0;
                    for (; p < length && line[at(k, p)] != 0; p++) if (hole[at(k, p)] != 0) inHole++;
                    int run = p - start;
                    if (inHole == 0 || run - inHole < LineOutsideShare * run) continue;
                    for (int q = start; q < p; q++) keepBytes[at(k, q)] = 255;
                }
        }
        Runs(v, w, h, (x, y) => y * w + x);
        Runs(a, h, w, (y, x) => y * w + x);
        using var keep = new Mat(h, w, MatType.CV_8UC1);
        keep.SetArray(keepBytes);
        own.SetTo(Scalar.Black, keep);
    }

    /// <summary><paramref name="img"/>(BGR)의 구멍들을 둘레의 무늬로 메운다. 일감마다 <c>Area</c>(쪽 좌표의 창)와 <c>Own</c>(그 창 크기의 마스크, 255 = **이 블록의** 지울 자리)를 받는다. 돌려주는 값 = 일감마다 됐는가.
    /// 창 안에 다른 블록의 획이 걸쳐 있어도 메우지 않는다(그 블록의 일감이 맡는다 — 쪽 마스크를 창으로 자르면 섞인다, 검토 2026-11-27).
    /// <paramref name="avoid"/>(쪽 크기, 255 = 재료로 쓰면 안 되는 자리 — 쪽의 모든 글자 획과 지울 자리): 아직 지우지 않은 다른 블록의 글자를 무늬로 가져오지 않게 재료에서 뺀다(그 자리를 메우지는 않는다).
    /// 메울 자리가 없거나, 가져올 무늬가 모자라거나, 너무 크거나, 준비·메우기가 실패한 일감은 false — 그 자리의 그림은 그대로다(호출부가 LaMa로 넘긴다). 취소 말고는 던지지 않는다.
    /// 일감들은 **메우기 전의 그림**에서 따로 준비해 돌리고(<see cref="Parallelism"/> — 기본은 하나씩) 차례로 붙인다 — 몇 개씩, 어떤 순서로 돌든 같은 입력에는 같은 결과가 나온다.
    /// <paramref name="tag"/>: 진단 덤프(NEKO_TEXFILL_DBG)의 파일 이름 앞머리 — 쪽마다 다르게 주면 여러 쪽의 덤프가 서로 덮지 않는다.</summary>
    internal static bool[] FillAll(Mat img, IReadOnlyList<(Rect Area, Mat Own)> holes, Mat avoid, CancellationToken ct = default, string? tag = null) =>
        FillAll(img, holes, avoid, out _, ct, tag);

    /// <summary><see cref="FillAll(Mat, IReadOnlyList{ValueTuple{Rect, Mat}}, Mat, CancellationToken, string)"/>와 같되 일감마다 **되풀이 무늬를 격자만큼 옮겨 그대로 이었는가**도 알려 준다
    /// (<paramref name="byLattice"/> — 둘레와 똑같은 무늬다. 베껴 메운 것(shift-map)·못 메운 것·메울 것이 없던 것은 false). 그대로 이은 자리는 SDXL로 다시 그릴 까닭이 없다.</summary>
    internal static bool[] FillAll(Mat img, IReadOnlyList<(Rect Area, Mat Own)> holes, Mat avoid, out bool[] byLattice, CancellationToken ct = default, string? tag = null)
    {
        var done = new bool[holes.Count];
        byLattice = new bool[holes.Count];
        var jobs = new Job?[holes.Count];
        try
        {
            for (int i = 0; i < holes.Count; i++)
            {
                // 준비(창 자르기·칸 테두리 가리기)의 예외도 그 일감의 실패일 뿐이다(Run과 같은 까닭 — 밖으로 나가면 그 장이 통째로 실패한다)
                try { jobs[i] = Job.Prepare(img, holes[i].Own, avoid, holes[i].Area, tag); }
                catch (Exception ex) when (ex is not OperationCanceledException) { jobs[i] = null; }
            }
            Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, i => jobs[i]?.Run());
            for (int i = 0; i < jobs.Length; i++)
            {
                if (jobs[i] is { Nothing: true }) { done[i] = true; continue; }   // 구멍이 전부 칸 테두리였다 — 메울 것이 없다(LaMa로 넘기지도 않는다)
                if (jobs[i] is not { Filled: { } filled } job) continue;
                using var target = new Mat(img, job.Win);
                filled.CopyTo(target, job.Own);
                done[i] = true;
                byLattice[i] = job.ByLattice;
            }
        }
        finally { foreach (var j in jobs) j?.Dispose(); }
        return done;
    }

    /// <summary>창 하나의 일감 — 넣을 그림(창을 자른 사본)·재료 마스크·구멍, 그리고 결과.</summary>
    private sealed class Job(Rect area, Rect win, Mat patch, Mat valid, Mat own, string? tag) : IDisposable
    {
        public Rect Win => win;
        public Mat Own => own;
        public Mat? Filled { get; private set; }
        /// <summary>칸 테두리를 빼고 나니 메울 자리가 남지 않았다.</summary>
        public bool Nothing { get; private init; }
        /// <summary>메운 방법(진단용): lattice | shiftmap.</summary>
        public string Method { get; private set; } = "";
        /// <summary>격자만큼 옮겨 메웠다(되풀이 무늬를 그대로 이었다) — <see cref="Filled"/>가 있을 때만 뜻이 있다.</summary>
        public bool ByLattice { get; private set; }

        /// <summary><paramref name="hole"/>: <paramref name="area"/> 크기의 마스크(이 블록의 구멍).</summary>
        public static Job? Prepare(Mat img, Mat hole, Mat avoid, Rect area, string? tag)
        {
            if (img.Type() != MatType.CV_8UC3) return null;
            var bounds = new Rect(0, 0, img.Width, img.Height);
            if (area.Width <= 0 || area.Height <= 0 || (area & bounds) != area || hole.Empty() || hole.Size() != area.Size) return null;
            var win = new Rect(area.X - ContextPx, area.Y - ContextPx, area.Width + 2 * ContextPx, area.Height + 2 * ContextPx) & bounds;
            if (win.Width < 16 || win.Height < 16) return null;

            // 구멍을 창 좌표로
            Mat? own = new Mat(win.Height, win.Width, MatType.CV_8UC1, Scalar.Black), valid = null, patch = null;
            try
            {
                using (var dst = new Mat(own, new Rect(area.X - win.X, area.Y - win.Y, area.Width, area.Height)))
                    hole.CopyTo(dst);
                using var growKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * GrowPx + 1, 2 * GrowPx + 1));
                Cv2.Dilate(own, own, growKernel);
                int holePx = Cv2.CountNonZero(own);
                if (holePx == 0 || holePx > MaxHolePx) return null;

                // 재료 = 창에서 구멍과 다른 글자를 뺀 곳. xphoto의 마스크는 0 = 메울 자리, 그 밖 = 재료
                using var invalid = new Mat();
                using (var others = new Mat(avoid, win)) Cv2.Dilate(others, invalid, growKernel);
                Cv2.BitwiseOr(invalid, own, invalid);
                int validPx = win.Width * win.Height - Cv2.CountNonZero(invalid);
                if (validPx < holePx / 2 || validPx < 400) return null;   // 구멍보다 한참 적은 재료로는 무늬가 되풀이될 뿐이다
                valid = new Mat();
                Cv2.BitwiseNot(invalid, valid);
                using (var view = new Mat(img, win)) patch = view.Clone();
                KeepLongLines(patch, own);
                var made = new Job(area, win, patch, valid, own, tag) { Nothing = Cv2.CountNonZero(own) == 0 };
                (own, valid, patch) = (null, null, null);   // 일감이 맡았다
                return made;
            }
            finally { own?.Dispose(); valid?.Dispose(); patch?.Dispose(); }
        }

        /// <summary>메운다 — 못 하면 <see cref="Filled"/>가 null로 남는다(호출부가 LaMa로 넘긴다). **던지지 않는다**: 무늬 메우기는 보너스다.
        /// 한 창의 실패(격자 코드의 예기치 못한 예외 포함)가 Parallel.For를 타고 나가면 그 장이 통째로 실패한다(검토 2026-11-27).</summary>
        public void Run()
        {
            if (Nothing) return;
            Mat? filled = null;
            try
            {
                // 되풀이 무늬 위면 격자만큼 옮겨 메운다 — 아니면(null) shift-map
                filled = LatticeFill.TryFill(patch, valid, own, out string note);
                Method = "lattice: " + note;   // loc:none
                ByLattice = filled != null;
                if (filled == null)
                {
                    Method = "shiftmap (" + note + ")";   // loc:none
                    filled = new Mat();
                    CvXPhoto.Inpaint(patch, valid, filled, OpenCvSharp.XPhoto.InpaintTypes.SHIFTMAP);
                    if (filled.Empty() || filled.Size() != patch.Size() || filled.Type() != patch.Type()) { filled.Dispose(); return; }
                }
                // 진단: NEKO_TEXFILL_DBG=<폴더>면 창마다 넣은 그림·재료 마스크·구멍·결과·방법을 남긴다(실제 원고에서 엉뚱한 무늬를 가져온 까닭을 볼 때)
                if (Environment.GetEnvironmentVariable("NEKO_TEXFILL_DBG") is { Length: > 0 } dbg)
                    try
                    {
                        Directory.CreateDirectory(dbg);
                        string stem = Path.Combine(dbg, $"{(string.IsNullOrEmpty(tag) ? "" : tag + "_")}{area.X}_{area.Y}");   // loc:none
                        ImgIo.Write(stem + "_patch.png", patch); ImgIo.Write(stem + "_valid.png", valid); ImgIo.Write(stem + "_own.png", own); ImgIo.Write(stem + "_filled.png", filled);
                        File.WriteAllText(stem + "_method.txt", Method);
                    }
                    catch { }
                Filled = filled;
                filled = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            finally { filled?.Dispose(); }
        }

        public void Dispose()
        {
            patch.Dispose(); valid.Dispose(); own.Dispose(); Filled?.Dispose();
        }
    }
}
