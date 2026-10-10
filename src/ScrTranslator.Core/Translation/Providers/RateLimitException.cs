namespace ScrTranslator.Core.Translation.Providers;

/// <summary>LLM API 返回 HTTP 429 限流。
/// 由客户端收到 429 响应时抛出；provider 捕获后进入限流冷却（<see cref="RateLimitCooldown"/>）再重抛，
/// 触发编排器的池回退链——冷却期由 GetStatus 拦截，不会每拍再打一次注定被限的请求。</summary>
public sealed class RateLimitException : Exception
{
    /// <summary>服务端给的 Retry-After（秒→时间）。null 表示没带该头，provider 改走指数退避。</summary>
    public TimeSpan? RetryAfter { get; }

    public RateLimitException(string message, TimeSpan? retryAfter) : base(message) { RetryAfter = retryAfter; }
    public RateLimitException(string message, TimeSpan? retryAfter, Exception innerException)
        : base(message, innerException) { RetryAfter = retryAfter; }
}
