using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScrTranslator.App.Logging;
using ScrTranslator.App.Secrets;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Auth;

namespace ScrTranslator.App.Auth;

/// <summary>登录结果：Success=授权完成；Cancelled=用户关闭授权页/等待超时（无回调，非硬错误）；Failed=端口占用/网络等真失败。</summary>
public enum LoginResult { Success, Cancelled, Failed }

public sealed class OpenAiOAuth : ITokenSource
{
    private const string TokenUrl = OAuthPkce.AuthBase + "/oauth/token";
    private readonly SecretStore _store; private readonly HttpClient _http;
    private readonly FileLogger? _log;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public OpenAiOAuth(SecretStore store, HttpClient http, FileLogger? log = null)
        { _store = store; _http = http; _log = log; }

    public (bool loggedIn, string email, string plan) Status()
    {
        var t = _store.Load().OpenAi;
        return (!string.IsNullOrEmpty(t.AccessToken), t.Email, t.PlanType);
    }
    public bool IsLoggedIn() => Status().loggedIn;
    public void Logout()
    {
        // Update 在 store 锁内读改写：避免与后台 token 刷新/其它密钥窗口并发时互相覆盖。
        _store.Update(d => d.OpenAi = new OpenAiTokens());
        _log?.Info("[app][oauth] 已登出 ChatGPT（清空本地 token）");
    }

    public async Task<(string, string)> GetAsync(CancellationToken ct)
    {
        // 只在锁内取"是否需要刷新"的快照，真正刷新时再在 store 锁内读改写（见下）。
        var snapshot = _store.Load().OpenAi;
        if (string.IsNullOrEmpty(snapshot.AccessToken)) throw new InvalidOperationException("未登录 ChatGPT 订阅");
        long? exp = DateTimeOffset.TryParse(snapshot.ExpiresAt, out var dt) ? dt.ToUnixTimeSeconds() : null;
        if (JwtClaims.NeedsRefresh(exp, DateTimeOffset.UtcNow))
        {
            _log?.Debug("[app][oauth] access_token 需刷新，走 refresh_token 流程");
            await _refreshLock.WaitAsync(ct);
            try
            {
                // 复检：持锁期间别的调用可能已刷好，别重复刷（refresh_token 会被轮换，重复用旧的直接掉登录态）。
                var data = _store.Load();
                var t = data.OpenAi;
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
                    // 只回写 OpenAi 这一段：不拿整个旧快照覆盖别的线程刚写进去的密钥。
                    _store.Update(d => ApplyTokens(d, tok));
                    t = _store.Load().OpenAi;
                    _log?.Info("[app][oauth] refresh_token 刷新成功");
                }
                return (t.AccessToken, t.AccountId);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[app][oauth] token 刷新失败: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally { _refreshLock.Release(); }
        }
        return (snapshot.AccessToken, snapshot.AccountId);
    }

    public async Task<LoginResult> LoginAsync(CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkce.GenPkce();
        var state = Guid.NewGuid().ToString("N");
        // 只绑回环地址（127.0.0.1 与 ::1 各一个）：RedirectUri 是 localhost，Windows 上可能解析为任一。
        // 旧实现 TcpListener.Create(1455) 实际绑的是 IPv6Any + DualMode = **所有网卡**：
        // 同网段任意主机可抢先连上回调端口占掉唯一的那次 Accept → 用户登录必然失败（且防火墙会弹允许框）。
        // 双 listener 也是为了不改 RedirectUri（改地址要同步改 OAuth 客户端注册）。
        var v4 = new TcpListener(IPAddress.Loopback, 1455);
        TcpListener? v6 = null;
        try
        {
            v4.Start();
            try { v6 = new TcpListener(IPAddress.IPv6Loopback, 1455); v6.Start(); } catch { v6 = null; } // 无 IPv6 栈时只留 v4
        }
        catch (Exception ex)
        {
            v4.Stop(); v6?.Stop();
            _log?.Error($"[app][oauth] 本地回调端口 1455 启动失败（可能被占用）: {ex.Message}");
            return LoginResult.Failed;
        }
        using var stopV4 = v4; // 用 using 保证退出时释放监听端口
        using var stopV6 = v6; // 可能为 null（无 IPv6 栈），using 对 null 是安全的
        _log?.Debug($"[app][oauth] 已打开授权页 state={state}，等待浏览器回调 localhost:1455");
        try
        {
            Process.Start(new ProcessStartInfo(OAuthPkce.BuildAuthorizeUrl(challenge, state)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 无默认浏览器等情况：按"登录失败"处理，别让异常逃出 LoginAsync 破坏返回契约。
            _log?.Error($"[app][oauth] 打开授权页失败: {ex.GetType().Name}: {ex.Message}");
            return LoginResult.Failed;
        }
        try
        {
            using var reg = ct.Register(() => { v4.Stop(); v6?.Stop(); });
            var acceptTasks = new List<Task<TcpClient>> { v4.AcceptTcpClientAsync(ct).AsTask() };
            if (v6 is not null) acceptTasks.Add(v6.AcceptTcpClientAsync(ct).AsTask());
            using var accept = await await Task.WhenAny(acceptTasks); // 两个回环地址谁先连上都行
            _log?.Debug("[app][oauth] 收到浏览器回调");
            var (code, gotState) = await ReadCallback(accept, ct);
            if (code is null || gotState != state)
            {
                _log?.Warn($"[app][oauth] 回调校验失败：code={(code is null ? "空" : "有")} state 匹配={(gotState == state)}（可能是过期/重复回调或被拦截）");
                return LoginResult.Failed;
            }
            var tok = await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthPkce.ClientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthPkce.RedirectUri,
                ["code_verifier"] = verifier,
            }, ct);
            // Update 在 store 锁内只改 OpenAi 段：不拿整个旧快照覆盖并发写入的其它密钥。
            _store.Update(d => ApplyTokens(d, tok));
            _log?.Debug($"[app][oauth] token 交换成功，邮箱={_store.Load().OpenAi.Email}");
            return LoginResult.Success;
        }
        catch (OperationCanceledException) { return LoginResult.Cancelled; } // 用户直接关了授权页（无回调）或等待超时，非硬错误
        catch (Exception ex) { _log?.Error($"[app][oauth] 登录失败（回调读取/token 交换异常）: {ex.GetType().Name}: {ex.Message}"); return LoginResult.Failed; }
        finally { v4.Stop(); v6?.Stop(); }
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
