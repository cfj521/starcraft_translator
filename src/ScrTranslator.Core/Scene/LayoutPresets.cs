using System.Text.Json;

namespace ScrTranslator.Core.Scene;

/// <summary>列表框内的一列：X 是相对**列表框左边缘**的像素偏移（不是屏幕坐标），Width 为该列像素宽。</summary>
public sealed record ListColumn(ColumnKind Kind, int X, int Width)
{
    /// <summary>换算成 ROI 列定义用的相对区间（相对列表框宽度，0~1）。</summary>
    public (double Start, double End) ToRelative(int listWidth)
    {
        if (listWidth <= 0) return (0, 0);
        return (Math.Clamp(X / (double)listWidth, 0d, 1d), Math.Clamp((X + Width) / (double)listWidth, 0d, 1d));
    }
}

/// <summary>自定义游戏列表的网格：整框 + 固定行高 + 需要翻译的列。</summary>
public sealed record RoomListGrid(int X, int Y, int Width, int Height, int RowHeight, IReadOnlyList<ListColumn> Columns)
{
    public int Rows => RowHeight > 0 ? Height / RowHeight : 0;
}

/// <summary>一个矩形区域（该分辨率下的整数像素）。</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// 某场景在某分辨率下的**界面布局预设**：聊天框 + （仅寻找房间）房间列表网格。
///
/// 为什么内置而不是校准：这些面板在给定分辨率下位置和尺寸是死的，让用户每次拖框既没准头也没必要。
/// 数值来源：1080p 从 docs/scenes pictures 的截图量得（列表用绿色人数槽的 y 周期 + 表头亮字团定列；
/// 聊天框用面板边框；游戏内聊天用 ingame-w/h 两张专用图定出单行最大宽度与最大行数），
/// 2K/4K 按 4/3、2 倍等比推算并用叠加图逐档目视核验。校准/编辑能力保留，用于改版时覆盖。
///
/// 坐标一律是该分辨率下的整数像素，且**分辨率必须精确匹配**——UI 不是严格等比缩放，
/// 借用邻近分辨率的预设会让框子整体偏移。
/// </summary>
public sealed record LayoutPreset(string Scene, string Resolution, PixelRect? Chat, RoomListGrid? RoomList)
{
    /// <summary>
    /// 展开成普通 ROI 列表，这样运行时**完全复用**既有通路（逐 ROI 裁剪 → OCR → 按类型/列决定翻不翻），
    /// 不必为"预设"另写一套流程。名字带"(预设)"以便在区域页与校准结果区分。
    /// </summary>
    public IReadOnlyList<Roi> ToRois()
    {
        var (fw, fh) = ParseSize(Resolution);
        var list = new List<Roi>();
        if (Chat is { } chat)
            list.Add(new Roi($"{Scene}·聊天(预设)", "Chat", Rel(chat, fw, fh)));
        if (RoomList is not null)
        {
            var g = RoomList;
            var cols = g.Columns
                .Select(c => { var (s, e) = c.ToRelative(g.Width); return new RoiColumn(s, e, c.Kind); })
                .ToList();
            list.Add(new Roi($"{Scene}·房间列表(预设)", "RoomList",
                Rel(new PixelRect(g.X, g.Y, g.Width, g.Height), fw, fh), cols));
        }
        return list;
    }

    static RelRect Rel(PixelRect r, int fw, int fh) => new(
        r.X / (double)fw, r.Y / (double)fh, r.Width / (double)fw, r.Height / (double)fh);

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

    public IReadOnlyList<LayoutPreset> Presets { get; private init; } = Array.Empty<LayoutPreset>();
    public string? LoadError { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    public bool IsUsable => LoadError is null && Presets.Count > 0;

    public LayoutPreset? Find(string sceneId, int width, int height)
        => Presets.FirstOrDefault(p => p.Scene == sceneId && p.Resolution == $"{width}x{height}");

    /// <summary>该场景是否有任何预设（用于"没校准也能翻"的可用性判断）。</summary>
    public bool HasScene(string sceneId) => Presets.Any(p => p.Scene == sceneId);

    public static LayoutPresetStore Load(string path)
    {
        var warnings = new List<string>();
        try
        {
            if (!File.Exists(path))
                return new LayoutPresetStore { LoadError = $"找不到界面布局预设文件 {Path.GetFileName(path)}" };

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.TryGetInt32(out int ver) && ver != SupportedVersion)
                return new LayoutPresetStore { LoadError = $"{path} version={ver}，本程序只支持 v{SupportedVersion}" };
            if (!root.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Object)
                return new LayoutPresetStore { LoadError = "layout-presets.json 缺少 presets 对象" };

            var list = new List<LayoutPreset>();
            foreach (var sceneEl in presets.EnumerateObject())
                foreach (var resEl in sceneEl.Value.EnumerateObject())
                {
                    var p = ParseOne(sceneEl.Name, resEl.Name, resEl.Value, warnings);
                    if (p is not null) list.Add(p);
                }
            return new LayoutPresetStore { Presets = list, Warnings = warnings };
        }
        catch (Exception ex)
        {
            return new LayoutPresetStore { LoadError = $"layout-presets.json 解析失败：{ex.Message}" };
        }
    }

    static LayoutPreset? ParseOne(string scene, string resolution, JsonElement el, List<string> warnings)
    {
        try
        {
            PixelRect? chat = null;
            if (el.TryGetProperty("chat", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                var a = c.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                if (a.Length == 4 && a[2] > 0 && a[3] > 0) chat = new PixelRect(a[0], a[1], a[2], a[3]);
                else warnings.Add($"{scene}/{resolution} 的 chat 矩形非法：[{string.Join(",", a)}]");
            }

            RoomListGrid? grid = null;
            if (el.TryGetProperty("roomList", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                var list = g.GetProperty("list").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                int rowHeight = g.GetProperty("rowHeight").GetInt32();
                var cols = new List<ListColumn>();
                foreach (var col in g.GetProperty("columns").EnumerateArray())
                    cols.Add(new ListColumn(
                        ColumnKinds.Parse(col.GetProperty("kind").GetString() ?? "Other"),
                        col.GetProperty("x").GetInt32(), col.GetProperty("width").GetInt32()));
                if (list.Length != 4 || list[2] <= 0 || list[3] <= 0 || rowHeight <= 0 || cols.Count == 0)
                    warnings.Add($"{scene}/{resolution} 的 roomList 参数非法（list={list.Length} rowHeight={rowHeight} columns={cols.Count}）");
                else grid = new RoomListGrid(list[0], list[1], list[2], list[3], rowHeight, cols);
            }

            if (chat is null && grid is null)
            {
                warnings.Add($"{scene}/{resolution} 既无 chat 也无 roomList，已跳过");
                return null;
            }
            return new LayoutPreset(scene, resolution, chat, grid);
        }
        catch (Exception ex)
        {
            warnings.Add($"{scene}/{resolution} 解析失败：{ex.Message}");
            return null;
        }
    }
}
