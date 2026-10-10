using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScrTranslator.App.Secrets;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Auth;

namespace ScrTranslator.App.Auth;

public sealed class OpenAiOAuth : ITokenSource
{
    private const string TokenUrl = OAuthPkce.AuthBase + "/oauth/token";
    private readonly SecretStore _store; private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public OpenAiOAuth(SecretStore store, HttpClient http) { _store = store; _http = http; }

    public (bool loggedIn, string email, string plan) Status()
    {
        var t = _store.Load().OpenAi;
        return (!string.IsNullOrEmpty(t.AccessToken), t.Email, t.PlanType);
    }
    public bool IsLoggedIn() => Status().loggedIn;
    public void Logout() { var d = _store.Load(); d.OpenAi = new OpenAiTokens(); _store.Save(d); }

    public async Task<(string, string)> GetAsync(CancellationToken ct)
    {
        var data = _store.Load();
        var t = data.OpenAi;
        if (string.IsNullOrEmpty(t.AccessToken)) throw new InvalidOperationException("未登录 ChatGPT 订阅");
        long? exp = DateTimeOffset.TryParse(t.ExpiresAt, out var dt) ? dt.ToUnixTimeSeconds() : null;
        if (JwtClaims.NeedsRefresh(exp, DateTimeOffset.UtcNow))
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                data = _store.Load(); t = data.OpenAi;
                exp = DateTimeOffset.TryParse(t.ExpiresAt, out var dt2) ? dt2.ToUnixTimeSeconds() : null;
                if (JwtClaims.NeedsRefresh(exp, DateTimeOffset.UtcNow))
                {
                    if (string.IsNullOrEmpty(t.RefreshToken)) throw new InvalidOperationException("缺少 refresh_token，请重新登录");
                    var tok = await PostToken(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = OAuthPkce.ClientId,
                        ["refresh_token"] = t.RefreshToken,
                        ["scope"] = OAuthPkce.Scope,
                    }, ct);
                    ApplyTokens(data, tok); _store.Save(data); t = data.OpenAi;
                }
            }
            finally { _refreshLock.Release(); }
        }
        return (t.AccessToken, t.AccountId);
    }

    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkce.GenPkce();
        var state = Guid.NewGuid().ToString("N");
        // 双栈：TcpListener.Create 绑 IPv6Any 且 DualMode，同时收 IPv4/IPv6。
        // 必须双栈——RedirectUri 用 localhost，Windows 上常解析为 ::1，单绑 127.0.0.1 会被拒。
        using var listener = TcpListener.Create(1455);
        try { listener.Start(); } catch { return false; } // 端口占用
        Process.Start(new ProcessStartInfo(OAuthPkce.BuildAuthorizeUrl(challenge, state)) { UseShellExecute = true });
        try
        {
            using var reg = ct.Register(listener.Stop);
            using var accept = await listener.AcceptTcpClientAsync(ct);
            var (code, gotState) = await ReadCallback(accept, ct);
            if (code is null || gotState != state) return false;
            var tok = await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthPkce.ClientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthPkce.RedirectUri,
                ["code_verifier"] = verifier,
            }, ct);
            var data = _store.Load(); ApplyTokens(data, tok); _store.Save(data);
            return true;
        }
        catch { return false; }
        finally { listener.Stop(); }
    }

    private async Task<JsonElement> PostToken(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"oauth/token {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void ApplyTokens(SecretData data, JsonElement tok)
    {
        var t = data.OpenAi;
        t.AccessToken = tok.GetProperty("access_token").GetString() ?? "";
        if (tok.TryGetProperty("refresh_token", out var r)) t.RefreshToken = r.GetString() ?? t.RefreshToken;
        if (tok.TryGetProperty("id_token", out var i)) t.IdToken = i.GetString() ?? t.IdToken;
        var c = JwtClaims.Parse(t.AccessToken);
        t.AccountId = string.IsNullOrEmpty(c.AccountId) ? t.AccountId : c.AccountId;
        t.PlanType = string.IsNullOrEmpty(c.PlanType) ? t.PlanType : c.PlanType;
        t.Email = string.IsNullOrEmpty(c.Email) ? t.Email : c.Email;
        if (c.Exp is { } exp) t.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).ToString("o");
    }

    private static async Task<(string? code, string? state)> ReadCallback(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var buf = new byte[4096];
        int n = await stream.ReadAsync(buf, ct);
        var reqLine = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0]; // GET /auth/callback?code=..&state=.. HTTP/1.1
        string? code = null, state = null;
        var qIdx = reqLine.IndexOf('?');
        if (qIdx >= 0)
        {
            var path = reqLine[(qIdx + 1)..].Split(' ')[0];
            foreach (var kv in path.Split('&'))
            {
                var p = kv.Split('=', 2);
                if (p.Length == 2 && p[0] == "code") code = Uri.UnescapeDataString(p[1]);
                if (p.Length == 2 && p[0] == "state") state = Uri.UnescapeDataString(p[1]);
            }
        }
        var html = "<html><body><h3>登录完成，可关闭本页。</h3></body></html>";
        var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n" + html;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
        return (code, state);
    }
}
