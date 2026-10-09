using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class SettingsStoreTests
{
    [Fact] public void Save_Then_Load_RoundTrips()
    {
        var s = new AppSettings
        {
            Translation = TranslationSettings.Default with { TwoLevel = true, TranslatePlayerId = true },
            Ocr = new OcrSettings { ChatPollMs = 250 },
        };
        var path = Path.GetTempFileName();
        SettingsStore.Save(s, path);
        var back = SettingsStore.Load(path);
        Assert.True(back.Translation.TwoLevel);
        Assert.True(back.Translation.TranslatePlayerId);
        Assert.Equal(250, back.Ocr.ChatPollMs);
        Assert.Equal(7, back.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F9, back.Hotkeys["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["OpenConfig"].Modifiers);
    }

    [Fact] public void Load_MissingFile_ReturnsDefault()
        => Assert.Equal("StarCraft", SettingsStore.Load("nope_no_file.json").General.TargetProcess);

    [Fact] public void Load_CorruptJson_ReturnsDefault()
    {
        var path = Path.GetTempFileName(); File.WriteAllText(path, "{ not valid json ");
        Assert.Equal(TranslationEngine.DashScope, SettingsStore.Load(path).Translation.Active);
    }

    [Fact] public void Load_OldConfig_MissingNewHotkey_MergedFromDefaults_KeepsCustom()
    {
        // 模拟"新增 F6 之前"的老配置：hotkeys 只有 6 项，且 ToggleTranslate 被用户改成了 Ctrl+F12。
        var json = """
        {"Hotkeys":{
          "ToggleTranslate":{"Modifiers":2,"Vk":123},
          "ToggleOverlay":{"Modifiers":0,"Vk":121},
          "Calibrate":{"Modifiers":0,"Vk":119},
          "OpenConfig":{"Modifiers":2,"Vk":120},
          "TogglePlayerId":{"Modifiers":0,"Vk":118},
          "TogglePause":{"Modifiers":0,"Vk":122}
        }}
        """;
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var back = SettingsStore.Load(path);
        // 缺失的 ToggleDebug 用默认(F6)补上 → 共 7 项
        Assert.Equal(7, back.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F6, back.Hotkeys["ToggleDebug"].Vk);
        // 用户自定义的绑定不被默认覆盖
        Assert.Equal(123u, back.Hotkeys["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["ToggleTranslate"].Modifiers);
    }

    [Fact] public void Saved_Json_ContainsNoSecretKeyField()
    {
        var path = Path.GetTempFileName();
        SettingsStore.Save(new AppSettings(), path);
        var json = File.ReadAllText(path);
        // 断言不含任何密钥字段名(AppSettings 本就不含,回归保护防未来误加)
        Assert.DoesNotContain("ApiKey", json);
        Assert.DoesNotContain("accessToken", json);
        Assert.DoesNotContain("refreshToken", json);
    }
}
