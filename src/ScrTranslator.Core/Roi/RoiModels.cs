namespace ScrTranslator.Core;

public record RelRect(double X, double Y, double W, double H);
public record Roi(string Name, string Type, RelRect Rect);
public record SceneBucket(string Resolution, IReadOnlyList<Roi> Rois);
public record RoiConfig(IReadOnlyDictionary<string, IReadOnlyList<SceneBucket>> Scenes);
