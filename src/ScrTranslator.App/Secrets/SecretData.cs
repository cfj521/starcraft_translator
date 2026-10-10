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
    public string OpenAiApiKey { get; set; } = ""; // OpenAI API Key 模式（sk-...），与订阅 OAuth 的 OpenAi tokens 分开存
    public OpenAiTokens OpenAi { get; set; } = new();
    public AliyunSecrets Aliyun { get; set; } = new();
}
