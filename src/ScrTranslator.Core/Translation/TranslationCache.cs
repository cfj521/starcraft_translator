namespace ScrTranslator.Core.Translation;
public sealed class TranslationCache
{
    private readonly int _cap;
    private readonly object _lock = new();
    private readonly LinkedList<(string k, string v)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string k, string v)>> _idx = new(StringComparer.Ordinal);
    public TranslationCache(int capacity = 512) => _cap = Math.Max(1, capacity);
    public int Count { get { lock (_lock) return _idx.Count; } }

    public bool TryGet(string ko, out string zh)
    {
        zh = "";
        if (string.IsNullOrWhiteSpace(ko)) return false;
        var key = ko.Trim();
        lock (_lock)
        {
            if (!_idx.TryGetValue(key, out var node)) return false;
            _lru.Remove(node); _lru.AddFirst(node);
            zh = node.Value.v; return true;
        }
    }

    public void Set(string ko, string zh)
    {
        if (string.IsNullOrWhiteSpace(ko)) return;
        var key = ko.Trim();
        lock (_lock)
        {
            if (_idx.TryGetValue(key, out var existing)) { _lru.Remove(existing); }
            var node = new LinkedListNode<(string, string)>((key, zh));
            _lru.AddFirst(node); _idx[key] = node;
            while (_idx.Count > _cap)
            {
                var last = _lru.Last!; _lru.RemoveLast(); _idx.Remove(last.Value.k);
            }
        }
    }
}
