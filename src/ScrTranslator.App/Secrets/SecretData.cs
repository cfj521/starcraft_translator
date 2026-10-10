namespace ScrTranslator.App.Secrets;

public sealed class OpenAiTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ExpiresAt { get; set; } = "";   // ISO8601
    public string PlanType { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>腾讯云机器翻译凭据（TC3-HMAC-SHA256 签名用）。免费 500 万字符/月。</summary>
public sealed class TencentSecrets
{
    public string SecretId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool IsSet => !string.IsNullOrWhiteSpace(SecretId) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>阿里云机器翻译凭据（RPC 签名用）。免费 100 万字符/月。</summary>
public sealed class AliyunSecrets
{
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";
    public bool IsSet => !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(AccessKeySecret);
}

public sealed class SecretData
{
    public string DashScopeApiKey { get; set; } = "";
    public OpenAiTokens OpenAi { get; set; } = new();
    public TencentSecrets Tencent { get; set; } = new();
    public AliyunSecrets Aliyun { get; set; } = new();
}
