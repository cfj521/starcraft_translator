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
}
