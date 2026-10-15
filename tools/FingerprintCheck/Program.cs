using SkiaSharp;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.Tools.FingerprintCheck;

/// <summary>
/// 非 16:9 截图指纹核验：把出厂 1080p 基准区域缩放到截图分辨率，在截图上算实时块均值指纹，
/// 跟出厂指纹逐块比命中（块数保持与参考一致，字节数相同，直接可比）。
///
/// 两套缩放口径：
///  - 按轴等比（sx=W/1920, sy=H/1080）：「拉伸铺满」假设——游戏把 16:9 内容铺满更高窗口，相对坐标守恒。
///  - 宽度等比（sx=sy=W/1920）：现行 FittingRegions/SceneRefGen 公式——16:9 内成立，非 16:9 预期错位。
/// </summary>
internal static class Program
{
    const int Tolerance = 16;
    const double MinHitRatio = 0.90;

    public static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        if (args.Length > 0 && args[0] == "crosstest")
        {
            return CropCrossTest(repoRoot);
        }
        if (args.Length > 0 && args[0] == "crossscene")
        {
            return CrossSceneTest(repoRoot);
        }
        if (args.Length > 0 && args[0] == "ingameanchor")
        {
            return IngamAnchorTest(repoRoot);
        }
        if (args.Length > 0 && args[0] == "region")
        {
            // region <left> <top> <width> <height> <blocksX> <blocksY> <baseW> <baseH>
            int left = int.Parse(args[1]), top = int.Parse(args[2]);
            int rw = int.Parse(args[3]), rh = int.Parse(args[4]);
            int rbx = int.Parse(args[5]), rby = int.Parse(args[6]);
            int baseW = int.Parse(args[7]), baseH = int.Parse(args[8]);
            return RegionCompare(repoRoot, left, top, rw, rh, rbx, rby, baseW, baseH);
        }
        if (args.Length > 0 && args[0] == "racetest")
        {
            // racetest <left> <top> <width> <height> <blocksX> <blocksY>  （区域按 16:9 1920x1080 坐标，三种族互比）
            int left = int.Parse(args[1]), top = int.Parse(args[2]);
            int rw = int.Parse(args[3]), rh = int.Parse(args[4]);
            int rbx = int.Parse(args[5]), rby = int.Parse(args[6]);
            return RaceCompare(repoRoot, left, top, rw, rh, rbx, rby);
        }
        var factoryPath = Path.Combine(repoRoot, "src", "ScrTranslator.App", "scene-refs.json");
        var store = SceneRefStore.Load(factoryPath);
        if (!store.IsUsable)
        {
            Console.Error.WriteLine($"出厂参考加载失败：{store.LoadError}");
            return 1;
        }
        Console.WriteLine($"出厂参考：{store.References.Count} 条，容差 {store.ChannelTolerance}，阈值 {store.MinHitRatio:P0}");
        Console.WriteLine();

        // 自检：1080p 参考原图 scale=1.0，理论 100%（参考就从这些图生成）。不过说明工具有 bug。
        var selfTests = new (string File, string SceneId)[]
        {
            ("room-1.png", "room"),
            ("ingame-1t.png", "ingame_t"),
            ("browsing-1.png", "browsing"),
        };
        Console.WriteLine("========== 自检（1080p 原图，应 100%）==========");
        int selfFail = 0;
        foreach (var (file, sceneId) in selfTests)
        {
            var path = Path.Combine(repoRoot, "resources", "scenes_pictures", file);
            if (!File.Exists(path)) { Console.Error.WriteLine($"缺自检图 {file}"); selfFail++; continue; }
            bool ok = RunCase(path, file, sceneId, store);
            Console.WriteLine();
            if (!ok) selfFail++;
        }
        if (selfFail > 0) { Console.Error.WriteLine("自检未过，工具不可信，先修工具。"); return 3; }
        Console.WriteLine();

