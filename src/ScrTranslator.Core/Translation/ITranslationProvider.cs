namespace ScrTranslator.Core.Translation;

/// <summary>provider 可用性状态。<see cref="Usable"/> 为 true 时编排器才会向它发请求。</summary>
public enum ProviderState
{
    /// <summary>可发请求：凭据齐、额度未耗尽。</summary>
    Available,
    /// <summary>没配凭据（API Key / 密钥 / 未登录）。编排器跳过、不发请求。</summary>
    NoCredentials,
    /// <summary>免费额度已用尽（或近期被 API 判为超额）。编排器跳过，直到额度重置/冷却结束。</summary>
    QuotaExhausted,
    /// <summary>命中限流（HTTP 429），正处于冷却期（Retry-After 或指数退避）。编排器跳过、不发请求，
    /// 冷却结束自动恢复——期间整批回落原文或落到池内下一个 provider。</summary>
    RateLimited,
}

/// <summary><see cref="ITranslationProvider.GetStatus"/> 的返回值。<see cref="Detail"/> 给日志/界面展示原因。</summary>
public sealed record ProviderStatus(ProviderState State, string Detail)
{
    public bool Usable => State == ProviderState.Available;
    public static ProviderStatus Ok(string detail = "") => new(ProviderState.Available, detail);
    public static ProviderStatus NoCreds(string detail) => new(ProviderState.NoCredentials, detail);
    public static ProviderStatus Exhausted(string detail) => new(ProviderState.QuotaExhausted, detail);
    public static ProviderStatus RateLimited(string detail) => new(ProviderState.RateLimited, detail);
}

/// <summary>provider 调用 API 被判"免费额度已耗尽"时抛它。
/// provider 抛前已自标记 <see cref="ProviderState.QuotaExhausted"/>，编排器捕获后跳过本 provider、回退下一个。</summary>
public sealed class ProviderQuotaExhaustedException : Exception
{
    public ProviderQuotaExhaustedException(string msg) : base(msg) { }
}

/// <summary>翻译 provider 的统一接口。池内每个 provider（免费机器翻译 / LLM）都实现它。
/// 编排器按池顺序逐个 <see cref="GetStatus"/> 过滤可用项，再 <see cref="TranslateAsync"/>，失败回退下一个。</summary>
public interface ITranslationProvider
{
    /// <summary>provider 标识（settings 池里用它定位、日志里用它标注失败来源）。</summary>
    string Name { get; }

    /// <summary>本地快速可用性检查（**不联网**）：凭据是否齐、额度是否已知耗尽。
    /// 返回非 <see cref="ProviderState.Available"/> 时编排器直接跳过、不发请求。</summary>
    ProviderStatus GetStatus();

    /// <summary>翻译一批韩文行。实现约定：翻不出的行**原样返回**（引用相等），编排器据此判未翻成。
    /// 网络/鉴权失败直接抛异常，由编排器捕获后回退下一个 provider。</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct);
}
