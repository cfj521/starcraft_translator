using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationSettingsTests
{
    [Fact]
    public void Default_HasSaneValues()
    {
        var s = TranslationSettings.Default;
        Assert.Equal(TranslationEngine.DashScope, s.Active);
        Assert.False(string.IsNullOrWhiteSpace(s.DashScopeModel));
        Assert.False(string.IsNullOrWhiteSpace(s.ChatGptModel));
        Assert.InRange(s.TimeoutSeconds, 5, 30);
    }

    [Fact]
    public void Default_TwoLevelDisabled_FastIsDashScopeFlash()
    {
        var s = TranslationSettings.Default;
        Assert.False(s.TwoLevel);                                   // 默认关闭，防意外双请求
        Assert.Equal(TranslationEngine.DashScope, s.FastEngine);
        Assert.Equal("qwen-flash", s.FastModel);
    }
}
