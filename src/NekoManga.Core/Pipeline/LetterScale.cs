using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary>번역 블록 좌표(번역할 때의 원본 쪽 기준) → 식자할 그림(식자 전 원고·결과물) 좌표의 축별 비율.
///
/// 채색은 쪽을 메가픽셀에 맞춰 줄이면서 변을 8의 배수로 맞춘다(WorkflowBuilder의 resolution_steps 8) — 그래서 가로 비율과 세로 비율이
/// 조금 다르다(1000×1500 → 832×1240이면 0.832와 0.8267, 쪽 맨 아래에서 8px 벌어진다).
/// 최종 식자(<see cref="TextRenderer"/>.RenderBlocks)와 결과 창의 편집 미리보기·상자 옮기기가 이 한 곳의 계산을 같이 쓴다. v1.8.2까지는 미리보기만 두 축에 가로 비율 하나를 써서
/// 미리보기 상자가 쪽 아래로 갈수록 최종 글자보다 밑에 놓였고, 손으로 옮긴 상자도 세로가 틀린 비율로 블록에 저장됐다.</summary>
public readonly record struct LetterScale(double X, double Y)
{
    /// <summary>원본 크기를 알 길이 없을 때 — 블록 좌표를 그대로 쓴다.</summary>
    public static LetterScale One => new(1, 1);

    /// <summary>식자할 그림의 크기(imageW×imageH)와 원본 크기로 비율을 정한다.
    /// 식자 사이드카에 박제한 원본 크기(srcW×srcH)가 있으면 그것으로 — 참조 파일(원본)은 읽지 않는다.
    /// 0이면 참조 파일의 크기로, 그것도 못 읽으면 비율 1. 0으로 오는 것은 크기 없이 부르는 호출(파이프라인의 첫 식자 — 인자 5개짜리 RenderBlocks)과
    /// 손으로 만들거나 고친 사이드카뿐이다 — 식자 사이드카(<see cref="LetterSidecar"/>)는 처음 생긴 v0.6.0부터 SrcW·SrcH를 저장해서 '크기가 없는 옛 사이드카'는 없다.
    /// 참조 파일이 있는데 열리지 않으면(잠김·권한) <see cref="ImgIo.Read"/>의 예외가 그대로 나간다.</summary>
    public static LetterScale Resolve(int imageW, int imageH, int srcW, int srcH, string? sourceSizeRefPath = null)
    {
        if (srcW > 0 && srcH > 0)
            return new((double)imageW / srcW, (double)imageH / srcH);
        if (File.Exists(sourceSizeRefPath))
        {
            using var src = ImgIo.Read(sourceSizeRefPath!, ImreadModes.Color);
            if (!src.Empty())
                return new((double)imageW / src.Width, (double)imageH / src.Height);
        }
        return One;
    }

    /// <summary>식자할 그림 좌표의 사각형(왼쪽 위 + 크기)을 블록 좌표(원본 기준, 정수)로 되돌린다 — 결과 창에서 손으로 옮기거나 늘린 상자를 블록에 저장할 때.
    /// 왼쪽 위와 크기를 따로 반올림한다: 옮기기만 한 상자의 크기가 반올림 때문에 1px씩 달라지지 않게.</summary>
    public (int X1, int Y1, int X2, int Y2) ToSource(double left, double top, double width, double height)
    {
        // default(LetterScale)처럼 비율이 0이면 나눌 수 없다 — 모르는 것으로 보고 1.
        double sx = X > 0 ? X : 1, sy = Y > 0 ? Y : 1;
        int x1 = (int)Math.Round(left / sx), y1 = (int)Math.Round(top / sy);
        return (x1, y1, x1 + (int)Math.Round(width / sx), y1 + (int)Math.Round(height / sy));
    }

    /// <summary>결과 창에서 편집 상자를 놓았을 때 블록에 저장할 사각형(원본 기준, 정수). 상자는 식자할 그림 좌표로 준다:
    /// shown은 상자를 띄운 자리(그 뒤에 한 번 놓았으면 그때 놓은 자리), dropped는 지금 놓은 자리.
    /// <para>둘이 같으면 null — 아무것도 바꾸지 않는다. 손잡이를 누르기만 하고 놓아도 '놓았다'(DragCompleted)가 오는데, 그때 상자 자리를 블록에 저장하면
    /// 자동으로 놓인 블록이 잰 풍선 크기의 손으로 잡은 블록으로 바뀌었다(v1.8.2까지 실측: (150,620,350,720) → ManualRect (100,560,400,780)).</para>
    /// <para>옮기거나 늘렸으면 그 상자가 '글 자리'가 되는 블록 사각형을 돌려준다(<see cref="TextRenderer.ManualBlockFor"/>) — ManualRect로 저장하면
    /// 다시 띄운 상자와 최종 식자가 놓은 자리에 온다(블록 좌표가 정수라 원본 1px이 안 되는 오차는 남는다). 놓고 다시 띄우기를 되풀이해도 블록이 줄거나 밀리지 않는다.
    /// 상자를 띄울 때 쓴 비율로 불러야 한다 — 다른 비율로 되돌리면 놓은 자리와 다른 곳(쪽 밖일 수도 있다)이 저장된다.</para></summary>
    public (int X1, int Y1, int X2, int Y2)? CommitBox(LetterBox shown, LetterBox dropped)
    {
        if (dropped.SameAs(shown)) return null;
        var (left, top, width, height) = TextRenderer.ManualBlockFor(dropped.Left, dropped.Top, dropped.Width, dropped.Height);
        return ToSource(left, top, width, height);
    }
}

/// <summary>결과 창 편집 상자 하나의 자리 — 식자할 그림 좌표(화면 좌표 ÷ 확대 배율).</summary>
public readonly record struct LetterBox(double Left, double Top, double Width, double Height)
{
    /// <summary>같은 자리인가. 나눗셈의 끝자리 오차만 봐준다 — 마우스로 옮길 수 있는 가장 짧은 거리(화면 1px, 가장 크게 확대한 4배에서 그림 0.25px)보다 훨씬 작은 값이다.</summary>
    public bool SameAs(LetterBox o) =>
        Math.Abs(Left - o.Left) < Tolerance && Math.Abs(Top - o.Top) < Tolerance &&
        Math.Abs(Width - o.Width) < Tolerance && Math.Abs(Height - o.Height) < Tolerance;

    private const double Tolerance = 1e-6;
}
