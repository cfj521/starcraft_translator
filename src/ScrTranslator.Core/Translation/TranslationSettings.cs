namespace ScrTranslator.Core.Translation;

/// <summary>provider 池里的一行：用 <see cref="Id"/> 定位具体 provider，<see cref="Enabled"/> 控制是否参与选取。</summary>
public sealed record TranslationProviderEntry(string Id, bool Enabled)
{
    public const string Aliyun  = "aliyun";    // 阿里云机器翻译（免费）
    public const string ChatGpt = "chatgpt";   // OpenAI LLM（ChatGPT 订阅，OAuth 登录）
    public const string DashScope = "dashscope"; // 阿里云 LLM（DashScope）
    public const string OpenAiApi = "openai_api"; // OpenAI LLM（API Key 模式，sk-...，走官方 api.openai.com）
}

/// <summary>翻译设置。
/// <see cref="Providers"/> 是有序的 provider 池：编排器从上到下取第一个「启用 且 可用」的 provider，
/// 不可用/额度耗尽/翻译失败就跳到下一个。默认顺序＝先免费机器翻译、后 LLM。</summary>
public sealed record TranslationSettings
{
    public IReadOnlyList<TranslationProviderEntry> Providers { get; init; } = DefaultProviders;

    public string DashScopeModel { get; init; } = "qwen-flash";
    public string ChatGptModel { get; init; } = "gpt-5";
    public string OpenAiApiModel { get; init; } = "gpt-4o";
    public int TimeoutSeconds { get; init; } = 10;
    // 曾有 Active(DashScope|ChatGpt) 单选：provider 池化后废弃，由 Providers 的顺序+启用取代。
    // 旧 settings.json 里的 Active 字段反序列化时被忽略，SettingsStore 负责迁移到 Providers。
    public bool TranslateChat { get; init; } = true;
    public bool TranslateRoomName { get; init; } = true;
    public bool TranslateMapInfo { get; init; } = true;
    public bool TranslatePlayerId { get; init; } = false;
    public static TranslationSettings Default => new();

    /// <summary>默认池顺序：腾讯云(免费) → 阿里云(免费) → OpenAI LLM → 阿里云LLM。全部启用。</summary>
    public static IReadOnlyList<TranslationProviderEntry> DefaultProviders { get; } = new TranslationProviderEntry[]
    {
        new(TranslationProviderEntry.Aliyun,     true),
        new(TranslationProviderEntry.ChatGpt,    true),
        new(TranslationProviderEntry.DashScope,  true),
        new(TranslationProviderEntry.OpenAiApi,  true),
    };
}
