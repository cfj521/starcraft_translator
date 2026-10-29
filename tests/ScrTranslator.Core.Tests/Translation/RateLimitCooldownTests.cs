using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;

/// <summary>429 限流冷却：Retry-After 优先、指数退避（5s→×2→60s 封顶）、成功复位、重试时长夹到 1s–5min。</summary>
public class RateLimitCooldownTests
{
    [Fact]
    public void Enter_WithoutRetryAfter_ExponentialBackoff_Doubles_AndCapsAt60s()
    {
        var c = new RateLimitCooldown();
        Assert.False(c.Active);

        Assert.Equal(TimeSpan.FromSeconds(5), c.Enter(null));      // 首档 5s
        Assert.True(c.Active);
        Assert.Equal(TimeSpan.FromSeconds(10), c.Enter(null));    // 翻倍 10s
        Assert.Equal(TimeSpan.FromSeconds(20), c.Enter(null));
        Assert.Equal(TimeSpan.FromSeconds(40), c.Enter(null));
        Assert.Equal(TimeSpan.FromSeconds(60), c.Enter(null));    // 封顶 60s
        Assert.Equal(TimeSpan.FromSeconds(60), c.Enter(null));    // 封顶后不再涨
    }

    [Fact]
    public void Enter_WithRetryAfter_UsesServerValue_ClampedTo1sTo5min()
    {
        var c = new RateLimitCooldown();
        Assert.Equal(TimeSpan.FromSeconds(3), c.Enter(TimeSpan.FromSeconds(3)));   // 按服务端给的来
        Assert.Equal(TimeSpan.FromSeconds(1), c.Enter(TimeSpan.Zero));             // 0 → 夹到 1s
        Assert.Equal(TimeSpan.FromSeconds(300), c.Enter(TimeSpan.FromHours(1)));   // 1h → 夹到 5min
    }

    [Fact]
    public void Reset_ClearsCooldown_AndBackoffReturnsTo5s()
    {
        var c = new RateLimitCooldown();
        c.Enter(null); c.Enter(null);                 // 5s → 10s
        Assert.True(c.Active);
        c.Reset();
        Assert.False(c.Active);
        Assert.Equal(TimeSpan.FromSeconds(5), c.Enter(null)); // 退避归零重计
        Assert.True(c.Active);
    }

    [Fact]
    public void Enter_SubSecondRetryAfter_ClampsUpTo1s()
    {
        var c = new RateLimitCooldown();
        Assert.Equal(TimeSpan.FromSeconds(1), c.Enter(TimeSpan.FromMilliseconds(200))); // 亚秒值不再被 (int) 截断成 0
        Assert.Equal(TimeSpan.FromSeconds(1), c.Enter(TimeSpan.FromSeconds(-30)));      // 负值同样夹到 1s
    }

    [Fact]
    public void RetryAfterHeader_HttpDateForm_IsConvertedToWait()
    {
        // 服务端只给 HTTP-date 形式时，旧实现读 Delta=null → 退化成 5s 指数退避
        var date = DateTimeOffset.UtcNow.AddSeconds(42);
        var header = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
        var wait = RateLimitException.FromHeader(header);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalSeconds, 40, 43);

        // 已过期 → 视作没给（走退避），不能算出负等待
        Assert.Null(RateLimitException.FromHeader(new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1))));
        // 秒数形式仍按秒数
        Assert.Equal(TimeSpan.FromSeconds(7),
            RateLimitException.FromHeader(new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7))));
        Assert.Null(RateLimitException.FromHeader(null));
    }

    [Fact]
    public void Until_ReflectsTheEnteredDelay()
    {
        var c = new RateLimitCooldown();
        var before = DateTime.UtcNow;
        c.Enter(TimeSpan.FromSeconds(7));
        var after = DateTime.UtcNow;
        Assert.True(c.Until >= before.AddSeconds(7) && c.Until <= after.AddSeconds(7) + TimeSpan.FromSeconds(1));
    }
}
