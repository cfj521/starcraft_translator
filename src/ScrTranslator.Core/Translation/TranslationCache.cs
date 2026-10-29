namespace ScrTranslator.Core.Translation;
public sealed class TranslationCache
{
    private readonly int _cap;
    private readonly object _lock = new();
    private readonly LinkedList<(string k, string v)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string k, string v)>> _idx = new(StringComparer.Ordinal);
    public TranslationCache(int capacity = 512) => _cap = Math.Max(1, capacity);
    public int Count { get { lock (_lock) return _idx.Count; } }

    /// <summary>清空全部条目。切换 provider/模型、改术语表、换引擎后调用：
    /// 缓存键不含 provider/模型，不清就会一直显示旧模型的译文直到进程重启。</summary>
    public void Clear()
    {
        lock (_lock) { _lru.Clear(); _idx.Clear(); }
    }

    /// <summary>编排器用的复合键：场景进键。同一句韩文在不同场景（browsing/room/ingame）本该译得不一样
    /// （场景写在 system prompt 里），只按文本做键会让首个场景的译文污染其余场景。</summary>
    public static string Key(string? scene, string ko) => $"{scene ?? ""}\u0001{ko.Trim()}";

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
