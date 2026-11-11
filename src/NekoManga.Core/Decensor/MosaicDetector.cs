using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>모자이크 블록 패턴을 픽셀 분석으로 감지해 마스크를 만든다.
/// 모자이크 = 일정 크기 블록 안에서 픽셀값 표준편차가 낮은 패턴 (기존 remove_mosaic.py 휴리스틱 이식).</summary>
public static class MosaicDetector
{
    /// <summary>감지된 영역의 8UC1 마스크(255=검열)를 돌려준다. 하나도 없으면 null.
    /// img: BGR.
    ///
    /// 모자이크의 특징은 "블록 내부는 평탄한데 인접 블록과는 색이 뚜렷이 다르다"(격자 패턴)이다.
    /// 평탄함만 보면 피부·하늘 같은 매끈한 배경을 통째로 오탐하므로, 블록 평균색 그리드를 만들어
    /// 이웃과의 색차(격자 경계)가 충분한 평탄 블록만 모자이크 후보로 삼는다.
    /// 평탄하지 않은 이웃과의 색차는 그 너머가 나와 같은 톤으로 이어지면 세지 않는다 — 평탄한 톤 위에 그은 외곽선은 격자가 아니다.
    /// 그렇게 모은 후보 덩어리 가운데 밝기 경계가 가로·세로 두 방향 다 축에 맞는 것(격자 근거)이 하나도 없으면 그 쪽에는 모자이크가 없다고 본다
    /// (<see cref="KeepGridLike"/>) — 굵은 선·나란한 선·두 톤의 경계는 블록 색차만으로는 모자이크와 가를 수 없다.</summary>
    public static Mat? BuildMask(Mat img, int blockSize, int threshold, int grow)
    {
        if (blockSize < 2) blockSize = 2;
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        int gw = img.Cols / blockSize, gh = img.Rows / blockSize;
        if (gw < 3 || gh < 3) return null;

        var meanGrid = new double[gh, gw];
        var flat = new bool[gh, gw];
        var lined = new bool[gh, gw];   // 블록 안에서 밝기가 크게 흩어진다 — 선·경계가 지나는 블록
        unsafe
        {
            byte* pg = (byte*)gray.DataPointer; long sg = (long)gray.Step();
            for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int by = gy * blockSize, bx = gx * blockSize;
                double sum = 0, sumSq = 0;
                int n = blockSize * blockSize;
                for (int y = by; y < by + blockSize; y++)
                for (int x = bx; x < bx + blockSize; x++)
                {
                    double v = pg[y * sg + x];
                    sum += v; sumSq += v * v;
                }
                double mean = sum / n;
                meanGrid[gy, gx] = mean;
                double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
                flat[gy, gx] = std < threshold && mean is > 20 and < 235;
                lined[gy, gx] = std >= threshold;
            }
        }

        // 격자 경계 확인: 평탄 블록이면서 이웃(상하좌우) 중 하나와 색차가 임계 이상
        const double EdgeDelta = 8; // 인접 블록 평균색 차 (모자이크 격자 특징)
        // 선이 지나는 이웃 너머로 같은 톤을 찾아볼 거리(이 블록에서 몇 칸까지) — 4px 안팎의 가는 선은 어느 각도로 지나가도
        // 선이 지나는 블록이 세 칸을 넘게 이어지지 않는다 (합성 측정: 세 칸까지만 보면 비스듬한 3~4px 선이 남았다. 블록 크기 8 기준).
        const int LineReach = 4;

        // (gy,gx) 블록과 (dy,dx) 쪽 이웃의 색차. 이웃이 선이 지나는 블록(안에서 밝기가 흩어진 블록)이고 그 너머에서 처음 만나는
        // 블록이 나와 같은 톤의 평탄 블록이면, 같은 톤 위를 지나는 가는 선이 이웃 블록의 평균만 바꾼 것이라 격자 경계가 아니다(0).
        // 그대로 세면 선 양옆의 평탄 블록이 다 후보가 되고 닫기가 두 줄을 이어 열기도 통과하는 띠가 남는다
        // (실측: 480×360 톤 140 위 2px 선 두 줄 → 17,728px, Auto도 'Mosaic').
        // 모자이크 칸 경계가 블록을 가르는 경우는 너머가 다른 칸(다른 톤)이라 그대로 센다. 고르게 검거나 흰 이웃(밝기가 흩어지지 않은 블록)은
        // 선이 아니라 칸이므로 건너뛰지 않는다 — 건너뛰면 칸 톤이 검정·회색 둘뿐인 모자이크가 통째로 빠진다.
        // 실원고 144쪽 측정: 모자이크 라벨 적중 26/89 그대로, 모자이크가 아닌 쪽에 잡힌 오탐 21.1M → 18.4M px.
        // 5px를 넘는 굵은 선, 가까이 나란한 선 둘, 서로 다른 두 톤의 경계는 여기서는 그대로 후보가 된다 — 닫기·열기 뒤에 KeepGridLike가 거른다.
        double EdgeTo(int gy, int gx, int dy, int dx)
        {
            int ny = gy + dy, nx = gx + dx;
            if (ny < 0 || ny >= gh || nx < 0 || nx >= gw) return 0;
            double m = meanGrid[gy, gx];
            if (lined[ny, nx])
                for (int s = 2; s <= LineReach; s++)
                {
                    int sy = gy + s * dy, sx = gx + s * dx;
                    if (sy < 0 || sy >= gh || sx < 0 || sx >= gw) break;
                    if (lined[sy, sx]) continue;
                    if (flat[sy, sx] && Math.Abs(m - meanGrid[sy, sx]) < EdgeDelta) return 0;
                    break;
                }
            return Math.Abs(m - meanGrid[ny, nx]);
        }

        var mask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        bool any = false;
        for (int gy = 0; gy < gh; gy++)
        for (int gx = 0; gx < gw; gx++)
        {
            if (!flat[gy, gx]) continue;
            double maxDelta = Math.Max(Math.Max(EdgeTo(gy, gx, 0, -1), EdgeTo(gy, gx, 0, 1)),
                Math.Max(EdgeTo(gy, gx, -1, 0), EdgeTo(gy, gx, 1, 0)));
            if (maxDelta >= EdgeDelta)
            {
                any = true;
                Cv2.Rectangle(mask, new Rect(gx * blockSize, gy * blockSize, blockSize, blockSize), Scalar.White, -1);
            }
        }
        if (!any) { mask.Dispose(); return null; }

