using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NekoManga.Core.Engine;

public interface ITranslator
{
    /// <summary>원문 목록을 같은 순서의 번역문 목록으로.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct);

    /// <summary>용어집·앞 쪽 문맥·효과음 여부(<see cref="TranslateOptions"/>)를 같이 넘기는 꼴(2026-11-26) — 따로 받지 않는 번역기는 옵션 없는 쪽으로 간다.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, TranslateOptions options, CancellationToken ct)
        => TranslateAsync(texts, sourceLang, targetLang, ct);

    /// <summary>같은 글을 다시 물으면 다른 답이 올 수 있는가(LLM) — 검증에 걸린 줄을 다시 묻는 것은 그런 번역기뿐이다(Google·DeepL은 같은 답이 온다).</summary>
    bool AnswersVary => false;
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

    /// <summary>용어집은 원문 치환으로 쓴다(지시문을 줄 수 없다 — Glossary.ApplySource). 문맥·효과음 지시는 쓰지 못한다.</summary>
    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, TranslateOptions options, CancellationToken ct)
        => TranslateAsync(texts.Select(t => Glossary.ApplySource(t, options.Glossary)).ToList(), sourceLang, targetLang, ct);

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
            throw new InvalidOperationException(Loc.F($"DeepL 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 줘냥."));
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (root?["translations"] as JsonArray)?
            .Select(t => t?["text"]?.GetValue<string>() ?? "").ToList() ?? new List<string>();
    }

    /// <summary>용어집은 원문 치환으로(Google과 같다). DeepL의 용어집 API는 쓰지 않는다 — 키마다 용어집을 올려 두는 절차가 더 든다.</summary>
    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, TranslateOptions options, CancellationToken ct)
        => TranslateAsync(texts.Select(t => Glossary.ApplySource(t, options.Glossary)).ToList(), sourceLang, targetLang, ct);
}

/// <summary>지시문과 글을 보내면 글로 답하는 모델(OpenAI 호환 chat) — 번역 말고 다른 일을 물을 때 쓴다(화 전체 검수 <see cref="Pipeline.ChapterReview"/>).
/// <see cref="LlmTranslator"/>가 구현한다(DeepSeek·Grok). 번역만 하는 번역기(Google·DeepL)는 구현하지 않는다 — 그 번역기를 고른 설정에서는 이 일을 할 수 없다고 화면이 알린다.
/// 못 답하면 던진다(HTTP 오류·제한 시간·끊긴 답) — 반쯤 받은 답을 돌려주지 않는다.</summary>
public interface IChatModel
{
    Task<string> AskAsync(string system, string user, int maxTokens, CancellationToken ct);
}

