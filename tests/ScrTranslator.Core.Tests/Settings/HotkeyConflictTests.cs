using ScrTranslator.Core.Settings;
namespace ScrTranslator.Core.Tests.Settings;

public class HotkeyConflictTests
{
    [Fact] public void Defaults_NoConflict()
        => Assert.Empty(HotkeyConflict.Find(HotkeyDefaults.Map));

    [Fact] public void SameKey_TwoFunctions_OneConflictGroup()
    {
        var m = new Dictionary<string, HotkeyBinding>
        {
            ["A"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["B"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["C"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8),
        };
        var groups = HotkeyConflict.Find(m);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Contains("A", groups[0]); Assert.Contains("B", groups[0]);
    }

    [Fact] public void DifferentModifiers_SameVk_NoConflict()
    {
        var m = new Dictionary<string, HotkeyBinding>
        {
            ["A"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F9),
            ["B"] = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9),
        };
        Assert.Empty(HotkeyConflict.Find(m));
    }
}
