namespace ScrTranslator.Core.Scene;

/// <summary>黑边方位：None=无黑边，LeftRight=左右黑边，TopBottom=上下黑边。</summary>
public enum BlackBarSide
{
    None,
    LeftRight,
    TopBottom,
}

/// <summary>
/// 非常规分辨率分析结果。
/// <list type="bullet">
/// <item><see cref="Ratio"/>：整窗（WGC 抓到含黑边）的宽高比。</item>
/// <item><see cref="HasBlackBars"/>：比例差值是否超过阈值（&gt;0.08）。</item>
/// <item><see cref="NearestRatioName"/> / <see cref="NearestRatio"/> / <see cref="Diff"/>：最近标准比例及其差值。</item>
/// <item><see cref="NearestResolutionW"/> / <see cref="NearestResolutionH"/>：匹配到的标准分辨率（无黑边=整窗；有黑边=列表里最近的标准分辨率）。</item>
/// <item><see cref="Side"/> / <see cref="BarPixels"/>：黑边方位与单侧像素数。</item>
/// <item><see cref="ContentRect"/>：内容区相对坐标（无黑边=[0,0,1,1]）。</item>
/// </list>
/// </summary>
public sealed record ResolutionAnalysis(
    double Ratio,
    bool HasBlackBars,
    string NearestRatioName,
    double NearestRatio,
    double Diff,
    int NearestResolutionW,
    int NearestResolutionH,
    BlackBarSide Side,
    double BarPixels,
    RelRect ContentRect)
{
    public string NearestResolution => $"{NearestResolutionW}x{NearestResolutionH}";
}

/// <summary>
/// 非常规分辨率的黑边分析（纯计算，不读盘、不依赖场景/预设）。
/// 流程：
/// <list type="number">
/// <item>算比例 <c>W/H</c>，与三档标准比例比差值；差值 &gt; <see cref="BlackBarThreshold"/> → 有黑边。</item>
/// <item>无黑边 → 内容区=整窗，不需要匹配分辨率。</item>
/// <item>有黑边 → 先定最近比例，再据此算出 fit 目标尺寸，然后在内建标准分辨率列表里匹配最接近的那一档作为内容区。</item>
/// <item>算黑边像素 + 内容区相对坐标。</item>
/// </list>
/// </summary>
public static class ResolutionAnalyzer
{
    /// <summary>比例差值超过该阈值即判为有黑边。</summary>
    public const double BlackBarThreshold = 0.08;

    /// <summary>三档标准比例（与 layout-presets.json 的桶键一致）。</summary>
    public static readonly (string Name, double Ratio)[] StandardRatios =
    {
        ("16:9", 16.0 / 9.0),
        ("16:10", 16.0 / 10.0),
        ("4:3", 4.0 / 3.0),
    };

    /// <summary>
    /// 内建标准分辨率完整列表，用于「匹配分辨率」。每项 = (桶键, 宽, 高)。
    /// 覆盖三档常见整数分辨率；匹配时只在「最近比例」那档里挑。
    /// </summary>
    public static readonly (string Tier, int W, int H)[] StandardResolutions =
    {
        // 4:3
        ("4:3", 800, 600),
        ("4:3", 1024, 768),
        ("4:3", 1152, 864),
        ("4:3", 1280, 960),
        ("4:3", 1600, 1200),
        // 16:10
        ("16:10", 1280, 800),
        ("16:10", 1440, 900),
        ("16:10", 1600, 1000),
        ("16:10", 1920, 1200),
        // 16:9
        ("16:9", 1280, 720),
        ("16:9", 1600, 900),
        ("16:9", 1920, 1080),
        ("16:9", 2560, 1440),
    };

    public static ResolutionAnalysis Analyze(int width, int height)
    {
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "height 必须为正");
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width 必须为正");
        double ratio = (double)width / height;

        // 第 1 步：找最近标准比例。
        string name = "";
        double nearest = 0.0;
        double diff = double.MaxValue;
        foreach (var (n, r) in StandardRatios)
        {
            double d = Math.Abs(r - ratio);
            if (d < diff) { diff = d; nearest = r; name = n; }
        }

        // 无黑边：内容区 = 整窗，不需要匹配分辨率。
        if (diff <= BlackBarThreshold)
            return new ResolutionAnalysis(ratio, false, name, nearest, diff,
                width, height, BlackBarSide.None, 0, new RelRect(0, 0, 1, 1));

        // 有黑边：先算 fit 目标（按最近比例 fit 进整窗），再在标准分辨率列表里匹配最接近的那一档。
        int targetW = ratio > nearest
            ? (int)Math.Round(height * nearest)
            : width;
        int targetH = ratio > nearest
            ? height
            : (int)Math.Round(width / nearest);

        var (mw, mh) = MatchResolution(name, targetW, targetH, width, height);

        // 内容区 = 匹配到的标准分辨率，居中放在整窗里。
        // 整窗比标准比例宽 → 左右黑边；整窗比标准比例高 → 上下黑边。
        double offX = (width - mw) / 2.0;
        double offY = (height - mh) / 2.0;
        BlackBarSide side = ratio > nearest ? BlackBarSide.LeftRight : BlackBarSide.TopBottom;
        double bar = Math.Max(offX, offY);

        var rect = new RelRect(offX / width, offY / height, mw / (double)width, mh / (double)height);
        return new ResolutionAnalysis(ratio, true, name, nearest, diff, mw, mh, side, bar, rect);
    }

    /// <summary>
    /// 在「标准分辨率列表」中，从 <paramref name="tier"/> 这一档里，
    /// 找能装进整窗（w ≤ maxW 且 h ≤ maxH）、且与 (targetW, targetH) 欧氏距离最近的标准分辨率。
    /// </summary>
    public static (int W, int H) MatchResolution(string tier, int targetW, int targetH, int maxW, int maxH)
    {
        int bestW = 0, bestH = 0;
        double bestDist = double.MaxValue;
        int tierW = 0, tierH = 0; // 该档最小的标准分辨率（回落用）
        foreach (var (t, w, h) in StandardResolutions)
        {
            if (t != tier) continue;
            if (tierW == 0 || (long)w * h < (long)tierW * tierH) { tierW = w; tierH = h; }
            if (w > maxW || h > maxH) continue; // 必须能装进整窗
            double dx = w - targetW, dy = h - targetH;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; bestW = w; bestH = h; }
        }
        if (bestW != 0) return (bestW, bestH);

        // 整窗比该档最小的标准分辨率还小（小窗口/异常比例）：不再抛异常（调用方据此整拍失败），
        // 按该档比例缩到能装进整窗的尺寸，至少 1px。
        if (tierW == 0 || maxW <= 0 || maxH <= 0) return (Math.Max(1, Math.Min(targetW, Math.Max(1, maxW))), Math.Max(1, Math.Min(targetH, Math.Max(1, maxH))));
        double scale = Math.Min((double)maxW / tierW, (double)maxH / tierH);
        int fw = Math.Max(1, (int)Math.Round(tierW * scale));
        int fh = Math.Max(1, (int)Math.Round(tierH * scale));
        return (Math.Min(fw, maxW), Math.Min(fh, maxH));
    }
}
