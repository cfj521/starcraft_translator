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
        Assert.Equal(6, back.Hotkeys.Count);
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
