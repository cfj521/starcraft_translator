using System.Text;
using ScrTranslator.Core.Translation.Auth;
namespace ScrTranslator.Core.Tests.Translation;
public class JwtClaimsTests
{
    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+','-').Replace('/','_');
    [Fact]
    public void Parse_ReadsExpAccountEmail()
    {
        var payload = "{\"exp\":1782600454,\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"ACC\",\"chatgpt_plan_type\":\"plus\"},\"https://api.openai.com/profile\":{\"email\":\"a@b.com\"}}";
        var jwt = "h." + B64Url(payload) + ".s";
        var c = JwtClaims.Parse(jwt);
        Assert.Equal(1782600454, c.Exp);
        Assert.Equal("ACC", c.AccountId);
        Assert.Equal("plus", c.PlanType);
        Assert.Equal("a@b.com", c.Email);
    }
    [Fact]
    public void NeedsRefresh_WithinMargin_True()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Assert.True(JwtClaims.NeedsRefresh(1200, now, 300));   // 剩 200s < 300
        Assert.False(JwtClaims.NeedsRefresh(2000, now, 300));  // 剩 1000s
        Assert.True(JwtClaims.NeedsRefresh(null, now, 300));
    }
}
