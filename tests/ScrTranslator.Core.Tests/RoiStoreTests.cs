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
    public void SelectBucket_ExactResolutionMatch()
    {
        var cfg = MakeConfig();
        var b = RoiStore.SelectBucket(cfg, "scene_a", 1920, 1080);
        Assert.Equal("1920x1080", b.Resolution);
    }

    [Fact]
    public void SelectBucket_NoExact_PicksNearestAspectRatio()
    {
        var cfg = MakeConfig();
        var b = RoiStore.SelectBucket(cfg, "scene_a", 2560, 1440);
        Assert.Equal("1920x1080", b.Resolution);
    }

    [Fact]
    public void Load_ParsesScenesAndRois()
    {
        string json = """
        { "scenes": { "scene_a": { "buckets": [
          { "resolution": "1920x1080", "rois": [
            { "name": "game_name", "type": "RoomList", "rect": [0.18, 0.28, 0.22, 0.40] }
          ] } ] } } }
        """;
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var cfg = RoiStore.Load(path);
        var roi = cfg.Scenes["scene_a"][0].Rois[0];
        Assert.Equal("game_name", roi.Name);
        Assert.Equal(0.22, roi.Rect.W, 3);
    }

    private static RoiConfig MakeConfig()
    {
        var roi = new Roi("game_name", "RoomList", new RelRect(0.1, 0.1, 0.2, 0.2));
        var scenes = new Dictionary<string, IReadOnlyList<SceneBucket>>
        {
            ["scene_a"] = new List<SceneBucket>
            {
                new("1920x1080", new[] { roi }),
                new("1280x960", new[] { roi }),
            }
        };
        return new RoiConfig(scenes);
    }

    [Fact]
    public void Load_OldRois_NoColumns_ColumnsNull()
    {
        // 复用现有 assets/rois 或写临时旧格式(无 columns)
        var json = "{\"scenes\":{\"s\":{\"buckets\":[{\"resolution\":\"1920x1080\",\"rois\":[" +
                   "{\"name\":\"房间\",\"type\":\"RoomList\",\"rect\":[0.1,0.2,0.3,0.4]}]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var cfg = RoiStore.Load(path);
        var roi = cfg.Scenes["s"][0].Rois[0];
        Assert.Null(roi.Columns);
        Assert.Equal("RoomList", roi.Type);
    }

    [Fact]
    public void Load_V2_ParsesColumns()
    {
        var json = "{\"scenes\":{\"s\":{\"buckets\":[{\"resolution\":\"1920x1080\",\"rois\":[" +
                   "{\"name\":\"房间\",\"type\":\"RoomList\",\"rect\":[0.1,0.2,0.3,0.4]," +
                   "\"columns\":[{\"start\":0.0,\"end\":0.3,\"kind\":\"PlayerId\"}," +
                   "{\"start\":0.3,\"end\":1.0,\"kind\":\"RoomName\"}]}]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var roi = RoiStore.Load(path).Scenes["s"][0].Rois[0];
        Assert.NotNull(roi.Columns);
        Assert.Equal(2, roi.Columns!.Count);
        Assert.Equal(ColumnKind.PlayerId, roi.Columns[0].Kind);
        Assert.Equal(0.3, roi.Columns[1].RelStart);
    }

    [Fact]
    public void RoiTypes_UnknownType_FallsBackChat()
        => Assert.Equal(RoiType.Chat, RoiTypes.Parse("nonsense"));

    [Fact]
    public void ColumnKinds_UnknownKind_FallsBackOther()
        => Assert.Equal(ColumnKind.Other, ColumnKinds.Parse("nonsense"));

    [Fact]
    public void Save_Then_Load_RoundTrips_WithColumns()
    {
        var roi = new Roi("房间", "RoomList", new RelRect(0.1, 0.2, 0.3, 0.4),
            new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId), new RoiColumn(0.3, 1.0, ColumnKind.RoomName) });
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>
            { ["s"] = new[] { new SceneBucket("1920x1080", new[] { roi }) } });
        var path = Path.GetTempFileName();
        RoiStore.Save(cfg, path);
        var back = RoiStore.Load(path).Scenes["s"][0].Rois[0];
        Assert.Equal("RoomList", back.Type);
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
    public void Save_WritesAtomically_NoLeftoverTempFile()
    {
        var roi = new Roi("x", "Chat", new RelRect(0, 0, 1, 1));
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>
            { ["s"] = new[] { new SceneBucket("1x1", new[] { roi }) } });
        var path = Path.GetTempFileName();
        RoiStore.Save(cfg, path);
        Assert.True(File.Exists(path));
        string dir = Path.GetDirectoryName(path)!;
        var leftoverTemps = Directory.GetFiles(dir, Path.GetFileName(path) + ".tmp*");
        Assert.Empty(leftoverTemps);
    }
}
