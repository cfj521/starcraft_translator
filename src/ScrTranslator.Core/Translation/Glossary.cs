using System.IO;
namespace ScrTranslator.Core.Translation;
public sealed class Glossary
{
    private readonly Dictionary<string, string> _map;
    private Glossary(Dictionary<string, string> map) => _map = map;
    public int Count => _map.Count;

    public static Glossary FromEntries(IEnumerable<(string ko, string zh)> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ko, zh) in entries)
        {
            var k = ko.Trim();
            if (k.Length > 0 && !map.ContainsKey(k)) map[k] = zh.Trim();
        }
        return new Glossary(map);
    }

    public static Glossary LoadFromMarkdown(string path)
    {
        if (!File.Exists(path)) return new Glossary(new(StringComparer.Ordinal));
        var entries = new List<(string, string)>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '|') continue;
            var cells = line.Trim('|').Split('|');
            if (cells.Length < 2) continue;
            var ko = cells[0].Trim();
            var zh = cells[1].Trim();
            if (ko.Length == 0 || zh.Length == 0) continue;
            if (ko.Contains("韩文") || ko.StartsWith("---") || ko.StartsWith(":--")) continue; // 表头/分隔
            var zhFirst = zh.Split('/')[0].Trim();
            foreach (var koPart in ko.Split('/'))
            {
                var k = koPart.Trim();
                if (k.Length > 0) entries.Add((k, zhFirst));
            }
        }
        return FromEntries(entries);
    }

    public bool TryGet(string koreanLine, out string chinese)
    {
        chinese = "";
        if (string.IsNullOrWhiteSpace(koreanLine)) return false;
        if (_map.TryGetValue(koreanLine.Trim(), out var v)) { chinese = v; return true; }
        return false; // 保证未命中时 chinese 为 ""（TryGetValue 会把 out 置 null）
    }
}
