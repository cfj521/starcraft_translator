namespace ScrTranslator.Core.Translation.Providers;

/// <summary>自定义模型（OpenAI 兼容模式）：用户自配 baseUrl / 模型 ID / KEY，指向任意 OpenAI 兼容端点
/// （自建推理服务、第三方代理等）。与 <see cref="OpenAiApiProvider"/>（固定 api.openai.com）并列，
/// 是可独立启用/排序的池成员。KEY 走 SecretStore（DPAPI），URL/模型 ID 存 TranslationSettings。
/// baseUrl 会随用户改配置变化，故每次调用现建 <see cref="OpenAiCompatibleChatClient"/>（client 无状态，重建无成本）。</summary>
public sealed class CustomModelProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Func<string> _model;
    private readonly Func<string> _url;
    private readonly Func<string> _key; // 供 GetStatus 判断 KEY 是否已配（不联网）
    private readonly RateLimitCooldown _cooldown = new(); // 429 限流冷却（成功请求自动复位）

    public CustomModelProvider(HttpClient http, Func<string> modelGetter, Func<string> urlGetter, Func<string> keyGetter)
    { _http = http; _model = modelGetter; _url = urlGetter; _key = keyGetter; }

    public string Name => "custom_model";
    public EngineKind Kind => EngineKind.LLM;

    public ProviderStatus GetStatus()
    {
        // 凭据优先报（没 URL/KEY 时谈限流没意义）；凭据齐但正处 429 冷却期 → RateLimited，编排器跳过、不发请求。
        if (string.IsNullOrWhiteSpace(_url()) || string.IsNullOrWhiteSpace(_key()))
            return ProviderStatus.NoCreds("未配置自定义模型（URL/KEY 为空）");
        if (_cooldown.Active) return ProviderStatus.RateLimited($"限流冷却中（至 {_cooldown.Until:HH:mm:ss}）");
        return ProviderStatus.Ok();
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        // baseUrl 取自实时配置（用户可随时改），故每次现建 client，不复用单例。
        var client = new OpenAiCompatibleChatClient(_http, _url(), _key);
        try
        {
            // 只翻韩文保护：非韩文串（英文/数字/标点）换 ⟨N⟩ 占位符，只翻韩文；收译文后还原，丢了回退原文。
            var (protectedLines, tokens) = KoreanOnlyProtector.Protect(lines);
            var reply = await client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx, KoreanOnlyProtector.HasTokens(tokens)), BatchProtocol.BuildUser(protectedLines), ct);
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
