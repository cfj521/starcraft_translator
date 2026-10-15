using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 非常规分辨率的黑边分析：算比例 → 判黑边（差值 &gt; 0.1）→ 最近比例 → 最近分辨率 → 黑边像素 → 内容区相对坐标。
/// </summary>
public class ResolutionAnalyzerTests
{
    [Fact]
    public void ExactStandardRatio_NoBlackBars()
    {
        var a = ResolutionAnalyzer.Analyze(1920, 1080);
        Assert.Equal("16:9", a.NearestRatioName);
        Assert.True(a.Diff < 1e-9);
        Assert.False(a.HasBlackBars);
        Assert.Equal(BlackBarSide.None, a.Side);
        Assert.Equal((1920, 1080), (a.NearestResolutionW, a.NearestResolutionH));
        Assert.Equal((0, 0, 1, 1), (a.ContentRect.X, a.ContentRect.Y, a.ContentRect.W, a.ContentRect.H));
    }

    [Fact]
    public void Near169_NoBlackBars()
    {
        // 1366×768：差值 0.0009，远低于 0.1 → 无黑边。
        var a = ResolutionAnalyzer.Analyze(1366, 768);
        Assert.Equal("16:9", a.NearestRatioName);
        Assert.Equal(0.0009, a.Diff, 4);
        Assert.False(a.HasBlackBars);
        Assert.Equal(BlackBarSide.None, a.Side);
        Assert.Equal((1366, 768), (a.NearestResolutionW, a.NearestResolutionH));
    }

    [Fact]
    public void FiveByThree_Nearest1610_NoBlackBars()
    {
        // 1280×768 = 5:3，离 16:10 差 0.0667（< 0.1）→ 无黑边，最近档 16:10。
        var a = ResolutionAnalyzer.Analyze(1280, 768);
        Assert.Equal("16:10", a.NearestRatioName);
        Assert.Equal(0.0667, a.Diff, 3);
        Assert.False(a.HasBlackBars);
        Assert.Equal(BlackBarSide.None, a.Side);
        Assert.Equal((1280, 768), (a.NearestResolutionW, a.NearestResolutionH));
    }

    [Fact]
    public void FiveByFour_TopBottomBars()
    {
        // 1280×1024 = 5:4，离 4:3 差 0.0833 —— 超过 0.08 阈值 → 上下黑边。
        // 内容区按 4:3 fit 到宽度 1280 → 1280×960，上下各 32px。
        var a = ResolutionAnalyzer.Analyze(1280, 1024);
        Assert.Equal("4:3", a.NearestRatioName);
        Assert.InRange(a.Diff, 0.0833, 0.0834);
        Assert.True(a.HasBlackBars);
        Assert.Equal(BlackBarSide.TopBottom, a.Side);
        Assert.Equal((1280, 960), (a.NearestResolutionW, a.NearestResolutionH));
        Assert.Equal(32, a.BarPixels, 6);
        Assert.Equal(0, a.ContentRect.X, 6);
        Assert.Equal(32 / 1024d, a.ContentRect.Y, 6);
        Assert.Equal(1, a.ContentRect.W, 6);
        Assert.Equal(960 / 1024d, a.ContentRect.H, 6);
    }

    [Fact]
    public void WiderThan169_LeftRightBars()
    {
        // 2048×1080 = 1.8963，离 16:9 差 0.1185（> 0.1）→ 左右黑边。
        // 内容区按 16:9 fit 到高度 1080 → 1920×1080，左右各 64px。
        var a = ResolutionAnalyzer.Analyze(2048, 1080);
        Assert.Equal("16:9", a.NearestRatioName);
        Assert.InRange(a.Diff, 0.1185, 0.1186);
        Assert.True(a.HasBlackBars);
        Assert.Equal(BlackBarSide.LeftRight, a.Side);
        Assert.Equal((1920, 1080), (a.NearestResolutionW, a.NearestResolutionH));
        Assert.Equal(64, a.BarPixels, 6);
        Assert.Equal(64 / 2048d, a.ContentRect.X, 6);
        Assert.Equal(0, a.ContentRect.Y, 6);
        Assert.Equal(1920 / 2048d, a.ContentRect.W, 6);
        Assert.Equal(1, a.ContentRect.H, 6);
    }

