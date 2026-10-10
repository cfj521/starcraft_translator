namespace ScrTranslator.Core.Translation.Providers;
public sealed class DashScopeProvider : ITranslationProvider
{
    private readonly OpenAiCompatibleChatClient _client;
    private readonly Func<string> _model;
    private readonly Func<string> _key; // 供 GetStatus 判断 key 是否已配（不联网）
    public DashScopeProvider(OpenAiCompatibleChatClient client, Func<string> modelGetter, Func<string> keyGetter)
    { _client = client; _model = modelGetter; _key = keyGetter; }
    public string Name => "dashscope";
    public ProviderStatus GetStatus() =>
        string.IsNullOrWhiteSpace(_key())
            ? ProviderStatus.NoCreds("未配置阿里云 DashScope API Key")
            : ProviderStatus.Ok();
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx), BatchProtocol.BuildUser(lines), ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
}
