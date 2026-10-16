using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationSettingsTests
{
    [Fact]
    public void Default_HasSaneValues()
    {
        var s = TranslationSettings.Default;
        // 默认池顺序：本地 Hy-MT2(离线优先) → 阿里云 → 自定义模型 → OpenAI LLM(订阅) → 阿里云LLM → OpenAI LLM(API)。
        // 本地离线引擎置顶，云端引擎兜底。
        Assert.Equal(new[] { TranslationProviderEntry.HyMt2,
                              TranslationProviderEntry.Aliyun,
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
        // 本地 Hy-MT2 默认指向出厂 2-bit 权重路径（非空；文件是否存在由 GetStatus 运行时判）
        Assert.False(string.IsNullOrWhiteSpace(s.HyMt2ModelPath));
        // 内部/外部分组单选：默认选中内部（本地引擎）。GroupOf 把 Hy-MT2 归内部、其余归外部。
        Assert.Equal(TranslationGroup.Internal, s.SelectedGroup);
        Assert.Equal(TranslationGroup.Internal, TranslationSettings.GroupOf(TranslationProviderEntry.HyMt2));
        Assert.Equal(TranslationGroup.External, TranslationSettings.GroupOf(TranslationProviderEntry.Aliyun));
        Assert.Equal(TranslationGroup.External, TranslationSettings.GroupOf(TranslationProviderEntry.CustomModel));
        Assert.Equal(TranslationGroup.External, TranslationSettings.GroupOf(TranslationProviderEntry.ChatGpt));
        Assert.Equal(TranslationGroup.External, TranslationSettings.GroupOf(TranslationProviderEntry.DashScope));
        Assert.Equal(TranslationGroup.External, TranslationSettings.GroupOf(TranslationProviderEntry.OpenAiApi));
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
