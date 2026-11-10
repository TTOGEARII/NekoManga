using NekoManga.Core.Pipeline;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>결과 이름 규칙·사이드카·이미지 입출력 테스트가 함께 쓰는 도우미.</summary>
internal static class PipelineTestHelpers
{
    /// <summary>한글·일본어·공백에 더해, 한 가지 ANSI 코드페이지(CP949·CP932·CP1252 어느 것이든)로는 다 옮길 수 없는 문자(반각 가나·간체자·악센트)를 섞은 이름.
    /// 경로를 현재 코드페이지로 마샬링하는 API는 이 이름에서 반드시 실패한다.</summary>
    public const string UnicodeName = "만화 원고 日本語 ｶﾅ 们 é";

    /// <summary>모델·ComfyUI 없이 러너를 돌릴 설정 — 번역·검열 제거·채색을 모두 끄고, 런타임 폴더도 임시 폴더 안으로 돌려 둔다.</summary>
    public static AppSettings EnginelessSettings(TempDir t, string outputRoot = "") => new()
    {
        EnableTranslate = false,
        EnableDecensor = false,
        EnableAutoColor = false,
        RuntimeRoot = t.At("runtime"),
        OutputRoot = outputRoot,
    };

    /// <summary>무늬가 있는 합성 컬러 이미지(BGR). seed마다 바탕색이 달라 서로 다른 원고 구실을 한다.</summary>
    public static Mat ColorImage(int width = 60, int height = 40, int seed = 0)
    {
        var img = new Mat(height, width, MatType.CV_8UC3, new Scalar(20 + seed * 37 % 200, 120, 230 - seed * 53 % 200));
        img.Rectangle(new Rect(width / 6, height / 5, width / 3, height / 4), new Scalar(255, 0, 0), -1);
        img.Line(0, height - 1, width - 1, 0, new Scalar(0, 0, 255), 2);
        return img;
    }

    /// <summary>밝은 바탕에 검은 사각형 하나가 있는 회색조 이미지.</summary>
    public static Mat GrayImage(int width = 60, int height = 40)
    {
        var img = new Mat(height, width, MatType.CV_8UC1, new Scalar(255));
        img.Rectangle(new Rect(width / 6, height / 5, width / 3, height / 4), new Scalar(0), -1);
        return img;
    }

    /// <summary>사각형 하나가 칠해진 검열 마스크(CV_8UC1, 255=지울 곳).</summary>
    public static Mat RectMask(int width, int height, Rect rect)
    {
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        mask.Rectangle(rect, Scalar.White, -1);
        return mask;
    }

    /// <summary>합성 이미지를 그 확장자의 포맷으로 저장하고 경로를 돌려준다 (폴더는 만들어 준다).</summary>
    public static string WriteImageFile(string path, int seed = 0, int width = 60, int height = 40)
    {
        using var img = ColorImage(width, height, seed);
        Assert.True(ImgIo.Write(path, img));
        return path;
    }

    /// <summary>내용이 정해진 바이트 파일을 만들고 경로를 돌려준다 (폴더는 만들어 준다) — 길이만 중요한 원고 대역.</summary>
    public static string WriteBytesFile(string path, int length, byte fill = 0x5A)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>두 이미지가 크기·형식·픽셀까지 같은지 본다.</summary>
    public static void AssertSamePixels(Mat expected, Mat? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Size(), actual.Size());
        Assert.Equal(expected.Type(), actual.Type());
        using var diff = new Mat();
        Cv2.Absdiff(expected, actual, diff);
        using var flat = diff.Reshape(1);
        Assert.Equal(0, Cv2.CountNonZero(flat));
    }

    /// <summary>폴더 아래 모든 파일의 상대 경로(정렬됨) — 무엇이 생기고 사라졌는지 통째로 비교할 때 쓴다. 폴더가 없으면 빈 목록.</summary>
    public static string[] FilesUnder(string root) =>
        !Directory.Exists(root) ? [] :
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(root, f))
                 .OrderBy(f => f, StringComparer.Ordinal)
                 .ToArray();

    /// <summary>임시 쓰기 파일(&lt;경로&gt;.xxxxxxxx.part)이 폴더 아래 어디에도 남지 않았는지 본다.</summary>
    public static void AssertNoTempFiles(string root) =>
        Assert.DoesNotContain(FilesUnder(root), f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));

    /// <summary>보고를 부른 자리에서 바로 모으는 진행 수집기. Progress&lt;T&gt;는 보고를 나중에 다른 스레드에서 전달해 순서와 시점이 흔들린다.</summary>
    public sealed class ProgressLog : IProgress<PageProgress>
    {
        private readonly List<PageProgress> _items = [];

        public void Report(PageProgress value) { lock (_items) _items.Add(value); }

        public IReadOnlyList<PageProgress> Items { get { lock (_items) return _items.ToList(); } }

        /// <summary>그 원고의 끝 줄(Done) — 한 장에 정확히 한 번이어야 한다.</summary>
        public PageProgress FinalOf(string file) => Assert.Single(Items, p => p.Done && p.File == file);
    }
}
