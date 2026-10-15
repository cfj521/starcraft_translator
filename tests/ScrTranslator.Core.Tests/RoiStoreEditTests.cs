using ScrTranslator.Core;

namespace ScrTranslator.Core.Tests;

/// <summary>
/// 配置页"编辑/删除区域"背后的两条纯改写操作。
/// 它们按 (场景, 序号) 定位——定位不到必须返回 null，
/// 绝不能"差不多就行"地改到别的场景上（那会让用户的校准莫名其妙坏掉）。
/// 相对坐标跨分辨率通用，不再有按分辨率分桶的概念。
/// </summary>
public class RoiStoreEditTests
{
    private static RoiConfig Sample()
    {
        var map = new Dictionary<string, IReadOnlyList<Roi>>
        {
            ["browsing"] = new[]
            {
                new Roi("房间聊天", new RelRect(0.1, 0.6, 0.4, 0.3)),
                new Roi("房间名", new RelRect(0.6, 0.3, 0.2, 0.05),
                    new[] { new RoiColumn(0d, 1d, ColumnKind.RoomName) }),
            },
            ["room"] = new[]
            {
                new Roi("大厅聊天", new RelRect(0.05, 0.7, 0.5, 0.25)),
            },
        };
        return new RoiConfig(map);
    }

    [Fact]
    public void ReplaceRoi_ChangesOnlyTheTargetedEntry()
    {
        var cfg = Sample();
        var updated = RoiStore.ReplaceRoi(cfg, "browsing", 1,
            new Roi("房间名列表", new RelRect(0.6, 0.36, 0.23, 0.03),
                new[] { new RoiColumn(0, 1, ColumnKind.RoomName) }));

        Assert.NotNull(updated);
        var browsing = updated!.Scenes["browsing"];
        Assert.Equal("房间名列表", browsing[1].Name);
        Assert.Equal(0.36, browsing[1].Rect.Y, 6);
        // 列定义是调用方自己带进来的，ReplaceRoi 不负责动它——这里确认它没被丢
        Assert.Single(browsing[1].Columns!);
        // 另一场景必须原样不动
        Assert.Equal("大厅聊天", updated.Scenes["room"][0].Name);
        // 原对象不可变
        Assert.Equal("房间名", cfg.Scenes["browsing"][1].Name);
    }

    [Fact]
    public void ReplaceRoi_RenameDoesNotDisturbOrder()
    {
        var updated = RoiStore.ReplaceRoi(Sample(), "browsing", 0,
            new Roi("聊天框", new RelRect(0.1, 0.6, 0.4, 0.3)));
        var rois = updated!.Scenes["browsing"];
        Assert.Equal(2, rois.Count);
        Assert.Equal(new[] { "聊天框", "房间名" }, rois.Select(r => r.Name));
    }

    [Fact]
    public void RemoveRoi_DropsOnlyThatOne()
    {
        var updated = RoiStore.RemoveRoi(Sample(), "browsing", 0);

        Assert.NotNull(updated);
        Assert.Equal("房间名", updated!.Scenes["browsing"].Single().Name);
        Assert.Single(updated.Scenes["room"]);               // 另一场景没被牵连
    }

    [Theory]
    [InlineData("nope", 0)]        // 场景不存在
    [InlineData("browsing", 9)]    // 序号越界
    [InlineData("browsing", -1)]   // 负序号
    public void LocateFailures_ReturnNull_NeverGuess(string scene, int index)
    {
        var cfg = Sample();
        Assert.Null(RoiStore.ReplaceRoi(cfg, scene, index, new Roi("x", new RelRect(0, 0, 0.1, 0.1))));
        Assert.Null(RoiStore.RemoveRoi(cfg, scene, index));
    }

    [Fact]
    public void MoveRoi_ChangesSceneAndKeepsTheEntryIntact()
    {
        var cfg = Sample();
        var updated = RoiStore.MoveRoi(cfg, "browsing", 1, "room");

        Assert.NotNull(updated);
        Assert.Single(updated!.Scenes["browsing"]);                 // 源场景只剩聊天
        Assert.Equal("房间聊天", updated.Scenes["browsing"][0].Name);
        var moved = updated.Scenes["room"];
        Assert.Equal(2, moved.Count);                               // 目标场景原有 1 条 + 搬来 1 条
        Assert.Equal("房间名", moved[^1].Name);
        Assert.Equal(0.6, moved[^1].Rect.X, 6);                     // 坐标/列定义原样带过去
        Assert.Single(moved[^1].Columns!);
    }

    [Fact]
    public void MoveRoi_CreatesTargetSceneWhenAbsent()
    {
        // 目标场景从没校准过 → 建那个场景，而不是塞进别的场景里
        var updated = RoiStore.MoveRoi(Sample(), "browsing", 0, "ingame");

        Assert.NotNull(updated);
        Assert.Single(updated!.Scenes["browsing"]);                // 源场景只剩"房间名"
        Assert.Equal("房间名", updated.Scenes["browsing"][0].Name);
        var target = updated.Scenes["ingame"];
        Assert.Equal("房间聊天", target.Single().Name);
    }

    [Fact]
    public void MoveRoi_SameScene_IsNoOp()
    {
        var cfg = Sample();
        Assert.Same(cfg, RoiStore.MoveRoi(cfg, "browsing", 0, "browsing"));
    }

    [Fact]
    public void MoveRoi_BadLocator_ReturnsNull()
    {
        var cfg = Sample();
        Assert.Null(RoiStore.MoveRoi(cfg, "nope", 0, "room"));
        Assert.Null(RoiStore.MoveRoi(cfg, "browsing", 99, "room"));
    }

    [Fact]
    public void EditThen_SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "rois-" + Guid.NewGuid() + ".json");
        var edited = RoiStore.ReplaceRoi(Sample(), "room", 0,
            new Roi("大厅聊天", new RelRect(0.046875, 0.694444, 0.5, 0.25)));
        RoiStore.Save(edited!, path);

        var back = RoiStore.Load(path);
        var roi = back.Scenes["room"][0];
        Assert.Equal(0.046875, roi.Rect.X, 6);
        Assert.Equal(0.694444, roi.Rect.Y, 6);
        File.Delete(path);
    }
}
