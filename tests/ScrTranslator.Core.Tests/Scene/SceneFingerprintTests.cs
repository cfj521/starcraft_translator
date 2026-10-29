using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>测试用：手工构造 BGRA 帧缓冲与区域，不依赖 SkiaSharp（Core 也不依赖）。</summary>
internal static class SceneTestFrames
{
    public const int Bgra = 4;

    /// <summary>造一张纯色 BGRA 帧（A=255）。</summary>
    public static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * Bgra];
        for (int i = 0; i < buf.Length; i += Bgra) { buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255; }
        return buf;
    }

    /// <summary>在帧缓冲上涂一个矩形（用于让不同块取到不同均值）。</summary>
    public static void Paint(byte[] buf, int frameW, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * frameW + x) * Bgra;
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
            }
    }

    /// <summary>把缓冲包成 FramePixels（BGRA → R 偏移 2、G 1、B 0）。</summary>
    public static FramePixels View(byte[] buf, int w, int h) =>
        new(buf, w, h, w * Bgra, Bgra, 2, 1, 0);
}

public class SceneFingerprintTests
{
    static SceneRefRegion Region(int x = 0, int y = 0, int w = 8, int h = 8, int blockPx = 4, string name = "r")
        => new(name, x, y, w, h, blockPx, w / blockPx, h / blockPx);

    [Fact]
    public void ByteLength_SumsAllRegions()
    {
        var regions = new[] { Region(w: 8, h: 8), Region(x: 10, w: 12, h: 8) };
        // 8x8/块4 → 2x2 块 =4 块；12x8/块4 → 3x2=6 块；共 10 块 ×3 字节
        Assert.Equal(30, SceneFingerprint.ByteLength(regions));
    }

    [Fact]
    public void Compute_TakesPerBlockAverageInRowMajorOrder()
    {
        var buf = SceneTestFrames.Solid(16, 16, 0, 0, 0);
        SceneTestFrames.Paint(buf, 16, 0, 0, 8, 8, 10, 11, 12);     // 块(0,0)
        SceneTestFrames.Paint(buf, 16, 8, 0, 8, 8, 100, 101, 102);  // 块(1,0)
        SceneTestFrames.Paint(buf, 16, 0, 8, 8, 8, 200, 201, 202);  // 块(0,1)
        SceneTestFrames.Paint(buf, 16, 8, 8, 8, 8, 50, 51, 52);     // 块(1,1)
        var regions = new[] { Region(w: 16, h: 16, blockPx: 8) };   // 16/8 → 2x2 网格
        var fp = new byte[SceneFingerprint.ByteLength(regions)];

        SceneFingerprint.Compute(SceneTestFrames.View(buf, 16, 16), regions, fp);

        Assert.Equal(new byte[] { 10, 11, 12, 100, 101, 102, 200, 201, 202, 50, 51, 52 }, fp);
    }

    [Fact]
    public void Compute_ConcatenatesRegionsInGivenOrder()
    {
        var buf = SceneTestFrames.Solid(16, 8, 7, 7, 7);
        SceneTestFrames.Paint(buf, 16, 8, 0, 8, 8, 90, 90, 90);
        var regions = new[] { Region(x: 0, w: 8, h: 8, blockPx: 8), Region(x: 8, w: 8, h: 8, blockPx: 8) };
        var fp = new byte[SceneFingerprint.ByteLength(regions)];

        SceneFingerprint.Compute(SceneTestFrames.View(buf, 16, 8), regions, fp);

        // 每区域 1x1 块 = 3 字节；第二块从下标 3 开始
        Assert.Equal(7, fp[0]);
        Assert.Equal(90, fp[3]);
    }

