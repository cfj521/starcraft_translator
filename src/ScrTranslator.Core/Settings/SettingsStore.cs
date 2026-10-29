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
    {
        // 原子写：直接 WriteAllText 若在写入中途被中断（崩溃/断电/杀进程）会留下半截 JSON，
        // 下次 Load 反序列化失败 → catch 返回全默认 → 用户所有设置静默丢失。
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            if (s is null) return new AppSettings();
            // STJ 会把显式写出的 "Hotkeys": null / "Translation": null 赋成 null（init 默认值失效），
            // 下面的 foreach 立刻 NRE → 被 catch 吞成"默认设置"→ 用户全部自定义配置静默丢失。
            // 这里补回默认实例：只有那一段回默认，其余段仍按用户配置加载。
            s = s with
            {
                General = s.General ?? new GeneralSettings(),
                Ocr = s.Ocr ?? new OcrSettings(),
                Appearance = s.Appearance ?? new AppearanceSettings(),
                Hotkeys = s.Hotkeys ?? HotkeyDefaults.Map,
                Translation = s.Translation ?? TranslationSettings.Default,
            };
            // 合并热键：以默认全集为底，用户已存的覆盖之。这样后续新增功能对老配置自动补上默认绑定，
            // 而用户自定义的绑定不被冲掉；再做一次迁移（删退役功能、旧默认升新默认）。
            var merged = new Dictionary<string, HotkeyBinding>(HotkeyDefaults.Map);
            foreach (var kv in s.Hotkeys) merged[kv.Key] = kv.Value;
            MigrateHotkeys(merged);
            // provider 迁移：先滤掉已退役的腾讯云机翻（免费额度已下线、QPS 将归零），老配置池里若还有这一行就清掉。
            // Providers 也可能被写成 null（同上的 STJ 行为），同样补回默认池再迁移。
            var providers = (s.Translation.Providers ?? TranslationSettings.DefaultProviders).Where(p => p.Id != "tencent").ToList();
            // 老配置池里没有新加的 OpenAI API 模式则补上，让既有用户也能看到/启用这一行。
            if (providers.All(p => p.Id != TranslationProviderEntry.OpenAiApi))
                providers.Add(new TranslationProviderEntry(TranslationProviderEntry.OpenAiApi, true));
            // 自定义模型（OpenAI 兼容）：老池里没有则补上（启用态），新用户在默认池里位于第二行。
            if (providers.All(p => p.Id != TranslationProviderEntry.CustomModel))
                providers.Add(new TranslationProviderEntry(TranslationProviderEntry.CustomModel, true));
            // 本地 Hy-MT2（库模式、进程内推理）：老池里没有则插到**第一行**（离线主翻译，用户可拖拽改序）。
            // 插到 index 0 而非尾部：它是离线/不限流的主力，应优先于云端 provider 被取用。
            if (providers.All(p => p.Id != TranslationProviderEntry.HyMt2))
                providers.Insert(0, new TranslationProviderEntry(TranslationProviderEntry.HyMt2, true));
            return s with { Hotkeys = merged, Translation = s.Translation with { Providers = providers } };
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
