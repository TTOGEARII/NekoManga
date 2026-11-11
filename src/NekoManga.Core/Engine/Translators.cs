using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
            var json = await Http.GetStringAsync(RequestUrl(text, sourceLang, targetLang), ct);
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

    /// <summary>말풍선 하나를 번역해 달라는 요청 주소. 언어 코드는 <see cref="RequestCode"/>를 거친다.</summary>
    internal static string RequestUrl(string text, string sourceLang, string targetLang) =>
        "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
        $"&sl={RequestCode(sourceLang)}&tl={RequestCode(targetLang)}&q={Uri.EscapeDataString(text)}";

    /// <summary>Google에 보낼 언어 코드. 설정값을 그대로 보내되, 무료 엔드포인트가 못 알아듣는 세 글자 코드(ISO 639-2/3)만 받는 두 글자 코드로 바꾼다:
    /// zho·chi·cmn → zh-CN, jpn → ja, kor → ko, eng → en (앞뒤 공백·대소문자는 가리지 않는다). 그 밖의 코드는 한 글자도 바꾸지 않는다 — zh-TW는 zh-TW 그대로 간다.
    /// <para>실측(2026-11-11, 원문 "おはようございます"): tl=zho·chi·kor·eng·jpn 은 오류 없이 전부 영어("good morning")로 답했고, sl=jpn 은 원문을 못 알아봐
    /// "はよござい마스" 같은 깨진 답을 냈다. 뒷손질(<see cref="LangCode"/>)은 zho·kor 을 중국어·한국어로 알아보므로, 그대로 보내면 영어 번역문을 중국어 규칙으로 손질하게 된다.
    /// cmn 은 지금도 중국어(간체)로 답하지만(zh-CN 과 같은 답) 같은 표에 넣어 zh-CN 으로 보낸다.</para>
    /// LLM 번역기가 못 준 줄을 메우는 Google 호출도 이 번역기를 거치므로 같이 적용된다. DeepL·LLM 요청은 바꾸지 않는다.</summary>
    internal static string RequestCode(string code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "zho" or "chi" or "cmn" => "zh-CN",
        "jpn" => "ja",
        "kor" => "ko",
        "eng" => "en",
        _ => code!,
    };
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
            // 설정값을 대문자로만 바꿔 그대로 보낸다. DeepL 문서의 중국어 목표 코드는 ZH·ZH-HANS·ZH-HANT 뿐이다 — zh-CN·zh-TW·zho 같은 꼴은
            // 문서에 없다(거절되는지 다른 언어로 답하는지는 API 키가 없어 확인하지 못했다). 여기서는 코드를 고치지 않는다.
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
/// 만화 맥락을 살리려 한 페이지의 말풍선을 번호로 묶어 한 번에 번역하고 번호로 되돌린다.
///
/// 추론(thinking) 모델 주의 — DeepSeek v4-pro 같은 모델은 답하기 전에 추론 토큰을 쓰는데 그것도 max_tokens에
/// 포함된다. 상한이 작으면 추론만 하다 끊겨(finish_reason=length) 본문이 빈 채로 돌아오고, 그러면 번역문 자리에
/// 원문이 그대로 들어간다 (실측: 8줄 1536토큰 상한 → reasoning_tokens 1536, content "" → 06·10·13·22장 전부 미번역).
/// 대책: ① DeepSeek은 thinking을 끈다 (번역엔 불필요, 20초→1.6초) ② 상한을 넉넉히 ③ 그래도 비면 상한을 키워 한 번 더
/// ④ 끝내 못 받은 줄은 Google로 메운다 — 원문을 그대로 두는 것보다 낫다.</summary>
public class LlmTranslator(string endpoint, string apiKey, string model, string engineName, bool disableThinking = false) : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var nonEmpty = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).ToList();
        if (nonEmpty.Count == 0) return texts.Select(_ => "").ToList();

        var srcName = LangName(sourceLang);
        var tgtName = LangName(targetLang);
        var numbered = string.Join("\n", nonEmpty.Select((x, k) => $"[{k + 1}] {x.t.Replace("\n", " ")}"));
        var sys = $"You are a professional manga/comic translator. Translate each numbered line from {srcName} to {tgtName}. " +
                  "Keep the natural, casual tone of manga dialogue. Lines are consecutive speech bubbles of one page, so use the others as context. " +
                  $"Write the output entirely in {tgtName}: never leave {srcName} characters such as kana, kanji or the prolonged-sound mark ー in it (render stretched sounds with ~ or {tgtName} letters). " +
                  "Output ONLY the translations, each prefixed with its number in [n] form, one per line. Do not add notes.";

        // 본문은 길어야 수백 토큰이지만 추론 모델은 추론 토큰까지 여기서 쓴다 — 넉넉히 잡고 모자라면 키워서 한 번 더
        int maxTokens = Math.Clamp(1024 + nonEmpty.Count * 200, 2048, 8192);
        bool thinkingOff = disableThinking;
        string[] parsed = new string[nonEmpty.Count];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["temperature"] = 0.3,
                ["max_tokens"] = maxTokens,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = sys },
                    new JsonObject { ["role"] = "user", ["content"] = numbered }),
            };
            if (thinkingOff) body["thinking"] = new JsonObject { ["type"] = "disabled" };

            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                resp = await Http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                // 일시적 네트워크 오류("An error occurred while sending the request", 실측 25장 중 1장) — 잠깐 쉬고 한 번 더
                await Task.Delay(2000, ct);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient 타임아웃(150초)은 TaskCanceledException으로 와서 사용자 취소와 구분이 안 된다 — 그대로 던지면
                // 러너가 '멈춤'으로 보고 작업 전체를 조용히 끝낸다. 한 번 더 보내고, 또 넘기면 이 페이지는 Google로 메운다.
                if (attempt == 0) continue;
                break;
            }
            using var _ = resp;
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // thinking 파라미터를 모르는 모델/엔드포인트면 그것 없이 한 번 더
                if (thinkingOff && (int)resp.StatusCode == 400) { thinkingOff = false; continue; }
                throw new InvalidOperationException($"{engineName} 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 주세요.");
            }

            var choice = JsonNode.Parse(text)?["choices"]?[0];
            var content = choice?["message"]?["content"]?.GetValue<string>() ?? "";
            var finish = choice?["finish_reason"]?.GetValue<string>() ?? "";
            parsed = ParseNumbered(content, nonEmpty.Count);
            int got = parsed.Count(p => !string.IsNullOrWhiteSpace(p));
            if (got == nonEmpty.Count) break;
            // 추론만 하다 끊겼거나 본문이 모자라다 — 상한을 키워 한 번 더 (최대 32k, 모델이 거부하면 400 → 예외)
            if (finish == "length" && maxTokens < 32768) { maxTokens = Math.Min(32768, maxTokens * 4); continue; }
            break;
        }

        // 번호 매칭을 원래 인덱스에 되돌린다. LLM이 못 준 줄은 Google로 메운다(실패하면 원문).
        var result = texts.Select(_ => "").ToArray();
        var missing = new List<int>();
        for (int k = 0; k < nonEmpty.Count; k++)
        {
            if (string.IsNullOrWhiteSpace(parsed[k])) missing.Add(k);
            else result[nonEmpty[k].i] = parsed[k];
        }
        if (missing.Count > 0)
        {
            IReadOnlyList<string> fb = Array.Empty<string>();
            try { fb = await new GoogleTranslator().TranslateAsync(missing.Select(k => nonEmpty[k].t).ToList(), sourceLang, targetLang, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }   // Google 타임아웃·오류 — 원문으로 둔다
            for (int m = 0; m < missing.Count; m++)
            {
                var k = missing[m];
                var v = m < fb.Count ? fb[m] : "";
                result[nonEmpty[k].i] = string.IsNullOrWhiteSpace(v) ? nonEmpty[k].t : v;
            }
        }
        return result;
    }

    private static readonly Regex ThinkTag = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);
    private static readonly Regex NumberedLine = new(@"^(?:\[(\d+)\]|(\d+)[\.\):])\s*(.*)$", RegexOptions.Compiled);

    /// <summary>"[n] 번역" 줄을 번호대로 배열에. 일부 모델은 본문에 &lt;think&gt; 블록을 끼우거나 여러 줄을 한 줄에 이어
    /// 붙이므로(" [1] … [2] …") 그것도 받아 준다. 번호 없는 줄은 무시 — 숫자로 시작하는 번역문("3명이…")을 번호로 오인하지 않게
    /// 대괄호 또는 구분 기호(. ) :)를 요구한다.</summary>
    internal static string[] ParseNumbered(string content, int count)
    {
        var outp = new string[count];
        content = ThinkTag.Replace(content ?? "", "");
        foreach (var raw in Regex.Split(content, @"\n|(?=\[\d+\]\s)"))
        {
            var line = raw.Trim();
            var m = NumberedLine.Match(line);
            if (!m.Success) continue;
            var num = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(num, out int n) && n >= 1 && n <= count && string.IsNullOrEmpty(outp[n - 1]))
                outp[n - 1] = m.Groups[3].Value.Trim();
        }
        return outp;
    }

    // 프롬프트에 넣을 이름일 뿐 언어 판정(LangCode)이 아니다 — zh-TW·zh-Hans 같은 꼴은 코드 그대로 넘어가 간체·번체 구분이 프롬프트에 남는다
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
        // DeepSeek: thinking 끔 — 번역엔 추론이 필요 없고, 켜 두면 추론 토큰이 max_tokens를 먹어 본문이 비는 사고가 난다 (v4-pro 실측)
        "DeepSeek" when !string.IsNullOrWhiteSpace(s.DeepSeekApiKey) =>
            new LlmTranslator(DeepSeekEndpoint, s.DeepSeekApiKey,
                Model(s.DeepSeekModel, DeepSeekDefaultModel), "DeepSeek", disableThinking: true),
        "Grok" when !string.IsNullOrWhiteSpace(s.GrokApiKey) =>
            new LlmTranslator(GrokEndpoint, s.GrokApiKey,
                Model(s.GrokModel, GrokDefaultModel), "Grok"),
        _ => new GoogleTranslator(),
    };

    private static string Model(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    /// <summary>진행 상태에 보여 줄 엔진 이름 (키가 없어 Google로 떨어지면 Google).</summary>
    public static string EngineLabel(AppSettings s) => s.TranslateEngine switch
    {
        "DeepL" when !string.IsNullOrWhiteSpace(s.DeepLApiKey) => "DeepL",
        "DeepSeek" when !string.IsNullOrWhiteSpace(s.DeepSeekApiKey) => "DeepSeek " + Model(s.DeepSeekModel, DeepSeekDefaultModel),
        "Grok" when !string.IsNullOrWhiteSpace(s.GrokApiKey) => "Grok " + Model(s.GrokModel, GrokDefaultModel),
        _ => "Google",
    };
}

