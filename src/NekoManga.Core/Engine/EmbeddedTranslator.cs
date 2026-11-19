using System.Text.RegularExpressions;
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
    double RotationDeg = 0,        // 글자 기울이기(회전, 도)
    // 아래 넷은 결과 창 식자 탭의 "선택한 풍선" 글자 속성(2026-11-17) — 옛 json에는 없다 → 자동·쪽 설정
    double SizePx = 0,             // 글자 크기(px — 결과 그림의 픽셀). 0 = 자동(풍선에 맞춤 × StyleScale)
    string FontFamily = "",        // 글꼴 — 비면 쪽 설정
    double OutlinePx = -1,         // 외곽선 두께(px — 결과 그림의 픽셀, 글자 밖으로 보이는 폭). 음수 = 쪽 설정(자동 두께), 0 = 없음
    string OutlineColor = "",      // 외곽선 색 #RRGGBB — 비면 쪽 설정
    bool Hidden = false,           // 결과 창 [이 블록 숨기기] — 지우지 않고 그리지만 않는다(되살릴 수 있게). 옛 json에는 없다 → false
    bool ManualLines = false,      // 줄을 직접 나눴다 — Translation의 줄바꿈 그대로 그리고 자동 줄 나눔을 하지 않는다(결과 창에서 제자리 글 고치기·[줄 직접 나누기])
    bool Added = false);           // 결과 창 [새 글 넣기]로 넣은 블록 — 원문이 없다. 지우면 숨기지 않고 목록에서 뺀다

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
    /// <param name="stem">작업 폴더에 남길 중간 파일 이름(확장자 없이). 비우면 원고 이름 — 러너는 결과물 이름을 넘겨 01.jpg·01.png가 서로 덮어쓰지 않게 한다.</param>
    public async Task<PageTranslation> TranslatePageAsync(
        string imagePath, string workDir, IProgress<string>? status, CancellationToken ct, string? stem = null)
    {
        if (!IsReady)
            throw new FileNotFoundException(Loc.T("내장 엔진 모델이 없다냥. 설정 → 구성요소에서 설치해 줘냥."));

        using var img = ImgIo.Read(imagePath, ImreadModes.Color);
        if (img.Empty())
            throw new InvalidDataException(Loc.F($"이미지를 읽지 못했다냥: {Path.GetFileName(imagePath)}"));

        // 진단: NEKO_TIMING=<파일>이면 단계별로 걸린 시간을 적는다 (어디가 느린지 볼 때)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var laps = new List<string>();
        void Lap(string name) { laps.Add($"{name}={clock.ElapsedMilliseconds}ms"); clock.Restart(); }

        status?.Report(Loc.T("말풍선 찾는 중이다냥"));
        var det = await Task.Run(() => _detector.Detect(img), ct);
        Lap("감지");   // loc:none
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
        Lap("풍선 나누기·합치기");   // loc:none

        // ── OCR ──
        // 한 쪽의 블록을 한 묶음으로 읽는다 — 블록마다 따로 읽으면 디코더 실행 횟수가 블록 수만큼 곱해진다 (MangaOcr 주석).
        var blocks = new List<(DetectedBlock Box, string Text)>();
        var readable = new List<DetectedBlock>();
        var crops = new List<Mat>();
        try
        {
            foreach (var b in detBlocks)
            {
                var pad = BubbleSolver.Enlarge(new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1), 1.1, img.Width, img.Height);
                var crop = new Mat(img, pad);
                // 거의 단색(새까만 페이지의 노이즈, 흰 여백)인 블록은 글자가 있을 수 없다 — OCR·번역을 건너뛴다.
                // 실사고: 검은 페이지에서 CTD가 노이즈 두 덩어리를 말풍선으로 잡아 쓰레기 글자를 DeepSeek에 보내 몇 분씩 기다렸다.
                if (IsFlatCrop(crop)) { crop.Dispose(); continue; }
                readable.Add(b);
                crops.Add(crop);
            }
            ct.ThrowIfCancellationRequested();
            if (crops.Count > 0)
            {
                status?.Report(Loc.F($"글자 읽는 중이다냥 ({crops.Count}개 말풍선)"));
                var texts = await Task.Run(() => _ocr.RecognizeBatch(crops), ct);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(texts[i])) continue;
                    // OCR 결과에 원문 언어의 글자가 하나도 없으면(기호·숫자·알파벳 잡음) 번역할 것이 없다
                    if (!LooksLikeSource(texts[i], settings.TranslateSource)) continue;
                    blocks.Add((readable[i], texts[i]));
                }
            }
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
        Lap($"글자 읽기({detBlocks.Count}블록)");   // loc:none

        // ── 말풍선 밖의 효과음은 번역하지 않는다 (TranslateBubbleOnly, 2026-11-18 사용자 "번역은 말풍선 안에 있는 것만") ──
        // 풍선 밖으로 보이고(BubbleSolver.IsInBubble — 닫힌 테두리도 단색 바탕도 없다) 글이 효과음처럼 생긴(LooksLikeSfx) 블록은 번역기에 보내지 않고
        // 지우지도 식자하지도 않는다. 그런 글은 번역기가 폭주하기 쉽고(95자 "あぁ、あぁ......あああ…" → 32,772자, 35p) 식자해도 그림을 가린다.
        // 풍선 판정만으로 거르지 않는 까닭: 실제 원고 448블록에서 테두리 규칙은 65%만 풍선으로 봐(이 작가는 테두리가 닫히지 않는 풍선이 많다) 대사가 빠졌다 — 두 조건을 같이 건다.
        int outsideBubble = 0;
        if (settings.TranslateBubbleOnly && blocks.Count > 0)
        {
            var kept = new List<(DetectedBlock Box, string Text)>();
            foreach (var (b, text) in blocks)
            {
                var rect = new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1);
                if (LooksLikeSfx(text) && !await Task.Run(() => BubbleSolver.IsInBubble(img, textMask, rect), ct)) outsideBubble++;
                else kept.Add((b, text));
            }
            blocks = kept;
            // 거른 수를 진행 줄에 남긴다 — 다른 작가의 원고에서 대사가 빠지면 OCR 실패인지 이 거르기인지 가릴 길이 있어야 한다(검수 2026-11-19)
            if (outsideBubble > 0) status?.Report(Loc.F($"풍선 밖 효과음 {outsideBubble}개는 번역하지 않는다냥"));
        }
        Lap($"풍선 밖 글 거르기({outsideBubble}블록 제외)");   // loc:none

        // ── 번역 (배치) ──
        IReadOnlyList<string> translations = Array.Empty<string>();
        if (blocks.Count > 0)
        {
            var engine = TranslatorFactory.EngineLabel(settings);
            status?.Report(Loc.F($"번역 중이다냥 ({blocks.Count}개 말풍선 · {engine})"));
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
                        status?.Report(Loc.F($"번역 중이다냥 ({blocks.Count}개 말풍선 · {engine} {sec}초)"));
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

        Lap($"번역({blocks.Count}개·{TranslatorFactory.EngineLabel(settings)})");   // loc:none

        // ── 글자 색 추출 + 글자 지우기 ──
        status?.Report(Loc.T("글자 지우는 중이다냥"));
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
            tr = TameRunaway(PolishTranslation(tr, settings.TranslateTarget), text);
            result.Add(new TextBlockInfo(
                b.X1, b.Y1, b.X2, b.Y2, text, tr,
                EstimateFontSize(rect, tr.Length),
                Vertical: rect.Height > rect.Width * 1.3,
                color));
        }

        Lap("글자 지우기(단색)");   // loc:none
        if (needLama)
        {
            status?.Report(Loc.T("배경 복원 중이다냥 (LaMa)"));
            await Task.Run(() => _lama.Inpaint(img, lamaMask), ct);
            Lap("배경 복원(LaMa)");   // loc:none
        }

        Directory.CreateDirectory(workDir);
        var outPath = Path.Combine(workDir, (stem ?? Path.GetFileNameWithoutExtension(imagePath)) + ".inpainted.png");
        ImgIo.Write(outPath, img);
        Lap("저장");   // loc:none
        if (Environment.GetEnvironmentVariable("NEKO_TIMING") is { Length: > 0 } timing)
            try { File.AppendAllText(timing, Path.GetFileName(imagePath) + ": " + string.Join(", ", laps) + Environment.NewLine); } catch { }
        return new PageTranslation(outPath, result);
    }

    /// <summary><see cref="TameRunaway"/>의 상한 — 번역문은 원문의 이 배를 넘지 않고(짧은 원문은 최소 글자 수까지), 같은 글자는 이 수까지만 이어진다.
    /// 한국어는 일본어보다 글자 수가 비슷하거나 적어 4배면 정상 번역은 걸리지 않는다(지금까지의 식자 블록 537개 가운데 150자를 넘은 것은 폭주한 둘뿐).</summary>
    internal const int RunawayRatio = 4, RunawayMinChars = 60, MaxRepeat = 8;

    /// <summary>폭주한 번역문을 억제한다 — LLM 번역기가 늘인 소리("あああ…")를 되풀이하다 토큰 상한까지 채우면 한 어절 32,757자가 온다(DeepSeek, 2026-11-18 35p).
    /// 그 길이는 GDI+ 글 API의 한계(32K자)를 넘어 식자가 "A generic error occurred in GDI+"로 던졌고, 식자해도 풍선에 들어갈 글이 아니다.
    /// 원문의 <see cref="RunawayRatio"/>배(최소 <see cref="RunawayMinChars"/>자) 안이면 그대로 둔다. 넘으면 같은 글자가 <see cref="MaxRepeat"/> 넘게 이어진 것을
    /// 그 수로 접고, 그래도 길면 상한에서 자른다(서로게이트 쌍은 가르지 않는다). 식자기도 따로 길이를 막는다(TextRenderer.MaxTextChars) — 다른 길로 온 글 때문에.</summary>
    internal static string TameRunaway(string tr, string source)
    {
        int limit = Math.Max(RunawayMinChars, RunawayRatio * source.Length);
        if (tr.Length <= limit) return tr;
        var sb = new System.Text.StringBuilder(limit);
        int run = 0;
        for (int i = 0; i < tr.Length; i++)
        {
            run = i > 0 && tr[i] == tr[i - 1] ? run + 1 : 1;
            if (run <= MaxRepeat) sb.Append(tr[i]);
        }
        if (sb.Length > limit) sb.Length = char.IsHighSurrogate(sb[limit - 1]) ? limit - 1 : limit;
        return sb.ToString();
    }

    /// <summary>원문이 효과음·늘인 소리처럼 생겼는가 — 한자가 있으면 대사. 문장부호(、。？！「」)가 있고 알맹이(채움 글자를 뺀 뒤 같은 글자의 되풀이를 하나로 접은 것)가
    /// 둘 이상이면 대사("いやだあああ！" — 전에는 되풀이 셋에서 효과음으로 가, 풍선 판정까지 어긋나면 대사가 빠졌다, 검수 2026-11-19). 같은 글자(장음 포함)가 셋 넘게 이어지면("あああ", "はーーー") 효과음,
    /// 그 밖에 문장부호가 있으면 대사. 알맹이가 한 글자 이하면("あ", "ぐっっ", "はーっ...") 효과음 — 이 규칙은 **가나 글에만** 쓴다(채움 글자 표가 일본어 것이라 한국어·영어 한 음절 대사 "네"·"I"가 걸렸다).
    /// 그 밖은 대사("はい", "それを", "...じゃあ"는 남긴다). 풍선 밖 판정과 **같이** 써서 번역에서 뺀다(TranslateBubbleOnly).
    /// 실제 원고 448블록의 풍선 밖 판정 157블록 가운데 이것으로 거른 것과 남긴 것은 notes/translate-letter.md.</summary>
    public static bool LooksLikeSfx(string source)
    {
        var s = source.Trim();
        if (s.Length == 0) return true;
        bool kana = false;
        foreach (var ch in s)
        {
            if (IsKanji(ch)) return false;   // 한자 = 대사
            kana |= IsKana(ch);
        }
        // 알맹이: 채움 글자를 빼고, 같은 글자가 이어진 것은 하나로 — "あぁ、あぁ......あ、あああ"는 'あ' 하나, "いやだあああ！"는 'いやだ'
        var core = new System.Text.StringBuilder();
        foreach (var ch in s)
            if (!SfxFiller.Contains(ch) && (core.Length == 0 || core[^1] != ch)) core.Append(ch);
        bool marks = s.IndexOfAny(SentenceMarks) >= 0;
        if (marks && core.Length >= 2) return false;   // 늘인 소리가 섞여도 말이 있는 대사
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] == s[i - 1] && Array.IndexOf(RunBreakers, s[i]) < 0 ? run + 1 : 1;   // "..."·"……"는 되풀이가 아니다
            if (run >= 3) return true;   // "あああ"·"はーーー" — 폭주하는 꼴
        }
        if (marks) return false;
        return kana && core.Length <= 1;
    }

    /// <summary>한자(CJK 통합 한자 + 확장 A) — <see cref="LooksLikeSource"/>와 같은 범위.</summary>
    private static bool IsKanji(char ch) => ch is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿');
    /// <summary>가나(히라가나·가타카나·반각 가타카나).</summary>
    private static bool IsKana(char ch) => ch is (>= 'ぁ' and <= 'ヿ') or (>= 'ｦ' and <= 'ﾟ');

    private static readonly char[] SentenceMarks = ['、', '。', '？', '?', '！', '!', '「', '」', '『', '』'];
    private static readonly char[] RunBreakers = ['.', '…', '・', '、', '。', '!', '?', '！', '？', ' ', '　'];
    private const string SfxFiller = "ー〜～~♡♥…・.,、。!?！？ 　\n\tっッぁぃぅぇぉゃゅょゎァィゥェォャュョヮ゛゜゙゚";

    /// <summary>번역문 뒷손질 — 일본어가 아닌 목표 언어에 장음 기호 ー·〜가 남으면 ~로 (LLM이 "또ー!?"처럼 그대로 두는 실측),
    /// 일본어 전각 느낌표·물음표는 반각으로. 세로쓰기 기호가 가로 식자에서 긴 막대로 그려지는 것을 막는다.
    /// 중국어가 목표면 반각이 아니라 중국어 조판의 전각 부호로 맞춘다(<see cref="PolishChinese"/>). 일본어가 목표면 손대지 않는다.
    /// 목표 언어 코드는 settings.json 값 그대로 온다(설정 창에는 ko·en뿐) — 어느 언어인지는 <see cref="LangCode"/>가 정한다.</summary>
    internal static string PolishTranslation(string tr, string targetLang)
    {
        // 일본어로 옮길 때는 ー·〜·！？·・・・·‥ 가 전부 맞는 표기다 — 하나도 바꾸지 않는다 (ja-JP·jpn 같은 꼴도 같다)
        if (LangCode.IsJapanese(targetLang)) return tr;
        if (LangCode.IsChinese(targetLang)) return PolishChinese(tr);
        // 여기부터는 한국어·영어(와 그 밖의 언어) — 예전 그대로다
        tr = tr.Replace('ー', '~').Replace('〜', '~').Replace('～', '~');
        tr = tr.Replace('！', '!').Replace('？', '?');
        // 가운뎃점 말줄임은 가로 식자에서 점이 띄엄띄엄 그려진다 (실측 06p). 셋 이상 이어진 것은 남김없이 개수만큼 마침표로 바꾼다 —
        // 세 개씩만 바꾸던 때는 "・・・・"에서 하나가 남았다. "..."로 모으지 않는 것은 여섯 점이 예전부터 "......"(맞춤법의 아래 여섯 점 줄임표)로
        // 바뀌어 왔기 때문이다. 둘 이하는 예전처럼 그대로 둔다(하나는 '빨강・파랑' 같은 열거일 수 있다).
        return MiddleDotRun.Replace(tr, m => new string('.', m.Length)).Replace("‥", "..");
    }

    private static readonly Regex MiddleDotRun = new("・{3,}", RegexOptions.Compiled);

    /// <summary>중국어 번역문의 뒷손질. 중국어 조판(GB/T 15834)은 전각 문장부호가 표준이라, 한국어·영어용 바꾸기를 그대로 쓰면
    /// 맞는 표기를 틀린 표기로 바꾼다. <see cref="PolishTranslation"/>의 바꾸기를 하나씩 따져 정한 것:
    /// <list type="bullet">
    /// <item>ー(일본어 장음 기호) → ～ : 중국어 글자가 아니라서 남기면 안 되고, 늘인 소리는 전각 물결표로 쓴다(반각 ~가 아니다).</item>
    /// <item>〜(U+301C, 일본어 물결표) → ～(U+FF5E) : 모양은 같지만 중국어 글꼴·GBK가 쓰는 것은 U+FF5E다 — 그쪽으로 모은다.</item>
    /// <item>～(U+FF5E) : 이미 맞는 표기 — 그대로.</item>
    /// <item>~(반각 물결표) → ～ : LLM 프롬프트가 늘인 소리를 반각 ~로 쓰라고 해서(LlmTranslator) 번역문에는 ~가 오는데, 위에서 ー·〜는 ～로 바꾸므로
    /// 그대로 두면 한 쪽에 "啊~"와 "啊～"가 섞였다. 늘인 소리 표시는 ～ 하나로 모은다. 다만 늘인 소리가 아닌 것이 분명한 ~는 그대로 둔다(<see cref="KeepsHalfWidthTilde"/>):
    /// 하나짜리 ~ 바로 뒤가 숫자이거나(범위 1~2, 어림 ~10), 앞뒤가 모두 로마자·숫자인 것(A~Z). ~~처럼 이어진 것과 그 밖의 자리(글 끝, 한자·문장부호·공백 앞)는 전부 ～.</item>
    /// <item>！？ : 표준 문장부호 — 그대로 (v1.8.1).</item>
    /// <item>・・・(가운뎃점 셋 이상) → …… : 중국어 줄임표는 여섯 점(U+2026 둘)이다. 마침표 "..."는 중국어 표기가 아니다.
    /// 여섯 개까지는 줄임표 하나(……), 그보다 길면 여섯 개마다 하나씩 더(열두 점 …………까지가 표준의 긴 줄임표). 둘 이하는 한국어처럼 그대로.</item>
    /// <item>‥(두 점 줄임표) → … : 중국어에 없는 부호다 — 같은 계열의 줄임표로(‥‥ → ……).</item>
    /// </list>
    /// 이 함수가 원래 손대지 않는 전각 부호(，。：；（）「」『』、 전각 공백)는 중국어에서 전부 맞는 표기이고, 여기서도 손대지 않는다.
    /// 식자(TextRenderer)는 목표 언어를 보지 않고 늘 가로로 그리므로 ～·……가 세로쓰기 꼴로 눕는 일은 없다.</summary>
    private static string PolishChinese(string tr)
    {
        tr = tr.Replace('ー', '～').Replace('〜', '～');
        var text = tr;
        tr = HalfWidthTildeRun.Replace(tr, m => KeepsHalfWidthTilde(text, m.Index, m.Length) ? m.Value : new string('～', m.Length));
        return MiddleDotRun.Replace(tr, m => new string('…', 2 * ((m.Length + 5) / 6))).Replace('‥', '…');
    }

    private static readonly Regex HalfWidthTildeRun = new("~+", RegexOptions.Compiled);

    /// <summary>중국어 번역문의 반각 ~(index에서 length개 이어진 것)가 늘인 소리 표시가 아니라 범위·어림 표시인가 — 그러면 ～로 바꾸지 않는다.
    /// 하나짜리이고, 바로 뒤가 숫자이거나(1~2·~10·约~10 — 전각 숫자 포함) 바로 앞뒤가 모두 로마자·숫자일 때(A~Z)만 그렇다.
    /// 띄어 쓴 "1 ~ 2"는 ～가 된다 — 중국어 조판에서는 범위도 ～(浪纹线)로 쓰므로 틀린 표기는 아니다.</summary>
    private static bool KeepsHalfWidthTilde(string text, int index, int length)
    {
        if (length != 1 || index + 1 >= text.Length) return false;
        char next = text[index + 1];
        if (char.IsDigit(next)) return true;
        return char.IsAsciiLetter(next) && index > 0 && (char.IsAsciiLetter(text[index - 1]) || char.IsDigit(text[index - 1]));
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
        // 어느 언어인지는 뒷손질(PolishTranslation)과 같은 판정을 쓴다 — 예전에는 여기만 "zh"·"zh-cn"·"zh-tw" 셋만 받아
        // zh-Hant·zh_CN 은 뒷손질에서는 중국어, 여기서는 '그 밖의 언어'였다
        var lang = LangCode.Primary(sourceLang);
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
                "zh" => han,
                "ko" => hangul || han,
                _ => char.IsLetter(ch),
            };
            if (hit) hits++;
        }
        return hits > 0 && hits * 2 >= letters;
    }
}
