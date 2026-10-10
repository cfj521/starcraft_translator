namespace ScrTranslator.Core.Translation.Providers;
public sealed class ChatGptSubscriptionProvider : ITranslationProvider
{
    private readonly CodexResponsesClient _client; private readonly ITokenSource _tokens; private readonly Func<string> _model;
    public ChatGptSubscriptionProvider(CodexResponsesClient client, ITokenSource tokens, Func<string> modelGetter)
    { _client = client; _tokens = tokens; _model = modelGetter; }
    public string Name => "chatgpt";
    public ProviderStatus GetStatus() =>
        _tokens.IsLoggedIn()
            ? ProviderStatus.Ok()
            : ProviderStatus.NoCreds("未登录 ChatGPT 订阅");
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (token, account) = await _tokens.GetAsync(ct);
        var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx), BatchProtocol.BuildUser(lines), token, account, ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
}
