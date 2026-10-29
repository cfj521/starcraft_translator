using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Auth;
public static class JwtClaims
{
    public sealed record Claims(long? Exp, string AccountId, string PlanType, string Email);

    public static Claims Parse(string jwt)
    {
        // 从服务器/本地缓存来的 token 可能畸形（缺段、非 base64、非 JSON）：一律降级成空 claims，
        // 让调用方按"需要刷新/未登录"处理，而不是 FormatException 打崩调用链。
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return new Claims(null, "", "", "");
            var json = Encoding.UTF8.GetString(FromB64Url(parts[1]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            long? exp = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var ev) ? ev : null;
            string acc = "", plan = "", email = "";
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth))
            {
                acc = auth.TryGetProperty("chatgpt_account_id", out var a) ? a.GetString() ?? "" : "";
                plan = auth.TryGetProperty("chatgpt_plan_type", out var p) ? p.GetString() ?? "" : "";
            }
            if (root.TryGetProperty("https://api.openai.com/profile", out var prof) && prof.TryGetProperty("email", out var em))
                email = em.GetString() ?? "";
            else if (root.TryGetProperty("email", out var em2)) email = em2.GetString() ?? "";
            return new Claims(exp, acc, plan, email);
        }
        catch (FormatException) { return new Claims(null, "", "", ""); }
        catch (JsonException) { return new Claims(null, "", "", ""); }
        catch (ArgumentException) { return new Claims(null, "", "", ""); }
    }

    public static bool NeedsRefresh(long? exp, DateTimeOffset now, int marginSeconds = 300)
    {
        if (exp is null) return true;
        return exp.Value - now.ToUnixTimeSeconds() < marginSeconds;
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
