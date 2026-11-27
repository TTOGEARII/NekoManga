using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary><see cref="PipelineRunner.TextRegenAsync"/>의 결과 — 그린 창 수 / 전체 창 수 / 멈춘 까닭(없으면 null). 사용자 취소는 예외로 올라간다.</summary>
internal readonly record struct TextRegenResult(int Drawn, int Total, string? Error);

// 글자 지운 자리 다시 그리기(TextRegen, 2026-11-26) — 번역 단계(1차)에서 LaMa로 지운 자리 가운데 바탕에 그림이 있는 곳만 사용자의 ComfyUI SDXL로 다시 그린다.
// 맡은 일: 대상 마스크(EmbeddedTranslator가 남긴 .textregen.png)를 창으로 묶어(TextRegenPlan) 창마다 그림 하나를 올리고 인페인트 그래프(WorkflowBuilder.BuildTextRegen)를 돌려
// 제 구멍만 붙인다 · 빈 조각이면 새 시드로 다시 그린다(TextRegenCheck) · 시작 전 모델 파일 확인. 실패는 그 쪽의 경고로만 — 쪽은 LaMa 결과로 간다.
// 검열 재생성(PipelineRunner.Decensor.cs의 ComfyRegenDecensorAsync)과 코드를 나누지 않는다: 영역·태그·2단계·시드 기록이 글자 자리에는 없고, 설정도 따로다(TextRegen*).
public partial class PipelineRunner
{
    /// <summary>프롬프트 하나를 기다리는 한도 — 한 단계뿐이라 재생성(20분)의 절반. 시험이 줄여 쓴다.</summary>
    internal TimeSpan TextRegenTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>이 실행에서 글자 자리 그래프에 넣을 ControlNet 파일 이름 — 시작 전 확인(<see cref="CheckTextRegenModels"/>)이 설치된 이름으로 바꿔 잡았을 때만 값이 있다(null = 설정대로).</summary>
    private string? _textRegenControlNet;