    [Fact]
    public void Compute_RegionPartlyOutsideFrame_DoesNotThrow()
    {
        // 参考区域比帧还大（兜底路径）：原点夹进帧内，但**宽度/高度保持参考原值**，
        // 否则块宽 = 区域宽/BlocksX 会变、采样网格与出厂参考错位（P3-47）。
        // 完全落在帧外的块按 n==0 写 0（该块必然不命中），绝不越界读。
        var buf = SceneTestFrames.Solid(8, 8, 40, 40, 40);
        var regions = new[] { Region(x: 4, y: 4, w: 8, h: 8, blockPx: 4) }; // 8x8/块4 → 2x2 块
        var fp = new byte[SceneFingerprint.ByteLength(regions)];
        SceneFingerprint.Compute(SceneTestFrames.View(buf, 8, 8), regions, fp);
        // 只有帧内的块 (0,0) 拿到均值 40，其余三块完全在帧外 → 0
        Assert.Equal(new byte[] { 40, 40, 40, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, fp);
    }

    [Fact]
    public void ClampTo_KeepsWidthAndHeight_SoBlockGridStaysAligned()
    {
        // 区域贴右边：原点夹进帧内，宽度保持 16 → 块宽 16/2=8 不变（旧实现会缩成 4，块宽变 2 与参考错位）。
        var r = Region(x: 12, y: 0, w: 16, h: 8, blockPx: 8);
        var (x, y, w, h) = r.ClampTo(16, 8);
        Assert.Equal(12, x);
        Assert.Equal(0, y);
        Assert.Equal(16, w);
        Assert.Equal(8, h);
    }

    [Fact]
    public void HitRatio_Identical_IsOne()
        => Assert.Equal(1.0, SceneFingerprint.HitRatio(new byte[] { 5, 5, 5, 6, 6, 6 }, new byte[] { 5, 5, 5, 6, 6, 6 }, 16));

    [Theory]
    [InlineData(16, 1.0)]  // 差正好等于容差 → 命中（闭区间）
    [InlineData(17, 0.0)]  // 差超容差 1 → 不命中
    public void HitRatio_ToleranceBoundary(int delta, double expected)
        => Assert.Equal(expected, SceneFingerprint.HitRatio(new byte[] { 100, 100, 100 }, new byte[] { (byte)(100 - delta), 100, 100 }, 16));

    [Fact]
    public void HitRatio_UsesWorstChannel_NotLuminance()
    {
        // R/G 完全相同、B 超差：必须判不命中（只看亮度会漏掉这种"同亮度不同色"，种族区分就靠颜色）
        Assert.Equal(0.0, SceneFingerprint.HitRatio(new byte[] { 10, 10, 10 }, new byte[] { 10, 10, 200 }, 16));
    }

    [Fact]
    public void HitRatio_LengthMismatch_PenalizesMissingBlocks()
    {
        // 实时 2 块全中、参考只有 1 块：总块数按长的算 → 0.5，不给 100%
        var live = new byte[] { 5, 5, 5, 5, 5, 5 };
        var reference = new byte[] { 5, 5, 5 };
        Assert.Equal(0.5, SceneFingerprint.HitRatio(live, reference, 16));
    }

    [Fact]
    public void HitRatio_Empty_IsZero()
        => Assert.Equal(0.0, SceneFingerprint.HitRatio(Array.Empty<byte>(), Array.Empty<byte>(), 16));

    [Fact]
    public void Compute_WrongBufferLength_ThrowsArgument()
    {
        var buf = SceneTestFrames.Solid(8, 8, 40, 40, 40);
        var regions = new[] { Region(x: 0, y: 0, w: 8, h: 8, blockPx: 4) }; // 2x2 块 = 12 字节
        var tooSmall = new byte[11];
        Assert.Throws<ArgumentException>(() => SceneFingerprint.Compute(SceneTestFrames.View(buf, 8, 8), regions, tooSmall));
    }

    [Fact]
    public void Compute_ShortRowStride_ThrowsArgument()
    {
        var buf = SceneTestFrames.Solid(8, 8, 40, 40, 40);
        var regions = new[] { Region(w: 8, h: 8, blockPx: 4) };
        var fp = new byte[SceneFingerprint.ByteLength(regions)];
        var bad = new FramePixels(buf, 8, 8, rowBytes: 8, 4, 2, 1, 0); // 行跨距只够 2 像素
        // FramePixels 是 ref struct，不能进 lambda 捕获，只能就地 try/catch 断言。
        bool threw = false;
        try { SceneFingerprint.Compute(bad, regions, fp); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw, "短行跨距应抛 ArgumentException");
    }

    [Fact]
    public void Compute_TruncatedBuffer_ThrowsArgument()
    {
        var full = SceneTestFrames.Solid(8, 8, 40, 40, 40);
        var buf = full.Take(full.Length - 8).ToArray();
        var regions = new[] { Region(w: 8, h: 8, blockPx: 4) };
        var fp = new byte[SceneFingerprint.ByteLength(regions)];
        Assert.Throws<ArgumentException>(() => SceneFingerprint.Compute(SceneTestFrames.View(buf, 8, 8), regions, fp));
    }
}
