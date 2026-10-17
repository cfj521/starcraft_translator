namespace ScrTranslator.Core.Scene;

/// <summary>一次指纹判定的结论状态。<see cref="Matched"/> 之外都不应切换 ROI。</summary>
public enum SceneMatchStatus
{
    /// <summary>有候选达到阈值，<see cref="SceneRecognition.Scene"/> 可用。</summary>
    Matched,
    /// <summary>所有候选命中率都低于阈值 → 判"其他界面"（主菜单/加载/街机/单人等，不翻译）。</summary>
    BelowThreshold,
    /// <summary>当前帧分辨率没有对应参考（需补截图重跑 tools/SceneRefGen，或用现场校准）。</summary>
    NoReferenceForResolution,
    /// <summary>scene-refs.json 没加载成功，识别不可用。</summary>
    NoReferences,
}

/// <summary>单个参考条目的打分明细（DEBUG 面板用）。</summary>
public sealed record SceneScore(string RefId, string Label, GameScene Scene, double HitRatio);

/// <summary>一次场景识别的结果。不区分"哪个种族"时看 <see cref="Scene"/>；要看种族看 <see cref="Race"/>。</summary>
public sealed record SceneRecognition(
    SceneMatchStatus Status,
    GameScene Scene,
    string? RefId,
    string? RefLabel,
    GameRace Race,
    double HitRatio,
    double RunnerUpRatio,
    IReadOnlyList<SceneScore> Scores,
    string? Detail,
    /// <summary>命中率最高那条参考的缩放区域（帧像素坐标）——DEBUG 画指纹框用；无参考/未识别时为空。</summary>
    IReadOnlyList<SceneRefRegion> WinnerRegions)
{
    public bool IsMatched => Status == SceneMatchStatus.Matched;
    /// <summary>面板/日志用的一行摘要。</summary>
    public string Summary => Status switch
    {
        SceneMatchStatus.Matched => $"{RefLabel} {HitRatio:P1} (次 {RunnerUpRatio:P1})",
        SceneMatchStatus.BelowThreshold => $"无匹配 最高 {HitRatio:P1} (次 {RunnerUpRatio:P1})",
        SceneMatchStatus.NoReferenceForResolution => $"该分辨率无参考 {Detail}",
        _ => Detail ?? "参考未加载",
    };
}

/// <summary>
/// 图像指纹场景识别器：拿一帧，对**每个候选参考用它自己的区域**算实时指纹并比对，取命中率最高者。
///
/// 设计要点：
///  - 区域是**每个参考自带**的（browsing/room 用顶部边框角，ingame 用底部种族装饰板），
///    因为不存在一块对所有场景都静态的区域——游戏内除装饰板外全是会变的视口/小地图/单位面板。
///  - 只接受**分辨率精确匹配**：块均值网格随分辨率变，借用邻近分辨率参考会整片错位。
///  - 单一阈值 <see cref="SceneRefStore.MinHitRatio"/>：实测同场景 100%、跨场景 ≤25%，
///    余量 65 个百分点，无需再设"领先次优多少"的边际阈值（真出现两个候选同时 ≥90% 才需要加）。
///  - 纯函数、无 IO、无 WPF，可直接单测；迟滞/切换策略留在调用方（TranslationEngine）。
/// </summary>
public sealed class SceneRecognizer
{
    private readonly SceneRefStore _store;

    public SceneRecognizer(SceneRefStore store) => _store = store;

    /// <summary>已加载的参考集合（上层可据此提示"支持哪些分辨率"）。</summary>
    public SceneRefStore Store => _store;

