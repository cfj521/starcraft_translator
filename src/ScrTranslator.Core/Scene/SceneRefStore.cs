using System.Text.Json;

namespace ScrTranslator.Core.Scene;

/// <summary>
/// 单个识别区域的几何：rect 是该分辨率下的**绝对像素**坐标（x,y,w,h，double）。
/// BlockPx 为块均值边长（像素，仅显示用），BlocksX×BlocksY 为指纹网格。
/// 生成器(tools/SceneRefGen)与运行时共用同一套缩放参数——相对值只在 JSON 里（0~1，÷分辨率宽高），
/// 加载时换算回绝对像素，运行时按同一组参数取均值，否则网格错位、指纹全不命中。
/// </summary>
public sealed record SceneRefRegion(string Name, double X, double Y, double Width, double Height, int BlockPx, int BlocksX, int BlocksY)
{
    /// <summary>该区域的指纹字节数（RGB24，行优先）。</summary>
    public int ByteLength => BlocksX * BlocksY * 3;

    /// <summary>把区域矩形夹进帧内，防止参考分辨率与实际帧不符时越界。</summary>
    public (int X, int Y, int Width, int Height) ClampTo(int frameWidth, int frameHeight)
    {
        int x = Math.Clamp((int)Math.Round(X), 0, Math.Max(0, frameWidth - 1));
        int y = Math.Clamp((int)Math.Round(Y), 0, Math.Max(0, frameHeight - 1));
        return (x, y, Math.Min((int)Math.Round(Width), frameWidth - x), Math.Min((int)Math.Round(Height), frameHeight - y));
    }
}

/// <summary>某场景在某分辨率下的一套参考：各区域几何 + 按区域顺序拼接的指纹字节。</summary>
public sealed record SceneRefResolution(
    string Source, int Width, int Height,
    IReadOnlyList<SceneRefRegion> Regions, byte[] Fingerprint)
{
    /// <summary>指纹总块数（= Fingerprint.Length / 3）。</summary>
    public int BlockCount => Fingerprint.Length / 3;
}

/// <summary>
/// 一个指纹参考条目 = 场景 × (游戏内再乘种族)。
/// <see cref="Scene"/> 是归并后的四态场景（ingame_t/p/z 都是 <see cref="GameScene.InGame"/>），
/// 供 ROI 选择用；<see cref="Race"/> 只用于诊断与"按种族选聊天框"这类后续增强。
/// </summary>
public sealed record SceneReference(
    string Id, string Label, GameScene Scene, GameRace Race,
    IReadOnlyDictionary<string, SceneRefResolution> Resolutions)
{
    /// <summary>取该分辨率的参考。必须**精确匹配**：块均值网格随分辨率变，借用别的分辨率参考会整片错位。</summary>
    public bool TryResolution(int width, int height, out SceneRefResolution resolution)
    {
        var key = $"{width}x{height}";
        if (Resolutions.TryGetValue(key, out var found) && found is not null)
        {
            resolution = found;
            return true;
        }
        resolution = null!;
        return false;
    }

    /// <summary>已收录的分辨率键（诊断/提示用户"这个分辨率还没参考"）。</summary>
    public IEnumerable<string> AvailableResolutions => Resolutions.Keys.OrderBy(k => k);
}

/// <summary>
/// 场景识别参考的只读加载器。两层数据，**必须分文件**：
///  - **出厂层** scene-refs.json：跟着 exe 走，由 tools/SceneRefGen 从仓库截图生成，程序永不写回。
///    带 SHA-256 完整性校验，被改坏/截断时整份拒用（宁可识别不了，也不能拿半份指纹误判）。
///  - **覆盖层** scene-overrides.json：放 %APPDATA%，装用户现场采集的参考。
///    分文件的原因：① 升级/重装 exe 不会冲掉用户数据；② 出厂层可校验，混写就没法校验了。
/// 合并规则：同 (场景, 分辨率) 覆盖层胜出；覆盖层里的新场景直接追加。
/// 加载失败一律不抛异常（缺失/截断/字段非法/校验不过），原因放 <see cref="LoadError"/>。
/// </summary>
public sealed class SceneRefStore
{
    /// <summary>支持的 JSON schema 版本；不匹配时拒绝加载（避免拿旧格式硬解出错位指纹）。</summary>
    public const int SupportedVersion = 1;

