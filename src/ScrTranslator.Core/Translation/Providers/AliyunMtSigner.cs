using System.Security.Cryptography;
using System.Text;

namespace ScrTranslator.Core.Translation.Providers;

/// <summary>阿里云 RPC（签名机制 1.0）的 HMAC-SHA1 签名（纯计算、不联网，便于单测）。
/// 参考 https://help.aliyun.com/zh/sdk/developer-reference/signature-mechanism 。
/// 给定参数表 + AccessKeySecret，产出要拼进 URL 的 Signature。</summary>
public static class AliyunMtSigner
{
    public const string Host = "mt.aliyuncs.com";
    public const string Region = "cn-shanghai";

    /// <summary>对参数表签名，返回含 Signature 的完整参数表（已排序）。Timestamp/SignatureNonce 由本方法生成。</summary>
    public static Dictionary<string, string> Sign(Dictionary<string, string> inputParams, string accessKeyId, string accessKeySecret)
    {
        var p = new Dictionary<string, string>(inputParams)
        {
            ["AccessKeyId"] = accessKeyId,
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureVersion"] = "1.0",
            ["Format"] = "JSON",
            ["RegionId"] = Region,
            ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
            ["Timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };
        string canonical = Canonicalize(p);
        string stringToSign = "POST&%2F&" + PercentEncode(canonical);
        p["Signature"] = ComputeSignature(stringToSign, accessKeySecret + "&");
        return p;
    }

    /// <summary>按 key 字典序拼 canonicalized query string（每个 key/value 都做阿里云式百分号编码）。</summary>
    public static string Canonicalize(IDictionary<string, string> p)
    {
        var parts = new List<string>();
        foreach (var kv in p.OrderBy(x => x.Key, StringComparer.Ordinal))
            parts.Add(PercentEncode(kv.Key) + "=" + PercentEncode(kv.Value));
        return string.Join("&", parts);
    }

    public static string ComputeSignature(string stringToSign, string key)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
    }

    /// <summary>阿里云的百分号编码：RFC3986 基础上，`+`→%20（标准库给 %2B）、`%7E`→~ 两处特殊处理。</summary>
    public static string PercentEncode(string s) =>
        Uri.EscapeDataString(s).Replace("%2B", "%20").Replace("%7E", "~");
}
