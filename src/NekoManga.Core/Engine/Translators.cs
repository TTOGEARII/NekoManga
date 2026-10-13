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

/// <summary>OpenAI 호환 chat completions API를 쓰는 LLM 번역기 (DeepSeek·Grok·기타 호환 엔드포인트).
/// 만화 맥락을 살리려 한 페이지의 말풍선을 번호로 묶어 한 번에 번역하고 번호로 되돌린다.</summary>
public class LlmTranslator(string endpoint, string apiKey, string model, string engineName) : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var nonEmpty = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).ToList();
        if (nonEmpty.Count == 0) return texts.Select(_ => "").ToList();

        var srcName = LangName(sourceLang);
        var tgtName = LangName(targetLang);
        var numbered = string.Join("\n", nonEmpty.Select((x, k) => $"[{k + 1}] {x.t.Replace("\n", " ")}"));
        var sys = $"You are a professional manga/comic translator. Translate each numbered line from {srcName} to {tgtName}. " +
                  "Keep the natural, casual tone of manga dialogue. Output ONLY the translations, each prefixed with its number in [n] form, one per line. Do not add notes.";

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = sys },
                new JsonObject { ["role"] = "user", ["content"] = numbered }),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{engineName} 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 주세요.");

        var content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var parsed = ParseNumbered(content, nonEmpty.Count);

        // 번호 매칭을 원래 인덱스에 되돌린다
        var result = texts.Select(_ => "").ToArray();
        for (int k = 0; k < nonEmpty.Count; k++)
            result[nonEmpty[k].i] = string.IsNullOrWhiteSpace(parsed[k]) ? nonEmpty[k].t : parsed[k];
        return result;
    }

    private static string[] ParseNumbered(string content, int count)
    {
        var outp = new string[count];
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\[?(\d+)\]?[\.\)\s]+(.*)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= count)
                outp[n - 1] = m.Groups[2].Value.Trim();
        }
        return outp;
    }

    private static string LangName(string code) => code switch
    {
        "ja" => "Japanese", "ko" => "Korean", "en" => "English", "zh" => "Chinese", _ => code,
    };
}

public static class TranslatorFactory
{
    // OpenAI 호환 엔드포인트·기본 모델 (2026-10 공식 문서 기준, 설정에서 변경 가능)
    public const string DeepSeekEndpoint = "https://api.deepseek.com/chat/completions";
    /// <summary>저렴하고 빠른 기본 모델. (구 deepseek-chat은 폐기됨)</summary>
    public const string DeepSeekDefaultModel = "deepseek-flash";
    public const string GrokEndpoint = "https://api.x.ai/v1/chat/completions";
    /// <summary>번역엔 저비용 모델로 충분하다. 더 좋은 품질은 grok-4.7.</summary>
    public const string GrokDefaultModel = "grok-4.3";

    /// <summary>설정에 따라 번역기를 만든다. 언어 코드: ja/ko/en 등.</summary>
    public static ITranslator Create(AppSettings s) => s.TranslateEngine switch
    {
        "DeepL" when !string.IsNullOrWhiteSpace(s.DeepLApiKey) => new DeepLTranslator(s.DeepLApiKey),
        "DeepSeek" when !string.IsNullOrWhiteSpace(s.DeepSeekApiKey) =>
            new LlmTranslator(DeepSeekEndpoint, s.DeepSeekApiKey,
                Model(s.DeepSeekModel, DeepSeekDefaultModel), "DeepSeek"),
        "Grok" when !string.IsNullOrWhiteSpace(s.GrokApiKey) =>
            new LlmTranslator(GrokEndpoint, s.GrokApiKey,
                Model(s.GrokModel, GrokDefaultModel), "Grok"),
        _ => new GoogleTranslator(),
    };

    private static string Model(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}
