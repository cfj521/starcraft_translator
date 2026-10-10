using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;

/// <summary>
/// 翻译编排：术语表 → 缓存 → 活跃 provider，单级一批一请求。
/// （原 TieredTranslator 的两级路径——快引擎临时态 + 主力定稿——已整体移除，相关用例一并删除。）
/// </summary>
public class TranslationOrchestratorTests
{
    private static readonly TranslationContext Ctx = new("browsing", null);

    private sealed class FakeProvider : ITranslationProvider
    {
        public enum Mode { Real, Throw, Degrade, Short }
        // Real=正常加前缀；Throw=抛异常；Degrade=原样返回传入引用(引用相等==静默降级)；
        // Short=违约只回 1 行（但回的那行是真译文），测"缺行不越界崩掉"。
        public Mode Behavior = Mode.Real;
        public string Prefix = "T:";
        public int Calls;
        public bool Usable = true;          // false 时 GetStatus 报"无凭据"，编排器直接跳过、不发请求
        public string Name { get; }
        public FakeProvider(string name = "fake") { Name = name; }
        public ProviderStatus GetStatus() => Usable ? ProviderStatus.Ok() : ProviderStatus.NoCreds(Name + " 无凭据");
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
        {
            Calls++;
            if (Behavior == Mode.Throw) throw new InvalidOperationException("boom");
            if (Behavior == Mode.Degrade) return Task.FromResult(lines);           // 引用相等 == 没翻
            if (Behavior == Mode.Short) return Task.FromResult<IReadOnlyList<string>>(lines.Take(1).Select(l => Prefix + l).ToArray());
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => Prefix + l).ToArray());
        }
    }

    private static (TranslationOrchestrator o, TranslationCache cache, List<string> errors)
        Make(FakeProvider provider, Glossary? glo = null, TranslationCache? cache = null)
    {
        glo ??= Glossary.FromEntries(System.Array.Empty<(string, string)>());
        cache ??= new TranslationCache();
        var errors = new List<string>();
        return (new TranslationOrchestrator(glo, cache, () => new ITranslationProvider[] { provider }, m => errors.Add(m)), cache, errors);
    }

    /// <summary>多 provider 池：按传入顺序回退。池测试用。</summary>
    private static (TranslationOrchestrator o, TranslationCache cache, List<string> errors)
        MakePool(params FakeProvider[] providers)
    {
        var errors = new List<string>();
        var cache = new TranslationCache();
        ITranslationProvider[] arr = providers;
        return (new TranslationOrchestrator(Glossary.FromEntries(System.Array.Empty<(string, string)>()), cache, () => arr, m => errors.Add(m)), cache, errors);
    }

    [Fact]
    public async Task Miss_CallsProviderOnce_AndCachesResult()
    {
        var p = new FakeProvider();
        var (o, cache, _) = Make(p);
        var r = await o.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);

        Assert.Equal(new[] { "T:가", "T:나" }, r.Texts);
        Assert.Equal(1, p.Calls);                       // 一批一次，不是每行一次
        Assert.True(r.AllResolved);
        Assert.Null(r.Error);
        Assert.True(cache.TryGet("가", out var c) && c == "T:가");

        // 同批再来一次：全部走缓存，provider 不再被调用
        await o.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);
        Assert.Equal(1, p.Calls);
    }

    [Fact]
    public async Task GlossaryAndCache_ShortCircuit_ProviderOnlySeesTheRest()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var cache = new TranslationCache(); cache.Set("나", "缓存中");
        var p = new FakeProvider();
        var (o, _, _) = Make(p, glo, cache);
        var r = await o.TranslateAsync(new[] { "저그", "나", "다" }, Ctx, CancellationToken.None);

        Assert.Equal(new[] { "虫族", "缓存中", "T:다" }, r.Texts);   // 下标必须与输入一一对应
        Assert.Equal(1, p.Calls);
        Assert.True(r.AllResolved);
    }

    [Fact]
    public async Task AllHitByGlossary_ZeroProviderCalls()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var p = new FakeProvider();
        var (o, _, _) = Make(p, glo);
        var r = await o.TranslateAsync(new[] { "저그" }, Ctx, CancellationToken.None);

        Assert.Equal(0, p.Calls);
        Assert.Equal("虫族", r.Texts[0]);
        Assert.True(r.AllResolved);
    }

    [Fact]
    public async Task ProviderThrows_FallsBackToOriginal_NotCached_ReportsError()
    {
        var p = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var (o, cache, errors) = Make(p);
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("가", r.Texts[0]);                 // 回落原文（调用方须自行过滤，别当译文显示）
        Assert.False(r.AllResolved);
        Assert.False(cache.TryGet("가", out _));        // 失败结果绝不进缓存
        Assert.Contains("fake 翻译失败", r.Error);
        Assert.Single(errors);                          // 且上报过一次，不会静默
    }

    [Fact]
    public async Task ProviderSilentDegrade_CountsAsUnresolved_NotCached()
    {
        var (o, cache, _) = Make(new FakeProvider { Behavior = FakeProvider.Mode.Degrade });
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("가", r.Texts[0]);
        Assert.False(r.AllResolved);
        Assert.False(cache.TryGet("가", out _));
    }

    [Fact]
    public async Task ProviderReturnsShortBatch_UnresolvedWithoutThrowing()
    {
        // provider 违约（返回行数少于请求）：不能越界崩掉，缺的行按未翻成处理。
        var (o, cache, _) = Make(new FakeProvider { Behavior = FakeProvider.Mode.Short });
        var r = await o.TranslateAsync(new[] { "가", "나", "다" }, Ctx, CancellationToken.None);

        Assert.False(r.AllResolved);
        Assert.Equal("T:가", r.Texts[0]);
        Assert.Equal("나", r.Texts[1]);                 // 没翻成的仍是原文
        Assert.Equal("다", r.Texts[2]);
        Assert.False(cache.TryGet("나", out _));
    }

    [Fact]
    public async Task EmptyInput_IsResolvedWithNoCall()
    {
        var p = new FakeProvider();
        var (o, _, _) = Make(p);
        var r = await o.TranslateAsync(System.Array.Empty<string>(), Ctx, CancellationToken.None);

        Assert.Empty(r.Texts);
        Assert.True(r.AllResolved);
        Assert.Equal(0, p.Calls);
    }

    // ── provider 池回退链 ──────────────────────────────────────────────

    [Fact]
    public async Task Pool_SkipsNoCredentials_TriesNext()
    {
        // 第一个 provider 无凭据（GetStatus 报 NoCreds）：编排器直接跳过、不发请求，落到第二个。
        var dead = new FakeProvider("tencent") { Usable = false };
        var live = new FakeProvider("aliyun");
        var (o, cache, errors) = MakePool(dead, live);
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("T:가", r.Texts[0]);
        Assert.Equal(0, dead.Calls);        // 无凭据的压根没被调用
        Assert.Equal(1, live.Calls);        // 用的是第二个
        Assert.True(r.AllResolved);
        Assert.Null(r.Error);
        Assert.Empty(errors);              // 跳过不算失败，不上报
        Assert.True(cache.TryGet("가", out var c) && c == "T:가");
    }

    [Fact]
    public async Task Pool_FirstThrows_FallsThroughToSecond()
    {
        // 第一个可用但翻译时抛异常（网络失败）：回退下一个。
        var boom = new FakeProvider("tencent") { Behavior = FakeProvider.Mode.Throw };
        var live = new FakeProvider("aliyun");
        var (o, _, errors) = MakePool(boom, live);
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("T:가", r.Texts[0]);
        Assert.Equal(1, boom.Calls);        // 被试过了（但失败）
        Assert.Equal(1, live.Calls);        // 回退到第二个成功
        Assert.True(r.AllResolved);
        Assert.StartsWith("tencent 翻译失败", Assert.Single(errors)); // 失败有上报（后接 ": boom"），不会静默
    }

    [Fact]
    public async Task Pool_AllUnavailable_ReturnsError_NoProviderCalled()
    {
        var d1 = new FakeProvider("tencent") { Usable = false };
        var d2 = new FakeProvider("aliyun") { Usable = false };
        var (o, cache, _) = MakePool(d1, d2);
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("가", r.Texts[0]);          // 整批回落原文
        Assert.False(r.AllResolved);
        Assert.Equal(0, d1.Calls);
        Assert.Equal(0, d2.Calls);
        Assert.False(cache.TryGet("가", out _));
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Pool_AllThrow_ReturnsError()
    {
        var b1 = new FakeProvider("tencent") { Behavior = FakeProvider.Mode.Throw };
        var b2 = new FakeProvider("aliyun") { Behavior = FakeProvider.Mode.Throw };
        var (o, _, errors) = MakePool(b1, b2);
        var r = await o.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);

        Assert.Equal("가", r.Texts[0]);
        Assert.False(r.AllResolved);
        Assert.Equal(1, b1.Calls);
        Assert.Equal(1, b2.Calls);
        Assert.Equal(2, errors.Count);          // 两个都失败、都上报
        Assert.NotNull(r.Error);
    }
}
