namespace ScrTranslator.Core.Translation.Providers;
public sealed class ChatGptSubscriptionProvider : ITranslationProvider
{
    private readonly CodexResponsesClient _client; private readonly ITokenSource _tokens; private readonly Func<string> _model;
    private readonly RateLimitCooldown _cooldown = new(); // 429 限流冷却（成功请求自动复位）
    public ChatGptSubscriptionProvider(CodexResponsesClient client, ITokenSource tokens, Func<string> modelGetter)
    { _client = client; _tokens = tokens; _model = modelGetter; }
    public string Name => "chatgpt";
    public EngineKind Kind => EngineKind.LLM;
    public ProviderStatus GetStatus()
    {
        // 凭据优先报（没登录时谈限流没意义）；凭据齐但正处 429 冷却期 → RateLimited，编排器跳过、不发请求。
        if (!_tokens.IsLoggedIn()) return ProviderStatus.NoCreds("未登录 ChatGPT 订阅");
        if (_cooldown.Active) return ProviderStatus.RateLimited($"限流冷却中（至 {_cooldown.Until:HH:mm:ss}）");
        return ProviderStatus.Ok();
    }
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        try
        {
            var (token, account) = await _tokens.GetAsync(ct);
            // 只翻韩文保护：非韩文串（英文/数字/标点）换 ⟨N⟩ 占位符，只翻韩文；收译文后还原，丢了回退原文。
            var (protectedLines, tokens) = KoreanOnlyProtector.Protect(lines);
            var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx, KoreanOnlyProtector.HasTokens(tokens)), BatchProtocol.BuildUser(protectedLines), token, account, ct);
            _cooldown.Reset(); // 成功：清冷却、退避归零
            return KoreanOnlyProtector.ParseAndRestore(reply, lines, protectedLines, tokens) ?? lines;
        }
        catch (RateLimitException ex)
        {
            var delay = _cooldown.Enter(ex.RetryAfter);
            // 重抛触发编排器回退链（本批落到下一个 provider），并把冷却信息带进异常消息——
            // 编排器/探测的日志会记下"限流冷却 X 秒，至 HH:mm:ss"，不用另开日志通道。
            throw new RateLimitException($"{ex.Message}（冷却 {delay.TotalSeconds:0.#}s，至 {_cooldown.Until:HH:mm:ss}）", ex.RetryAfter, ex);
        }
    }
}
