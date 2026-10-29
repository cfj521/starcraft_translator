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
    private DateTime _throttledUntil;   // QPS/并发限流（秒级~分钟级），与"月度免费额度耗尽"完全两回事
    private readonly object _gate = new();

    public AliyunMtProvider(HttpClient http, Func<(string, string)> creds) { _http = http; _creds = creds; }

    public string Name => "aliyun";
    public EngineKind Kind => EngineKind.MT;

    public ProviderStatus GetStatus()
    {
        var (id, key) = _creds();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key))
            return ProviderStatus.NoCreds("未配置阿里云 AccessKeyId/AccessKeySecret");
        lock (_gate)
        {
            if (_quotaExhaustedUntil > DateTime.UtcNow)
                return ProviderStatus.Exhausted($"阿里云免费额度已用尽，冷却至 {_quotaExhaustedUntil.ToLocalTime():MM-dd HH:mm}");
            if (_throttledUntil > DateTime.UtcNow)
                return ProviderStatus.RateLimited($"阿里云限流中，{Math.Ceiling((_throttledUntil - DateTime.UtcNow).TotalSeconds):0} 秒后重试");
        }
        return ProviderStatus.Ok();
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (id, key) = _creds();
        var source = string.Join("\n", lines); // 阿里云 TranslateGeneral 单文本；换行分隔、按行拆回
        var signed = AliyunMtSigner.Sign(new Dictionary<string, string>
        {
            // Version 是阿里云 RPC 必带的公共参数（API 版本，区别于 Sign 里加的 SignatureVersion=1.0 签名算法版本）。
            // 缺 Version 网关无法路由到具体 API，报 InvalidAction.NotFound（404 "Specified api is not found"）。
            ["Version"] = "2018-10-12",
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
            if (LooksLikeThrottled(body)) { MarkThrottled(); throw new HttpRequestException($"阿里云限流: {Trunc(body)}"); }
            if (LooksLikeQuotaExhausted(body)) { MarkExhausted(); throw new ProviderQuotaExhaustedException("阿里云免费额度已用尽"); }
            throw new HttpRequestException($"阿里云 Translate {(int)resp.StatusCode}: {Trunc(body)}");
        }
        var translated = ParseResponse(body);
        if (translated is null) return lines;
        // 尾部的空行只是排版噪声（多数接口末尾会补一个换行）：先去掉再分行，
        // 否则 split.Length 比行数多 1 → 整批回落原文（表现为"永远不出中文、每拍重发请求"）。
        var split = translated.TrimEnd('\r', '\n').Split('\n');
        return split.Length == lines.Count
            ? split.Select(s => s.TrimEnd('\r')).ToArray()
            : lines; // 行数对不上 → 整批回落原文
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

    /// <summary>阿里云额度耗尽的典型返回：Code 含 QuotaExhausted / 消息含"免费额度"。
    /// **不含** LimitExceeded / Throttling——那是 QPS 限流（见 <see cref="LooksLikeThrottled"/>），
    /// 把它当"月度额度耗尽"会让 1 秒的限流换来十几小时的停机。</summary>
    public static bool LooksLikeQuotaExhausted(string body)
    {
        string b = body.ToLowerInvariant();
        return b.Contains("quotaexhausted") || body.Contains("免费额度");
    }

    /// <summary>QPS/并发限流：秒级~分钟级可恢复，冷却几十秒即可，不消耗月度额度语义。</summary>
    public static bool LooksLikeThrottled(string body)
    {
        string b = body.ToLowerInvariant();
        return b.Contains("limitexceeded") || b.Contains("throttling") || b.Contains("toomanyrequests")
            || b.Contains("flowcontrol") || b.Contains("限流");
    }

    private void MarkExhausted()
    {
        // 阿里云免费额度按**北京时间 00:00** 重置。旧实现用 `UtcNow.Date.AddDays(1).AddHours(8)`
        // （= 次日 08:00 UTC = 次日 16:00 北京时间），比真实重置时刻晚 16 小时，
        // 最坏情况（北京时间下午耗尽）会多停摆近 32 小时。
        lock (_gate) _quotaExhaustedUntil = NextBeijingResetUtc(DateTime.UtcNow);
    }

    /// <summary>下一次阿里云免费额度重置时刻（北京时间 00:00）对应的 UTC 时刻。
    /// 独立出来便于单测：纯函数，无状态。</summary>
    public static DateTime NextBeijingResetUtc(DateTime nowUtc)
    {
        DateTime beijingNow = nowUtc.AddHours(8);   // 已平移，只用于取"北京日期"
        DateTime nextBeijingMidnight = beijingNow.Date.AddDays(1);
        // 平移回 UTC：SpecifyKind 保证不受本机时区影响（DateTime 的 Kind 不参与算术）
        return DateTime.SpecifyKind(nextBeijingMidnight, DateTimeKind.Utc).AddHours(-8);
    }

    private void MarkThrottled()
    {
        lock (_gate) _throttledUntil = DateTime.UtcNow.AddSeconds(30);
    }
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