        var cases = new (string File, string SceneId)[]
        {
            ("room-1024x768.png", "room"),
            ("room-1440x900.png", "room"),
            ("ingame-1024x768t.png", "ingame_t"),
            ("browsing-2048(1920)x1080.png", "browsing"),
        };
        int fail = 0;
        foreach (var (file, sceneId) in cases)
        {
            var path = Path.Combine(repoRoot, "resources", "screenshots", file);
            if (!File.Exists(path)) { Console.Error.WriteLine($"缺截图 {file}"); fail++; continue; }
            RunCase(path, file, sceneId, store);
            Console.WriteLine();
        }
        return fail == 0 ? 0 : 2;
    }

    static bool RunCase(string path, string file, string sceneId, SceneRefStore store)
    {
        using var bm = Decode(path);
        int W = bm.Width, H = bm.Height;
        var reference = store.Find(sceneId) ?? throw new InvalidOperationException($"找不到参考 {sceneId}");
        if (!reference.TryResolution(1920, 1080, out var baseRes))
            throw new InvalidOperationException($"{sceneId} 无 1920x1080 基准参考");

        Console.WriteLine($"=== {file}  {W}x{H}  (宽高比 {W / (double)H:F3})  对参考 {sceneId} ===");
        Console.WriteLine($"基准区域(1080p): {string.Join(" | ", baseRes.Regions.Select(r => $"{r.Name} ({r.X},{r.Y},{r.Width},{r.Height}) 块{r.BlocksX}x{r.BlocksY}"))}");
        Console.WriteLine();

        // 基准指纹按区域切分（区域顺序拼接）
        int off = 0;
        var refSlices = new List<byte[]>();
        foreach (var r in baseRes.Regions)
        {
            refSlices.Add(baseRes.Fingerprint.AsSpan(off, r.BlocksX * r.BlocksY * 3).ToArray());
            off += r.BlocksX * r.BlocksY * 3;
        }

        double[] PerAxis = new double[baseRes.Regions.Count];
        double[] WidthScale = new double[baseRes.Regions.Count];
        int[] perAxisLandingY = new int[baseRes.Regions.Count];
        int[] widthLandingY = new int[baseRes.Regions.Count];

        double sx = W / 1920.0, sy = H / 1080.0;
        Console.WriteLine($"按轴等比 sx={sx:F4} sy={sy:F4}   |   宽度等比 s={sx:F4}（两轴同）");
        Console.WriteLine();

        for (int i = 0; i < baseRes.Regions.Count; i++)
        {
            var r = baseRes.Regions[i];
            // 按轴：X,Y,W,H 各按自己的轴缩放
            int ax = (int)Math.Round(r.X * sx), ay = (int)Math.Round(r.Y * sy);
            int aw = (int)Math.Round(r.Width * sx), ah = (int)Math.Round(r.Height * sy);
            // 宽度等比：同一 scale 套两轴（现公式）
            int wx = (int)Math.Round(r.X * sx), wy = (int)Math.Round(r.Y * sx);
            int ww = (int)Math.Round(r.Width * sx), wh = (int)Math.Round(r.Height * sx);

            byte[] liveA = Live(bm, ax, ay, aw, ah, r.BlocksX, r.BlocksY);
            byte[] liveW = Live(bm, wx, wy, ww, wh, r.BlocksX, r.BlocksY);
            PerAxis[i] = HitRatio(liveA, refSlices[i]);
            WidthScale[i] = HitRatio(liveW, refSlices[i]);
            perAxisLandingY[i] = ay;
            widthLandingY[i] = wy;

            Console.WriteLine($"{r.Name,-16} 按轴落在 ({ax},{ay},{aw}x{ah})  命中 {PerAxis[i]:P1}   |   宽度等比落在 ({wx},{wy},{ww}x{wh})  命中 {WidthScale[i]:P1}");
            // 该区域沿 Y 轴的真实最佳落点（X 固定在按轴位置）——揭示非 16:9 里装饰板到底在哪
            VerticalScan(bm, r.Name, ax, aw, ah, r.BlocksX, r.BlocksY, refSlices[i]);
        }

        // 逐区域命中率都达标才算通过（拼接后的总命中率会掩盖单区域错位）
        double bestA = PerAxis.Min(), bestW = WidthScale.Min();
        bool okA = bestA >= MinHitRatio, okW = bestW >= MinHitRatio;
        Console.WriteLine($"最低区域命中：按轴 {bestA:P1} {(okA ? "✓ 达标" : "✗ 低于阈值")}   |   宽度等比 {bestW:P1} {(okW ? "✓ 达标" : "✗ 低于阈值")}");
        return okA;
    }

    /// <summary>
    /// 跨分辨率裁剪测试：以 4:3 的 room 区域 (0,0,130,30) 为基准指纹，
    /// 用「16:9 居中、裁两侧」模型把区域映射到 16:9 / 16:10，比命中率。
    /// 三张图都用同一 13×3 块网格（块随区域缩放），内容相同则命中应 ~100%。
    /// </summary>
    static int CropCrossTest(string repoRoot)
    {
        const int BaseW = 1920, BaseH = 1080;
        // 区域：4:3 基准 (0,0)~(130,30)，13×3 块
        const int rx = 0, ry = 0, rw = 130, rh = 30, bx = 13, by = 3;

        (string Name, string Path, int W, int H)[] imgs =
        {
            ("4:3  (1024x768)", "resources/screenshots/room-1024x768.png", 1024, 768),
            ("16:10(1440x900)", "resources/screenshots/room-1440x900.png", 1440, 900),
            ("16:9 (1920x1080)", "resources/scenes_pictures/room-1.png",   1920, 1080),
        };

        // 把 4:3 基准区域映射到 16:9 内容坐标（居中裁剪模型）
        double ar43 = 1024.0 / 768;
        double crop43 = (BaseW - BaseH * ar43) / 2;          // 240
        double contentX0 = crop43 + rx * ((BaseW - 2 * crop43) / 1024.0);   // 240
        double contentX1 = crop43 + (rx + rw) * ((BaseW - 2 * crop43) / 1024.0); // 422.8
        double contentY0 = ry * (BaseH / 768.0);             // 0
        double contentY1 = (ry + rh) * (BaseH / 768.0);      // 42.2
        Console.WriteLine($"基准 4:3 区域 ({rx},{ry},{rw}x{rh}) → 16:9 内容坐标 ({contentX0:F1},{contentY0:F1},{contentX1 - contentX0:F1}x{contentY1 - contentY0:F1})");
        Console.WriteLine($"（16:9 居中裁剪：4:3 裁两侧各 {crop43:F0}px = {crop43 / BaseW:P1}；16:10 裁各 {(BaseW - BaseH * 1.6) / 2:F0}px = {(BaseW - BaseH * 1.6) / 2 / BaseW:P1}）");
        Console.WriteLine();

        // 基准指纹
        using var bmRef = Decode(Path.Combine(repoRoot, imgs[0].Path));
        byte[] refFp = Live(bmRef, rx, ry, rw, rh, bx, by);

        bool allOk = true;
        foreach (var (name, path, W, H) in imgs)
        {
            double ar = (double)W / H;
            double crop = (BaseW - BaseH * ar) / 2;
            double visW = BaseW - 2 * crop;
            // 16:9 内容坐标 → 该分辨率窗口坐标
            int x0 = (int)Math.Round((contentX0 - crop) * (W / visW));
            int y0 = (int)Math.Round(contentY0 * (H / (double)BaseH));
            int w = (int)Math.Round((contentX1 - contentX0) * (W / visW));
            int h = (int)Math.Round((contentY1 - contentY0) * (H / (double)BaseH));
            using var bm = Decode(Path.Combine(repoRoot, path));
            byte[] live = Live(bm, x0, y0, w, h, bx, by);
            double hit = HitRatio(live, refFp);
            bool ok = hit >= MinHitRatio;
            allOk &= ok;
            Console.WriteLine($"{name}  区域 ({x0},{y0},{w}x{h})  命中 {hit:P1}  {(ok ? "✓ 达标" : "✗ 低于阈值")}");
        }
        Console.WriteLine();
        return allOk ? 0 : 2;
    }

    /// <summary>
    /// 跨场景对比：browsing vs room，每档分辨率（16:9 / 16:10 / 4:3）算两场景的实时指纹，
    /// 互相比命中率。命中高=两场景边框太像会混淆；低=区分度够。只比 browsing/room，不涉及 ingame。
    /// 区域用 1080p 基准 frame-left/frame-right 经居中裁剪映射到各分辨率。
    /// </summary>
    static int CrossSceneTest(string repoRoot)
    {
        const int BaseW = 1920, BaseH = 1080;
        // 1080p 基准区域（新 frame-left / frame-right）
        (string Name, int X, int Y, int W, int H, int BX, int BY)[] baseRegions =
        {
            ("frame-left",  240, 0, 183, 42, 13, 3),
            ("frame-right", 1544, 0, 136, 42, 13, 3),
        };
        (string Name, string File, int W, int H)[] resolutions =
        {
            ("16:9 (1920x1080)", "browsing-1.png",     1920, 1080),
            ("16:10(1440x900)",  "browsing-1440x900.png", 1440, 900),
            ("4:3  (1024x768)",  "browsing-1024x768.png", 1024, 768),
        };

        Console.WriteLine($"跨场景对比：browsing vs room（阈值 {MinHitRatio:P0}）");
        Console.WriteLine($"基准区域：{string.Join(" | ", baseRegions.Select(r => $"{r.Name} ({r.X},{r.Y},{r.W}x{r.H})"))}");
        Console.WriteLine();

        bool allOk = true;
        foreach (var (label, file, W, H) in resolutions)
        {
            double ar = (double)W / H;
            double crop = (BaseW - BaseH * ar) / 2;
            double visW = BaseW - 2 * crop;

            // 把基准区域映射到该分辨率窗口坐标
            var mapped = baseRegions.Select(r => (
                Name: r.Name, BX: r.BX, BY: r.BY,
                X: (int)Math.Round((r.X - crop) * (W / visW)),
                Y: (int)Math.Round(r.Y * (H / (double)BaseH)),
                W: (int)Math.Round(r.W * (W / visW)),
                H: (int)Math.Round(r.H * (H / (double)BaseH))
            )).ToList();

            // 分别算 browsing 和 room 的实时指纹（拼接所有区域）
            byte[] browsingFp = ComputeAll(repoRoot, "browsing", mapped, W, H);
            byte[] roomFp = ComputeAll(repoRoot, "room", mapped, W, H);
            double cross = HitRatio(browsingFp, roomFp);
            bool ok = cross < MinHitRatio; // 跨场景应低于阈值
            allOk &= ok;

            string regions = string.Join(" | ", mapped.Select(r => $"{r.Name}({r.X},{r.Y},{r.W}x{r.H})"));
            Console.WriteLine($"{label}  裁 {crop:F0}px/侧  {regions}");
            Console.WriteLine($"  browsing vs room 命中 {cross:P1}  {(ok ? "✓ 区分度够" : "✗ 太相近会混淆")}");
            Console.WriteLine();
        }
        return allOk ? 0 : 2;
    }

    /// <summary>载入某场景某分辨率的截图，把 mapped 区域逐一算指纹并拼接成完整指纹。</summary>
    static byte[] ComputeAll(string repoRoot, string scene, List<(string Name, int BX, int BY, int X, int Y, int W, int H)> mapped, int W, int H)
    {
        string file = $"{scene}-{W}x{H}.png";
        string path = W == 1920 && H == 1080
            ? Path.Combine(repoRoot, "resources", "scenes_pictures", $"{scene}-1.png")
            : Path.Combine(repoRoot, "resources", "screenshots", file);
        using var bm = Decode(path);
        var parts = mapped.Select(r => Live(bm, r.X, r.Y, r.W, r.H, r.BX, r.BY)).ToArray();
        int total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        int off = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, buf, off, p.Length); off += p.Length; }
        return buf;
    }

    /// <summary>
    /// ingame 新锚点跨分辨率验证：锚点定义在 2560×1600（16:10），
    ///   区域1 (500,1095,40,125)  区域2 (1800,1250,260,40)
    /// 经居中裁剪映射到 16:9 内容坐标，再映射到 1920×1080 / 2048×1536，
    /// 算各分辨率实时指纹并互相比命中。命中高=锚点在三套比例下都静态存在。
    /// </summary>
    static int IngamAnchorTest(string repoRoot)
    {
        // 锚点定义在 2560×1600（16:10）
        const int defW = 2560, defH = 1600;
        (string Name, int X, int Y, int W, int H, int BX, int BY)[] anchors =
        {
            ("anchor-1", 500, 1095, 40, 125, 2, 10),
            ("anchor-2", 1800, 1250, 260, 40, 13, 2),
        };

        Console.WriteLine($"ingame 锚点（定义于 {defW}×{defH}），按边缘距离等比换算（W/{defW}、H/{defH}）：");
        Console.WriteLine();

        (string Name, string Path, int W, int H)[] resolutions =
        {
            ("16:9 (1920x1080)",  "resources/scenes_pictures/ingame-1t.png",          1920, 1080),
            ("16:10(2560x1600)",  "resources/screenshots/ingame-2560x1600t.png",     2560, 1600),
            ("4:3  (2048x1536)",  "resources/screenshots/ingame-2048x1536t.png",     2048, 1536),
            ("4:3  (1024x768)",   "resources/screenshots/ingame-1024x768t.png",      1024,  768),
            ("16:10(1440x900)",   "resources/screenshots/ingame-1440x900t.png",      1440,  900),
        };

        // 各分辨率实时指纹（拼接两个锚点）。
        // ingame 裁剪原则：以锚点为基准，按"边缘到锚点距离"等比换算——
        // 锚点坐标直接按 W/defW、H/defH 等比缩放（从原点），不叠加居中裁剪偏移。
        var fps = new List<(string Name, int W, int H, byte[] Fp)>();
        foreach (var (name, path, W, H) in resolutions)
        {
            var parts = new List<byte[]>();
            foreach (var a in anchors)
            {
                int x = (int)Math.Round(a.X * (W / (double)defW));
                int y = (int)Math.Round(a.Y * (H / (double)defH));
                int w = (int)Math.Round(a.W * (W / (double)defW));
                int h = (int)Math.Round(a.H * (H / (double)defH));
                using var bm = Decode(Path.Combine(repoRoot, path));
                parts.Add(Live(bm, x, y, w, h, a.BX, a.BY));
            }
            int total = parts.Sum(p => p.Length);
            var buf = new byte[total];
            int off = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, buf, off, p.Length); off += p.Length; }
            fps.Add((name, W, H, buf));
        }

        // 两两对比
        Console.WriteLine("跨分辨率命中（两两对比）：");
        for (int i = 0; i < fps.Count; i++)
            for (int j = i + 1; j < fps.Count; j++)
            {
                double hit = HitRatio(fps[i].Fp, fps[j].Fp);
                bool ok = hit >= MinHitRatio;
                Console.WriteLine($"  {fps[i].Name}  vs  {fps[j].Name}  命中 {hit:P1}  {(ok ? "✓" : "✗")}");
            }
        bool allOk = true;
        for (int i = 0; i < fps.Count; i++)
            for (int j = i + 1; j < fps.Count; j++)
                allOk &= HitRatio(fps[i].Fp, fps[j].Fp) >= MinHitRatio;
        return allOk ? 0 : 2;
    }

    /// <summary>
    /// 同一字面区域在四档 ingame-t 分辨率下采样互比：
    /// 1024x768 / 1440x900 / 2048x1536 / 1920x1080，区域 (left,top,w,h)，块网格 bx×by。
    /// 命中高=该区域内容跨分辨率静态；命中低=内容随比例变或坐标不通用。
    /// </summary>
    static int RegionCompare(string repoRoot, int left, int top, int rw, int rh, int bx, int by, int baseW, int baseH)
    {
        (string Name, string Path, int W, int H)[] imgs =
        {
            ("1024x768  (4:3)",  "resources/screenshots/ingame-1024x768t.png",   1024, 768),
            ("1440x900 (16:10)", "resources/screenshots/ingame-1440x900t.png",  1440, 900),
            ("2048x1536(4:3)",   "resources/screenshots/ingame-2048x1536t.png", 2048, 1536),
            ("1920x1080(16:9)",  "resources/scenes_pictures/ingame-1t.png",     1920, 1080),
        };
        Console.WriteLine($"区域 ({left},{top},{rw}x{rh}) 块 {bx}x{by}，定义于 {baseW}x{baseH}，等比换算后四档互比：");
        var fps = new List<(string Name, byte[] Fp, int W, int H)>();
        foreach (var (name, path, W, H) in imgs)
        {
            // 从基准分辨率等比换算到当前分辨率
            double sx = W / (double)baseW, sy = H / (double)baseH;
            int x = (int)Math.Round(left * sx);
            int y = (int)Math.Round(top * sy);
            int w = (int)Math.Round(rw * sx);
            int h = (int)Math.Round(rh * sy);
            // 区域可能超出帧，夹进帧内
            x = Math.Clamp(x, 0, W - 1); y = Math.Clamp(y, 0, H - 1);
            w = Math.Min(w, W - x); h = Math.Min(h, H - y);
            using var bm = Decode(Path.Combine(repoRoot, path));
            byte[] fp = Live(bm, x, y, w, h, bx, by);
            fps.Add((name, fp, W, H));
            Console.WriteLine($"  {name}  换算后 ({x},{y},{w}x{h})");
        }
        Console.WriteLine();
        bool allOk = true;
        for (int i = 0; i < fps.Count; i++)
            for (int j = i + 1; j < fps.Count; j++)
            {
                double hit = HitRatio(fps[i].Fp, fps[j].Fp);
                allOk &= hit >= MinHitRatio;
                Console.WriteLine($"  {fps[i].Name}  vs  {fps[j].Name}  命中 {hit:P1}  {(hit >= MinHitRatio ? "✓" : "✗")}");
            }
        return allOk ? 0 : 2;
    }

    /// <summary>
    /// 三种族 ingame 互比：同一区域（16:9 1920x1080 坐标）在人族(t)/虫族(z)/神族(p)的 ingame 图上算指纹互比。
    /// 期望跨种族命中低（HUD 随种族变，指纹有区分度）。
    /// </summary>
    static int RaceCompare(string repoRoot, int left, int top, int rw, int rh, int bx, int by)
    {
        (string Name, string Path)[] races =
        {
            ("人族(t)", "resources/scenes_pictures/ingame-1t.png"),
            ("虫族(z)", "resources/scenes_pictures/ingame-1z.png"),
            ("神族(p)", "resources/scenes_pictures/ingame-1p.png"),
        };
        Console.WriteLine($"区域 ({left},{top},{rw}x{rh}) 块 {bx}x{by}，三种族 ingame 互比（16:9 1920x1080）：");
        var fps = new List<(string Name, byte[] Fp)>();
        foreach (var (name, path) in races)
        {
            using var bm = Decode(Path.Combine(repoRoot, path));
            byte[] fp = Live(bm, left, top, rw, rh, bx, by);
            fps.Add((name, fp));
        }
        Console.WriteLine();
        bool allLow = true;   // 期望跨种族命中都低于阈值（有区分度）
        for (int i = 0; i < fps.Count; i++)
            for (int j = i + 1; j < fps.Count; j++)
            {
                double hit = HitRatio(fps[i].Fp, fps[j].Fp);
                allLow &= hit < MinHitRatio;
                Console.WriteLine($"  {fps[i].Name}  vs  {fps[j].Name}  命中 {hit:P1}  {(hit < MinHitRatio ? "✓(区分)" : "✗(混淆)")}");
            }
        return allLow ? 0 : 2;
    }

    /// <summary>
    /// 垂直扫描：把参考区域（缩放到 w×h）沿 Y 轴从 0 滑到帧底，X 固定在 fixX，
    /// 找出命中率最高的落点——用来定位非 16:9 图里装饰板/边框的真实位置。
    /// </summary>
    static void VerticalScan(SKBitmap bm, string name, int fixX, int w, int h, int blocksX, int blocksY, byte[] reference)
    {
        double best = 0; int bestY = -1;
        for (int y = 0; y + h <= bm.Height; y++)
        {
            byte[] live = Live(bm, fixX, y, w, h, blocksX, blocksY);
            double r = HitRatio(live, reference);
            if (r > best) { best = r; bestY = y; }
        }
        Console.WriteLine($"{name}: 最佳落点 X={fixX} Y={bestY} (相对Y {bestY / (double)bm.Height:F3})  命中 {best:P1}");
    }

    /// <summary>把区域等分为 blocksX×blocksY 块（浮点边界，整块覆盖，不丢边缘），逐块 RGB 均值，RGB24 行优先。</summary>
    static byte[] Live(SKBitmap bm, int x, int y, int w, int h, int blocksX, int blocksY)
    {
        var bytes = new byte[blocksX * blocksY * 3];
        var (ro, go, bo) = ChannelOffsets(bm);
        unsafe
        {
            byte* p = (byte*)bm.GetPixels();
            for (int gy = 0; gy < blocksY; gy++)
            {
                int y0 = y + (int)((double)gy / blocksY * h);
                int y1 = y + (int)((double)(gy + 1) / blocksY * h);
                if (y1 <= y0) { y1 = y0 + 1; }
                y0 = Math.Clamp(y0, 0, bm.Height - 1); y1 = Math.Clamp(y1, y0 + 1, bm.Height);
                for (int gx = 0; gx < blocksX; gx++)
                {
                    int x0 = x + (int)((double)gx / blocksX * w);
                    int x1 = x + (int)((double)(gx + 1) / blocksX * w);
                    if (x1 <= x0) { x1 = x0 + 1; }
                    x0 = Math.Clamp(x0, 0, bm.Width - 1); x1 = Math.Clamp(x1, x0 + 1, bm.Width);
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            byte* q = p + (long)yy * bm.RowBytes + (long)xx * 4;
                            sr += q[ro]; sg += q[go]; sb += q[bo]; n++;
                        }
                    int o = (gy * blocksX + gx) * 3;
                    bytes[o] = (byte)(sr / n); bytes[o + 1] = (byte)(sg / n); bytes[o + 2] = (byte)(sb / n);
                }
            }
        }
        return bytes;
    }

    /// <summary>逐块 RGB 最大通道差 ≤ 容差记命中；块数一致（长度相同），直接比全部块。</summary>
    static double HitRatio(byte[] live, byte[] reference)
    {
        int n = Math.Min(live.Length, reference.Length) / 3;
        if (n == 0) return 0;
        int hit = 0;
        for (int k = 0; k < n; k++)
        {
            int o = k * 3;
            int d = Math.Max(Math.Abs(live[o] - reference[o]),
                    Math.Max(Math.Abs(live[o + 1] - reference[o + 1]), Math.Abs(live[o + 2] - reference[o + 2])));
            if (d <= Tolerance) hit++;
        }
        return (double)hit / n;
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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ScrTranslator.App", "scene-refs.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
