using System.IO;
using NekoManga.Core;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
using TextDetector = NekoManga.Core.Engine.TextDetector;
using TextRenderer = NekoManga.Core.Pipeline.TextRenderer;

namespace NekoManga.App;

/// <summary>--selftest: 내장 엔진을 창 없이 돌려 단계별 결과 파일을 남긴다 (개발·문제 진단용).</summary>
public static class SelfTest
{
    public static void Run(string imagePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
        var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath));
        var log = new StreamWriter(stem + ".selftest.log", false, System.Text.Encoding.UTF8);
        void L(string m) { log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"); log.Flush(); }

        try
        {
            var settings = AppSettings.Load();
            using var env = new OnnxEnv(settings);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1) 감지
            var detector = new TextDetector(env);
            L($"models dir: {env.ModelsDir} / detector ready: {detector.IsReady}");
            using var img = ImgIo.Read(imagePath, ImreadModes.Color);
            var det = detector.Detect(img);
            L($"감지: {det.Blocks.Count}개 블록, {sw.ElapsedMilliseconds}ms");
            using (var vis = img.Clone())
            {
                foreach (var b in det.Blocks)
                    Cv2.Rectangle(vis, new Rect(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1), Scalar.Red, 3);
                ImgIo.Write(stem + ".selftest.boxes.png", vis);
            }
            ImgIo.Write(stem + ".selftest.mask.png", det.TextMask);

            // 2) 전체 번역 파이프라인
            sw.Restart();
            var translator = new EmbeddedTranslator(settings, env);
            var result = translator.TranslatePageAsync(imagePath, dir, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            L($"번역 파이프라인: {sw.ElapsedMilliseconds}ms, 블록 {result.Blocks.Count}개");
            foreach (var b in result.Blocks)
                L($"  [{b.X1},{b.Y1},{b.X2},{b.Y2}] v={b.Vertical} fs={b.FontSize:f0} '{b.SourceText}' → '{b.Translation}'");
            File.Copy(result.InpaintedPath, stem + ".selftest.inpainted.png", true);

            // 3) 식자
            var finalPath = stem + ".selftest.final.png";
            // 파이프라인과 동일하게 식자 사이드카도 남긴다 — 결과 보기 창의 식자 탭 테스트용
            using (var baseImg = ImgIo.Read(result.InpaintedPath))
            using (var srcImg = ImgIo.Read(imagePath))
                if (!baseImg.Empty() && !srcImg.Empty())
                {
                    ImgIo.Write(LetterSidecar.BasePathFor(finalPath), baseImg);
                    new LetterSidecar(imagePath, srcImg.Width, srcImg.Height,
                        LetterSidecar.BasePathFor(finalPath), [.. result.Blocks]).Save(finalPath);
                }
            TextRenderer.RenderBlocks(result.InpaintedPath, imagePath, result.Blocks, finalPath, settings);
            L("식자 완료 → .selftest.final.png");

            // 4) 검열제거(기본) — 마스크가 잡히면
            sw.Restart();
            using (var img2 = ImgIo.Read(imagePath, ImreadModes.Color))
            {
                var exclude = result.Blocks.Select(b =>
                    new Rect(b.X1, b.Y1, Math.Max(1, b.X2 - b.X1), Math.Max(1, b.Y2 - b.Y1))).ToList();

                using var mask = settings.DecensorTarget == "Mosaic"
                    ? MosaicDetector.BuildMask(img2, settings.DecensorBlockSize, settings.DecensorThreshold, settings.DecensorMaskGrow)
                    : MosaicDetector.BuildBarMask(img2,
                        white: settings.DecensorTarget == "WhiteBar",
                        black: settings.DecensorTarget == "BlackBar",
                        settings.DecensorBarMinAspect, settings.DecensorBarMinArea,
                        settings.DecensorMaskGrow, exclude);
                L($"검열 감지({settings.DecensorTarget}): {(mask == null ? "없음" : Cv2.CountNonZero(mask) + "px")}");
                if (mask != null)
                {
                    ImgIo.Write(stem + ".selftest.censormask.png", mask);
                    using var maskCopy = mask.Clone(); // FindContours가 원본을 변형하므로 LaMa엔 복사본
                    new LamaInpainter(env).Inpaint(img2, maskCopy);
                    ImgIo.Write(stem + ".selftest.decensor.png", img2);
                    L($"검열제거(기본): {sw.ElapsedMilliseconds}ms");
                }
                else L("검열제거: 검열 미감지 (정상일 수 있음)");
            }

            L("SELFTEST OK");
        }
        catch (Exception ex)
        {
            L("SELFTEST FAILED: " + ex);
        }
        finally { log.Dispose(); }
    }
}
