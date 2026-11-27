using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.XPhoto;

namespace NekoManga.Core.Engine;

/// <summary>무늬 있는 바탕(스크린톤·줄무늬·속도선·빗금) 위의 글자 자리를 **둘레의 무늬를 옮겨** 메운다 — shift-map 인페인팅(<see cref="ShiftMap"/>: OpenCV xphoto의 것을 옮겨 와 헛도는 곳을 걷어 낸 것, 2026-11-28).
/// 2026-11-26 사용자 "복구된 부분이 부자연스러워" · "여기도 뭉개졌어"(줄무늬 풍선, 망점·먹칠에 걸친 글): LaMa 만화판은 가는 선·망점을 잇지 못하고 회색으로 뭉갠다
/// (조각을 원본 해상도로 줄여 넣어도 같았다). 같은 자리를 실제 쪽 열 군데에서 견주니 shift-map은 줄무늬·망점·먹칠 경계를 그대로 이었다(Telea·FSR은 번지거나 흐렸다).
/// 무늬를 '그리는' 것이 아니라 가까운 자리의 픽셀을 그대로 가져오므로 톤의 주기·굵기가 유지된다. 쓰는 자리는 <see cref="EmbeddedTranslator.EraseBlock"/> —
/// 구멍 둘레가 평평하지 않은 블록(<see cref="BubbleSolver.RegenStats"/>)만. 평평한 바탕은 단색 칠하기·LaMa가 맡는다.
/// 알고 둔 것(실측 — xphoto의 구현으로 잰 것이고 지금 구현도 같은 성질이다): 같은 입력에는 늘 같은 결과지만 **입력이 조금만 달라도 결과가 크게 달라진다**. 구멍이 풍선 테두리나 그 밖의 그림에 닿으면
/// 밖의 그림 덩어리를 통째로 가져오고(그래서 흰 테는 정말 테일 때만 구멍에 넣는다 — <see cref="BubbleSolver.TextHalo"/>), 둘레 범위를 60px로 줄이면 재료가 모자라 더 자주 틀린다(90이 낫다).
/// 셋 이상의 바탕이 만나는 자리(망점·흰 띠·먹칠)에서는 경계를 엉뚱한 데 긋기도 한다 — LaMa의 뭉개짐보다는 낫지만 완전하지 않다. 직접 짠 예시 기반 메우기(Criminisi)도 견줘 봤으나 실제 쪽에서 더 흔들렸다.
/// **되풀이 무늬(줄무늬·망점) 위의 구멍은 shift-map보다 먼저 <see cref="LatticeFill"/>로 메운다**(2026-11-27): 무늬의 격자만큼 옮겨 가져와 한 픽셀도 안 틀리고, 풍선 밖의 그림을 가져오지 않는다. 격자가 안 되는 창만 shift-map.
/// **둘레가 평평한 쪽은 무늬로 메우지 않고 그 색으로 칠한다**(<see cref="FlatPart"/>, 2026-11-27 속도): 격자가 안 되는 창에서, 선·무늬가 닿은 둘레 가까이와 구멍을 건너가는 선의 길만 shift-map으로 메운다 —
/// 남은 자리는 가까운 것끼리 묶어 묶음마다 제 창으로(<see cref="Groups"/>). 칠할 자리가 구멍의 절반(<see cref="FlatMinShare"/>)이 안 되는 창은 전과 똑같이 통째로 메운다.
/// 실측(앱 안, 2026-11-27): 실제 다섯 쪽의 무늬 메우기 68.1초 → 38.4초, 26쪽 121.1초 → 76.0초 — 달라진 창은 다섯(구멍의 71~96%를 칠함)뿐이고 나머지 38창의 결과는 전과 한 픽셀도 다르지 않다.
/// 빗금·옆 칸의 그림이 평평한 자리로 딸려 들어오던 것도 그 다섯에서 없어졌다. 속도선·톤 위의 큰 글(구멍 10만 px, 20초)은 그때 그대로였다 — 그 시간의 절반은 구멍과 무관한 xphoto의 헛일이었다(<see cref="ShiftMap"/>으로 바꿔 같은 창이 7초, 실제 한 화의 무늬 메우기 194초 → 73초).
/// **구멍 밖으로 길게 이어지는 곧은 먹(칸 테두리·먹칠한 면)은 메우지 않는다**(<see cref="KeepLongLines"/>): 감지기 마스크는 획을 3px쯤 불린 것이라 칸 테두리에 붙은 글에서는 테두리까지 덮는다 — 그대로 메우면 테두리가 무늬에 파먹힌다.</summary>
internal static class TextureFill
{
    /// <summary>구멍 둘레에서 무늬를 가져오는 범위(px). 실측: 60이면 줄무늬 풍선에 밖의 머리카락이 들어오는 일이 잦았고, 130은 느리기만 했다.</summary>
    internal const int ContextPx = 90;
    /// <summary>메우는 범위 = 구멍을 이만큼 넓힌 것 — 획 가장자리의 안티에일리어싱까지.</summary>
    internal const int GrowPx = 2;
    /// <summary>창 하나에서 메울 픽셀의 상한 — 넘으면 하지 않는다(호출부가 LaMa로 넘긴다). 쪽의 반을 덮는 구멍에 분 단위로 매달리지 않게(11만 px에 7초쯤 — xphoto의 구현으로는 20초였다).</summary>
    internal const int MaxHolePx = 200_000;
    /// <summary><see cref="FlatPart"/>: 둘레를 재는 고리(구멍 밖 px — 안쪽 5px은 건너뛴다: 글자 가장자리의 번짐·흰 테의 끝자락이 거기 남는다. 실측: 먹칠 위 흰 테 글자는 3~9px 고리에서 번짐이 사방에 걸려
    /// 구멍의 44%가 무늬로 갔고, 5~11px로 재면 그림에 닿은 곳만 남아 4%였다) · 평평하다고 보는 밝기의 폭(고리의 가운뎃값 ± 이만큼 — 스캔 잡음은 ±6쯤, 옅은 선은 25 넘게 다르다) ·
    /// 선·무늬가 닿은 둘레에서 이 안(px)은 무늬로 메운다(닿은 선이 글자 밑으로 이어질 자리).</summary>
    internal const int FlatRimInner = 5, FlatRimOuter = 11, FlatTol = 12, FlatReachPx = 24;
    /// <summary>칠할 자리가 구멍의 이 비율에 못 미치면 칠하지 않고 **전처럼 창을 통째로** 메운다(같은 입력, 같은 결과). 시간은 무늬로 메울 넓이에 달려 있어 조금 칠해서는 얻는 것이 없고,
    /// 칠한 자리가 바로 옆 무늬의 재료가 되면서 shift-map의 결과가 흔들린다 — 실제 26쪽 "見たこと"(39%를 칠함)는 줄무늬 띠를 가로지르는 검은 막대가 생겼다.
    /// 실측(26쪽, 창 43개): 칠한 비율은 96·82·74·74·71%(빨라지고 깨끗해진 다섯)와 46·39·23·15·14·7% 이하(견줘 보니 나을 것이 없던 것들)로 갈렸다.</summary>
    internal const double FlatMinShare = 0.5;
    /// <summary><see cref="Crossing"/>: 건너가는 선을 찾는 방향의 수(180° ÷ 이 수 = 5° 간격) · 방향이 어긋난 만큼 봐주는 폭(px — 5° 간격이면 길이 180px의 길에서 ±8px).</summary>
    internal const int ChordDirections = 36, ChordTolPx = 8;
    /// <summary><see cref="Groups"/>: 이 거리(px) 안의 조각은 한 묶음 — 글자 사이의 좁은 틈.</summary>
    internal const int GroupPx = 8;
    /// <summary>한 번에 돌리는 창의 수 — 코어 수에 맞춘다(<see cref="DefaultParallelism"/>). 실측(2026-11-27 낮, 6코어 12스레드, 여유 메모리 20GB대, 실제 26쪽의 여덟 창 — 앱 안에서):
    /// 48.0초(1) · 36.0초(2) · 34.8초(4). 창이 둘뿐인 05쪽은 23.2 → 21.9초(창 하나가 4~11초라 창 수가 적으면 안 준다). 결과는 몇 창씩 돌리든 같다.
    /// 같은 날 아침에는 '나란히가 더 느리다'(53초(1) · 71초(2) · 77초(4))고 재서 1로 뒀었다 — 그때는 여유 메모리가 1.5~4GB였다(다른 프로그램의 누수). 메모리가 바닥난 PC에서는 지금도 그럴 수 있다.
    /// 진단: NEKO_TEXFILL_PAR=&lt;n&gt;으로 바꿔 잰다.</summary>
    internal static int Parallelism =>
        int.TryParse(Environment.GetEnvironmentVariable("NEKO_TEXFILL_PAR"), out int forced) && forced > 0 ? Math.Min(forced, 8) : DefaultParallelism(Environment.ProcessorCount);

