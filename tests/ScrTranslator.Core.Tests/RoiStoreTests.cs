using System.Drawing;
using ScrTranslator.Core;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class RoiStoreTests
{
    [Fact]
    public void ToPixels_ConvertsRelativeToPixels()
    {
        var rect = RoiStore.ToPixels(new RelRect(0.25, 0.5, 0.2, 0.1), 1920, 1080);
        Assert.Equal(new Rectangle(480, 540, 384, 108), rect);
    }

    [Fact]
    public void ToPixels_ClampsWhenExceedingBounds()
    {
        var rect = RoiStore.ToPixels(new RelRect(0.9, 0.0, 0.2, 0.1), 1000, 1000);
        Assert.Equal(900, rect.X);
        Assert.Equal(100, rect.Width);
    }

    [Fact]
    public void SelectRois_ReturnsTheSceneRoiList()
    {
        // 相对坐标跨分辨率通用：SelectRois 不再需要分辨率参数，同一份 ROI 列表对所有分辨率成立。
        var roi = new Roi("game_name", new RelRect(0.1, 0.1, 0.2, 0.2));
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>> { ["scene_a"] = new[] { roi } });
        Assert.Same(roi, RoiStore.SelectRois(cfg, "scene_a").Single());
    }

    [Fact]
    public void Load_NewFormat_ParsesScenesAndRois()
    {
        string json = """
        { "scenes": { "scene_a": { "rois": [
          { "name": "game_name", "rect": [0.18, 0.28, 0.22, 0.40] }
        ] } } }
        """;
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var roi = RoiStore.Load(path).Scenes["scene_a"][0];
        Assert.Equal("game_name", roi.Name);
        Assert.Equal(0.22, roi.Rect.W, 3);
    }

    [Fact]
    public void Load_OldFormat_Buckets_MigratesToRoiList()
    {
        // 旧结构兼容：场景键下是 buckets 数组（按分辨率分档）。
        // 各档是同一份相对坐标（等比缩放、冗余存储），取首档即可，不必逐档读。
        string json = """
        { "scenes": { "scene_a": { "buckets": [
          { "resolution": "1920x1080", "rois": [
            { "name": "game_name", "rect": [0.18, 0.28, 0.22, 0.40] }
          ] },
          { "resolution": "1280x720", "rois": [
            { "name": "game_name", "rect": [0.18, 0.28, 0.22, 0.40] }
          ] } ] } } }
        """;
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var rois = RoiStore.Load(path).Scenes["scene_a"];
        Assert.Single(rois);
        Assert.Equal("game_name", rois[0].Name);
        Assert.Equal(0.22, rois[0].Rect.W, 3);
    }

    [Fact]
    public void Load_OldRois_NoColumns_ColumnsNull()
    {
        var json = "{\"scenes\":{\"s\":{\"rois\":[" +
                   "{\"name\":\"房间\",\"rect\":[0.1,0.2,0.3,0.4]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var roi = RoiStore.Load(path).Scenes["s"][0];
        Assert.Null(roi.Columns);
    }

    [Fact]
    public void Load_V2_ParsesColumns()
    {
        var json = "{\"scenes\":{\"s\":{\"rois\":[" +
                   "{\"name\":\"房间\",\"rect\":[0.1,0.2,0.3,0.4]," +
                   "\"columns\":[{\"start\":0.0,\"end\":0.3,\"kind\":\"PlayerId\"}," +
                   "{\"start\":0.3,\"end\":1.0,\"kind\":\"RoomName\"}]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var roi = RoiStore.Load(path).Scenes["s"][0];
        Assert.NotNull(roi.Columns);
        Assert.Equal(2, roi.Columns!.Count);
        Assert.Equal(ColumnKind.PlayerId, roi.Columns[0].Kind);
        Assert.Equal(0.3, roi.Columns[1].RelStart);
    }

    [Fact]
    public void ColumnKinds_UnknownKind_FallsBackOther()
        => Assert.Equal(ColumnKind.Other, ColumnKinds.Parse("nonsense"));

    [Fact]
    public void Save_Then_Load_RoundTrips_WithColumns()
    {
        var roi = new Roi("房间", new RelRect(0.1, 0.2, 0.3, 0.4),
            new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId), new RoiColumn(0.3, 1.0, ColumnKind.RoomName) });
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>> { ["s"] = new[] { roi } });
        var path = Path.GetTempFileName();
        RoiStore.Save(cfg, path);
        var back = RoiStore.Load(path).Scenes["s"][0];
        Assert.Equal(2, back.Columns!.Count);
        Assert.Equal(ColumnKind.RoomName, back.Columns[1].Kind);
        Assert.Equal(1.0, back.Columns[1].RelEnd);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyConfig_NotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "definitely_missing_" + Guid.NewGuid() + ".json");
        var cfg = RoiStore.Load(path);
        Assert.Empty(cfg.Scenes);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyConfig_NotThrow()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ scenes: { this is not valid json ");
        var cfg = RoiStore.Load(path);
        Assert.Empty(cfg.Scenes);
    }

    [Fact]
    public void SelectRois_SceneMissing_ThrowsInvalidOperationException_NotKeyNotFound()
    {
        var cfg = RoiStore.Load(Path.Combine(Path.GetTempPath(), "definitely_missing_" + Guid.NewGuid() + ".json"));
        Assert.Throws<InvalidOperationException>(() => RoiStore.SelectRois(cfg, "custom_games_listing"));
    }

    [Fact]
    public void Save_WritesAtomically_NoLeftoverTempFile()
    {
        var roi = new Roi("x", new RelRect(0, 0, 1, 1));
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>> { ["s"] = new[] { roi } });
        var path = Path.GetTempFileName();
        RoiStore.Save(cfg, path);
        Assert.True(File.Exists(path));
        string dir = Path.GetDirectoryName(path)!;
        var leftoverTemps = Directory.GetFiles(dir, Path.GetFileName(path) + ".tmp*");
        Assert.Empty(leftoverTemps);
    }
}
