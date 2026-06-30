using ScrTranslator.Core.Interop;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class WindowStylesTests
{
    [Fact]
    public void ApplyClickThrough_SetsTransparentAndLayeredBits()
    {
        int result = WindowStyles.ApplyClickThrough(0);
        Assert.Equal(
            WindowStyles.WS_EX_TRANSPARENT | WindowStyles.WS_EX_LAYERED,
            result);
    }

    [Fact]
    public void ApplyClickThrough_PreservesExistingBits()
    {
        const int existing = 0x00000008; // WS_EX_TOPMOST
        int result = WindowStyles.ApplyClickThrough(existing);

        Assert.Equal(existing, result & existing);
        Assert.True((result & WindowStyles.WS_EX_TRANSPARENT) != 0);
        Assert.True((result & WindowStyles.WS_EX_LAYERED) != 0);
    }

    [Fact]
    public void ApplyClickThrough_IsIdempotent()
    {
        int once = WindowStyles.ApplyClickThrough(0x40);
        int twice = WindowStyles.ApplyClickThrough(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void RemoveClickThrough_ClearsBitsButKeepsOthers()
    {
        const int other = 0x00000008; // WS_EX_TOPMOST
        int applied = WindowStyles.ApplyClickThrough(other);
        int removed = WindowStyles.RemoveClickThrough(applied);
        Assert.Equal(other, removed);
    }
}