    [Fact]
    public void TallerThan43_TopBottomBars()
    {
        // 构造一个比 4:3 更高的窗口：1280×1200 = 1.0667，离 4:3 差 0.2667（> 0.1）→ 上下黑边。
        // 内容区按 4:3 fit 到宽度 1280 → 1280×960，上下各 120px。
        var a = ResolutionAnalyzer.Analyze(1280, 1200);
        Assert.Equal("4:3", a.NearestRatioName);
        Assert.True(a.HasBlackBars);
        Assert.Equal(BlackBarSide.TopBottom, a.Side);
        Assert.Equal((1280, 960), (a.NearestResolutionW, a.NearestResolutionH));
        Assert.Equal(120, a.BarPixels, 6);
        Assert.Equal(0, a.ContentRect.X, 6);
        Assert.Equal(120 / 1200d, a.ContentRect.Y, 6);
        Assert.Equal(1, a.ContentRect.W, 6);
        Assert.Equal(960 / 1200d, a.ContentRect.H, 6);
    }

    [Theory]
    [InlineData(1280, 1024)]
    [InlineData(2048, 1080)]
    [InlineData(1280, 768)]
    [InlineData(1600, 1024)]
    [InlineData(1366, 768)]
    [InlineData(1360, 768)]
    [InlineData(1176, 664)]
    public void Print_AllResolutions(int w, int h)
    {
        var a = ResolutionAnalyzer.Analyze(w, h);
        string bars = a.HasBlackBars
            ? $"{a.Side} 每侧{a.BarPixels}px 内容区[{a.ContentRect.X:F6},{a.ContentRect.Y:F6},{a.ContentRect.W:F6},{a.ContentRect.H:F6}]"
            : "无";
        Console.WriteLine($"[{w}x{h}] 比例={a.Ratio:F6} 最近档={a.NearestRatioName} 差值={a.Diff:F6} 黑边={bars} 最近分辨率={a.NearestResolution}");
    }

    [Fact]
    public void NonStandardWindow_ListMatchingPicksFittingResolution()
    {
        // 窗口 1900×1000：比例 1.9，最近档 16:9，差值 0.1222（> 0.08）→ 有黑边。
        // 纯 fit 目标 = (round(1000*16/9), 1000) = 1778×1000，非标准分辨率。
        // 列表匹配：16:9 档里能装进 1900×1000 的只有 1280×720 / 1600×900（1920×1080 高 1080 装不下）。
        // 离 1778×1000 最近的是 1600×900 → 内容区 = 1600×900。
        var a = ResolutionAnalyzer.Analyze(1900, 1000);
        Assert.Equal("16:9", a.NearestRatioName);
        Assert.True(a.HasBlackBars);
        Assert.Equal((1600, 900), (a.NearestResolutionW, a.NearestResolutionH));
        Assert.Equal(BlackBarSide.LeftRight, a.Side);
        // 居中：offX=(1900-1600)/2=150, offY=(1000-900)/2=50。
        Assert.Equal(150 / 1900d, a.ContentRect.X, 6);
        Assert.Equal(50 / 1000d, a.ContentRect.Y, 6);
        Assert.Equal(1600 / 1900d, a.ContentRect.W, 6);
        Assert.Equal(900 / 1000d, a.ContentRect.H, 6);
    }

    [Fact]
    public void TwentyFiveBySixteen_Nearest1610_NoBlackBars()
    {
        // 1600×1024 = 25:16，离 16:10 差 0.0375（< 0.1）→ 无黑边，最近档 16:10。
        var a = ResolutionAnalyzer.Analyze(1600, 1024);
        Assert.Equal("16:10", a.NearestRatioName);
        Assert.Equal(0.0375, a.Diff, 6);
        Assert.False(a.HasBlackBars);
        Assert.Equal(BlackBarSide.None, a.Side);
        Assert.Equal((1600, 1024), (a.NearestResolutionW, a.NearestResolutionH));
    }
}
