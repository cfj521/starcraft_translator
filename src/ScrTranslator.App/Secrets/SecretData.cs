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

public sealed class SecretData
{
    public string DashScopeApiKey { get; set; } = "";
    public OpenAiTokens OpenAi { get; set; } = new();
}
