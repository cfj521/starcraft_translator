using System.Text;
using System.Text.Json;

namespace ScrTranslator.Core.Scene;

/// <summary>
/// 某场景在某个宽高比档（16:9 / 16:10 / 4:3）下的**界面布局预设**：聊天框 + （仅寻找房间）房间名列表区域 + （仅房间内）房间名称/地图名称。
///
/// 为什么内置而不是校准：这些面板在给定宽高比下位置和尺寸是死的，让用户每次拖框既没准头也没必要。
/// 数值来源：1080p 从 docs/scenes pictures 的截图量得，2K/4K 按 4/3、2 倍等比推算并用叠加图逐档目视核验。
/// 校准/编辑能力保留，用于改版时覆盖。
///
/// 坐标一律是**相对内容区的 0~1 比例**。UI 等比缩放，同一宽高比档跨分辨率通用，
/// 运行时按当前帧宽高比选最接近的预设档（见 <see cref="LayoutPresetStore.Find"/>），不按分辨率分档。
/// </summary>
public sealed record LayoutPreset(string Scene, string Resolution, RelRect? Chat, RelRect? RoomNameList = null,
    RelRect? GameName = null, RelRect? MapName = null)
{
    /// <summary>
    /// 预设的识别区域（名字 + 相对矩形）——名字就是字段名本身。
    /// 翻译管线与 UI 直接消费这个列表，不再包成 ROI。
    /// </summary>
    public IReadOnlyList<(string Name, RelRect Rect)> Parts()
    {
        var list = new List<(string Name, RelRect Rect)>();
        if (Chat is { } chat)
            list.Add(("chat", chat));
        if (RoomNameList is { } rnl)
            list.Add(("roomNameList", rnl));
        if (GameName is { } gn)
            list.Add(("gameName", gn));
        if (MapName is { } mn)
            list.Add(("mapName", mn));
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
/// layout-presets.json 直接读写器（出厂即唯一权威）。与 <see cref="SceneRefStore"/> 同一套纪律：
/// 加载失败不抛异常、原因进 <see cref="LoadError"/>；按宽高比选最接近的预设档。
/// </summary>
public sealed class LayoutPresetStore
{
    public const int SupportedVersion = 1;
    public const string DefaultFileName = "layout-presets.json";

    public IReadOnlyList<LayoutPreset> Presets { get; private init; } = Array.Empty<LayoutPreset>();
    public string? LoadError { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>本 store 实例读出的预设文件路径。暴露出来是为了让单例能被调用方**原地重载**
    /// （配置页改了预设后，运行时必须重新读盘）。</summary>
    public string FilePath { get; private init; } = "";

    public bool IsUsable => LoadError is null && Presets.Count > 0;

    /// <summary>按宽高比找最接近的预设档。Resolution 字段支持 "16:9"（比例）或 "1920x1080"（分辨率）两种格式。</summary>
    public LayoutPreset? Find(string sceneId, int width, int height)
    {
        if (height <= 0) return null;
        double ratio = (double)width / height;
        LayoutPreset? best = null;
        double bestDiff = double.MaxValue;
        foreach (var p in Presets)
        {
            if (p.Scene != sceneId) continue;
            double presetRatio = ParseRatio(p.Resolution);
            if (presetRatio <= 0) continue;
            double diff = Math.Abs(presetRatio - ratio);
            if (diff < bestDiff) { bestDiff = diff; best = p; }
        }
        return best;
    }

    /// <summary>解析分辨率/比例键为宽高比。"16:9"→16/9, "1920x1080"→1920/1080。无法解析返回 0。</summary>
    internal static double ParseRatio(string key)
    {
        var colon = key.Split(':');
        if (colon.Length == 2 && int.TryParse(colon[0], out int cw) && int.TryParse(colon[1], out int ch) && ch > 0)
            return (double)cw / ch;
        var x = key.Split('x');
        if (x.Length == 2 && int.TryParse(x[0], out int rw) && int.TryParse(x[1], out int rh) && rh > 0)
            return (double)rw / rh;
        return 0;
    }

    /// <summary>该场景是否有任何预设（用于"没校准也能翻"的可用性判断）。</summary>
    public bool HasScene(string sceneId) => Presets.Any(p => p.Scene == sceneId);

    /// <summary>读预设文件（直接读出厂文件，不再有覆盖层）。</summary>
    public static LayoutPresetStore Load(string factoryPath)
    {
        var warnings = new List<string>();
        var presets = ReadFile(factoryPath, "布局预设", warnings, out var error);
        if (error is not null) return new LayoutPresetStore { LoadError = error, Warnings = warnings, FilePath = factoryPath };
        return new LayoutPresetStore { Presets = presets, Warnings = warnings, FilePath = factoryPath };
    }

    /// <summary>把一条预设直写出厂文件（同 (场景,分辨率) 整条替换）。
    /// 原子写纪律：先写临时文件再 File.Move 替换，避免写到一半被中断导致截断损坏。</summary>
    public static void Save(LayoutPreset preset, string path)
    {
        var existing = File.Exists(path)
            ? ReadFile(path, "布局预设", new List<string>(), out _)
            : Array.Empty<LayoutPreset>();
        var next = Upsert(existing, preset);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, ToJson(next), new UTF8Encoding(false));
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort 清理，忽略失败 */ }
            throw;
        }
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
        sb.Append("  \"note\": \"界面布局预设（出厂唯一权威）；同 (场景,分辨率) 整条替换。配置页/校准窗直写本文件。\",\n");
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
                if (p.Chat is { } c) parts.Add($" \"chat\": [{c.X}, {c.Y}, {c.W}, {c.H}, {c.OffsetX}, {c.OffsetY}]");
                if (p.RoomNameList is { } rnl) parts.Add($" \"roomNameList\": [{rnl.X}, {rnl.Y}, {rnl.W}, {rnl.H}, {rnl.OffsetX}, {rnl.OffsetY}]");
                if (p.GameName is { } gn) parts.Add($" \"gameName\": [{gn.X}, {gn.Y}, {gn.W}, {gn.H}, {gn.OffsetX}, {gn.OffsetY}]");
                if (p.MapName is { } mn) parts.Add($" \"mapName\": [{mn.X}, {mn.Y}, {mn.W}, {mn.H}, {mn.OffsetX}, {mn.OffsetY}]");
                sb.Append(string.Join(",", parts));
                sb.Append(" }");
            }
            sb.Append("\n    }");
        }
        sb.Append("\n  }\n}\n");
        return sb.ToString();
    }

    /// <summary>读一份预设文件。</summary>
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
        // [x, y, w, h]（旧，无偏移）或 [x, y, w, h, offsetX, offsetY]（新，物理像素偏移）
        if (a.Length == 4 && a[2] > 0 && a[3] > 0) return new RelRect(a[0], a[1], a[2], a[3]);
        if (a.Length == 6 && a[2] > 0 && a[3] > 0) return new RelRect(a[0], a[1], a[2], a[3], a[4], a[5]);
        warnings.Add($"{scene}/{resolution} 的 {name} 矩形非法：[{string.Join(",", a)}]");
        return null;
    }
}
