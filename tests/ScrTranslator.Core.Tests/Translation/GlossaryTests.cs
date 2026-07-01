using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class GlossaryTests
{
    private static Glossary Load() =>
        Glossary.LoadFromMarkdown(Path.Combine(AppContext.BaseDirectory, "assets", "glossary_sample.md"));

    [Fact] public void Hit_SingleKey() { Assert.True(Load().TryGet("저그", out var zh)); Assert.Equal("虫族", zh); }
    [Fact] public void Hit_TrimsWhitespace() { Assert.True(Load().TryGet("  저그 ", out var zh)); Assert.Equal("虫族", zh); }
    [Fact] public void Hit_SlashAlternateKey() { Assert.True(Load().TryGet("인구", out var zh)); Assert.Equal("人口", zh); }
    [Fact] public void Zh_TakesFirstSlashSegment() { Assert.True(Load().TryGet("러시", out var zh)); Assert.Equal("速攻", zh); }
    [Fact] public void Miss_ReturnsFalse() { Assert.False(Load().TryGet("없음", out _)); }
    [Fact] public void Miss_Empty() { Assert.False(Load().TryGet("", out _)); }
    [Fact] public void Miss_ChineseIsEmptyNotNull() { Load().TryGet("없음", out var zh); Assert.Equal("", zh); }
    [Fact] public void MissingFile_EmptyGlossary() { Assert.Equal(0, Glossary.LoadFromMarkdown("nope.md").Count); }
}
