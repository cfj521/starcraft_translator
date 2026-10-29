namespace ScrTranslator.Core.Translation;

/// <summary>provider 池里的一行：用 <see cref="Id"/> 定位具体 provider，<see cref="Enabled"/> 控制是否参与选取。</summary>
public sealed record TranslationProviderEntry(string Id, bool Enabled)
{
    public const string HyMt2 = "hy_mt2";        // 本地 Hy-MT2（腾讯翻译模型，纯 C# 进程内推理，离线/不限流，用户自配 GGUF 权重路径）
    public const string Aliyun  = "aliyun";    // 阿里云机器翻译（免费）
    public const string CustomModel = "custom_model"; // 自定义模型（用户自配 URL/KEY/模型，OpenAI 兼容模式）
    public const string ChatGpt = "chatgpt";   // OpenAI LLM（ChatGPT 订阅，OAuth 登录）
    public const string DashScope = "dashscope"; // 阿里云 LLM（DashScope）
    public const string OpenAiApi = "openai_api"; // OpenAI LLM（API Key 模式，sk-...，走官方 api.openai.com）
}

/// <summary>provider 的分组。内部＝本地引擎（进程内 CPU 推理，离线/不限流，现为 Hy-MT2）；外部＝云端 API 引擎。
/// 两组**单选**（二选一），只有选中组参与翻译，另一组完全不参与（不是回退链）。</summary>
public enum TranslationGroup
{
    /// <summary>内部（本地引擎，离线 · CPU 推理）。</summary>
    Internal,
    /// <summary>外部（云端 API 引擎）。</summary>
    External,
}

/// <summary>翻译设置。
/// <see cref="Providers"/> 是有序的 provider 池：编排器从上到下取第一个「启用 且 可用」的 provider，
/// 不可用/额度耗尽/翻译失败就跳到下一个。默认顺序＝先免费机器翻译、后 LLM。</summary>
public sealed record TranslationSettings
{
    public IReadOnlyList<TranslationProviderEntry> Providers { get; init; } = DefaultProviders;

    // 内部/外部分组单选（二选一）：只有选中组的 provider 参与翻译，另一组完全不参与（不是回退链）。
    // 默认内部（本地 Hy-MT2，离线/不限流，作主翻译）。旧 settings.json 无此字段时反序列化取默认值＝内部。
    public TranslationGroup SelectedGroup { get; init; } = TranslationGroup.Internal;

    /// <summary>provider id → 所属组。内部组现为 Hy-MT2（本地引擎）；其余全是外部（云端 API）。</summary>
    public static TranslationGroup GroupOf(string id) =>
        id == TranslationProviderEntry.HyMt2 ? TranslationGroup.Internal : TranslationGroup.External;

    public string DashScopeModel { get; init; } = "qwen3.8-flash";
    public string ChatGptModel { get; init; } = "gpt-6-luna";
    public string OpenAiApiModel { get; init; } = "gpt-6-luna";
    // 自定义模型（OpenAI 兼容模式）：URL 与模型 ID 存这里，KEY 走 SecretStore（DPAPI）。
    // 二者为空＝未配置，GetStatus 报 NoCredentials，编排器跳过。
    public string CustomModelUrl { get; init; } = "";
    public string CustomModelId { get; init; } = "";
    // 本地 Hy-MT2：GGUF 权重文件路径（非密钥，进 settings.json）。默认指向 2-bit 权重；
    // 文件不存在＝未配置，GetStatus 报 NoCredentials，编排器跳过。
    public string HyMt2ModelPath { get; init; } = @"D:\models\Tencent\Hy-MT2\Hy-MT2-1.8B-2Bit.gguf";
    // 本地 Hy-MT2 推理设备："cpu"（默认，纯 CPU SIMD）或 "gpu"（Vulkan，需系统有可用 Vulkan 设备）。
    // provider 把 "gpu" 映射到 HyMT2Sharp 的 "vulkan" 后端；加载时若 GPU 不可用会回落 CPU。
    public string HyMt2Backend { get; init; } = "cpu";
    public int TimeoutSeconds { get; init; } = 10;
    // 曾有 Active(DashScope|ChatGpt) 单选：provider 池化后废弃，由 Providers 的顺序+启用取代。
    // 旧 settings.json 里的 Active 字段反序列化时被忽略，SettingsStore 负责迁移到 Providers。
    public static TranslationSettings Default => new();

    /// <summary>默认池顺序：本地 Hy-MT2(离线优先) → 阿里云(免费) → 自定义模型(用户自配) → OpenAI LLM(订阅) → 阿里云LLM(DashScope) → OpenAI LLM(API)。全部启用。
    /// 用只读包装暴露：数组本身可被强转回 <c>TranslationProviderEntry[]</c> 改写，进而污染所有默认实例。</summary>
    public static IReadOnlyList<TranslationProviderEntry> DefaultProviders { get; } =
        Array.AsReadOnly(new TranslationProviderEntry[]
        {
            new(TranslationProviderEntry.HyMt2,        true),
            new(TranslationProviderEntry.Aliyun,       true),
            new(TranslationProviderEntry.CustomModel,  true),
            new(TranslationProviderEntry.ChatGpt,      true),
            new(TranslationProviderEntry.DashScope,    true),
            new(TranslationProviderEntry.OpenAiApi,    true),
        });
}