    public const string DefaultFileName = "scene-refs.json";
    public const string OverridesFileName = "scene-overrides.json";

    public int BaseWidth { get; private init; } = 1920;
    /// <summary>1080p 基准块宽（像素）。实际块宽 = round(BlockSize × 分辨率缩放)。</summary>
    public int BlockSize { get; private init; } = 4;
    /// <summary>单块 RGB 最大通道差容差。</summary>
    public int ChannelTolerance { get; private init; } = 16;
    /// <summary>命中率阈值：最高候选也要低于此值才判"其他界面"。</summary>
    public double MinHitRatio { get; private init; } = 0.90;

    public IReadOnlyList<SceneReference> References { get; private init; } = Array.Empty<SceneReference>();

    /// <summary>加载失败原因；null=成功。非空时 <see cref="References"/> 可能为空或不全。</summary>
    public string? LoadError { get; private init; }

    /// <summary>单条目解析失败的原因（不致命，但要在 DEBUG 面板露出来）。</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>来自覆盖层（用户现场采集）的分辨率条目数。</summary>
    public int OverrideCount { get; private init; }

    public bool IsUsable => LoadError is null && References.Count > 0;

    public SceneReference? Find(string id) => References.FirstOrDefault(r => r.Id == id);

    /// <summary>按四态场景取参考条目（游戏内会返回三条：ingame_t/p/z）。</summary>
    public IEnumerable<SceneReference> ForScene(GameScene scene) => References.Where(r => r.Scene == scene);

