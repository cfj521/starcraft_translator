using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 界面布局预设（聊天框 + 房间名列表区域 + 房间名称/地图名称）。
/// 预设直接存相对坐标（0~1），<see cref="LayoutPreset.ToRois"/> 不再换算、原样喂给运行时。
/// </summary>
public class LayoutPresetTests
{
    private const string Json = """
        { "version": 1,
          "presets": { "browsing": {
            "1920x1080": { "chat": [0.14583333333333333, 0.6851851851851852, 0.4166666666666667, 0.2287037037037037],
              "roomNameList": [0.225, 0.2824074074074074, 0.14375, 0.3537037037037037] },
            "3840x2160": { "chat": [0.14583333333333333, 0.6851851851851852, 0.4166666666666667, 0.2287037037037037] } } } }
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
    public void ToRois_ReturnsRelativeRectsAsIs()
    {
        var p = LayoutPresetStore.Load(TempWith(Json)).Find("browsing", 1920, 1080)!;
        var rois = p.ToRois();

        Assert.Equal(2, rois.Count);
        var chat = rois.First(r => r.Type == "Chat");
        Assert.Equal(280 / 1920d, chat.Rect.X, 6);
        Assert.Equal(740 / 1080d, chat.Rect.Y, 6);
        Assert.Equal(800 / 1920d, chat.Rect.W, 6);
        Assert.Equal(247 / 1080d, chat.Rect.H, 6);

        // 房间名列表是单一区域（相对画面），不再分列
        var rnl = rois.First(r => r.Type == "RoomNameList");
        Assert.Equal(432 / 1920d, rnl.Rect.X, 6);
        Assert.Equal(305 / 1080d, rnl.Rect.Y, 6);
        Assert.Equal(276 / 1920d, rnl.Rect.W, 6);
        Assert.Equal(382 / 1080d, rnl.Rect.H, 6);
        Assert.True(rnl.Columns is null, "房间名列表无列");
    }

    [Fact]
    public void ToRois_SceneWithOnlyChat_ReturnsSingleRoi()
    {
        var p = LayoutPresetStore.Load(TempWith(Json)).Find("browsing", 3840, 2160)!;
        var rois = p.ToRois();

        Assert.Single(rois);
        Assert.Equal("Chat", rois[0].Type);
        Assert.Equal(560 / 3840d, rois[0].Rect.X, 6);
        Assert.True(p.RoomNameList is null);
    }

    // ---- 房间名称 / 地图名称（room 场景新增的两块预设区域）----

    [Fact]
    public void ToRois_GameNameAndMapName_ReturnRelativeRects()
    {
        var p = new LayoutPreset("room", "1920x1080",
            Chat: new RelRect(270 / 1920d, 744 / 1080d, 805 / 1920d, 241 / 1080d),
            GameName: new RelRect(1085 / 1920d, 395 / 1080d, 360 / 1920d, 21 / 1080d),
            MapName: new RelRect(1085 / 1920d, 447 / 1080d, 360 / 1920d, 21 / 1080d));
        var rois = p.ToRois();

        Assert.Equal(3, rois.Count);
        var gn = rois.First(r => r.Type == "GameName");
        Assert.Equal(1085 / 1920d, gn.Rect.X, 6);
        Assert.Equal(395 / 1080d, gn.Rect.Y, 6);
        Assert.Equal(360 / 1920d, gn.Rect.W, 6);
        Assert.Equal(21 / 1080d, gn.Rect.H, 6);
        var mn = rois.First(r => r.Type == "MapName");
        Assert.Equal(1085 / 1920d, mn.Rect.X, 6);
        Assert.Equal(447 / 1080d, mn.Rect.Y, 6);
        Assert.True(gn.Columns is null && mn.Columns is null, "这两块区域无列");
    }

    [Fact]
    public void Load_ParsesGameNameAndMapName()
    {
        var store = LayoutPresetStore.Load(TempWith(
            """
            { "version": 1, "presets": { "room": {
              "1920x1080": { "chat": [0.140625, 0.6888888888888889, 0.41927083333333334, 0.22314814814814814],
                             "gameName": [0.56510416666666663, 0.3657407407407407, 0.1875, 0.019444444444444442],
                             "mapName": [0.56510416666666663, 0.4138888888888889, 0.1875, 0.019444444444444442] } } } }
            """));
        Assert.Null(store.LoadError);
        var p = store.Find("room", 1920, 1080)!;
        Assert.Equal(1085 / 1920d, p.GameName!.X, 6);
        Assert.Equal(21 / 1080d, p.GameName.H, 6);
        Assert.Equal(1085 / 1920d, p.MapName!.X, 6);
        Assert.Equal(447 / 1080d, p.MapName.Y, 6);
    }

    [Fact]
    public void SaveOverride_GameNameMapName_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        var p = new LayoutPreset("room", "1920x1080",
            Chat: new RelRect(270 / 1920d, 744 / 1080d, 805 / 1920d, 241 / 1080d),
            GameName: new RelRect(1085 / 1920d, 395 / 1080d, 360 / 1920d, 21 / 1080d),
            MapName: new RelRect(1085 / 1920d, 447 / 1080d, 360 / 1920d, 21 / 1080d));
        LayoutPresetStore.SaveOverride(path, p);
        var back = LayoutPresetStore.Load(TempWith(Json), path);
        var found = back.Find("room", 1920, 1080)!;
        Assert.Equal(1085 / 1920d, found.GameName!.X, 6);
        Assert.Equal(21 / 1080d, found.GameName.H, 6);
        Assert.Equal(447 / 1080d, found.MapName!.Y, 6);
        File.Delete(path);
    }

    [Fact]
    public void ShippingPresets_RoomSceneCarriesGameNameAndMapName()
    {
        // 出厂预设里 room 场景必须带这两块区域，漏了说明出厂文件没更新。
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        foreach (var res in new[] { "1920x1080", "2560x1440", "3840x2160" })
        {
            var (w, h) = (int.Parse(res.Split('x')[0]), int.Parse(res.Split('x')[1]));
            var p = store.Find("room", w, h)!;
            Assert.True(p.GameName is not null, $"room/{res} 缺 gameName");
            Assert.True(p.MapName is not null, $"room/{res} 缺 mapName");
        }
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
    public void Load_IllegalRoomNameListRect_KeepsTheRestAndWarns()
    {
        // 房间名列表矩形坏了只丢这一块，聊天框照常生效——一条坏数据不该把整个场景的预设拖死，
        // 但必须留下告警，否则"列表不翻"这种半残状态没人知道为什么。
        var store = LayoutPresetStore.Load(TempWith(Json.Replace(
            "[0.225, 0.2824074074074074, 0.14375, 0.3537037037037037]",
            "[0.225, 0.2824074074074074, 0, 0.3537037037037037]")));

        Assert.Contains(store.Warnings, w => w.Contains("roomNameList 矩形非法"));
        var p = Assert.Single(store.Presets, x => x.Resolution == "1920x1080");
        Assert.True(p.RoomNameList is null);
        Assert.True(p.Chat is not null);
    }

    [Fact]
    public void Load_EmptySceneEntry_IsSkipped()
    {
        var store = LayoutPresetStore.Load(TempWith(
            """
            { "version": 1, "presets": { "room": { "1920x1080": {} } } }
            """));
        Assert.Empty(store.Presets);
        Assert.Contains(store.Warnings, w => w.Contains("既无 chat/roomNameList 也无 gameName/mapName"));
    }

    // ---- 覆盖层（用户在配置页改预设）----

    [Fact]
    public void Override_WinsPerSceneResolution_AndIsCounted()
    {
        var factory = TempWith(Json);
        var over = TempWith("""
            { "version": 1, "presets": { "browsing": {
              "1920x1080": { "chat": [0.0052, 0.0185, 0.0156, 0.037] } } } }
            """);

        var store = LayoutPresetStore.Load(factory, over);

        Assert.Equal(1, store.OverrideCount);
        var p = store.Find("browsing", 1920, 1080)!;
        Assert.Equal(0.0052, p.Chat!.X, 6);                       // 覆盖层胜出
        Assert.True(p.RoomNameList is null);                       // 整条替换，不是逐字段合并
        Assert.Equal(560 / 3840d, store.Find("browsing", 3840, 2160)!.Chat!.X, 6);   // 另一档没被牵连，仍是出厂值
    }

    [Fact]
    public void SaveOverride_RoundTripsThroughLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        var p = new LayoutPreset("ingame", "3840x2160", new RelRect(16 / 3840d, 470 / 2160d, 2296 / 3840d, 830 / 2160d), null);

        LayoutPresetStore.SaveOverride(path, p);
        var back = LayoutPresetStore.Load(TempWith(Json), path);

        var found = back.Find("ingame", 3840, 2160);
        Assert.NotNull(found);
        Assert.Equal(2296 / 3840d, found!.Chat!.W, 6);
        Assert.True(found.RoomNameList is null);
        Assert.Equal(1, back.OverrideCount);
        File.Delete(path);
    }

    [Fact]
    public void SaveOverride_Upserts_NotAppends()
    {
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("room", "1920x1080", new RelRect(1 / 1920d, 2 / 1080d, 3 / 1920d, 4 / 1080d), null));
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("room", "1920x1080", new RelRect(9 / 1920d, 9 / 1080d, 9 / 1920d, 9 / 1080d), null));

        var store = LayoutPresetStore.Load(TempWith(Json), path);
        Assert.Equal(1, store.OverrideCount);
        Assert.Equal(9 / 1920d, store.Find("room", 1920, 1080)!.Chat!.X, 6);
        File.Delete(path);
    }

    [Fact]
    public void RemoveOverride_FallsBackToFactory()
    {
        var factory = TempWith(Json);
        var path = Path.Combine(Path.GetTempPath(), "layout-ov-" + Guid.NewGuid() + ".json");
        LayoutPresetStore.SaveOverride(path, new LayoutPreset("browsing", "1920x1080", new RelRect(1 / 1920d, 1 / 1080d, 1 / 1920d, 1 / 1080d), null));

        LayoutPresetStore.RemoveOverride(path, "browsing", "1920x1080");
        var store = LayoutPresetStore.Load(factory, path);

        Assert.Equal(0, store.OverrideCount);
        Assert.Equal(280 / 1920d, store.Find("browsing", 1920, 1080)!.Chat!.X, 6);   // 回到出厂值
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
            var c = p.Chat!;
            Assert.True(c.X >= 0 && c.Y >= 0, $"{p.Scene}/{p.Resolution} 聊天框有负坐标");
            Assert.True(c.X + c.W <= 1, $"{p.Scene}/{p.Resolution} 聊天框横向超出画面");
            Assert.True(c.Y + c.H <= 1, $"{p.Scene}/{p.Resolution} 聊天框纵向超出画面");
            // 聊天框不该小于一行字，也不该吃掉半个屏幕（那样会把 HUD/视口一起 OCR 进来）
            Assert.True(c.H >= 0.10 && c.H <= 0.55, $"{p.Scene}/{p.Resolution} 聊天框高度占比异常");
            Assert.True(c.W >= 0.15 && c.W <= 0.65, $"{p.Scene}/{p.Resolution} 聊天框宽度占比异常");
        }
    }

    [Fact]
    public void ShippingPresets_OnlyBrowsingCarriesTheRoomNameList()
    {
        // 房间名列表区域只属于"寻找房间"；房间内/游戏内没有这块，配错了说明预设串了场景。
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        Assert.All(store.Presets.Where(p => p.Scene != "browsing"), p => Assert.True(p.RoomNameList is null));
        Assert.All(store.Presets.Where(p => p.Scene == "browsing"),
            p =>
            {
                Assert.NotNull(p.RoomNameList);
                var r = p.RoomNameList!;
                Assert.True(r.W > 0 && r.H > 0, $"{p.Resolution} 房间名列表尺寸非法");
                Assert.True(r.X + r.W <= 1 && r.Y + r.H <= 1, $"{p.Resolution} 房间名列表超出画面");
            });
    }

    [Fact]
    public void ShippingPresets_ThreeResolutionsAreMutuallyConsistent()
    {
        // 三档应是同一套 UI 的等比呈现；偏差过大说明某一档是手填的而不是量/推算出来的。
        // 预设直接存相对坐标，三档的相对值应几乎相同（偏差来自像素取整）。
        var store = LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
        var b = store.Find("browsing", 1920, 1080)!;
        foreach (var (w, h) in new[] { (2560, 1440), (3840, 2160) })
        {
            var p = store.Find("browsing", w, h)!;
            void Close(double expect, double actual, double tol, string what)
                => Assert.True(Math.Abs(expect - actual) <= tol,
                    $"{w}x{h} {what}={actual:F6} 与 1080p {expect:F6} 偏差超 {tol:F6}");
            Close(b.Chat!.X, p.Chat!.X, 0.002, "chat.x");
            Close(b.Chat!.W, p.Chat!.W, 0.002, "chat.宽");
            Close(b.RoomNameList!.W, p.RoomNameList!.W, 0.002, "roomNameList.宽");
        }
    }
}
