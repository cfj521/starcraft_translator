using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class PromptBuilderTests
{
    [Fact]
    public void SystemPrompt_IncludesSceneAndGlossaryHint()
    {
        var ctx = new TranslationContext("custom_games_listing", "저그=虫族; 러시=速攻");
        var sys = PromptBuilder.BuildSystemPrompt(ctx);
        Assert.Contains("自定义游戏房间列表", sys);
        Assert.Contains("저그=虫族", sys);
    }

    [Fact]
    public void SystemPrompt_NullHint_NoGlossarySection_NoCrash()
    {
        var sys = PromptBuilder.BuildSystemPrompt(new TranslationContext("custom_games_listing", null));
        Assert.Contains("自定义游戏房间列表", sys);
        Assert.DoesNotContain("统一采用", sys);
    }

    [Fact]
    public void SystemPrompt_UnknownScene_UsesRawLabel()
    {
        var sys = PromptBuilder.BuildSystemPrompt(new TranslationContext("chat_room", null));
        Assert.Contains("chat_room", sys);
    }

    [Fact]
    public void GlossaryHint_JoinsPairs_WithSemicolon()
    {
        var hint = PromptBuilder.BuildGlossaryHint(new[]
        {
            new KeyValuePair<string,string>("저그","虫族"),
            new KeyValuePair<string,string>("러시","速攻"),
        });
        Assert.Equal("저그=虫族; 러시=速攻", hint);
    }

    [Fact]
    public void GlossaryHint_ExceedingMax_TruncatesWithEllipsis()
    {
        var many = Enumerable.Range(0, 500)
            .Select(i => new KeyValuePair<string,string>("한국어단어" + i, "中文" + i));
        var hint = PromptBuilder.BuildGlossaryHint(many, maxChars: 100);
        Assert.True(hint.Length <= 101, $"len={hint.Length}");
        Assert.EndsWith("…", hint);
    }
}
