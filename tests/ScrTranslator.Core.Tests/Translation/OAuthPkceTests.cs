using System.Security.Cryptography;
using System.Text;
using ScrTranslator.Core.Translation.Auth;
namespace ScrTranslator.Core.Tests.Translation;
public class OAuthPkceTests
{
    [Fact]
    public void GenPkce_ChallengeIsSha256OfVerifier()
    {
        var (verifier, challenge) = OAuthPkce.GenPkce();
        var expected = Base64UrlNoPad(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
        Assert.True(verifier.Length >= 43);
    }
    [Fact]
    public void AuthorizeUrl_HasParamsAndEncodesSpaceAs20()
    {
        var url = OAuthPkce.BuildAuthorizeUrl("CH", "ST");
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?", url);
        Assert.Contains("code_challenge=CH", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=ST", url);
        Assert.Contains("scope=openid%20profile%20email%20offline_access", url);
        Assert.DoesNotContain("+", url);
    }
    private static string Base64UrlNoPad(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
}
