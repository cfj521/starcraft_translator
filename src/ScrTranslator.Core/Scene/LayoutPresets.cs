using System.Text;
using System.Text.Json;

namespace ScrTranslator.Core.Scene;

/// <summary>
/// 某场景在某分辨率下的**界面布局预设**：聊天框 + （仅寻找房间）房间名列表区域 + （仅房间内）房间名称/地图名称。
///
/// 为什么内置而不是校准：这些面板在给定分辨率下位置和尺寸是死的，让用户每次拖框既没准头也没必要。
/// 数值来源：1080p 从 docs/scenes pictures 的截图量得，2K/4K 按 4/3、2 倍等比推算并用叠加图逐档目视核验。
/// 校准/编辑能力保留，用于改版时覆盖。
///
/// 坐标一律是**相对该分辨率的 0~1 比例**（与 rois.json 同一口径），且**分辨率必须精确匹配**——
/// UI 不是严格等比缩放，借用邻近分辨率的预设会让框子整体偏移。
/// </summary>
public sealed record LayoutPreset(string Scene, string Resolution, RelRect? Chat, RelRect? RoomNameList = null,
    RelRect? GameName = null, RelRect? MapName = null)
{
    /// <summary>预设 ROI 名字的特殊标记。名字带标记 = 预设：校准窗靠它在保存时分流
    /// （带标记的写 layout-overrides.json 覆盖层，不带的写 rois.json 用户框），运行时日志/调试面板也据此区分。</summary>
    public const string PresetNamePrefix = "%preset%";

    /// <summary>
    /// 展开成普通 ROI 列表，这样运行时**完全复用**既有通路（逐 ROI 裁剪 → OCR → 按类型决定翻不翻），
    /// 不必为"预设"另写一套流程。名字带 <see cref="PresetNamePrefix"/> 标记以便与用户校准框区分。
    /// </summary>
    public IReadOnlyList<Roi> ToRois()
    {
        var list = new List<Roi>();
        if (Chat is { } chat)
            list.Add(new Roi($"{PresetNamePrefix}Chat", "Chat", chat));
        if (RoomNameList is { } rnl)
            list.Add(new Roi($"{PresetNamePrefix}RoomNameList", "RoomNameList", rnl));
        if (GameName is { } gn)
            list.Add(new Roi($"{PresetNamePrefix}GameName", "GameName", gn));
        if (MapName is { } mn)
            list.Add(new Roi($"{PresetNamePrefix}MapName", "MapName", mn));
        return list;
    }

    /// <summary>解析分辨率键（"1920x1080"→(1920,1080)）。仅旧 roomList 网格格式的像素→相对换算要用。</summary>
    internal static (int W, int H) ParseSize(string resolution)
    {
        var parts = (resolution ?? "").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            return (w, h);
        return (1, 1);
    }
}

/// <summary>
/// layout-presets.json 的只读加载器（出厂层）。与 <see cref="SceneRefStore"/> 同一套纪律：
/// 加载失败不抛异常、原因进 <see cref="LoadError"/>；分辨率精确匹配，缺该档就回落到校准 ROI。
/// </summary>
public sealed class LayoutPresetStore
{
    public const int SupportedVersion = 1;
    public const string DefaultFileName = "layout-presets.json";
    /// <summary>用户编辑预设时落的覆盖层文件名（与 rois.json 同目录，跟出厂层分家，升级 exe 不丢）。</summary>
    public const string OverridesFileName = "layout-overrides.json";

