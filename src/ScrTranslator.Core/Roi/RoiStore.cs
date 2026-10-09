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
        if (!cfg.Scenes.TryGetValue(scene, out var buckets) || buckets.Count == 0)
            throw new InvalidOperationException($"未找到场景 '{scene}' 的 ROI 配置（rois.json 缺失/损坏/为空——请先校准）");
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

    /// <summary>读取 rois.json；文件缺失/损坏(截断/非法 JSON)一律**不抛异常**，返回空-但-合法的 RoiConfig
    /// (Scenes 为空字典)，避免校准/探测因一份坏文件而崩溃启动。</summary>
    public static RoiConfig Load(string jsonPath)
    {
        try
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
                // 场景键归一：老校准文件里的 custom_games_listing / waiting_room / in_game
                // 直接映射到 browsing / room / ingame，用户不必因为改名重校准。
                // 新旧键并存时以规范键那条为准。
                string sceneKey = Scene.GameSceneIds.Normalize(sceneProp.Name);
                if (!scenes.ContainsKey(sceneKey) || sceneProp.Name == sceneKey) scenes[sceneKey] = bucketList;
            }
            return new RoiConfig(scenes);
        }
        catch
        {
            // 文件不存在 / 权限问题 / JSON 截断或非法 / schema 缺字段 —— 一律兜底为空配置，不崩调用方。
            return new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>());
        }
    }

    /// <summary>
    /// 替换（或重命名）指定 (场景, 分辨率bucket, 序号) 的那一条 ROI，返回**新配置**（record 不可变，原对象不动）。
    /// 定位不到（场景/bucket/序号越界）时返回 null —— 调用方据此提示，绝不静默改到别的条目上。
    /// 给配置页的"编辑区域"用：允许在不进校准窗的情况下改名字/类型/像素矩形。
    /// </summary>
    public static RoiConfig? ReplaceRoi(RoiConfig cfg, string scene, string resolution, int index, Roi updated)
    {
        if (!Locate(cfg, scene, resolution, index, out var buckets, out var bucket, out var rois)) return null;
        var edited = rois.ToList();
        edited[index] = updated;
        return Rebuild(cfg, scene, buckets, ReplaceAt(buckets, bucket, new SceneBucket(bucket.Resolution, edited)));
    }

    /// <summary>删除指定 (场景, 分辨率bucket, 序号) 的那一条 ROI；定位失败同样返回 null。</summary>
    public static RoiConfig? RemoveRoi(RoiConfig cfg, string scene, string resolution, int index)
    {
        if (!Locate(cfg, scene, resolution, index, out var buckets, out var bucket, out var rois)) return null;
        var kept = rois.ToList();
        kept.RemoveAt(index);
        return Rebuild(cfg, scene, buckets, ReplaceAt(buckets, bucket, new SceneBucket(bucket.Resolution, kept)));
    }

    static bool Locate(RoiConfig cfg, string scene, string resolution, int index,
        out IReadOnlyList<SceneBucket> buckets, out SceneBucket bucket, out IReadOnlyList<Roi> rois)
    {
        buckets = Array.Empty<SceneBucket>(); bucket = null!; rois = Array.Empty<Roi>();
        if (index < 0 || !cfg.Scenes.TryGetValue(scene, out buckets)) return false;
        var found = buckets.FirstOrDefault(b => b.Resolution == resolution);
        if (found is null) return false;
        bucket = found;
        rois = found.Rois;
        return index < rois.Count;
    }

    static IReadOnlyList<SceneBucket> ReplaceAt(IReadOnlyList<SceneBucket> buckets, SceneBucket oldB, SceneBucket newB)
        => buckets.Select(b => ReferenceEquals(b, oldB) ? newB : b).ToList();

    static RoiConfig Rebuild(RoiConfig cfg, string scene, IReadOnlyList<SceneBucket> oldBuckets, IReadOnlyList<SceneBucket> newBuckets)
    {
        var map = new Dictionary<string, IReadOnlyList<SceneBucket>>(cfg.Scenes);
        map[scene] = newBuckets;
        return new RoiConfig(map);
    }

    /// <summary>写 rois.json：先写临时文件再 <see cref="File.Move(string,string,bool)"/> 原子替换，
    /// 避免写到一半被中断（崩溃/断电）导致目标文件截断损坏。</summary>
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
        string json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });

        string? dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = jsonPath + ".tmp" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json);
        try
        {
            File.Move(tmp, jsonPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort 清理，忽略失败 */ }
            throw;
        }
    }
}
