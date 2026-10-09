using ScrTranslator.Core;

namespace ScrTranslator.Core.Tests;

/// <summary>
/// 翻译入口的内容闸门：只有含韩文的行才值得送进 LLM。
/// 这些"不该翻"的样本全部来自 SC:R 客户端真实 UI 文本——它们曾原样出现在悬浮层上。
/// </summary>
public class KoreanTextTests
{
    [Theory]
    [InlineData("러시 가요")]              // 正常韩文句子
    [InlineData("2002 AP+ 러시")]          // 混合行：有韩文就要翻
    [InlineData("한")]                     // 音节起点 U+AC00
    [InlineData("힣")]                     // 音节终点 U+D7A3
    [InlineData("ㄱ")]                     // 兼容 Jamo
    [InlineData("ᄀ")]                     // Jamo 扩展 A
    [InlineData("gg 러시")]                // 夹英文黑话仍算韩文行
    public void ContainsHangul_True(string text) => Assert.True(KoreanText.ContainsHangul(text));

    [Theory]
    [InlineData("")]                       // 空
    [InlineData("   ")]                    // 空白
    [InlineData("2002 AP+")]               // 地图名
    [InlineData("Monkey Business")]        // 地图名
    [InlineData("Top vs Bottom")]          // 对战设置
    [InlineData("Use Map Settings")]       // 设置项
    [InlineData("Faster")]                 // 速度
    [InlineData("Players")]                // 表头
    [InlineData("1234")]                   // 纯数字
    [InlineData("!!! ??? gg gl hf")]       // 纯符号/英文黑话
    [InlineData("PSI ADL TA 2000")]        // 地图名变体
    public void ContainsHangul_False(string text) => Assert.False(KoreanText.ContainsHangul(text));

    [Fact]
    public void ContainsHangul_Null_IsFalse() => Assert.False(KoreanText.ContainsHangul(null));

    [Fact]
    public void ContainsHangul_DoesNotConfuseWithOtherCjk()
    {
        // 中日文不是韩文：别因为"是 CJK"就放行，闸门要精确到谚文区段。
        Assert.False(KoreanText.IsHangul('中'));
        Assert.False(KoreanText.IsHangul('あ'));
        Assert.False(KoreanText.IsHangul('ア'));
        Assert.True(KoreanText.IsHangul('가'));
    }
}
