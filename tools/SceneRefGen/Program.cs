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
    // ===== 指纹参数（16:9 / 1080p 基准）=====
    const int BaseWidth = 1920;      // 基准坐标所在分辨率宽度
    const int BaseHeight = 1080;     // 基准坐标所在分辨率高度（= BaseWidth * 9 / 16）
    const string BaseRatio = "16:9"; // 运行时只认这一档基准区域（任意分辨率按 H/1080 缩放后比对）
    const int BlockSize = 4;         // 块宽（1080p 像素），决定 blocks；运行时块宽由区域宽/blocks 推导
    const int Tolerance = 16;        // 单块 RGB 最大通道差容差
    const double MinHitRatio = 0.90; // 命中率阈值，低于此判"其他界面"

    // ===== 区域定义（1080p 坐标 rect = x,y,w,h）=====
    // browsing / room 共用顶部金属边框的"左上主体"。旧版曾取两角，非 16:9 被居中裁剪裁掉边角而失效；
    // 改用内缩的 frame-left（避开裁剪区），三套比例下静态不变、跨分辨率 100% 命中，单区即可，
    // frame-right 冗余，已移除（见 tools/FingerprintCheck crosstest）。
    static readonly Region[] FrameRegions =
    {
        new("frame-left", 240, 0, 183, 42),
    };
    // ingame 用底部信息栏左侧一条窄带：统一按高度因子 H/1080 等比缩放（x/y/w/h 同因子，无居中裁剪），
    // 跨分辨率静态（实测 5 档 100% 命中），且随种族区分度高（非对角线仅 50~60%，余量 30~40 个百分点）。
    // 取代旧的两块装饰板（420,930,120,120 / 1200,910,120,120）——它们在 4:3/16:10 被裁掉或残缺，跨比例失效。
    // 曾试过更大的区域 (0,1004,211,77)：场景识别没问题，但三种族共享静态内容太多，非对角线升到 78~86%、
    // 种族区分（尤其 p）余量只剩个位数，弹窗/局部动画一来就串，故回退到此窄带。
    static readonly Region[] IngamRaceRegions =
    {
        new("race-hud", 0, 712, 188, 11),
    };

    // ===== 场景 → 区域 + 参考截图 =====
    // 每个 gameScene 只存 16:9 一档参考：运行时把基准区域按 H/1080 缩放（(browsing/room 另加居中裁剪)）
    // 后与任意分辨率帧比对，无需为每个分辨率各存一份。
    // 同一 gameScene 下的多条 SceneDef 必须共用同一套区域（文件里 regions 只存一份）。
    static readonly SceneDef[] SceneDefs =
    {
        new("browsing", "寻找房间", "browsing", null, FrameRegions,
            new() { ["16:9"] = "browsing-1920x1080.png" }),
        new("room", "房间内", "room", null, FrameRegions,
            new() { ["16:9"] = "room-3840x2160.png" }),
        new("ingame_t", "游戏内·人族", "ingame", "t", IngamRaceRegions,
            new() { ["16:9"] = "ingame-1920x1080t.png" }),
        new("ingame_p", "游戏内·神族", "ingame", "p", IngamRaceRegions,
            new() { ["16:9"] = "ingame-1920x1080p.png" }),
        new("ingame_z", "游戏内·虫族", "ingame", "z", IngamRaceRegions,
            new() { ["16:9"] = "ingame-1920x1080z.png" }),
    };

    static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        // 16:9 参考图在 resources/ss-scenes（文件名带分辨率后缀，如 browsing-1920x1080.png）
        var imgDir = Path.Combine(repoRoot, "resources", "ss-scenes");
        var outPath = Path.Combine(repoRoot, "src", "ScrTranslator.App", "scene-refs.json");
        if (!Directory.Exists(imgDir)) { Console.Error.WriteLine($"找不到截图目录 {imgDir}"); return 1; }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["generatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["generator"] = "tools/SceneRefGen",
            ["note"] = "场景识别像素参考。scenes 以 gameScene 为键（browsing/room/ingame）；regions 为该场景的 16:9 基准几何（name+rect，相对值）；data 为按比例（ratio）存的块均值 RGB 指纹（base64, rgb24 行优先），ingame 的 data 条目用 race 字段区分种族。",
            ["fingerprint"] = new JsonObject
            {
                ["baseRatio"] = BaseRatio,
                ["baseResolution"] = $"{BaseWidth}x{BaseHeight}",
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
        // 参考缓存：scene id → 指纹（每个 id 只有 16:9 一档；方案 B 下任意分辨率都拿这一份比）
        var refs = new Dictionary<string, Fingerprint>();

        // 新格式：scenes 以 gameScene 为键，种族不再是键的一部分（ingame 一个键装 t/p/z 三条 data）。
        foreach (var group in SceneDefs.GroupBy(s => s.GameScene))
        {
            var defs = group.ToList();
            var primary = defs[0];
            // 同 gameScene 下各 SceneDef 必须共用同一套区域：regions 在文件里只存一份。
            foreach (var d in defs.Skip(1))
            {
                if (d.Regions.Length != primary.Regions.Length ||
                    !d.Regions.Zip(primary.Regions).All(p => p.First == p.Second))
                    Console.Error.WriteLine($"!! {primary.GameScene} 下 {d.Id} 的区域与 {primary.Id} 不同，" +
                                            "而新格式每个 gameScene 只存一份 regions —— 请合并为同一套区域");
            }

            var sceneNode = new JsonObject
            {
                ["label"] = string.Join("/", defs.Select(d => d.Label)),
                ["gameScene"] = group.Key,
                // 相对 16:9 基准的 0~1 值（= 绝对 ÷ 1920 / ÷ 1080）。
                ["regions"] = new JsonObject
                {
                    ["name"] = primary.Regions[0].Name,
                    ["rect"] = RegionRectJson(primary.Regions[0]),
                },
                ["data"] = new JsonArray(),
            };
            var dataNode = (JsonArray)sceneNode["data"]!;

            foreach (var scene in defs)
            {
                foreach (var (ratio, file) in scene.SourcesByRatio.OrderBy(k => k.Key))
                {
                    // 运行时只存/只认 16:9 一档：任意分辨率都是把 16:9 基准区域按 H/1080 缩放后比对
                    // （方案 B）。其它比例的条目永远不会被选中，生成出来只会是死数据，故直接拒绝。
                    if (ratio != BaseRatio)
                    {
                        Console.Error.WriteLine($"!! {scene.Id} 的 {ratio} 参考（{file}）已跳过：" +
                                                $"运行时只使用 {BaseRatio} 基准区域按高度缩放，不消费其它比例条目");
                        continue;
                    }
                    var path = Path.Combine(imgDir, file);
                    if (!File.Exists(path)) { Console.Error.WriteLine($"!! 缺参考图 {scene.Id} {ratio}: {file}（跳过该比例）"); continue; }
                    using var bm = Decode(path);
                    int width = bm.Width, height = bm.Height;
                    // 换算式与运行时完全一致（SceneRefCapture.FittingRegions）：
                    // 统一高度因子 H/1080，browsing/room 叠加居中水平裁剪，ingame 贴左边缘不裁。
                    var rects = scene.Regions.Select(r => ScaledRect(r, width, height, CenterCropFor(scene.GameScene))).ToArray();
                    var parts = new List<Fingerprint>();
                    foreach (var (region, rect) in scene.Regions.Zip(rects))
                    {
                        var fp = BuildFingerprint(bm, rect);
                        parts.Add(fp);
                        double relX = region.X / (double)BaseWidth, relY = region.Y / (double)BaseHeight,
                               relW = region.W / (double)BaseWidth, relH = region.H / (double)BaseHeight;
                        integrityEntries.Add(new SceneRefIntegrity.Entry(
                            SceneRefIntegrity.Key(scene.Id, ratio, region.Name),
                            SceneRefIntegrity.Geometry(relX, relY, relW, relH, BlockPx(height), fp.BlocksX, fp.BlocksY),
                            fp.Bytes));
                        dataNode.Add(new JsonObject
                        {
                            ["ratio"] = ratio,
                            ["source_file"] = file,
                            // blockPx 仅作显示/诊断：运行时块宽 = 缩放后区域宽 / blocks（见 SceneFingerprint.Compute），
                            // 真正决定网格的是 blocks，务必与生成时一致。
                            ["blockPx"] = BlockPx(height),
                            ["blocks"] = new JsonArray(fp.BlocksX, fp.BlocksY),
                            ["data"] = Convert.ToBase64String(fp.Bytes),
                            ["race"] = scene.GameScene == "ingame" ? scene.Race : null,
                        });
                    }
                    refs[scene.Id] = parts.Count == 1 ? parts[0] : MergeFp(parts);
                }
            }
            scenesNode[group.Key] = sceneNode;
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

        return Validate(imgDir, refs) ? 0 : 2;
    }

    // ===== 验证：每张截图对全部场景参考的命中率，判对应否 =====
    // 几何换算必须与运行时（SceneRefCapture.FittingRegions + SceneRecognizer 的 centerCrop 规则）一致，
    // 否则矩阵会"自洽地报 100%"，而实机因为 crop 差几像素整片不命中。
    static bool Validate(string imgDir, Dictionary<string, Fingerprint> refs)
    {
        bool allOk = true;
        double threshold = MinHitRatio * 100;
        var shots = Directory.GetFiles(imgDir, "*.png")
            .Select(p => (Path: p, Name: Path.GetFileNameWithoutExtension(p)))
            .Where(x => !x.Name.StartsWith("ingame-w") && !x.Name.StartsWith("ingame-h"))   // 量聊天区用的，不参与
            .Select(x =>
            {
                // 先用编解码器读尺寸分组，避免一次性解码整批 4K 位图；真正比时再逐张解码。
                using var codec = SKCodec.Create(x.Path) ?? throw new InvalidOperationException($"解码失败 {x.Path}");
                return (x.Path, x.Name, codec.Info.Width, codec.Info.Height);
            })
            .GroupBy(x => (x.Width, x.Height))
            .OrderBy(g => g.Key.Width);

        foreach (var group in shots)
        {
            var (width, height) = group.Key;
            Console.WriteLine($"\n===== 验证 {width}x{height}（阈值 {threshold:F0}%）=====");
            Console.Write($"{"待判图",-16}{string.Concat(SceneDefs.Select(s => $"{s.Id,11}"))}  判定 → 期望");
            Console.WriteLine();
            foreach (var shot in group.OrderBy(x => x.Name))
            {
                using var bm = Decode(shot.Path);
                var expected = ExpectedScene(shot.Name);
                Console.Write($"{shot.Name,-16}");
                double best = -1; string bestId = "?";
                foreach (var scene in SceneDefs)
                {
                    if (!refs.TryGetValue(scene.Id, out var rf)) { Console.Write($"{"-",11}"); continue; }
                    var live = LiveFingerprint(bm, scene.Regions, width, height, CenterCropFor(scene.GameScene), rf);
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
    record SceneDef(string Id, string Label, string GameScene, string? Race, Region[] Regions, Dictionary<string, string> SourcesByRatio);

    /// <summary>与运行时同为"游戏内不裁剪、其它场景居中水平裁剪"（见 SceneRecognizer.Recognize）。</summary>
    static bool CenterCropFor(string gameScene) => gameScene != "ingame";

    static JsonArray RegionRectJson(Region r) => new(
        r.X / (double)BaseWidth, r.Y / (double)BaseHeight,
        r.W / (double)BaseWidth, r.H / (double)BaseHeight);

    /// <summary>基准（1920×1080）区域 → 目标分辨率像素矩形。**与 SceneRefCapture.FittingRegions 同一公式**：
    /// 统一高度因子 H/1080（x/y/w/h 同因子），<paramref name="centerCrop"/> 时再叠加居中水平裁剪
    /// crop=(1920−1080·ar)/2，x=(基准x−crop)·H/1080。绝不在这里另立一套缩放口径。</summary>
    static Rect ScaledRect(Region r, int width, int height, bool centerCrop)
    {
        double sy = height / (double)BaseHeight;
        double crop = centerCrop ? (BaseWidth - BaseHeight * (width / (double)height)) / 2.0 : 0.0;
        return new Rect(
            (int)Math.Round((r.X - crop) * sy), (int)Math.Round(r.Y * sy),
            (int)Math.Round(r.W * sy), (int)Math.Round(r.H * sy));
    }

    /// <summary>名义块宽（像素，仅用于推导块数 / 写 blockPx 供诊断）。运行时实际块宽 = 区域宽/blocks。</summary>
    static int BlockPx(int height) => Math.Max(2, (int)Math.Round(BlockSize * (height / (double)BaseHeight)));

    static Fingerprint BuildFingerprint(SKBitmap bm, Rect rect, int blocksX = 0, int blocksY = 0)
    {
        // 块数**必须沿用出厂参考的 BlocksX/BlocksY**（1080p 那套网格），与运行时 SceneFingerprint.Compute 一致。
        // 若按目标分辨率的块宽重算块数，非整数缩放下块数会变（1024 时 45→43），
        // 指纹长度就与出厂参考不等，比对必然掉成"随机"——这正是验证矩阵此前自欺的根源。
        if (blocksX <= 0 || blocksY <= 0)
        {
            int bs = Math.Max(2, (int)Math.Round(BlockSize * (bm.Height / (double)BaseHeight)));
            blocksX = Math.Max(1, rect.W / bs);
            blocksY = Math.Max(1, rect.H / bs);
        }
        var bytes = new byte[blocksX * blocksY * 3];
        var (ro, go, bo) = ChannelOffsets(bm);
        double bsx = rect.W / (double)blocksX;   // 均分块宽（浮点），与 Compute 同
        double bsy = rect.H / (double)blocksY;
        unsafe
        {
            byte* p = (byte*)bm.GetPixels();
            for (int gy = 0; gy < blocksY; gy++)
                for (int gx = 0; gx < blocksX; gx++)
                {
                    int x0 = Math.Max(0, (int)Math.Round(rect.X + gx * bsx));
                    int x1 = Math.Min(bm.Width, (int)Math.Round(rect.X + (gx + 1) * bsx));
                    int y0 = Math.Max(0, (int)Math.Round(rect.Y + gy * bsy));
                    int y1 = Math.Min(bm.Height, (int)Math.Round(rect.Y + (gy + 1) * bsy));
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            byte* q = p + (long)yy * bm.RowBytes + (long)xx * 4;
                            sr += q[ro]; sg += q[go]; sb += q[bo]; n++;
                        }
                    int o = (gy * blocksX + gx) * 3;
                    bytes[o] = (byte)(n == 0 ? 0 : sr / n);
                    bytes[o + 1] = (byte)(n == 0 ? 0 : sg / n);
                    bytes[o + 2] = (byte)(n == 0 ? 0 : sb / n);
                }
        }
        return new Fingerprint(blocksX, blocksY, bytes);
    }

    /// <summary>把某场景所有区域的指纹拼成一维（运行时同样顺序拼接后直接比）。
    /// 块网格取自参考指纹（<paramref name="rf"/>），与运行时"块数由参考决定"同口径。</summary>
    static Fingerprint LiveFingerprint(SKBitmap bm, Region[] regions, int width, int height, bool centerCrop,
        Fingerprint rf)
    {
        var rects = regions.Select(r => ScaledRect(r, width, height, centerCrop)).ToArray();
        if (regions.Length == 1)
            return BuildFingerprint(bm, rects[0], rf.BlocksX, rf.BlocksY);
        // 多区域：按参考块的纵向分布拆分（当前每个场景只有一个区域，此分支仅防御）
        return MergeFp(regions.Zip(rects).Select(p => BuildFingerprint(bm, p.Second)).ToArray());
    }

    static Fingerprint MergeFp(IReadOnlyList<Fingerprint> parts)
    {
        if (parts.Count == 1) return parts[0];
        var buf = new byte[parts.Sum(p => p.Bytes.Length)];
        int off = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p.Bytes, 0, buf, off, p.Bytes.Length); off += p.Bytes.Length; }
        return new Fingerprint(parts[0].BlocksX, parts.Sum(p => p.BlocksY), buf);
    }

    static double HitRatio(Fingerprint live, Fingerprint rf, int? tolerance = null)
    {
        int n = Math.Min(live.Bytes.Length, rf.Bytes.Length) / 3;
        if (n == 0) return 0;
        int hit = 0;
        for (int k = 0; k < n; k++)
        {
            int o = k * 3;
            int d = Math.Max(Math.Abs(live.Bytes[o] - rf.Bytes[o]),
                    Math.Max(Math.Abs(live.Bytes[o + 1] - rf.Bytes[o + 1]), Math.Abs(live.Bytes[o + 2] - rf.Bytes[o + 2])));
            if (d <= (tolerance ?? Tolerance)) hit++;
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
