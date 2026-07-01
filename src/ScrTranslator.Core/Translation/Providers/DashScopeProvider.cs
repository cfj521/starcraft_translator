namespace ScrTranslator.Core.Translation.Providers;
public sealed class DashScopeProvider : ITranslationProvider
{
    private readonly OpenAiCompatibleChatClient _client; private readonly Func<string> _model;
    public DashScopeProvider(OpenAiCompatibleChatClient client, Func<string> modelGetter) { _client = client; _model = modelGetter; }
    public string Name => "dashscope";
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var reply = await _client.CompleteAsync(_model(), BatchProtocol.SystemPrompt, BatchProtocol.BuildUser(lines), ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
}
