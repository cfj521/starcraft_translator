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

    public static void Save(AppSettings settings, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(settings, Opts));

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            if (s is null) return new AppSettings();
            // 合并热键：以默认全集为底，用户已存的覆盖之。这样后续新增功能（如 ToggleDebug/F6）
            // 对老配置自动补上默认绑定，而用户自定义的绑定不被冲掉。
            var merged = new Dictionary<string, HotkeyBinding>(HotkeyDefaults.Map);
            foreach (var kv in s.Hotkeys) merged[kv.Key] = kv.Value;
            return s with { Hotkeys = merged };
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
