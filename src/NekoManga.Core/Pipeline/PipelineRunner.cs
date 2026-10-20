using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

public record PageProgress(string File, string Stage, double Percent, bool Done = false, string? Error = null);

/// <summary>페이지 처리 파이프라인.
///
///   원고 → [번역: 내장 엔진이 글자 감지→OCR→번역→글자 지움]
///        → [검열제거: 검열 감지 → 기본(내장 LaMa 만화판) / 엔진 LaMa(ComfyUI)]
///        → [자동채색: ComfyUI SDXL]
///        → [식자: 번역문을 C#으로 렌더]
///        → 완성본
///
/// 번역·기본 검열제거는 전부 프로세스 안(ONNX)에서 돌고, 채색·엔진 LaMa만 ComfyUI가 필요하다.
/// (SDXL+LoRA 재생성('강력') 경로와 그 후임 DeepCreamPy 모드는 2026-10-19에 폐기 — CLAUDE.md 참조.)</summary>
public class PipelineRunner(AppSettings settings) : IDisposable
{
    private readonly OnnxEnv _onnx = new(settings);
    private string? _refImageName;   // IPAdapter용으로 업로드한 표지 (서버에 없으면 null)
    private readonly ComfyServer _server = new(settings);

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;

        var translator = settings.EnableTranslate ? new EmbeddedTranslator(settings, _onnx) : null;
        var lama = new LamaInpainter(_onnx);

        bool comfyLama = settings.EnableDecensor && settings.DecensorMode == "ComfyLama";
        bool comfyRegen = settings.EnableDecensor && settings.DecensorMode == "ComfyRegen";
        bool comfyCustom = settings.EnableDecensor && settings.DecensorMode == "ComfyCustom";
        bool colorCustom = settings.EnableAutoColor && !string.IsNullOrWhiteSpace(settings.ColorCustomWorkflow);
        bool needComfy = settings.EnableAutoColor || comfyLama || comfyRegen || comfyCustom;

