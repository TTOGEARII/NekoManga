using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using NekoManga.Core.Engine;
using OpenCvSharp;
using Point = System.Drawing.Point;
using Rect = OpenCvSharp.Rect;

namespace NekoManga.Core.Pipeline;

/// <summary>번역문을 완성본(채색된 이미지) 위에 그린다.
/// 감지 단계가 알려준 블록 좌표·폰트 크기를 쓰되, 좌표는 원본 크기 기준이므로
/// 채색 과정에서 리사이즈된 비율만큼 환산한다. 넘치면 글자를 줄여서 맞춘다.
///
/// 이미지 입출력은 OpenCV로 한다 — System.Drawing(GDI+)은 webp를 못 읽어
/// `new Bitmap(webp)`가 "Parameter is not valid"로 터진다 (만화 파일에 webp가 흔하다).
/// 글자 그리기만 GDI+를 쓰고, 픽셀은 메모리에서 주고받는다.</summary>
public static class TextRenderer
{
    public static void RenderBlocks(string imagePath, string sourceSizeRefPath,
        IReadOnlyList<TextBlockInfo> blocks, string outPath, AppSettings? settings = null) =>
        RenderBlocks(imagePath, sourceSizeRefPath, 0, 0, blocks, outPath, settings);

    /// <summary>원본 크기(srcW×srcH, 식자 사이드카에 박제한 값)를 함께 받는 판 — 크기를 알면 그 값으로 환산하고 참조 파일(원본)은 읽지 않는다.
    /// 블록 좌표는 번역할 때의 원본 기준이라, 그 뒤 원본이 옮겨지거나 지워지거나 같은 자리에서 다른 해상도로 바뀌어도 박제한 크기가 맞는 기준이다.
    /// 0이면 모르는 것으로 보고 참조 파일의 크기로, 그것도 못 읽으면 예전처럼 비율 1로 둔다.</summary>
    public static void RenderBlocks(string imagePath, string sourceSizeRefPath, int srcW, int srcH,
        IReadOnlyList<TextBlockInfo> blocks, string outPath, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        using var mat = ImgIo.Read(imagePath, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidDataException($"이미지를 읽지 못했어요: {Path.GetFileName(imagePath)}");
        using var bmp = ToBitmap(mat);

        // 다시 식자(결과 창 [다시 식자], --reletter)는 박제한 원본 크기로 환산한다. 원본 파일에 기대면 원고 폴더를 옮기거나 원본을 지운 뒤에는
        // 비율 1이 되어, 채색으로 식자 전 원고의 크기가 원본과 달라진 쪽에서 글이 엉뚱한 자리에 그려지거나 이미지 밖으로 나가 아무 알림 없이 빠졌다
        // (편집 미리보기는 사이드카 크기로 환산해 제자리에 보여 줘서, 저장하면 미리보기와 다른 결과가 남았다). 원본이 잠겨 있어도 실패하지 않는다.
        // 비율은 축별이고 결과 창의 편집 미리보기와 같은 계산(LetterScale.Resolve)이다 — 미리보기와 최종 식자가 같은 자리에 놓이게.
        var scale = LetterScale.Resolve(mat.Cols, mat.Rows, srcW, srcH, sourceSizeRefPath);   // bmp와 같은 크기
        double scaleX = scale.X, scaleY = scale.Y;

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // 말풍선마다 글이 들어갈 자리와 글자 크기를 먼저 정한다 — 결과 보기 창의 편집 미리보기도 같은 계산(PlanLayout)을 쓴다.
        var layouts = PlanLayout(mat, blocks, scaleX, scaleY, settings, g);
        for (int bi = 0; bi < blocks.Count; bi++)
        {
            var b = blocks[bi];
            if (layouts[bi].Size <= 0) continue;

            // 글자 색: 블록별 지정 > 설정 전역 > 원문 색 자동
            var color = !string.IsNullOrWhiteSpace(b.ColorOverride) ? ParseColor(b.ColorOverride)
                : string.IsNullOrWhiteSpace(settings.LetterColorHex) ? ParseColor(b.FontColorHex)
                : ParseColor(settings.LetterColorHex);
            DrawBlock(g, b, layouts[bi], color, settings);
        }

        // 저장 폴더는 ImgIo.Write가 만든다. 여기서 Directory.CreateDirectory(Path.GetDirectoryName(outPath)!)를 하면 폴더 없이 파일 이름만 준
        // 출력(--reletter <결과.png> out.png)에서 GetDirectoryName이 ""라 그림을 다 그린 뒤 저장 직전에 던졌다 (AppSettings.Save와 같은 함정).
        using var outMat = ToMat(bmp);
        if (!ImgIo.Write(outPath, outMat))
            throw new IOException($"결과 저장에 실패했어요: {outPath}");
    }

    /// <summary>세로쓰기 블록을 말풍선의 실제 흰 폭까지 가로로 넓힌다.
    /// 블록 세로 25%/50%/75% 세 줄에서 중심으로부터 밝은 픽셀(≥225)이 이어지는 구간을 재고,
    /// 꼬리 등으로 한 줄만 비정상적으로 넓게 새는 것을 막으려 **셋 중 최소 폭**을 쓴다.
    /// 과확장 방지로 한쪽 최대 2.5배, 테두리 여유로 측정 폭의 86%만 쓴다.
    /// avoid(다른 블록 영역)에 들어가면 측정을 멈춘다 — 결합 풍선은 테두리 없이 내부가
    /// 이어져 흰 구간이 이웃 풍선까지 이어지는데, 거기로 넓히면 글이 풍선 밖/남의 풍선에 겹친다
    /// (실측: 07p '집에서 기다려'가 좁은 풍선을 넘어 바지 위까지 식자됨).</summary>
    public static RectangleF WidenToBubble(Mat img, RectangleF r, IReadOnlyList<RectangleF>? avoid = null)
    {
        int cx = Math.Clamp((int)(r.X + r.Width / 2), 0, img.Cols - 1);
        int capL = (int)Math.Max(8, r.Width * 2.5f);

        bool Bright(int x, int y)
        {
            var p = img.At<Vec3b>(y, x);
            return (p.Item0 + p.Item1 + p.Item2) / 3 >= 225;
        }

        bool Blocked(int x, int y)
        {
            if (avoid == null) return false;
            foreach (var a in avoid)
                if (x >= a.Left - 4 && x <= a.Right + 4 && y >= a.Top - 4 && y <= a.Bottom + 4)
                    return true;
            return false;
        }

        float minRun = float.MaxValue;
        float runMid = cx;   // 가장 좁게 잰 줄의 흰 구간 중심 — 넓힌 블록은 글 기둥이 아니라 풍선 중심에 맞춘다
        foreach (var fy in new[] { 0.25f, 0.5f, 0.75f })
        {
            int y = Math.Clamp((int)(r.Y + r.Height * fy), 0, img.Rows - 1);
            if (!Bright(cx, y)) { if (r.Width < minRun) { minRun = r.Width; runMid = cx; } continue; }
            int xl = cx, xr = cx;
            while (xl - 1 >= 0 && cx - xl < capL && Bright(xl - 1, y) && !Blocked(xl - 1, y)) xl--;
            while (xr + 1 < img.Cols && xr - cx < capL && Bright(xr + 1, y) && !Blocked(xr + 1, y)) xr++;
            if (xr - xl < minRun) { minRun = xr - xl; runMid = (xl + xr) / 2f; }
        }

        float newW = minRun * 0.86f;
        if (newW <= r.Width) return r;
        // 기둥 중심(cx)이 아니라 흰 구간 중심에 둔다 — 풍선 오른쪽 끝 기둥(일본어 첫 줄)을 기준으로 넓히면
        // 글이 오른쪽으로 쏠려 테두리를 넘어갔다. 단, 원래 블록은 반드시 안에 들어가게 한다(블록이 이미지 안에 있을 때 — 넓힌 자리는
        // 이미지 경계에서 자르므로 밖으로 걸친 블록에서는 원래 블록보다 작아진다. 그래서 PlanLayout은 이미지 안쪽 부분만 넘긴다).
        // 소수 좌표(채색으로 결과 크기가 바뀐 쪽)에서는 넓힐 폭(newW)이 블록 폭보다 아주 조금만 크면 반올림으로 r.Right - newW가 r.X보다 커질 수 있다 —
        // 그대로 Math.Clamp에 주면 최솟값 > 최댓값으로 던져 이미지 안 블록 하나로 쪽 전체 식자가 멈췄다(LimitGrowth와 같은 함정). 그때는 원래 자리(r.X)에서 시작한다.
        float lo = Math.Max(0, r.Right - newW), hi = Math.Max(0, r.X);
        float nx = Math.Clamp(runMid - newW / 2, Math.Min(lo, hi), hi);
        return new RectangleF(nx, r.Y, Math.Min(newW, img.Cols - nx), r.Height);
    }

    /// <summary>세로쓰기 블록의 높이를 풍선의 실제 흰 높이까지 늘린다 — WidenToBubble의 세로판.
    /// 글자 기둥 bbox보다 풍선이 길쭉하면 그 여유 줄 수만큼 글자를 덜 줄여도 되므로,
    /// 페이지 공통 크기(중간값)가 좁은 풍선에서도 유지된다 (크기 제각각 피드백의 대책).</summary>
    public static RectangleF HeightenToBubble(Mat img, RectangleF r, IReadOnlyList<RectangleF>? avoid = null)
    {
        int cy = Math.Clamp((int)(r.Y + r.Height / 2), 0, img.Rows - 1);
        int capL = (int)Math.Max(8, r.Height * 1.8f);

        bool Bright(int x, int y)
        {
            var p = img.At<Vec3b>(y, x);
            return (p.Item0 + p.Item1 + p.Item2) / 3 >= 225;
        }

        bool Blocked(int x, int y)
        {
            if (avoid == null) return false;
            foreach (var a in avoid)
                if (x >= a.Left - 4 && x <= a.Right + 4 && y >= a.Top - 4 && y <= a.Bottom + 4)
                    return true;
            return false;
        }

        float minRun = float.MaxValue;
        float runMid = cy;
        foreach (var fx in new[] { 0.25f, 0.5f, 0.75f })
        {
            int x = Math.Clamp((int)(r.X + r.Width * fx), 0, img.Cols - 1);
            if (!Bright(x, cy)) { if (r.Height < minRun) { minRun = r.Height; runMid = cy; } continue; }
            int yt = cy, yb = cy;
            while (yt - 1 >= 0 && cy - yt < capL && Bright(x, yt - 1) && !Blocked(x, yt - 1)) yt--;
            while (yb + 1 < img.Rows && yb - cy < capL && Bright(x, yb + 1) && !Blocked(x, yb + 1)) yb++;
            if (yb - yt < minRun) { minRun = yb - yt; runMid = (yt + yb) / 2f; }
        }

        float newH = minRun * 0.86f;
        if (newH <= r.Height) return r;
        // 반올림으로 최솟값이 최댓값을 넘으면 원래 자리(r.Y)에서 시작한다 — WidenToBubble과 같은 함정.
        float lo = Math.Max(0, r.Bottom - newH), hi = Math.Max(0, r.Y);
        float ny = Math.Clamp(runMid - newH / 2, Math.Min(lo, hi), hi);
        return new RectangleF(r.X, ny, r.Width, Math.Min(newH, img.Rows - ny));
    }

    // ───────── 자리와 글자 크기 정하기 ─────────
    //
    // 2026-11-09 사용자 예시(사람이 식자한 원고)에 맞춰 다시 짰다. 예전에는 원문 글자 크기 추정값(블록 FontSize)의 중앙값을
    // 그대로 썼는데, 그러면 글자가 말풍선에 비해 작았다(쪽 높이의 0.9~1.3% — 예시는 1.6~2.1%).
    // 지금은 말풍선마다 "풍선 안에 들어가는 가장 큰 크기"를 구하고, 쪽 전체는 그 값들의 중앙값으로 맞춘다.
    // 그보다 좁은 풍선만 자기 크기로 줄어든다 — 크기가 제각각이 되지 않으면서 풍선을 채운다.

    /// <summary>블록 하나를 그릴 자리(결과 이미지 좌표)와 글자 크기(px). Size가 0이면 그리지 않는다.</summary>
    /// <param name="MaxWrap">이웃 블록과 겹치지 않으려면 넘지 말아야 할 줄 폭.</param>
    /// <param name="Round">풍선 모양(타원)에 맞춰 넣을지 — 손으로 잡은 블록은 사각형 그대로 쓴다.</param>
    public readonly record struct BlockLayout(RectangleF Rect, float Size, float MaxWrap, bool Round);

    // 아래 상수들은 사람이 식자한 원고를 잰 값에 맞췄다 (2026-11-09). 자료: 사용자 예시 8쪽 + 사용자 서고 602권에서 권당 6쪽씩 3,600쪽,
    // 그중 가로쓰기 여러 줄이 분명한 말풍선 2,013개(507권). 측정값·스크립트는 D:\NekoManga-train\letter_survey\ (survey.py, summarize_korean.py).
    //   글자 높이/쪽 높이: 중앙값 1.3%, 75% 1.5%, 90% 1.8% (예시 8쪽은 1.6~2.1%)   줄 간격(줄에서 줄)/글자 높이: 중앙값 1.38
    //   글 폭/풍선 폭: 중앙값 0.83, 75% 0.90   글 높이/풍선 높이: 3줄 0.39, 4줄 0.49, 5줄 0.63 (75%: 0.52·0.62·0.76)
    //   띄어쓰기/글자 높이: 중앙값 0.27   글 덩어리 위치: 풍선 한가운데(어긋남 중앙값 0)   정렬: 왼쪽 36%·가운데 34%·섞임 26%
    // 맑은 고딕 굵게의 글자 높이는 em의 0.95쯤이다 — 아래 em 단위 상수는 그걸로 환산한 값.

    /// <summary>글자 크기(em)의 상한 — 쪽 높이에 대한 비율. 강조된 큰 대사가 이 정도다.</summary>
    private const float MaxSizeOfPage = 0.026f;
    /// <summary>쪽 공통 크기의 상한 — 글자 높이로 쪽의 1.8%(서고의 90번째 백분위, 예시 원고의 보통 크기). 풍선이 다 널찍한 쪽에서 한없이 커지지 않게.</summary>
    private const float CommonSizeOfPage = 0.019f;
    private const float MinSize = 9;
    /// <summary>가로쓰기 블록을 풍선 쪽으로 넓히는 한도(배). 테두리 없는 해설문이 흰 바탕으로 번지지 않게 조금만.</summary>
    private const float HorizontalGrowth = 1.35f;
    /// <summary>WidenToBubble·HeightenToBubble이 측정 한도(한쪽 2.5배·1.8배)까지 가서야 멈췄다고 볼 배율 — 그 결과의 90%.</summary>
    private const float OpenWidth = 0.86f * 5.0f * 0.9f, OpenHeight = 0.86f * 3.6f * 0.9f;
    /// <summary>줄 간격 1.0일 때 줄에서 줄까지의 거리(em) — 글자 높이의 1.4배(서고 중앙값 1.38).</summary>
    private const float LineAdvanceEm = 1.33f;
    /// <summary>손으로 잡은 블록(ManualRect)의 글 자리를 블록 사각형에서 안으로 들이는 양 — 한쪽에 폭의 5%·높이의 3%(글 자리는 블록의 90% × 94%).</summary>
    internal const float ManualInsetX = 0.05f, ManualInsetY = 0.03f;

    /// <summary>글 자리가 주어진 사각형(식자할 그림 좌표)이 되는 '손으로 잡은 블록'의 사각형 — <see cref="PlanLayout"/>이 ManualRect 블록에 하는 들임의 역이다.
    /// 결과 창은 PlanLayout이 돌려준 글 자리를 편집 상자로 보여 준다. 상자를 놓은 자리를 그대로 블록에 저장하면 다음에 상자를 다시 띄울 때 또 들여서,
    /// 놓을 때마다 블록이 90% × 94%로 줄었다(v1.8.2까지 실측: 300×140 → 270×132 → 243×124 … 여섯 번 뒤 159×97).
    /// 저장할 때 이만큼 넓혀 두면 다시 띄운 상자도 최종 식자도 놓은 자리에 온다. 들임을 그리는 쪽(PlanLayout)에서 없애지 않는 것은
    /// 이미 저장된 손으로 잡은 블록의 식자 결과가 달라지기 때문이다.</summary>
    public static (double Left, double Top, double Width, double Height) ManualBlockFor(double left, double top, double width, double height)
    {
        double w = width / (1 - 2 * ManualInsetX), h = height / (1 - 2 * ManualInsetY);
        return (left - w * ManualInsetX, top - h * ManualInsetY, w, h);
    }

    public static List<BlockLayout> PlanLayout(Mat img, IReadOnlyList<TextBlockInfo> blocks, double scaleX, double scaleY,
        AppSettings settings, Graphics? g = null)
    {
        Bitmap? scratch = null;
        if (g == null)
        {
            scratch = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
            g = Graphics.FromImage(scratch);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
        try
        {
            int n = blocks.Count;
            var allRects = blocks.Select(b => new RectangleF(
                (float)(b.X1 * scaleX), (float)(b.Y1 * scaleY),
                (float)((b.X2 - b.X1) * scaleX), (float)((b.Y2 - b.Y1) * scaleY))).ToList();
            var rects = new RectangleF[n];
            var wraps = new float[n];
            var round = new bool[n];
            var fits = new float[n];
            var partial = new bool[n];   // 이미지 밖으로 걸쳐 보이는 부분이 블록의 절반도 안 되는 블록 — 쪽 공통 크기를 구할 때 뺀다
            float cap = Math.Max(MinSize, img.Rows * MaxSizeOfPage);

            for (int i = 0; i < n; i++)
            {
                var b = blocks[i];
                var rect = rects[i] = allRects[i];
                // 결과 이미지 밖으로 걸친 감지 블록은 이미지 안쪽 부분을 원래 블록으로 삼는다 — 원본 크기를 몰라 비율 1로 환산했는데(RenderBlocks가 원본도, 박제한
                // 원본 크기도 얻지 못했을 때) 식자 전 원고가 원본보다 작으면 블록이 밖으로 나간다(v1.8.2까지는 결과 창 미리보기가 두 축에 한 비율을 써서도 그랬다).
                // 그대로 재면 WidenToBubble·HeightenToBubble이 넓힌 자리를 이미지 경계에서 잘라 원래 블록보다 작게 돌려주고
                // LimitGrowth가 던져 쪽 전체 식자가 멈췄다(세로쓰기는 던지지 않는 대신 완전히 밖인 블록을 이미지 가장자리나 높이가 음수인 자리에 그렸다).
                // 보이는 부분의 폭이나 높이가 4px도 안 되면(완전히 밖이면 0) 아래 조건으로 그리지 않는다.
                // 반 픽셀까지의 넘침은 걸친 것으로 치지 않는다 — 가장자리에 닿은 블록이 소수 비율 환산으로 살짝 넘는 정도라 예전 계산 그대로 둔다.
                // 손으로 잡은 블록은 좌표 그대로 쓴다(넓히지 않으니 던지지 않는다).
                if (!b.ManualRect && Overhangs(rect, img))
                {
                    var visible = RectangleF.Intersect(rect, new RectangleF(0, 0, img.Cols, img.Rows));
                    partial[i] = visible.Width * visible.Height < rect.Width * rect.Height * 0.5f;
                    rect = visible;
                }
                if (rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(b.Translation)) continue;

                // 감지 블록은 '글자가 있던 자리'라 풍선보다 작다. 세로쓰기는 글자 기둥만 잡혀 아주 좁기도 하다(실측 26px).
                // 원고에서 풍선의 실제 흰 폭·높이를 재서 넓힌다. 다른 블록 영역은 벽으로 삼는다 — 결합 풍선은 내부가 이어져
                // 측정이 이웃 풍선까지 샌다. 사용자가 결과 보기 창에서 손으로 잡은 블록(ManualRect)은 그 좌표를 존중한다.
                if (!b.ManualRect)
                {
                    var others = allRects.Where((_, j) => j != i).ToList();
                    if (MeasureBubble(img, rect, others) is { } bubble)
                    {
                        // 닫힌 풍선: 풍선의 흰 안쪽을 자리로 삼고 글 덩어리를 그 한가운데에 둔다 (타원에 맞춰 넣는다).
                        // 다만 가운데는 원래 글이 있던 자리에서 크게 벗어나지 않게 한다 — 이웃 풍선과 이어진 풍선은 잰 구간이 한쪽으로
                        // 치우쳐서, 그 중심에 맞추면 글이 테두리 쪽으로 밀렸다. 자리는 그 가운데에서 양쪽으로 같은 거리만큼만 쓴다.
                        float bx = rect.X + rect.Width / 2, by = rect.Y + rect.Height / 2;
                        float cx = Math.Clamp(bubble.X + bubble.Width / 2, bx - rect.Width * 0.15f, bx + rect.Width * 0.15f);
                        float cy = Math.Clamp(bubble.Y + bubble.Height / 2, by - rect.Height * 0.15f, by + rect.Height * 0.15f);
                        float hw = Math.Min(cx - bubble.Left, bubble.Right - cx), hh = Math.Min(cy - bubble.Top, bubble.Bottom - cy);
                        rect = new RectangleF(cx - hw, cy - hh, 2 * hw, 2 * hh);
                        round[i] = true;
                    }
                    else
                    {
                        // 테두리 없는 해설문·여백: 흰 바탕이 측정 한도까지 이어진다. 세로쓰기는 예전처럼 잰 만큼, 가로쓰기는 조금만 넓힌다.
                        var grown = HeightenToBubble(img, WidenToBubble(img, rect, others), others);
                        rect = b.Vertical ? grown : LimitGrowth(rect, grown, HorizontalGrowth);
                        rect.Inflate(-rect.Width * 0.05f, -rect.Height * 0.03f);
                    }
                }
                else
                    // 손으로 잡은 블록: 사각형 가장자리까지 글자를 채우면 테두리에 닿는다 — 약간 안쪽으로.
                    // 결과 창은 이 들인 자리를 편집 상자로 보여 주고, 상자를 놓을 때 이 들임을 되돌려 저장한다(ManualBlockFor).
                    rect.Inflate(-rect.Width * ManualInsetX, -rect.Height * ManualInsetY);

                // 줄 폭 넘침 허용치를 이웃 블록까지의 여유로 깎는다 — 결합 풍선에서 분할된 형제 블록들이 바로 옆에
                // 붙어 있으면 넘침이 서로의 글 위에 겹쳐 그려졌다 (실측 22p).
                float maxWrap = rect.Width * WordOverflowAllow;
                float sideFree = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    var o = allRects[j];
                    if (o.Bottom <= rect.Top || o.Top >= rect.Bottom) continue;   // 세로 안 겹침
                    if (o.Right <= rect.Left) sideFree = Math.Min(sideFree, rect.Left - o.Right);
                    else if (o.Left >= rect.Right) sideFree = Math.Min(sideFree, o.Left - rect.Right);
                    else sideFree = 0;   // 가로로도 겹침 — 넘침 금지
                }
                if (sideFree != float.MaxValue)
                    maxWrap = Math.Min(maxWrap, rect.Width + 2 * Math.Max(0, sideFree - 4));

                rects[i] = rect;
                wraps[i] = maxWrap;
                fits[i] = LargestFit(g, b, new BlockLayout(rect, cap, maxWrap, round[i]), settings);
            }

            // 쪽 공통 크기: 풍선별 최대 크기의 중앙값 — 널찍한 풍선들은 같은 크기를 쓰고, 좁거나 글이 긴 풍선만 자기 크기로 줄어든다.
            // (더 아래쪽 백분위를 쓰면 글이 긴 풍선 한두 개가 쪽 전체를 작게 만든다 — 풍선이 대여섯 개뿐인 쪽에서 실제로 그랬다.)
            // 이미지 밖으로 걸쳐 보이는 부분이 블록의 절반도 안 되는 블록은 빼고 구한다(그런 블록뿐인 쪽이면 전체로) — 가장자리에 몇 px만 보이는 블록은
            // 그 좁은 띠에 맞춘 작은 크기라, 중앙값에 끼면 이미지 안 블록의 글자까지 줄었다(아래 가장자리에 5~6px 걸친 세로쓰기 둘이 있는 쪽에서 28.5px → 10.3px).
            // 조금만 걸친 블록은 넣는다 — v1.8.2까지 결과 창 편집 미리보기는 두 축에 한 비율을 써서 채색한 쪽의 가장자리 블록이 몇 px씩 걸쳤는데, 그것까지 빼면
            // 미리보기의 글자 크기가 최종 식자와 달라졌다(채색 비율로 합성한 4,000쪽 중 122쪽). 지금은 미리보기도 축별 비율(LetterScale)이라 최종과 같은 블록이 걸친다.
            // 걸친 블록의 크기도 아래에서 다른 블록처럼 자기 맞춤 크기와 공통 크기 가운데 작은 쪽이다.
            var sorted = fits.Where((f, i) => f > 0 && !partial[i]).OrderBy(f => f).ToList();
            if (sorted.Count == 0) sorted = fits.Where(f => f > 0).OrderBy(f => f).ToList();
            float common = sorted.Count == 0 ? 16 : sorted[sorted.Count / 2];
            common = Math.Min(common, img.Rows * CommonSizeOfPage) * (float)Math.Clamp(settings.LetterSizeScale, 0.3, 3.0);

            var result = new List<BlockLayout>(n);
            for (int i = 0; i < n; i++)
            {
                float size = 0;
                if (fits[i] > 0)
                {
                    // 블록별 배율(이 풍선만 크게/작게)은 공통 크기에 곱한다. 크게 한 블록은 풍선에 맞춘 크기도 그만큼 넘게 둔다.
                    float scale = (float)Math.Clamp(blocks[i].StyleScale, 0.3, 3.0);
                    size = Math.Max(MinSize, Math.Min(fits[i] * Math.Max(1, scale), common * scale));
                }
                result.Add(new BlockLayout(rects[i], size, wraps[i], round[i]));
            }
            // 진단: NEKO_LETTER_DBG=<파일>이면 블록마다 감지 자리 → 넓힌 자리, 풍선에 맞춘 크기 → 실제 크기를 적는다 (글 내용은 적지 않는다)
            if (Environment.GetEnvironmentVariable("NEKO_LETTER_DBG") is { Length: > 0 } dbg)
                File.AppendAllLines(dbg, Enumerable.Range(0, n).Select(i =>
                    $"[{i}] v={blocks[i].Vertical} manual={blocks[i].ManualRect} chars={blocks[i].Translation?.Length ?? 0} " +
                    $"block={allRects[i].Width:0}x{allRects[i].Height:0} place={rects[i].Width:0}x{rects[i].Height:0} round={round[i]} " +
                    $"fit={fits[i]:0.0} size={result[i].Size:0.0}").Append($"common={common:0.0} cap={cap:0.0} page={img.Cols}x{img.Rows}"));
            return result;
        }
        finally
        {
            if (scratch != null) { g.Dispose(); scratch.Dispose(); }
        }
    }

    /// <summary>블록이 들어 있는 말풍선의 흰 안쪽을 잰다: 블록 높이의 25·50·75% 세 줄에서 좌우로, 블록 폭의 25·50·75% 세 줄에서 위아래로
    /// 밝은 픽셀(풍선 바탕색 기준)이 이어지는 구간을 재고 각각 **가장 좁은 값**을 쓴다(꼬리로 한 줄만 새는 것 방지). 다른 블록 영역은 벽이다.
    /// 어느 쪽이든 측정 한도(가로 한쪽 2.5배, 세로 한쪽 1.8배)까지 흰 바탕이 이어지면 풍선이 아니라고 보고 null.</summary>
    private static RectangleF? MeasureBubble(Mat img, RectangleF r, IReadOnlyList<RectangleF> avoid)
    {
        // 블록 안(글자를 지운 자리)과 그 둘레 조금은 밝기를 따지지 않는다. 글자 지움(LaMa)이 메운 자리는 순백이 아니라 옅은 회색(실측 213~223)이라,
        // 거기서부터 밝기를 재면 가운데에서 바로 막혀 풍선을 하나도 못 쟀다(실측: 한 쪽의 블록 11개 전부 실패).
        float rim = Math.Max(8, Math.Min(r.Width, r.Height) * 0.08f);
        var own = RectangleF.Inflate(r, rim, rim);
        int Gray(int x, int y)
        {
            var p = img.At<Vec3b>(y, x);
            return (p.Item0 + p.Item1 + p.Item2) / 3;
        }

        // 풍선 바탕의 밝기는 원고마다 다르다 — 스캔본은 종이색이 215쯤인 것도 있어서 고정 기준(225)으로는 풍선 안이 전부 '어둡다'고 나왔다.
        // 블록 둘레 한 바퀴의 밝기 중앙값을 그 풍선의 바탕색으로 보고 거기서 조금 어두운 데까지를 풍선 안으로 친다.
        // 둘레가 어두우면(185 미만) 흰 풍선이 아니라 그림·망점 위의 글이다.
        var ring = new List<int>();
        int ox1 = Math.Clamp((int)own.Left, 0, img.Cols - 1), ox2 = Math.Clamp((int)own.Right, 0, img.Cols - 1);
        int oy1 = Math.Clamp((int)own.Top, 0, img.Rows - 1), oy2 = Math.Clamp((int)own.Bottom, 0, img.Rows - 1);
        for (int x = ox1; x <= ox2; x += 2) { ring.Add(Gray(x, oy1)); ring.Add(Gray(x, oy2)); }
        for (int y = oy1; y <= oy2; y += 2) { ring.Add(Gray(ox1, y)); ring.Add(Gray(ox2, y)); }
        ring.Sort();
        int tone = ring[ring.Count / 2];
        if (tone < 185) return null;
        int threshold = Math.Min(225, tone - 18);

        bool Bright(int x, int y) => own.Contains(x, y) || Gray(x, y) >= threshold;
        bool Blocked(int x, int y)
        {
            foreach (var a in avoid)
                if (x >= a.Left - 4 && x <= a.Right + 4 && y >= a.Top - 4 && y <= a.Bottom + 4) return true;
            return false;
        }

        int cx = Math.Clamp((int)(r.X + r.Width / 2), 0, img.Cols - 1), cy = Math.Clamp((int)(r.Y + r.Height / 2), 0, img.Rows - 1);
        int capX = (int)Math.Max(8, r.Width * 2.5f), capY = (int)Math.Max(8, r.Height * 1.8f);
        float left = float.MinValue, right = float.MaxValue, top = float.MinValue, bottom = float.MaxValue;
        foreach (var f in new[] { 0.25f, 0.5f, 0.75f })
        {
            int y = Math.Clamp((int)(r.Y + r.Height * f), 0, img.Rows - 1);
            int xl = cx, xr = cx;
            while (xl - 1 >= 0 && Bright(xl - 1, y) && !Blocked(xl - 1, y)) { xl--; if (cx - xl >= capX) return null; }
            while (xr + 1 < img.Cols && Bright(xr + 1, y) && !Blocked(xr + 1, y)) { xr++; if (xr - cx >= capX) return null; }
            left = Math.Max(left, xl); right = Math.Min(right, xr);

            int x = Math.Clamp((int)(r.X + r.Width * f), 0, img.Cols - 1);
            int yt = cy, yb = cy;
            while (yt - 1 >= 0 && Bright(x, yt - 1) && !Blocked(x, yt - 1)) { yt--; if (cy - yt >= capY) return null; }
            while (yb + 1 < img.Rows && Bright(x, yb + 1) && !Blocked(x, yb + 1)) { yb++; if (yb - cy >= capY) return null; }
            top = Math.Max(top, yt); bottom = Math.Min(bottom, yb);
        }
        // 흰 구간이 블록 둘레(밝기를 따지지 않은 곳)를 넘어서지 못했으면 흰 풍선 안이 아니다 — 그림·망점 위에 놓인 글이다.
        if (right - left <= own.Width + 6 && bottom - top <= own.Height + 6) return null;
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>넓힌 사각형을 원래 크기의 limit배까지만 받아들인다 (넓힌 쪽의 중심은 유지).</summary>
    private static RectangleF LimitGrowth(RectangleF original, RectangleF grown, float limit)
    {
        float w = Math.Min(grown.Width, original.Width * limit), h = Math.Min(grown.Height, original.Height * limit);
        float cx = grown.X + grown.Width / 2, cy = grown.Y + grown.Height / 2;
        // 원래 블록은 반드시 안에 들어가게 한다. 다만 넓히지 못해 w가 원래 폭 그대로면 소수 좌표(채색으로 결과 크기가 바뀐 쪽)에서는 반올림으로
        // original.Right - w가 original.X보다 아주 조금 커질 수 있다 — 그대로 Math.Clamp에 주면 최솟값 > 최댓값으로 던져 쪽 전체 식자가 멈췄다.
        // 그때는 원래 자리(original.X)에서 시작한다. 높이도 같다.
        float x = Math.Clamp(cx - w / 2, Math.Min(original.Right - w, original.X), original.X),
              y = Math.Clamp(cy - h / 2, Math.Min(original.Bottom - h, original.Y), original.Y);
        return new RectangleF(x, y, w, h);
    }

    /// <summary>블록이 이미지 밖으로 반 픽셀 넘게 나갔는지.</summary>
    private static bool Overhangs(RectangleF r, Mat img) =>
        r.Left < -0.5f || r.Top < -0.5f || r.Right > img.Cols + 0.5f || r.Bottom > img.Rows + 0.5f;

    /// <summary>어절을 꺾지 않고 풍선 안에 들어가는 가장 큰 글자 크기. 어떤 크기로도 안 들어가면 최소 크기.</summary>
    private static float LargestFit(Graphics g, TextBlockInfo b, BlockLayout box, AppSettings s)
    {
        var (family, style) = ResolveStyle(b, s);
        var words = new WordMetrics(g, family, style, b.Translation, s);
        float spacing = LineSpacing(b, s);
        float whole = 0;
        for (float size = box.Size; size >= MinSize; size *= 0.95f)
            if (words.Arrange(size, box, spacing) != null) { whole = size; break; }
        if (whole >= box.Size * WholeIsEnough) return whole;

        // 어절째로는 아주 작아야만 들어간다 — 띄어쓰기 없이 긴 글이 한 줄에 다 들어갈 때까지 줄어든 것이다.
        // 글자 단위로 꺾으면 훨씬 크게 들어가므로 그 크기를 쓴다. (작은 값을 그대로 돌려주면 쪽 공통 크기까지 끌려 내려가
        // 쪽 전체가 깨알 글씨가 된다 — 띄어쓰기 없는 18자·26자 블록 둘이 쪽을 10px로 만든 것을 실제로 봤다.)
        for (float size = box.Size; size > Math.Max(MinSize, whole); size *= 0.92f)
            if (BrokenLines(g, b.Translation, family, style, size, box, spacing) != null) return size;
        return Math.Max(MinSize, whole);
    }

    /// <summary>어절째로 들어가는 크기가 상한의 이 비율 이상이면 충분하다고 보고 글자 단위 꺾기를 시험하지 않는다.</summary>
    private const float WholeIsEnough = 0.35f;
    /// <summary>그릴 때: 정해진 크기에서 이 비율까지는 줄여서라도 어절을 통째로 두고, 그래도 안 되면 글자 단위로 꺾는다.</summary>
    private const float ShrinkBeforeBreak = 0.75f;

    /// <summary>긴 어절을 글자 단위로 꺾는 것을 허용한 줄 나눔. 이 크기로 자리에 들어가면 줄들을, 아니면 null.</summary>
    private static string[]? BrokenLines(Graphics g, string text, FontFamily family, FontStyle style, float size, BlockLayout box, float spacing)
    {
        var rect = box.Rect;
        float wrapWidth = Math.Min(rect.Width * WordOverflowAllow, Math.Max(rect.Width, box.MaxWrap));
        using var font = new Font(family, size, style, GraphicsUnit.Pixel);
        var lines = WrapByWords(g, text, font, wrapWidth, out _).Split('\n');
        float totalH = font.GetHeight(g) + (lines.Length - 1) * size * LineAdvanceEm * spacing;
        float maxW = lines.Max(l => l.Length == 0 ? 0 : g.MeasureString(l, font, PointF.Empty, Typographic).Width);
        // 한 줄에 한두 자씩 쌓이는 크기는 여기서도 버린다 (Arrange와 같은 규칙) — 더 작은 크기에서 줄을 넓게 쓴다
        if (text.Count(c => !char.IsWhiteSpace(c)) > 6 && lines.Length > 2 && maxW < size * WordMetrics.NarrowestLine) return null;
        return totalH <= rect.Height * 1.08f + 2 && maxW <= wrapWidth * 1.05f + 2 ? lines : null;
    }

    private static (FontFamily Family, FontStyle Style) ResolveStyle(TextBlockInfo b, AppSettings s)
    {
        // 블록별 스타일: 굵기(페이지 설정/보통/굵게) + 기울임 (페이지 기본에 블록이 더한다)
        var style = b.BoldOverride switch { 0 => FontStyle.Regular, 1 => FontStyle.Bold, _ => s.LetterBold ? FontStyle.Bold : FontStyle.Regular };
        if (b.Italic || s.LetterItalic) style |= FontStyle.Italic;
        var family = ResolveFamily(s.LetterFontFamily);
        // 일부 글꼴은 일부 스타일만 있다 — 없는 스타일로 Font를 만들면 던지므로 가능한 것으로 보정
        if (!family.IsStyleAvailable(style))
        {
            foreach (var cand in new[] { style & ~FontStyle.Italic, FontStyle.Regular, FontStyle.Bold })
                if (family.IsStyleAvailable(cand)) { style = cand; break; }
        }
        return (family, style);
    }

    /// <summary>줄 간격 배율: 블록별 지정이 없으면(0) 페이지 기본 설정.</summary>
    private static float LineSpacing(TextBlockInfo b, AppSettings s) =>
        (float)Math.Clamp(b.LineSpacing > 0 ? b.LineSpacing : s.LetterLineSpacing, 0.6, 3.0);

    /// <summary>글꼴에 맞춘 여백 없는 측정·그리기 형식 (기본 형식은 글자 양옆에 여백을 붙여 폭이 부풀려진다).</summary>
    private static readonly StringFormat Typographic = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormat.GenericTypographic.FormatFlags | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap,
    };

    /// <summary>한 블록의 글을 어절 단위로 재 둔 것. 폭은 글자 크기에 비례하므로 한 번만 재고(기준 크기) 크기마다 곱해 쓴다 —
    /// 크기·줄 폭을 여러 가지로 시험해도 측정은 어절 수만큼만 한다.</summary>
    private sealed class WordMetrics
    {
        private const float Reference = 100;
        private readonly string[][] _words;     // 줄바꿈으로 나뉜 문단마다의 어절
        private readonly float[][] _unit;       // 어절 폭 (글자 크기 1px 기준)
        private readonly float _space;          // 어절 사이 간격 (글자 크기 1px 기준, 사용자 배율 적용)
        private readonly float _lineHeight;     // 한 줄의 높이 (글자 크기 1px 기준)
        private readonly int _letters;          // 공백을 뺀 글자 수

        public WordMetrics(Graphics g, FontFamily family, FontStyle style, string text, AppSettings s)
        {
            using var font = new Font(family, Reference, style, GraphicsUnit.Pixel);
            float W(string t) => g.MeasureString(t, font, PointF.Empty, Typographic).Width / Reference;
            _words = text.Split('\n').Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();   // 사용자가 넣은 줄바꿈은 존중
            _unit = _words.Select(p => p.Select(W).ToArray()).ToArray();
            _space = W(" ") * (float)Math.Clamp(s.LetterWordSpacing, 0.3, 2.0);
            _lineHeight = font.GetHeight(g) / Reference;
            _letters = _words.Sum(p => p.Sum(w => w.Length));
        }

        public float SpaceAt(float size) => _space * size;

        public float LineHeightAt(float size) => _lineHeight * size;

        /// <summary>줄 폭 maxWidth로 어절을 줄에 담는다. 줄 수를 늘리지 않는 한도에서 줄 폭을 좁혀 줄 길이를 고르게 한다
        /// ("세상엔 길거리에서 / 코스프레" 대신 "세상엔 / 길거리에서 / 코스프레"처럼). 어절 하나가 줄보다 길면 null.</summary>
        private List<string>? Wrap(float size, float maxWidth, out float widest)
        {
            widest = 0;
            float longestWord = _unit.SelectMany(p => p).DefaultIfEmpty(0).Max() * size;
            if (longestWord > maxWidth) return null;

            int target = Count(size, maxWidth);
            float lo = longestWord, hi = maxWidth;
            for (int i = 0; i < 8 && hi - lo > 1; i++)
            {
                float mid = (lo + hi) / 2;
                if (Count(size, mid) <= target) hi = mid; else lo = mid;
            }

            var lines = new List<string>();
            float space = _space * size;
            for (int p = 0; p < _words.Length; p++)
            {
                var line = new List<string>();
                float width = 0;
                for (int k = 0; k < _words[p].Length; k++)
                {
                    float w = _unit[p][k] * size;
                    if (line.Count > 0 && width + space + w > hi)
                    {
                        lines.Add(string.Join(' ', line)); widest = Math.Max(widest, width);
                        line.Clear(); width = 0;
                    }
                    width += (line.Count > 0 ? space : 0) + w;
                    line.Add(_words[p][k]);
                }
                lines.Add(string.Join(' ', line)); widest = Math.Max(widest, width);
            }
            return lines;
        }

        private int Count(float size, float maxWidth)
        {
            int lines = 0;
            float space = _space * size;
            foreach (var paragraph in _unit)
            {
                lines++;
                float width = 0;
                bool any = false;
                foreach (float unit in paragraph)
                {
                    float w = unit * size;
                    if (any && width + space + w > maxWidth) { lines++; width = 0; any = false; }
                    width += (any ? space : 0) + w;
                    any = true;
                }
            }
            return lines;
        }

        /// <summary>이 크기로 풍선 안에 넣을 줄 나눔을 찾는다. 줄 폭을 넓게도 좁게도 잡아 보고 가장 여유 있게 들어가는 것을 고른다.
        /// 어절을 꺾지 않고는 들어가지 않으면 null.</summary>
        public List<string>? Arrange(float size, BlockLayout box, float spacing)
        {
            float lineH = LineHeightAt(size), advance = size * LineAdvanceEm * spacing;
            var rect = box.Rect;
            List<string>? best = null;
            float bestScore = 1.0001f;
            foreach (float part in box.Round ? RoundWidths : BoxWidths)
            {
                float wrap = Math.Min(rect.Width * part, box.Round ? rect.Width : Math.Max(rect.Width, box.MaxWrap));
                if (Wrap(size, wrap, out float widest) is not { } lines) continue;
                // 좁은 풍선(겹친 풍선의 한쪽 등)에서 글자를 키우면 한 줄에 한 자씩 세로로 쌓인다 — 그건 버린다.
                // 다만 **한 줄에 두 자는 받아들인다**: 사람이 식자한 원고(한글 식자가 분명한 57권, 여러 줄 말풍선 805개)에서 폭이 글자 3.5자 안 되는
                // 좁은 풍선 157개는 줄당 글자 수 중앙값이 2.1자(25~75%: 1.7~2.6)이고, 글자 크기는 줄이지 않았다(글자 높이가 쪽의 1.46% —
                // 넓은 풍선의 1.2~1.3%보다 오히려 크다). 처음에는 "세 자 폭 이상"(2.6em)으로 잡았다가 좁은 풍선의 글자가 너무 작아져 이 자료로 고쳤다
                // (사용자: "좁은 풍선 글자 크기는 학습한 데이터로 조절", 자료: D:\NekoManga-train\letter_survey\narrow.py).
                if (_letters > 6 && lines.Count > 2 && widest < size * NarrowestLine) continue;
                float height = lineH + (lines.Count - 1) * advance;
                float score;
                if (box.Round)
                {
                    // 글 덩어리의 네 귀가 풍선 안에 들어가야 한다. 풍선은 타원과 둥근 사각형의 중간쯤이라 지수 2.6의 곡선으로 본다.
                    // 자리(rect)는 세 줄 중 가장 좁게 잰 값이라 타원 풍선에서는 이미 실제보다 13%쯤 작다 — 그만큼이 테두리 여유가 된다.
                    // 사람은 글을 풍선 폭의 0.8~0.9까지 넓게 쓰고 위아래는 넉넉히 비운다(3~4줄에서 높이의 0.4~0.6).
                    // 세로 한도 0.50 = 폭 0.88일 때 높이 0.61까지. 0.43으로 더 빡빡하게 잡았더니 번역 안 된 원고 32쪽에서 글자 높이 중앙값이
                    // 쪽의 1.1%로 사람 식자(1.3%)보다 작았고, 0.50에서 1.2%가 되면서 잰 9개 항목이 모두 사람 식자의 25~75% 범위에 들어왔다.
                    float a = rect.Width * 0.50f, b = rect.Height * 0.50f;
                    score = MathF.Pow(widest / 2 / a, 2.6f) + MathF.Pow(height / 2 / b, 2.6f);
                }
                else
                    score = Math.Max(widest / (rect.Width * 1.05f + 2), height / (rect.Height + 2));
                if (score < bestScore) { bestScore = score; best = lines; }
            }
            return best;
        }

        /// <summary>글이 길 때 가장 긴 줄이 가져야 할 최소 폭(em) — 두 자 폭. 한 자씩 쌓이는 것만 막는다.</summary>
        public const float NarrowestLine = 1.7f;

        private static readonly float[] RoundWidths = [0.92f, 0.80f, 0.68f, 0.56f, 0.44f];
        private static readonly float[] BoxWidths = [1.00f, 0.85f, 0.70f];
    }

    /// <summary>OpenCV Mat(BGR 8UC3) → GDI+ Bitmap(24bppRgb). 행 단위로 복사한다 (stride가 다름).</summary>
    private static unsafe Bitmap ToBitmap(Mat mat)
    {
        using var bgr = mat.Channels() == 3 ? mat.Clone() : mat.CvtColor(ColorConversionCodes.GRAY2BGR);
        var bmp = new Bitmap(bgr.Width, bgr.Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            long srcStep = (long)bgr.Step();
            int rowBytes = bgr.Width * 3;
            for (int y = 0; y < bgr.Height; y++)
            {
                var src = bgr.DataPointer + y * srcStep;
                var dst = data.Scan0 + y * data.Stride;
                Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>GDI+ Bitmap → OpenCV Mat(BGR 8UC3).</summary>
    private static unsafe Mat ToMat(Bitmap bmp)
    {
        var mat = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC3);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            long dstStep = (long)mat.Step();
            int rowBytes = bmp.Width * 3;
            for (int y = 0; y < bmp.Height; y++)
            {
                var src = data.Scan0 + y * data.Stride;
                var dst = mat.DataPointer + y * dstStep;
                Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        return mat;
    }

    private static void DrawBlock(Graphics g, TextBlockInfo b, BlockLayout plan, Color color, AppSettings s)
    {
        var (family, style) = ResolveStyle(b, s);
        // 블록별 지정이 없으면(빈 문자열) 페이지 기본 설정을 따른다
        var alignName = string.IsNullOrEmpty(b.Align) ? s.LetterAlign : b.Align;
        var align = alignName switch { "Left" => StringAlignment.Near, "Right" => StringAlignment.Far, _ => StringAlignment.Center };
        float spacing = LineSpacing(b, s);
        var words = new WordMetrics(g, family, style, b.Translation, s);

        // 세로쓰기 원문도 한국어는 가로로 그린다 (국내 식자 관행). 줄바꿈은 어절 단위로 직접 짠다 — GDI+ 기본 래핑은
        // CJK를 글자 아무 데서나 꺾어 "멀긴한/데"처럼 어절이 잘린다.
        // 크기는 PlanLayout이 풍선에 맞춰 정해 왔다. 순서: ① 그 크기에서 조금(75%까지) 줄여서라도 어절을 통째로,
        // ② 안 되면 긴 어절을 글자 단위로 꺾어서(띄어쓰기 없는 긴 글, 아주 좁은 풍선), ③ 그래도 안 되면 최소 크기로 넘치더라도 그린다.
        void Draw(IReadOnlyList<string> lines, float size) =>
            DrawLines(g, lines, family, style, size, size * LineAdvanceEm * spacing, words.SpaceAt(size), plan.Rect, align, color, s, (float)b.RotationDeg);

        for (float size = plan.Size; size >= Math.Max(MinSize, plan.Size * ShrinkBeforeBreak); size *= 0.95f)
            if (words.Arrange(size, plan, spacing) is { } lines) { Draw(lines, size); return; }
        for (float size = plan.Size; size >= MinSize; size *= 0.92f)
            if (BrokenLines(g, b.Translation, family, style, size, plan, spacing) is { } lines) { Draw(lines, size); return; }

        using var smallest = new Font(family, MinSize, style, GraphicsUnit.Pixel);
        float wrapWidth = Math.Min(plan.Rect.Width * WordOverflowAllow, Math.Max(plan.Rect.Width, plan.MaxWrap));
        Draw(WrapByWords(g, b.Translation, smallest, wrapWidth, out _).Split('\n'), MinSize);
    }

    /// <summary>줄마다 어절을 하나씩 놓아 경로를 쌓고 외곽선+채움으로 그린다 — 줄 간격·어절 간격·정렬·회전을 직접 제어한다.
    /// 왼쪽·오른쪽 정렬은 글 덩어리를 자리 한가운데에 두고 그 안에서 줄을 맞춘다 (사람이 식자한 원고의 왼쪽 맞춤이 이 모양이다).</summary>
    private static void DrawLines(Graphics g, IReadOnlyList<string> lines, FontFamily family, FontStyle style, float size,
        float lineAdvance, float wordGap, RectangleF rect, StringAlignment align, Color color, AppSettings s, float rotationDeg)
    {
        using var font = new Font(family, size, style, GraphicsUnit.Pixel);
        float lineH = font.GetHeight(g);
        float totalH = lines.Count == 0 ? 0 : lineAdvance * (lines.Count - 1) + lineH;
        float y = rect.Y + Math.Max(0, (rect.Height - totalH) / 2);

        float Width(string t) => g.MeasureString(t, font, PointF.Empty, Typographic).Width;

        // 줄마다 (앞 들여쓰기, 어절들, 어절 폭들, 줄 폭)
        var laid = new List<(float Indent, string[] Words, float[] Widths, float Total)>();
        foreach (var line in lines)
        {
            int lead = line.Length - line.TrimStart(' ').Length;      // 꺾인 어절의 이어지는 줄은 공백으로 들여 쓴다
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var widths = parts.Select(Width).ToArray();
            float indent = lead * wordGap;
            laid.Add((indent, parts, widths, indent + widths.Sum() + Math.Max(0, parts.Length - 1) * wordGap));
        }
        float blockW = laid.Count == 0 ? 0 : laid.Max(l => l.Total);
        float center = rect.X + rect.Width / 2;

        using var path = new GraphicsPath();
        foreach (var (indent, parts, widths, total) in laid)
        {
            float x = align switch
            {
                StringAlignment.Near => center - blockW / 2,
                StringAlignment.Far => center + blockW / 2 - total,
                _ => center - total / 2,
            } + indent;
            for (int i = 0; i < parts.Length; i++)
            {
                path.AddString(parts[i], family, (int)style, size, new PointF(x, y), Typographic);
                x += widths[i] + wordGap;
            }
            y += lineAdvance;
        }

        if (Math.Abs(rotationDeg) > 0.05f)
        {
            using var m = new Matrix();
            m.RotateAt(rotationDeg, new PointF(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            path.Transform(m);
        }

        // 외곽선(halo)으로 어떤 배경에서도 읽히게 한다.
        if (s.LetterOutlineScale > 0)
        {
            float w = (float)(Math.Max(2.5f, size / 7f) * Math.Clamp(s.LetterOutlineScale, 0, 4));
            using var halo = new Pen(ParseColor(s.LetterOutlineColorHex, Color.White), w) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, path);
        }
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>어절(스페이스 단위) 줄바꿈. 한 줄에 다음 어절이 안 들어가면 줄을 넘긴다 —
    /// "좀 / 멀긴한데 / 우리 집에서 / 기다리고 / 있어!!"처럼 어절이 중간에 꺾이지 않는다.
    /// 어절 하나가 줄 폭 자체를 넘으면 그 어절만 어쩔 수 없이 글자 단위로 쪼갠다.</summary>
    private static string WrapByWords(Graphics g, string text, Font font, float maxWidth, out bool brokeWord)
    {
        float W(string t) => t.Length == 0 ? 0 : g.MeasureString(t, font).Width;

        brokeWord = false;
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))   // 사용자가 직접 넣은 줄바꿈은 존중
        {
            string line = "";
            foreach (var word in rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (W(candidate) <= maxWidth) { line = candidate; continue; }

                if (line.Length > 0) { lines.Add(line); line = ""; }
                if (W(word) <= maxWidth) { line = word; continue; }

                // 어절 하나가 줄보다 길다 — 풍선을 살짝 벗어나더라도(35%까지) 통째로 둔다.
                // "근처에/서"처럼 어절이 꺾이는 것보다 가독성이 낫다 (사용자 요청: 벗어나도 됨).
                if (W(word) <= maxWidth * WordOverflowAllow) { lines.Add(word); continue; }

                // 그래도 너무 길면 글자 단위로 쪼개되, 이어지는 조각 줄은 들여쓰기로 표시한다.
                brokeWord = true;
                string piece = "";
                bool first = true;
                foreach (var ch in word)
                {
                    if (W(piece + ch) > maxWidth && piece.Length > (first ? 0 : BreakIndent.Length))
                    {
                        lines.Add(piece);
                        piece = BreakIndent;    // 들여쓰기 — 이 줄이 앞 어절의 연속임을 보여준다
                        first = false;
                    }
                    piece += ch;
                }
                line = piece;
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>긴 어절을 통째로 둘 때 허용하는 줄 폭 초과 배율 — 말풍선을 살짝 벗어나는 건
    /// 어절이 중간에 꺾이는 것보다 읽기 좋다 (사용자 피드백). DrawFitted의 폭 검사도 같이 쓴다.</summary>
    public const float WordOverflowAllow = 1.3f;
    /// <summary>쪼개진 어절의 이어지는 줄 앞에 붙이는 들여쓰기 — 일반 공백 2칸 (EN SPACE는 경로 렌더에서 □로 나온다, 실측).</summary>
    private const string BreakIndent = "  ";

    /// <summary>설정의 글꼴 이름을 FontFamily로. 미설치 이름이면 Malgun Gothic → GenericSansSerif 순으로 대체.</summary>
    private static FontFamily ResolveFamily(string name)
    {
        try { return new FontFamily(name); }
        catch
        {
            try { return new FontFamily("Malgun Gothic"); }
            catch { return FontFamily.GenericSansSerif; }
        }
    }

    private static Color ParseColor(string hex, Color fallback) =>
        hex.StartsWith('#') && hex.Length == 7 ? ParseColor(hex) : fallback;

    private static Color ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith('#') && hex.Length == 7)
                return Color.FromArgb(
                    Convert.ToInt32(hex[1..3], 16),
                    Convert.ToInt32(hex[3..5], 16),
                    Convert.ToInt32(hex[5..7], 16));
        }
        catch { }
        return Color.Black;
    }
}
