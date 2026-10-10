using System.Security.Cryptography;
using System.Text;

namespace ScrTranslator.Core.Translation.Providers;

/// <summary>腾讯云 API 的 TC3-HMAC-SHA256 签名（纯计算、不联网，便于单测）。
/// 算法参考 https://cloud.tencent.com/document/product/595/35482 。
/// 给定 secretId/secretKey/时间戳/请求体，产出要放进 Authorization 头的签名串。</summary>
public static class TencentMtSigner
{
    public const string Host = "tmt.tencentcloud.com";
    public const string Service = "tmt";
    public const string Action = "TranslateText";
    public const string Version = "2019-03-11";

    /// <summary>返回 (authorization, timestamp, dateUtc)。调用方把这三个塞进请求头即可。</summary>
    public static (string Authorization, string Timestamp, string Date) Sign(
        string secretId, string secretKey, string payload, long timestamp)
    {
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
        string credentialScope = $"{date}/{Service}/tc3_request";

        // 1) 规范请求
        const string signedHeaders = "content-type:application/json\nhost:" + Host;
        string hashedPayload = Sha256Hex(payload);
        string canonicalRequest = "POST\n/\n\n" + signedHeaders + "\n" + hashedPayload;

        // 2) 签名字符串
        string stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        // 3) 计算签名
        byte[] secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        byte[] secretService = HmacSha256(secretDate, Service);
        byte[] secretSigning = HmacSha256(secretService, "tc3_request");
        string signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();

        string authorization =
            $"TC3-HMAC-SHA256 Credential={secretId}/{credentialScope}, " +
            $"SignedHeaders=content-type;host, Signature={signature}";
        return (authorization, timestamp.ToString(), date);
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static byte[] HmacSha256(byte[] key, string text)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
}
