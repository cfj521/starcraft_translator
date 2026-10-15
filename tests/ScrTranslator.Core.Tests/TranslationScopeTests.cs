using ScrTranslator.Core;
namespace ScrTranslator.Core.Tests;
public class TranslationScopeTests
{
    [Fact] public void HitColumn_NoColumns_Null()
        => Assert.Null(TranslationScope.HitColumn(null, 0.5));
    [Fact] public void HitColumn_HitsInterval_HalfOpen()
    {
        var cols = new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId), new RoiColumn(0.3, 1.0, ColumnKind.RoomName) };
        Assert.Equal(ColumnKind.PlayerId, TranslationScope.HitColumn(cols, 0.1));
        Assert.Equal(ColumnKind.RoomName, TranslationScope.HitColumn(cols, 0.3)); // [start,end) → 0.3 属第二列
        Assert.Equal(ColumnKind.RoomName, TranslationScope.HitColumn(cols, 0.99));
    }
    [Fact] public void HitColumn_Miss_Null()
    {
        var cols = new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId) };
        Assert.Null(TranslationScope.HitColumn(cols, 0.5));
    }
}
