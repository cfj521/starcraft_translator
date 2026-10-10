using System.Text.Json;
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
    /// ① 删除已退役功能；② ToggleDebug/Calibrate 若仍是旧默认（F6/F8）升到新默认（F8/F10），
    /// 用户已改成别的键则不动。每次 Load 都跑，跑过之后再跑是空操作。</summary>
    private static void MigrateHotkeys(Dictionary<string, HotkeyBinding> hk)
    {
        foreach (var f in RemovedHotkeyFuncs) hk.Remove(f);
        if (hk.TryGetValue("ToggleDebug", out var d) && d.Vk == HotkeyKeys.VK_F6 && d.Modifiers == HotkeyKeys.MOD_NONE)
            hk["ToggleDebug"] = new HotkeyBinding(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8);
        if (hk.TryGetValue("Calibrate", out var c) && c.Vk == HotkeyKeys.VK_F8 && c.Modifiers == HotkeyKeys.MOD_NONE)
            hk["Calibrate"] = new HotkeyBinding(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F10);
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
            return s with { Hotkeys = merged };
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
