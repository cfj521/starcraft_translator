namespace ScrTranslator.Core.Translation;
public interface ITokenSource
{
    Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct);
    /// <summary>不抛异常的可用性检查：是否已登录且持有 token。供 provider 池的 GetStatus 用。</summary>
    bool IsLoggedIn();
}
