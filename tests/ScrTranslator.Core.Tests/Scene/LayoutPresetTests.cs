using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 界面布局预设（聊天框 + 房间列表网格）。
/// 换算规则是承重墙：列的 x 相对**列表框左边缘**、ROI 列区间相对**框宽**、
/// 而整框相对**画面**——三种口径混在一套房子里，错一处就翻错列。
/// </summary>
public class LayoutPresetTests
{
    private const string Json = """
        { "version": 1,
          "presets": { "browsing": {
            "1920x1080": { "chat": [280, 740, 800, 247],
              "roomList": { "list": [354, 305, 685, 382], "rowHeight": 38,
                "columns": [ { "kind": "RoomName", "x": 78, "width": 276 },
                             { "kind": "MapName",  "x": 503, "width": 96 } ] } },
            "3840x2160": { "chat": [560, 1480, 1600, 494] } } } }
        """;

    static string TempWith(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_FindsByExactResolutionOnly()
    {
        var store = LayoutPresetStore.Load(TempWith(Json));

        Assert.Null(store.LoadError);
        Assert.True(store.IsUsable);
        Assert.NotNull(store.Find("browsing", 1920, 1080));
        Assert.Null(store.Find("browsing", 2560, 1440));   // 没写就是没有，绝不借用邻近分辨率
        Assert.False(store.HasScene("room"));
        Assert.True(store.HasScene("browsing"));
    }

    [Fact]
    public void ToRois_ChatAndGrid_NormalizedAgainstTheRightReference()
    {
        var p = LayoutPresetStore.Load(TempWith(Json)).Find("browsing", 1920, 1080)!;
        var rois = p.ToRois();

        Assert.Equal(2, rois.Count);
        var chat = rois.First(r => r.Type == "Chat");
        Assert.Equal(280 / 1920d, chat.Rect.X, 6);
        Assert.Equal(740 / 1080d, chat.Rect.Y, 6);
        Assert.Equal(800 / 1920d, chat.Rect.W, 6);
        Assert.Equal(247 / 1080d, chat.Rect.H, 6);

        var grid = rois.First(r => r.Type == "RoomList");
        Assert.Equal(354 / 1920d, grid.Rect.X, 6);          // 整框相对**画面**
        var cols = grid.Columns!;
        Assert.Equal(78 / 685d, cols[0].RelStart, 6);       // 列相对**列表框宽**
        Assert.Equal(354 / 685d, cols[0].RelEnd, 6);
        Assert.Equal(ColumnKind.RoomName, cols[0].Kind);
        Assert.Equal(503 / 685d, cols[1].RelStart, 6);
        Assert.Equal(599 / 685d, cols[1].RelEnd, 6);
        Assert.Equal(ColumnKind.MapName, cols[1].Kind);
    }

    [Fact]
    public void ToRois_SceneWithOnlyChat_ReturnsSingleRoi()
    {
        var p = LayoutPresetStore.Load(TempWith(Json)).Find("browsing", 3840, 2160)!;
        var rois = p.ToRois();

        Assert.Single(rois);
        Assert.Equal("Chat", rois[0].Type);
        Assert.Equal(560 / 3840d, rois[0].Rect.X, 6);
        Assert.True(p.RoomList is null);
    }

    [Theory]
    [InlineData("{ \"version\": 9, \"presets\": {} }", "version")]
    [InlineData("{ presets: broken", "解析失败")]
    public void Load_BadFile_ReportsErrorNotThrow(string json, string expectInError)
    {
        var store = LayoutPresetStore.Load(TempWith(json));
        Assert.False(store.IsUsable);
        Assert.Contains(expectInError, store.LoadError);
    }

    [Fact]
    public void Load_MissingFile_ReportsErrorNotThrow()
    {
        var store = LayoutPresetStore.Load(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid() + ".json"));
        Assert.False(store.IsUsable);
        Assert.Contains("找不到", store.LoadError);
    }

