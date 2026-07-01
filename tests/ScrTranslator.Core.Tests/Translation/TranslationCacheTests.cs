using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationCacheTests
{
    [Fact] public void SetThenGet() { var c = new TranslationCache(); c.Set("저그","虫族"); Assert.True(c.TryGet("저그", out var v)); Assert.Equal("虫族", v); }
    [Fact] public void Miss() { Assert.False(new TranslationCache().TryGet("x", out _)); }
    [Fact] public void TrimKey() { var c = new TranslationCache(); c.Set(" 저그 ","虫族"); Assert.True(c.TryGet("저그", out _)); }
    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var c = new TranslationCache(2);
        c.Set("a","1"); c.Set("b","2");
        Assert.True(c.TryGet("a", out _));   // a 变最近
        c.Set("c","3");                       // 淘汰 b
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(2, c.Count);
    }
}