    public IReadOnlyList<LayoutPreset> Presets { get; private init; } = Array.Empty<LayoutPreset>();
    public string? LoadError { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>来自覆盖层（用户在配置页改的）条目数。</summary>
    public int OverrideCount { get; private init; }

    /// <summary>加载时用的两个路径。暴露出来是为了让 <see cref="LayoutPresetStore"/> 的单例
    /// 能被调用方**原地重载**（配置页改了覆盖层后，运行时必须能重新读盘）。</summary>
    public string FactoryPath { get; private init; } = "";
    public string OverridesPath { get; private init; } = "";

    public bool IsUsable => LoadError is null && Presets.Count > 0;

    public LayoutPreset? Find(string sceneId, int width, int height)
        => Presets.FirstOrDefault(p => p.Scene == sceneId && p.Resolution == $"{width}x{height}");

    /// <summary>该场景是否有任何预设（用于"没校准也能翻"的可用性判断）。</summary>
    public bool HasScene(string sceneId) => Presets.Any(p => p.Scene == sceneId);

    /// <summary>读出厂层 + 可选覆盖层。覆盖层坏了只告警、不连累出厂层。</summary>
    public static LayoutPresetStore Load(string factoryPath, string? overridesPath = null)
    {
        var warnings = new List<string>();
        var factory = ReadFile(factoryPath, "出厂布局预设", warnings, out var factoryError);
        if (factoryError is not null) return new LayoutPresetStore { LoadError = factoryError, Warnings = warnings };

        var merged = factory;
        int overrideCount = 0;
        if (overridesPath is not null && File.Exists(overridesPath))
        {
            var over = ReadFile(overridesPath, "布局预设覆盖层", warnings, out var overrideError);
            if (overrideError is not null)
                warnings.Add(overrideError + "（已忽略覆盖层，仅用出厂预设）");
            else
                (merged, overrideCount) = Merge(factory, over);
        }
        return new LayoutPresetStore
        {
            Presets = merged,
            OverrideCount = overrideCount,
            Warnings = warnings,
            FactoryPath = factoryPath,
            OverridesPath = overridesPath ?? "",
        };
    }

    /// <summary>同 (场景,分辨率) 覆盖层胜出；新场景/新分辨率直接追加。</summary>
    static (IReadOnlyList<LayoutPreset>, int) Merge(IReadOnlyList<LayoutPreset> factory, IReadOnlyList<LayoutPreset> overrides)
    {
        var result = factory.ToList();
        int applied = 0;
        foreach (var o in overrides)
        {
            applied++;
            int idx = result.FindIndex(p => p.Scene == o.Scene && p.Resolution == o.Resolution);
            if (idx < 0) result.Add(o);
            else result[idx] = o;
        }
        return (result, applied);
    }

    /// <summary>把一条预设写进覆盖层文件（同 (场景,分辨率) 整条替换）。出厂文件永不被程序改写。</summary>
    public static void SaveOverride(string overridesPath, LayoutPreset preset)
    {
        var existing = File.Exists(overridesPath)
            ? ReadFile(overridesPath, "布局预设覆盖层", new List<string>(), out _)
            : Array.Empty<LayoutPreset>();
        var next = Upsert(existing, preset);
        string? dir = Path.GetDirectoryName(overridesPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(overridesPath, ToJson(next), new UTF8Encoding(false));
    }

    /// <summary>从覆盖层删掉某条 = 该 (场景,分辨率) 恢复成出厂值。</summary>
    public static void RemoveOverride(string overridesPath, string scene, string resolution)
    {
        if (!File.Exists(overridesPath)) return;
        var existing = ReadFile(overridesPath, "布局预设覆盖层", new List<string>(), out _);
        var next = existing.Where(p => !(p.Scene == scene && p.Resolution == resolution)).ToList();
        File.WriteAllText(overridesPath, ToJson(next), new UTF8Encoding(false));
    }

    static IReadOnlyList<LayoutPreset> Upsert(IReadOnlyList<LayoutPreset> list, LayoutPreset preset)
    {
        var next = list.ToList();
        int idx = next.FindIndex(p => p.Scene == preset.Scene && p.Resolution == preset.Resolution);
        if (idx < 0) next.Add(preset);
        else next[idx] = preset;
        return next;
    }

    /// <summary>序列化回 JSON。手写而不是 JsonSerializer：字段顺序要和出厂文件一致，diff 才看得下去。</summary>
    public static string ToJson(IReadOnlyList<LayoutPreset> presets)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": 1,\n");
        sb.Append("  \"note\": \"用户在配置页改的界面布局预设覆盖层；同 (场景,分辨率) 覆盖出厂 layout-presets.json。删掉本文件即恢复出厂值。\",\n");
        sb.Append("  \"presets\": {");
        bool firstScene = true;
        foreach (var g in presets.GroupBy(p => p.Scene))
        {
            if (!firstScene) sb.Append(',');
            firstScene = false;
            sb.Append($"\n    \"{g.Key}\": {{");
            bool firstRes = true;
            foreach (var p in g)
            {
                if (!firstRes) sb.Append(',');
                firstRes = false;
                sb.Append($"\n      \"{p.Resolution}\": {{");
                var parts = new List<string>();
                if (p.Chat is { } c) parts.Add($" \"chat\": [{c.X}, {c.Y}, {c.W}, {c.H}]");
                if (p.RoomNameList is { } rnl) parts.Add($" \"roomNameList\": [{rnl.X}, {rnl.Y}, {rnl.W}, {rnl.H}]");
                if (p.GameName is { } gn) parts.Add($" \"gameName\": [{gn.X}, {gn.Y}, {gn.W}, {gn.H}]");
                if (p.MapName is { } mn) parts.Add($" \"mapName\": [{mn.X}, {mn.Y}, {mn.W}, {mn.H}]");
                sb.Append(string.Join(",", parts));
                sb.Append(" }");
            }
            sb.Append("\n    }");
        }
        sb.Append("\n  }\n}\n");
        return sb.ToString();
    }

