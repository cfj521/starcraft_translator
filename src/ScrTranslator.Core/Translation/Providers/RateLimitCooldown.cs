namespace ScrTranslator.Core.Translation.Providers;

/// <summary>LLM 429 限流冷却跟踪器（每个 LLM provider 一个实例）：
/// 收到 429 调 <see cref="Enter"/> 进入冷却——服务端给了 Retry-After 就用它（夹到 1s–5min 防异常值），
/// 没给就走指数退避（5s 起步、每次翻倍、60s 封顶）。请求成功调 <see cref="Reset"/> 清冷却并复位退避。
/// provider 在 <c>GetStatus()</c> 里查 <see cref="Active"/>，冷却期报 RateLimited，编排器据此跳过它、
/// 落到池内下一个 provider（而不是每拍都打一次注定被限的请求）。</summary>
public sealed class RateLimitCooldown
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private TimeSpan _backoff = InitialBackoff;
    // 用单调时钟（TickCount64，开机毫秒）而非 DateTime.UtcNow：系统时钟被 NTP 回拨时，
    // 绝对时刻比较会把 5s 冷却拉长到几分钟（表现为"一直报限流、怎么等都不恢复"）。
    private long _untilTicks;

    /// <summary>当前是否处于冷却期。</summary>
    public bool Active { get { lock (_gate) return Environment.TickCount64 < _untilTicks; } }

    /// <summary>冷却结束时刻（UTC，仅供日志/调试面板展示；判定一律用单调时钟）。</summary>
    public DateTime Until
    {
        get
        {
            lock (_gate)
            {
                long left = _untilTicks - Environment.TickCount64;
                return left <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddMilliseconds(left);
            }
        }
    }

    /// <summary>进入冷却，返回实际采用的冷却时长（供日志）。
    /// retryAfter 非空＝服务端明确给的等待时长（亚秒值向上取到 1s，超出 1s–5min 夹取）；为空＝按当前退避档（首档 5s，之后翻倍，封顶 60s）。</summary>
    public TimeSpan Enter(TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            var delay = retryAfter is null
                ? _backoff
                : TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.Value.TotalMilliseconds,
                    MinRetryAfter.TotalMilliseconds, MaxRetryAfter.TotalMilliseconds));
            _untilTicks = Environment.TickCount64 + (long)delay.TotalMilliseconds;
            _backoff = TimeSpan.FromSeconds(Math.Min(_backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds)); // 下一档退避（成功时 Reset 会归零）
            return delay;
        }
    }

    /// <summary>请求成功：清冷却、退避回到 5s 起步。</summary>
    public void Reset()
    {
        lock (_gate) { _untilTicks = 0; _backoff = InitialBackoff; }
    }
}