    [Fact]
    public void Load_IllegalGridParams_KeepsTheRestAndWarns()
    {
        // 网格参数坏了只丢网格，聊天框照常生效——一条坏数据不该把整个场景的预设拖死，
        // 但必须留下告警，否则"列表不翻"这种半残状态没人知道为什么。
        var store = LayoutPresetStore.Load(TempWith(Json.Replace("\"rowHeight\": 38", "\"rowHeight\": 0")));

        Assert.Contains(store.Warnings, w => w.Contains("roomList 参数非法"));
        var p = Assert.Single(store.Presets, x => x.Resolution == "1920x1080");
        Assert.True(p.RoomList is null);
        Assert.True(p.Chat is not null);
    }

    [Fact]
    public void Load_EmptySceneEntry_IsSkipped()
    {
        var store = LayoutPresetStore.Load(TempWith(
            """{ "version": 1, "presets": { "room": { "1920x1080": {} } } }"""));
        Assert.Empty(store.Presets);
        Assert.Contains(store.Warnings, w => w.Contains("既无 chat 也无 roomList"));
    }

    // ---- 覆盖层（用户在配置页改预设）----

    [Fact]
    public void Override_WinsPerSceneResolution_AndIsCounted()
    {
        var factory = TempWith(Json);
        var over = TempWith("""
            { "version": 1, "presets": { "browsing": {
              "1920x1080": { "chat": [10, 20, 30, 40] } } } }
            """);

        var store = LayoutPresetStore.Load(factory, over);

        Assert.Equal(1, store.OverrideCount);
        var p = store.Find("browsing", 1920, 1080)!;
        Assert.Equal(10, p.Chat!.X);                       // 覆盖层胜出
        Assert.True(p.RoomList is null);                    // 整条替换，不是逐字段合并
        Assert.Equal(560, store.Find("browsing", 3840, 2160)!.Chat!.X);   // 另一档没被牵连，仍是出厂值
    }

    [Fact]
    public void SaveOverride_RoundTripsThroughLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        var p = new LayoutPreset("ingame", "3840x2160", new PixelRect(16, 470, 2296, 830), null);

        LayoutPresetStore.SaveOverride(path, p);
        var back = LayoutPresetStore.Load(TempWith(Json), path);

