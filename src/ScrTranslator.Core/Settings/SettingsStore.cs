using System.Linq;
using System.Text.Json;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Settings;

/// <summary>settings.json 读写(只存非密钥；密钥走 SecretStore/DPAPI)。缺文件/损坏→默认,不崩。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        // Hotkeys 是 IReadOnlyDictionary<string,HotkeyBinding> —— STJ 原生支持 string 键字典。
    };

    /// <summary>已删除的热键功能（悬浮层显隐 / 翻译玩家ID / 暂停 → 改纯配置项，不再占热键）。加载时从旧配置清掉。</summary>
    private static readonly string[] RemovedHotkeyFuncs = { "ToggleOverlay", "TogglePlayerId", "TogglePause" };

    /// <summary>热键迁移（幂等）：
    /// ① 删除已退役功能；② 旧默认升新默认——ToggleDebug F6→F8、Calibrate F8→F10→Ctrl+F9、
    /// OpenConfig F11→F10；用户已改成别的键则不动。每次 Load 都跑，跑过之后再跑是空操作。</summary>
    private static void MigrateHotkeys(Dictionary<string, HotkeyBinding> hk)
    {
        foreach (var f in RemovedHotkeyFuncs) hk.Remove(f);
        if (hk.TryGetValue("ToggleDebug", out var d) && d.Vk == HotkeyKeys.VK_F6 && d.Modifiers == HotkeyKeys.MOD_NONE)
            hk["ToggleDebug"] = new HotkeyBinding(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8);
        if (hk.TryGetValue("Calibrate", out var c) && c.Vk == HotkeyKeys.VK_F8 && c.Modifiers == HotkeyKeys.MOD_NONE)
            hk["Calibrate"] = new HotkeyBinding(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F10);
        if (hk.TryGetValue("Calibrate", out var c2) && c2.Vk == HotkeyKeys.VK_F10 && c2.Modifiers == HotkeyKeys.MOD_NONE)
            hk["Calibrate"] = new HotkeyBinding(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9);
        if (hk.TryGetValue("OpenConfig", out var o) && o.Vk == HotkeyKeys.VK_F11 && o.Modifiers == HotkeyKeys.MOD_NONE)
            hk["OpenConfig"] = new HotkeyBinding(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F10);
    }

    public static void Save(AppSettings settings, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(settings, Opts));

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            if (s is null) return new AppSettings();
            // 合并热键：以默认全集为底，用户已存的覆盖之。这样后续新增功能对老配置自动补上默认绑定，
            // 而用户自定义的绑定不被冲掉；再做一次迁移（删退役功能、旧默认升新默认）。
            var merged = new Dictionary<string, HotkeyBinding>(HotkeyDefaults.Map);
            foreach (var kv in s.Hotkeys) merged[kv.Key] = kv.Value;
            MigrateHotkeys(merged);
            // provider 迁移：先滤掉已退役的腾讯云机翻（免费额度已下线、QPS 将归零），老配置池里若还有这一行就清掉。
            var providers = s.Translation.Providers.Where(p => p.Id != "tencent").ToList();
            // 老配置池里没有新加的 OpenAI API 模式则补上，让既有用户也能看到/启用这一行。
            if (providers.All(p => p.Id != TranslationProviderEntry.OpenAiApi))
                providers.Add(new TranslationProviderEntry(TranslationProviderEntry.OpenAiApi, true));
            // 自定义模型（OpenAI 兼容）：老池里没有则补上（启用态），新用户在默认池里位于第二行。
            if (providers.All(p => p.Id != TranslationProviderEntry.CustomModel))
                providers.Add(new TranslationProviderEntry(TranslationProviderEntry.CustomModel, true));
            return s with { Hotkeys = merged, Translation = s.Translation with { Providers = providers } };
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
