using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Providers;
public sealed class OpenAiCompatibleChatClient
{
    private readonly HttpClient _http; private readonly string _base; private readonly Func<string> _key;
    public OpenAiCompatibleChatClient(HttpClient http, string baseUrl, Func<string> apiKeyGetter)
    { _http = http; _base = baseUrl.TrimEnd('/'); _key = apiKeyGetter; }

    public async Task<string> CompleteAsync(string model, string system, string user, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            stream = false
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/chat/completions")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key());
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new RateLimitException($"chat/completions 429: {body}", RateLimitException.FromHeader(resp.Headers.RetryAfter));
            throw new HttpRequestException($"chat/completions {(int)resp.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        // choices 为空数组（内容过滤/服务端异常）→ 直接给清晰错误，别在 [0] 上抛 IndexOutOfRange（错误文案会误导成"翻译失败"）。
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"chat/completions 返回无 choices（可能被内容过滤）：{body}");
        var first = choices[0];
        if (!first.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
            throw new HttpRequestException($"chat/completions 返回缺少 message.content：{body}");
        return content.GetString() ?? "";
    }
}
