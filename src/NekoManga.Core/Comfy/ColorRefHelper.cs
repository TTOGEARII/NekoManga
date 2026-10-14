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
    ];

    // 색 단어가 들어 있어도 캐릭터 색과 무관한 태그
    private static readonly string[] StopWords =
    [
        "background", "monochrome", "greyscale", "grayscale", "theme", "border",
        "outline", "text", "speech", "sclera", "blood",
    ];

    /// <summary>IPAdapter 정밀 매칭이 가능한가 — 커스텀 노드 + SDXL IPAdapter 모델 + CLIP 비전 모델.</summary>
    public static bool IpAdapterAvailable(AppSettings s)
    {
        var root = Path.Combine(s.ComfyRoot, "ComfyUI");
        var ipaDir = Path.Combine(root, "models", "ipadapter");
        var cvDir = Path.Combine(root, "models", "clip_vision");
        return Directory.Exists(Path.Combine(root, "custom_nodes", "ComfyUI_IPAdapter_plus"))
            && Directory.Exists(ipaDir)
            && Directory.EnumerateFiles(ipaDir, "*sdxl*").Any(f => f.Contains("plus", StringComparison.OrdinalIgnoreCase))
            && Directory.Exists(cvDir)
            && Directory.EnumerateFiles(cvDir, "*.safetensors").Any();
    }

    /// <summary>WD14 출력에서 캐릭터 색 태그만 남긴다.</summary>
    public static string FilterColorTags(string wd14Output)
    {
        var picked = wd14Output.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Where(t => ColorWords.Any(c => t.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .Where(t => !StopWords.Any(sw => t.Contains(sw, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", picked);
    }

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
