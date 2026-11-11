using System.Text.RegularExpressions;

namespace NekoManga.Core.Comfy;

/// <summary>컬러 표지에서 메인 캐릭터의 '색 태그'를 뽑는다.
///
/// 흑백 원고만으로는 모델이 머리·눈·의상 색을 멋대로 정해 페이지마다 색이 달라진다.
/// 표지를 WD14 태거에 넣어 색이 들어간 태그(pink hair, blue eyes, red dress …)만 걸러
/// 채색 프롬프트에 함께 넣으면, 추가 모델 없이도 캐릭터 색이 표지와 맞춰진다.</summary>
public static class ColorRefHelper
{
    private static readonly string[] ColorWords =
    [
        "red", "orange", "yellow", "green", "blue", "purple", "pink", "brown",
        "black", "white", "grey", "gray", "blonde", "silver", "aqua", "teal",
        "violet", "lavender", "gold", "platinum", "tan", "auburn", "turquoise",
        "magenta", "cyan", "scarlet", "crimson", "indigo", "peach", "mint",
        "olive", "navy", "maroon", "beige", "cream", "multicolored", "gradient",
        "dark skin", "pale skin", "dark-skinned",
        // 색 이름 없이 외모 색을 말하는 태그(colored skin·colored inner hair·colored tips·tanlines …) — 부분 문자열로 찾던 때는 colored 속 red,
        // tanlines 속 tan으로 남았다. 머리색 배치·피부 무늬는 대신 남는 태그가 없어 빠지면 쪽마다 색 배치가 흔들릴 수 있다
        "colored", "tanlines",
    ];

    // 색 단어가 들어 있어도 캐릭터 색과 무관한 태그
    private static readonly string[] StopWords =
    [
        "background", "monochrome", "greyscale", "grayscale", "theme", "border",
        "outline", "text", "speech", "sclera", "blood",
        // colored를 색 낱말로 넣으며 같이 걸리는 화풍·그림자·화구 태그 (partially colored, colored shadow, colored pencil \(medium\) — 화구 태그는 '… \(medium\)' 꼴이다)
        "partially colored", "colored shadow", "(medium",
    ];

    // 색 낱말은 태그 안에서 낱말 단위로만 찾는다 — 부분 문자열로 찾으면 standing·tank top(tan), covered navel(red)처럼 색과 무관한
    // 흔한 태그가 남아 모든 쪽의 채색 프롬프트에 붙는다. WD14는 낱말을 공백(replace_underscore)이나 밑줄로 잇고
    // red-framed eyewear·dark-skinned처럼 붙임표로도 이으므로, 글자·숫자가 아닌 것은 모두 낱말 경계로 본다.
    private static readonly Regex WordRun = new(@"[\p{L}\p{N}]+");
    private static readonly string[] SpacedColorWords = ColorWords.Select(Spaced).ToArray();

