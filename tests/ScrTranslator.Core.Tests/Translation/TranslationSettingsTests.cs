using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationSettingsTests
{
    [Fact]
    public void Default_HasSaneValues()
    {
        var s = TranslationSettings.Default;
        // 默认池顺序：阿里云 → 自定义模型 → OpenAI LLM(订阅) → 阿里云LLM → OpenAI LLM(API)（先免费机器翻译、后 LLM）
        Assert.Equal(new[] { TranslationProviderEntry.Aliyun,
                              TranslationProviderEntry.CustomModel,
                              TranslationProviderEntry.ChatGpt, TranslationProviderEntry.DashScope,
                              TranslationProviderEntry.OpenAiApi },
            s.Providers.Select(p => p.Id).ToArray());
        Assert.All(s.Providers, p => Assert.True(p.Enabled));
        Assert.False(string.IsNullOrWhiteSpace(s.DashScopeModel));
        Assert.False(string.IsNullOrWhiteSpace(s.ChatGptModel));
        Assert.False(string.IsNullOrWhiteSpace(s.OpenAiApiModel));
        // 自定义模型默认未配置（URL/模型 ID 为空）
        Assert.True(string.IsNullOrWhiteSpace(s.CustomModelUrl));
        Assert.True(string.IsNullOrWhiteSpace(s.CustomModelId));
        Assert.InRange(s.TimeoutSeconds, 5, 30);
    }

    [Fact]
    public void TwoLevel_FieldsAreGone()
    {
        // 两级翻译（快引擎临时态 + 主力定稿）已整体移除：这是个很窄的翻译任务，低价模型单发就够好，
        // 两级只让每个未命中批次多打一次请求。这三个字段若被加回来，说明死功能被复活了。
        var names = typeof(TranslationSettings).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("TwoLevel", names);
        Assert.DoesNotContain("FastEngine", names);
        Assert.DoesNotContain("FastModel", names);
    }
}
