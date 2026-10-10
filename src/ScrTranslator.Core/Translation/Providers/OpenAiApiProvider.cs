namespace ScrTranslator.Core.Translation.Providers;

/// <summary>OpenAI LLM（API Key 模式）：标准 sk-... 密钥，走官方 api.openai.com/v1。
/// 与 ChatGptSubscriptionProvider（订阅/OAuth）并列，是可独立启用/排序的池成员。
/// 复用 OpenAiCompatibleChatClient（与 DashScope 同款，仅 baseUrl 与密钥不同）。</summary>
public sealed class OpenAiApiProvider : ITranslationProvider
{
    private readonly OpenAiCompatibleChatClient _client;
    private readonly Func<string> _model;
    private readonly Func<string> _key; // 供 GetStatus 判断 key 是否已配（不联网）
    private readonly RateLimitCooldown _cooldown = new(); // 429 限流冷却（成功请求自动复位）
    public OpenAiApiProvider(OpenAiCompatibleChatClient client, Func<string> modelGetter, Func<string> keyGetter)
    { _client = client; _model = modelGetter; _key = keyGetter; }
    public string Name => "openai_api";
    public ProviderStatus GetStatus()
    {
        // 凭据优先报（没 key 时谈限流没意义）；key 齐但正处 429 冷却期 → RateLimited，编排器跳过、不发请求。
        if (string.IsNullOrWhiteSpace(_key())) return ProviderStatus.NoCreds("未配置 OpenAI API Key");
        if (_cooldown.Active) return ProviderStatus.RateLimited($"限流冷却中（至 {_cooldown.Until:HH:mm:ss}）");
        return ProviderStatus.Ok();
    }
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        try
        {
            var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx), BatchProtocol.BuildUser(lines), ct);
            _cooldown.Reset(); // 成功：清冷却、退避归零
            return BatchProtocol.Parse(reply, lines.Count) ?? lines;
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