    /// <summary>논리 코어 수에서 한 번에 돌릴 창의 수: 셋에 하나, 1~4(넷과 둘의 차이가 이미 작다 — 34.8초 ↔ 36.0초).</summary>
    internal static int DefaultParallelism(int cores) => Math.Clamp(cores / 3, 1, 4);
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
    /// 일감들은 **메우기 전의 그림**에서 따로 준비해 돌리고(<see cref="Parallelism"/> — 코어 수에 맞춰 나란히. 일감은 다시 묶음으로 나뉘어 돈다) 차례로 붙인다 — 몇 개씩, 어떤 순서로 돌든 같은 입력에는 같은 결과가 나온다.
    /// <paramref name="tag"/>: 진단 덤프(NEKO_TEXFILL_DBG)의 파일 이름 앞머리 — 쪽마다 다르게 주면 여러 쪽의 덤프가 서로 덮지 않는다.</summary>
    internal static bool[] FillAll(Mat img, IReadOnlyList<(Rect Area, Mat Own)> holes, Mat avoid, CancellationToken ct = default, string? tag = null) =>
        FillAll(img, holes, avoid, out _, ct, tag);

    /// <summary><see cref="FillAll(Mat, IReadOnlyList{ValueTuple{Rect, Mat}}, Mat, CancellationToken, string)"/>와 같되 일감마다 **되풀이 무늬를 격자만큼 옮겨 그대로 이었는가**도 알려 준다
    /// (<paramref name="byLattice"/> — 둘레와 똑같은 무늬다. 베껴 메운 것(shift-map)·못 메운 것·메울 것이 없던 것은 false). 그대로 이은 자리는 SDXL로 다시 그릴 까닭이 없다.
    /// 차례(2026-11-27 속도): ① 일감마다 계획(<see cref="Job.Plan"/> — 격자로 다 되면 끝. 아니면 평평한 둘레 쪽을 그 색으로 칠하고 <see cref="FlatPart"/>, 남은 자리를 묶음으로 나눈다 <see cref="Groups"/>)
    /// ② 모든 일감의 묶음을 큰 것부터 나란히 shift-map ③ 일감마다 묶음의 결과를 붙인다. 묶음 하나라도 못 메운 일감은 통째로 실패다(반만 메운 그림을 남기지 않는다 — 호출부가 LaMa로 넘긴다).</summary>
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
            var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };
            Parallel.For(0, jobs.Length, options, i => jobs[i]?.Plan());
            // 묶음은 큰 것부터, 하나씩 집어 간다(NoBuffering) — 범위로 나눠 주면 큰 묶음들이 한 일꾼에게 몰려 줄을 선다
            var pieces = jobs.Where(j => j != null).SelectMany(j => j!.Pieces).OrderByDescending(p => p.HolePx).ToList();
            if (pieces.Count > 0)
                Parallel.ForEach(Partitioner.Create(pieces, EnumerablePartitionerOptions.NoBuffering), options, p => p.Run(ct, tag));
            ct.ThrowIfCancellationRequested();   // 묶음은 멈춤을 던지지 않고 그만둔다(Piece.Run) — 여기서 한 번 본다
            for (int i = 0; i < jobs.Length; i++)
            {
                if (jobs[i] is { Nothing: true }) { done[i] = true; continue; }   // 구멍이 전부 칸 테두리였다 — 메울 것이 없다(LaMa로 넘기지도 않는다)
                jobs[i]?.Finish();
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

    /// <summary>구멍(<paramref name="own"/>) 가운데 **둘레의 색으로 칠하면 되는 자리**(창 크기 마스크, 호출자가 해제)와 그 색 — 칠할 자리가 없으면 null.
    /// 2026-11-27 사용자 "속도 최적화": 빗금이 조금 든 흰 풍선·먹칠한 칸·그림 옆의 굵은 글은 둘레가 거의 다 한 색인데도 구멍 전체(8~13만 px)를 shift-map으로 메웠다 —
    /// 창 하나에 9~20초가 걸렸고(시간이 구멍 넓이보다 빨리 는다), 평평한 자리까지 무늬를 옮겨 오느라 빗금·옆 칸의 그림이 글자 자리로 딸려 들어왔다(실제 04·26쪽).
    /// 둘레 = 구멍 밖 <see cref="FlatRimInner"/>~<see cref="FlatRimOuter"/>px의 재료(<paramref name="valid"/>, 255 = 재료 — 다른 글자는 둘레가 아니다). 그 가운뎃값에서 <see cref="FlatTol"/> 넘게 벗어난 픽셀이 '선·무늬가 닿은 곳'이다.
    /// **무늬로 메울 자리**(칠하지 않는다) = ① 닿은 곳에서 <see cref="FlatReachPx"/> 안 — 닿은 선·빗금이 글자 밑으로 조금 이어진다 ② 닿은 곳 둘을 잇는 곧은 길 위(<see cref="Crossing"/>) — 구멍을 건너가는 선(속도선·머리카락)과
    /// 두 바탕의 경계가 지날 자리. ②가 없으면 속도선 위의 큰 글에서 '닿은 데서 먼' 한가운데가 칠해져 선이 끊긴 흰 구멍이 남는다(시제품에서 본 것). 톤·줄무늬처럼 둘레가 온통 무늬면 구멍 전체가 ①②에 든다(전과 같다).
    /// 나머지는 평평한 둘레의 평균색으로 칠한다 — 단색 풍선을 칠하는 것(<see cref="BubbleSolver.TrySolidFill"/>)과 같은 일을 구멍의 일부에 한다.</summary>
    internal static Mat? FlatPart(Mat patch, Mat valid, Mat own, out Scalar color)
    {
        color = default;
        using var gray = patch.Channels() == 1 ? patch.Clone() : patch.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var rim = new Mat();
        using (var outerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * FlatRimOuter + 1, 2 * FlatRimOuter + 1)))
        using (var innerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * FlatRimInner + 1, 2 * FlatRimInner + 1)))
        using (var skipped = new Mat())
        {
            Cv2.Dilate(own, rim, outerKernel);
            Cv2.Dilate(own, skipped, innerKernel);
            Cv2.Subtract(rim, skipped, rim);
        }
        Cv2.BitwiseAnd(rim, valid, rim);
        if (Cv2.CountNonZero(rim) < 32) return null;   // 둘레가 거의 없다(다른 글자에 둘러싸였다) — 무슨 색인지 알 수 없다

        int level = BubbleSolver.MedianOf(gray, rim);
        using var deviating = new Mat();
        using (var off = new Mat())
        {
            Cv2.Absdiff(gray, new Scalar(level), off);
            Cv2.Threshold(off, deviating, FlatTol, 255, ThresholdTypes.Binary);
        }
        Cv2.BitwiseAnd(deviating, rim, deviating);
        using var plain = new Mat();
        Cv2.Subtract(rim, deviating, plain);

        var flat = new Mat();
        try
        {
            Cv2.Threshold(own, flat, 0, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(deviating) > 0)
            {
                using var textured = new Mat();
                using (var reachKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * FlatReachPx + 1, 2 * FlatReachPx + 1)))
                    Cv2.Dilate(deviating, textured, reachKernel);
                flat.SetTo(Scalar.Black, textured);
                if (Cv2.CountNonZero(flat) > 0)   // 남은 것이 있을 때만 — 방향마다 창을 돌려 보는 일이라 싸지 않다
                {
                    using var solid = new Mat();
                    Cv2.BitwiseNot(valid, solid);
                    using var crossing = Crossing(own, solid, deviating);
                    flat.SetTo(Scalar.Black, crossing);
                }
            }
            if (Cv2.CountNonZero(flat) == 0) { flat.Dispose(); return null; }
            var mean = Cv2.Mean(patch, plain);
            color = new Scalar(Math.Round(mean.Val0), Math.Round(mean.Val1), Math.Round(mean.Val2));
            return flat;
        }
        catch { flat.Dispose(); throw; }
    }

    /// <summary>구멍(<paramref name="own"/>) 가운데 **벗어난 둘레 두 곳을 잇는 곧은 길** 위의 픽셀(창 크기 마스크, 호출자가 해제) — 선이 구멍을 건너가거나 두 바탕의 경계가 지날 자리.
    /// <paramref name="solid"/> = 재료가 아닌 곳(구멍 + 다른 글자 — 길은 여기를 지나간다), <paramref name="deviating"/> = 둘레 가운데 선·무늬가 닿은 곳.
    /// 방향마다(<see cref="ChordDirections"/> — 5° 간격) 창을 돌려 그 방향의 길이 가로줄이 되게 하고, 줄마다 solid의 이어진 구간을 본다: 구간의 **양 끝 바로 밖**에 닿은 곳이 있으면 그 구간이 길이다.
    /// 방향이 5° 간격이라 선이 그 사이로 비껴 있으면 양 끝이 다른 줄에 떨어진다 — 닿은 곳을 <see cref="ChordTolPx"/>만큼 넓혀서 본다(길이 180px의 길까지 맞는다. 그만큼 길도 굵게 잡힌다 — 선 둘레를 같이 메운다).</summary>
    internal static Mat Crossing(Mat own, Mat solid, Mat deviating)
    {
        int w = own.Cols, h = own.Rows;
        var cross = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
        try
        {
            using var marks = new Mat();
            using (var tolKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * ChordTolPx + 1, 2 * ChordTolPx + 1)))
                Cv2.Dilate(deviating, marks, tolKernel);
            int side = (int)Math.Ceiling(Math.Sqrt((double)w * w + (double)h * h)) + 4;
            var canvas = new Size(side, side);
            using var turnedSolid = new Mat(); using var turnedMarks = new Mat();
            using var path = new Mat(side, side, MatType.CV_8UC1);
            using var back = new Mat();
            var s = new byte[side * side]; var m = new byte[side * side]; var p = new byte[side * side];
            for (int k = 0; k < ChordDirections; k++)
            {
                using var turn = Cv2.GetRotationMatrix2D(new Point2f(w / 2f, h / 2f), 180.0 * k / ChordDirections, 1.0);
                turn.Set(0, 2, turn.At<double>(0, 2) + (side - w) / 2.0);
                turn.Set(1, 2, turn.At<double>(1, 2) + (side - h) / 2.0);
                Cv2.WarpAffine(solid, turnedSolid, turn, canvas, InterpolationFlags.Nearest);
                Cv2.WarpAffine(marks, turnedMarks, turn, canvas, InterpolationFlags.Nearest);
                CopyRows(turnedSolid, s, side);
                CopyRows(turnedMarks, m, side);
                Array.Clear(p);
                bool any = false;
                for (int y = 0; y < side; y++)
                {
                    int row = y * side;
                    for (int x = 0; x < side;)
                    {
                        if (s[row + x] == 0) { x++; continue; }
                        int start = x;
                        while (x < side && s[row + x] != 0) x++;
                        // [start, x) = 재료가 아닌 구간. 그 양 끝 바로 밖(둘레의 폭 안)에 닿은 곳이 있는가
                        bool before = false, after = false;
                        for (int q = Math.Max(0, start - FlatRimOuter - 1); q < start && !before; q++) before = m[row + q] != 0;
                        if (!before) continue;
                        for (int q = x, end = Math.Min(side, x + FlatRimOuter + 1); q < end && !after; q++) after = m[row + q] != 0;
                        if (!after) continue;
                        Array.Fill(p, (byte)255, row + start, x - start);
                        any = true;
                    }
                }
                if (!any) continue;
                Marshal.Copy(p, 0, path.Data, p.Length);   // path는 방금 만든 이어진 버퍼다(side × side, 한 채널)
                using var unturn = new Mat();
                Cv2.InvertAffineTransform(turn, unturn);
                Cv2.WarpAffine(path, back, unturn, new Size(w, h), InterpolationFlags.Nearest);
                Cv2.BitwiseOr(cross, back, cross);
            }
            Cv2.BitwiseAnd(cross, own, cross);
            return cross;
        }
        catch { cross.Dispose(); throw; }
    }

    /// <summary>한 채널 8비트 그림을 줄마다 배열로 옮긴다(줄 끝의 채움 바이트는 건너뛴다).</summary>
    private static void CopyRows(Mat mat, byte[] into, int width)
    {
        int rows = mat.Rows;
        for (int y = 0; y < rows; y++) Marshal.Copy(mat.Ptr(y), into, y * width, width);
    }

    /// <summary>무늬로 메울 자리(<paramref name="hole"/>)를 가까운 것끼리(<see cref="GroupPx"/> 안) 묶어, 묶음마다 **제 창**(묶음의 상자 + 둘레 <see cref="ContextPx"/>, 그림 안으로 자른 것)과
    /// 그 창 크기의 구멍 마스크(호출자가 해제)를 돌려준다. shift-map은 구멍이 클수록 훨씬 느리다 — 떨어진 덩어리를 한 창에서 같이 풀 까닭이 없다(나란히 돌릴 수도 있다).
    /// 글자 사이의 좁은 틈으로 갈린 조각은 한 묶음이다: 따로 풀면 창만 늘고 틈을 사이에 두고 무늬가 어긋난다.</summary>
    internal static List<(Rect Window, Mat Hole)> Groups(Mat hole)
    {
        var groups = new List<(Rect Window, Mat Hole)>();
        if (Cv2.CountNonZero(hole) == 0) return groups;
        var bounds = new Rect(0, 0, hole.Cols, hole.Rows);
        using var joined = new Mat();
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * GroupPx + 1, 2 * GroupPx + 1)))
            Cv2.Dilate(hole, joined, kernel);
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(joined, labels, stats, centroids, PixelConnectivity.Connectivity8);
        try
        {
            for (int i = 1; i < n; i++)
            {
                // 불린 덩어리의 상자 안에서 이 묶음의 조각만 고른다
                var grown = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
                using var mine = new Mat();
                using (var lab = new Mat(labels, grown)) Cv2.InRange(lab, new Scalar(i), new Scalar(i), mine);
                using (var part = new Mat(hole, grown)) Cv2.BitwiseAnd(mine, part, mine);
                using var points = new Mat();
                Cv2.FindNonZero(mine, points);
                if (points.Empty()) continue;
                var box = Cv2.BoundingRect(points);
                box = new Rect(box.X + grown.X, box.Y + grown.Y, box.Width, box.Height);
                var window = new Rect(box.X - ContextPx, box.Y - ContextPx, box.Width + 2 * ContextPx, box.Height + 2 * ContextPx) & bounds;
                var own = new Mat(window.Height, window.Width, MatType.CV_8UC1, Scalar.Black);
                using (var dst = new Mat(own, new Rect(grown.X - window.X, grown.Y - window.Y, grown.Width, grown.Height)))
                    mine.CopyTo(dst);
                groups.Add((window, own));
            }
            return groups;
        }
        catch { foreach (var g in groups) g.Hole.Dispose(); throw; }
    }

    /// <summary>창 하나의 일감 — 넣을 그림(창을 자른 사본)·재료 마스크·구멍, 그리고 결과.</summary>
    private sealed class Job(Rect area, Rect win, Mat patch, Mat valid, Mat own, string? tag) : IDisposable
    {
        public Rect Win => win;
        public Mat Own => own;
        public Mat? Filled { get; private set; }
        /// <summary>칸 테두리를 빼고 나니 메울 자리가 남지 않았다.</summary>
        public bool Nothing { get; private init; }
        /// <summary>메운 방법(진단용): "lattice: …" | "flat Npx (…)" | "flat Npx + shiftmap 묶음 수 Mpx (…)" | "shiftmap (…)".</summary>
        public string Method { get; private set; } = "";
        /// <summary>격자만큼 옮겨 메웠다(되풀이 무늬를 그대로 이었다) — <see cref="Filled"/>가 있을 때만 뜻이 있다.</summary>
        public bool ByLattice { get; private set; }
        /// <summary>shift-map으로 메울 묶음들 — <see cref="Plan"/>이 채운다(격자로 다 메웠거나 전부 칠했으면 비어 있다).</summary>
        public List<Piece> Pieces { get; } = new();
        /// <summary>평평한 자리를 칠해 둔 그림 — 묶음의 결과를 여기에 붙여 <see cref="Filled"/>가 된다.</summary>
        private Mat? _work;

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

        /// <summary>어떻게 메울지 정한다: 되풀이 무늬면 격자만큼 옮겨 다 메우고 끝(<see cref="Filled"/>). 아니면 가까운 둘레가 평평한 자리를 그 색으로 칠하고(<see cref="FlatPart"/>),
        /// 남은 자리를 묶음으로 나눈다(<see cref="Pieces"/> — 칠한 자리는 묶음의 재료가 된다). 칠할 자리가 구멍의 <see cref="FlatMinShare"/>에 못 미치면 전처럼 창 하나를 통째로 메운다(묶음 하나).
        /// **던지지 않는다**: 무늬 메우기는 보너스다 —
        /// 한 창의 실패(격자 코드의 예기치 못한 예외 포함)가 Parallel.For를 타고 나가면 그 장이 통째로 실패한다(검토 2026-11-27). 못 하면 Filled가 null로 남는다(호출부가 LaMa로 넘긴다).</summary>
        public void Plan()
        {
            if (Nothing) return;
            Mat? work = null;
            try
            {
                if (LatticeFill.TryFill(patch, valid, own, out string note) is { } exact)
                {
                    Method = "lattice: " + note;   // loc:none
                    ByLattice = true;
                    Filled = exact;
                    Dump();
                    return;
                }
                work = patch.Clone();
                int holePx = Cv2.CountNonZero(own);
                using var flat = FlatPart(patch, valid, own, out var color);
                int painted = flat == null ? 0 : Cv2.CountNonZero(flat);
                if (flat == null || painted < FlatMinShare * holePx)
                {
                    // 칠할 자리가 없거나 적다 — 전과 똑같이 창을 통째로 메운다(같은 입력, 같은 결과)
                    Mat? whole = null, material = null, hole = null;
                    try
                    {
                        (whole, material, hole) = (patch.Clone(), valid.Clone(), own.Clone());
                        Pieces.Add(new Piece(new Rect(0, 0, own.Cols, own.Rows), whole, material, hole));
                    }
                    catch { whole?.Dispose(); material?.Dispose(); hole?.Dispose(); throw; }
                    Method = $"shiftmap ({note})";   // loc:none
                }
                else
                {
                    using var rest = own.Clone();
                    using var material = valid.Clone();
                    work.SetTo(color, flat);
                    rest.SetTo(Scalar.Black, flat);
                    Cv2.BitwiseOr(material, flat, material);   // 칠한 자리는 묶음의 재료가 된다(다시 메우지 않는다)
                    var groups = Groups(rest);
                    for (int g = 0; g < groups.Count; g++)
                    {
                        var (window, hole) = groups[g];
                        Mat? piecePatch = null, pieceMaterial = null;
                        try
                        {
                            using (var view = new Mat(work, window)) piecePatch = view.Clone();
                            using (var view = new Mat(material, window)) pieceMaterial = view.Clone();
                            Pieces.Add(new Piece(window, piecePatch, pieceMaterial, hole));
                        }
                        catch
                        {
                            piecePatch?.Dispose(); pieceMaterial?.Dispose();
                            for (int r = g; r < groups.Count; r++) groups[r].Hole.Dispose();   // 아직 묶음이 맡지 않은 것
                            throw;
                        }
                    }
                    Method = Pieces.Count == 0 ? $"flat {painted}px ({note})"   // loc:none
                        : $"flat {painted}px + shiftmap {Pieces.Count} {holePx - painted}px ({note})";   // loc:none
                }
                _work = work;
                work = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var piece in Pieces) piece.Dispose();
                Pieces.Clear();
            }
            finally { work?.Dispose(); }
        }

        /// <summary>묶음들의 결과를 칠해 둔 그림에 붙여 <see cref="Filled"/>를 만든다 — 묶음 하나라도 못 메웠으면 이 일감은 실패다(Filled = null). 던지지 않는다.</summary>
        public void Finish()
        {
            if (Nothing || Filled != null || _work == null) return;
            try
            {
                foreach (var piece in Pieces)
                {
                    if (piece.Filled == null) return;
                    using var target = new Mat(_work, piece.Window);
                    piece.Filled.CopyTo(target, piece.Hole);
                }
                Filled = _work;
                _work = null;
                Dump();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Filled?.Dispose(); Filled = null; }
        }

        /// <summary>진단: NEKO_TEXFILL_DBG=&lt;폴더&gt;면 창마다 넣은 그림·재료 마스크·구멍·결과·방법을 남긴다(실제 원고에서 엉뚱한 무늬를 가져온 까닭을 볼 때).</summary>
        private void Dump()
        {
            if (Filled == null || Environment.GetEnvironmentVariable("NEKO_TEXFILL_DBG") is not { Length: > 0 } dbg) return;
            try
            {
                Directory.CreateDirectory(dbg);
                string stem = Path.Combine(dbg, $"{(string.IsNullOrEmpty(tag) ? "" : tag + "_")}{area.X}_{area.Y}");   // loc:none
                ImgIo.Write(stem + "_patch.png", patch); ImgIo.Write(stem + "_valid.png", valid); ImgIo.Write(stem + "_own.png", own); ImgIo.Write(stem + "_filled.png", Filled);
                File.WriteAllText(stem + "_method.txt", Method);
            }
            catch { }
        }

        public void Dispose()
        {
            patch.Dispose(); valid.Dispose(); own.Dispose(); Filled?.Dispose(); _work?.Dispose();
            foreach (var piece in Pieces) piece.Dispose();
        }
    }

    /// <summary>shift-map으로 메울 묶음 하나 — 제 창의 그림(평평한 자리는 이미 칠해져 있다)·재료 마스크(255 = 재료. 칠한 자리는 재료, 다른 묶음의 구멍과 다른 글자는 아니다)·구멍.</summary>
    private sealed class Piece(Rect window, Mat patch, Mat material, Mat hole) : IDisposable
    {
        /// <summary>일감의 창 안에서 이 묶음의 창.</summary>
        public Rect Window => window;
        public Mat Hole => hole;
        public int HolePx { get; } = Cv2.CountNonZero(hole);
        public Mat? Filled { get; private set; }

        /// <summary>메운다 — 못 하면 <see cref="Filled"/>가 null로 남는다. **던지지 않는다**(<see cref="Job.Plan"/>과 같은 까닭 — 멈춤도 던지지 않고 그만둔다. 호출부가 본다).
        /// 메우는 것은 직접 옮겨 짠 <see cref="ShiftMap"/>이다(2026-11-28 — xphoto의 것은 구멍과 상관없이 창 넓이의 제곱만큼 느렸다). 진단: NEKO_TEXFILL_OPENCV=1이면 전의 xphoto 구현으로(전후를 견줄 때),
        /// NEKO_TEXFILL_DBG=&lt;폴더&gt;면 묶음마다 창 크기·구멍 넓이·걸린 시간을 pieces.txt에 적는다.</summary>
        public void Run(CancellationToken ct = default, string? tag = null)
        {
            Mat? filled = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool opencv = Environment.GetEnvironmentVariable("NEKO_TEXFILL_OPENCV") == "1";
            string detail = "";
            try
            {
                // 마스크에 메울 자리(0)가 하나도 없으면 xphoto가 프로세스째 죽는다(변이 시험에서 봄 — 시험 프로세스가 통째로 내려갔다). 메울 것이 없으면 그림 그대로다
                if (Cv2.CountNonZero(material) == material.Rows * material.Cols) { Filled = patch.Clone(); return; }
                if (opencv)
                {
                    filled = new Mat();
                    CvXPhoto.Inpaint(patch, material, filled, OpenCvSharp.XPhoto.InpaintTypes.SHIFTMAP);
                }
                else filled = ShiftMap.Inpaint(patch, material, ct, note => detail = " | " + note);
                if (filled == null || filled.Empty() || filled.Size() != patch.Size() || filled.Type() != patch.Type()) return;
                Filled = filled;
                filled = null;
            }
            catch { }   // 멈춤 포함 — 이 묶음은 못 메운 것으로 남는다
            finally
            {
                filled?.Dispose();
                if (Environment.GetEnvironmentVariable("NEKO_TEXFILL_DBG") is { Length: > 0 } dbg)
                    try
                    {
                        Directory.CreateDirectory(dbg);
                        lock (LogLock)
                            File.AppendAllText(Path.Combine(dbg, "pieces.txt"),   // loc:none
                                $"{tag} {window.X},{window.Y} win {patch.Cols}x{patch.Rows} hole {HolePx} {(opencv ? "opencv" : "own")} {(Filled != null ? "ok" : "fail")} {clock.ElapsedMilliseconds}ms{detail}\n");   // loc:none
                    }
                    catch { }
            }
        }

        private static readonly object LogLock = new();

        public void Dispose()
        {
            patch.Dispose(); material.Dispose(); hole.Dispose(); Filled?.Dispose();
        }
    }
}
