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
    bool Added = false,            // 결과 창 [새 글 넣기]로 넣은 블록 — 원문이 없다. 지우면 숨기지 않고 목록에서 뺀다
    bool Sfx = false)              // 풍선 밖 효과음(2026-11-26 시험 기능, SfxMode=Replace): 원문을 지우고 그 자리에 굵게 덮는다 — 풍선을 재지 않고 자리에 맞춘 크기, 세로면 글자를 쌓는다(TextRenderer.SfxStyle). 옛 json에는 없다 → false
{
    /// <summary>결과 창 식자 탭이 번역 글 위에 보여 주는 원문(OCR 글) — 앞뒤 빈칸·줄 바꿈을 뗀다(2026-11-26 사용자 요청).
    /// 빈 글이면 창이 칸을 접는다: [새 글 넣기]로 만든 글은 원문이 ""이고, 옛 json에 칸이 없으면 null로 들어온다.</summary>
    public string ShownSource() => SourceText?.Trim() ?? "";
}

/// <param name="RegenMaskPath">글자 지운 자리 가운데 SDXL로 다시 그릴 곳의 마스크(쪽 크기 PNG, 255 = 대상) — 설정(TextRegen)이 꺼져 있거나 대상이 없으면 null. 러너(PipelineRunner.TextRegenFileAsync)가 읽는다.</param>
/// <param name="RegenBlocks">그 마스크에 든 블록 수 — 진행 줄에 보인다(대상 고르기가 몇 개나 SDXL로 보냈는가).</param>
public record PageTranslation(string InpaintedPath, IReadOnlyList<TextBlockInfo> Blocks, string? RegenMaskPath = null, int RegenBlocks = 0);

/// <summary>감지한 글 블록의 역할(2026-11-26): 대사 | 풍선 밖 효과음(지우고 번역으로 덮는다 — 시험 중) | 뺀다(풍선 밖 효과음을 그대로 두는 옛 동작).</summary>
public enum BlockRole { Dialogue, Sfx, Drop }

/// <summary><see cref="EmbeddedTranslator.EraseBlock"/>이 블록을 지우는 방식 — 단색으로 칠했다 / 둘레의 무늬로 메울 자리에 넣었다(TextureFill) / LaMa 마스크에 넣었다.</summary>
internal enum EraseKind { Solid, Texture, Lama }

/// <summary>내장 번역 파이프라인: 감지(CTD) → 블록 나누기·합치기·효과음 가르기 → OCR(manga-ocr) → 번역 → 글자 지우기(단색 풍선은 획만 칠하기 / 무늬 있는 바탕은 둘레의 무늬로 메우기 / 나머지는 LaMa).
/// BallonsTranslator 없이 전부 프로세스 안(ONNX Runtime + OpenCV)에서 돈다. 켜 두면 그림 위의 글자 자리는 러너가 뒤에 SDXL로 다시 그린다(TextRegen — 이 클래스는 대상 마스크만 남긴다).</summary>
public class EmbeddedTranslator(AppSettings settings, OnnxEnv env)
{
    private readonly TextDetector _detector = new(env);
    private readonly MangaOcr _ocr = new(env);
    private readonly LamaInpainter _lama = new(env, unfusedOnGpu: true);   // 글자 지우기의 LaMa — GPU에서는 그래프 융합 없이(융합하면 GPU가 멈춘다)

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
        // 대사 블록에 붙어 잡힌 손글씨 효과음의 획은 따로 뗀다(2026-11-26 사용자 "이런 부분은 효과음이야") — 떼지 않으면 대사를 지울 때 같이 지워진다
        detBlocks = await Task.Run(() => BubbleSplitter.SplitOffGiants(img, textMask, detBlocks), ct);
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