    /// <summary>
    /// 加载出厂层 + 可选覆盖层。<paramref name="overridesPath"/> 为 null 或文件不存在时只有出厂数据。
    /// </summary>
    public static SceneRefStore Load(string factoryPath, string? overridesPath = null)
    {
        var warnings = new List<string>();
        try
        {
            var factory = ReadFile(factoryPath, "出厂场景参考", requireIntegrity: true, warnings, out var factoryError);
            if (factoryError is not null)
                return new SceneRefStore { LoadError = factoryError, Warnings = warnings };

            int overrideCount = 0;
            var merged = factory.References;
            if (overridesPath is not null && File.Exists(overridesPath))
            {
                var overrides = ReadFile(overridesPath, "现场采集参考", requireIntegrity: false, warnings, out var overrideError);
                if (overrideError is not null)
                {
                    // 覆盖层坏了不该连累出厂层：记一条告警，继续用出厂数据跑。
                    warnings.Add(overrideError + "（已忽略覆盖层，仅用出厂参考）");
                }
                else
                {
                    (merged, overrideCount) = Merge(factory.References, overrides.References);
                }
            }

            return new SceneRefStore
            {
                BaseWidth = factory.BaseWidth,
                BlockSize = factory.BlockSize,
                ChannelTolerance = factory.ChannelTolerance,
                MinHitRatio = factory.MinHitRatio,
                References = merged,
                OverrideCount = overrideCount,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            return new SceneRefStore { LoadError = $"场景参考加载失败：{ex.Message}" };
        }
    }

    /// <summary>读一份参考文件（出厂层或覆盖层共用同一 schema）。</summary>
    static SceneRefStore ReadFile(string path, string what, bool requireIntegrity, List<string> warnings, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"找不到{what}文件 {Path.GetFileName(path)}";
                return new SceneRefStore();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("version", out var ver) && ver.TryGetInt32(out int v) && v != SupportedVersion)
            {
                error = $"{what} version={v}，本程序只支持 v{SupportedVersion}（请用 tools/SceneRefGen 重新生成）";
                return new SceneRefStore();
            }

            int baseWidth = 1920, blockSize = 4, tol = 16;
            double minHit = 0.90;
            if (root.TryGetProperty("fingerprint", out var fp))
            {
                if (fp.TryGetProperty("baseResolution", out var br) && br.ValueKind == JsonValueKind.String)
                {
                    var parts = (br.GetString() ?? "").Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int bw)) baseWidth = bw;
                }
                blockSize = GetInt(fp, "blockSize", blockSize);
                tol = GetInt(fp, "channelTolerance", tol);
                minHit = GetDouble(fp, "minHitRatio", minHit);
            }

            if (!root.TryGetProperty("scenes", out var scenesEl) || scenesEl.ValueKind != JsonValueKind.Object)
            {
                error = $"{what} 缺少 scenes 对象";
                return new SceneRefStore();
            }

            // 校验输入 = 各条目的"键 + 几何 + 指纹字节"，与 tools/SceneRefGen 共用 SceneRefIntegrity。
            var entries = new List<SceneRefIntegrity.Entry>();
            var refs = new List<SceneReference>();
            foreach (var sceneEl in scenesEl.EnumerateObject())
            {
                var parsed = ParseReference(sceneEl.Name, sceneEl.Value, entries, warnings);
                if (parsed is not null) refs.Add(parsed);
            }
            if (refs.Count == 0) { error = $"{what} 没有任何可用条目"; return new SceneRefStore(); }

            if (requireIntegrity)
            {
                // 出厂层必须带校验和：少了它就没法区分"生成器漏写"和"被人整段替换过"。
                if (!root.TryGetProperty("integrity", out var integrity))
                {
                    error = $"{what} 缺 integrity 字段——该文件由 tools/SceneRefGen 生成，不接受手工编辑";
                    return new SceneRefStore();
                }
                var declared = integrity.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                var actual = SceneRefIntegrity.Hash(entries);
                if (string.IsNullOrEmpty(declared))
                {
                    error = $"{what} 的 integrity.sha256 为空";
                    return new SceneRefStore();
                }
                if (!string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{what} 校验不通过（sha256 声明 {declared[..Math.Min(12, declared.Length)]}… 实际 {actual[..12]}…）" +
                            "——文件被改坏或打包截断，请用 tools/SceneRefGen 重新生成";
                    return new SceneRefStore();
                }
                long bytes = GetDeclaredBytes(integrity);
                if (bytes > 0 && SceneRefIntegrity.DataBytes(entries) != bytes)
                {
                    error = $"{what} 指纹字节数 {SceneRefIntegrity.DataBytes(entries)} ≠ 声明 {bytes}";
                    return new SceneRefStore();
                }
            }

            return new SceneRefStore
            {
                BaseWidth = baseWidth, BlockSize = blockSize, ChannelTolerance = tol, MinHitRatio = minHit,
                References = refs,
            };
        }
        catch (Exception ex)
        {
            error = $"{what} 解析失败：{ex.Message}";
            return new SceneRefStore();
        }
    }

    /// <summary>合并覆盖层：同 (场景,分辨率) 覆盖层胜出；新场景追加。计数=现场采集条目总数（新增与替换都算）。</summary>
    static (IReadOnlyList<SceneReference>, int) Merge(IReadOnlyList<SceneReference> factory, IReadOnlyList<SceneReference> overrides)
    {
        var result = factory.ToList();
        int applied = 0;
        foreach (var o in overrides)
        {
            applied += o.Resolutions.Count; // 面板上"现场覆盖 N 条"= 用户自己采了多少条，替换也算
            int idx = result.FindIndex(r => r.Id == o.Id);
            if (idx < 0) { result.Add(o); continue; }
            var existing = result[idx];
            var combined = new Dictionary<string, SceneRefResolution>(existing.Resolutions);
            foreach (var (key, value) in o.Resolutions)
                combined[key] = value; // 现场采集优先：用户环境可能和出厂截图的客户端语言/主题不同
            result[idx] = existing with { Resolutions = combined };
        }
        return (result, applied);
    }

    static long GetDeclaredBytes(JsonElement integrity)
        => integrity.TryGetProperty("bytes", out var b) && b.TryGetInt64(out long n) ? n : 0;

    static SceneReference? ParseReference(string id, JsonElement el, List<SceneRefIntegrity.Entry> entries, List<string> warnings)
    {
        string label = el.TryGetProperty("label", out var l) ? l.GetString() ?? id : id;
        var race = GameRaces.FromLetter(el.TryGetProperty("race", out var r) ? r.GetString() : null);

        if (!el.TryGetProperty("resolutions", out var resEl) || resEl.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"参考 {id} 无 resolutions，已跳过");
            return null;
        }
        var resolutions = new Dictionary<string, SceneRefResolution>();
        foreach (var rp in resEl.EnumerateObject())
        {
            var res = ParseResolution(id, rp.Name, rp.Value, entries, warnings);
            if (res is not null) resolutions[rp.Name] = res;
        }
        if (resolutions.Count == 0)
        {
            warnings.Add($"参考 {id} 没有任何有效分辨率");
            return null;
        }

        // 语义校验放在解析**之后**：校验和覆盖的是"字节+几何"，不该因为某条 gameScene 写错而整份失效。
        // 否则一条坏场景会把其余好数据一起判死，报出来还是误导性的"校验不通过"。
        var gameScene = GameSceneIds.FromId(el.TryGetProperty("gameScene", out var gs) ? gs.GetString() ?? "" : "");
        if (gameScene == GameScene.Unknown)
        {
            warnings.Add($"参考 {id} 的 gameScene 无法识别，已跳过");
            return null;
        }
        return new SceneReference(id, label, gameScene, race, resolutions);
    }

    static SceneRefResolution? ParseResolution(string id, string key, JsonElement el, List<SceneRefIntegrity.Entry> entries, List<string> warnings)
    {
        var parts = key.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
        {
            warnings.Add($"参考 {id}/{key} 的分辨率键非法");
            return null;
        }
        if (!el.TryGetProperty("regions", out var regionsEl) || regionsEl.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"参考 {id}/{key} 缺 regions");
            return null;
        }

        var regions = new List<SceneRefRegion>();
        var bytes = new List<byte>();
        foreach (var reg in regionsEl.EnumerateArray())
        {
            try
            {
                var name = reg.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                var rect = reg.GetProperty("rect");
                double x = rect[0].GetDouble(), y = rect[1].GetDouble(), w = rect[2].GetDouble(), h = rect[3].GetDouble();
                var blocks = reg.GetProperty("blocks");
                int bx = blocks[0].GetInt32(), by = blocks[1].GetInt32();
                int blockPx = GetInt(reg, "blockPx", 0);
                var data = Convert.FromBase64String(reg.GetProperty("data").GetString() ?? "");
                if (blockPx <= 0 || bx <= 0 || by <= 0 || w <= 0 || h <= 0)
                {
                    warnings.Add($"参考 {id}/{key}/{name} 几何非法 rect={w:R}x{h:R} blockPx={blockPx} blocks={bx}x{by}");
                    continue;
                }
                if (data.Length != bx * by * 3)
                {
                    // 长度不符=指纹与网格不匹配（多半是手改过 data 或版本串了），整条分辨率丢弃，
                    // 否则错位比对会让该场景全线误判。
                    warnings.Add($"参考 {id}/{key}/{name} 指纹长度 {data.Length} ≠ {bx * by * 3}，该分辨率已丢弃");
                    return null;
                }
                // 几何（进 sha256）用 JSON 里的相对值；记录存绝对像素（相对 × 分辨率宽高）。
                regions.Add(new SceneRefRegion(name, x * width, y * height, w * width, h * height, blockPx, bx, by));
                entries.Add(new SceneRefIntegrity.Entry(
                    SceneRefIntegrity.Key(id, key, name),
                    SceneRefIntegrity.Geometry(x, y, w, h, blockPx, bx, by),
                    data));
                bytes.AddRange(data);
            }
            catch (Exception ex)
            {
                warnings.Add($"参考 {id}/{key} 某区域解析失败：{ex.Message}");
                return null;
            }
        }
        if (regions.Count == 0)
        {
            warnings.Add($"参考 {id}/{key} 无有效区域");
            return null;
        }
        string source = el.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
        return new SceneRefResolution(source, width, height, regions, bytes.ToArray());
    }

    static int GetInt(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : fallback;

    static double GetDouble(JsonElement el, string name, double fallback)
        => el.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : fallback;
}
