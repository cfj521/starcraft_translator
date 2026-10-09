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
        public string Name => "fake";
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
        return (new TranslationOrchestrator(glo, cache, () => provider, m => errors.Add(m)), cache, errors);
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
}