    /// <summary>对整帧做场景判定。<paramref name="frame"/> 通常来自 WGC 捕获缓冲（BGRA，1:1 物理像素）。</summary>
    public SceneRecognition Recognize(in FramePixels frame)
    {
        if (_store.References.Count == 0)
            return new SceneRecognition(SceneMatchStatus.NoReferences, GameScene.Unknown, null, null,
                GameRace.Unknown, 0, 0, Array.Empty<SceneScore>(), _store.LoadError ?? "无参考",
                Array.Empty<SceneRefRegion>());

        // 方案 B：所有参考只存 1080p 一档。把 1080p 基准区域按统一高度因子 H/1080 缩放到当前帧，
        // 块数固定（= 1080p 那套），实时指纹长度恒等于出厂参考，直接比对。
        // browsing/room 的金属边框叠加居中水平裁剪（只裁宽度）；ingame 的种族装饰区贴左边缘、无裁剪。
        string baseKey = SceneRefStore.RatioKey(_store.BaseWidth, _store.BaseWidth * 9 / 16);
        var candidates = new List<(SceneReference Ref, SceneRefResolution Res, IReadOnlyList<SceneRefRegion> Scaled)>();
        foreach (var r in _store.References)
            if (r.Resolutions.TryGetValue(baseKey, out var baseRes))
            {
                // 只有有黑边（ar 偏离最近标准比例超 0.08）才裁；无黑边帧 frame-left 本就内缩安全区，不裁
                bool centerCrop = r.Scene != GameScene.InGame
                    && ResolutionAnalyzer.Analyze(frame.Width, frame.Height).HasBlackBars;
                candidates.Add((r, baseRes, SceneRefCapture.FittingRegions(baseRes.Regions, frame.Width, frame.Height, centerCrop)));
            }
        if (candidates.Count == 0)
        {
            var have = string.Join(",", _store.References.SelectMany(r => r.AvailableResolutions).Distinct().OrderBy(x => x));
            return new SceneRecognition(SceneMatchStatus.NoReferenceForResolution, GameScene.Unknown, null, null,
                GameRace.Unknown, 0, 0, Array.Empty<SceneScore>(), $"{frame.Width}x{frame.Height}（已收录 {have}）",
                Array.Empty<SceneRefRegion>());
        }

        // 区域几何完全相同的候选（如 ingame_t/p/z 共用 race-hud）共用同一份实时指纹，只算一次。
        var liveCache = new Dictionary<string, byte[]>();
        var scores = new List<SceneScore>(candidates.Count);
        foreach (var (reference, res, scaled) in candidates)
        {
            string geoKey = GeometryKey(res);
            if (!liveCache.TryGetValue(geoKey, out var live))
            {
                live = new byte[SceneFingerprint.ByteLength(scaled)];
                SceneFingerprint.Compute(frame, scaled, live);
                liveCache[geoKey] = live;
            }
            double hit = SceneFingerprint.HitRatio(live, res.Fingerprint, _store.ChannelTolerance);
            scores.Add(new SceneScore(reference.Id, reference.Label, reference.Scene, hit));
        }

        var ranked = scores.OrderByDescending(s => s.HitRatio).ToList();
        double best = ranked[0].HitRatio, second = ranked.Count > 1 ? ranked[1].HitRatio : 0;
        // 最高命中那条参考的缩放区域（帧像素坐标，和指纹计算用同一套 centerCrop+FittingRegions）：
        // DEBUG 画指纹框用——框坐标天然和裁剪/缩放一致。
        var winnerRegions = candidates.FirstOrDefault(c => c.Ref.Id == ranked[0].RefId).Scaled
            ?? Array.Empty<SceneRefRegion>();

        if (best < _store.MinHitRatio)
            return new SceneRecognition(SceneMatchStatus.BelowThreshold, GameScene.Other, ranked[0].RefId,
                ranked[0].Label, GameRace.Unknown, best, second, ranked,
                $"阈值 {_store.MinHitRatio:P0}", winnerRegions);

        var winner = ranked[0];
        var race = _store.Find(winner.RefId)?.Race ?? GameRace.Unknown;
        return new SceneRecognition(SceneMatchStatus.Matched, winner.Scene, winner.RefId, winner.Label,
            race, best, second, ranked, null, winnerRegions);
    }

    /// <summary>区域几何指纹（用于共享实时指纹）：只看 rect/blockPx/blocks，不看像素内容。</summary>
    static string GeometryKey(SceneRefResolution res) =>
        string.Join(";", res.Regions.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height},{r.BlockPx},{r.BlocksX}x{r.BlocksY}"));
}
