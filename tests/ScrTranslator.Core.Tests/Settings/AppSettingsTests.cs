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
        Assert.Equal(200, a.Ocr.SceneDetectPollMs);
        Assert.Equal(500, a.Ocr.TranslatePollMs);
        Assert.InRange(a.Appearance.Opacity, 0.0, 1.0);
        Assert.Equal(0.8, a.Appearance.BackplateAlpha(), 3); // 文字底衬默认 80% 黑底
        Assert.Equal(TranslationEngine.DashScope, a.Translation.Active); // 复用 TranslationSettings 默认
    }

    [Theory]
    [InlineData(0.0, 0.0)]    // 0 = 不画底衬（旧观感）
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(-0.5, 0.0)]   // 越界钳制，渲染层可直接 (byte)(a*255) 不再各自 clamp
    [InlineData(3.0, 1.0)]
    public void BackplateAlpha_Clamps(double set, double expected)
        => Assert.Equal(expected, new AppearanceSettings { BackplateOpacity = set }.BackplateAlpha(), 6);

    [Fact] public void HotkeyDefaults_HasAllEightFunctions_WithExactBindings()
    {
        var m = HotkeyDefaults.Map;
        Assert.Equal(HotkeyKeys.VK_F9, m["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.VK_F10, m["ToggleOverlay"].Vk);
        Assert.Equal(HotkeyKeys.VK_F8, m["Calibrate"].Vk);
        Assert.Equal(HotkeyKeys.VK_F9, m["OpenConfig"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, m["OpenConfig"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F7, m["TogglePlayerId"].Vk);
        Assert.Equal(HotkeyKeys.VK_F11, m["TogglePause"].Vk);
        Assert.Equal(HotkeyKeys.VK_F6, m["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.VK_F6, m["CopyDebug"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, m["CopyDebug"].Modifiers);
        Assert.Equal(8, m.Count);
    }
}
