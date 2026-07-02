namespace ScrTranslator.Core;

/// <summary>内容范围开关（对应配置"翻译聊天/房间名/地图/玩家ID"）。</summary>
public record ContentScopeSettings(bool Chat, bool RoomName, bool MapInfo, bool PlayerId);

/// <summary>纯判定：某行是否翻译，及其所属列。无 UI 依赖，可单测。</summary>
public static class TranslationScope
{
    public static bool ShouldTranslate(RoiType type, ColumnKind? column, ContentScopeSettings s) => type switch
    {
        RoiType.Chat => s.Chat,
        RoiType.MapInfo => s.MapInfo,
        RoiType.RoomList => column switch
        {
            ColumnKind.RoomName => s.RoomName,
            ColumnKind.MapName => s.MapInfo,
            ColumnKind.PlayerId => s.PlayerId,
            _ => false, // Other / null(未命中列) → 不翻
        },
        _ => false,
    };

    /// <summary>relX = 行在 ROI 内的水平相对位置(0..1)；返回命中列语义,无列/未命中→null。区间取 [start,end)。</summary>
    public static ColumnKind? HitColumn(IReadOnlyList<RoiColumn>? columns, double relX)
    {
        if (columns is null) return null;
        foreach (var c in columns)
            if (relX >= c.RelStart && relX < c.RelEnd) return c.Kind;
        return null;
    }
}
