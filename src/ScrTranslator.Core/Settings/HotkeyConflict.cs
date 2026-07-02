using System.Linq;
namespace ScrTranslator.Core.Settings;

public static class HotkeyConflict
{
    public static IReadOnlyList<IReadOnlyList<string>> Find(IReadOnlyDictionary<string, HotkeyBinding> bindings)
        => bindings
            .GroupBy(kv => (kv.Value.Modifiers, kv.Value.Vk))
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<string>)g.Select(kv => kv.Key).ToList())
            .ToList();
}
