using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class SettingsStoreTests
{
    [Fact] public void Save_Then_Load_RoundTrips()
    {
        var s = new AppSettings
        {
            // 自定义池顺序（ChatGPT 在前、DashScope 被禁用）：验证顺序+启用态能往返
            Translation = TranslationSettings.Default with
            {
                Providers = new[]
                {
                    new TranslationProviderEntry(TranslationProviderEntry.ChatGpt, true),
                    new TranslationProviderEntry(TranslationProviderEntry.DashScope, false),
                },
                TranslatePlayerId = true,
            },
            Ocr = new OcrSettings { TranslatePollMs = 250 },
        };
        var path = Path.GetTempFileName();
        SettingsStore.Save(s, path);
        var back = SettingsStore.Load(path);
        // 老池（ChatGPT/DashScope）缺新加的 OpenAI API 模式，加载时迁移补到末尾（启用态），故 2→3
        Assert.Equal(3, back.Translation.Providers.Count);
        Assert.Equal(TranslationProviderEntry.ChatGpt, back.Translation.Providers[0].Id);
        Assert.False(back.Translation.Providers[1].Enabled);   // DashScope 被禁用的状态保留
        Assert.Equal(TranslationProviderEntry.OpenAiApi, back.Translation.Providers[2].Id); // 迁移补上的新 provider
        Assert.True(back.Translation.Providers[2].Enabled);    // 默认启用
        Assert.True(back.Translation.TranslatePlayerId);
        Assert.Equal(250, back.Ocr.TranslatePollMs);
        Assert.Equal(5, back.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F9, back.Hotkeys["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_NONE, back.Hotkeys["OpenConfig"].Modifiers);
    }

    [Fact] public void Load_MissingFile_ReturnsDefault()
        => Assert.Equal("StarCraft", SettingsStore.Load("nope_no_file.json").General.TargetProcess);

    [Fact] public void Load_CorruptJson_ReturnsDefault()
    {
        var path = Path.GetTempFileName(); File.WriteAllText(path, "{ not valid json ");
        Assert.Equal(4, SettingsStore.Load(path).Translation.Providers.Count); // 损坏 JSON → 默认池（含 OpenAI API 模式，已移除腾讯云）
    }

    [Fact] public void Load_OldConfig_MissingNewHotkey_MergedFromDefaults_KeepsCustom()
    {
        // 模拟老配置：只存了部分热键（ToggleTranslate 被用户改成了 Ctrl+F12），缺失的项用默认补上。
        var json = """
        {"Hotkeys":{
          "ToggleTranslate":{"Modifiers":2,"Vk":123},
          "Calibrate":{"Modifiers":0,"Vk":121},
          "OpenConfig":{"Modifiers":2,"Vk":120}
        }}
        """;
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var back = SettingsStore.Load(path);
        // 缺失的 ToggleDebug/CopyDebug 用默认补上 → 共 5 项
        Assert.Equal(5, back.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F8, back.Hotkeys["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["CopyDebug"].Modifiers);
        // 用户自定义的绑定不被默认覆盖
        Assert.Equal(123u, back.Hotkeys["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["ToggleTranslate"].Modifiers);
    }

    [Fact] public void Load_MigratesRemovedFuncs_And_UpgradesChangedDefaults()
    {
        // 老配置：含三个已删功能 + ToggleDebug/Calibrate 停留在旧默认（F6/F8）。
        var json = """
        {"Hotkeys":{
          "ToggleTranslate":{"Modifiers":0,"Vk":120},
          "ToggleOverlay":{"Modifiers":0,"Vk":121},
          "Calibrate":{"Modifiers":0,"Vk":119},
          "TogglePlayerId":{"Modifiers":0,"Vk":118},
          "TogglePause":{"Modifiers":0,"Vk":122},
          "ToggleDebug":{"Modifiers":0,"Vk":117}
        }}
        """;
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var back = SettingsStore.Load(path);
        // 三个已删功能被清掉；ToggleDebug(F6)→F8、Calibrate(F8)→F10→Ctrl+F9（一次到最新）；ToggleTranslate 保持。
        Assert.False(back.Hotkeys.ContainsKey("ToggleOverlay"));
        Assert.False(back.Hotkeys.ContainsKey("TogglePlayerId"));
        Assert.False(back.Hotkeys.ContainsKey("TogglePause"));
        Assert.Equal(HotkeyKeys.VK_F8, back.Hotkeys["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.VK_F9, back.Hotkeys["Calibrate"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, back.Hotkeys["Calibrate"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F9, back.Hotkeys["ToggleTranslate"].Vk);
        Assert.Equal(5, back.Hotkeys.Count);
        // 幂等：再存再读一次，结果不变。
        SettingsStore.Save(back, path);
        var again = SettingsStore.Load(path);
        Assert.Equal(5, again.Hotkeys.Count);
        Assert.Equal(HotkeyKeys.VK_F8, again.Hotkeys["ToggleDebug"].Vk);
        Assert.Equal(HotkeyKeys.VK_F9, again.Hotkeys["Calibrate"].Vk);
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
