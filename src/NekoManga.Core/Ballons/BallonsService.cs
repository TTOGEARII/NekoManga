using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Ballons;

/// <summary>번역된 말풍선 하나.</summary>
public record TextBlockInfo(
    int X1, int Y1, int X2, int Y2,
    string Translation,
    double FontSize,
    bool Vertical,
    string FontColorHex);

public record BallonsResult(
    /// <summary>글자가 지워진(인페인트) 페이지 이미지 경로. 없으면 원본 그대로.</summary>
    string InpaintedPath,
    IReadOnlyList<TextBlockInfo> Blocks);

/// <summary>BallonsTranslator를 헤드리스로 돌려 텍스트 감지→OCR→번역→글자 지우기를 수행한다.
/// 결과물: 프로젝트 폴더의 inpainted/ (글자 지운 원고) + imgtrans_*.json (블록 좌표·번역문).
/// 번역문 렌더링은 채색 이후 C#(TextRenderer)에서 한다 — BT가 렌더한 결과는 흑백 원고 기준이라서.</summary>
public class BallonsService(AppSettings settings)
{
    public string PythonExe => Path.Combine(settings.BallonsRoot, "ballontrans_pylibs_win", "python.exe");

    public bool IsAvailable =>
        File.Exists(PythonExe) &&
        File.Exists(Path.Combine(settings.BallonsRoot, "ballontranslator", "launch.py"));

    /// <summary>이미지들을 임시 프로젝트 폴더로 복사하고 BT 헤드리스를 1회 실행한다.
    /// 반환: 입력 파일명(확장자 포함) → 결과.</summary>
    public async Task<IReadOnlyDictionary<string, BallonsResult>> TranslateAsync(
        IReadOnlyList<string> imagePaths,
        IProgress<string>? status,
        CancellationToken ct)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("BallonsTranslator가 설치되어 있지 않아요. 설정 → 구성요소에서 설치해 주세요.");

        var projDir = Path.Combine(AppSettings.DataDir, "work",
            $"bt-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(projDir);
        foreach (var src in imagePaths)
            File.Copy(src, Path.Combine(projDir, Path.GetFileName(src)), overwrite: true);

        WriteHeadlessConfig();

        status?.Report("번역 엔진 실행 중... (첫 실행은 모델 다운로드로 오래 걸려요)");
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"-m ballontranslator --headless --exec_dirs \"{projDir}\"",
            WorkingDirectory = settings.BallonsRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // launch_win.bat과 동일하게 전용 파이썬 환경을 PATH 앞에 둔다.
        var pylibs = Path.Combine(settings.BallonsRoot, "ballontrans_pylibs_win");
        psi.Environment["PATH"] = $"{pylibs};{Path.Combine(pylibs, "Scripts")};{Environment.GetEnvironmentVariable("PATH")}";

        var log = new List<string>();
        using (var p = Process.Start(psi)!)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (log) log.Add(e.Data); Report(status, e.Data); } };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.Add(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"번역 엔진이 실패했어요 (exit {p.ExitCode}).\n{string.Join("\n", log.TakeLast(15))}");
        }

        return ParseProject(projDir);
    }

    private static void Report(IProgress<string>? status, string line)
    {
        // BT 로그에서 사용자에게 의미 있는 진행만 추린다.
        if (line.Contains("Translating") || line.Contains("OCR") || line.Contains("inpaint", StringComparison.OrdinalIgnoreCase))
            status?.Report("번역 중: " + (line.Length > 60 ? line[..60] : line));
    }

    /// <summary>헤드리스 실행이 쓸 config.json을 준비한다 (번역기·언어·모듈 프리셋).
    /// 사용자가 BT 설정을 직접 만진 적이 있으면 보존하고 번역기/언어만 갱신한다.</summary>
    private void WriteHeadlessConfig()
    {
        var cfgDir = Path.Combine(settings.BallonsRoot, "config");
        Directory.CreateDirectory(cfgDir);
        var cfgPath = Path.Combine(cfgDir, "config.json");

        JsonObject cfg;
        try
        {
            cfg = File.Exists(cfgPath)
                ? JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch { cfg = new JsonObject(); }

        var module = cfg["module"] as JsonObject ?? new JsonObject();
        module["textdetector"] = "ctd";
        module["ocr"] = "mit48px_ctc";
        module["inpainter"] = "lama_mpe";
        module["translator"] = settings.TranslateEngine;
        module["translate_source"] = settings.TranslateSource;
        module["translate_target"] = settings.TranslateTarget;
        module["enable_detect"] = true;
        module["enable_ocr"] = true;
        module["enable_translate"] = true;
        module["enable_inpaint"] = true;
        cfg["module"] = module;

        File.WriteAllText(cfgPath, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyDictionary<string, BallonsResult> ParseProject(string projDir)
    {
        var projJson = Path.Combine(projDir, $"imgtrans_{Path.GetFileName(projDir)}.json");
        if (!File.Exists(projJson))
            projJson = Directory.EnumerateFiles(projDir, "imgtrans_*.json").FirstOrDefault()
                ?? throw new FileNotFoundException("번역 결과(프로젝트 파일)를 찾지 못했어요.", projJson);

        var root = JsonNode.Parse(File.ReadAllText(projJson))!;
        var pages = root["pages"] as JsonObject
            ?? throw new InvalidDataException("프로젝트 파일에 pages가 없어요.");

        var results = new Dictionary<string, BallonsResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (imgName, blocksNode) in pages)
        {
            var blocks = new List<TextBlockInfo>();
            if (blocksNode is JsonArray arr)
                foreach (var b in arr)
                {
                    if (b == null) continue;
                    var xyxy = b["xyxy"] as JsonArray;
                    if (xyxy is not { Count: 4 }) continue;
                    var translation = b["translation"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(translation)) continue;

                    var ffmt = b["fontformat"] as JsonObject;
                    double fontSize = ffmt?["font_size"]?.GetValue<double>() ?? -1;
                    if (fontSize <= 0) fontSize = b["_detected_font_size"]?.GetValue<double>() ?? 24;
                    bool vertical = ffmt?["vertical"]?.GetValue<bool>() ?? false;
                    string color = ReadColor(ffmt);

                    blocks.Add(new TextBlockInfo(
                        (int)xyxy[0]!.GetValue<double>(), (int)xyxy[1]!.GetValue<double>(),
                        (int)xyxy[2]!.GetValue<double>(), (int)xyxy[3]!.GetValue<double>(),
                        translation, fontSize, vertical, color));
                }

            var inpainted = Path.Combine(projDir, "inpainted",
                Path.GetFileNameWithoutExtension(imgName) + ".png");
            results[imgName] = new BallonsResult(
                File.Exists(inpainted) ? inpainted : Path.Combine(projDir, imgName),
                blocks);
        }
        return results;
    }

    private static string ReadColor(JsonObject? ffmt)
    {
        try
        {
            if (ffmt?["frgb"] is JsonArray { Count: 3 } rgb)
                return $"#{(int)rgb[0]!.GetValue<double>():X2}{(int)rgb[1]!.GetValue<double>():X2}{(int)rgb[2]!.GetValue<double>():X2}";
        }
        catch { }
        return "#000000";
    }
}
