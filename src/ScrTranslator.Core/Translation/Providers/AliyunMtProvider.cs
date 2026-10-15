using System.Text;
using System.Text.Json;

namespace ScrTranslator.Core.Translation.Providers;

/// <summary>阿里云机器翻译（免费 100 万字符/月）。RPC 签名，批量翻译（以换行拼接、按行拆回）。
/// 额度耗尽时自标记 <see cref="ProviderState.QuotaExhausted"/>（冷却到次日），编排器随即跳到下一个。</summary>
public sealed class AliyunMtProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Func<(string AccessKeyId, string AccessKeySecret)> _creds;
    private DateTime _quotaExhaustedUntil;
    private readonly object _gate = new();

    public AliyunMtProvider(HttpClient http, Func<(string, string)> creds) { _http = http; _creds = creds; }

    public string Name => "aliyun";
    public EngineKind Kind => EngineKind.MT;

    public ProviderStatus GetStatus()
    {
        var (id, key) = _creds();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key))
            return ProviderStatus.NoCreds("未配置阿里云 AccessKeyId/AccessKeySecret");
        lock (_gate) if (_quotaExhaustedUntil > DateTime.UtcNow)
            return ProviderStatus.Exhausted($"阿里云免费额度已用尽，冷却至 {_quotaExhaustedUntil:MM-dd HH:mm}");
        return ProviderStatus.Ok();
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (id, key) = _creds();
        var source = string.Join("\n", lines); // 阿里云 TranslateGeneral 单文本；换行分隔、按行拆回
        var signed = AliyunMtSigner.Sign(new Dictionary<string, string>
        {
            ["Action"] = "TranslateGeneral",
            ["SourceText"] = source,
            ["SourceLanguage"] = "ko",
            ["TargetLanguage"] = "zh",
            ["FormatType"] = "text",
            ["Scene"] = "general",
        }, id, key);

        string query = string.Join("&", signed.Select(kv => $"{AliyunMtSigner.PercentEncode(kv.Key)}={AliyunMtSigner.PercentEncode(kv.Value)}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{AliyunMtSigner.Host}/?{query}")
        { Content = new StringContent("", Encoding.UTF8) };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (LooksLikeQuotaExhausted(body)) { MarkExhausted(); throw new ProviderQuotaExhaustedException("阿里云免费额度已用尽"); }
            throw new HttpRequestException($"阿里云 Translate {(int)resp.StatusCode}: {Trunc(body)}");
        }
        var translated = ParseResponse(body);
        if (translated is null) return lines;
        var split = translated.Split('\n');
        return split.Length == lines.Count ? split : lines; // 行数对不上 → 整批回落原文
    }

    /// <summary>解析成功响应的 Data.Translated 字段（RPC TranslateGeneral 返回）。异常/缺失返回 null。</summary>
    internal static string? ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("Data").GetProperty("Translated").GetString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>阿里云额度耗尽的典型返回：Code 含 QuotaExhausted / LimitExceeded / 消息含"免费额度"。</summary>
    internal static bool LooksLikeQuotaExhausted(string body)
    {
        string b = body.ToLowerInvariant();
        return b.Contains("quotaexhausted") || b.Contains("limitexceeded") || body.Contains("免费额度");
    }

    private void MarkExhausted()
    {
        lock (_gate) _quotaExhaustedUntil = DateTime.UtcNow.Date.AddDays(1).AddHours(8);
    }
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
