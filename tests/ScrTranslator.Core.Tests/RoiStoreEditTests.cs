using ScrTranslator.Core;

namespace ScrTranslator.Core.Tests;

/// <summary>
/// 配置页"编辑/删除区域"背后的两条纯改写操作。
/// 它们按 (场景, 分辨率, 序号) 定位——定位不到必须返回 null，
/// 绝不能"差不多就行"地改到别的场景/别的分辨率上（那会让用户的校准莫名其妙坏掉）。
/// </summary>
public class RoiStoreEditTests
{
    private static RoiConfig Sample()
    {
        var browsing = new[]
        {
            new SceneBucket("1920x1080", new[]
            {
                new Roi("房间聊天", "Chat", new RelRect(0.1, 0.6, 0.4, 0.3)),
                new Roi("房间名", "RoomList", new RelRect(0.6, 0.3, 0.2, 0.05),
                    new[] { new RoiColumn(0d, 1d, ColumnKind.RoomName) }),
            }),
            new SceneBucket("3840x2160", new[]
            {
                new Roi("房间聊天", "Chat", new RelRect(0.12, 0.62, 0.42, 0.31)),
            }),
        };
        var room = new[]
        {
            new SceneBucket("1920x1080", new[]
            {
                new Roi("大厅聊天", "Chat", new RelRect(0.05, 0.7, 0.5, 0.25)),
            }),
        };
        var map = new Dictionary<string, IReadOnlyList<SceneBucket>> { ["browsing"] = browsing, ["room"] = room };
        return new RoiConfig(map);
    }

    [Fact]
    public void ReplaceRoi_ChangesOnlyTheTargetedEntry()
    {
        var cfg = Sample();
        var updated = RoiStore.ReplaceRoi(cfg, "browsing", "1920x1080", 1,
            new Roi("房间名列表", "RoomList", new RelRect(0.6, 0.36, 0.23, 0.03),
                new[] { new RoiColumn(0, 1, ColumnKind.RoomName) }));

        Assert.NotNull(updated);
        var browsing = updated!.Scenes["browsing"];
        Assert.Equal("房间名列表", browsing[0].Rois[1].Name);
        Assert.Equal(0.36, browsing[0].Rois[1].Rect.Y, 6);
        // 列定义是调用方自己带进来的，ReplaceRoi 不负责动它——这里确认它没被丢
        Assert.Single(browsing[0].Rois[1].Columns!);
        // 同场景另一分辨率、另一场景，都必须原样不动
        Assert.Single(browsing[1].Rois);
        Assert.Equal(0.62, browsing[1].Rois[0].Rect.Y, 6);
        Assert.Equal("大厅聊天", updated.Scenes["room"][0].Rois[0].Name);
        // 原对象不可变
        Assert.Equal("房间名", cfg.Scenes["browsing"][0].Rois[1].Name);
    }

    [Fact]
    public void ReplaceRoi_RenameDoesNotDisturbOrder()
    {
        var updated = RoiStore.ReplaceRoi(Sample(), "browsing", "1920x1080", 0,
            new Roi("聊天框", "Chat", new RelRect(0.1, 0.6, 0.4, 0.3)));
        var rois = updated!.Scenes["browsing"][0].Rois;
        Assert.Equal(2, rois.Count);
        Assert.Equal(new[] { "聊天框", "房间名" }, rois.Select(r => r.Name));
    }

    [Fact]
    public void RemoveRoi_DropsOnlyThatOne()
    {
        var updated = RoiStore.RemoveRoi(Sample(), "browsing", "1920x1080", 0);

        Assert.NotNull(updated);
        Assert.Equal("房间名", updated!.Scenes["browsing"][0].Rois.Single().Name);
        Assert.Single(updated.Scenes["browsing"][1].Rois);   // 另一分辨率没被牵连
        Assert.Single(updated.Scenes["room"]);               // 另一场景没被牵连
    }

    [Theory]
    [InlineData("nope", "1920x1080", 0)]          // 场景不存在
    [InlineData("browsing", "2560x1440", 0)]      // 分辨率桶不存在
    [InlineData("browsing", "1920x1080", 9)]      // 序号越界
    [InlineData("browsing", "1920x1080", -1)]     // 负序号
    public void LocateFailures_ReturnNull_NeverGuess(string scene, string res, int index)
    {
        var cfg = Sample();
        Assert.Null(RoiStore.ReplaceRoi(cfg, scene, res, index, new Roi("x", "Chat", new RelRect(0, 0, 0.1, 0.1))));
        Assert.Null(RoiStore.RemoveRoi(cfg, scene, res, index));
    }

    [Fact]
    public void EditThen_SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "rois-" + Guid.NewGuid() + ".json");
        var edited = RoiStore.ReplaceRoi(Sample(), "room", "1920x1080", 0,
            new Roi("大厅聊天", "Chat", new RelRect(0.046875, 0.694444, 0.5, 0.25)));
        RoiStore.Save(edited!, path);

        var back = RoiStore.Load(path);
        var roi = back.Scenes["room"][0].Rois[0];
        Assert.Equal(0.046875, roi.Rect.X, 6);
        Assert.Equal(0.694444, roi.Rect.Y, 6);
        File.Delete(path);
    }
}