    // IPAdapter_plus 로더(utils.py의 get_ipadapter_file·get_clipvision_file)가 채색 워크플로의 'PLUS (high strength)' 프리셋 + SDXL 체크포인트에서
    // 모델 파일을 고르는 정규식과 같다(대소문자 무시). CLIP 쪽 끝($)은 로더가 목록을 모델 확장자로 먼저 거르는 것을 대신한다 — 받다 만 .part는 빠진다
    private static readonly Regex PlusSdxlModel = new(@"plus.sdxl.vit.h\.(safetensors|bin)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VitHClipVision = new(@"(ViT.H.14.*s32B.b79K|ipadapter.*sd15|sd1.?5.*model)\.(bin|safetensors)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly EnumerationOptions InFolder = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>IPAdapter 정밀 매칭이 가능한가 — 커스텀 노드 + SDXL IPAdapter 모델 + CLIP 비전 모델.</summary>
    public static bool IpAdapterAvailable(AppSettings s)
    {
        var root = Path.Combine(s.ComfyRoot, "ComfyUI");
        var ipaDir = Path.Combine(root, "models", "ipadapter");
        var cvDir = Path.Combine(root, "models", "clip_vision");
        // 로더가 고르지 않는 파일(얼굴 전용 plus-face·faceid-plusv2, 받다 만 .part, bigG CLIP)만 있는데 준비됐다고 보면
        // 'PLUS (high strength)' 프리셋이 모델을 못 찾아 모든 쪽의 채색이 실행 중에 실패한다 — 로더와 같은 규칙으로 본다
        return Directory.Exists(Path.Combine(root, "custom_nodes", "ComfyUI_IPAdapter_plus"))
            && LoaderFinds(ipaDir, PlusSdxlModel)
            && LoaderFinds(cvDir, VitHClipVision);
    }

    /// <summary>폴더 아래(하위 폴더 포함)에 폴더 기준 상대 경로가 rule에 맞는 파일이 있는가 — ComfyUI가 모델 목록을 만드는 꼴(folder_paths.get_filename_list)과 같다.
    /// 전체 경로로 보면 엔진 폴더 이름(ComfyUI-plus 같은)까지 규칙에 걸린다.</summary>
    private static bool LoaderFinds(string dir, Regex rule) =>
        Directory.Exists(dir) && FilesBelow(dir).Any(f => rule.IsMatch(Path.GetRelativePath(dir, f)));

    /// <summary>dir 아래의 모든 파일(하위 폴더 포함, 위쪽 폴더부터). 열리지 않는 하위 폴더는 건너뛰고 나머지를 계속 본다 — ComfyUI가 모델 목록을 만드는 os.walk도 그런 폴더를 조용히 넘긴다.
    /// RecurseSubdirectories로 한 번에 훑으면 꺼진 NAS를 가리키는 폴더 링크 하나에 IOException('네트워크 이름을 찾을 수 없습니다')이 나고(IgnoreInaccessible은 접근 거부만 넘긴다),
    /// 이 판정은 작업 준비의 try 밖에서 불려 채색 작업 전체가 시작하지 못했다 (2026-11-11).</summary>
    private static IEnumerable<string> FilesBelow(string dir)
    {
        var pending = new Queue<string>([dir]);
        while (pending.Count > 0)
        {
            var folder = pending.Dequeue();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder, "*", InFolder);
                folders = Directory.GetDirectories(folder, "*", InFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var sub in folders) pending.Enqueue(sub);
        }
    }

    /// <summary>WD14 출력에서 캐릭터 색 태그만 남긴다.</summary>
    public static string FilterColorTags(string wd14Output)
    {
        var picked = wd14Output.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Where(HasColorWord)
            .Where(t => !StopWords.Any(sw => t.Contains(sw, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", picked);
    }

    private static bool HasColorWord(string tag)
    {
        var words = Spaced(tag);
        return SpacedColorWords.Any(c => words.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>낱말만 한 칸씩 띄워 앞뒤를 공백으로 감싼다 ("dark-skinned_female" → " dark skinned female ") — 낱말 단위로 맞춰 보는 데 쓴다.</summary>
    private static string Spaced(string s) => " " + string.Join(' ', WordRun.Matches(s).Select(m => m.Value)) + " ";

    /// <summary>표지 이미지를 ComfyUI WD14에 태워 색 태그를 뽑는다. ComfyUI가 꺼져 있으면 켠다.</summary>
    public static async Task<string> ExtractAsync(AppSettings settings, string imagePath,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("표지 이미지를 찾을 수 없어요.", imagePath);

        await using var server = new ComfyServer(settings);
        await server.EnsureRunningAsync(status, ct);
        var client = new ComfyClient(server.BaseUrl);

        status?.Report("표지에서 색 태그 뽑는 중...");
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        var up = await client.UploadImageAsync(bytes, $"nm_ref_{Guid.NewGuid():N}.png", ct);
        var pid = await client.QueuePromptAsync(WorkflowBuilder.BuildTagExtract(settings, up), ct);
        var outDir = Path.Combine(settings.ComfyRoot, "ComfyUI", "output");
        var r = await client.WaitAsync(pid, outDir, TimeSpan.FromMinutes(5), ct);

        var raw = r.Texts.FirstOrDefault() ?? "";
        return FilterColorTags(raw);
    }
}
