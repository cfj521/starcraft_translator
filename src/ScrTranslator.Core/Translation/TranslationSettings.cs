namespace ScrTranslator.Core.Translation;

/// <summary>provider 池里的一行：用 <see cref="Id"/> 定位具体 provider，<see cref="Enabled"/> 控制是否参与选取。</summary>
public sealed record TranslationProviderEntry(string Id, bool Enabled)
{
    public const string Aliyun  = "aliyun";    // 阿里云机器翻译（免费）
    public const string CustomModel = "custom_model"; // 自定义模型（用户自配 URL/KEY/模型，OpenAI 兼容模式）
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

    public string DashScopeModel { get; init; } = "qwen3.8-flash";
    public string ChatGptModel { get; init; } = "gpt-6-luna";
    public string OpenAiApiModel { get; init; } = "gpt-6-luna";
    // 自定义模型（OpenAI 兼容模式）：URL 与模型 ID 存这里，KEY 走 SecretStore（DPAPI）。
    // 二者为空＝未配置，GetStatus 报 NoCredentials，编排器跳过。
    public string CustomModelUrl { get; init; } = "";
    public string CustomModelId { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 10;
    // 曾有 Active(DashScope|ChatGpt) 单选：provider 池化后废弃，由 Providers 的顺序+启用取代。
    // 旧 settings.json 里的 Active 字段反序列化时被忽略，SettingsStore 负责迁移到 Providers。
    public static TranslationSettings Default => new();

    /// <summary>默认池顺序：阿里云(免费) → 自定义模型(用户自配) → OpenAI LLM(订阅) → 阿里云LLM(DashScope) → OpenAI LLM(API)。全部启用。</summary>
    public static IReadOnlyList<TranslationProviderEntry> DefaultProviders { get; } = new TranslationProviderEntry[]
    {
        new(TranslationProviderEntry.Aliyun,       true),
        new(TranslationProviderEntry.CustomModel,  true),
        new(TranslationProviderEntry.ChatGpt,      true),
        new(TranslationProviderEntry.DashScope,    true),
        new(TranslationProviderEntry.OpenAiApi,    true),
    };
}
