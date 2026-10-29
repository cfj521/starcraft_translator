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

    /// <summary>把 Retry-After 头转成等待时长。该头有两种合法形式：秒数（Delta）与 HTTP-date（Date），
    /// 只读 Delta 会把 HTTP-date 形式当成"没给"→ 退化成 5s 指数退避，服务端要求等 2 分钟时会被反复撞限。
    /// Date 形式按"该时刻 − 现在"换算，已过期返回 null（视作没给，走退避）。</summary>
    public static TimeSpan? FromHeader(System.Net.Http.Headers.RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } d) return d;
        if (header.Date is { } date)
        {
            var left = date - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : null;
        }
        return null;
    }
}
