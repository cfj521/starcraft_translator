namespace ScrTranslator.Core.Translation;
public interface ITokenSource
{
    Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct);
}
