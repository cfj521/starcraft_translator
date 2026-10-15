using System.Drawing;
using ScrTranslator.Core;

namespace ScrTranslator.Core.Tests;

public class RelRectTests
{
    [Fact]
    public void ToPixels_ConvertsAndClamps()
    {
        var r = new RelRect(0.25, 0.5, 0.2, 0.1).ToPixels(1920, 1080);
        Assert.Equal(480, r.X);
        Assert.Equal(540, r.Y);
        Assert.Equal(384, r.Width);
        Assert.Equal(108, r.Height);

        // 越界矩形被钳制进画面：x 起点 900，宽最多到右边缘 1000 → 100
        var over = new RelRect(0.9, 0.0, 0.5, 0.5).ToPixels(1000, 1000);
        Assert.Equal(900, over.X);
        Assert.Equal(100, over.Width);
    }
}