        // ── 풍선 밖 효과음 가르기 (TranslateBubbleOnly, 2026-11-18 사용자 "번역은 말풍선 안에 있는 것만" / SfxMode, 2026-11-26 시험 기능 — CarrotMangaTranslator 참고) ──
        // 풍선 밖으로 보이고(BubbleSolver.IsInBubble — 닫힌 테두리도 단색 바탕도 없다) 글이 효과음처럼 생긴(LooksLikeSfx) 블록은 대사가 아니다(Decide):
        // SfxMode=Keep이면 번역기에 보내지 않고 지우지도 식자하지도 않는다(그런 글은 번역기가 폭주하기 쉽고 식자해도 그림을 가린다),
        // Replace면 용어집·내장 사전·번역기로 짧게 옮겨 원문을 지우고 그 자리에 굵게 덮는다(TextBlockInfo.Sfx).
        // 풍선 판정만으로 거르지 않는 까닭: 실제 원고 448블록에서 테두리 규칙은 65%만 풍선으로 봐(이 작가는 테두리가 닫히지 않는 풍선이 많다) 대사가 빠졌다 — 두 조건을 같이 건다.
        var roles = new List<BlockRole>(blocks.Count);
        int outsideBubble = 0, sfxCount = 0;
        // 지우지 않고 그대로 둘 블록의 자리 — 그 획이 옆 블록의 지우기 창에 걸려 같이 지워지지 않게 뒤에서 지울 마스크에서 뺀다(ProtectStrokes).
        // 갈라낸 손글씨 획인데 읽히지 않아(OCR이 비었거나 원문 글자가 없어) 여기까지 못 온 것도 그대로 둘 것이다
        var keptAsIs = detBlocks.Where(d => d.HandLettered && !blocks.Any(k => ReferenceEquals(k.Box, d)))
            .Select(d => new Rect(d.X1, d.Y1, d.X2 - d.X1, d.Y2 - d.Y1)).ToList();
        {
            var kept = new List<(DetectedBlock Box, string Text)>();
            foreach (var (b, text) in blocks)
            {
                var rect = new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1);
                // 효과음 꼴 = 글이 그렇게 생겼거나(LooksLikeSfx), 대사 블록에서 갈라낸 손글씨 획(HandLettered — OCR이 무엇으로 읽었든)
                bool looks = settings.TranslateBubbleOnly && (LooksLikeSfx(text) || b.HandLettered);
                bool inBubble = !looks || await Task.Run(() => BubbleSolver.IsInBubble(img, textMask, rect), ct);   // 그림 분석은 효과음 꼴일 때만
                var role = Decide(looks, inBubble, settings);
                if (role == BlockRole.Drop) { outsideBubble++; keptAsIs.Add(rect); continue; }
                kept.Add((b, text));
                roles.Add(role);
                if (role == BlockRole.Sfx) sfxCount++;
            }
            blocks = kept;
            // 거른 수를 진행 줄에 남긴다 — 다른 작가의 원고에서 대사가 빠지면 OCR 실패인지 이 거르기인지 가릴 길이 있어야 한다(검수 2026-11-19)
            if (outsideBubble > 0) status?.Report(Loc.F($"풍선 밖 효과음 {outsideBubble}개는 번역하지 않는다냥"));
            if (sfxCount > 0) status?.Report(Loc.F($"효과음 {sfxCount}개는 지우고 번역으로 덮는다냥 (시험 중)"));
        }
        Lap($"풍선 밖 글 거르기({outsideBubble}블록 제외, 효과음 {sfxCount})");   // loc:none

        // ── 번역 (배치) ──
        // 번역 요청은 띄워 두고 글자 지우기를 바로 시작한다 — 지우기는 번역문이 필요 없는데, 전에는 쪽마다 번역을 기다린 뒤에야 지웠다(실측 2026-11-27: DeepSeek 1.6~5.8초).
        // 받는 것은 지우기·메우기·LaMa가 끝난 뒤다. 효과음(시험 중)이 섞인 쪽만 전처럼 먼저 받는다(OverlapsTranslation). 번역이 실패하면 전처럼 그 쪽이 실패한다(저장 전에 받는다).
        IReadOnlyList<string> translations = Array.Empty<string>();
        string engine = TranslatorFactory.EngineLabel(settings);
        async Task<IReadOnlyList<string>> WaitShowingSeconds(Task<IReadOnlyList<string>> request, DateTime started)
        {
            // 느린 LLM 엔진은 한 페이지에 1~2분씩 걸린다 — 멈춘 게 아니라는 걸 알 수 있게 경과 초를 띄운다
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
            try { return await request; }
            finally { tick.Cancel(); await ticker; }
        }
        Task<IReadOnlyList<string>>? pending = null;
        var requested = DateTime.UtcNow;
        var requestClock = System.Diagnostics.Stopwatch.StartNew();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRequest = new CancelOnExit(requestCts);   // 지우다 예외·취소로 나가면 떠 있는 요청도 거둔다(끝난 요청에는 아무 일도 없다)
        if (blocks.Count > 0)
        {
            status?.Report(Loc.F($"번역 중이다냥 ({blocks.Count}개 말풍선 · {engine})"));
            var translator = TranslatorOverride ?? TranslatorFactory.Create(settings);
            var request = TranslateBlocksAsync(translator, blocks, roles, Glossary.Parse(settings.TranslateGlossary), requestCts.Token);
            _ = request.ContinueWith(_ => requestClock.Stop(), TaskContinuationOptions.ExecuteSynchronously);
            if (OverlapsTranslation(roles))
            {
                pending = request;
                // 지우다 실패해 이 요청을 기다리지 않고 나가면, 요청의 예외는 아무도 받지 않는다 — '관찰되지 않은 예외'로 남지 않게 본 것으로만 해 둔다(나가는 것은 먼저 난 예외다)
                _ = request.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            else translations = await WaitShowingSeconds(request, requested);
        }
        if (pending == null) Lap($"번역({blocks.Count}개·{engine})");   // loc:none

        // ── 글자 색 추출 + 글자 지우기 ──
        status?.Report(Loc.T("글자 지우는 중이다냥"));
        var result = new List<TextBlockInfo>();
        var lettered = new List<(int Index, Rect Rect, string Color, bool Sfx)>();   // 지운 블록 — 번역을 받은 뒤 식자 정보로 만든다
        using var lamaMask = new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black);
        // 지울 블록 = 번역해 식자할 블록. 검증에 걸려 못 옮긴 효과음("")은 지우지도 덮지도 않는다 — 설명문이나 원문 글자가 굵게 덮이는 것보다 원문 그대로가 낫다(검수 2026-11-26)
        bool WillErase(int i) => !(roles[i] == BlockRole.Sfx && (i >= translations.Count || string.IsNullOrWhiteSpace(translations[i])));
        var erasedRects = new List<Rect>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var r = new Rect(blocks[i].Box.X1, blocks[i].Box.Y1, blocks[i].Box.X2 - blocks[i].Box.X1, blocks[i].Box.Y2 - blocks[i].Box.Y1);
            if (WillErase(i)) erasedRects.Add(r); else keptAsIs.Add(r);
        }
        // 지울 블록마다 '제 글'의 상자 — 감지기 상자 둘 이상을 합친 블록은 감싼 상자가 아니라 그 상자들이다(엇갈린 기둥을 합치면 감싼 상자에 빈 모서리가 생긴다)
        var detected = det.Blocks.Select(d => new Rect(d.X1, d.Y1, d.X2 - d.X1, d.Y2 - d.Y1)).ToList();
        var erasedParts = erasedRects.Select(r => OwnBoxes(r, detected)).ToList();
        // 지울 글의 획 = 감지기 마스크에서 그대로 둘 블록(풍선 밖 효과음·못 옮긴 효과음·갈라낸 손글씨 획)의 획과 어느 지울 블록의 것도 아닌 획(상자로 안 잡힌 효과음)을 빼고,
        // 지울 블록에서 감지기가 놓친 획을 더한 것(PrepareStrokes) —
        // 단색 판정·흰 테·메우기·LaMa 마스크가 전부 이것을 본다. 빼지 않으면 그 획이 옆 블록의 창(1.15배)에 걸린 만큼 같이 지워지고, 풍선 옆의 효과음 획 때문에 단색 풍선도 LaMa로 넘어간다.
        // 글자 색은 원래 마스크(textMask)로 잰다
        using var strokes = await Task.Run(() => PrepareStrokes(img, textMask, keptAsIs, erasedRects, erasedParts), ct);
        // 지울 마스크 = 지울 글의 획 + 그림·톤 위 글의 흰 테(フチ) — 테가 마스크 밖에 남으면 허연 글자 모양 얼룩이 된다(사용자 보고 2026-11-26, 05·13쪽)
        using var eraseMask = strokes.Clone();
        // 무늬 있는 바탕(톤·줄무늬·속도선) 위의 글자 자리는 둘레의 무늬로 메운다(TextureFill) — LaMa는 그런 자리를 회색으로 뭉갠다. 끄면(EraseTextureFill) 전처럼 전부 LaMa
        using var textureMask = settings.EraseTextureFill ? new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black) : null;
        using var textureJobs = new OwnedMasks();   // 블록마다 창과 제 구멍
        // SDXL로 다시 그릴 자리(TextRegen, 2026-11-26): 단색으로 칠하지 못한 블록 가운데 구멍 둘레에 그림·무늬가 있는 것만 모은다(BubbleSolver.RegenStats — 흰 풍선 안은 LaMa 그대로).
        // 무늬로 메운 블록(TextureFill)도 같은 판정이라 여기에 든다 — 켜 두면 메운 자리 위에 SDXL이 다시 그린다(되풀이 무늬를 격자로 정확히 이은 자리도. 빼는 것은 사용자가 결과를 보고 정할 일).
        // 설정이 꺼져 있으면 재지도 쓰지도 않는다. NEKO_TEXTREGEN_DBG=<파일>이면 블록별 수치만 적는다(문턱을 실제 원고로 맞출 때)
        using var regenMask = settings.TextRegen ? new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black) : null;
        string? regenDbg = Environment.GetEnvironmentVariable("NEKO_TEXTREGEN_DBG") is { Length: > 0 } rd ? rd : null;
        // 이 쪽에서 감지한 블록 전부의 자리(번역하지 않고 둔 효과음·못 읽은 블록 포함) — 다시 그릴 구멍에 남의 글자가 섞이지 않게 EraseBlock이 본다
        var pageRects = detBlocks.Select(d => new Rect(d.X1, d.Y1, d.X2 - d.X1, d.Y2 - d.Y1)).ToList();
        bool needLama = false;
        int sfxSkipped = 0, regenBlocks = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            var (b, text) = blocks[i];
            var rect = new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1);
            bool sfx = roles[i] == BlockRole.Sfx;
            if (!WillErase(i)) { sfxSkipped++; continue; }

            string color = SampleTextColor(img, textMask, rect);
            var erased = await Task.Run(() => EraseBlock(img, strokes, eraseMask, rect, lamaMask, textureMask, regenMask, pageRects, textureJobs), ct);
            if (erased.Kind == EraseKind.Lama) needLama = true;
            if (erased.RegenWanted && regenMask != null) regenBlocks++;
            if (erased.Kind != EraseKind.Solid && regenDbg != null)
                try { File.AppendAllText(regenDbg, $"{Path.GetFileName(imagePath)}: [{b.X1},{b.Y1},{b.X2},{b.Y2}] white={erased.White:0.00} black={erased.Black:0.00} wanted={erased.RegenWanted} kind={erased.Kind}{Environment.NewLine}"); } catch { }   // loc:none

            lettered.Add((i, rect, color, sfx));
        }

        if (sfxSkipped > 0) status?.Report(Loc.F($"효과음 {sfxSkipped}개는 옮기지 못해 그대로 둔다냥"));
        Lap("글자 지우기(단색)");   // loc:none
        // 무늬로 메우기는 LaMa보다 먼저 — 아직 뭉개지지 않은 원래 무늬를 재료로 쓴다. 재료에서는 쪽의 글자 획 전부(그대로 둘 효과음 포함)를 뺀다
        if (textureJobs.Count > 0)
        {
            using var avoid = new Mat();
            Cv2.BitwiseOr(textMask, eraseMask, avoid);
            status?.Report(Loc.F($"배경 무늬 메우는 중이다냥 ({textureJobs.Count}곳)"));
            string tag = stem ?? Path.GetFileNameWithoutExtension(imagePath);
            var fill = await Task.Run(() => FillTextures(img, textureJobs, avoid, lamaMask, ct, tag, regenMask), ct);
            if (fill.HandedOver) needLama = true;
            regenBlocks = Math.Max(0, regenBlocks - fill.KeptOutOfRegen);
            Lap($"배경 무늬 메우기({textureJobs.Count})");   // loc:none
        }
        if (needLama)
        {
            status?.Report(Loc.T("배경 복원 중이다냥 (LaMa)"));
            await Task.Run(() => _lama.Inpaint(img, lamaMask), ct);
            Lap("배경 복원(LaMa)");   // loc:none
        }

        // 띄워 둔 번역을 받는다 — 지우는 동안 왔으면 바로, 아직이면 기다린다(경과 초를 띄운다)
        if (pending != null)
        {
            if (!pending.IsCompleted) status?.Report(Loc.F($"번역 중이다냥 ({blocks.Count}개 말풍선 · {engine})"));
            translations = await WaitShowingSeconds(pending, requested);
            laps.Add($"번역({blocks.Count}개·{engine}, 지우기와 나란히)={requestClock.ElapsedMilliseconds}ms");   // loc:none
            Lap("번역 기다림");   // loc:none
        }
        foreach (var (i, rect, color, sfx) in lettered)
        {
            var (b, text) = blocks[i];
            var tr = i < translations.Count ? translations[i] : "";
            if (string.IsNullOrWhiteSpace(tr)) tr = text;
            tr = TameRunaway(PolishTranslation(tr, settings.TranslateTarget), text);
            result.Add(new TextBlockInfo(
                b.X1, b.Y1, b.X2, b.Y2, text, tr,
                EstimateFontSize(rect, tr.Length),
                // 효과음은 Carrot처럼 높이가 폭의 1.5배를 넘으면 세로(글자를 쌓는다), 대사는 전처럼 1.3배(한국어는 어차피 가로로 그린다 — 풍선 재기에만 쓴다)
                Vertical: rect.Height > rect.Width * (sfx ? 1.5 : 1.3),
                color, Sfx: sfx));
        }
        // 앞 쪽 문맥(TranslateCarryContext): 이 쪽의 대사(원문→번역)를 다음 쪽의 LLM 번역에 참고로 넘긴다 — 최근 ContextLines줄만. 효과음은 넣지 않는다
        if (settings.TranslateCarryContext)
        {
            foreach (var r in result)
                if (!r.Sfx && r.Translation != r.SourceText) _recent.Add((r.SourceText, r.Translation));   // 못 옮겨 원문으로 둔 줄은 문맥에 넣지 않는다(LLM이 원문을 두라고 배운다 — 검수 2026-11-26)
            if (_recent.Count > ContextLines) _recent.RemoveRange(0, _recent.Count - ContextLines);
        }


        Directory.CreateDirectory(workDir);
        var outPath = Path.Combine(workDir, (stem ?? Path.GetFileNameWithoutExtension(imagePath)) + ".inpainted.png");
        ImgIo.Write(outPath, img);
        // 다시 그릴 자리가 있으면 마스크를 작업 폴더에 남긴다 — 러너(PipelineRunner.TextRegenFileAsync)가 읽는다. 못 쓰면 없는 것으로(LaMa 결과 그대로 간다)
        string? regenPath = null;
        if (regenMask != null && Cv2.CountNonZero(regenMask) > 0)
        {
            regenPath = Path.Combine(workDir, (stem ?? Path.GetFileNameWithoutExtension(imagePath)) + ".textregen.png");
            if (!ImgIo.TryWrite(regenPath, regenMask)) regenPath = null;   // Write는 파일을 못 쓰면 던진다 — 그러면 이 장이 실패로 끝난다(검토 2026-11-27)
        }
        Lap("저장");   // loc:none
        if (Environment.GetEnvironmentVariable("NEKO_TIMING") is { Length: > 0 } timing)
            try { File.AppendAllText(timing, Path.GetFileName(imagePath) + ": " + string.Join(", ", laps) + Environment.NewLine); } catch { }
        return new PageTranslation(outPath, result, regenPath, regenPath != null ? regenBlocks : 0);
    }

    /// <summary>앞 쪽의 대사(원문→번역) — 한 작업(러너가 쪽마다 같은 EmbeddedTranslator를 쓴다) 안에서 다음 쪽의 LLM 번역에 문맥으로 넘긴다(TranslateCarryContext).</summary>
    private readonly List<(string Source, string Translation)> _recent = new();

    /// <summary>번역기를 바꿔 끼우는 이음새(확인용, <see cref="Pipeline.PipelineRunner.DetectorOverride"/>와 같은 꼴) — 글자 지우기만 실제 원고로 확인할 때 가짜 번역기를 꽂는다
    /// (Google 비공식 끝점은 몇 장만 돌려도 429가 나 지우기 확인이 막혔다, 2026-11-26). null이면 설정대로 <see cref="TranslatorFactory.Create"/>.</summary>
    internal ITranslator? TranslatorOverride { get; set; }

    /// <summary>번역 요청을 글자 지우기와 나란히 보내도 되는가 — 블록이 전부 대사일 때만. 효과음(시험 중, SfxMode=Replace)은 번역이 비면 지우지 않으므로 번역을 먼저 받아야 지울 블록이 정해진다.</summary>
    internal static bool OverlapsTranslation(IReadOnlyList<BlockRole> roles) => roles.Count > 0 && !roles.Contains(BlockRole.Sfx);

    /// <summary>나갈 때 취소한다 — using으로 걸어 둔다(이미 치운 것은 건드리지 않는다).</summary>
    private sealed class CancelOnExit(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() { try { cts.Cancel(); } catch (ObjectDisposedException) { } }   // 순서가 바뀌어 먼저 치워졌으면 취소할 것도 없다
    }
    /// <summary>문맥으로 넘기는 최근 대사 줄 수 — 한두 쪽 분량. 더 길면 프롬프트만 커지고 LLM이 옛 줄을 번역해 버리는 일이 는다.</summary>
    internal const int ContextLines = 30;

    /// <summary>블록의 역할(2026-11-26): 풍선 밖이면서 효과음 꼴일 때만 SfxMode를 따른다 — Replace면 지우고 덮을 효과음, Keep이면 전처럼 뺀다. 그 밖은 대사.
    /// TranslateBubbleOnly가 꺼져 있으면 가르지 않고 전부 대사다(옛 동작).</summary>
    internal static BlockRole Decide(bool looksSfx, bool inBubble, AppSettings s)
    {
        if (!s.TranslateBubbleOnly || !looksSfx || inBubble) return BlockRole.Dialogue;
        return s.SfxMode == "Replace" ? BlockRole.Sfx : BlockRole.Drop;
    }

    /// <summary>대사와 효과음을 따로 번역한다(2026-11-26). 대사: 용어집 + 앞 쪽 문맥. 효과음: 용어집(원문이 그대로 한 항목) → 내장 사전(SfxDictionary) → 번역기에 효과음 지시문으로.
    /// 돌려주는 목록은 blocks와 같은 순서·길이. 못 옮긴 줄은 ""(호출부가 원문으로 둔다).</summary>
    private async Task<IReadOnlyList<string>> TranslateBlocksAsync(ITranslator translator, IReadOnlyList<(DetectedBlock Box, string Text)> blocks,
        IReadOnlyList<BlockRole> roles, IReadOnlyList<(string Source, string Target)> glossary, CancellationToken ct)
    {
        var result = new string[blocks.Count];
        var dialogue = new List<int>();
        var pendingSfx = new List<int>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (roles[i] != BlockRole.Sfx) { dialogue.Add(i); continue; }
            var text = blocks[i].Text.Trim();
            var hit = glossary.FirstOrDefault(g => g.Source == text).Target ?? SfxDictionary.Lookup(text, settings.TranslateTarget);
            if (hit != null) result[i] = hit; else pendingSfx.Add(i);
        }
        if (dialogue.Count > 0)
        {
            var opts = new TranslateOptions(glossary, settings.TranslateCarryContext ? _recent.ToArray() : [], Sfx: false);
            var tr = await TranslateCheckedAsync(translator, dialogue.Select(i => blocks[i].Text).ToList(), opts, settings.TranslateSource, settings.TranslateTarget, ct);
            for (int k = 0; k < dialogue.Count; k++) result[dialogue[k]] = tr[k];
        }
        if (pendingSfx.Count > 0)
        {
            var tr = await TranslateCheckedAsync(translator, pendingSfx.Select(i => blocks[i].Text).ToList(), new TranslateOptions(glossary, [], Sfx: true), settings.TranslateSource, settings.TranslateTarget, ct);
            for (int k = 0; k < pendingSfx.Count; k++) result[pendingSfx[k]] = tr[k];
        }
        return result;
    }

    /// <summary>블록 하나의 글자 지우기 — 번역 쪽 루프(TranslatePageAsync)에서 블록마다의 판단만 떼어 낸 것(모델 없이 시험한다, 검수 2026-11-26).
    /// ① 단색 풍선이면 획을 칠하고 끝(Solid). **판정은 지울 글의 획 마스크(<paramref name="textMask"/>)로 먼저** 한다(v1.18.0과 같은 순서): 흰 테를 더해 불린 마스크로 판정하면
    ///    풍선 테두리에 닿아 윤곽이 마스크를 다 못 덮고, 단색으로 칠하던 풍선이 LaMa로 넘어간다.
    /// ② 아니면 그림·톤 위 글의 흰 테를 지울 마스크(<paramref name="eraseMask"/>)에 더하고(<see cref="BubbleSolver.TextHalo"/>) 구멍 둘레를 잰다(<see cref="BubbleSolver.RegenStats"/>).
    ///    둘레에 무늬·그림이 있고 <paramref name="textureMask"/>가 있으면(설정 EraseTextureFill) **이 블록의 획만** 거기에 넣는다(Texture — 호출부가 둘레의 무늬로 메운다):
    ///    다른 블록의 자리(<paramref name="pageBlocks"/> — 제 자리와 겹치는 부분은 제 것)는 뺀다. 옆 블록의 글자까지 구멍에 넣으면 그 블록의 깨끗한 풍선에 무늬가 그려진다.
    ///    둘레가 평평하거나(흰 종이·먹) 무늬 메우기를 껐으면 전처럼 블록 창(1.15배) 안의 획을 전부 LaMa 마스크에 모은다(Lama — 창에 걸친 옆 블록의 획까지 모으는 것은 전부터의 동작).
    /// ③ <paramref name="regenMask"/>가 있으면(설정 TextRegen) 둘레에 무늬·그림이 있는 블록의 획(같은 '제 획만')을 넣는다 — 러너가 SDXL로 다시 그린다.
    /// 구멍 = 블록의 1.15배 창 안의 지울 마스크 + 그 창 밖으로 나간 **제** 흰 테·갇힌 틈(<see cref="BubbleSolver.TextHalo"/> — 넓어진 자리의 남의 획은 모으지 않는다).
    /// <paramref name="textureJobs"/>를 주면 무늬로 메울 블록의 (창, 제 구멍)을 거기에 더한다(호출자가 해제) — 메우기는 쪽 마스크가 아니라 이 일감으로 한다(<see cref="FillTextures"/>).</summary>
    internal static (EraseKind Kind, bool RegenWanted, double White, double Black) EraseBlock(Mat img, Mat textMask, Mat eraseMask, Rect rect, Mat lamaMask, Mat? textureMask, Mat? regenMask,
        IReadOnlyList<Rect> pageBlocks, List<(Rect Area, Mat Own)>? textureJobs = null)
    {
        if (BubbleSolver.TrySolidFill(img, textMask, rect)) return (EraseKind.Solid, false, 0, 0);
        var window = BubbleSolver.Enlarge(rect, 1.15, img.Width, img.Height);
        // 흰 테·갇힌 틈(이 블록의 획에 두른 것)을 지울 마스크에 더한다. 테가 1.15배 창 밖으로 나가면 구멍을 모으는 자리(roi)를 거기까지 넓힌다 — 좁은 줄(폭 40px이면 창은 좌우 3px)에서
        // 테의 바깥쪽이 지울 마스크에는 들었는데(메울 재료에서 빠진다) 구멍에는 안 들어 글 양옆에 흰 띠가 남았다(검토 2026-11-27). 더한 것이 없는 블록(흰 풍선)은 전과 같은 창이다
        var roi = window;
        using var halo = BubbleSolver.TextHalo(img, textMask, rect, out var haloWindow, pageBlocks);
        if (halo != null)
        {
            using (var into = new Mat(eraseMask, haloWindow)) Cv2.BitwiseOr(into, halo, into);
            using var points = new Mat();
            Cv2.FindNonZero(halo, points);
            var reach = Cv2.BoundingRect(points);
            roi = (window | new Rect(reach.X + haloWindow.X, reach.Y + haloWindow.Y, reach.Width, reach.Height)) & new Rect(0, 0, img.Width, img.Height);
        }
        // 이 블록의 구멍 = 1.15배 창 안의 지울 마스크 + 창 밖으로 나간 제 흰 테. 넓어진 자리에 든 남의 획(옆 블록·상자로 안 잡힌 효과음)은 모으지 않는다
        using var src = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        using (var from = new Mat(eraseMask, window))
        using (var to = new Mat(src, new Rect(window.X - roi.X, window.Y - roi.Y, window.Width, window.Height)))
            from.CopyTo(to);
        if (halo != null)
        {
            var cut = haloWindow & roi;
            using var from = new Mat(halo, new Rect(cut.X - haloWindow.X, cut.Y - haloWindow.Y, cut.Width, cut.Height));
            using var to = new Mat(src, new Rect(cut.X - roi.X, cut.Y - roi.Y, cut.Width, cut.Height));
            Cv2.BitwiseOr(to, from, to);
        }
        // 구멍 둘레에 무늬·그림이 있는가 — 이 시점의 img는 아직 메우기 전이다(둘레를 원래 그림에서 잰다)
        var (wanted, white, black) = BubbleSolver.RegenStats(img, eraseMask, roi, src);
        bool texture = wanted && textureMask != null;
        if (!texture)
        {
            using var dst = new Mat(lamaMask, roi);
            Cv2.BitwiseOr(dst, src, dst);
        }
        if (wanted && (textureMask != null || regenMask != null))
        {
            // 이 블록의 획만: 창 안의 마스크에서 다른 블록의 자리를 뺀다(제 자리와 겹친 부분은 제 것)
            using var keep = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.White);
            foreach (var other in pageBlocks)
            {
                if (other == rect) continue;
                var cut = other & roi;
                if (cut.Width > 0 && cut.Height > 0) keep.Rectangle(new Rect(cut.X - roi.X, cut.Y - roi.Y, cut.Width, cut.Height), Scalar.Black, -1);
            }
            var mine = rect & roi;
            if (mine.Width > 0 && mine.Height > 0) keep.Rectangle(new Rect(mine.X - roi.X, mine.Y - roi.Y, mine.Width, mine.Height), Scalar.White, -1);
            Mat? own = new Mat();
            try
            {
                Cv2.BitwiseAnd(src, keep, own);
                if (textureMask != null) { using var textureDst = new Mat(textureMask, roi); Cv2.BitwiseOr(textureDst, own, textureDst); }
                if (regenMask != null) { using var regenDst = new Mat(regenMask, roi); Cv2.BitwiseOr(regenDst, own, regenDst); }
                // 메울 일감 = 이 블록의 창과 제 구멍 — 쪽 마스크를 창으로 다시 자르면 창에 걸친 옆 블록의 획이 섞인다(호출부가 해제)
                if (texture && textureJobs != null) { textureJobs.Add((roi, own)); own = null; }
            }
            finally { own?.Dispose(); }
        }
        return (texture ? EraseKind.Texture : EraseKind.Lama, wanted, white, black);
    }

    /// <summary>그대로 둘 블록(<paramref name="keptAsIs"/> — 풍선 밖 효과음·못 옮긴 효과음·갈라낸 손글씨 획)의 획을 지울 글의 획 마스크에서 비운다. 지울 블록(<paramref name="erased"/>)의 자리와 겹친 부분은 남긴다.
    /// 2026-11-26 사용자 "이런 부분은 효과음이야": 그대로 두기로 한 효과음도 획이 마스크에 남아 있으면 옆 블록의 지우기 창(1.15배)에 걸린 만큼 LaMa로 지워졌고,
    /// 풍선 옆의 효과음 획이 단색 판정의 창(1.7배)에 들어와 "윤곽이 마스크를 다 못 덮는다"며 깨끗한 흰 풍선을 LaMa로 넘겼다. 어느 블록에도 안 든 획은 건드리지 않는다.</summary>
    internal static void ProtectStrokes(Mat strokes, IReadOnlyList<Rect> keptAsIs, IReadOnlyList<Rect> erased)
    {
        if (keptAsIs.Count == 0) return;
        var bounds = new Rect(0, 0, strokes.Width, strokes.Height);
        using var clear = new Mat(strokes.Rows, strokes.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var r in keptAsIs) { var c = r & bounds; if (c.Width > 0 && c.Height > 0) clear.Rectangle(c, Scalar.White, -1); }
        foreach (var r in erased) { var c = r & bounds; if (c.Width > 0 && c.Height > 0) clear.Rectangle(c, Scalar.Black, -1); }
        strokes.SetTo(Scalar.Black, clear);
    }

    /// <summary>지울 블록 <paramref name="block"/>의 글이 든 상자들 — 감지기가 낸 상자(<paramref name="detected"/>) 가운데 넓이의 절반 넘게 이 블록 안에 든 것이 둘 이상이면 그 상자들, 아니면 블록 자신.
    /// 엇갈린 두 기둥을 합친 블록(<see cref="BubbleSplitter.MergeSiblings"/>)은 감싼 상자에 빈 모서리가 생기고, 거기 든 획(상자로 안 잡힌 손글씨 효과음)은 이 블록의 글이 아니다
    /// (2026-11-27 실제 05쪽 "ちゅ" — <see cref="BubbleSolver.DropUnownedStrokes"/>). 감지기 상자 하나에서 나온 블록(대부분)·그 상자를 풍선별로 나눈 조각·효과음을 떼고 남은 대사는 블록 자신이다.
    /// 풍선 나누기가 낸 조각들(Split)은 쓰지 않는다 — 글을 다 덮지 못한다(실제 쪽에서 조각 밖의 "っ"·"え"가 빠졌다).</summary>
    internal static IReadOnlyList<Rect> OwnBoxes(Rect block, IReadOnlyList<Rect> detected)
    {
        var inside = detected.Where(d => d.Width > 0 && d.Height > 0 && (long)(d & block).Width * (d & block).Height * 2 > (long)d.Width * d.Height).ToList();
        return inside.Count >= 2 ? inside : [block];
    }

    /// <summary>일감 목록 — 든 마스크를 같이 해제한다.</summary>
    private sealed class OwnedMasks : List<(Rect Area, Mat Own)>, IDisposable
    {
        public void Dispose() { foreach (var (_, own) in this) own.Dispose(); Clear(); }
    }

    /// <summary>지울 글의 획 마스크를 만든다(호출자가 해제): 감지기 마스크의 사본에서 ① 그대로 둘 블록의 획을 비우고(<see cref="ProtectStrokes"/>)
    /// ② 어느 지울 블록의 것도 아닌 획을 비우고(<see cref="BubbleSolver.DropUnownedStrokes"/> — 상자로 안 잡힌 효과음이 대사의 창에 걸린 만큼 지워지지 않게.
    /// <paramref name="parts"/> = 지울 블록마다 제 글의 상자들(<see cref="OwnBoxes"/>), 없으면 블록 자신) ③ 그 상자마다 감지기가 놓친 획을 더한다
    /// (<see cref="BubbleSolver.AddMissedStrokes"/> — 상자 안에 그림이 섞인 블록은 손대지 않는다). 단색 판정·흰 테·메우기·LaMa 마스크가 전부 이것을 본다. 감지기 마스크는 바꾸지 않는다(글자 색은 그것으로 잰다).
    /// ②③은 보너스다 — 실패해도 그 단계(③은 그 블록)만 건너뛴다(쪽 루프에서 떼어 낸 것, 검토 2026-11-27).</summary>
    internal static Mat PrepareStrokes(Mat img, Mat textMask, IReadOnlyList<Rect> keptAsIs, IReadOnlyList<Rect> erased, IReadOnlyList<IReadOnlyList<Rect>>? parts = null)
    {
        var strokes = textMask.Clone();
        try
        {
            ProtectStrokes(strokes, keptAsIs, erased);
            var boxes = parts ?? erased.Select(r => (IReadOnlyList<Rect>)[r]).ToList();
            try { BubbleSolver.DropUnownedStrokes(strokes, boxes); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            // 놓친 획도 제 글의 상자 안에서만 찾는다 — 합친 블록의 감싼 상자로 찾으면, 빈 모서리에서 방금 비운 효과음이 상자에 걸친 조각에 닿아 '덮이지 않은 글자'로 통째로 다시 들어온다(검토 2026-11-27)
            foreach (var box in boxes.SelectMany(own => own))
            {
                try { BubbleSolver.AddMissedStrokes(img, strokes, box); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            return strokes;
        }
        catch { strokes.Dispose(); throw; }
    }

    /// <summary>무늬 메우기를 돌리고(<see cref="TextureFill.FillAll"/>), 못 메운 일감(가져올 무늬가 모자람·구멍이 너무 큼·메우기 실패)의
    /// **제 구멍만** LaMa 마스크에 넘긴다 — 창에 걸쳐 들어온 옆 블록의 획은 그 블록의 일감이 맡는다. HandedOver = LaMa로 넘긴 것이 있는가.
    /// <paramref name="regenMask"/>(SDXL로 다시 그릴 자리 — 설정 TextRegen)를 주면 **되풀이 무늬를 격자만큼 옮겨 그대로 이은 일감**의 제 구멍을 거기서 뺀다(KeptOutOfRegen = 뺀 일감 수):
    /// 줄무늬·망점은 이미 둘레와 똑같이 이어졌고, SDXL은 거기에 점·얼룩을 얹거나 망점을 옅게 만들 뿐이었다(실제 26쪽 줄무늬 풍선 둘·13쪽 망점, 2026-11-27).
    /// 베껴 메운 자리(shift-map — 어긋난 조각이 남는다)와 LaMa로 넘긴 자리는 그대로 다시 그린다.</summary>
    internal static (bool HandedOver, int KeptOutOfRegen) FillTextures(Mat img, IReadOnlyList<(Rect Area, Mat Own)> jobs, Mat avoid, Mat lamaMask, CancellationToken ct = default, string? tag = null,
        Mat? regenMask = null)
    {
        var filled = TextureFill.FillAll(img, jobs, avoid, out var byLattice, ct, tag);
        bool handedOver = false;
        int kept = 0;
        for (int k = 0; k < jobs.Count; k++)
        {
            if (filled[k])
            {
                if (byLattice[k] && regenMask != null)
                {
                    using var regenDst = new Mat(regenMask, jobs[k].Area);
                    regenDst.SetTo(Scalar.Black, jobs[k].Own);
                    kept++;
                }
                continue;
            }
            using var dst = new Mat(lamaMask, jobs[k].Area);
            Cv2.BitwiseOr(dst, jobs[k].Own, dst);
            handedOver = true;
        }
        return (handedOver, kept);
    }

    /// <summary>번역하고 검증한다(TranslationCheck — Carrot처럼 목표 언어 글자가 없거나 원문 글자가 남거나 효과음이 길면 거부). 걸린 줄만 LLM에 한 번 더 묻고(Google·DeepL은 같은 답이 오므로 묻지 않는다),
    /// 그래도 걸리면 Google(<paramref name="fallback"/> — 시험은 가짜를 넣는다)로 메운다. 끝내 못 옮긴 줄: 대사는 처음 답 그대로(비면 "" — 호출부가 원문으로 둔다), 효과음은 ""(호출부가 지우지도 덮지도 않는다 — 검수 2026-11-26).</summary>
    internal static async Task<string[]> TranslateCheckedAsync(ITranslator translator, IReadOnlyList<string> texts, TranslateOptions opts, string sourceLang, string targetLang,
        CancellationToken ct, ITranslator? fallback = null)
    {
        var first = await translator.TranslateAsync(texts, sourceLang, targetLang, opts, ct);
        var result = texts.Select((_, i) => i < first.Count ? first[i] ?? "" : "").ToArray();
        bool Ok(int i, string tr) => TranslationCheck.IsAcceptable(PolishTranslation(tr, targetLang), targetLang, opts.Sfx, texts[i]);
        var bad = Enumerable.Range(0, texts.Count).Where(i => !Ok(i, result[i])).ToList();
        if (bad.Count > 0 && translator is LlmTranslator)
        {
            var again = await translator.TranslateAsync(bad.Select(i => texts[i]).ToList(), sourceLang, targetLang, opts, ct);
            for (int k = 0; k < bad.Count; k++)
                if (k < again.Count && Ok(bad[k], again[k] ?? "")) result[bad[k]] = again[k];
            bad = bad.Where(i => !Ok(i, result[i])).ToList();
        }
        if (bad.Count > 0 && translator is not GoogleTranslator)
        {
            try
            {
                var fb = await (fallback ?? new GoogleTranslator()).TranslateAsync(bad.Select(i => texts[i]).ToList(), sourceLang, targetLang, opts, ct);
                for (int k = 0; k < bad.Count; k++)
                    if (k < fb.Count && Ok(bad[k], fb[k] ?? "")) result[bad[k]] = fb[k];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }   // Google 오류 — 처음 답으로 둔다
            bad = bad.Where(i => !Ok(i, result[i])).ToList();
        }
        if (opts.Sfx) foreach (var i in bad) result[i] = "";
        return result;
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
