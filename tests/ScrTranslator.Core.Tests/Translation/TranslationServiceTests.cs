using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationServiceTests
{
    private sealed class FakeProvider : ITranslationProvider
    {
        public List<string> Requested = new(); public bool Throw;
        public string Name => "fake";
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException();
            Requested.AddRange(lines);
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => "T:" + l).ToArray());
        }
    }

    [Fact]
    public async Task GlossaryAndCache_ShortCircuit_OnlyMissesHitProvider()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var cache = new TranslationCache(); cache.Set("나", "缓存中");
        var fake = new FakeProvider();
        var svc = new TranslationService(glo, cache, () => fake);
        var r = await svc.TranslateAsync(new[] { "저그", "나", "다" }, CancellationToken.None);
        Assert.Equal(new[] { "虫族", "缓存中", "T:다" }, r);
        Assert.Equal(new[] { "다" }, fake.Requested);          // 只有未命中的 다 走 provider
        Assert.True(cache.TryGet("다", out var c)); Assert.Equal("T:다", c); // 结果入缓存
    }

    [Fact]
    public async Task ProviderThrows_FallsBackToOriginal()
    {
        var svc = new TranslationService(Glossary.FromEntries(Array.Empty<(string,string)>()),
            new TranslationCache(), () => new FakeProvider { Throw = true });
        var r = await svc.TranslateAsync(new[] { "가", "나" }, CancellationToken.None);
        Assert.Equal(new[] { "가", "나" }, r);
    }
}
