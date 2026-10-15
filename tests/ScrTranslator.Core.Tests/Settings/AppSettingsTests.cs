using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class AppSettingsTests
{
    [Fact] public void Defaults_AreSane()
    {
        var a = new AppSettings();
        Assert.Equal("StarCraft", a.General.TargetProcess);
        Assert.True(a.General.StartMinimized);
        Assert.Equal(20, a.Ocr.SceneDetectPollMs);
        Assert.Equal(200, a.Ocr.TranslatePollMs);
        Assert.InRange(a.Appearance.Opacity, 0.0, 1.0);
        Assert.Equal(0.8, a.Appearance.BackplateAlpha(), 3); // 文字底衬默认 80% 黑底
        Assert.Equal(1.0, a.Appearance.FontWidthScale); // 字宽缩放默认正常
        Assert.Equal(TranslationProviderEntry.Aliyun, a.Translation.Providers[0].Id); // 默认池首位＝阿里云（先免费）
        Assert.Equal(4, a.Translation.Providers.Count); // 含新加的 OpenAI API 模式（已移除腾讯云）
    }

    [Theory]
    [InlineData(0.0, 0.0)]    // 0 = 不画底衬（旧观感）
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(-0.5, 0.0)]   // 越界钳制，渲染层可直接 (byte)(a*255) 不再各自 clamp
    [InlineData(3.0, 1.0)]
    public void BackplateAlpha_Clamps(double set, double expected)
        => Assert.Equal(expected, new AppearanceSettings { BackplateOpacity = set }.BackplateAlpha(), 6);

    [Theory]
    [InlineData(0.5, 0.7)]    // 过小 → 下限
    [InlineData(0.7, 0.7)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.3, 1.3)]
    [InlineData(2.0, 1.3)]    // 过大 → 上限
    public void WidthScale_ClampsToRange(double set, double expected)
        => Assert.Equal(expected, new AppearanceSettings { FontWidthScale = set }.WidthScale(), 6);

    [Fact] public void HotkeyDefaults_HasExactBindings()
    {
        var m = HotkeyDefaults.Map;
        Assert.Equal(HotkeyKeys.VK_F9, m["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.VK_F9, m["Calibrate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, m["Calibrate"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F7, m["OpenConfig"].Vk);
        Assert.Equal(HotkeyKeys.MOD_NONE, m["OpenConfig"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F8, m["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.VK_F8, m["CopyDebug"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, m["CopyDebug"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F6, m["Screenshot"].Vk);
        Assert.Equal(HotkeyKeys.MOD_NONE, m["Screenshot"].Modifiers);
        Assert.Equal(6, m.Count);
    }
}
