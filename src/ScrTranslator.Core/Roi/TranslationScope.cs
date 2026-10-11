namespace ScrTranslator.Core;

/// <summary>内容范围开关（对应配置"翻译聊天/房间名/地图/玩家ID"）。</summary>
public record ContentScopeSettings(bool Chat, bool RoomName, bool MapInfo, bool PlayerId);

/// <summary>纯判定：某行是否翻译，及其所属列。无 UI 依赖，可单测。</summary>
public static class TranslationScope
{
    /// <summary>
    /// 该行按用户开关该不该翻。**只管开关**：内容层面"有没有韩文"的闸门由调用方先用
    /// <see cref="KoreanText.ContainsHangul"/> 判掉（它还要为跳过的行记一条诊断日志），
    /// 两件事各归一处，别在这里混成一个方法。
    /// </summary>
    public static bool ShouldTranslate(RoiType type, ColumnKind? column, ContentScopeSettings s) => type switch
    {
        RoiType.Chat => s.Chat,
        RoiType.MapInfo => s.MapInfo,
        RoiType.GameName => s.RoomName, // 房间(游戏)名称：随"房间名"开关
        RoiType.MapName => s.MapInfo, // 地图名称：随"地图"开关
        // 房间名列表已简化为单一区域（不再分列），column 恒为 RoomName → 随"房间名"开关
        RoiType.RoomNameList => s.RoomName,
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
