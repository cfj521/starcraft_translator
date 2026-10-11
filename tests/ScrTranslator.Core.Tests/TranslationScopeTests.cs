using ScrTranslator.Core;
namespace ScrTranslator.Core.Tests;
public class TranslationScopeTests
{
    private static readonly ContentScopeSettings All = new(true, true, true, true);
    private static readonly ContentScopeSettings Default = new(Chat: true, RoomName: true, MapInfo: true, PlayerId: false);

    [Fact] public void Chat_TakesChatFlag()
    {
        Assert.True(TranslationScope.ShouldTranslate(RoiType.Chat, null, All));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.Chat, null, All with { Chat = false }));
    }
    [Fact] public void MapInfo_TakesMapFlag()
        => Assert.False(TranslationScope.ShouldTranslate(RoiType.MapInfo, null, All with { MapInfo = false }));

    [Fact] public void GameName_TakesRoomNameFlag()
    {
        Assert.True(TranslationScope.ShouldTranslate(RoiType.GameName, null, All));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.GameName, null, All with { RoomName = false }));
    }
    [Fact] public void MapName_TakesMapInfoFlag()
        => Assert.False(TranslationScope.ShouldTranslate(RoiType.MapName, null, All with { MapInfo = false }));

    // 房间名列表已简化为单一区域（不再分列）：无论传什么列，都随"房间名"开关
    [Fact] public void RoomNameList_TakesRoomNameFlag_IgnoresColumn()
    {
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomNameList, ColumnKind.RoomName, Default));
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomNameList, ColumnKind.MapName, Default));
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomNameList, ColumnKind.PlayerId, Default));
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomNameList, null, Default));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomNameList, ColumnKind.RoomName, Default with { RoomName = false }));
    }

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

    // RoomNameList 无列定义 → 整块按 RoomName
    [Fact] public void RoomNameList_NoColumnsSemantics_ControllerPassesRoomName()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomNameList, ColumnKind.RoomName, Default));
}
