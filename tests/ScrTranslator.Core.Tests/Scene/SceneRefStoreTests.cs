using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 造 scene-refs.json 测试样本。统一用 1920x1080 基准分辨率（= 运行时唯一基准键）+一个 8x8/块宽4 的区域
/// （2x2=4 块，12 字节），让测试不必搬 4K 截图也能跑完整解析路径。
/// 方案 B 下运行时把该区域等比缩放到帧分辨率、块数固定，所以参考必须建在 1920x1080 上。
/// </summary>
internal static class SceneRefJson
{
    public const string ResKey = "16:9";
    public const int Blocks = 4; // 2x2
    // 测试区域用 1920x1080 下 8x8 的绝对像素，换算成相对 0~1 值（与出厂 scene-refs.json 同一口径）。
    public const double RelW = 8.0 / 1920, RelH = 8.0 / 1080;
    /// <summary>测试区域相对 rect 的 JSON 文本（[0,0,w,h]）。</summary>
    public static string RelRect => $"[0,0,{RelW:R},{RelH:R}]";

    /// <summary>整块同一灰度的指纹（4 块 ×RGB）。</summary>
    public static byte[] Flat(byte v) => new byte[] { v, v, v, v, v, v, v, v, v, v, v, v };

    public static string Build(
        (string id, string gameScene, string? race, byte[] data)[] scenes,
        int tolerance = 16, double minHit = 0.9, int version = 1, bool withIntegrity = true)
    {
        // 新格式：scenes 以 gameScene 为识别键（browsing/room/ingame）；race 挪进 data 条目。
        // 按 gameScene 分组，同场景的多种族各占一条 data 条目。
        var parts = scenes.GroupBy(s => s.gameScene).Select(g =>
        {
            var gameScene = g.Key;
            var dataEntries = g.Select(s =>
            {
                var race = s.race is null ? "" : $"\"race\": \"{s.race}\", ";
                return $$"""
                         { "gameScene": "{{gameScene}}", {{race}}"ratio": "{{ResKey}}",
                           "source_file": "test", "blockPx": 4, "blocks": [2,2],
                           "data": "{{Convert.ToBase64String(s.data)}}" }
                         """;
            });
            return $$"""
                     "{{gameScene}}": { "label": "{{gameScene}}", "gameScene": "{{gameScene}}",
                       "regions": { "name": "r", "rect": {{RelRect}} },
                       "data": [ {{string.Join(",", dataEntries)}} ] }
                     """;
        });
        // 出厂层现在强制要求 integrity，所以测试样本也得走同一套哈希实现。
        // 口径必须与 ParseReference 一致：未知 gameScene 的条目会被 ParseReference 跳过、不产生 entries，
        // 哈希里也不该有它——否则 declared 覆盖 2 条、computed 只有 1 条，整份被拒。
        var entries = scenes.Where(s => GameSceneIds.FromId(s.gameScene) != GameScene.Unknown)
            .Select(s => new SceneRefIntegrity.Entry(
                SceneRefIntegrity.Key(s.id, ResKey, "r"),
                SceneRefIntegrity.Geometry(0, 0, RelW, RelH, 4, 2, 2), s.data)).ToList();
        var integrity = withIntegrity
            ? $$"""
                  "integrity": { "algorithm": "sha256", "bytes": {{SceneRefIntegrity.DataBytes(entries)}},
                                 "sha256": "{{SceneRefIntegrity.Hash(entries)}}" },
                """
            : "";
        return $$"""
            { "version": {{version}},
              "fingerprint": { "baseResolution": "1920x1080", "blockSize": 4,
                               "channelTolerance": {{tolerance}}, "minHitRatio": {{minHit}} },
              {{integrity}}"scenes": { {{string.Join(",", parts)}} } }
            """;
    }

