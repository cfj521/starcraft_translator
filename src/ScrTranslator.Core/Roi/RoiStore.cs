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

    /// <summary>取某场景的 ROI 列表。相对坐标跨分辨率通用（等比缩放），不按分辨率匹配。</summary>
    public static IReadOnlyList<Roi> SelectRois(RoiConfig cfg, string scene)
    {
        if (!cfg.Scenes.TryGetValue(scene, out var rois) || rois.Count == 0)
            throw new InvalidOperationException($"未找到场景 '{scene}' 的 ROI 配置（rois.json 缺失/损坏/为空——请先校准）");
        return rois;
    }

    /// <summary>读取 rois.json；文件缺失/损坏(截断/非法 JSON)一律**不抛异常**，返回空-但-合法的 RoiConfig
    /// (Scenes 为空字典)，避免校准/探测因一份坏文件而崩溃启动。
    /// 兼容旧结构：老文件里场景键下是 buckets 数组（按分辨率分档），新结构是 rois 数组。
    /// 旧结构的各档 ROI 是同一份相对坐标（等比缩放，冗余存储），取首档即可。</summary>
    public static RoiConfig Load(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var scenes = new Dictionary<string, IReadOnlyList<Roi>>();
            foreach (var sceneProp in doc.RootElement.GetProperty("scenes").EnumerateObject())
            {
                IReadOnlyList<Roi> rois;
                if (sceneProp.Value.TryGetProperty("rois", out var newRoisEl) && newRoisEl.ValueKind == JsonValueKind.Array)
                {
                    // 新结构：场景键下直接是 rois 数组。
                    rois = ParseRois(newRoisEl);
                }
                else if (sceneProp.Value.TryGetProperty("buckets", out var bucketsEl) && bucketsEl.ValueKind == JsonValueKind.Array)
                {
                    // 旧结构兼容：buckets 数组，取首档的 rois（各档是同一份相对坐标，冗余）。
                    rois = Array.Empty<Roi>();
                    foreach (var b in bucketsEl.EnumerateArray())
                    {
                        if (b.TryGetProperty("rois", out var r) && r.ValueKind == JsonValueKind.Array)
                        {
                            rois = ParseRois(r);
                            break;
                        }
                    }
                }
                else
                {
                    rois = Array.Empty<Roi>();
                }

                // 场景键归一：老校准文件里的 custom_games_listing / waiting_room / in_game
                // 直接映射到 browsing / room / ingame，用户不必因为改名重校准。
                // 新旧键并存时以规范键那条为准。
                string sceneKey = Scene.GameSceneIds.Normalize(sceneProp.Name);
                if (!scenes.ContainsKey(sceneKey) || sceneProp.Name == sceneKey) scenes[sceneKey] = rois;
            }
            return new RoiConfig(scenes);
        }
        catch
        {
            // 文件不存在 / 权限问题 / JSON 截断或非法 / schema 缺字段 —— 一律兜底为空配置，不崩调用方。
            return new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>>());
        }
    }

    static IReadOnlyList<Roi> ParseRois(JsonElement roisEl)
    {
        var rois = new List<Roi>();
        foreach (var roiEl in roisEl.EnumerateArray())
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
                rect, columns));
        }
        return rois;
    }

    /// <summary>
    /// 替换（或重命名）指定 (场景, 序号) 的那一条 ROI，返回**新配置**（record 不可变，原对象不动）。
    /// 定位不到（场景/序号越界）时返回 null —— 调用方据此提示，绝不静默改到别的条目上。
    /// 给配置页的"编辑区域"用：允许在不进校准窗的情况下改名字/类型/像素矩形。
    /// </summary>
    public static RoiConfig? ReplaceRoi(RoiConfig cfg, string scene, int index, Roi updated)
    {
        if (!cfg.Scenes.TryGetValue(scene, out var rois) || index < 0 || index >= rois.Count) return null;
        var edited = rois.ToList();
        edited[index] = updated;
        return Rebuild(cfg, scene, edited);
    }

    /// <summary>删除指定 (场景, 序号) 的那一条 ROI；定位失败同样返回 null。</summary>
    public static RoiConfig? RemoveRoi(RoiConfig cfg, string scene, int index)
    {
        if (!cfg.Scenes.TryGetValue(scene, out var rois) || index < 0 || index >= rois.Count) return null;
        var kept = rois.ToList();
        kept.RemoveAt(index);
        return Rebuild(cfg, scene, kept);
    }

    /// <summary>
    /// 把一条 ROI 从某场景挪到另一个场景（区域页"改场景"用）：从源场景删除，追加到目标场景。
    /// 定位失败返回 null，绝不静默复制出第二条。
    /// 用途：误在别的界面按了 F8，条目落错了场景——改个场景即可，不必删了重画。
    /// </summary>
    public static RoiConfig? MoveRoi(RoiConfig cfg, string fromScene, int index, string toScene)
    {
        if (fromScene == toScene) return cfg;
        if (!cfg.Scenes.TryGetValue(fromScene, out var rois) || index < 0 || index >= rois.Count) return null;
        var moved = rois[index];
        var shrunk = rois.ToList();
        shrunk.RemoveAt(index);

        var map = new Dictionary<string, IReadOnlyList<Roi>>(cfg.Scenes);
        map[fromScene] = shrunk;

        map.TryGetValue(toScene, out var toRois);
        toRois ??= Array.Empty<Roi>();
        map[toScene] = toRois.Append(moved).ToList();

        return new RoiConfig(map);
    }

    static RoiConfig Rebuild(RoiConfig cfg, string scene, IReadOnlyList<Roi> rois)
    {
        var map = new Dictionary<string, IReadOnlyList<Roi>>(cfg.Scenes);
        map[scene] = rois;
        return new RoiConfig(map);
    }

    /// <summary>写 rois.json：先写临时文件再 <see cref="File.Move(string,string,bool)"/> 原子替换，
    /// 避免写到一半被中断（崩溃/断电）导致目标文件截断损坏。</summary>
    public static void Save(RoiConfig cfg, string jsonPath)
    {
        var scenes = new Dictionary<string, object>();
        foreach (var (scene, rois) in cfg.Scenes)
        {
            scenes[scene] = new
            {
                rois = rois.Select(r => r.Columns is null
                    ? (object)new { name = r.Name, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H } }
                    : new { name = r.Name, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H },
                            columns = r.Columns.Select(c => new { start = c.RelStart, end = c.RelEnd, kind = c.Kind.ToString() }) })
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
