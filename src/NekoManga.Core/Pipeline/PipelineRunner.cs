using NekoManga.Core.Ballons;
using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;

namespace NekoManga.Core.Pipeline;

public record PageProgress(string File, string Stage, double Percent, bool Done = false, string? Error = null);

/// <summary>페이지 처리 파이프라인.
///
///   원고 → [번역: BT가 글자 감지→OCR→번역→글자 지움]
///        → [검열제거: 모자이크 감지 → LaMa]
///        → [자동채색: ComfyUI SDXL]
///        → [식자: 번역문을 C#으로 렌더]
///        → 완성본
///
/// 번역을 먼저 돌리는 이유: 글자가 지워진 원고를 채색해야 말풍선이 깨끗하고,
/// OCR도 채색 전 원본에서 해야 정확하다. 각 단계는 설정에서 켜고 끌 수 있다.</summary>
public class PipelineRunner(AppSettings settings)
{
    private readonly BallonsService _ballons = new(settings);
    private readonly ComfyServer _server = new(settings);

    public async Task RunAsync(IReadOnlyList<string> files, IProgress<PageProgress> progress, CancellationToken ct)
    {
        if (files.Count == 0) return;

        // ── 1단계: 번역 (배치 — BT는 폴더 단위가 효율적) ──
        IReadOnlyDictionary<string, BallonsResult>? translated = null;
        if (settings.EnableTranslate)
        {
            Report(progress, files, "번역 준비", 5);
            var status = new Progress<string>(m => Report(progress, files, m, 20));
            translated = await _ballons.TranslateAsync(files, status, ct);
        }

        var needComfy = settings.EnableAutoColor || settings.EnableDecensor;
        ComfyClient? client = null;
        string comfyOutputDir = "";
        if (needComfy)
        {
            var status = new Progress<string>(m => Report(progress, files, m, 30));
            await _server.EnsureRunningAsync(status, ct);
            client = new ComfyClient(_server.BaseUrl);
            comfyOutputDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
        }

        // ── 2단계~: 페이지별 처리 ──
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var name = Path.GetFileName(file);
            try
            {
                // 번역 결과: 글자 지워진 원고 (없으면 원본)
                var work = translated != null && translated.TryGetValue(name, out var tr)
                    ? tr.InpaintedPath : file;
                var blocks = translated != null && translated.TryGetValue(name, out var tr2)
                    ? tr2.Blocks : Array.Empty<TextBlockInfo>();

                // 검열제거
                if (settings.EnableDecensor && client != null)
                {
                    progress.Report(new(name, "검열 영역 찾는 중", 40));
                    var masked = MosaicDetector.BuildMaskedPng(work,
                        settings.DecensorBlockSize, settings.DecensorThreshold);
                    if (masked != null)
                    {
                        progress.Report(new(name, "검열 지우는 중", 45));
                        var up = await client.UploadImageAsync(masked, $"nm_dc_{i}.png", ct);
                        var pid = await client.QueuePromptAsync(
                            WorkflowBuilder.BuildDecensor(settings, up, $"nm_decensor_{i}"), ct);
                        var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(10), ct);
                        if (r.OutputFiles.Count > 0) work = r.OutputFiles[^1];
                    }
                }

                // 자동채색
                if (settings.EnableAutoColor && client != null)
                {
                    progress.Report(new(name, "채색 중 (페이지당 1~2분)", 55));
                    var png = await File.ReadAllBytesAsync(work, ct);
                    var up = await client.UploadImageAsync(png, $"nm_cl_{i}.png", ct);
                    var pid = await client.QueuePromptAsync(
                        WorkflowBuilder.BuildAutoColor(settings, up, $"nm_color_{i}"), ct);
                    var r = await client.WaitAsync(pid, comfyOutputDir, TimeSpan.FromMinutes(20), ct);
                    if (r.OutputFiles.Count == 0)
                        throw new InvalidOperationException("채색 결과 파일이 없어요.");
                    work = r.OutputFiles[^1];
                }

                // 식자(번역문 렌더) 및 최종 저장
                var outPath = OutputPathFor(file);
                if (blocks.Count > 0)
                {
                    progress.Report(new(name, "식자 중", 90));
                    TextRenderer.RenderBlocks(work, file, blocks, outPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.Copy(work, outPath, overwrite: true);
                }

                progress.Report(new(name, "완료", 100, Done: true));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report(new(name, "실패", 100, Done: true, Error: ex.Message));
            }
        }

        await _server.DisposeAsync();
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
}