        var found = back.Find("ingame", 3840, 2160);
        Assert.NotNull(found);
        Assert.Equal(2296, found!.Chat!.Width);
        Assert.True(found.RoomList is null);
        Assert.Equal(1, back.OverrideCount);
        File.Delete(path);
    }

    [Fact]
    public void SaveOverride_Upserts_NotAppends()
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("room", "1920x1080", new PixelRect(1, 2, 3, 4), null));
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("room", "1920x1080", new PixelRect(9, 9, 9, 9), null));

        var store = LayoutPresetStore.Load(TempWith(Json), path);
        Assert.Equal(1, store.OverrideCount);
        Assert.Equal(9, store.Find("room", 1920, 1080)!.Chat!.X);
        File.Delete(path);
    }

    [Fact]
    public void RemoveOverride_FallsBackToFactory()
    {
        var factory = TempWith(Json);
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("browsing", "1920x1080", new PixelRect(1, 1, 1, 1), null));

        LayoutPresetStore.RemoveOverride(path, "browsing", "1920x1080");
        var store = LayoutPresetStore.Load(factory, path);

        Assert.Equal(0, store.OverrideCount);
        Assert.Equal(280, store.Find("browsing", 1920, 1080)!.Chat!.X);   // 回到出厂值
        File.Delete(path);
    }

    [Fact]
    public void BrokenOverride_DoesNotTakeFactoryDown()
    {
        var over = TempWith("{ presets: 这不是 json ");
        var store = LayoutPresetStore.Load(TempWith(Json), over);

        Assert.True(store.IsUsable, "覆盖层坏了不该连累出厂层");
        Assert.NotNull(store.Find("browsing", 1920, 1080));
        Assert.Contains(store.Warnings, w => w.Contains("已忽略覆盖层"));
    }

    // ---- 出厂预设回归 ----

    [Fact]
    public void ShippingPresets_EverySceneHasAllThreeResolutions()
    {
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));

        Assert.True(store.IsUsable, store.LoadError);
        Assert.Empty(store.Warnings);
        foreach (var scene in new[] { "browsing", "room", "ingame" })
            foreach (var res in new[] { "1920x1080", "2560x1440", "3840x2160" })
            {
                var (w, h) = (int.Parse(res.Split('x')[0]), int.Parse(res.Split('x')[1]));
                var p = store.Find(scene, w, h);
                Assert.True(p is not null, $"{scene} 缺 {res} 预设");
                Assert.True(p!.Chat is not null, $"{scene}/{res} 没有聊天框");
            }
    }

    [Fact]
    public void ShippingPresets_ChatBoxesSitInsideTheirFrame()
    {
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        foreach (var p in store.Presets)
        {
            var parts = p.Resolution.Split('x');
            int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
            var c = p.Chat!;
            Assert.True(c.X >= 0 && c.Y >= 0, $"{p.Scene}/{p.Resolution} 聊天框有负坐标");
            Assert.True(c.X + c.Width <= w, $"{p.Scene}/{p.Resolution} 聊天框横向超出画面");
            Assert.True(c.Y + c.Height <= h, $"{p.Scene}/{p.Resolution} 聊天框纵向超出画面");
            // 聊天框不该小于一行字，也不该吃掉半个屏幕（那样会把 HUD/视口一起 OCR 进来）
            Assert.True(c.Height >= h * 0.10 && c.Height <= h * 0.55, $"{p.Scene}/{p.Resolution} 聊天框高度占比异常");
            Assert.True(c.Width >= w * 0.15 && c.Width <= w * 0.65, $"{p.Scene}/{p.Resolution} 聊天框宽度占比异常");
        }
    }

    [Fact]
    public void ShippingPresets_OnlyBrowsingCarriesTheRoomListGrid()
    {
        // 房间列表网格只属于"寻找房间"；房间内/游戏内没有这张表，配错了说明预设串了场景。
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        Assert.All(store.Presets.Where(p => p.Scene != "browsing"), p => Assert.True(p.RoomList is null));
        Assert.All(store.Presets.Where(p => p.Scene == "browsing"),
            p =>
            {
                Assert.True(p.RoomList is not null);
                Assert.True(p.RoomList!.Rows >= 5, $"{p.Resolution} 列表只容 {p.RoomList.Rows} 行，行高或框高量错了");
                Assert.Equal(new[] { ColumnKind.RoomName, ColumnKind.MapName }, p.RoomList.Columns.Select(c => c.Kind));
                // 两列之间隔着 Game Type，必须不重叠且都在框内
                var (a, b) = (p.RoomList.Columns[0], p.RoomList.Columns[1]);
                Assert.True(b.X > a.X + a.Width, $"{p.Resolution} 地图名列与房间名列重叠");
                Assert.True(a.X + a.Width < p.RoomList.Width && b.X + b.Width < p.RoomList.Width,
                    $"{p.Resolution} 列超出列表框");
            });
    }

    [Fact]
    public void ShippingPresets_ThreeResolutionsAreMutuallyConsistent()
    {
        // 三档应是同一套 UI 的等比呈现；偏差过大说明某一档是手填的而不是量/推算出来的。
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        var b = store.Find("browsing", 1920, 1080)!;
        foreach (var (w, h, s) in new[] { (2560, 1440, 2560 / 1920d), (3840, 2160, 2d) })
        {
            var p = store.Find("browsing", w, h)!;
            void Close(double expect, int actual, double tol, string what)
                => Assert.True(Math.Abs(expect - actual) <= tol,
                    $"{w}x{h} {what}={actual} 与 1080p 推算 {expect:F1} 偏差超 {tol:F1}");
            Close(b.Chat!.X * s, p.Chat!.X, 3 * s, "chat.x");
            Close(b.Chat.Width * s, p.Chat.Width, 6 * s, "chat.宽");
            Close(b.RoomList!.RowHeight * s, p.RoomList!.RowHeight, 2 * s, "rowHeight");
            Close(b.RoomList.Width * s, p.RoomList.Width, 6 * s, "list.宽");
        }
    }
}
