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
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"chat/completions {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
