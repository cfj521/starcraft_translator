using ScrTranslator.Core;

namespace ScrTranslator.Core.Tests.Calibration;

/// <summary>校准窗画布框 → 游戏画面相对矩形的换算（原 BoxToRoi 的纯数学部分，抽到 Core 便于测）。</summary>
public class BoxToRelRectTests
{
    [Fact]
    public void FromCanvasDip_ComputesGameRelativeRect()
    {
        // 画布框 DIP (10, 20, 80, 60)，DPI 2x → 物理像素 (20, 40, 160, 120)
        // 游戏窗口物理像素矩形 left=100, top=200, w=800, h=600
        var rel = RelRect.FromCanvasDip(10, 20, 80, 60, 2.0, 2.0, 100, 200, 800, 600);

        Assert.Equal((20 - 100) / 800.0, rel.X, 6);   // -0.1
        Assert.Equal((40 - 200) / 600.0, rel.Y, 6);   // -0.266...
        Assert.Equal(160 / 800.0, rel.W, 6);          // 0.2
        Assert.Equal(120 / 600.0, rel.H, 6);          // 0.2
    }

    [Fact]
    public void FromCanvasDip_UnitDpi_UsesCanvasDirectly()
    {
        // DPI 1.0：物理 = 画布 DIP 原值
        var rel = RelRect.FromCanvasDip(0, 0, 80, 60, 1.0, 1.0, 0, 0, 800, 600);
        Assert.Equal(0, rel.X, 6);
        Assert.Equal(0, rel.Y, 6);
        Assert.Equal(80 / 800.0, rel.W, 6);   // 0.1
        Assert.Equal(60 / 600.0, rel.H, 6);   // 0.1
    }
}
