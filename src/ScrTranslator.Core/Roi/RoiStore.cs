using System.Drawing;
using System.Linq;
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
                    IReadOnlyList<RoiColumn>? columns = null;
                    if (roiEl.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<RoiColumn>();
                        foreach (var c in colsEl.EnumerateArray())
                            list.Add(new RoiColumn(
                                c.GetProperty("start").GetDouble(),
                                c.GetProperty("end").GetDouble(),
                                ColumnKinds.Parse(c.GetProperty("kind").GetString() ?? "Other")));
                        columns = list;
                    }
                    rois.Add(new Roi(
                        roiEl.GetProperty("name").GetString()!,
                        roiEl.GetProperty("type").GetString()!,
                        rect, columns));
                }
                bucketList.Add(new SceneBucket(res, rois));
            }
            scenes[sceneProp.Name] = bucketList;
        }
        return new RoiConfig(scenes);
    }

    public static void Save(RoiConfig cfg, string jsonPath)
    {
        var scenes = new Dictionary<string, object>();
        foreach (var (scene, buckets) in cfg.Scenes)
        {
            scenes[scene] = new
            {
                buckets = buckets.Select(b => new
                {
                    resolution = b.Resolution,
                    rois = b.Rois.Select(r => r.Columns is null
                        ? (object)new { name = r.Name, type = r.Type, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H } }
                        : new { name = r.Name, type = r.Type, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H },
                                columns = r.Columns.Select(c => new { start = c.RelStart, end = c.RelEnd, kind = c.Kind.ToString() }) })
                })
            };
        }
        var root = new { scenes };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(root,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
