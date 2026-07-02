namespace ScrTranslator.Core;

public record RelRect(double X, double Y, double W, double H);
public record Roi(string Name, string Type, RelRect Rect, IReadOnlyList<RoiColumn>? Columns = null);
public record SceneBucket(string Resolution, IReadOnlyList<Roi> Rois);
public record RoiConfig(IReadOnlyDictionary<string, IReadOnlyList<SceneBucket>> Scenes);

public enum RoiType { Chat, RoomList, MapInfo }
public enum ColumnKind { RoomName, MapName, PlayerId, Other }
public record RoiColumn(double RelStart, double RelEnd, ColumnKind Kind);

public static class RoiTypes
{
    public static RoiType Parse(string s) => s switch
    {
        "Chat" => RoiType.Chat,
        "RoomList" => RoiType.RoomList,
        "MapInfo" => RoiType.MapInfo,
        _ => RoiType.Chat, // 未知兜底(调用方可另记日志)
    };
}
public static class ColumnKinds
{
    public static ColumnKind Parse(string s) => s switch
    {
        "RoomName" => ColumnKind.RoomName,
        "MapName" => ColumnKind.MapName,
        "PlayerId" => ColumnKind.PlayerId,
        "Other" => ColumnKind.Other,
        _ => ColumnKind.Other, // 未知兜底=不翻,最安全
    };
}
