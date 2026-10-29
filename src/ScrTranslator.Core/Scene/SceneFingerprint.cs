namespace ScrTranslator.Core.Scene;

/// <summary>
/// 一帧像素的只读视图。Core 不依赖任何图像库：调用方（App 捕获层）把帧缓冲与通道偏移交进来即可。
/// 用 ref struct 是为了直接持有 <see cref="ReadOnlySpan{T}"/>，避免为诊断每帧再拷一份 4K 缓冲。
/// </summary>
public readonly ref struct FramePixels
{
    public ReadOnlySpan<byte> Bytes { get; }
    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }
    public int BytesPerPixel { get; }
    public int ROffset { get; }
    public int GOffset { get; }
    public int BOffset { get; }

    public FramePixels(ReadOnlySpan<byte> bytes, int width, int height, int rowBytes, int bytesPerPixel,
        int rOffset, int gOffset, int bOffset)
    {
        Bytes = bytes;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        BytesPerPixel = bytesPerPixel;
        ROffset = rOffset;
        GOffset = gOffset;
        BOffset = bOffset;
    }
}

/// <summary>
/// 场景指纹：对若干静态区域做 **BlockPx×BlockPx 块的 RGB 均值**，按区域顺序拼成一维字节。
///
/// 为什么不是整帧/降采样灰度：
///  - 整帧被游戏视口污染（实测两张游戏内截图整帧粗网格只有 62/144 命中）；
///  - 只用亮度会丢掉种族信息（神族蓝 / 虫族紫可能同亮度）。
/// 块均值而非逐像素：SC:R 的金属贴图自带噪点，逐像素存原始数据 134 万像素要 2.47MB，
/// 块均值后 114KB 且把噪点平均掉，判别力反而更好（跨场景命中率从 37% 降到 24%）。
///
/// 计算顺序必须与 tools/SceneRefGen 完全一致（区域顺序 → 行优先块 → RGB 三字节），
/// 两边共用 rect/blockPx/blocks 三元组，故几何参数一律从 JSON 读，绝不在这里重新推导。
/// </summary>
public static class SceneFingerprint
{
    /// <summary>按给定区域列表算出的指纹字节数（用于预分配缓冲区）。</summary>
    public static int ByteLength(IReadOnlyList<SceneRefRegion> regions)
    {
        long sum = 0;
        foreach (var r in regions) sum += r.ByteLength;
        if (sum > Array.MaxLength) throw new InvalidOperationException($"指纹过大: {sum}");
        return (int)sum;
    }

    /// <summary>
    /// 把 <paramref name="frame"/> 中各区域的块均值指纹写进 <paramref name="into"/>（行优先，RGB24）。
    /// <paramref name="into"/> 长度须等于 <see cref="ByteLength"/>；调用方可复用缓冲区避免每帧分配。
    /// </summary>
    public static void Compute(in FramePixels frame, IReadOnlyList<SceneRefRegion> regions, byte[] into)
    {
        // 参数不校验时，传错长度的缓冲会在中途 IndexOutOfRange（甚至只写了一半），调用方难以定位；
        // 行跨距/缓冲不足也会在 span 索引处抛难以理解的越界异常。
        if (into is null) throw new ArgumentNullException(nameof(into));
        int need = ByteLength(regions);
        if (into.Length != need)
            throw new ArgumentException($"指纹缓冲区长度 {into.Length} 与区域所需 {need} 不符", nameof(into));
        if (frame.Width <= 0 || frame.Height <= 0 || frame.BytesPerPixel <= 0)
            throw new ArgumentException($"帧尺寸非法: {frame.Width}x{frame.Height} bpp={frame.BytesPerPixel}");
        if (frame.RowBytes < frame.Width * frame.BytesPerPixel)
            throw new ArgumentException($"行跨距 {frame.RowBytes} 小于 宽×bpp={frame.Width * frame.BytesPerPixel}");
        long requiredBytes = (long)frame.RowBytes * (frame.Height - 1) + (long)frame.Width * frame.BytesPerPixel;
        if (frame.Bytes.Length < requiredBytes)
            throw new ArgumentException($"帧缓冲 {frame.Bytes.Length} 字节不足（需 {requiredBytes}）");

        int off = 0;
        foreach (var region in regions)
        {
            var (x0, y0, w, h) = region.ClampTo(frame.Width, frame.Height);
            // 方案 B：块数固定（= 1080p 的 BlocksX/BlocksY），块宽 = 缩放后区域宽 / 块数（浮点）。
            // 在帧上按 1080p 网格采样，指纹长度恒等于出厂参考，跨分辨率可比。
            double bsx = w / (double)region.BlocksX;
            double bsy = h / (double)region.BlocksY;
            for (int gy = 0; gy < region.BlocksY; gy++)
            {
                for (int gx = 0; gx < region.BlocksX; gx++)
                {
                    int px = (int)Math.Round(x0 + gx * bsx);
                    int py = (int)Math.Round(y0 + gy * bsy);
                    int maxX = Math.Min((int)Math.Round(x0 + (gx + 1) * bsx), frame.Width);
                    int maxY = Math.Min((int)Math.Round(y0 + (gy + 1) * bsy), frame.Height);
                    long sr = 0, sg = 0, sb = 0;
                    int n = 0;
                    // 块可能被帧边缘切断：按实际可用像素求均值，全空则写 0（该块必然不命中）。
                    for (int y = py; y < maxY; y++)
                    {
                        int row = y * frame.RowBytes;
                        for (int x = px; x < maxX; x++)
                        {
                            int i = row + x * frame.BytesPerPixel;
                            sr += frame.Bytes[i + frame.ROffset];
                            sg += frame.Bytes[i + frame.GOffset];
                            sb += frame.Bytes[i + frame.BOffset];
                            n++;
                        }
                    }
                    into[off++] = (byte)(n == 0 ? 0 : sr / n);
                    into[off++] = (byte)(n == 0 ? 0 : sg / n);
                    into[off++] = (byte)(n == 0 ? 0 : sb / n);
                }
            }
        }
    }

    /// <summary>命中比例：逐块比 RGB 最大通道差，≤ 容差记一块命中。返回 0~1；长度不等按短的比（多出的算不命中）。</summary>
    public static double HitRatio(ReadOnlySpan<byte> live, ReadOnlySpan<byte> reference, int tolerance)
    {
        int n = Math.Min(live.Length, reference.Length) / 3;
        if (n == 0) return 0;
        int hit = 0;
        for (int k = 0; k < n; k++)
        {
            int o = k * 3;
            int d = Math.Max(Math.Abs(live[o] - reference[o]),
                    Math.Max(Math.Abs(live[o + 1] - reference[o + 1]), Math.Abs(live[o + 2] - reference[o + 2])));
            if (d <= tolerance) hit++;
        }
        // 长度不一致时，缺的部分按不命中计（防止"只存了半个指纹"却拿 100% 命中）
        int total = Math.Max(live.Length, reference.Length) / 3;
        return total == 0 ? 0 : (double)hit / total;
    }
}
