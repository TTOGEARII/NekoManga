using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Engine;

public interface ITranslator
{
    /// <summary>원문 목록을 같은 순서의 번역문 목록으로.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct);
}

/// <summary>구글 웹 번역 (무료 엔드포인트, 키 불필요). 기본 엔진.</summary>
public class GoogleTranslator : ITranslator
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        return c;
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var results = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) { results.Add(""); continue; }
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
                      $"&sl={sourceLang}&tl={targetLang}&q={Uri.EscapeDataString(text)}";
            var json = await Http.GetStringAsync(url, ct);
            // 응답: [[[번역문, 원문, ...], [조각2...]], ...]
            var root = JsonNode.Parse(json);
            var sb = new System.Text.StringBuilder();
            if (root?[0] is JsonArray segs)
                foreach (var seg in segs)
                    if (seg?[0] != null) sb.Append(seg[0]!.GetValue<string>());
            results.Add(sb.ToString().Trim());
            await Task.Delay(120, ct); // 무료 엔드포인트 — 과속 금지
        }
        return results;
    }
}

/// <summary>DeepL API (키 필요, 키가 :fx로 끝나면 무료 호스트).</summary>
public class DeepLTranslator(string apiKey) : ITranslator
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var host = apiKey.TrimEnd().EndsWith(":fx") ? "api-free.deepl.com" : "api.deepl.com";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate");
        req.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");
        req.Content = JsonContent.Create(new
        {
            text = texts,
            source_lang = sourceLang.ToUpperInvariant(),
            target_lang = targetLang.ToUpperInvariant(),
        });
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 주세요.");
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (root?["translations"] as JsonArray)?
            .Select(t => t?["text"]?.GetValue<string>() ?? "").ToList() ?? new List<string>();
    }
}

public static class TranslatorFactory
{
    /// <summary>설정에 따라 번역기를 만든다. 언어 코드: ja/ko/en 등.</summary>
    public static ITranslator Create(AppSettings s) => s.TranslateEngine switch
    {
        "DeepL" when !string.IsNullOrWhiteSpace(s.DeepLApiKey) => new DeepLTranslator(s.DeepLApiKey),
        _ => new GoogleTranslator(),
    };
}
