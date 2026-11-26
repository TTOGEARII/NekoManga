namespace NekoManga.Core.Pipeline;

/// <summary>작업 폴더(<c>%AppData%\NekoManga\work\job-…</c>)와 ComfyUI에 올리고 받은 파일의 뒷정리.
///
/// 작업 폴더에는 그 작업의 중간 그림(글자 지운 원고·검열 지운 그림·채색 결과 사본)만 들어간다 — 결과물과 사이드카는 출력 폴더에 따로 있어 작업이 끝나면 쓸 데가 없다.
/// 2026-11-13까지는 지우는 코드가 없어 641개 6.0GB가 쌓여 있었다(요즘은 작업 한 번에 50MB쯤). ComfyUI 폴더에도 앱이 만든 파일이 남았다(output\nm_* 460개 566MB, input\nm_* 154개 356MB).
/// 그래서: 작업이 끝나면(성공·실패·멈춤) 그 작업의 폴더를 지우고(<see cref="Remove"/>), 앱이 시작할 때 오래된 것(꺼지거나 죽어서 남은 것, 옛 버전이 쌓아 둔 것)을 지운다(<see cref="SweepOld()"/>).
/// 진단하려고 중간 그림을 남기고 싶으면 환경변수 <c>NEKO_KEEP_WORK=1</c>.</summary>
public static class WorkFolders
{
    public static string Root => Path.Combine(AppSettings.DataDir, "work");

    /// <summary>앱이 시작할 때 이보다 오래된 작업 폴더를 지운다. 넉넉하게 둔다 — 다른 창(같은 PC의 또 다른 NekoManga)이 돌리는 작업의 폴더일 수 있고,
    /// 검토 대기로 며칠 열어 둔 작업은 그 폴더의 글자 지운 원고를 아직 쓴다.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>진단용 — <c>NEKO_KEEP_WORK=1</c>이면 작업 폴더도 ComfyUI의 입력·출력 파일도 지우지 않는다.</summary>
    public static bool Keep => Environment.GetEnvironmentVariable("NEKO_KEEP_WORK") == "1";

    /// <summary>새 작업 폴더의 경로(만들지는 않는다) — <c>job-&lt;시각&gt;-&lt;난수 4자&gt;</c>. 난수 꼬리는 같은 초에 시작한 두 작업(다른 창, 묶음과 낱장)이 한 폴더를 쓰지 않게:
    /// 끝난 작업이 폴더를 지우므로, 같이 쓰면 먼저 끝난 쪽이 아직 도는 쪽의 중간 그림을 지운다.</summary>
    public static string NewJobDir(string? root = null) =>
        Path.Combine(root ?? Root, $"job-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}");

    /// <summary>끝난 작업의 폴더를 지운다 — 이름이 <c>job-</c>로 시작하는 폴더만. 못 지워도(다른 프로그램이 쥔 파일) 조용히 넘어간다: 다음 시작 때의 훑기가 다시 지운다.</summary>
    public static void Remove(string jobDir)
    {
        if (Keep) return;
        try
        {
            if (Path.GetFileName(jobDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).StartsWith("job-", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(jobDir))
                Directory.Delete(jobDir, recursive: true);
        }
        catch { }
    }

    /// <summary>앱이 시작할 때 부른다 — <see cref="MaxAge"/>보다 오래된 작업 폴더를 지운다(<c>NEKO_KEEP_WORK=1</c>이면 아무것도 안 한다). 지운 폴더 수.</summary>
    public static int SweepOld() => Keep ? 0 : SweepOld(Root, MaxAge);

    /// <summary><paramref name="root"/> 바로 아래의 <c>job-*</c> 폴더 가운데 마지막으로 쓰인 지 <paramref name="maxAge"/>를 넘은 것을 지운다. 다른 이름의 폴더·파일은 건드리지 않는다.
    /// 폴더 하나를 못 지워도 나머지를 계속한다. 지운 폴더 수.</summary>
    public static int SweepOld(string root, TimeSpan maxAge, DateTime? nowUtc = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            var cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
            foreach (var dir in Directory.GetDirectories(root, "job-*"))
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch { }
        }
        catch { }
        return removed;
    }

    /// <summary>프롬프트 하나가 끝난 뒤, 그 프롬프트를 위해 ComfyUI에 올린 입력 파일과 ComfyUI가 저장한 출력 파일을 지운다.
    /// 앱이 만든 파일만: 이름이 <c>nm_</c>로 시작하고 그 ComfyUI의 <c>input</c>(바로 아래)·<c>output</c> 폴더 안에 있는 것. 그 밖의 경로·이름은 무엇이 오든 건드리지 않는다
    /// (사용자가 직접 쓰는 ComfyUI의 폴더다). 못 지워도 조용히 넘어간다.</summary>
    /// <param name="outputFiles">ComfyClient.WaitAsync가 돌려준 출력 파일 경로들.</param>
    /// <param name="outputPrefix">그 프롬프트의 SaveImage 접두사(PipelineRunner.NextOutputPrefix — 프롬프트마다 다르다). 있으면 output 바로 아래의 <c>&lt;접두사&gt;_*</c>도 지운다:
    /// 멈춤·제한 시간 초과·연결 오류로 기다리기를 그만두면 출력 파일 이름을 못 받는데, 그사이 서버가 저장했으면 남는다. 글자·숫자·밑줄만 든 <c>nm_…</c>(8자 이상)일 때만 훑는다.</param>
    /// <param name="inputNames">올린 입력 파일 이름들(폴더 없이) — UploadImageAsync가 돌려준 이름과, 답을 못 받았을 때를 위해 올리며 부탁한 이름.</param>
    public static void RemoveComfyFiles(string comfyRoot, IEnumerable<string>? outputFiles, string? outputPrefix, params string?[] inputNames)
    {
        if (Keep) return;
        var outDir = Path.Combine(comfyRoot, "ComfyUI", "output");
        var inDir = Path.Combine(comfyRoot, "ComfyUI", "input");
        foreach (var f in outputFiles ?? []) DeleteOurs(f, outDir, anyDepth: true);
        if (outputPrefix is { Length: >= 8 } prefix && prefix.StartsWith("nm_", StringComparison.OrdinalIgnoreCase)
            && prefix.All(c => c == '_' || (c < 128 && char.IsLetterOrDigit(c))))
            try
            {
                foreach (var f in Directory.GetFiles(outDir, prefix + "_*")) DeleteOurs(f, outDir, anyDepth: false);
            }
            catch { }
        foreach (var n in inputNames)
            if (!string.IsNullOrEmpty(n)) DeleteOurs(Path.Combine(inDir, n), inDir, anyDepth: false);
    }

    private static void DeleteOurs(string file, string under, bool anyDepth)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var dir = Path.GetFullPath(under).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFileName(full).StartsWith("nm_", StringComparison.OrdinalIgnoreCase)) return;
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return;
            if (!anyDepth && !string.Equals(Path.GetDirectoryName(full) + Path.DirectorySeparatorChar, dir, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(full)) File.Delete(full);
        }
        catch { }
    }
}
