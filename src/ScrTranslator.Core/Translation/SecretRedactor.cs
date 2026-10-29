using System.Text.RegularExpressions;

namespace ScrTranslator.Core.Translation;

/// <summary>
/// 在错误消息写日志/上屏前擦掉疑似密钥片段。
/// 为什么需要：provider 的异常消息里直接拼了 HTTP 响应体（诊断需要），而 OpenAI 系接口在
/// 401/429 的错误体里会回显**部分** key（如 <c>Incorrect API key provided: sk-ab...cd</c>），
/// Aliyun 的回显里也可能带 AccessKeyId。这些消息经 <c>onError</c> 直通日志页与 debug 面板，
/// 用户截图上报时就把密钥指纹一起漏出去了。这里只做**保守**擦除：命中已知形状才替换，
/// 不改变消息长度以外的语义。
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>擦除常见密钥形状：<c>sk-</c> 前缀、Bearer 头、URL/表单里的 key/token/secret 参数。</summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        string s = message;

        // sk-xxx（OpenAI / DashScope / 兼容端点）：保留前 4 位便于用户核对是不是自己那把
        s = SkPattern().Replace(s, m =>
        {
            string body = m.Groups["body"].Value;
            return body.Length <= 6 ? "sk-***" : $"sk-{body[..4]}***";
        });

        // Authorization: Bearer xxx
        s = BearerPattern().Replace(s, "Bearer ***");

        // JSON / 查询串里的敏感键
        s = KeyValuePattern().Replace(s, m =>
            $"{m.Groups["k"].Value}{m.Groups["sep"].Value}***");

        return s;
    }

    [GeneratedRegex(@"sk-(?<body>[A-Za-z0-9_\-]{4,})", RegexOptions.Compiled)]
    private static partial Regex SkPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("""(?i)["']?\b(?<k>api[_-]?key|access[_-]?key(?:[_-]?id)?|access[_-]?token|refresh[_-]?token|client[_-]?secret|secret|password|authorization)["']?\s*(?<sep>["']?\s*[:=]\s*["']?)(?<v>[^"',\s}&]{6,})""", RegexOptions.Compiled)]
    private static partial Regex KeyValuePattern();
}
