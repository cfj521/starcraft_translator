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

    [Fact] public void RoomList_RoomNameColumn_TakesRoomNameFlag()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.RoomName, Default));
    [Fact] public void RoomList_MapNameColumn_TakesMapInfoFlag()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.MapName, Default));
    [Fact] public void RoomList_PlayerIdColumn_TakesPlayerIdFlag_DefaultOff()
    {
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.PlayerId, Default));
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.PlayerId, Default with { PlayerId = true }));
    }
    [Fact] public void RoomList_OtherOrNullColumn_False()
    {
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.Other, All));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, null, All)); // 有列语义但未命中列
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

    // RoomList 无列定义 → 整块按 RoomName(兼容 Phase 2)
    [Fact] public void RoomList_NoColumnsSemantics_ControllerPassesRoomName()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.RoomName, Default));
}
