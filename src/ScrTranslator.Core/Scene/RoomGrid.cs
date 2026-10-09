using System.Text.Json;

namespace ScrTranslator.Core.Scene;

/// <summary>列表框内的一列：X 是相对**列表框左边缘**的像素偏移（不是屏幕坐标），Width 为该列像素宽。</summary>
public sealed record RoomGridColumn(ColumnKind Kind, int X, int Width)
{
    /// <summary>换算成 ROI 列定义用的相对区间（相对列表框宽度，0~1）。</summary>
    public (double Start, double End) ToRelative(int listWidth)
    {
        if (listWidth <= 0) return (0, 0);
        return (Math.Clamp(X / (double)listWidth, 0d, 1d), Math.Clamp((X + Width) / (double)listWidth, 0d, 1d));
    }
}

/// <summary>
/// 某个分辨率下"自定义游戏列表"的固定网格预设。
///
/// 为什么不需要校准：这块界面在给定分辨率下**位置和行高都是死的**，而且整行里只有
/// 房间名、地图名两列需要翻译（Players / Game Type / 绿色人数槽都不是文本目标）。
/// 与其让用户每次拖框，不如按分辨率内置一套参数；校准/编辑能力保留，用于版本改版时覆盖。
///
/// 坐标一律是该分辨率下的**整数像素**（与 scene-refs.json 同一口径：分辨率必须精确匹配，
/// 因为 UI 不是严格等比缩放的，1440p/2160p 的值必须由该分辨率自己的截图量出来）。
/// </summary>
public sealed record RoomGridPreset(
    string Scene, string Resolution, int X, int Y, int Width, int Height,
    int RowHeight, IReadOnlyList<RoomGridColumn> Columns)
{
    /// <summary>可见行数（列表框高度 ÷ 行高，向下取整）。</summary>
    public int Rows => RowHeight > 0 ? Height / RowHeight : 0;

    /// <summary>
    /// 展开成一条普通 ROI（类型 RoomList + 列定义），这样运行时**完全复用**既有的
    /// "整框一次 OCR → 按列区间决定翻不翻" 通路，不必为网格另写一套裁剪逻辑。
    /// 相对坐标按该分辨率换算，交给 RoiStore.ToPixels 还原成像素。
    /// </summary>
    public Roi ToRoi()
    {
        var (fw, fh) = ParseSize(Resolution);
        var cols = Columns
            .Select(c => { var (s, e) = c.ToRelative(Width); return new RoiColumn(s, e, c.Kind); })
            .ToList();
        return new Roi($"{Scene}·房间列表(预设)", "RoomList",
            new RelRect(X / (double)fw, Y / (double)fh, Width / (double)fw, Height / (double)fh), cols);
    }

    internal static (int W, int H) ParseSize(string resolution)
    {
        var parts = (resolution ?? "").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            return (w, h);
        return (1, 1);
    }
}

/// <summary>
/// room-grid.json 的只读加载器（出厂预设）。与 <see cref="SceneRefStore"/> 同一套纪律：
/// 加载失败不抛异常，原因进 <see cref="LoadError"/>；分辨率**精确匹配**才用，缺该分辨率就回落到校准 ROI。
/// </summary>
public sealed class RoomGridStore
{
    public const int SupportedVersion = 1;
    public const string DefaultFileName = "room-grid.json";

    public IReadOnlyList<RoomGridPreset> Presets { get; private init; } = Array.Empty<RoomGridPreset>();
    public string? LoadError { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    public bool IsUsable => LoadError is null && Presets.Count > 0;

    public RoomGridPreset? Find(string sceneId, int width, int height)
        => Presets.FirstOrDefault(p => p.Scene == sceneId && p.Resolution == $"{width}x{height}");

    public static RoomGridStore Load(string path)
    {
        var warnings = new List<string>();
        try
        {
            if (!File.Exists(path))
                return new RoomGridStore { LoadError = $"找不到房间列表预设文件 {Path.GetFileName(path)}" };

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.TryGetInt32(out int ver) && ver != SupportedVersion)
                return new RoomGridStore { LoadError = $"room-grid.json version={ver}，本程序只支持 v{SupportedVersion}" };
            if (!root.TryGetProperty("grids", out var grids) || grids.ValueKind != JsonValueKind.Object)
                return new RoomGridStore { LoadError = "room-grid.json 缺少 grids 对象" };

            var list = new List<RoomGridPreset>();
            foreach (var sceneEl in grids.EnumerateObject())
                foreach (var resEl in sceneEl.Value.EnumerateObject())
                {
                    var p = ParseOne(sceneEl.Name, resEl.Name, resEl.Value, warnings);
                    if (p is not null) list.Add(p);
                }
            return new RoomGridStore { Presets = list, Warnings = warnings };
        }
        catch (Exception ex)
        {
            return new RoomGridStore { LoadError = $"room-grid.json 解析失败：{ex.Message}" };
        }
    }

    static RoomGridPreset? ParseOne(string scene, string resolution, JsonElement el, List<string> warnings)
    {
        try
        {
            var rect = el.GetProperty("list").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (rect.Length != 4) { warnings.Add($"{scene}/{resolution} 的 list 不是 4 个数"); return null; }
            int rowHeight = el.GetProperty("rowHeight").GetInt32();
            var cols = new List<RoomGridColumn>();
            foreach (var c in el.GetProperty("columns").EnumerateArray())
                cols.Add(new RoomGridColumn(
                    ColumnKinds.Parse(c.GetProperty("kind").GetString() ?? "Other"),
                    c.GetProperty("x").GetInt32(), c.GetProperty("width").GetInt32()));

            if (rowHeight <= 0 || rect[2] <= 0 || rect[3] <= 0 || cols.Count == 0)
            {
                warnings.Add($"{scene}/{resolution} 参数非法（rowHeight={rowHeight} list={rect[2]}x{rect[3]} columns={cols.Count}）");
                return null;
            }
            return new RoomGridPreset(scene, resolution, rect[0], rect[1], rect[2], rect[3], rowHeight, cols);
        }
        catch (Exception ex)
        {
            warnings.Add($"{scene}/{resolution} 解析失败：{ex.Message}");
            return null;
        }
    }
}