/// <summary>설정의 언어 코드가 어느 언어인지 — 판정은 여기 한 군데서만 한다. 번역문 뒷손질(PolishTranslation)과 원문 판정(LooksLikeSource)이
/// 중국어를 서로 다른 규칙으로 알아보던 것을 모았다. 코드는 settings.json 값 그대로 온다(설정 창에는 ja / ko·en뿐이지만 손으로 고치면 무엇이든 온다).
/// **여기서 고른 값은 판정에만 쓴다 — 번역기에 보내는 코드는 번역기마다 따로 정한다.** 번역기마다 받는 꼴이 달라서다(2026-11-11 확인):
/// Google은 zh·zh-CN·zh_CN·zh-TW·zh-Hans·zh-Hant·cmn 을 대소문자 가리지 않고 받지만 zho·chi 같은 세 글자 코드는 조용히 영어로 답한다(kor·eng·jpn도 같다) —
/// 그래서 Google로 보낼 때만 그 세 글자 코드를 두 글자 코드로 바꾼다(<see cref="GoogleTranslator.RequestCode"/>). 나머지는 설정값 그대로 보낸다.
/// DeepL 문서의 중국어 목표는 ZH·ZH-HANS·ZH-HANT 뿐이다. Primary의 결과를 번역기에 그대로 넘기면 zh-TW(번체)가 zh(간체)로 바뀐다.</summary>
internal static class LangCode
{
    /// <summary>지역·문자 꼬리표를 뗀 언어 부분을 소문자 두 글자 코드로. 구분자는 -와 _ 둘 다, ISO 639-2/3의 세 글자 코드도 받는다:
    /// zh-CN·ZH_tw·zh-Hans·zho·chi·cmn → "zh", ja-JP·jpn → "ja", ko-KR·kor → "ko", en-US·eng → "en". 모르는 코드는 꼬리표만 뗀 소문자 그대로.</summary>
    internal static string Primary(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        int cut = c.IndexOfAny(Separators);
        if (cut >= 0) c = c[..cut];
        return c switch
        {
            "zho" or "chi" or "cmn" => "zh",   // 중국어(639-2 T·B), 표준 중국어(639-3)
            "jpn" => "ja",
            "kor" => "ko",
            "eng" => "en",
            _ => c,
        };
    }

    private static readonly char[] Separators = ['-', '_'];

    /// <summary>중국어인가 — 간체·번체, 지역을 가리지 않는다(문장부호와 글자 집합은 같다).</summary>
    internal static bool IsChinese(string? code) => Primary(code) == "zh";

    internal static bool IsJapanese(string? code) => Primary(code) == "ja";
}
