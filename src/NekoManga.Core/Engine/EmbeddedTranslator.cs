using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>번역된 말풍선 하나 (원본 이미지 좌표).
/// ManualRect: 결과 보기 창에서 사용자가 위치·크기를 손으로 조정했음 —
/// 식자 때 말풍선 폭 자동 측정(WidenToBubble)을 건너뛰고 이 좌표를 그대로 쓴다.</summary>
public record TextBlockInfo(
    int X1, int Y1, int X2, int Y2,
    string SourceText,
    string Translation,
    double FontSize,
    bool Vertical,
    string FontColorHex,
    bool ManualRect = false);

public record PageTranslation(string InpaintedPath, IReadOnlyList<TextBlockInfo> Blocks);

/// <summary>내장 번역 파이프라인: 감지(CTD) → OCR(manga-ocr) → 번역 → 글자 지우기(단색 풍선/LaMa).
/// BallonsTranslator 없이 전부 프로세스 안(ONNX Runtime)에서 돈다.</summary>
public class EmbeddedTranslator(AppSettings settings, OnnxEnv env)
{
    private readonly TextDetector _detector = new(env);
    private readonly MangaOcr _ocr = new(env);
    private readonly LamaInpainter _lama = new(env);

    public bool IsReady => _detector.IsReady && _ocr.IsReady && _lama.IsReady;

    public static readonly string[] RequiredModels =
        { TextDetector.ModelFile, MangaOcr.EncoderFile, MangaOcr.DecoderFile, MangaOcr.VocabFile, LamaInpainter.ModelFile };

    /// <summary>한 페이지를 처리한다. 글자 지운 이미지를 workDir에 저장하고 블록·번역을 돌려준다.</summary>
    public async Task<PageTranslation> TranslatePageAsync(
        string imagePath, string workDir, IProgress<string>? status, CancellationToken ct)
    {
        if (!IsReady)
            throw new FileNotFoundException("내장 엔진 모델이 없어요. 설정 → 구성요소에서 설치해 주세요.");

        using var img = ImgIo.Read(imagePath, ImreadModes.Color);
        if (img.Empty())
            throw new InvalidDataException($"이미지를 읽지 못했어요: {Path.GetFileName(imagePath)}");

        status?.Report("말풍선 찾는 중");
        var det = await Task.Run(() => _detector.Detect(img), ct);
        using var textMask = det.TextMask;

        // 맞닿은 말풍선 묶음이 한 블록으로 잡히면 풍선별로 나눈다 — 안 나누면
        // 여러 풍선의 대사가 한 번에 OCR·번역돼 풍선 경계를 무시한 식자가 된다.
        var detBlocks = new List<DetectedBlock>();
        foreach (var b in det.Blocks)
            detBlocks.AddRange(await Task.Run(() => BubbleSplitter.Split(img, textMask, b), ct));
        if (detBlocks.Count > det.Blocks.Count) // 읽기 순서 복원 (위→아래, 오른→왼)
            detBlocks.Sort((a, b2) => a.Y1 != b2.Y1 ? a.Y1.CompareTo(b2.Y1) : b2.X1.CompareTo(a.X1));

        // ── OCR ──
        var blocks = new List<(DetectedBlock Box, string Text)>();
        foreach (var b in detBlocks)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"글자 읽는 중 ({blocks.Count + 1}/{detBlocks.Count})");
            var pad = BubbleSolver.Enlarge(new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1), 1.1, img.Width, img.Height);
            using var crop = new Mat(img, pad);
            var text = await Task.Run(() => _ocr.Recognize(crop), ct);
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add((b, text));
        }

        // ── 번역 (배치) ──
        IReadOnlyList<string> translations = Array.Empty<string>();
        if (blocks.Count > 0)
        {
            status?.Report($"번역 중 ({blocks.Count}개 말풍선)");
            var translator = TranslatorFactory.Create(settings);
            translations = await translator.TranslateAsync(
                blocks.Select(b => b.Text).ToList(),
                settings.TranslateSource, settings.TranslateTarget, ct);
        }

        // ── 글자 색 추출 + 글자 지우기 ──
        status?.Report("글자 지우는 중");
        var result = new List<TextBlockInfo>();
        using var lamaMask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        bool needLama = false;

        for (int i = 0; i < blocks.Count; i++)
        {
            var (b, text) = blocks[i];
            var rect = new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1);

            string color = SampleTextColor(img, textMask, rect);

            if (!await Task.Run(() => BubbleSolver.TrySolidFill(img, textMask, rect), ct))
            {
                // 단색 풍선이 아님 — 블록 내부 마스크를 LaMa 대상으로 모은다
                var roi = BubbleSolver.Enlarge(rect, 1.15, img.Width, img.Height);
                using var src = new Mat(textMask, roi);
                using var dst = new Mat(lamaMask, roi);
                Cv2.BitwiseOr(dst, src, dst);
                needLama = true;
            }

            var tr = i < translations.Count ? translations[i] : "";
            if (string.IsNullOrWhiteSpace(tr)) tr = text;
            result.Add(new TextBlockInfo(
                b.X1, b.Y1, b.X2, b.Y2, text, tr,
                EstimateFontSize(rect, tr.Length),
                Vertical: rect.Height > rect.Width * 1.3,
                color));
        }

        if (needLama)
        {
            status?.Report("배경 복원 중 (LaMa)");
            await Task.Run(() => _lama.Inpaint(img, lamaMask), ct);
        }

        Directory.CreateDirectory(workDir);
        var outPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(imagePath) + ".inpainted.png");
        ImgIo.Write(outPath, img);
        return new PageTranslation(outPath, result);
    }

    /// <summary>블록 안 텍스트 획 픽셀의 평균색 → 식자 글자색.</summary>
    private static string SampleTextColor(Mat img, Mat textMask, Rect rect)
    {
        try
        {
            using var imRoi = new Mat(img, rect);
            using var mkRoi = new Mat(textMask, rect);
            if (Cv2.CountNonZero(mkRoi) < 8) return "#000000";
            var mean = Cv2.Mean(imRoi, mkRoi); // BGR
            return $"#{(int)mean.Val2:X2}{(int)mean.Val1:X2}{(int)mean.Val0:X2}";
        }
        catch { return "#000000"; }
    }

    private static double EstimateFontSize(Rect rect, int charCount)
    {
        double area = (double)rect.Width * rect.Height;
        double size = Math.Sqrt(area / Math.Max(1, charCount)) * 0.85;
        return Math.Clamp(size, 11, 64);
    }
}
