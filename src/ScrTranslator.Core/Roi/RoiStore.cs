using System.Drawing;
using System.Text.Json;

namespace ScrTranslator.Core;

public static class RoiStore
{
    public static Rectangle ToPixels(RelRect rel, int width, int height)
    {
        int x = (int)Math.Round(rel.X * width);
        int y = (int)Math.Round(rel.Y * height);
        int w = (int)Math.Round(rel.W * width);
        int h = (int)Math.Round(rel.H * height);
        x = Math.Clamp(x, 0, width);
        y = Math.Clamp(y, 0, height);
        w = Math.Clamp(w, 0, width - x);
        h = Math.Clamp(h, 0, height - y);
        return new Rectangle(x, y, w, h);
    }

    public static SceneBucket SelectBucket(RoiConfig cfg, string scene, int width, int height)
    {
        var buckets = cfg.Scenes[scene];
        string key = $"{width}x{height}";
        foreach (var b in buckets)
            if (b.Resolution == key) return b;

        double target = (double)width / height;
        SceneBucket? best = null;
        double bestDiff = double.MaxValue;
        foreach (var b in buckets)
        {
            var parts = b.Resolution.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int bw) && int.TryParse(parts[1], out int bh) && bh != 0)
            {
                double diff = Math.Abs((double)bw / bh - target);
                if (diff < bestDiff) { bestDiff = diff; best = b; }
            }
        }
        return best ?? buckets[0];
    }

    public static RoiConfig Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var scenes = new Dictionary<string, IReadOnlyList<SceneBucket>>();
        foreach (var sceneProp in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            var bucketList = new List<SceneBucket>();
            foreach (var bucketEl in sceneProp.Value.GetProperty("buckets").EnumerateArray())
            {
                string res = bucketEl.GetProperty("resolution").GetString()!;
                var rois = new List<Roi>();
                foreach (var roiEl in bucketEl.GetProperty("rois").EnumerateArray())
                {
                    var r = roiEl.GetProperty("rect");
                    var rect = new RelRect(
                        r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble());
                    rois.Add(new Roi(
                        roiEl.GetProperty("name").GetString()!,
                        roiEl.GetProperty("type").GetString()!,
                        rect));
                }
                bucketList.Add(new SceneBucket(res, rois));
            }
            scenes[sceneProp.Name] = bucketList;
        }
        return new RoiConfig(scenes);
    }
}
