using System.Drawing;
using ScrTranslator.Core;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class ScreenMapperTests
{
    [Fact]
    public void MapFrameToScreen_TranslatesAndScales()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(10, 10, 20, 20), new Size(100, 100), new Rectangle(500, 300, 200, 200));
        Assert.Equal(520f, r.X);
        Assert.Equal(320f, r.Y);
        Assert.Equal(40f, r.Width);
        Assert.Equal(40f, r.Height);
    }

    [Fact]
    public void MapFrameToScreen_ScaleOne_PureTranslation()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(5, 7, 10, 10), new Size(800, 600), new Rectangle(100, 50, 800, 600));
        Assert.Equal(105f, r.X);
        Assert.Equal(57f, r.Y);
    }

    [Fact]
    public void MapFrameToScreen_ZeroFrameSize_NoDivideByZero()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(1, 1, 1, 1), new Size(0, 0), new Rectangle(0, 0, 100, 100));
        Assert.Equal(1f, r.X);
        Assert.Equal(1f, r.Y);
    }
}
