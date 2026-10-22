using OpenCvSharp;

namespace NekoManga.Core.Engine;

/// <summary>번역된 말풍선 하나 (원본 이미지 좌표).
/// ManualRect: 결과 보기 창에서 사용자가 위치·크기를 손으로 조정했음 —
/// 식자 때 말풍선 폭 자동 측정(WidenToBubble)을 건너뛰고 이 좌표를 그대로 쓴다.
/// StyleScale 이하는 블록별 스타일 오버라이드(결과 보기 창의 '이 풍선만') —
/// 전부 기본값 있는 마지막 파라미터라 구버전 사이드카 JSON도 그대로 읽힌다.</summary>
public record TextBlockInfo(
    int X1, int Y1, int X2, int Y2,
    string SourceText,
    string Translation,
    double FontSize,
    bool Vertical,
    string FontColorHex,
    bool ManualRect = false,
    double StyleScale = 1.0,       // 블록별 크기 배율 (1 = 페이지 공통 크기 그대로)
    string Align = "",             // Left/Center/Right — 비면 페이지 기본(LetterAlign) 따름
    int BoldOverride = -1,         // -1=페이지 설정 따름, 0=보통, 1=굵게
    bool Italic = false,           // 페이지 기본(LetterItalic)에 더해 켠다
    double LineSpacing = 0,        // 줄간격 배율 — 0이면 페이지 기본(LetterLineSpacing) 따름
    string ColorOverride = "",     // 블록별 글자색 #RRGGBB (비면 전역 규칙)
    double RotationDeg = 0);       // 글자 기울이기(회전, 도)

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
        // 반대로 한 풍선의 글 기둥이 기둥마다 따로 잡힌 것은 다시 하나로 — 따로 두면 반 토막 번역이
        // 좁은 띠에 억지로 들어가 옆 기둥의 번역과 겹친다 (실측 401ちゃん 05·11·12·22p).
        detBlocks = await Task.Run(() => BubbleSplitter.MergeSiblings(img, textMask, detBlocks), ct);

        // ── OCR ──
        var blocks = new List<(DetectedBlock Box, string Text)>();
        foreach (var b in detBlocks)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"글자 읽는 중 ({blocks.Count + 1}/{detBlocks.Count})");
            var pad = BubbleSolver.Enlarge(new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1), 1.1, img.Width, img.Height);
            using var crop = new Mat(img, pad);
            // 거의 단색(새까만 페이지의 노이즈, 흰 여백)인 블록은 글자가 있을 수 없다 — OCR·번역을 건너뛴다.
            // 실사고: 검은 페이지에서 CTD가 노이즈 두 덩어리를 말풍선으로 잡아 쓰레기 글자를 DeepSeek에 보내 몇 분씩 기다렸다.
            if (IsFlatCrop(crop)) continue;
            var text = await Task.Run(() => _ocr.Recognize(crop), ct);
            if (string.IsNullOrWhiteSpace(text)) continue;
            // OCR 결과에 원문 언어의 글자가 하나도 없으면(기호·숫자·알파벳 잡음) 번역할 것이 없다
            if (!LooksLikeSource(text, settings.TranslateSource)) continue;
            blocks.Add((b, text));
        }

        // ── 번역 (배치) ──
        IReadOnlyList<string> translations = Array.Empty<string>();
        if (blocks.Count > 0)
        {
            var engine = TranslatorFactory.EngineLabel(settings);
            status?.Report($"번역 중 ({blocks.Count}개 말풍선 · {engine})");
            var translator = TranslatorFactory.Create(settings);
            // 느린 LLM 엔진은 한 페이지에 1~2분씩 걸린다 — 멈춘 게 아니라는 걸 알 수 있게 경과 초를 띄운다
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var started = DateTime.UtcNow;
            var ticker = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(5000, tick.Token);
                        var sec = (int)(DateTime.UtcNow - started).TotalSeconds;
                        status?.Report($"번역 중 ({blocks.Count}개 말풍선 · {engine} {sec}초)");
                    }
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                translations = await translator.TranslateAsync(
                    blocks.Select(b => b.Text).ToList(),
                    settings.TranslateSource, settings.TranslateTarget, ct);
            }
            finally { tick.Cancel(); await ticker; }
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
            tr = PolishTranslation(tr, settings.TranslateTarget);
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

    /// <summary>번역문 뒷손질 — 일본어가 아닌 목표 언어에 장음 기호 ー·〜가 남으면 ~로 (LLM이 "또ー!?"처럼 그대로 두는 실측),
    /// 일본어 전각 느낌표·물음표는 반각으로. 세로쓰기 기호가 가로 식자에서 긴 막대로 그려지는 것을 막는다.</summary>
    internal static string PolishTranslation(string tr, string targetLang)
    {
        var lang = (targetLang ?? "").ToLowerInvariant();
        if (lang == "ja") return tr;
        return tr.Replace('ー', '~').Replace('〜', '~').Replace('～', '~').Replace('！', '!').Replace('？', '?')
                 .Replace("・・・", "...").Replace("‥", "..");   // 가운뎃점 말줄임은 가로 식자에서 점이 띄엄띄엄 그려진다 (실측 06p)
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

    /// <summary>블록이 거의 단색(표준편차가 아주 작음)이면 글자가 있을 수 없다 — 검은 페이지의 노이즈, 빈 여백.</summary>
    internal static bool IsFlatCrop(Mat crop)
    {
        if (crop.Empty()) return true;
        using var gray = new Mat();
        if (crop.Channels() == 1) crop.CopyTo(gray); else Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out _, out var std);
        return std.Val0 < 6;
    }

    /// <summary>OCR 결과가 원문 언어의 글처럼 보이는지 — 글자(문장부호·공백 제외) 중 원문 문자 집합 비율이 절반 이상.
    /// 일본어는 가나·한자, 중국어는 한자, 한국어는 한글·한자, 그 외는 Letter. manga-ocr은 잡음·영문 손글씨("END")에도
    /// 'Ekゅ' 같은 환각을 내므로 한 글자 포함으로는 못 거른다.</summary>
    internal static bool LooksLikeSource(string text, string sourceLang)
    {
        var lang = (sourceLang ?? "").ToLowerInvariant();
        int letters = 0, hits = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            letters++;
            int c = ch;
            bool kana = c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;
            bool han = c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF;
            bool hangul = c is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;
            bool hit = lang switch
            {
                "ja" => kana || han,
                "zh" or "zh-cn" or "zh-tw" => han,
                "ko" => hangul || han,
                _ => char.IsLetter(ch),
            };
            if (hit) hits++;
        }
        return hits > 0 && hits * 2 >= letters;
    }
}
