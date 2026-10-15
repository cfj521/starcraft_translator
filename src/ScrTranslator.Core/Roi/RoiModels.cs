namespace ScrTranslator.Core;

public record RelRect(double X, double Y, double W, double H);
public record Roi(string Name, RelRect Rect, IReadOnlyList<RoiColumn>? Columns = null);

/// <summary>某场景的 ROI 集合。相对坐标跨分辨率通用（等比缩放），不按分辨率分档。</summary>
public record RoiConfig(IReadOnlyDictionary<string, IReadOnlyList<Roi>> Scenes);

public enum ColumnKind { RoomName, MapName, PlayerId, Other }
public record RoiColumn(double RelStart, double RelEnd, ColumnKind Kind);

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
