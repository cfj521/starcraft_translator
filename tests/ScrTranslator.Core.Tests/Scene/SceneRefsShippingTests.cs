using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 对**出厂 scene-refs.json** 的回归守卫。文件由 tools/SceneRefGen 从 docs/scenes pictures 的截图生成，
/// 这里不重读截图，只验两件事：
///  ① 文件解析干净（版本、长度、几何都自洽）；
///  ② 区域**真的选对了**——同一套区域下，各场景/各种族的参考彼此必须足够不像。
/// 要是哪天有人把区域挪到会变的画面上（比如游戏内视口），两条参考会突然变得高度相似，
/// 这个测试直接变红；否则就得等实机"翻译突然停了"才发现。
/// </summary>
public class SceneRefsShippingTests
{
    static readonly Lazy<SceneRefStore> Loaded = new(() =>
        SceneRefStore.Load(Path.Combine(AppContext.BaseDirectory, SceneRefStore.DefaultFileName)));

    static SceneRefStore Store => Loaded.Value;

    // 方案 B：参考只存 1080p 一档基准（运行时等比缩放到帧分辨率，块数固定），不再按三档分辨率各存一份。
    static readonly string[] ExpectedResolutions = { "16:9" };

    [Fact]
    public void ShippingFile_LoadsWithoutErrorsOrWarnings()
    {
        Assert.True(Store.IsUsable, Store.LoadError ?? "参考为空");
        Assert.Empty(Store.Warnings);
    }

    [Fact]
    public void ShippingFile_ContainsAllSceneAndRaceReferences()
    {
        Assert.Equal(
            new[] { "browsing", "ingame_p", "ingame_t", "ingame_z", "room" },
            Store.References.Select(r => r.Id).OrderBy(x => x));
        Assert.Equal(2, Store.ForScene(GameScene.Browsing).Count() + Store.ForScene(GameScene.Room).Count());
        Assert.Equal(3, Store.ForScene(GameScene.InGame).Count()); // 游戏内按种族分三套
    }

    [Fact]
    public void ShippingFile_EveryReferenceCoversTheBaseResolution()
    {
        // 方案 B 只存 1080p 基准一档；每条参考必须有它，运行时才能缩放到任意帧分辨率。
        foreach (var reference in Store.References)
            foreach (var res in ExpectedResolutions)
                Assert.True(reference.Resolutions.ContainsKey(res), $"{reference.Id} 缺 {res} 参考");
    }

    [Fact]
    public void ShippingFile_FingerprintLengthMatchesDeclaredGrid()
    {
        foreach (var reference in Store.References)
            foreach (var res in reference.Resolutions.Values)
            {
                Assert.Equal(res.Regions.Sum(r => r.ByteLength), res.Fingerprint.Length);
                foreach (var region in res.Regions)
                {
                    Assert.True(region.BlockPx >= 1, $"{reference.Id} 块宽非法");
                    Assert.True(region.Width >= region.BlockPx && region.Height >= region.BlockPx,
                        $"{reference.Id}/{region.Name} 区域比块还小，网格不可能成立");
                }
            }
    }

    [Fact]
    public void ShippingFile_ReferencesSharingGeometry_AreMutuallyDistinct()
    {
        // 只有几何完全相同的参考才能逐块对比（browsing↔room、ingame_t↔p↔z 各自同几何）。
        foreach (var res in ExpectedResolutions)
        {
            var groups = Store.References
                .Select(r => (r.Id, Res: r.Resolutions[res]))
                .GroupBy(x => GeometryKey(x.Res))
                .Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                var items = group.ToList();
                for (int i = 0; i < items.Count; i++)
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        double cross = SceneFingerprint.HitRatio(
                            items[i].Res.Fingerprint, items[j].Res.Fingerprint, Store.ChannelTolerance);
                        Assert.True(cross < 0.5,
                            $"{res} 下 {items[i].Id} 与 {items[j].Id} 相似度 {cross:P0}——区域选到了共同静态内容，无法区分场景/种族");
                    }
            }
        }
    }

    [Fact]
    public void ShippingFile_ThresholdLeavesLargeMarginOverWorstConfusion()
    {
        double worst = 0; string worstPair = "";
        foreach (var res in ExpectedResolutions)
            foreach (var group in Store.References
                         .Select(r => (r.Id, Res: r.Resolutions[res])).GroupBy(x => GeometryKey(x.Res)))
            {
                var items = group.ToList();
                for (int i = 0; i < items.Count; i++)
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        double cross = SceneFingerprint.HitRatio(
                            items[i].Res.Fingerprint, items[j].Res.Fingerprint, Store.ChannelTolerance);
                        if (cross > worst) { worst = cross; worstPair = $"{res}: {items[i].Id}↔{items[j].Id}"; }
                    }
            }

        Assert.NotEqual("", worstPair);
        // 阈值必须比"最坏混淆"高出一大截：留 0.3 的余量，才扛得住弹窗遮挡/局部动画。
        Assert.True(worst + 0.3 <= Store.MinHitRatio,
            $"阈值余量不足：最坏混淆 {worst:P0}（{worstPair}），阈值 {Store.MinHitRatio:P0}");
    }

    static string GeometryKey(SceneRefResolution res) =>
        string.Join(";", res.Regions.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height},{r.BlockPx},{r.BlocksX}x{r.BlocksY}"));
}