/// <summary>OpenAI 호환 chat completions API를 쓰는 LLM 번역기 (DeepSeek·Grok·기타 호환 엔드포인트).
/// 만화 맥락을 살리려 한 페이지의 말풍선을 번호로 묶어 한 번에 번역하고 번호로 되돌린다.
///
/// 추론(thinking) 모델 주의 — DeepSeek v4-pro 같은 모델은 답하기 전에 추론 토큰을 쓰는데 그것도 max_tokens에
/// 포함된다. 상한이 작으면 추론만 하다 끊겨(finish_reason=length) 본문이 빈 채로 돌아오고, 그러면 번역문 자리에
/// 원문이 그대로 들어간다 (실측: 8줄 1536토큰 상한 → reasoning_tokens 1536, content "" → 06·10·13·22장 전부 미번역).
/// 대책: ① DeepSeek은 thinking을 끈다 (번역엔 불필요, 20초→1.6초) ② 상한을 넉넉히 ③ 그래도 비면 상한을 키워 한 번 더
/// ④ 끝내 못 받은 줄은 Google로 메운다 — 원문을 그대로 두는 것보다 낫다.</summary>
public class LlmTranslator(string endpoint, string apiKey, string model, string engineName, bool disableThinking = false) : ITranslator, IChatModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };
    /// <summary>화 전체 검수용 — 한 화(수백 줄)를 한 번에 보내고 고친 줄을 받는다. 쪽 하나의 번역보다 답이 늦다(추론 모델은 더).</summary>
    private static readonly HttpClient LongHttp = new() { Timeout = TimeSpan.FromSeconds(300) };
    /// <summary>보내는 자리를 바꿔 끼우는 이음새(시험용) — <see cref="AskAsync"/>의 오류 길(네트워크 오류·제한 시간·400·끊긴 답)을 서버 없이 본다. 비면 실제로 보낸다.</summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; init; }
    /// <summary><see cref="AskAsync"/>가 네트워크 오류 뒤 다시 보내기 전에 쉬는 시간(시험에서는 0).</summary>
    internal TimeSpan ResendWait { get; init; } = TimeSpan.FromSeconds(2);

    public bool AnswersVary => true;

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        => TranslateAsync(texts, sourceLang, targetLang, TranslateOptions.None, ct);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, TranslateOptions options, CancellationToken ct)
    {
        var nonEmpty = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).ToList();
        if (nonEmpty.Count == 0) return texts.Select(_ => "").ToList();

        var srcName = LangName(sourceLang);
        var tgtName = LangName(targetLang);
        // 풍선 자리는 줄과 같은 수로 왔을 때만 — 빈 줄을 뺀 차례에 맞춰 같이 고른다
        var places = options.Places is { } all && all.Count == texts.Count ? nonEmpty.Select(x => all[x.i]).ToList() : null;
        if (places == null && options.Places != null) options = options with { Places = null };   // 프롬프트의 자리 설명도 뺀다
        var numbered = BuildUserText(nonEmpty.Select(x => x.t).ToList(), places);
        var sys = BuildSystemPrompt(srcName, tgtName, options);

        // 본문은 길어야 수백 토큰이지만 추론 모델은 추론 토큰까지 여기서 쓴다 — 넉넉히 잡고 모자라면 키워서 한 번 더
        int maxTokens = Math.Clamp(1024 + nonEmpty.Count * 200, 2048, 8192);
        bool thinkingOff = disableThinking;
        string[] parsed = new string[nonEmpty.Count];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var body = BuildRequestBody(model, sys, numbered, maxTokens, thinkingOff, options.PageImage);

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
                // 그림을 못 받는 모델에 그림을 보냈다 — 그림 없이 조용히 다시 보내지 않는다(고른 대로 안 된 것을 사용자가 알아야 한다)
                if (options.PageImage != null && (int)resp.StatusCode == 400)
                    throw new InvalidOperationException(Loc.F($"{engineName}가 쪽 그림을 받지 못했다냥 (HTTP 400) — 그림을 읽는 모델이 아니면 설정 → 번역 지침에서 '쪽 그림도 같이 보낸다'를 꺼 줘냥."));
                throw new InvalidOperationException(Loc.F($"{engineName} 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 줘냥."));
            }

            var choice = JsonNode.Parse(text)?["choices"]?[0];
            var content = choice?["message"]?["content"]?.GetValue<string>() ?? "";
            var finish = choice?["finish_reason"]?.GetValue<string>() ?? "";
            parsed = ParseNumbered(content, nonEmpty.Count);
            // 진단: NEKO_TRANSLATE_DBG=<파일>이면 보낸 지시문·글과 받은 답을 그대로 적는다 — 프롬프트를 고칠 때 번역기가 무엇을 보고 무엇을 냈는지 본다
            if (Environment.GetEnvironmentVariable("NEKO_TRANSLATE_DBG") is { Length: > 0 } dbg)
                try { File.AppendAllText(dbg, $"=== {engineName} {model} (attempt {attempt}, finish {finish})\n--- system\n{sys}\n--- user\n{numbered}\n--- answer\n{content}\n\n"); } catch { }   // loc:none
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
            try { fb = await new GoogleTranslator().TranslateAsync(missing.Select(k => nonEmpty[k].t).ToList(), sourceLang, targetLang, options, ct); }
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

    /// <summary>지시문과 글을 그대로 보내고 답의 본문을 돌려준다(<see cref="IChatModel"/> — 화 전체 검수). 번역 요청과 달리 다른 번역기로 넘어가지 않는다:
    /// 네트워크 오류는 한 번 다시 보내고, thinking 파라미터를 모르는 모델이면 그것 없이 다시 보내고, 답이 길이로 끊기면 상한을 한 번 키워 다시 묻는다 —
    /// 그래도 끊기면 받은 데까지 쓰지 않고 던진다(끊긴 마지막 줄이 번역으로 들어가지 않게). 제한 시간(300초)을 넘기면 <see cref="TimeoutException"/>(사용자 취소와 가른다).</summary>
    public async Task<string> AskAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        bool thinkingOff = disableThinking, resent = false, grown = false;
        while (true)
        {
            var body = BuildRequestBody(model, system, user, maxTokens, thinkingOff, null);
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                resp = SendOverride != null ? await SendOverride(req, ct) : await LongHttp.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (!resent)
            {
                resent = true;
                await Task.Delay(ResendWait, ct);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.F($"{engineName}의 답이 너무 늦다냥 — 잠시 뒤에 다시 해 줘냥."));
            }
            using var _ = resp;
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (thinkingOff && (int)resp.StatusCode == 400) { thinkingOff = false; continue; }
                throw new InvalidOperationException(Loc.F($"{engineName} 오류 (HTTP {(int)resp.StatusCode}) — 설정의 API 키를 확인해 줘냥."));
            }
            var choice = JsonNode.Parse(text)?["choices"]?[0];
            var content = choice?["message"]?["content"]?.GetValue<string>() ?? "";
            var finish = choice?["finish_reason"]?.GetValue<string>() ?? "";
            if (Environment.GetEnvironmentVariable("NEKO_TRANSLATE_DBG") is { Length: > 0 } dbg)
                try { File.AppendAllText(dbg, $"=== {engineName} {model} (ask, max {maxTokens}, finish {finish})\n--- system\n{system}\n--- user\n{user}\n--- answer\n{content}\n\n"); } catch { }   // loc:none
            if (finish == "length")
            {
                if (!grown && maxTokens < 32768) { grown = true; maxTokens = Math.Min(32768, maxTokens * 4); continue; }
                throw new InvalidOperationException(Loc.F($"{engineName}의 답이 너무 길어 끝까지 받지 못했다냥."));
            }
            return content;
        }
    }

    private static readonly Regex ThinkTag = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);
    private static readonly Regex NumberedLine = new(@"^(?:\[(\d+)\]|(\d+)[\.\):])\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex PlaceTag = new(@"^\s*<\d{1,3}\s*,\s*\d{1,3}>\s*", RegexOptions.Compiled);

    /// <summary>번역할 줄을 번호를 붙여 한 덩어리로 — "[n] 글". 풍선 자리(<paramref name="places"/>)가 줄 수만큼 있으면 "[n] &lt;x,y&gt; 글"(수가 다르면 붙이지 않는다 — 엉뚱한 줄에 엉뚱한 자리가 붙는 것보다 없는 것이 낫다).</summary>
    internal static string BuildUserText(IReadOnlyList<string> lines, IReadOnlyList<(int X, int Y)>? places)
    {
        bool tagged = places != null && places.Count == lines.Count;
        return string.Join("\n", lines.Select((t, k) => tagged ? $"[{k + 1}] <{places![k].X},{places[k].Y}> {t.Replace("\n", " ")}" : $"[{k + 1}] {t.Replace("\n", " ")}"));
    }

    /// <summary>요청 본문(OpenAI 호환 chat completions). 그림이 없으면 전과 같은 꼴(user의 content가 문자열), 있으면 [글, 그림(JPEG data URL)] 두 조각 — 그림을 읽는 모델만 받는다.</summary>
    internal static JsonObject BuildRequestBody(string model, string system, string userText, int maxTokens, bool thinkingOff, byte[]? pageImage)
    {
        JsonNode user = pageImage == null
            ? JsonValue.Create(userText)
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = userText },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(pageImage) } });
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (thinkingOff) body["thinking"] = new JsonObject { ["type"] = "disabled" };
        return body;
    }

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
                outp[n - 1] = PlaceTag.Replace(m.Groups[3].Value, "").Trim();   // 줄 앞에 붙여 보낸 풍선 자리를 따라 적은 답도 받는다
        }
        return outp;
    }

    /// <summary>시스템 프롬프트(2026-11-26 용어집·문맥·효과음 — CarrotMangaTranslator 참고). 대사는 전과 같은 지시 + 용어집 표 + 앞 쪽 대사(참고용, 출력 금지).
    /// 효과음(<see cref="TranslateOptions.Sfx"/>)은 짧은 의성어·의태어로(<see cref="TranslationCheck.SfxMaxChars"/>자 안, 설명·따옴표 없이) — 대사용 "한 쪽의 연속한 풍선" 문장은 뺀다.</summary>
    internal static string BuildSystemPrompt(string srcName, string tgtName, TranslateOptions o)
    {
        var sb = new System.Text.StringBuilder();
        if (o.Sfx)
        {
            sb.Append($"You are a professional manga translator. Each numbered line is a sound effect or onomatopoeia drawn on the artwork, in {srcName}. ")
              .Append($"Translate each into the {tgtName} sound-effect expression a manga letterer would use: short (at most {TranslationCheck.SfxMaxChars} characters), no explanation, no quotation marks. ")
              .Append($"Write the output entirely in {tgtName}: never leave {srcName} characters such as kana, kanji or the prolonged-sound mark ー in it (render stretched sounds with ~ or {tgtName} letters). ")
              .Append("Output ONLY the translations, each prefixed with its number in [n] form, one per line. Do not add notes.");
        }
        else
        {
            // 대사(3단계, 2026-11-27 — Carrot의 지침: 화 전체 문맥으로, 공손함·위계·인물 말투를 지킨다). 전의 프롬프트("Keep the natural, casual tone … use the others as context")로는
            // 풍선을 넘어 이어지는 문장을 줄마다 따로 옮기고("몇 번인가"/"본 적"), 감탄사를 글자대로 옮기고("왓"·"엣"), OCR이 틀린 반 토막을 그대로 옮겼다(실제 다섯 쪽)
            var g = o.Guide;
            sb.Append($"You are a professional manga translator. Translate the numbered lines from {srcName} to {tgtName}. ")
              .Append("They are the speech bubbles of one manga page, listed roughly in reading order.\n\nRules:\n")
              .Append("- A sentence often runs across consecutive bubbles. Read the whole page first, then translate so the bubbles read as one continuous, natural conversation. ")
              .Append("Keep every bubble on its own line: the same numbers and the same number of lines — never merge, split, drop or reorder lines.\n")
              .Append($"- Write natural spoken {tgtName}, the way a published {tgtName} edition of a manga reads, not a word-for-word rendering. ")
              .Append("Keep each speaker's tone, politeness level and personality consistent across the page and with the earlier pages. ")
              .Append("Match the register of each line: never make it cruder, more explicit, more formal or more polite than the original — soft or cute wording stays soft or cute.\n")
              .Append($"- Translate every word by its meaning. Never carry a {srcName} word into {tgtName} by its sound — slang, mimetic words and loanwords included; only the names of people and places, and glossary terms, keep their sound.\n")
              .Append($"- Render interjections, gasps, cries and moans with the expressions a {tgtName} reader would actually use, not a letter-by-letter transliteration.{InterjectionExamples(tgtName)}\n")
              .Append($"- Write the output entirely in {tgtName}: never leave {srcName} characters such as kana, kanji or the prolonged-sound mark ー in it (render stretched sounds with ~ or {tgtName} letters; drop a cut-off small っ, or show the cut-off with ! or …).\n")
              .Append("- The source text was read by OCR, so a line may contain a misread character or be only a fragment. When the surrounding bubbles make the intended words clear, translate those; ")
              .Append("otherwise translate what is written as plainly as you can. Never invent new content, and never spell out a line sound by sound: for an unreadable scrap, write the short natural reaction that fits the scene.\n")
              .Append(HonorificRule(g.Honorifics, tgtName));
            if (g.Tone == "Literal") sb.Append("- Stay close to the wording of the original: do not embellish or localize beyond what the grammar needs.\n");
            if (o.Places is { Count: > 0 })
                sb.Append("- Each line starts with the bubble's position on the page as <x,y> in percent (0,0 is the top-left corner, 100,100 the bottom-right). ")
                  .Append("Manga pages are read right to left, top to bottom: use the positions to tell which bubbles belong to the same exchange. Never output the positions.\n");
            if (o.PageImage != null)
                sb.Append("- The page image is attached: use it to see who is speaking, to whom, and in what mood. Translate only the numbered lines.\n");
            sb.Append("\nOutput ONLY the translations, each prefixed with its number in [n] form, one per line. Do not add notes.");
            if (g.Characters.Count > 0)
            {
                sb.Append("\n\nCharacters (who they are and how they speak — keep it consistent):");
                foreach (var (name, voice) in g.Characters.Take(TranslateGuide.MaxCharacters)) sb.Append('\n').Append(name).Append(": ").Append(voice);
            }
            if (!string.IsNullOrWhiteSpace(g.Notes))
                sb.Append("\n\nNotes for this work (follow them):\n").Append(g.Notes.Length > TranslateGuide.MaxNotesChars ? g.Notes[..TranslateGuide.MaxNotesChars] : g.Notes);
        }
        if (o.Glossary.Count > 0)
        {
            sb.Append("\n\nGlossary (always translate these terms exactly as given):");
            foreach (var (src, tgt) in o.Glossary) sb.Append('\n').Append(src).Append(" => ").Append(tgt);
        }
        if (o.Context.Count > 0)
        {
            sb.Append("\n\nContext from the previous pages, already translated — for reference only, do not translate or output these lines:");
            foreach (var (src, tr) in o.Context) sb.Append('\n').Append(src.Replace("\n", " ")).Append(" => ").Append(tr.Replace("\n", " "));
        }
        return sb.ToString();
    }

    /// <summary>감탄사·신음을 목표 언어의 것으로 옮기라는 줄에 붙이는 보기 — 한국어·영어만(다른 언어는 보기 없이 지시만).
    /// 보기가 없을 때는 "わっ" → "왓", "えっ" → "엣", "うあ" → "우아"처럼 소리를 글자대로 옮겼다.</summary>
    private static string InterjectionExamples(string tgtName) => tgtName switch
    {
        "Korean" => " For example: えっ → 어?, わっ → 앗 or 우왓, うあ → 으앗, はぁはぁ → 하아 하아, んっ → 읏.",   // loc:none
        "English" => " For example: えっ → Huh?, わっ → Whoa!, はぁはぁ → Hah... hah..., んっ → Nngh.",
        _ => "",
    };

    // 프롬프트에 넣을 이름일 뿐 언어 판정(LangCode)이 아니다 — zh-TW·zh-Hans 같은 꼴은 코드 그대로 넘어가 간체·번체 구분이 프롬프트에 남는다
    /// <summary>이름 뒤 호칭(경칭)의 지침 한 줄 — 설정 TranslateHonorifics("Keep"·"Natural"·"Drop"). 번역과 화 전체 검수(<see cref="Pipeline.ChapterReview"/>)가 같은 글을 쓴다.</summary>
    internal static string HonorificRule(string honorifics, string tgtName) => honorifics switch
    {
        "Natural" => $"- Adapt name honorifics (-chan, -kun, -san, -sama, senpai …) to the way natural {tgtName} addresses the person: drop them where a {tgtName} speaker would just use the name, and use native terms of address where they fit.\n",
        "Drop" => "- Leave out name honorifics (-chan, -kun, -san, -sama …): use the bare name.\n",
        _ => $"- Keep name honorifics (-chan, -kun, -san, -sama, senpai …) in their customary {tgtName} form right after the name.\n",
    };

    internal static string LangName(string code) => code switch
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