    /// <summary>대상 마스크의 창마다 그려 **제 구멍과 그 둘레(<see cref="TextRegenPasteReach"/> — 그래프가 넓혀 다시 그린 데까지)만** <paramref name="img"/>에 붙인다. 창은 위에서 아래로 차례로 돈다(ComfyUI 큐 하나).
    /// 그린 자리가 둘레보다 눈에 띄게 희면(빈 조각) 첫 시드에서 끌어낸 시드로 설정 횟수(TextRegenWhiteRetries)까지 다시 그려 나은 것을 고른다 — 올린 그림은 그대로 쓰고 그래프만 다시 보낸다.
    /// 창 하나가 첫 시도에서 실패하면 거기서 멈추고(서버가 죽었으면 남은 창마다 한도까지 기다리게 된다) 그때까지 붙인 것은 둔다 — 까닭은 결과의 Error. 재시도의 실패는 첫 결과를 둔다.
    /// 사용자 취소만 던진다. 어느 경우든 올린 입력·출력은 ComfyUI 폴더에서 치운다.</summary>
    internal async Task<TextRegenResult> TextRegenAsync(ComfyClient client, string comfyOutputDir, Mat img, Mat regenMask, int pageIdx, CancellationToken ct, IProgress<string>? status = null,
        int blocks = 0)
    {
        var windows = TextRegenPlan.WindowsFor(regenMask, img.Size(), settings.TextRegenCropSize);
        bool mono = !TextRegenPlan.IsColorPage(img);   // 붙이기 전에 잰다 — 붙인 조각의 색이 판정에 섞이지 않게
        try
        {
            int retries = settings.TextRegenWhiteRetries;
            for (int k = 0; k < windows.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var (w, hole) = (windows[k].Window, windows[k].Hole);
                int reach = TextRegenPasteReach(settings.TextRegenInpaintGrow, w.Scale);   // 그래프의 노이즈 마스크(넓힘 + 블러 2)가 창에서 닿는 범위
                // 올릴 이름은 우리가 짓는다(서버는 overwrite=true라 그대로 쓴다) — 요청이 서버에 닿은 뒤 답을 못 받고 끝나도(멈춤·연결 끊김) 이 이름으로 치운다
                string upName = $"nm_textregen_{_tag}_{pageIdx}_{k}.png";
                // 블록 수(번역 단계가 센 것)를 같이 보인다 — 대상 고르기가 몇 개나 SDXL로 보냈는지가 이 단계의 비용이다
                status?.Report(blocks > 0
                    ? Loc.F($"글자 자리 다시 그리는 중이다냥 (블록 {blocks}개 · 창 {k + 1}/{windows.Count})")
                    : Loc.F($"글자 자리 다시 그리는 중이다냥 ({k + 1}/{windows.Count})"));
                string? up = null;
                Mat? best = null; List<(double Hole, double Rim)> bestStats = [];
                try
                {
                    using (var crop = Scaled(img, w, InterpolationFlags.Area))
                    using (var cropHole = hole.Size() == crop.Size() ? hole.Clone() : hole.Resize(crop.Size(), 0, 0, InterpolationFlags.Nearest))
                        up = await client.UploadImageAsync(MosaicDetector.ToMaskedPng(crop, cropHole), upName, ct);
                    long first = settings.TextRegenSeed >= 0 ? settings.TextRegenSeed : Random.Shared.NextInt64(0, uint.MaxValue);
                    for (int attempt = 0; attempt <= retries; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (attempt > 0) status?.Report(Loc.F($"글자 자리를 다시 그리는 중이다냥 ({k + 1}/{windows.Count}, 빈 조각 {TextRegenCheck.BlankCount(bestStats)}개 → 새 시드 {attempt}/{retries})"));
                        Mat back;
                        try { back = await RunTextRegenGraphAsync(client, comfyOutputDir, up, w, RegenWhiteCheck.RetrySeed(first, attempt), ct); }
                        catch (Exception ex) when (attempt > 0 && !ct.IsCancellationRequested)
                        {
                            // 다시 그리다 실패하면 첫 결과를 버리지 않는다(재생성과 같은 판단)
                            status?.Report(Loc.F($"글자 자리 다시 그리기를 멈췄다냥 ({k + 1}/{windows.Count}) — {ex.Message}"));
                            break;
                        }
                        List<(double Hole, double Rim)> stats;
                        try
                        {
                            using var before = new Mat(img, w.Window);   // 아직 붙이기 전 — 둘레는 원래 그림에서 잰다
                            stats = retries > 0 ? TextRegenCheck.PieceStats(back, before, hole, reach) : [];
                        }
                        catch { back.Dispose(); throw; }
                        if (best == null || TextRegenCheck.IsBetter(stats, bestStats)) { best?.Dispose(); best = back; bestStats = stats; }
                        else back.Dispose();
                        if (TextRegenCheck.BlankCount(bestStats) == 0) break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { best?.Dispose(); throw; }
                catch (Exception ex)
                {
                    best?.Dispose();
                    return new TextRegenResult(k, windows.Count, ex.Message);
                }
                finally { WorkFolders.RemoveComfyFiles(settings.ComfyRoot, null, null, up, upName); }
                using var bestOwner = best!;
                // 흑백 쪽에 SDXL이 색을 얹는 일이 있다(실제 26쪽: 빗금 위 대사 자리에 연두색 얼룩, 2026-11-27) — 붙이기 전에 밝기만 남긴다
                if (mono && bestOwner.Channels() == 3)
                {
                    using var gray = bestOwner.CvtColor(ColorConversionCodes.BGR2GRAY);
                    Cv2.CvtColor(gray, bestOwner, ColorConversionCodes.GRAY2BGR);
                }
                using var take = Grown(hole, reach);
                using var dst = new Mat(img, w.Window);
                bestOwner.CopyTo(dst, take);
            }
            return new TextRegenResult(windows.Count, windows.Count, null);
        }
        finally { foreach (var w in windows) w.Dispose(); }
    }

    /// <summary>다시 그린 것을 붙이는 범위(창의 px, 구멍 밖으로) = 그래프가 넓혀 그리는 폭 + 블러(<see cref="WorkflowBuilder.TextRegenMaskBlur"/>). 그래프의 넓힘(<paramref name="grow"/>)과 블러는 **보낸 그림**의 px라,
    /// 창을 줄여 보냈으면(<paramref name="scale"/> = 보낸 크기 ÷ 창 크기 &lt; 1 — 긴 변 640px 넘는 블록) 창에서는 그만큼 더 넓다. 창의 px로 grow + 2만 붙이면 다시 그린 띠의 바깥쪽이 잘려
    /// 딱딱한 가장자리가 남는다(검토 2026-11-27 — 처음에는 넓힘만 늘리고 블러 2는 창의 px로 더해, 붙이는 가장자리가 블러 띠 안에 떨어졌다).</summary>
    internal static int TextRegenPasteReach(int grow, double scale) => (int)Math.Ceiling((Math.Max(0, grow) + WorkflowBuilder.TextRegenMaskBlur) / Math.Clamp(scale, 0.05, 1.0));

    /// <summary>창을 잘라 보내는 크기로 맞춘 사본(호출자가 해제).</summary>
    private static Mat Scaled(Mat src, RegenWindow w, InterpolationFlags how)
    {
        using var roi = new Mat(src, w.Window);
        var send = new Size(w.SendW, w.SendH);
        return roi.Size() == send ? roi.Clone() : roi.Resize(send, 0, 0, how);
    }

    /// <summary>글자 자리 그래프를 한 번 돌려 창 크기의 결과를 돌려준다(없으면 던진다) — 출력 파일은 읽은 뒤 바로 치운다(올린 입력은 호출부가 시도를 다 마친 뒤).</summary>
    private async Task<Mat> RunTextRegenGraphAsync(ComfyClient client, string comfyOutputDir, string up, RegenWindow w, long seed, CancellationToken ct)
    {
        var outPrefix = NextOutputPrefix("textregenout");
        ComfyClient.ComfyResult? r = null;
        string? pid = null;
        Mat outImg;
        try
        {
            var graph = WorkflowBuilder.BuildTextRegen(settings, up, outPrefix, w.SendW, w.SendH, seed, _textRegenControlNet);
            pid = await client.QueuePromptAsync(graph, ct);
            r = await client.WaitAsync(pid, comfyOutputDir, TextRegenTimeout, ct);
            if (r.OutputFiles.Count == 0) throw new InvalidOperationException(Loc.T("글자 자리 다시 그리기의 결과 파일이 없다냥."));
            outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        }
        catch (Exception) when (pid != null && r == null && !ct.IsCancellationRequested)
        {
            // 기다리기만 그만두면(제한 시간·연결 오류) 프롬프트는 서버에서 끝까지 돌아, 아래에서 치운 뒤에 출력 파일을 남긴다(사용자의 ComfyUI 폴더) — 거둬 달라고 한다.
            // 사용자가 멈춘 경우는 WaitAsync가 이미 거뒀다. 되는 데까지만(CancelPromptAsync는 던지지 않는다)
            await client.CancelPromptAsync(pid);
            throw;
        }
        finally { WorkFolders.RemoveComfyFiles(settings.ComfyRoot, r?.OutputFiles, outPrefix); }
        using var outImgOwner = outImg;
        if (outImg.Empty()) throw new InvalidDataException(Loc.T("글자 자리 다시 그리기의 결과를 읽지 못했다냥."));
        return outImg.Size() == w.Window.Size ? outImg.Clone() : outImg.Resize(w.Window.Size, 0, 0, InterpolationFlags.Lanczos4);
    }

    /// <summary>1차가 부르는 길: 지운 그림(<paramref name="inpaintedPath"/>)과 대상 마스크를 읽어 다시 그리고 같은 경로에 쓴다 — 뒤 단계(검열·채색·식자)가 그 파일을 그대로 이어받는다.
    /// 돌려주는 값 = 그 장의 경고 글(없으면 null). **이 단계의 실패로 장을 실패시키지 않는다**: 못 읽음·크기 불일치·ComfyUI 오류 모두 LaMa 결과 그대로 두고 경고만. 사용자 취소만 던진다.
    /// 컬러 쪽은 건너뛰고 그 사실을 돌려준다 — 기본 프롬프트가 흑백 만화용이라 컬러 표지에 회색 조각이 생긴다. 일부 창만 그리고 멈췄으면 그린 데까지는 쓴다.</summary>
    internal async Task<string?> TextRegenFileAsync(ComfyClient client, string comfyOutputDir, string inpaintedPath, string maskPath, int pageIdx, CancellationToken ct, IProgress<string>? status = null,
        int blocks = 0)
    {
        if (_textRegenGaveUp) return null;   // 이어서 실패해 이 작업에서는 그만뒀다 — 알림은 그만둔 쪽에 한 번 남겼다
        try
        {
            // ImgIo.Read는 없는 파일에 던지고 깨진 파일에 빈 Mat을 준다 — 어느 쪽이든 이 단계만 건너뛴다
            using var img = ReadOrEmpty(inpaintedPath, ImreadModes.Color);
            using var mask = ReadOrEmpty(maskPath, ImreadModes.Grayscale);
            if (img.Empty() || mask.Empty()) return Loc.T("글자 자리 다시 그리기를 건너뛴다냥 — 지운 그림이나 대상 마스크를 읽지 못했다냥");
            if (mask.Size() != img.Size()) return Loc.T("글자 자리 다시 그리기를 건너뛴다냥 — 대상 마스크의 크기가 그림과 다르다냥");
            if (Cv2.CountNonZero(mask) == 0) return null;
            // 건너뛴 것은 그 장의 끝 줄에 남긴다(돌려주는 글) — 누렇게 바랜 흑백 스캔도 채도 때문에 여기로 오는데, 지나가는 진행 줄에만 적으면 켰는데 아무 일도 없는 까닭이 안 보인다
            if (TextRegenPlan.IsColorPage(img)) return Loc.T("컬러 쪽이라 글자 자리 다시 그리기는 건너뛴다냥");
            var r = await TextRegenAsync(client, comfyOutputDir, img, mask, pageIdx, ct, status, blocks);
            // 그린 데까지는 쓴다. 못 써도(읽기 전용·디스크 가득) 이 장은 지운 그림 그대로 간다 — ImgIo.Write는 그럴 때 던지므로 TryWrite
            bool saved = r.Drawn == 0 || ImgIo.TryWrite(inpaintedPath, img);
            if (r.Error != null) return TextRegenFailure(r, saved);
            _textRegenFailStreak = 0;
            return saved ? null : Loc.T("글자 자리를 다시 그린 그림을 쓰지 못했다냥 — LaMa 결과로 간다냥");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // 읽기·판정·쓰기에서 나는 그 밖의 예외(잘못된 경로, OpenCV의 메모리 부족)도 이 단계의 실패일 뿐이다 — 밖으로 나가면 그 장이 실패로 끝난다(검토 2026-11-27)
        catch (Exception ex) { return TextRegenFailure(new TextRegenResult(0, 0, ex.Message), saved: false); }
    }

    /// <summary>이어서 이만큼 실패하면 이 작업의 남은 쪽은 글자 자리 다시 그리기를 하지 않는다 — 프롬프트를 받기만 하고 끝내지 못하는 ComfyUI에서는 쪽마다 한도(<see cref="TextRegenTimeout"/> 10분)까지 기다려
    /// 긴 작업이 기어간다(검토 2026-11-27). 한 번 성공하면 다시 센다.</summary>
    internal const int TextRegenGiveUpAfter = 2;
    private int _textRegenFailStreak;
    private bool _textRegenGaveUp;

    /// <summary>실패한 쪽의 경고 글을 짓고 이어진 실패를 센다: 일부 창만 그렸으면 어디까지 했는지(그린 것이 저장됐을 때만 — 못 썼으면 파일은 LaMa 결과 그대로다),
    /// 이어서 <see cref="TextRegenGiveUpAfter"/>번째면 이 작업에서 그만둔다는 것을 **그 뒤에 잇는다**(그만두는 쪽도 창 몇 개는 그려 저장했을 수 있다 — 그 사실이 묻히지 않게).</summary>
    private string TextRegenFailure(TextRegenResult r, bool saved)
    {
        string message = r.Drawn > 0 && saved
            ? Loc.F($"글자 자리 다시 그리기를 {r.Drawn}/{r.Total}창까지만 했다냥 — 나머지 자리는 LaMa 결과다냥: {r.Error}")
            : Loc.F($"글자 자리 다시 그리기 실패다냥 — LaMa 결과로 간다냥: {r.Error}");
        if (++_textRegenFailStreak < TextRegenGiveUpAfter) return message;
        _textRegenGaveUp = true;
        return JoinWarning(message, Loc.F($"글자 자리 다시 그리기가 이어서 {TextRegenGiveUpAfter}번 실패해 이 작업의 남은 쪽은 하지 않는다냥"));
    }

    private static Mat ReadOrEmpty(string path, ImreadModes mode)
    {
        try { return ImgIo.Read(path, mode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Mat(); }
    }

    /// <summary>글자용 SDXL 체크포인트·ControlNet 파일이 ComfyUI models 폴더에 있는지 먼저 본다(<see cref="CheckRegenModels"/>와 같은 까닭 — 없으면 ComfyUI가 큐 단계에서 알아듣기 어려운 오류를 낸다).
    /// 글자용 칸이 비었으면 재생성의 모델을 본다(<see cref="AppSettings.TextRegenCheckpointInUse"/>). ControlNet은 강도가 0이면 그래프에 없으니 보지 않는다.
    /// promax 이름 대체(설정에는 사용자 워크플로의 이름, 파일은 앱 설치기의 이름)도 재생성과 같되 **설정에는 적지 않고 이 러너만 기억한다**(<see cref="_textRegenControlNet"/>):
    /// `--run`은 끝나며 설정을 저장한다 — 글자용 칸에 적으면 "비움 = 재생성의 ControlNet을 따른다"가 말없이 풀린다(검토 2026-11-26).</summary>
    internal void CheckTextRegenModels()
    {
        _textRegenControlNet = null;
        var models = Path.Combine(settings.ComfyRoot, "ComfyUI", "models");
        var ck = Path.Combine(models, "checkpoints", settings.TextRegenCheckpointInUse);
        if (!File.Exists(ck))
            throw new FileNotFoundException(Loc.F($"글자 자리 다시 그리기의 체크포인트가 없다냥: {ck}\n설정 → 번역 → 지운 자리 다시 그리기 → 고급 옵션의 체크포인트를 맞춰 줘냥."));
        if (settings.TextRegenCnStrength <= 0) return;
        var cnDir = Path.Combine(models, "controlnet");
        var cn = Path.Combine(cnDir, settings.TextRegenControlNetInUse);
        if (!File.Exists(cn) && settings.TextRegenControlNetInUse.Contains("promax", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(cnDir, settings.ColorControlNet)) && settings.ColorControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase))
        {
            _textRegenControlNet = settings.ColorControlNet;
            cn = Path.Combine(cnDir, _textRegenControlNet);
        }
        if (!File.Exists(cn))
            throw new FileNotFoundException(Loc.F($"글자 자리 다시 그리기의 ControlNet이 없다냥: {cn}\n설정 → 번역 → 지운 자리 다시 그리기 → 고급 옵션의 ControlNet을 맞춰 줘냥."));
    }
}
