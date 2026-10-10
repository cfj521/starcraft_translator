using System.Text;
using System.Text.Json;

namespace ScrTranslator.Core.Translation.Providers;

/// <summary>腾讯云机器翻译（免费 500 万字符/月）。TC3-HMAC-SHA256 签名，批量翻译。
/// 额度耗尽时本 provider 自标记 <see cref="ProviderState.QuotaExhausted"/>（冷却到次日），编排器随即跳到下一个。</summary>
public sealed class TencentMtProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Func<(string SecretId, string SecretKey)> _creds;
    private DateTime _quotaExhaustedUntil; // 0 = 未标记耗尽
    private readonly object _gate = new();

    public TencentMtProvider(HttpClient http, Func<(string, string)> creds) { _http = http; _creds = creds; }

    public string Name => "tencent";

    public ProviderStatus GetStatus()
    {
        var (id, key) = _creds();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key))
            return ProviderStatus.NoCreds("未配置腾讯云 SecretId/SecretKey");
        lock (_gate) if (_quotaExhaustedUntil > DateTime.UtcNow)
            return ProviderStatus.Exhausted($"腾讯云免费额度已用尽，冷却至 {_quotaExhaustedUntil:MM-dd HH:mm}");
        return ProviderStatus.Ok();
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (id, key) = _creds();
        var payload = JsonSerializer.Serialize(new
        {
            SourceTextList = lines,
            Source = "ko",
            Target = "zh",
            ProjectId = 0
        });
        var (auth, ts, _) = TencentMtSigner.Sign(id, key, payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{TencentMtSigner.Host}/")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.TryAddWithoutValidation("Host", TencentMtSigner.Host);
        req.Headers.TryAddWithoutValidation("X-TC-Action", TencentMtSigner.Action);
        req.Headers.TryAddWithoutValidation("X-TC-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-TC-Version", TencentMtSigner.Version);
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (LooksLikeQuotaExhausted(body)) { MarkExhausted(); throw new ProviderQuotaExhaustedException("腾讯云免费额度已用尽"); }
            throw new HttpRequestException($"腾讯云 TranslateText {(int)resp.StatusCode}: {Trunc(body)}");
        }
        return ParseResponse(body, lines.Count) ?? lines;
    }

    /// <summary>解析成功响应的 TargetTextList。行数不匹配返回 null（整批回落原文）。</summary>
    internal static IReadOnlyList<string>? ParseResponse(string body, int expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var resp = doc.RootElement.GetProperty("Response");
            if (!resp.TryGetProperty("TargetTextList", out var list)) return null;
            var res = new string[list.GetArrayLength()];
            for (int i = 0; i < res.Length; i++) res[i] = list[i].GetString() ?? "";
            return res.Length == expected ? res : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>腾讯云额度耗尽的典型返回：Error.Code 含 LimitExceeded / 消息含"免费额度""quota"。</summary>
    internal static bool LooksLikeQuotaExhausted(string body)
    {
        string b = body.ToLowerInvariant();
        return b.Contains("quota") || b.Contains("limitexceeded") || body.Contains("免费额度");
    }

    private void MarkExhausted()
    {
        lock (_gate) _quotaExhaustedUntil = DateTime.UtcNow.Date.AddDays(1).AddHours(8); // 冷却到次日（UTC 8 点 ≈ 北京零点，月额度按月重置）
    }
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>provider 判定免费额度用尽时抛出。编排器据此跳到下一个 provider（其它异常也回退，但只有它会标记冷却）。</summary>
public sealed class ProviderQuotaExhaustedException : Exception
{
    public ProviderQuotaExhaustedException(string msg) : base(msg) { }
}
