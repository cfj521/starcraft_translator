using System.Security.Cryptography;
using System.Text;
namespace ScrTranslator.Core.Translation.Auth;
public static class OAuthPkce
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string AuthBase = "https://auth.openai.com";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string Scope = "openid profile email offline_access";
    public const string Originator = "codex_cli_rs";

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string verifier, string challenge) GenPkce()
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string BuildAuthorizeUrl(string challenge, string state)
    {
        string E(string s) => Uri.EscapeDataString(s);
        var q = $"response_type=code&client_id={E(ClientId)}&redirect_uri={E(RedirectUri)}" +
                $"&scope={E(Scope)}&code_challenge={E(challenge)}&code_challenge_method=S256" +
                $"&state={E(state)}&originator={E(Originator)}";
        return $"{AuthBase}/oauth/authorize?{q}";
    }
}
