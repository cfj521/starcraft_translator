using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 房间列表固定网格预设：模型换算 + JSON 加载 + 出厂文件回归。
/// 换算规则是这套设计的承重墙——列的 x 是**相对列表框左边缘**的像素偏移，
/// 而 ROI 列区间是**相对框宽**的比例，两者一旦混用，房间名/地图名就会互相翻错列。
/// </summary>
public class RoomGridTests
{
    private const string Json = """
        { "version": 1,
          "grids": { "browsing": {
            "1920x1080": { "list": [360, 292, 685, 400], "rowHeight": 50,
              "columns": [ { "kind": "RoomName", "x": 80, "width": 270 },
                           { "kind": "MapName",  "x": 510, "width": 90 } ] } } } }
        """;

    static string TempWith(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "room-grid-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_ValidPreset_FindsByExactResolutionOnly()
    {
        var store = RoomGridStore.Load(TempWith(Json));

        Assert.Null(store.LoadError);
        Assert.True(store.IsUsable);
        var p = store.Find("browsing", 1920, 1080);
        Assert.NotNull(p);
        Assert.Equal(8, p!.Rows);                                   // 400 / 50
        Assert.Null(store.Find("browsing", 2560, 1440));            // 没写这个分辨率就是没有，绝不借用
        Assert.Null(store.Find("room", 1920, 1080));                // 别的场景
    }

    [Fact]
    public void ToRoi_NormalizesByThatResolution_AndColumnsAreRelativeToListBox()
    {
        var p = RoomGridStore.Load(TempWith(Json)).Find("browsing", 1920, 1080)!;
        var roi = p.ToRoi();

        Assert.Equal("RoomList", roi.Type);
        Assert.Equal(360 / 1920d, roi.Rect.X, 6);
        Assert.Equal(292 / 1080d, roi.Rect.Y, 6);
        Assert.Equal(685 / 1920d, roi.Rect.W, 6);
        Assert.Equal(400 / 1080d, roi.Rect.H, 6);

        // 列区间 = (x + width) / 列表框宽，且是"相对框"而非"相对屏幕"——地图名列 510 起不能被当成屏幕坐标
        var cols = roi.Columns!;
        Assert.Equal(2, cols.Count);
        Assert.Equal(80 / 685d, cols[0].RelStart, 6);
        Assert.Equal(350 / 685d, cols[0].RelEnd, 6);
        Assert.Equal(ColumnKind.RoomName, cols[0].Kind);
        Assert.Equal(510 / 685d, cols[1].RelStart, 6);
        Assert.Equal(600 / 685d, cols[1].RelEnd, 6);
        Assert.Equal(ColumnKind.MapName, cols[1].Kind);
    }

    [Fact]
    public void Columns_TwoColumnsAreNotAdjacent_GameTypeGapIsPreserved()
    {
        // 实测布局：Players | Game Name | Game Type | Map Name —— 两列之间有间隙。
        // 若只存"两个列宽"（没有 x 起点），第二列会被顶到第一列右边，正好盖住 Game Type。
        var p = RoomGridStore.Load(TempWith(Json)).Find("browsing", 1920, 1080)!;
        var cols = p.Columns;
        Assert.True(cols[1].X > cols[0].X + cols[0].Width,
            "地图名列起点必须在房间名列终点之后（中间隔着 Game Type）");
    }

    [Fact]
    public void Load_MissingFile_ReportsErrorNotThrow()
    {
        var store = RoomGridStore.Load(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid() + ".json"));
        Assert.False(store.IsUsable);
        Assert.Contains("找不到", store.LoadError);
    }

    [Fact]
    public void Load_BadVersion_Refuses()
    {
        var store = RoomGridStore.Load(TempWith(Json.Replace("\"version\": 1", "\"version\": 9")));
        Assert.False(store.IsUsable);
        Assert.Contains("version", store.LoadError);
    }

    [Fact]
    public void Load_TruncatedJson_ReportsErrorNotThrow()
    {
        var store = RoomGridStore.Load(TempWith("{ grids: { nope"));
        Assert.False(store.IsUsable);
        Assert.NotNull(store.LoadError);
    }

    [Fact]
    public void Load_IllegalRowHeight_SkipsPresetWithWarning()
    {
        var store = RoomGridStore.Load(TempWith(Json.Replace("\"rowHeight\": 50", "\"rowHeight\": 0")));
        Assert.Empty(store.Presets);
        Assert.Contains(store.Warnings, w => w.Contains("参数非法"));
    }

    // ---- 出厂预设回归 ----

    [Fact]
    public void ShippingPreset_LoadsAllThreeResolutions()
    {
        var store = RoomGridStore.Load(Path.Combine(AppContext.BaseDirectory, RoomGridStore.DefaultFileName));

        Assert.True(store.IsUsable, store.LoadError);
        Assert.Empty(store.Warnings);
        foreach (var res in new[] { "1920x1080", "2560x1440", "3840x2160" })
        {
            var (w, h) = (int.Parse(res.Split('x')[0]), int.Parse(res.Split('x')[1]));
            var p = store.Find("browsing", w, h);
            Assert.True(p is not null, $"{res} 缺预设");
            Assert.Equal(2, p!.Columns.Count);
            Assert.Equal(new[] { ColumnKind.RoomName, ColumnKind.MapName }, p.Columns.Select(c => c.Kind));
            Assert.True(p.RowHeight >= 20, $"{res} 行高 {p.RowHeight} 不合理（太小会把一行切成两半）");
            Assert.True(p.Rows >= 5, $"{res} 只容得下 {p.Rows} 行，列表框高度可能量错了");
            // 列表框必须完整落在该分辨率画面内
            Assert.True(p.X + p.Width <= w, $"{res} 列表框横向超出画面");
            Assert.True(p.Y + p.Height <= h, $"{res} 列表框纵向超出画面");
        }
    }

    [Fact]
    public void ShippingPreset_ThreeResolutionsAreMutuallyConsistent()
    {
        // 三套预设应当是同一块 UI 的等比呈现；偏差超过 3% 说明某一档是手填错的，而不是量出来的。
        var store = RoomGridStore.Load(Path.Combine(AppContext.BaseDirectory, RoomGridStore.DefaultFileName));
        var base1080 = store.Find("browsing", 1920, 1080)!;
        foreach (var (w, h, s) in new[] { (2560, 1440, 2560 / 1920d), (3840, 2160, 2d) })
        {
            var p = store.Find("browsing", w, h)!;
            void Close(double expect, int actual, double tolerance, string what)
                => Assert.True(Math.Abs(expect - actual) <= tolerance,
                    $"{w}x{h} 的 {what}={actual} 与 1080p 推算值 {expect:F1} 偏差超 {tolerance:F1}（该档预设大概是手填的，不是量出来的）");

            Close(base1080.X * s, p.X, 3 * s, "list.x");
            Close(base1080.Y * s, p.Y, 3 * s, "list.y");
            Close(base1080.Width * s, p.Width, 6 * s, "list.宽");
            Close(base1080.Height * s, p.Height, 6 * s, "list.高");
            Close(base1080.RowHeight * s, p.RowHeight, 2 * s, "rowHeight");
        }
    }
}
