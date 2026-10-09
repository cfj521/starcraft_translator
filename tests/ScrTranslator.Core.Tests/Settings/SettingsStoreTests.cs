using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class SettingsStoreTests
{
    [Fact] public void Save_Then_Load_RoundTrips()
    {
        var s = new AppSettings
        {
            Translation = TranslationSettings.Default with { Active = TranslationEngine.ChatGpt, TranslatePlayerId = true },
            Ocr = new OcrSettings { TranslatePollMs = 250 },
        };
        var path = Path.GetTempFileName();
        SettingsStore.Save(s, path);
        var back = SettingsStore.Load(path);
        Assert.Equal(TranslationEngine.ChatGpt, back.Translation.Active);
        Assert.True(back.Translation.TranslatePlayerId);
        Assert.Equal(250, back.Ocr.TranslatePollMs);
        Assert.Equal(8, back.Hotkeys.Count);
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
        // 缺失的 ToggleDebug/CopyDebug 用默认补上 → 共 8 项
        Assert.Equal(8, back.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F6, back.Hotkeys["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["CopyDebug"].Modifiers);
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

    [Fact] public void Load_LegacyRemovedFields_AreIgnored_AndDefaultsApply()
    {
        // 两批已删字段：ChatPollMs/OtherPollMs（轮询重做为两个正交节拍）、
        // TwoLevel/FastEngine/FastModel（两级翻译整体移除）。
        // 用户机器上的旧 settings.json 必须照常加载：多余字段忽略、同文件其余配置不丢、新默认值生效。
        // 键名用 PascalCase——SettingsStore 没配命名策略，STJ 默认大小写敏感，磁盘上就是这个形状。
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "General": { "TargetProcess": "StarCraft", "DebugMode": true },
              "Ocr": { "ChatPollMs": 300, "OtherPollMs": 800 },
              "Translation": { "TwoLevel": true, "FastEngine": 1, "FastModel": "qwen-flash", "TimeoutSeconds": 7 } }
            """);

        var back = SettingsStore.Load(path);

        Assert.Equal("StarCraft", back.General.TargetProcess);
        Assert.True(back.General.DebugMode);              // 同文件里的其他配置没被牵连丢失
        Assert.Equal(7, back.Translation.TimeoutSeconds); // 仍存在的字段照常读回
        Assert.Equal(200, back.Ocr.SceneDetectPollMs);    // 已删字段 → 用新默认，而非旧值
        Assert.Equal(500, back.Ocr.TranslatePollMs);
    }
}
