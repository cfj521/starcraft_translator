using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrTranslator.Core.Scene;

/// <summary>
/// 现场场景指纹采集：把"当前这一帧"算成某场景在该分辨率的块均值 RGB 指纹，写进覆盖层
/// scene-overrides.json（与出厂 scene-refs.json 同一 schema，但不带 integrity）。
///
/// 这是覆盖层的**唯一写入路径**——此前"读取与合并"已就绪却没有任何代码往里写，
/// "用户现场补新分辨率/新场景参考"这条链是断的。本类补上采集端。
///
/// 区域几何 = 出厂参考的 1080p 基准区域按分辨率等比缩放，公式与 tools/SceneRefGen 完全一致
/// （ScaledRect + BlockPx），保证"现场采集"与"出厂生成"的几何同源，不会网格错位。
/// </summary>
public static class SceneRefCapture
{
    /// <summary>把基准区域（1080p 坐标）等比缩放到目标分辨率。与 SceneRefGen.ScaledRect/BlockPx 同一公式。</summary>
    public static IReadOnlyList<SceneRefRegion> ScaleRegions(IReadOnlyList<SceneRefRegion> baseRegions, double scale, int baseBlockSize)
    {
        var result = new List<SceneRefRegion>(baseRegions.Count);
        foreach (var r in baseRegions)
        {
            int w = (int)Math.Round(r.Width * scale);
            int h = (int)Math.Round(r.Height * scale);
            int bs = Math.Max(2, (int)Math.Round(baseBlockSize * scale));
            int bx = Math.Max(1, w / bs), by = Math.Max(1, h / bs);
            result.Add(new SceneRefRegion(r.Name,
                (int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale), w, h, bs, bx, by));
        }
        return result;
    }

    /// <summary>取某场景出厂参考的 1080p 基准区域。null = 该场景无参考（出厂层缺失或整份被拒用）。</summary>
    public static IReadOnlyList<SceneRefRegion>? BaseRegions(SceneRefStore store, string sceneId)
    {
        var reference = store.Find(sceneId);
        if (reference is null) return null;
        string baseKey = $"{store.BaseWidth}x{store.BaseWidth * 9 / 16}";
        return reference.Resolutions.TryGetValue(baseKey, out var baseRes) ? baseRes.Regions : null;
    }

    /// <summary>
    /// 采集：把基准区域缩放到 (width,height)，对帧逐区域算块均值指纹。
    /// 返回缩放后的区域与**逐区域**指纹字节（写 JSON 时每个区域要各自带 data，不能拼成一整段再拆）。
    /// </summary>
    public static (IReadOnlyList<SceneRefRegion> Regions, byte[][] RegionBytes) Capture(
        in FramePixels frame, IReadOnlyList<SceneRefRegion> baseRegions, int width, int height, int baseWidth, int baseBlockSize)
    {
        double scale = (double)width / baseWidth;
        var regions = ScaleRegions(baseRegions, scale, baseBlockSize);
        var bytes = new byte[regions.Count][];
        for (int i = 0; i < regions.Count; i++)
        {
            bytes[i] = new byte[regions[i].ByteLength];
            SceneFingerprint.Compute(frame, new[] { regions[i] }, bytes[i]);
        }
        return (regions, bytes);
    }

    /// <summary>
    /// 把一条现场采集的参考写进覆盖层。同 (场景,分辨率) 覆盖胜出；新场景直接追加。文件不存在则新建。
    /// schema 与出厂层相同（version/scenes/…），但**不写 integrity**——覆盖层本就接受用户写入，不校验。
    /// </summary>
    public static void SaveOverride(string path, string sceneId, string label, string gameScene, string? race,
        int width, int height, string source, IReadOnlyList<SceneRefRegion> regions, byte[][] regionBytes)
    {
        JsonObject root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed
            ? parsed : new JsonObject();
        if (!root.ContainsKey("version")) root["version"] = SceneRefStore.SupportedVersion;

        var scenes = GetOrCreate(root, "scenes");
        var sceneNode = GetOrCreate(scenes, sceneId);
        sceneNode["label"] = label;
        sceneNode["gameScene"] = gameScene;
        if (race is not null) sceneNode["race"] = race; else sceneNode.Remove("race");

        var resolutions = GetOrCreate(sceneNode, "resolutions");

        string resKey = $"{width}x{height}";
        var regionsArr = new JsonArray();
        for (int i = 0; i < regions.Count; i++)
        {
            var reg = regions[i];
            regionsArr.Add(new JsonObject
            {
                ["name"] = reg.Name,
                ["rect"] = new JsonArray(reg.X, reg.Y, reg.Width, reg.Height),
                ["blockPx"] = reg.BlockPx,
                ["blocks"] = new JsonArray(reg.BlocksX, reg.BlocksY),
                ["data"] = Convert.ToBase64String(regionBytes[i]),
            });
        }
        resolutions[resKey] = new JsonObject { ["source"] = source, ["regions"] = regionsArr };

        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
    }

    /// <summary>取子对象；不存在或不是对象则新建并挂上（避免把已有数据覆盖掉）。</summary>
    static JsonObject GetOrCreate(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing; // 索引器对缺失键返回 null
        var obj = new JsonObject();
        parent[key] = obj;
        return obj;
    }
}