    public static string TempPathWith(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "scene-refs-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}

public class SceneRefStoreTests
{
    [Fact]
    public void Load_ValidJson_ParsesParamsReferencesAndRace()
    {
        var json = SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)),
            ("ingame_t", "ingame", "t", SceneRefJson.Flat(200)),
        });
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));

        Assert.Null(store.LoadError);
        Assert.True(store.IsUsable);
        Assert.Equal(16, store.ChannelTolerance);
        Assert.Equal(0.9, store.MinHitRatio, 3);
        Assert.Equal(4, store.BlockSize);
        Assert.Equal(2, store.References.Count);

        var ingame = store.Find("ingame_t")!;
        Assert.Equal(GameScene.InGame, ingame.Scene);      // gameScene 归并成四态
        Assert.Equal(GameRace.Terran, ingame.Race);        // race 单独保留
        Assert.True(ingame.TryResolution(1920, 1080, out var res));
        Assert.Equal(12, res.Fingerprint.Length);
        Assert.Single(res.Regions);
        Assert.Equal(2, res.Regions[0].BlocksX);
        Assert.Equal(4, res.Regions[0].BlockPx);
    }

    [Fact]
    public void Load_MissingFile_ReportsErrorInsteadOfThrowing()
    {
        var store = SceneRefStore.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"));
        Assert.False(store.IsUsable);
        Assert.Contains("找不到", store.LoadError);
    }

    [Fact]
    public void Load_TruncatedJson_ReportsErrorInsteadOfThrowing()
    {
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith("{ scenes: { this is not valid json "));
        Assert.False(store.IsUsable);
        Assert.NotNull(store.LoadError);
    }

    [Fact]
    public void Load_UnsupportedVersion_RefusesToGuess()
    {
        // 版本不符时格式可能已变（尤其网格参数），硬解会得到全体错位的指纹，必须明确拒绝。
        var json = SceneRefJson.Build(new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) }, version: 99);
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));
        Assert.False(store.IsUsable);
        Assert.Contains("version", store.LoadError);
    }

    [Fact]
    public void Load_FingerprintLengthMismatch_DropsThatResolutionAndWarns()
    {
        var bad = new byte[] { 1, 2, 3, 4, 5 }; // 声明 2x2 块却只给 5 字节
        var json = SceneRefJson.Build(new[] { ("browsing", "browsing", (string?)null, bad) });
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));

        Assert.Empty(store.References);
        Assert.Contains(store.Warnings, w => w.Contains("指纹长度"));
    }

    [Fact]
    public void Load_UnknownGameScene_SkipsThatReference()
    {
        var json = SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)),
            ("lobby", "not_a_scene", (string?)null, SceneRefJson.Flat(200)),
        });
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));
        Assert.Single(store.References);
        Assert.Null(store.Find("lobby"));
    }

    [Fact]
    public void TryResolution_RequiresExactMatch_NeverBorrowsNeighbour()
    {
        // 块均值网格随分辨率变，借邻近分辨率的参考会整片错位 → 必须拒绝。
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(
            SceneRefJson.Build(new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) })));
        var reference = store.References[0];
        Assert.True(reference.TryResolution(1920, 1080, out _));
        Assert.False(reference.TryResolution(128, 128, out _));
        Assert.Equal(new[] { SceneRefJson.ResKey }, reference.AvailableResolutions);
    }

    [Fact]
    public void ForScene_GroupsRaceVariants()
    {
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(SceneRefJson.Build(new[]
        {
            ("ingame_t", "ingame", "t", SceneRefJson.Flat(10)),
            ("ingame_p", "ingame", "p", SceneRefJson.Flat(110)),
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(200)),
        })));
        Assert.Equal(2, store.ForScene(GameScene.InGame).Count());
        Assert.Single(store.ForScene(GameScene.Browsing));
        Assert.Empty(store.ForScene(GameScene.Other));
    }

    // ===== 完整性校验 =====

    [Fact]
    public void Load_FactoryWithoutIntegrity_IsRejected()
    {
        // 出厂层少了校验和，就无法区分"生成器漏写"和"整份被替换"，一律拒用。
        var json = SceneRefJson.Build(
            new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) }, withIntegrity: false);
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));
        Assert.False(store.IsUsable);
        Assert.Contains("integrity", store.LoadError);
    }

    [Fact]
    public void Load_TamperedFingerprintData_IsRejected()
    {
        var json = SceneRefJson.Build(new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) });
        var tampered = json.Replace(Convert.ToBase64String(SceneRefJson.Flat(10)),
                                     Convert.ToBase64String(SceneRefJson.Flat(11)));
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(tampered));
        Assert.False(store.IsUsable);
        Assert.Contains("校验不通过", store.LoadError);
    }

    [Fact]
    public void Load_EditedGeometry_AlsoFailsIntegrity()
    {
        // 只挪一点相对坐标、数据一字不改，也必须失效：带着错位指纹上场是最难查的一类误判。
        var json = SceneRefJson.Build(new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) });
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json.Replace(SceneRefJson.RelRect, "[0.5,0,0.5,0.5]")));
        Assert.False(store.IsUsable);
        Assert.Contains("校验不通过", store.LoadError);
    }

    // ===== 覆盖层（用户现场采集）=====

    [Fact]
    public void Overrides_ReplaceSameResolution_AppendNewScene_AndAreCounted()
    {
        var factory = SceneRefJson.TempPathWith(SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)),
        }));
        var overrides = SceneRefJson.TempPathWith(SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(200)), // 覆盖同分辨率
            ("room", "room", (string?)null, SceneRefJson.Flat(120)),         // 新场景直接追加
        }));

        var store = SceneRefStore.Load(factory, overrides);

        Assert.Equal(2, store.References.Count);
        Assert.Equal(2, store.OverrideCount);
        Assert.Equal((byte)200, store.Find("browsing")!.Resolutions[SceneRefJson.ResKey].Fingerprint[0]);
        Assert.NotNull(store.Find("room"));
    }

    [Fact]
    public void Overrides_BrokenFile_DoesNotTakeFactoryDown()
    {
        var factory = SceneRefJson.TempPathWith(SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)),
        }));
        var overrides = SceneRefJson.TempPathWith("{ 覆盖层坏了 ");

        var store = SceneRefStore.Load(factory, overrides);

        Assert.True(store.IsUsable, "覆盖层损坏不该连累出厂层");
        Assert.Single(store.References);
        Assert.Contains(store.Warnings, w => w.Contains("已忽略覆盖层"));
    }

    [Fact]
    public void Overrides_AbsentIsNormalAndSilent()
    {
        var factory = SceneRefJson.TempPathWith(SceneRefJson.Build(new[]
        {
            ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)),
        }));
        var store = SceneRefStore.Load(factory,
            Path.Combine(Path.GetTempPath(), "no-overrides-" + Guid.NewGuid() + ".json"));

        Assert.True(store.IsUsable);
        Assert.Equal(0, store.OverrideCount);
        Assert.Empty(store.Warnings); // 没有覆盖层是常态，不该刷告警
    }
}
