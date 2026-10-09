using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScrTranslator.Core.Scene;
using SkiaSharp;

namespace ScrTranslator.Tools.SceneRefGen;

/// <summary>
/// 生成场景识别像素参考 src/ScrTranslator.App/scene-refs.json，并输出全量验证矩阵。
/// 区域以 1080p 为基准坐标，按分辨率等比缩放；指纹为"块均值 RGB"，逐块带容差比对。
/// 新增截图/分辨率/区域：改下面的 SceneDefs 与 BaseWidth 等常量后重跑本工具。
/// </summary>
internal static class Program
{
    // ===== 指纹参数（1080p 基准）=====
    const int BaseWidth = 1920;      // 基准坐标所在分辨率宽度
    const int BlockSize = 4;         // 块宽（1080p 像素），其它分辨率按等比放大
    const int Tolerance = 16;        // 单块 RGB 最大通道差容差
    const double MinHitRatio = 0.90; // 命中率阈值，低于此判"其他界面"

    // ===== 区域定义（1080p 坐标 rect = x,y,w,h）=====
    // browsing / room 共用顶部金属边框两角（取更小的 room 框，两者重叠）
    static readonly Region[] FrameRegions =
    {
        new("frame-top-left", 0, 0, 250, 110),
        new("frame-top-right", 1650, 0, 270, 110),
    };
    // ingame 用底部种族装饰板（唯一既静态又分种族的锚点），左上角+宽高 120x120
    static readonly Region[] HudDecoRegions =
    {
        new("hud-deco-left", 420, 930, 120, 120),
        new("hud-deco-right", 1200, 910, 120, 120),
    };

    // ===== 场景 → 区域 + 各分辨率参考截图（键=截图宽度；缺的分辨率留空，运行时会退到最近分辨率）=====
    static readonly SceneDef[] SceneDefs =
    {
        new("browsing", "寻找房间", "browsing", null, FrameRegions,
            new() { [1920] = "browsing-1.png", [2560] = "browsing-2.png", [3840] = "browsing-4.png" }),
        new("room", "房间内", "room", null, FrameRegions,
            new() { [1920] = "room-1.png", [2560] = "room-2.png", [3840] = "room-4.png" }),
        new("ingame_t", "游戏内·人族", "ingame", "t", HudDecoRegions,
            new() { [1920] = "ingame-1t.png", [2560] = "ingame-2t.png", [3840] = "ingame-4t.png" }),
        new("ingame_p", "游戏内·神族", "ingame", "p", HudDecoRegions,
            new() { [1920] = "ingame-1p.png", [2560] = "ingame-2p.png", [3840] = "ingame-4p.png" }),
        new("ingame_z", "游戏内·虫族", "ingame", "z", HudDecoRegions,
            new() { [1920] = "ingame-1z.png", [2560] = "ingame-2z.png", [3840] = "ingame-4z.png" }),
    };

    static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var imgDir = Path.Combine(repoRoot, "docs", "scenes pictures");
        var outPath = Path.Combine(repoRoot, "src", "ScrTranslator.App", "scene-refs.json");
        if (!Directory.Exists(imgDir)) { Console.Error.WriteLine($"找不到截图目录 {imgDir}"); return 1; }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["generatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["generator"] = "tools/SceneRefGen",
            ["note"] = "场景识别像素参考。regions 为 1080p 基准坐标；resolutions 下每分辨率独立存块均值 RGB 指纹（base64, rgb24 行优先）。",
            ["fingerprint"] = new JsonObject
            {
                ["baseResolution"] = $"{BaseWidth}x{BaseWidth * 9 / 16}",
                ["blockSize"] = BlockSize,
                ["channelTolerance"] = Tolerance,
                ["minHitRatio"] = MinHitRatio,
                ["encoding"] = "rgb24-row-major-base64",
            },
            ["scenes"] = new JsonObject(),
        };
        var scenesNode = (JsonObject)root["scenes"]!;

        // 校验和条目：几何 + 指纹字节一起进哈希（算法在 Core 的 SceneRefIntegrity，与加载端同一份实现）。
        // 手改 data、打包截断、甚至只挪了一个 rect 坐标都会让校验失败，
        // 加载阶段直接报"参考不可用"，而不是拿着错位指纹在场上一路误判（那才是最难查的 bug）。
        var integrityEntries = new List<SceneRefIntegrity.Entry>();
        // 缓存：分辨率宽度 → (场景 → 指纹)，供验证用
        var refCache = new Dictionary<int, Dictionary<string, Fingerprint>>();