        // 격자가 밀집한 영역만 남기기: 닫기(구멍 메움) → 열기(고립 블록 제거)
        var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(blockSize * 2 + 1, blockSize * 2 + 1));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open,
            Cv2.GetStructuringElement(MorphShapes.Rect, new Size(blockSize * 2 + 1, blockSize * 2 + 1)));
        if (Cv2.CountNonZero(mask) == 0) { mask.Dispose(); return null; }
        // 남은 덩어리 가운데 '격자'라는 근거가 있는 것이 하나도 없으면 이 쪽에는 모자이크가 없다 (KeepGridLike)
        if (!KeepGridLike(gray, mask)) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    /// <summary>후보 덩어리(닫기·열기를 지난 마스크의 연결 성분)를 '격자 근거'로 거른다. 근거가 있는 덩어리가 하나도 없으면 false(이 쪽에 모자이크 없음),
    /// 있으면 뚜렷이 격자가 아닌 큰 덩어리만 마스크에서 지우고 true.
    ///
    /// 블록 평균의 색차만으로는 굵은 선·가까이 나란한 선·두 톤의 경계·완만한 음영도 후보가 된다 — 이들과 모자이크를 가르는 것은
    /// 밝기 경계의 방향이다. 모자이크 칸의 경계는 가로·세로뿐이고 두 방향이 비슷하게 나온다. 그림의 선과 음영은 방향이 제각각이고,
    /// 칸 테두리·네모 톤처럼 축에 맞는 그림은 대개 한 방향뿐이다.
    /// 그래서 덩어리마다 밝기 경계 픽셀(3×3 소벨 기울기가 단차 <c>EdgeStep</c> 이상)을 세어
    ///  ① 기울기가 축에서 10° 안인 픽셀의 비율(축 비율)과 가로·세로 경계의 균형이 기준을 넘는 덩어리가 하나라도 있어야 그 쪽을 모자이크가 있는 쪽으로 본다
    ///     (없으면 전부 버린다). 기준은 큰 덩어리(<c>AnchorArea</c> 이상)면 <c>AxisShare</c>·<c>Balance</c>, 그보다 작으면 <c>SmallAxisShare</c>·<c>SmallBalance</c>이고,
    ///     근거로 삼는 최소 넓이는 쪽 넓이의 1/<c>AnchorPageShare</c>(많아야 AnchorArea, 적어도 <c>AnchorAreaMin</c>)다.
    ///  ② 모자이크가 있는 쪽에서도 큰 덩어리 가운데 축 비율이 <c>AxisDrop</c>에 못 미치는 것은 버린다.
    /// 최소 넓이에 못 미치는 덩어리는 판정하지 않고 ①을 따른다 — 경계 픽셀이 적어 조각 하나로는 방향 통계가 흔들린다. ②를 큰 덩어리에만 하고
    /// 균형을 넣지 않는 것은 조각마다 거르면 진짜 모자이크의 귀퉁이 조각이 빠져서다(길쭉한 모자이크 조각은 한 방향 경계가 많다).
    ///
    /// 실측(2026-11-11, 블록 8·임계 15, 마스크 확장 8 — v1.8.2 → 지금):
    ///  · 라벨 원고 144쪽: 모자이크 라벨 적중 26/89 그대로, 라벨 밖 17,448 → 14,965px, 모자이크가 아닌 91쪽의 발화 62쪽 18.4M px → 0쪽.
    ///  · 검열 없는 흑백 원고 1,038쪽: 발화 615쪽 3.01억 px → 23쪽 715만 px.
    ///  · 적중한 11쪽은 쪽마다 가장 좋은 덩어리의 축 비율이 0.84 이상이고 균형은 가장 낮은 쪽(덩어리 하나뿐)이 0.55, 모자이크가 아닌 쪽의 큰 덩어리는
    ///    균형 0.45 이상에서 축 비율 0.63이 최고였다(0.75에 가까운 것은 균형 0.36). 라벨 위 큰 조각은 축 비율 0.74 이상, 아닌 쪽 큰 덩어리의 94%는 0.6 미만.
    ///  · 최소 넓이를 쪽 크기에 맞춘 까닭: 3,000px로 못 박으면 같은 원고를 줄인 쪽에서 근거 덩어리가 그 아래로 내려가 모자이크가 통째로 빠진다.
    ///    라벨 원고(sumi, 2040×2880)를 줄여 잰 적중(v1.8.2 → 3,000px 고정 → 지금): 0.5배 25 → 17 → 22, 0.6배 25 → 22 → 23, 0.75배 26 → 23 → 24, 0.9배 25 → 24 → 24.
    ///  · 작은 덩어리 기준을 따로 둔 까닭: 큰 덩어리 기준 그대로면 흰 막대 원고 2쪽(1050×1525, 덩어리 1,344·1,408px, 축 비율 0.81·0.80)이 26.6만 px 발화한다.
    ///    값(0.82·0.35)은 줄인 라벨 원고에서 쪽을 살린 덩어리(축 비율 0.84~0.87, 균형 0.40~0.95)와 그 두 쪽 사이에 놓았다 — 여유가 0.01~0.02뿐이고
    ///    한 작품의 11쪽으로 정한 값이라 다른 원고에서도 맞는다는 보장은 없다.
    ///  · 그 값으로 치른 것(3,000px 고정 → 지금): 검열 없는 흑백 원고 발화 21 → 23쪽(690만 → 715만 px), 그리고 모자이크가 아닌 91쪽을 변형했을 때의 발화가
    ///    한두 쪽씩 는다(0.75배 0 → 1쪽, JPEG 품질 70 0 → 1쪽, 1° 회전 0 → 2쪽, 5° 회전 4 → 7쪽). 변형하지 않은 144쪽의 마스크는 그대로다.
    ///  · 해 보고 넣지 않은 것: 경계 단차 6(검열 없는 원고 발화가 18쪽으로 줄지만 JPEG로 다시 압축한 쪽에서 적중이 준다 — 품질 70 25 → 24, 0.75배 + 품질 75 23 → 20),
    ///    작은 덩어리 여럿을 합쳐 근거로 삼기(0.5배에서 한 쪽만 돌아온다), 창 단위 근거(검열 없는 원고의 오탐이 되살아난다).
    ///
    /// 알려진 한계 — 이 판정이 놓치는 것(대부분 v1.8.2는 잡았다. 그 대신 v1.8.2는 모자이크가 없는 쪽도 그만큼 잡았다):
    ///  (가) 칸 경계가 축에 맞지 않는 모자이크 양식: 라벨 원고 chuuc의 모자이크는 덩어리의 축 비율이 0.27~0.36이라 하나도 못 잡는다.
    ///       원래 크기에서는 v1.8.2도 0/59였지만 줄인 쪽(0.5~0.9배)에서는 v1.8.2가 36~44개를 잡았고 지금은 0이다.
    ///  (나) 흐려지거나 압축 무늬가 낀 모자이크: 합성 모자이크는 선명하면 축 비율 0.84(칸 8px)~0.95(22px)인데 칸 8px은 3×3 가우시안만으로 0.74,
    ///       5×5면 0.70이다(칸 12px 이상은 5×5에서도 0.81 이상). JPEG는 모자이크가 8×8 격자에 맞으면 품질 40에서도 0.85 이상이지만
    ///       어긋나면(자리를 (3,5)만큼 옮김) 칸 12px 품질 60이 0.71, 품질 40이 0.63, 칸 16px 품질 40이 0.70 — 감지가 100% → 0%다.
    ///       기운 쪽은 8°(0.79)까지고 10°(0.64)부터 놓친다. 라벨 원고를 통째로 흐리면 적중이 3×3 가우시안 28 → 18, 5×5 28 → 4.
    ///  (다) 모자이크가 작은 것뿐인 쪽: 근거 최소 넓이(540만 px 이상인 쪽 3,000px = 55×55쯤, 180만 px 이하인 쪽 1,000px)에 못 미치는 덩어리만 있으면 놓친다.
    ///  (라) 큰 오탐 덩어리에 묻힌 모자이크(컬러·회색 음영 원고): gt_eval 26쪽에서 v1.8.2는 쪽마다 넓이의 48%를 칠해 25쪽의 정답을 '덮었고',
    ///       지금은 쪽 넓이의 0.3%만 칠하며 정답을 덮는 쪽이 0이다.
    ///  (마) ②가 모자이크가 있는 쪽의 진짜 모자이크 덩어리도 지운다 — 모자이크가 옆의 그림과 한 덩어리로 붙어 축 비율이 0.6 아래로 내려간 경우
    ///       (sumi_014: 3덩어리, 라벨 16,238px. 적중 수는 그대로다. 0.5~0.75배로 줄인 쪽에서는 sumi_014·019가 상자를 하나씩 잃는다).
    ///  (바) 상수는 블록 8·임계 15에서 맞춘 것이고 사용자가 바꾸는 블록 크기·임계(설정 창 2~64·1~80)를 따라가지 않는다:
    ///       블록 4면 적중 19 → 10, 임계 25면 33 → 21 (v1.8.2 → 지금).
    ///  (사) 칸이 길쭉한 모자이크: 칸의 가로·세로가 1:2를 넘으면(합성 16×40 균형 0.42, 8×24 0.21) 한 방향 경계가 대부분이라 근거가 되지 못한다.
    /// 남는 오탐: 축에 맞는 네모 톤·타일·좁은 줄무늬처럼 경계가 두 방향 다 축에 맞는 그림, 그리고 그런 근거가 있는 쪽의 작은 조각들(②는 큰 덩어리만 본다).
    ///
    /// 상수를 바꾸려면: 단위 테스트(MosaicDetectorTests)는 상수마다 그 구실을 경계 가까이에서 붙잡을 뿐이고 값은 실원고로 정한 것이다 —
    /// 라벨 원고 평가(D:\NekoManga-train\rule_eval\README.md의 RuleEval run·score — 144쪽, 네 대상)와 검열 없는 흑백 원고
    /// (D:\NekoManga-train\canvases\bw* 1,038쪽)의 발화 쪽 수를 다시 잰 뒤에 넣는다.</summary>
    private static bool KeepGridLike(Mat gray, Mat mask)
    {
        // 아래 값은 실원고 수치로 정했다 — 바꾸면 라벨 원고 평가(D:\NekoManga-train\rule_eval\README.md의 RuleEval, 144쪽 네 대상)와
        // 검열 없는 흑백 원고(D:\NekoManga-train\canvases\bw* 1,038쪽)의 발화를 다시 잰다. 단위 테스트는 값마다 그 구실만 붙잡는다.
        const int AnchorArea = 3000;          // 큰 덩어리의 넓이(px, 확장 전) — 55×55쯤. 이 이상이면 ①은 AxisShare·Balance로 보고 ②도 본다
        const int AnchorPageShare = 1800;     // 작은 쪽에서는 ①의 근거로 삼을 최소 넓이를 쪽 넓이의 1/1800까지 내린다 (540만 px 이상인 쪽은 AnchorArea 그대로)
        const int AnchorAreaMin = 1000;       // 그렇게 내려도 이보다 작은 덩어리는 근거로 삼지 않는다 (180만 px 이하인 쪽의 바닥)
        const int EdgeStep = 8;               // 밝기 경계로 칠 단차 — 블록 평균 색차 기준(EdgeDelta)과 같다. 3×3 소벨 값은 단차의 4배
        const int AxisTan1000 = 176;          // 축에서 10° 안 (tan 10° × 1000)
        const double AxisShare = 0.75;        // ① 경계 픽셀 가운데 축에 맞는 것의 비율 (큰 덩어리)
        const double Balance = 0.45;          // ① 가로 경계와 세로 경계 가운데 적은 쪽 / 많은 쪽 (큰 덩어리)
        const double SmallAxisShare = 0.82;   // ① AnchorArea에 못 미치는 덩어리는 넓이라는 근거가 약하므로 축 비율을 더 엄하게,
        const double SmallBalance = 0.35;     //    칸이 몇 개뿐이라 모양에 따라 흔들리는 균형은 조금 느슨하게 본다
        const double AxisDrop = 0.6;          // ② 큰 덩어리의 축 비율이 이보다 낮으면 격자가 아니다
        const int MinEdgeShare = 50;          // 경계 픽셀이 넓이의 1/50은 되어야 판정한다

        int anchorFloor = (int)Math.Clamp((long)gray.Rows * gray.Cols / AnchorPageShare, AnchorAreaMin, AnchorArea);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var boxes = new Rect[n];
        var drop = new bool[n];
        bool found = false, anyDrop = false;
        Mat? gx = null, gy = null;
        try
        {
            for (int li = 1; li < n; li++)
            {
                int area = stats.At<int>(li, (int)ConnectedComponentsTypes.Area);
                if (area < anchorFloor) continue;
                if (gx == null || gy == null)
                {
                    gx = new Mat(); gy = new Mat();
                    Cv2.Sobel(gray, gx, MatType.CV_16S, 1, 0, 3);
                    Cv2.Sobel(gray, gy, MatType.CV_16S, 0, 1, 3);
                }
                var br = boxes[li] = new Rect(stats.At<int>(li, (int)ConnectedComponentsTypes.Left), stats.At<int>(li, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(li, (int)ConnectedComponentsTypes.Width), stats.At<int>(li, (int)ConnectedComponentsTypes.Height));
                long edges = 0, horizontal = 0, vertical = 0;
                unsafe
                {
                    for (int y = br.Y; y < br.Bottom; y++)
                    {
                        int* pl = (int*)labels.Ptr(y);
                        short* px = (short*)gx.Ptr(y);
                        short* py = (short*)gy.Ptr(y);
                        for (int x = br.X; x < br.Right; x++)
                        {
                            if (pl[x] != li) continue;
                            // int로 넓힌 뒤 절댓값 — 16비트 그림에서는 소벨 값이 short.MinValue로 포화하고 Math.Abs(short)는 그 값에서 예외를 던진다
                            int ax = Math.Abs((int)px[x]), ay = Math.Abs((int)py[x]);
                            int hi = Math.Max(ax, ay);
                            if (hi < EdgeStep * 4) continue;
                            edges++;
                            if (Math.Min(ax, ay) * 1000 >= AxisTan1000 * hi) continue;
                            if (ax > ay) horizontal++; else vertical++;
                        }
                    }
                }
                if (edges * MinEdgeShare < area) continue;   // 경계가 거의 없다 — 판정 불가, ①을 따른다
                long axis = horizontal + vertical;
                bool big = area >= AnchorArea;
                if (axis >= (big ? AxisShare : SmallAxisShare) * edges
                    && Math.Min(horizontal, vertical) >= (big ? Balance : SmallBalance) * Math.Max(horizontal, vertical)) found = true;
                else if (big && axis < AxisDrop * edges) drop[li] = anyDrop = true;
            }
            if (!found) return false;
            if (anyDrop)
                unsafe
                {
                    for (int li = 1; li < n; li++)
                    {
                        if (!drop[li]) continue;
                        var br = boxes[li];
                        for (int y = br.Y; y < br.Bottom; y++)
                        {
                            int* pl = (int*)labels.Ptr(y);
                            byte* pm = (byte*)mask.Ptr(y);
                            for (int x = br.X; x < br.Right; x++)
                                if (pl[x] == li) pm[x] = 0;
                        }
                    }
                }
            return true;
        }
        finally
        {
            gx?.Dispose();
            gy?.Dispose();
        }
    }

    /// <summary>설정의 DecensorTarget("Auto"/"BlackBar"/"WhiteBar"/"Mosaic")에 맞는 검열 마스크.
    /// Auto는 세 경로를 가볍게 돌려 '가장 그럴듯한 한 종류'를 고른다 — 한 번에 한 종류 원칙은
    /// 유지하고 선택만 대신한다. 우선순위는 오탐이 적은 순서: 검은 막대 → 모자이크 → 흰 막대
    /// (흰색은 원고의 흰 띠와 혼동이 가장 잦다). kindUsed로 어떤 종류를 썼는지 알려준다.</summary>
    public static Mat? BuildMaskForTarget(AppSettings s, Mat img, IReadOnlyList<Rect>? excludeRects, out string kindUsed)
    {
        kindUsed = s.DecensorTarget;
        if (s.DecensorTarget == "Mosaic")
            return BuildMask(img, s.DecensorBlockSize, s.DecensorThreshold, s.DecensorMaskGrow);
        if (s.DecensorTarget == "Color")
            return ColorCensorDetector.BuildMask(img, excludeRects, s.DecensorMaskGrow, out _, out _);
        if (s.DecensorTarget is "BlackBar" or "WhiteBar")
            return BuildBarMask(img,
                white: s.DecensorTarget == "WhiteBar", black: s.DecensorTarget == "BlackBar",
                s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);

        // Auto — 각 후보를 실제로 만들어 보고 판정 기준을 넘는 첫 종류를 쓴다.
        // 기준은 총 픽셀이 아니라 **가장 큰 단일 성분** — 효과음 붓획 조각들은 작게 흩어져
        // 총합만 크다 (실측 p20: 붓획 오탐 47k px이지만 최대 성분 5.2k vs 진짜 막대 12k+).
        // 수용 기준: 큰 단일 막대(최대 성분 ≥6000px) + 성분 수 ≤8.
        // 효과음 붓획·굵은 효과선은 막대와 기하학적으로 구분이 안 되지만(실측 p20·p22),
        // 검열 페이지는 막대 몇 개뿐이고 효과음 페이지는 조각이 많다는 점으로 가른다.
        static (int Largest, int Count) CompStats(Mat m)
        {
            using var lab = new Mat();
            using var st = new Mat();
            using var ce = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(m, lab, st, ce);
            int best = 0, cnt = 0;
            for (int i = 1; i < n; i++)
            {
                int a = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (a < 60) continue;   // 먼지 조각은 개수에 안 센다
                cnt++;
                best = Math.Max(best, a);
            }
            return (best, cnt);
        }
        const int BigBar = 6000;
        static bool BarLike(Mat m) { var (l, c) = CompStats(m); return l >= BigBar && c <= 8; }

        var blackM = BuildBarMask(img, white: false, black: true,
            s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);
        if (blackM != null && BarLike(blackM)) { kindUsed = "BlackBar"; return blackM; }
        blackM?.Dispose();

        // 모자이크는 격자 근거가 있는 쪽에서만 마스크가 나온다(BuildMask → KeepGridLike). 그 전에는 톤 위의 선화·톤 경계가 1500px을 쉽게 넘어
        // 흰 막대 원고 34쪽이 전부 'Mosaic'으로 끝났고 흰 막대는 보지도 못했다 (실측 144쪽: Auto의 흰 막대선 적중 0 → 7/78,
        // 고른 종류 Mosaic 34쪽 → WhiteBar 16·없음 18. 모자이크 오탐에 가려 있던 흰 막대 오탐이 드러난 쪽도 있다 — 검은 막대·모자이크 원고 8쪽).
        var mosaicM = BuildMask(img, s.DecensorBlockSize, s.DecensorThreshold, s.DecensorMaskGrow);
        if (mosaicM != null && Cv2.CountNonZero(mosaicM) >= 1500) { kindUsed = "Mosaic"; return mosaicM; }
        mosaicM?.Dispose();

        var whiteM = BuildBarMask(img, white: true, black: false,
            s.DecensorBarMinAspect, s.DecensorBarMinArea, s.DecensorMaskGrow, excludeRects);
        if (whiteM != null && BarLike(whiteM)) { kindUsed = "WhiteBar"; return whiteM; }
        whiteM?.Dispose();

        kindUsed = "없음";
        return null;
    }

    /// <summary>흰색·검은색 막대(바) 검열을 찾는다. 그림 위에 덮인 단색 직사각형이 특징이다.
    ///
    /// 판정 조건 — 네 개를 모두 만족해야 한다 (말풍선·여백·패널 테두리 오탐 방지):
    ///  ① 거의 순백(≥whiteLevel) 또는 거의 순흑(≤blackLevel)인 연결 영역
    ///  ② 채워진 직사각형에 가까움 (면적 / 최소외접사각형 면적 ≥ 0.80)
    ///  ③ 가로세로비가 minAspect 이상 (막대는 길쭉하다)
    ///  ④ 이미지 가장자리에 닿지 않음 (페이지 여백·패널 사이 간격 제외) + 면적 상·하한
    ///
    /// ②③은 '연결 성분 하나 = 막대 하나'를 가정한다. 원고에서는 막대 여러 개가 맞닿는 일이 잦아
    /// (교차하거나 나란히 붙음) 뭉치면 직사각형도 길쭉하지도 않게 되어 통째로 탈락한다.
    /// 그래서 조건에서 떨어진 덩어리는 <see cref="DecomposeBars"/>로 곧은 띠들로 쪼개 다시 판정한다.
    ///
    /// excludeRects(말풍선 등)와 겹치는 후보는 버린다.</summary>
    // 주의: CTD 텍스트 마스크로 글자를 보호해 보려다 되돌렸다 — CTD가 검열 막대 자체를 글자 획으로
    // 잡아내서, 보호를 켜면 진짜 막대 검출률이 100%→15%로 무너졌다 (실측).
    public static Mat? BuildBarMask(Mat img, bool white, bool black,
        double minAspect, int minAreaPx, int grow, IReadOnlyList<Rect>? excludeRects = null)
    {
        if (!white && !black) return null;

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        const int WhiteLevel = 244, BlackLevel = 18;
        double maxArea = (double)img.Rows * img.Cols * 0.25; // 패널 전체 같은 큰 영역은 검열이 아니다

        var mask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        bool any = false;
        var whiteAccepted = new List<(Mat Comp, Rect Br)>();   // 흰 후보는 모아서 장식 줄무늬 검사 후 반영

        foreach (var isWhite in new[] { true, false })
        {
            if (isWhite && !white) continue;
            if (!isWhite && !black) continue;

            using var bin = new Mat();
            if (isWhite) Cv2.Threshold(gray, bin, WhiteLevel, 255, ThresholdTypes.Binary);
            else Cv2.Threshold(gray, bin, BlackLevel, 255, ThresholdTypes.BinaryInv);

            // 가는 선·점 노이즈 제거 (막대는 두껍다)
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open,
                Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)));

            // 컨투어(RETR_EXTERNAL) 대신 연결 성분을 쓴다 — 흰 페이지 배경이 바깥 컨투어를 차지하면
            // 그림 안쪽의 흰 막대가 계층에서 밀려 아예 조회되지 않는다 (실제로 놓쳤던 버그).
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);

            // 흰 막대는 원고 곳곳의 흰 띠(선 사이 여백·하이라이트·패널 사이)와 구분이 어렵다.
            // 그래서 흰쪽은 v0.4.0 그대로 '통째로 직사각형'만 인정하고 최소 면적도 내리지 않는다
            // (실측: 흰쪽에 띠 분해를 켜면 막대가 없는 깨끗한 페이지에서 오탐이 1613→5795px).
            int areaFloor = isWhite ? Math.Max(minAreaPx, 600) : minAreaPx;

            for (int li = 1; li < n; li++) // 0 = 배경 레이블
            {
                int area = stats.At<int>(li, (int)ConnectedComponentsTypes.Area);
                if (area < areaFloor || area > maxArea) continue;

                int bx = stats.At<int>(li, (int)ConnectedComponentsTypes.Left);
                int by = stats.At<int>(li, (int)ConnectedComponentsTypes.Top);
                int bw = stats.At<int>(li, (int)ConnectedComponentsTypes.Width);
                int bh = stats.At<int>(li, (int)ConnectedComponentsTypes.Height);
                var br = new Rect(bx, by, bw, bh);

                // ④ 가장자리에 닿아도 작으면 봐준다 — 페이지 여백·패널 간격은 이미지의 거의 전체
                // 폭/높이를 차지하지만, 화면 끝에 걸친 검열 막대는 한 조각일 뿐이다
                // (실측: 하단 끝까지 이어진 흰 줄무늬 검열이 이 규칙에 걸려 하나만 남았었다).
                bool edge = br.X <= 1 || br.Y <= 1 || br.Right >= img.Cols - 1 || br.Bottom >= img.Rows - 1;
                if (edge && (bw >= img.Cols * 0.4 || bh >= img.Rows * 0.4)) continue;

                // 말풍선 등 제외 영역과 겹치면 버린다
                if (excludeRects != null && excludeRects.Any(e => (e & br).Width > 0 && (e & br).Height > 0))
                    continue;

                // 이 성분만 뽑아 회전까지 고려한 직사각형성·비율을 본다
                using var comp = new Mat();
                Cv2.Compare(labels, li, comp, CmpTypes.EQ);
                using var compRoi = new Mat(comp, br);
                using var pts = new Mat();
                Cv2.FindNonZero(compRoi, pts);
                if (pts.Empty()) continue;

                var rr = Cv2.MinAreaRect(pts);
                double w = rr.Size.Width, h = rr.Size.Height;
                if (w < 2 || h < 2) continue;

                double fill = area / (w * h), aspect = Math.Max(w, h) / Math.Min(w, h);

                // ② 채워진 직사각형이고 ③ 길쭉하면 막대 하나다.
                // 짧고 뭉툭한 막대(실제 원고에 18×21 = 비율 1.17짜리가 있었다)도 있으므로 비율 조건을
                // 낮추되 두 가지를 요구한다: '꽉 찬 사각형'(원의 채움비는 π/4 ≈ 0.785라 효과음 말줄임표
                // 같은 점은 걸러진다)이고 '작을 것'. 크기 제한이 없으면 효과음의 굵은 붓획 토막
                // (74×53, 채움비 0.9)까지 막대로 보고 글자를 지워 버린다 (실측으로 확인).
                bool stubby = !isWhite && fill >= 0.90 && aspect >= 1.15 && Math.Max(w, h) <= 45;
                if ((fill >= 0.80 && aspect >= minAspect) || stubby)
                {
                    if (isWhite)
                    {
                        // 흰 막대는 얇다 — 짧은 변이 두꺼우면 문짝·벽 같은 흰 그림 면이다
                        // (실측: 348px짜리 문 패널이 막대로 잡혀 통째로 지워질 뻔했다)
                        if (Math.Min(w, h) > Math.Max(45, img.Cols * 0.045)) continue;
                        whiteAccepted.Add((comp.Clone(), br));
                    }
                    else
                    {
                        mask.SetTo(Scalar.White, comp);
                        any = true;
                    }
                    continue;
                }

                // 아니면 맞닿은 막대 뭉치일 수 있다 — 곧은 띠로 분해해 본다 (검은 막대만)
                if (isWhite) continue;
                using var bars = DecomposeBars(compRoi, area);
                if (bars == null) continue;
                using var dst = new Mat(mask, br);
                Cv2.BitwiseOr(dst, bars, dst);
                any = true;
            }
        }

        // ── 흰 후보 반영: 장식 줄무늬 그룹을 걸러낸 뒤에 ──
        // 겹겹이 쌓인 평행 띠(≥3, x겹침, 가까운 간격)는 ① 빗금 검열(띠 사이에 그림이 있다)이거나
        // ② 장식 줄무늬 패널(띠 사이가 밋밋한 톤이다). 사이 영역의 표준편차로 가른다
        // (실측 22p: 장식 패널 3묶음 147k px가 검열로 잡혀 그림이 지워질 뻔했다 — 톤 사이 std 낮음).
        if (whiteAccepted.Count > 0)
        {
            var drop = new bool[whiteAccepted.Count];
            var sortedIdx = Enumerable.Range(0, whiteAccepted.Count)
                .OrderBy(i => whiteAccepted[i].Br.Y).ToList();
            for (int a = 0; a < sortedIdx.Count; a++)
            {
                var grp = new List<int> { sortedIdx[a] };
                for (int b = a + 1; b < sortedIdx.Count; b++)
                {
                    var p = whiteAccepted[grp[^1]].Br;
                    var q = whiteAccepted[sortedIdx[b]].Br;
                    int xov = Math.Min(p.Right, q.Right) - Math.Max(p.X, q.X);
                    if (xov < Math.Min(p.Width, q.Width) * 0.5) continue;
                    int gap = q.Y - p.Bottom;
                    if (gap < 0 || gap > Math.Max(p.Height, q.Height) * 2.5) continue;
                    grp.Add(sortedIdx[b]);
                }
                if (grp.Count < 3) continue;
                // 띠 사이 영역이 밋밋하면 장식
                var flats = new List<bool>();
                for (int k = 1; k < grp.Count; k++)
                {
                    var p = whiteAccepted[grp[k - 1]].Br;
                    var q = whiteAccepted[grp[k]].Br;
                    int gx = Math.Max(p.X, q.X), gr = Math.Min(p.Right, q.Right);
                    int gy = p.Bottom + 2, gb = q.Y - 2;
                    if (gr - gx < 8 || gb - gy < 4) continue;
                    using var gapRoi = new Mat(gray, new Rect(gx, gy, gr - gx, gb - gy));
                    // 망점 톤은 픽셀 std가 높다 — 블러로 뭉개면 평탄한 톤은 std가 낮아지고
                    // 진짜 그림(선화)은 블러 후에도 구조가 남아 std가 높다.
                    // 가로 3등분 중 '한 구간이라도' 밋밋하면 장식 — 줄무늬 패널 위에 인물이
                    // 걸쳐 있어도 옆 구간은 톤이 이어진다 (빗금 검열은 전 구간이 그림이다).
                    using var gapBlur = new Mat();
                    Cv2.GaussianBlur(gapRoi, gapBlur, new Size(7, 7), 0);
                    int third = Math.Max(4, gapBlur.Cols / 3);
                    double minStd = double.MaxValue;
                    for (int tx3 = 0; tx3 + third <= gapBlur.Cols; tx3 += third)
                    {
                        using var part = new Mat(gapBlur, new Rect(tx3, 0, third, gapBlur.Rows));
                        Cv2.MeanStdDev(part, out _, out var stdP);
                        minStd = Math.Min(minStd, stdP.Val0);
                    }
                    flats.Add(minStd < 26);
                }
                if (flats.Count > 0 && flats.Count(f => f) >= flats.Count * 0.6)
                    foreach (var gi in grp) drop[gi] = true;
            }
            for (int i = 0; i < whiteAccepted.Count; i++)
            {
                if (!drop[i])
                {
                    mask.SetTo(Scalar.White, whiteAccepted[i].Comp);
                    any = true;
                }
                whiteAccepted[i].Comp.Dispose();
            }
        }

        // 회전 흰 스트립 추가 — 여백·흰 그림과 이어진 막대는 연결 성분으로 못 잡는다
        // (WhiteStripDetector 주석 참조: 직선 테두리 기반 + 보수 정책으로 장식 오탐 0 확인).
        if (white && WhiteStripDetector.AddToMask(img, mask, grow: 0) > 0)
            any = true;

        if (!any) { mask.Dispose(); return null; }
        if (grow > 0)
            Cv2.Dilate(mask, mask, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(grow * 2 + 1, grow * 2 + 1)));
        return mask;
    }

    /// <summary>맞닿은 막대 뭉치를 '곧은 띠'들로 분해한다. 덩어리(comp, 성분 하나만 담긴 ROI)를
    /// 여러 각도의 직선 커널로 열어(opening) 그 각도로 곧게 뻗은 띠만 남기고, 띠들의 합이
    /// 덩어리를 거의 다 덮을 때만 막대로 인정한다.
    ///
    /// 각도별로 따로 라벨링해야 한다 — 전 각도 결과를 먼저 OR하면 교차한 막대가 도로 한 덩어리가 된다.
    /// '띠들의 합으로 설명되는가'가 핵심 판별식이다: 검열 막대 뭉치는 곧은 띠 몇 개로 전부 덮이지만,
    /// 머리카락·검은 옷·효과음 붓획은 대개 휘고 굵기가 변해 덮이지 않는다.
    /// 다만 굵게 말린 붓획(효과음 'つ'·말린 머리카락)은 짧은 띠 토막 여럿이 각도를 바꿔 가며 서로 겹쳐 88% 넘게 덮어 버린다
    /// (실측: 반지름 30·굵기 4→24→4·270° 붓획 94.6%) — 덮음만으로는 못 가르므로 <see cref="IsCurledStroke"/>로 한 번 더 거른다.
    /// (실측: 합성 원고에서 교차·짧은 막대 검출률 0% → 98%, 그림 손상 0px)</summary>
    private static Mat? DecomposeBars(Mat comp, int area)
    {
        const int BandThick = 9;     // 띠로 인정할 최소 두께
        const int BandLength = 26;   // 띠로 인정할 최소 길이
        const double BandFill = 0.85;   // 띠 외접사각형이 이만큼 차 있어야 한다
        const double BandAspect = 2.5;  // 띠는 길쭉하다
        const double Cover = 0.88;      // 띠들의 합이 덩어리를 덮는 비율

        var sub = new Mat(comp.Rows, comp.Cols, MatType.CV_8UC1, Scalar.Black);
        using var roi = new Mat(comp.Rows, comp.Cols, MatType.CV_8UC1);
        var bands = new List<(Point[] Box, RotatedRect Rr)>();   // 받아들인 띠 — 굽은 획 판정에 다시 쓴다

        for (int deg = 0; deg < 180; deg += 10)
        {
            using var kernel = LineKernel(BandLength, BandThick, deg);
            using var opened = new Mat();
            Cv2.MorphologyEx(comp, opened, MorphTypes.Open, kernel);
            if (Cv2.CountNonZero(opened) == 0) continue;

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(opened, labels, stats, centroids);
            for (int li = 1; li < n; li++)
            {
                if (stats.At<int>(li, (int)ConnectedComponentsTypes.Area) < 60) continue;

                using var band = new Mat();
                Cv2.Compare(labels, li, band, CmpTypes.EQ);
                using var pts = new Mat();
                Cv2.FindNonZero(band, pts);
                if (pts.Empty()) continue;

                var rr = Cv2.MinAreaRect(pts);
                double w = rr.Size.Width, h = rr.Size.Height;
                if (w < 2 || h < 2) continue;
                if (Math.Max(w, h) / Math.Min(w, h) < BandAspect) continue;

                var box = Cv2.BoxPoints(rr).Select(p => (Point)p).ToArray();
                roi.SetTo(Scalar.Black);
                Cv2.FillConvexPoly(roi, box, Scalar.White);
                int total = Cv2.CountNonZero(roi);
                if (total == 0) continue;
                using var inside = new Mat();
                Cv2.BitwiseAnd(comp, roi, inside);
                if ((double)Cv2.CountNonZero(inside) / total < BandFill) continue;

                Cv2.FillConvexPoly(sub, box, Scalar.White);
                bands.Add((box, rr));
            }
        }

        using var covered = new Mat();
        Cv2.BitwiseAnd(sub, comp, covered);
        if ((double)Cv2.CountNonZero(covered) / Math.Max(1, area) < Cover || IsCurledStroke(comp, area, bands, Cover))
        {
            sub.Dispose();
            return null;
        }
        // 막대 끝의 안티에일리어싱까지 덮도록 띠 사각형 그대로(덩어리 밖은 제외하지 않고) 돌려준다.
        return sub;
    }

    /// <summary>띠들로 덮인 덩어리가 맞닿은 막대 뭉치가 아니라 굽은 붓획인지 본다 — 가르는 것은 띠끼리 겹치는 정도다.
    ///
    /// 곧은 막대 둘이 각을 이루며 교차하거나 맞닿으면 겹치는 곳은 만나는 자리(굵기×굵기 남짓)뿐이라 작은 막대의 일부에 그친다
    /// (막대는 길쭉하다). 굽은 획은 짧은 띠 토막들이 획을 따라 조금씩 방향을 바꾸며 서로 크게 겹쳐야 덮인다.
    /// 그래서 덮음을 실제로 설명하는 띠만 고르고(새로 덮는 픽셀이 가장 많은 띠부터 cover에 이를 때까지 — 같은 막대를
    /// 이웃 각도의 커널이 거듭 찾은 띠는 이미 덮인 자리라 뽑히지 않는다), 그중 방향이 BendAngle 이상 다른 두 띠가
    /// 작은 띠의 BendOverlap 이상을 겹치면 굽은 획으로 본다. 띠 둘로 다 덮이는 덩어리는 막대 둘이 만난 것으로 보고 거르지 않는다.
    /// (실측: 말린 붓획 반지름 30·굵기 4→24→4·270°는 37~40° 차이 띠 쌍이 작은 띠의 59~67%를 겹치고 띠 4~6개가 뽑힌다. 실원고 144쪽에서
    /// 분해로 받아들인 성분 가운데 라벨 위 막대 뭉치 13개는 이 겹침이 34% 이하이고, 합성 ㄱ자·지그재그·ㄷ자·삼각형 막대는 41% 이하다.
    /// 짧고 굵은 막대 둘의 X·T자 교차는 50%를 넘기도 해서 띠 수로 가른다)</summary>
    private static bool IsCurledStroke(Mat comp, int area, List<(Point[] Box, RotatedRect Rr)> bands, double cover)
    {
        // 같은 막대를 거듭 찾은 띠끼리도 방향이 몇 도 어긋난다(실원고에서 11°까지) — 커널 각도 두 칸(20°) 이상 다른 쌍만 본다
        const double BendAngle = 20;
        const double BendOverlap = 0.5;   // 작은 띠의 절반 이상이 겹치면 막대끼리 만난 자리가 아니다

        var masks = new List<Mat>();
        try
        {
            foreach (var (box, _) in bands)
            {
                var m = new Mat(comp.Rows, comp.Cols, MatType.CV_8UC1, Scalar.Black);
                Cv2.FillConvexPoly(m, box, Scalar.White);
                masks.Add(m);
            }

            // 덮음을 설명하는 띠 고르기(탐욕 덮기) — 띠들의 합이 cover를 넘는 것은 이미 확인했으므로 끝난다
            var picked = new List<int>();
            using var left = comp.Clone();   // 아직 덮이지 않은 덩어리 픽셀
            using var gain = new Mat();
            int remain = Cv2.CountNonZero(left);
            while ((double)(area - remain) / Math.Max(1, area) < cover)
            {
                int best = -1, bestGain = 0;
                for (int i = 0; i < masks.Count; i++)
                {
                    if (picked.Contains(i)) continue;
                    Cv2.BitwiseAnd(masks[i], left, gain);
                    int g = Cv2.CountNonZero(gain);
                    if (g > bestGain) { bestGain = g; best = i; }
                }
                if (best < 0) break;
                picked.Add(best);
                left.SetTo(Scalar.Black, masks[best]);
                remain -= bestGain;
            }
            // 굽은 획은 토막 셋 이상이 차례로 꺾여야 덮인다. 띠 둘로 다 덮이는 덩어리는 막대 둘이 만난 것이다 — 짧고 굵은 막대의 X·T자 교차는
            // 두 막대 사이 각도의 커널이 만든 '섞인 띠'가 먼저 뽑혀 진짜 막대 띠와 절반 넘게 겹치므로, 여기서 가르지 않으면 통째로 버려진다
            // (실측: 80×22 막대 둘의 60° 교차 58%, 60×16의 45° 교차 54.5%).
            if (picked.Count < 3) return false;

            using var both = new Mat();
            for (int a = 0; a < picked.Count; a++)
            for (int b = a + 1; b < picked.Count; b++)
            {
                int i = picked[a], j = picked[b];
                if (DirDiff(LongAxisDeg(bands[i].Rr), LongAxisDeg(bands[j].Rr)) < BendAngle) continue;
                Cv2.BitwiseAnd(masks[i], masks[j], both);
                int small = Math.Min(Cv2.CountNonZero(masks[i]), Cv2.CountNonZero(masks[j]));
                if (small > 0 && (double)Cv2.CountNonZero(both) / small >= BendOverlap) return true;
            }
            return false;
        }
        finally
        {
            foreach (var m in masks) m.Dispose();
        }
    }

    /// <summary>회전 사각형의 긴 변 방향(도, 0~180).</summary>
    private static double LongAxisDeg(RotatedRect rr)
    {
        double d = rr.Size.Width >= rr.Size.Height ? rr.Angle : rr.Angle + 90;
        d %= 180;
        return d < 0 ? d + 180 : d;
    }

    /// <summary>두 방향(0~180도, 180도 주기)의 차이 — 0~90도.</summary>
    private static double DirDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 180;
        return Math.Min(d, 180 - d);
    }

    /// <summary>지정한 각도로 누운 length×thick 직사각형 구조요소.</summary>
    private static Mat LineKernel(int length, int thick, double deg)
    {
        int size = (int)Math.Ceiling(Math.Sqrt(length * length + thick * thick)) | 1;
        var k = new Mat(size, size, MatType.CV_8UC1, Scalar.Black);
        var rr = new RotatedRect(new Point2f(size / 2f, size / 2f), new Size2f(length, thick), (float)deg);
        Cv2.FillConvexPoly(k, Cv2.BoxPoints(rr).Select(p => (Point)p).ToArray(), Scalar.White);
        return k;
    }

    /// <summary>ComfyUI LoadImage용 마스크 PNG (알파 0 = 복원 영역, 255 = 보존 — ComfyUI 마스크 = 1 − 알파/255).
    /// 엔진 LaMa(ComfyLama) 경로가 쓴다. (SDXL 인페인트용 블러·점진 링 옵션은 2026-10-19 Strong 경로와 함께 폐기.)</summary>
    public static byte[] ToMaskedPng(Mat img, Mat mask)
    {
        // 아래는 포인터로 직접 읽고 쓴다 — 형식·크기가 어긋나면(그림보다 작은 마스크면 버퍼 밖을 읽는다) 메모리를 망가뜨리므로 먼저 막는다
        if (img.Type() != MatType.CV_8UC3) throw new ArgumentException("그림은 BGR 8비트(CV_8UC3)여야 해요.", nameof(img));
        if (mask.Type() != MatType.CV_8UC1 || mask.Size() != img.Size()) throw new ArgumentException("마스크는 그림과 같은 크기의 8비트 한 채널이어야 해요.", nameof(mask));
        using var rgba = new Mat();
        Cv2.CvtColor(img, rgba, ColorConversionCodes.BGR2BGRA);
        unsafe
        {
            byte* pa = (byte*)rgba.DataPointer; long sa = (long)rgba.Step();
            byte* pm = (byte*)mask.DataPointer; long sm = (long)mask.Step();
            for (int y = 0; y < img.Rows; y++)
            for (int x = 0; x < img.Cols; x++)
                pa[y * sa + x * 4 + 3] = pm[y * sm + x] > 127 ? (byte)0 : (byte)255;
        }
        Cv2.ImEncode(".png", rgba, out var bytes);
        return bytes;
    }
}
