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
            return s ?? new AppSettings();
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
