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
        Assert.Equal(TranslationEngine.DashScope, a.Translation.Active); // 复用 TranslationSettings 默认
    }

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