        if (translator is { IsReady: false })
            throw new FileNotFoundException("번역 엔진 모델이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (settings.EnableDecensor && !comfyLama && !comfyRegen && !comfyCustom && !lama.IsReady)
            throw new FileNotFoundException("검열제거(기본) 모델(LaMa)이 아직 설치되지 않았어요 — 설정 → 구성요소에서 받아주세요.");
        if (needComfy && !settings.ComfyAvailable)
            throw new FileNotFoundException(
                (settings.EnableAutoColor ? "자동 채색" : comfyRegen ? "재생성 검열제거" : comfyCustom ? "커스텀 워크플로 검열제거" : "엔진 LaMa 검열제거") +
                " 기능은 ComfyUI가 필요해요 — 설정 → 구성요소에서 ComfyUI를 설치해 주세요.");
        if (comfyRegen) CheckRegenModels();
        // 커스텀 워크플로 파일은 시작 전에 한 번 읽어 규약(LoadImage/SaveImage)을 검사한다 — 페이지마다 같은 오류로 실패하지 않게
        JsonObject? customDecensor = comfyCustom ? CustomWorkflow.Load(settings.CustomDecensorWorkflow) : null;
        JsonObject? customColor = colorCustom ? CustomWorkflow.Load(settings.ColorCustomWorkflow) : null;

        ComfyClient? client = null;
        string comfyOutputDir = "";
        if (needComfy)
        {
            var status = new Progress<string>(m => Report(progress, files, m, 10));
            await _server.EnsureRunningAsync(status, ct);
            client = new ComfyClient(_server.BaseUrl);
            comfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");

            // 표지가 등록돼 있는데 색 태그를 아직 안 뽑았으면 여기서 한 번 뽑아 캐시한다
            if (settings.EnableAutoColor && settings.ColorRefTags.Trim().Length == 0
                && settings.ColorRefImage.Trim().Length > 0 && File.Exists(settings.ColorRefImage))
                try
                {
                    Report(progress, files, "표지에서 캐릭터 색 뽑는 중...", 12);
                    settings.ColorRefTags = await ColorRefHelper.ExtractAsync(settings, settings.ColorRefImage, null, ct);
                    settings.Save();
                }
                catch { /* 표지 분석 실패는 치명적이지 않다 — 참고 색 없이 진행 */ }

            // 표지 정밀 매칭(IPAdapter): 모델이 설치돼 있고 서버가 노드를 알고 있으면 표지를 올려 둔다
            if (settings.EnableAutoColor && settings.ColorRefImage.Trim().Length > 0
                && File.Exists(settings.ColorRefImage) && ColorRefHelper.IpAdapterAvailable(settings))
                try
                {
                    if (await client.HasNodeAsync("IPAdapter", ct))
                    {
                        var refBytes = await File.ReadAllBytesAsync(settings.ColorRefImage, ct);
                        _refImageName = await client.UploadImageAsync(refBytes, "nm_cover_ref.png", ct);
                    }
                    // 노드를 모르면 = 서버가 설치 전부터 떠 있던 것 — 이번 실행은 태그만으로 간다
                }
                catch { _refImageName = null; }
        }

        var workDir = Path.Combine(AppSettings.DataDir, "work",
            $"job-{DateTime.Now:yyyyMMdd-HHmmss}");

        // ── 페이지별 상태. 검토 흐름(2026-10-20, 사용자 요청 "감지 → 사용자가 마스크 추가 → 제거")이면
        //    1차: 모든 페이지 번역+감지 → 검토 창(ReviewMasks) → 2차: 제거+채색+식자. 아니면 페이지마다 1차·2차를 이어서 돈다.
        var pages = files.Select(f => new ReviewPage(f)).ToList();
        bool review = settings.EnableDecensor && settings.DecensorReview && ReviewMasks != null;

        // 1차: 번역 + 검열 감지 (마스크는 페이지가 들고 있다 — 검토 창이 편집한다)
        async Task Stage1(ReviewPage pg, int i)
        {
            var name = pg.Name;
            if (translator != null)
            {
                var status = new Progress<string>(m => progress.Report(new(name, m, 25)));
                var tr = await translator.TranslatePageAsync(pg.WorkPath, workDir, status, ct);
                pg.WorkPath = tr.InpaintedPath;
                pg.Blocks = tr.Blocks;
            }
            if (!settings.EnableDecensor) return;
            progress.Report(new(name, "검열 영역 찾는 중", 45));
            using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
            // 말풍선은 흰 막대로 오인되기 쉬우므로 감지된 글자 블록을 제외 영역으로 넘긴다.
            var exclude = pg.Blocks.Select(b =>
                new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();
            // 검열 추적: AI 모델(합성 학습 전용 모델)이 있으면 그게 1순위 — 흰/검/모자이크를 한 번에 찾는다. 없거나 못 찾으면 규칙 기반으로 폴백.
            // autoBubbles는 말풍선 필터를 켠 경우에만 — 필터가 꺼져 있으면 글자 감지는 검/모자이크의 글자 상자 빼기에만 쓰여 비용 대비 효과가 작다
            var mask = DetectCensorMask(img, exclude, out var kindUsed, autoBubbles: settings.DecensorBubbleFilter);
            pg.Summary = kindUsed;
            if (settings.DecensorTarget == "Auto")
                progress.Report(new(name, $"검열 분석: {kindUsed}", 46));
            // 검토 흐름에서는 감지가 비었어도 사용자가 칠할 수 있게 빈 마스크를 준다
            pg.Mask = mask ?? (review ? new Mat(img.Rows, img.Cols, MatType.CV_8UC1, Scalar.Black) : null);
            if (review && settings.CollectCorrections) pg.AutoMask = pg.Mask?.Clone();   // 수정 전 사본 — 학습 재료의 '자동 감지' 쪽
            if (review) progress.Report(new(name, mask == null ? "검토 대기 (감지 없음)" : "검토 대기", 48));
        }

        // 2차: 검열 제거 + 채색 + 식자/저장
        async Task Stage2(ReviewPage pg, int i)
        {
            var name = pg.Name; var file = pg.File; var work = pg.WorkPath; var blocks = pg.Blocks;
            if (settings.EnableDecensor && pg.Mask != null && !pg.Skip && Cv2.CountNonZero(pg.Mask) > 0)
            {
                using var img = ImgIo.Read(work, ImreadModes.Color);
                var mask = pg.Mask;
                if (mask.Size() != img.Size())
                {
                    var rs = new Mat(); Cv2.Resize(mask, rs, img.Size(), 0, 0, InterpolationFlags.Nearest); mask = rs;
                }
                if (comfyCustom)
                {
                    progress.Report(new(name, "검열 지우는 중 (커스텀 워크플로·GPU)", 50));
                    await ComfyCustomDecensorAsync(client!, comfyOutputDir, customDecensor!, img, mask, i, ct);
                }
                else if (comfyRegen)
                {
                    progress.Report(new(name, "검열 자리 재생성 중 (SDXL·GPU)", 50));
                    await ComfyRegenDecensorAsync(client!, comfyOutputDir, img, mask, i, ct);
                }
                else if (comfyLama)
                {
                    progress.Report(new(name, "검열 지우는 중 (엔진 LaMa·GPU)", 50));
                    await ComfyLamaDecensorAsync(client!, comfyOutputDir, img, mask, i, ct);
                }
                else
                {
                    progress.Report(new(name, "검열 지우는 중 (기본·LaMa)", 50));
                    await Task.Run(() => lama.Inpaint(img, mask), ct);
                }
                if (!ReferenceEquals(mask, pg.Mask)) mask.Dispose();
                work = Path.Combine(workDir, Path.GetFileNameWithoutExtension(file) + ".decensored.png");
                Directory.CreateDirectory(workDir);
                ImgIo.Write(work, img);
            }

            // ── 자동채색 (ComfyUI) ──
            if (settings.EnableAutoColor && client != null)
            {
                progress.Report(new(name, customColor != null ? "채색 중 (커스텀 워크플로)" : "채색 중 (페이지당 1~2분)", 60));
                var png = await File.ReadAllBytesAsync(work, ct);
                var up = await client.UploadImageAsync(png, $"nm_cl_{i}.png", ct);
                var pid = await client.QueuePromptAsync(customColor != null
                    ? CustomWorkflow.Prepare(customColor, up, $"nm_color_{i}").Prompt
                    : WorkflowBuilder.BuildAutoColor(settings, up, $"nm_color_{i}", _refImageName), ct);
                var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
                if (r.OutputFiles.Count == 0)
                    throw new InvalidOperationException("채색 결과 파일이 없어요.");
                work = r.OutputFiles[^1];
            }

            // ── 식자(번역문 렌더) 및 최종 저장 ──
            var outPath = OutputPathFor(file);
            if (blocks.Count > 0)
            {
                progress.Report(new(name, "식자 중", 92));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                // 식자 전 원고와 블록을 남겨 둔다 — 결과 보기 창의 식자 탭이
                // 글꼴·색·번역문을 바꿔 다시 식자할 때 쓴다 (없으면 번역부터 다시 돌려야 한다).
                using (var baseImg = ImgIo.Read(work))
                using (var srcImg = ImgIo.Read(file))
                    if (!baseImg.Empty() && !srcImg.Empty())
                    {
                        ImgIo.Write(LetterSidecar.BasePathFor(outPath), baseImg);
                        new LetterSidecar(file, srcImg.Width, srcImg.Height,
                            LetterSidecar.BasePathFor(outPath), [.. blocks]).Save(outPath);
                    }

                TextRenderer.RenderBlocks(work, file, blocks, outPath, settings);
            }
            else if (work == file)
            {
                // 아무것도 감지·수정되지 않았다 (work가 원본 그대로) — 다시 인코딩하지 않고
                // 원본을 확장자까지 그대로 내보낸다. 손댈 게 없는데 jpg를 png로 재인코딩하면
                // 파일만 커지고 화질에도 득이 없다.
                var asIs = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(asIs)!);
                if (!string.Equals(asIs, file, StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, asIs, overwrite: true);
                progress.Report(new(name, "검열 없음 — 원본 그대로", 100, Done: true));
                return;
            }
            else
            {
                // 결과는 항상 png로 저장한다 — 원본이 webp/avif여도 확장자만 png가 되지 않도록
                // 그대로 복사하지 않고 OpenCV로 다시 인코딩한다.
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var done = ImgIo.Read(work, ImreadModes.Color);
                if (done.Empty())
                    throw new InvalidDataException($"결과 이미지를 읽지 못했어요: {Path.GetFileName(work)}");
                ImgIo.Write(outPath, done);
            }

            progress.Report(new(name, "완료", 100, Done: true));
        }

        try
        {
            if (review)
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Stage1(pages[i], i); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { pages[i].Error = ex.Message; progress.Report(new(pages[i].Name, "실패", 100, Done: true, Error: ex.Message)); }
                }
                var toReview = pages.Where(p => p.Error == null && p.Mask != null).ToList();
                if (toReview.Count > 0)
                {
                    bool go = await ReviewMasks!(toReview, ct);
                    if (!go) throw new OperationCanceledException("사용자가 검토 창에서 취소했어요.");
                    if (settings.CollectCorrections)
                        try { SaveCorrections(toReview); }
                        catch (Exception ex) { progress.Report(new(toReview[0].Name, $"학습 재료 저장 실패: {ex.Message}", 48)); }
                }
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pages[i].Error != null) continue;
                    try { await Stage2(pages[i], i); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { progress.Report(new(pages[i].Name, "실패", 100, Done: true, Error: ex.Message)); }
                }
            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Stage1(pages[i], i); await Stage2(pages[i], i); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { progress.Report(new(pages[i].Name, "실패", 100, Done: true, Error: ex.Message)); }
                }
            }
        }
        finally
        {
            foreach (var pg in pages) { pg.Mask?.Dispose(); pg.AutoMask?.Dispose(); }
        }

        await _server.DisposeAsync();
    }

    /// <summary>검토 창의 결과를 학습 재료로 저장한다 — 페이지마다 (원고 png, 자동 감지 마스크, 최종 마스크) 세 파일 + index.jsonl 한 줄.
    /// 자동 감지와 최종이 같고 둘 다 비었으면(검열 없는 페이지를 그냥 지나침) 깨끗 페이지 재료로도 쓸 수 있어 함께 저장한다.
    /// 경로: &lt;DataDir&gt;\training\corrections\&lt;yyyyMMdd&gt;\&lt;원본이름&gt;_&lt;HHmmss&gt;.{page,auto,mask}.png</summary>
    public static string CorrectionsDir => Path.Combine(AppSettings.DataDir, "training", "corrections");

    private void SaveCorrections(IReadOnlyList<ReviewPage> pages)
    {
        var dir = Path.Combine(CorrectionsDir, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("HHmmss");
        var lines = new List<string>();
        foreach (var pg in pages)
        {
            if (pg.Mask == null) continue;
            var stem = Path.GetFileNameWithoutExtension(pg.File) + "_" + stamp;
            using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
            if (img.Empty()) continue;
            ImgIo.Write(Path.Combine(dir, stem + ".page.png"), img);
            ImgIo.Write(Path.Combine(dir, stem + ".mask.png"), pg.Mask);
            if (pg.AutoMask != null) ImgIo.Write(Path.Combine(dir, stem + ".auto.png"), pg.AutoMask);
            int autoPx = pg.AutoMask == null ? 0 : Cv2.CountNonZero(pg.AutoMask);
            int finalPx = Cv2.CountNonZero(pg.Mask);
            int changed = 0;
            if (pg.AutoMask != null) { using var diff = new Mat(); Cv2.BitwiseXor(pg.AutoMask, pg.Mask, diff); changed = Cv2.CountNonZero(diff); }
            lines.Add($"{{\"stem\":\"{stem}\",\"source\":{System.Text.Json.JsonSerializer.Serialize(pg.File)},\"width\":{img.Width},\"height\":{img.Height}," +
                      $"\"auto_px\":{autoPx},\"final_px\":{finalPx},\"changed_px\":{changed},\"skip\":{(pg.Skip ? "true" : "false")}," +
                      $"\"summary\":{System.Text.Json.JsonSerializer.Serialize(pg.Summary)},\"model\":\"{Engine.CensorNet.ModelFile}\"}}");
        }
        if (lines.Count > 0) File.AppendAllLines(Path.Combine(dir, "index.jsonl"), lines);
    }

    /// <summary>검토 창 훅 — 검열 제거 전에 감지된 마스크를 사용자가 확인·편집한다. null이면(헤드리스) 검토 없이 진행.
    /// 페이지 목록의 Mask(CV_8UC1, 255=지울 곳)를 제자리에서 바꾸고, 진행하면 true·취소면 false를 돌려준다.</summary>
    public Func<IReadOnlyList<ReviewPage>, CancellationToken, Task<bool>>? ReviewMasks { get; set; }

    /// <summary>검토 흐름의 페이지 상태. Mask는 러너가 만들고(감지 결과 또는 빈 마스크) 러너가 해제한다 — 검토 창은 내용만 바꾼다.</summary>
    public sealed class ReviewPage(string file)
    {
        public string File { get; } = file;
        public string Name { get; } = Path.GetFileName(file);
        /// <summary>번역(글자 지움) 뒤의 원고 경로 — 검토 창이 보여 주는 그림이자 검열 제거 입력.</summary>
        public string WorkPath { get; set; } = file;
        public IReadOnlyList<TextBlockInfo> Blocks { get; set; } = Array.Empty<TextBlockInfo>();
        public Mat? Mask { get; set; }
        /// <summary>자동 감지 결과 사본(검토 전) — 학습 재료 저장용. CollectCorrections일 때만 채워진다.</summary>
        public Mat? AutoMask { get; set; }
        public string Summary { get; set; } = "";
        /// <summary>사용자가 '이 페이지는 검열 없음'으로 표시 — 제거를 건너뛴다.</summary>
        public bool Skip { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>검열 마스크 — AI 전용 모델(CensorNet) 1순위, 규칙 기반 폴백.
    /// AI 모델은 무검열 원고에 검열을 합성해 학습한 세그멘테이션이라 흰/검/모자이크를
    /// 한 번에, 장식 줄무늬·효과음과 구분해 찾는다. 모델 파일이 없으면 기존 규칙으로.</summary>
    public Mat? DetectCensorMask(Mat img, IReadOnlyList<Rect>? exclude, out string kindUsed, bool autoBubbles = false)
    {
        // 말풍선 오탐(실측 Confusion 12·21쪽: 흰 말풍선을 흰 검열로 봄)은 BuildMask의 텍스트 블록 필터가 걸러 주는데, 번역을 끄면
        // 블록이 비어 필터가 아예 안 돌았다 → autoBubbles면 글자 감지기(CTD, ~0.5초)만 따로 돌려 블록을 만든다 (2026-10-20).
        if (autoBubbles && (exclude == null || exclude.Count == 0))
        {
            var ctd = new Engine.TextDetector(_onnx);
            if (ctd.IsReady)
            {
                try
                {
                    var textDet = ctd.Detect(img);
                    exclude = textDet.Blocks.Select(b => new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();
                    textDet.TextMask.Dispose();
                }
                catch { /* 글자 감지 실패는 치명적이지 않다 — 필터 없이 진행 */ }
            }
        }
        var net = new CensorNet(_onnx);
        if (settings.DecensorTarget == "Auto" && net.IsReady)
        {
            // AI 모델이 있으면 AI가 유일한 판정자 — '없음'도 판정이다.
            // 규칙 기반으로 폴백하면 모델이 안 속는 장식 줄무늬 오탐이 도로 살아난다 (실측 22p).
            var aiMask = net.BuildMask(img, exclude, settings.DecensorMaskGrow, out kindUsed, bubbleFilter: settings.DecensorBubbleFilter);
            if (aiMask == null) kindUsed += " → 검열 없음";
            return aiMask;
        }
        return MosaicDetector.BuildMaskForTarget(settings, img, exclude, out kindUsed);
    }

    /// <summary>결과 보기 창에서 사용자가 손으로 고른 영역을 지운다 (자동 감지가 놓친 검열용).
    /// 파이프라인과 같은 복원 경로를 쓰므로 결과물의 성격이 일관된다.
    /// mode: "Basic" | "ComfyLama" | "ComfyRegen" (옛 "Strong"·"DeepCream"은 2026-10-19 폐기 — 모르는 값은 ArgumentException).</summary>
    public async Task ManualDecensorAsync(Mat img, Mat mask, string mode,
        IProgress<string>? status, CancellationToken ct)
    {
        switch (mode)
        {
            case "ComfyCustom":
            {
                var wf = CustomWorkflow.Load(settings.CustomDecensorWorkflow);
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("커스텀 워크플로로 복원 중...");
                await ComfyCustomDecensorAsync(client, outDir, wf, img, mask, 902, ct);
                return;
            }
            case "ComfyRegen":
            {
                CheckRegenModels();
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("검열 자리 재생성 중 (SDXL)...");
                await ComfyRegenDecensorAsync(client, outDir, img, mask, 901, ct);
                return;
            }
            case "Basic":
            {
                var lama = new LamaInpainter(_onnx);
                if (!lama.IsReady)
                    throw new InvalidOperationException("복원 모델(LaMa)이 없어요 — 설정 → 구성요소에서 설치해 주세요.");
                status?.Report("복원 중...");
                using var m = mask.Clone(); // LaMa 내부 FindContours가 마스크를 파괴한다
                lama.Inpaint(img, m);
                return;
            }
            case "ComfyLama":
            {
                await _server.EnsureRunningAsync(status, ct);
                var client = new ComfyClient(_server.BaseUrl);
                var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
                status?.Report("엔진 LaMa로 복원 중...");
                // 페이지 번호는 파일 이름 충돌만 피하면 되므로 손수정 전용 번호를 쓴다
                await ComfyLamaDecensorAsync(client, outDir, img, mask, 900, ct);
                return;
            }
            default:
                throw new ArgumentException($"알 수 없는 복원 방식: {mode} (Basic | ComfyLama | ComfyRegen | ComfyCustom)", nameof(mode));
        }
    }

    /// <summary>커스텀 워크플로 검열제거 — 사용자가 ComfyUI 화면에서 짠 API JSON(CustomWorkflow 규약)에 마스크 PNG(알파=마스크)를 넣어 돌리고,
    /// 결과를 마스크 둘레 CustomDecensorReach px 안쪽만(0이면 전체) 원본에 되돌린다. 워크플로 내용은 앱이 손대지 않는다.</summary>
    private async Task ComfyCustomDecensorAsync(ComfyClient client, string comfyOutputDir, JsonObject workflow,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
    {
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_custom_{pageIdx}.png", ct);
        var prepared = CustomWorkflow.Prepare(workflow, up, $"nm_customout_{pageIdx}");
        var pid = await client.QueuePromptAsync(prepared.Prompt, ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(30), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("커스텀 워크플로 결과 파일이 없어요 — SaveImage 노드가 결과를 저장하는지 확인해 주세요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("커스텀 워크플로 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height), 0, 0, InterpolationFlags.Lanczos4);
        int reach = settings.CustomDecensorReach;
        if (reach <= 0) { resized.CopyTo(img); return; }
        using var take = new Mat();
        Cv2.Dilate(mask, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
        resized.CopyTo(img, take);
    }

    /// <summary>재생성 워크플로가 쓰는 SDXL 체크포인트·ControlNet 파일이 ComfyUI models 폴더에 있는지 먼저 본다 —
    /// 없으면 ComfyUI가 큐 단계에서 알아듣기 어려운 오류를 내므로 파일 이름과 폴더를 그대로 알려 준다.
    /// (모델 파일은 사용자가 직접 넣는다 — 앱은 내려받지 않는다.)</summary>
    private void CheckRegenModels()
    {
        var ck = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", "checkpoints", settings.RegenCheckpoint);
        var cnDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "models", "controlnet");
        var cn = Path.Combine(cnDir, settings.RegenControlNet);
        // 사용자 워크플로의 'diffusion_pytorch_model_promax.safetensors'는 xinsir union promax 그 파일이고, 앱 설치기는 같은 모델을
        // 'controlnet-union-sdxl-promax.safetensors'로 받아 둔다 → 이름만 다르면 설치된 promax를 쓴다 (체크포인트는 대체하지 않는다).
        if (!File.Exists(cn) && settings.RegenControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(cnDir, settings.ColorControlNet)) && settings.ColorControlNet.Contains("promax", StringComparison.OrdinalIgnoreCase))
        {
            settings.RegenControlNet = settings.ColorControlNet;
            cn = Path.Combine(cnDir, settings.RegenControlNet);
        }
        if (!File.Exists(ck))
            throw new FileNotFoundException($"재생성 체크포인트가 없어요: {ck}\n설정 → 검열 제거 → 고급 옵션의 '재생성 체크포인트'를 ComfyUI models\\checkpoints 안의 파일 이름으로 맞춰 주세요.");
        if (!File.Exists(cn))
            throw new FileNotFoundException($"재생성 ControlNet이 없어요: {cn}\n설정 → 검열 제거 → 고급 옵션의 '재생성 ControlNet'을 ComfyUI models\\controlnet 안의 파일 이름으로 맞춰 주세요.");
    }

    /// <summary>재생성 검열제거 — 사용자가 ComfyUI에서 직접 짠 워크플로(2026-10-20, WorkflowBuilder.BuildRegenDecensor)로
    /// 페이지 전체를 보내 LaMa 1차 복원 → SDXL img2img(denoise 0.62, 선화 ControlNet) → 마스크 둘레만 합성해 받는다.
    /// 되가져올 때는 워크플로의 엣지 마스크(확장+블러)가 닿는 범위까지만 받아들이고 나머지 픽셀은 원본 그대로 둔다
    /// (VAE 왕복은 마스크 밖도 미세하게 바꾸지만 워크플로가 LaMa 결과 위에 합성하므로 밖은 LaMa 결과=원본이다).</summary>
    private async Task ComfyRegenDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
    {
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_regen_{pageIdx}.png", ct);
        var pid = await client.QueuePromptAsync(
            WorkflowBuilder.BuildRegenDecensor(settings, up, $"nm_regenout_{pageIdx}"), ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("재생성 결과 파일이 없어요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("재생성 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height), 0, 0, InterpolationFlags.Lanczos4);
        // 엣지 마스크(확장+블러)가 닿는 범위 + 여유 2px 안쪽만 받아들인다
        int reach = Math.Max(settings.RegenEdgeGrow + settings.RegenEdgeBlur, settings.RegenCoreGrow + settings.RegenCoreBlur) + 2;
        using var take = new Mat();
        Cv2.Dilate(mask, take, Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(reach * 2 + 1, reach * 2 + 1)));
        resized.CopyTo(img, take);
    }

    /// <summary>엔진 LaMa 검열제거 — 사용자의 원본 ComfyUI 파이프라인(LayerUtility: LaMa, GPU,
    /// 확장 8·블러 8) 그대로. 전체 이미지를 통째로 보내므로 ROI 분할이 필요 없다.</summary>
    private async Task ComfyLamaDecensorAsync(ComfyClient client, string comfyOutputDir,
        Mat img, Mat mask, int pageIdx, CancellationToken ct)
    {
        var maskedPng = MosaicDetector.ToMaskedPng(img, mask);
        var up = await client.UploadImageAsync(maskedPng, $"nm_lama_{pageIdx}.png", ct);
        var pid = await client.QueuePromptAsync(
            WorkflowBuilder.BuildLamaDecensor(settings, up, $"nm_lamaout_{pageIdx}"), ct);
        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(10), ct);
        if (r.OutputFiles.Count == 0)
            throw new InvalidOperationException("엔진 LaMa 결과 파일이 없어요.");

        using var outImg = ImgIo.Read(r.OutputFiles[^1], ImreadModes.Color);
        if (outImg.Empty()) throw new InvalidDataException("엔진 LaMa 결과를 읽지 못했어요.");
        using var resized = outImg.Width == img.Width && outImg.Height == img.Height
            ? outImg.Clone()
            : outImg.Resize(new OpenCvSharp.Size(img.Width, img.Height));
        // 마스크 영역만 받아들인다 — 나머지 픽셀은 원본 그대로 보존
        resized.CopyTo(img, mask);
    }

    public string OutputPathFor(string inputFile)
    {
        var dir = !string.IsNullOrWhiteSpace(settings.OutputRoot)
            ? settings.OutputRoot
            : Path.Combine(Path.GetDirectoryName(inputFile)!,
                Path.GetFileName(Path.GetDirectoryName(inputFile)!) + "_neko");
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(inputFile) + ".png");
    }

    private static void Report(IProgress<PageProgress> p, IReadOnlyList<string> files, string stage, double pct)
    {
        foreach (var f in files) p.Report(new(Path.GetFileName(f), stage, pct));
    }

    public void Dispose() => _onnx.Dispose();
}
