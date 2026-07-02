using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;

public class TieredTranslatorTests
{
    private static readonly TranslationContext Ctx = new("custom_games_listing", null);

    private sealed class FakeProvider : ITranslationProvider
    {
        public enum Mode { Real, Throw, Degrade } // Degrade=原样返回传入引用(静默降级)
        public Mode Behavior = Mode.Real;
        public string Prefix = "T:";
        public int Calls;
        public string Name => "fake";
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
        {
            Calls++;
            if (Behavior == Mode.Throw) throw new InvalidOperationException();
            if (Behavior == Mode.Degrade) return Task.FromResult(lines); // 引用相等
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => Prefix + l).ToArray());
        }
    }

    private static (TieredTranslator t, List<IReadOnlyList<LineTranslation>> snaps, TranslationCache cache)
        Make(FakeProvider fast, FakeProvider quality, bool twoLevel, Glossary? glo = null, TranslationCache? cache = null)
    {
        glo ??= Glossary.FromEntries(System.Array.Empty<(string,string)>());
        cache ??= new TranslationCache();
        var settings = TranslationSettings.Default with { TwoLevel = twoLevel };
        var t = new TieredTranslator(glo, cache, () => fast, () => quality, () => settings);
        return (t, new List<IReadOnlyList<LineTranslation>>(), cache);
    }

    // ---- 单级 ----
    [Fact]
    public async Task Single_GlossaryCache_ShortCircuit_OneCallback()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var cache = new TranslationCache(); cache.Set("나", "缓存中");
        var q = new FakeProvider();
        var (t, snaps, _) = Make(new FakeProvider(), q, twoLevel: false, glo, cache);
        var outcome = await t.TranslateAsync(new[] { "저그", "나", "다" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal(new[] { "虫族", "缓存中", "T:다" }, snaps[0].Select(x => x.Text));
        Assert.Equal(1, q.Calls);
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Single_ProviderThrows_FallsBackOriginal_NotResolved_NotCached()
    {
        var (t, snaps, cache) = Make(new FakeProvider(), new FakeProvider { Behavior = FakeProvider.Mode.Throw }, twoLevel: false);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal("가", snaps[0][0].Text);
        Assert.False(outcome.AllResolved);
        Assert.False(cache.TryGet("가", out _));
    }

    [Fact]
    public async Task Single_ProviderSilentDegrade_NotCached_NotResolved()
    {
        var (t, snaps, cache) = Make(new FakeProvider(), new FakeProvider { Behavior = FakeProvider.Mode.Degrade }, twoLevel: false);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("가", snaps[0][0].Text);
        Assert.False(outcome.AllResolved);
        Assert.False(cache.TryGet("가", out _));
    }

    // ---- 两级 ----
    [Fact]
    public async Task Two_EmitsProvisionalThenFinal_CachesQualityNotFast()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Prefix = "Q:" };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal(2, snaps.Count);
        Assert.Equal(TranslationStage.Provisional, snaps[0][0].Stage);
        Assert.Equal("F:가", snaps[0][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.Equal("Q:가", snaps[1][0].Text);
        Assert.True(cache.TryGet("가", out var c) && c == "Q:가"); // 缓存质量态
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_HitAndMiss_FirstSnapshotMixesDirectAndProvisional()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var (t, snaps, _) = Make(new FakeProvider { Prefix = "F:" }, new FakeProvider { Prefix = "Q:" }, twoLevel: true, glo);
        await t.TranslateAsync(new[] { "저그", "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal(2, snaps.Count);
        Assert.Equal(TranslationStage.Direct, snaps[0][0].Stage);        // 术语命中即 Direct
        Assert.Equal(TranslationStage.Provisional, snaps[0][1].Stage);   // miss 为临时
        Assert.All(snaps[1], x => Assert.Equal(TranslationStage.Final, x.Stage));
    }

    [Fact]
    public async Task Two_QualityThrows_FastReal_PromotesFast_Cached_Resolved()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("F:가", snaps[1][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.True(cache.TryGet("가", out var c) && c == "F:가");
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_QualitySilentDegrade_FastReal_PromotesFast_Cached_Resolved()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Degrade }; // 静默降级(不抛)
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("F:가", snaps[1][0].Text);          // 回落快态,严禁把原文当译文
        Assert.True(cache.TryGet("가", out var c) && c == "F:가");
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_BothFail_OriginalFinal_NotCached_NotResolved()
    {
        var fast = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("가", snaps[1][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.False(cache.TryGet("가", out _));
        Assert.False(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_AllHit_SingleDirectCallback()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var fast = new FakeProvider(); var quality = new FakeProvider();
        var (t, snaps, _) = Make(fast, quality, twoLevel: true, glo);
        var outcome = await t.TranslateAsync(new[] { "저그" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal(TranslationStage.Direct, snaps[0][0].Stage);
        Assert.Equal(0, fast.Calls); Assert.Equal(0, quality.Calls);
        Assert.True(outcome.AllResolved);
    }
}