    /// <summary>读一份预设文件（出厂层与覆盖层同一 schema）。</summary>
    static IReadOnlyList<LayoutPreset> ReadFile(string path, string what, List<string> warnings, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"找不到{what}文件 {Path.GetFileName(path)}";
                return Array.Empty<LayoutPreset>();
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.TryGetInt32(out int ver) && ver != SupportedVersion)
            {
                error = $"{what} version={ver}，本程序只支持 v{SupportedVersion}";
                return Array.Empty<LayoutPreset>();
            }
            if (!root.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Object)
            {
                error = $"{what} 缺少 presets 对象";
                return Array.Empty<LayoutPreset>();
            }
            var list = new List<LayoutPreset>();
            foreach (var sceneEl in presets.EnumerateObject())
                foreach (var resEl in sceneEl.Value.EnumerateObject())
                {
                    var p = ParseOne(sceneEl.Name, resEl.Name, resEl.Value, warnings);
                    if (p is not null) list.Add(p);
                }
            if (list.Count == 0) { error = $"{what} 没有任何可用条目"; return Array.Empty<LayoutPreset>(); }
            return list;
        }
        catch (Exception ex)
        {
            error = $"{what} 解析失败：{ex.Message}";
            return Array.Empty<LayoutPreset>();
        }
    }

    static LayoutPreset? ParseOne(string scene, string resolution, JsonElement el, List<string> warnings)
    {
        try
        {
            RelRect? chat = ParseRect(el, "chat", scene, resolution, warnings);

            // 新格式：roomNameList 是简单矩形 [x, y, w, h]（相对 0~1）。
            // 旧格式兼容：roomList 是网格对象 { list, rowHeight, columns } —— 只取 list 矩形（当时存的是像素），换算成相对。
            RelRect? roomNameList = ParseRect(el, "roomNameList", scene, resolution, warnings);
            if (roomNameList is null && el.TryGetProperty("roomList", out var oldG) && oldG.ValueKind == JsonValueKind.Object)
            {
                var list = oldG.GetProperty("list").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var (fw, fh) = LayoutPreset.ParseSize(resolution);
                if (list.Length == 4 && list[2] > 0 && list[3] > 0)
                    roomNameList = new RelRect(list[0] / fw, list[1] / fh, list[2] / fw, list[3] / fh);
                else warnings.Add($"{scene}/{resolution} 的旧 roomList 参数非法，已跳过");
            }

            RelRect? gameName = ParseRect(el, "gameName", scene, resolution, warnings);
            RelRect? mapName = ParseRect(el, "mapName", scene, resolution, warnings);

            if (chat is null && roomNameList is null && gameName is null && mapName is null)
            {
                warnings.Add($"{scene}/{resolution} 既无 chat/roomNameList 也无 gameName/mapName，已跳过");
                return null;
            }
            return new LayoutPreset(scene, resolution, chat, roomNameList, gameName, mapName);
        }
        catch (Exception ex)
        {
            warnings.Add($"{scene}/{resolution} 解析失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>解析 <c>[x, y, w, h]</c> 相对矩形（0~1，全精度）。属性缺省→null（该区域无预设）；值非法→告警并返回 null。</summary>
    static RelRect? ParseRect(JsonElement el, string name, string scene, string resolution, List<string> warnings)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        if (a.Length == 4 && a[2] > 0 && a[3] > 0) return new RelRect(a[0], a[1], a[2], a[3]);
        warnings.Add($"{scene}/{resolution} 的 {name} 矩形非法：[{string.Join(",", a)}]");
        return null;
    }
}
