using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Providers;
public sealed class CodexResponsesClient
{
    private const string Url = "https://chatgpt.com/backend-api/codex/responses";
    private readonly HttpClient _http;
    public CodexResponsesClient(HttpClient http) => _http = http;

    public async Task<string> CompleteAsync(string model, string system, string user,
        string accessToken, string accountId, CancellationToken ct)
    {
        var payload = new
        {
            model,
            instructions = system,
            input = new object[] { new { role = "user", content = new object[] { new { type = "input_text", text = user } } } },
            stream = true,
            store = false
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);
        req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new RateLimitException($"codex/responses 429: {err}", resp.Headers.RetryAfter?.Delta);
            throw new HttpRequestException($"codex/responses {(int)resp.StatusCode}: {err}");
        }
        var sb = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "response.output_text.delta" && doc.RootElement.TryGetProperty("delta", out var d))
                    sb.Append(d.GetString());
                else if (type == "response.completed") break;
            }
            catch (JsonException) { /* 忽略非 JSON 心跳行 */ }
        }
        return sb.ToString();
    }
}