        foreach (var scene in SceneDefs)
        {
            var sceneNode = new JsonObject
            {
                ["label"] = scene.Label,
                ["gameScene"] = scene.GameScene,
                ["regions"] = new JsonArray(scene.Regions.Select(r => (JsonNode)new JsonObject
                { ["name"] = r.Name, ["rect"] = new JsonArray(r.X, r.Y, r.W, r.H) }).ToArray()),
                ["resolutions"] = new JsonObject(),
            };
            if (scene.Race != null) sceneNode["race"] = scene.Race;
            var resNode = (JsonObject)sceneNode["resolutions"]!;

            foreach (var (width, file) in scene.SourcesByWidth.OrderBy(k => k.Key))
            {
                var path = Path.Combine(imgDir, file);
                if (!File.Exists(path)) { Console.Error.WriteLine($"!! 缺参考图 {scene.Id} {width}p: {file}（跳过该分辨率）"); continue; }
                using var bm = Decode(path);
                if (bm.Width != width)
                {
                    Console.Error.WriteLine($"!! {file} 实际宽 {bm.Width} ≠ 声明 {width}，跳过");
                    continue;
                }
                double scale = (double)width / BaseWidth;
                string resKey = $"{width}x{width * 9 / 16}";
                var regs = new JsonArray();
                var byScene = refCache.TryGetValue(width, out var d) ? d : new Dictionary<string, Fingerprint>();
                foreach (var region in scene.Regions)
                {
                    var rect = ScaledRect(region, scale);
                    var fp = BuildFingerprint(bm, rect, scale);
                    integrityEntries.Add(new SceneRefIntegrity.Entry(
                        SceneRefIntegrity.Key(scene.Id, resKey, region.Name),
                        SceneRefIntegrity.Geometry(rect.X, rect.Y, rect.W, rect.H, BlockPx(scale), fp.BlocksX, fp.BlocksY),
                        fp.Bytes));
                    byScene[scene.Id] = MergeFp(byScene.TryGetValue(scene.Id, out var old) ? old : null, fp);
                    regs.Add((JsonNode)new JsonObject
                    {
                        ["name"] = region.Name,
                        ["rect"] = new JsonArray(rect.X, rect.Y, rect.W, rect.H),
                        // blockPx 必须一起写死：运行时要用与生成时完全相同的块宽做均值，
                        // 否则 (rect.W / blocks) 的整除误差会让网格错位、指纹全不命中。
                        ["blockPx"] = BlockPx(scale),
                        ["blocks"] = new JsonArray(fp.BlocksX, fp.BlocksY),
                        ["data"] = Convert.ToBase64String(fp.Bytes),
                    });
                }
                refCache[width] = byScene;
                resNode[resKey] = new JsonObject { ["source"] = file, ["regions"] = regs };
            }
            scenesNode[scene.Id] = sceneNode;
        }

        root["integrity"] = new JsonObject
        {
            ["algorithm"] = SceneRefIntegrity.Algorithm,
            ["note"] = "覆盖各区域的 键+几何+指纹字节，改任何坐标或数据都会失效（SceneRefStore 加载时校验）",
            ["bytes"] = SceneRefIntegrity.DataBytes(integrityEntries),
            ["sha256"] = SceneRefIntegrity.Hash(integrityEntries),
        };

        long totalBytes = SceneRefIntegrity.DataBytes(integrityEntries);

        File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        Console.WriteLine($"✓ 写入 {Path.GetRelativePath(repoRoot, outPath)}  指纹数据 {totalBytes / 1024.0:F0} KB  文件 {new FileInfo(outPath).Length / 1024.0:F0} KB");

        return Validate(repoRoot, imgDir, refCache) ? 0 : 2;
    }

    // ===== 验证：每张同分辨率截图对全部场景参考的命中率，判对应否 =====
    static bool Validate(string repoRoot, string imgDir, Dictionary<int, Dictionary<string, Fingerprint>> refCache)
    {
        bool allOk = true;
        double threshold = MinHitRatio * 100;
        foreach (var width in new[] { 1920, 2560, 3840 })
        {
            if (!refCache.TryGetValue(width, out var refs)) continue;
            double scale = (double)width / BaseWidth;
            Console.WriteLine($"\n===== 验证 {width}x{width * 9 / 16}（阈值 {threshold:F0}%）=====");
            Console.Write($"{"待判图",-16}{string.Concat(SceneDefs.Select(s => $"{s.Id,11}"))}  判定 → 期望");
            Console.WriteLine();
            foreach (var path in Directory.GetFiles(imgDir, "*.png").OrderBy(p => p))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("ingame-w") || name.StartsWith("ingame-h")) continue;   // 量聊天区用的，不参与
                using var bm = Decode(path);
                if (bm.Width != width) continue;
                var expected = ExpectedScene(name);
                Console.Write($"{name,-16}");
                double best = -1; string bestId = "?";
                foreach (var scene in SceneDefs)
                {
                    if (!refs.TryGetValue(scene.Id, out var rf)) { Console.Write($"{"-",11}"); continue; }
                    var live = LiveFingerprint(bm, scene.Regions, scale);
                    double v = HitRatio(live, rf);
                    Console.Write($"{v,11:F1}");
                    if (v > best) { best = v; bestId = scene.Id; }
                }
                bool ok = bestId == expected;
                allOk &= ok;
                Console.WriteLine($"  {bestId} ({best:F1}%) → {expected}{(ok ? "  ✓" : "  ✗✗")}");
            }
        }
        Console.WriteLine(allOk ? "\n✓ 全部样本判定正确" : "\n✗ 存在误判，检查区域或容差");
        return allOk;
    }

    static string ExpectedScene(string fileName) => fileName switch
    {
        var s when s.StartsWith("browsing") => "browsing",
        var s when s.StartsWith("room") => "room",
        var s when s.EndsWith("t") => "ingame_t",
        var s when s.EndsWith("p") => "ingame_p",
        var s when s.EndsWith("z") => "ingame_z",
        _ => "?",
    };

    // ===== 指纹 =====
    record struct Rect(int X, int Y, int W, int H);
    record struct Fingerprint(int BlocksX, int BlocksY, byte[] Bytes);
    record Region(string Name, int X, int Y, int W, int H);
    record SceneDef(string Id, string Label, string GameScene, string? Race, Region[] Regions, Dictionary<int, string> SourcesByWidth);

    static Rect ScaledRect(Region r, double scale) => new(
        (int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale),
        (int)Math.Round(r.W * scale), (int)Math.Round(r.H * scale));

    /// <summary>该分辨率下的实际块宽（像素）。生成与运行时共用同一公式，保证网格对齐。</summary>
    static int BlockPx(double scale) => Math.Max(2, (int)Math.Round(BlockSize * scale));

    static Fingerprint BuildFingerprint(SKBitmap bm, Rect rect, double scale)
    {
        int bs = BlockPx(scale);
        int bx = rect.W / bs, by = rect.H / bs;
        if (bx < 1 || by < 1) throw new InvalidOperationException($"区域过小 {rect.W}x{rect.H} 块 {bs}");
        var bytes = new byte[bx * by * 3];
        var (ro, go, bo) = ChannelOffsets(bm);
        unsafe
        {
            byte* p = (byte*)bm.GetPixels();
            for (int gy = 0; gy < by; gy++)
                for (int gx = 0; gx < bx; gx++)
                {
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int yy = 0; yy < bs; yy++)
                        for (int xx = 0; xx < bs; xx++)
                        {
                            byte* q = p + (long)(rect.Y + gy * bs + yy) * bm.RowBytes + (long)(rect.X + gx * bs + xx) * 4;
                            sr += q[ro]; sg += q[go]; sb += q[bo]; n++;
                        }
                    int o = (gy * bx + gx) * 3;
                    bytes[o] = (byte)(sr / n); bytes[o + 1] = (byte)(sg / n); bytes[o + 2] = (byte)(sb / n);
                }
        }
        return new Fingerprint(bx, by, bytes);
    }

    /// <summary>把某场景所有区域的指纹拼成一维（运行时同样顺序拼接即可直接比）。</summary>
    static Fingerprint LiveFingerprint(SKBitmap bm, Region[] regions, double scale)
    {
        var parts = regions.Select(r => BuildFingerprint(bm, ScaledRect(r, scale), scale)).ToArray();
        int total = parts.Sum(p => p.Bytes.Length);
        var buf = new byte[total];
        int off = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p.Bytes, 0, buf, off, p.Bytes.Length); off += p.Bytes.Length; }
        return new Fingerprint(parts[0].BlocksX, parts.Sum(p => p.BlocksY), buf);
    }

    static Fingerprint MergeFp(Fingerprint? old, Fingerprint add)
        => old == null ? add : new Fingerprint(old.Value.BlocksX, old.Value.BlocksY + add.BlocksY, [.. old.Value.Bytes, .. add.Bytes]);

    static double HitRatio(Fingerprint live, Fingerprint rf)
    {
        int n = Math.Min(live.Bytes.Length, rf.Bytes.Length) / 3;
        if (n == 0) return 0;
        int hit = 0;
        for (int k = 0; k < n; k++)
        {
            int o = k * 3;
            int d = Math.Max(Math.Abs(live.Bytes[o] - rf.Bytes[o]),
                    Math.Max(Math.Abs(live.Bytes[o + 1] - rf.Bytes[o + 1]), Math.Abs(live.Bytes[o + 2] - rf.Bytes[o + 2])));
            if (d <= Tolerance) hit++;
        }
        return 100.0 * hit / n;
    }

    static (int R, int G, int B) ChannelOffsets(SKBitmap bm) => bm.ColorType switch
    {
        SKColorType.Bgra8888 => (2, 1, 0),
        SKColorType.Rgba8888 => (0, 1, 2),
        _ => throw new InvalidOperationException($"不支持的颜色类型 {bm.ColorType}"),
    };

    static SKBitmap Decode(string path)
    {
        var bm = SKBitmap.Decode(path) ?? throw new InvalidOperationException($"解码失败 {path}");
        if (bm.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
        {
            var conv = new SKBitmap(bm.Width, bm.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var cc = new SKCanvas(conv);
            cc.DrawBitmap(bm, 0, 0);
            bm.Dispose();
            return conv;
        }
        return bm;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScrTranslator.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
