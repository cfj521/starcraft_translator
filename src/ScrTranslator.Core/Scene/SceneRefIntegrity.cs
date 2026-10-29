using System.Security.Cryptography;
using System.Text;

namespace ScrTranslator.Core.Scene;

/// <summary>
/// scene-refs.json 的完整性校验。哈希覆盖**几何参数 + 指纹字节**：
/// 只哈希数据的话，手改一个 rect 坐标仍能通过校验，却会让指纹与画面悄悄错位——那正是最难查的故障。
/// 生成器(tools/SceneRefGen)与加载器(SceneRefStore)都调用这里，算法不可能两边写岔。
/// </summary>
public static class SceneRefIntegrity
{
    public const string Algorithm = "sha256";

    /// <summary>条目在哈希序列里的唯一键。</summary>
    public static string Key(string sceneId, string resolutionKey, string regionName)
        => $"{sceneId}/{resolutionKey}/{regionName}";

    /// <summary>参与哈希的几何描述。改任何坐标/块参数都会改变它。
    /// 区域坐标是相对 1080p 基准的 0~1 值，用 <c>R</c>（往返）格式，保证生成端与加载端哈希一致。</summary>
    public static string Geometry(double x, double y, double width, double height, int blockPx, int blocksX, int blocksY)
        => $"{x:R},{y:R},{width:R},{height:R},{blockPx},{blocksX}x{blocksY}";

    /// <summary>一个待哈希条目：键 + 几何串 + 指纹字节。</summary>
    public readonly record struct Entry(string Key, string Geometry, byte[] Data);

    /// <summary>按 (Key, Geometry, 指纹字节) 全序排序后，依次写入 "key|geometry|" 与其指纹字节，取 SHA-256（小写 hex）。
    /// 排序键必须覆盖**全部**参与哈希的内容：旧实现只按 Key 排、Key 相同的条目保持 JSON 里的先后，
    /// 于是「数据没改、只是两条同 Key 条目的书写顺序变了」也会算出不同哈希，表现为"明明没改却校验不过"。
    /// 同时按完整内容去重：完全相同的条目出现两次不该影响结果。</summary>
    public static string Hash(IReadOnlyList<Entry> entries)
    {
        var ordered = entries
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Geometry, StringComparer.Ordinal)
            .ThenBy(x => x.Data, ByteArrayComparer.Instance);

        using var ms = new MemoryStream();
        Entry? prev = null;
        foreach (var entry in ordered)
        {
            if (prev is { } p && SameEntry(p, entry)) continue; // 完全重复的条目只计一次
            var header = Encoding.UTF8.GetBytes($"{entry.Key}|{entry.Geometry}|");
            ms.Write(header, 0, header.Length);
            ms.Write(entry.Data, 0, entry.Data.Length);
            prev = entry;
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    private static bool SameEntry(in Entry a, in Entry b)
        => string.Equals(a.Key, b.Key, StringComparison.Ordinal)
        && string.Equals(a.Geometry, b.Geometry, StringComparison.Ordinal)
        && a.Data.AsSpan().SequenceEqual(b.Data);

    /// <summary>字节数组按字典序比大小（用作排序键，不关心内容语义）。</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int c = x[i].CompareTo(y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>指纹净数据总字节数（写进 integrity.bytes，加载时一并核对）。</summary>
    public static long DataBytes(IReadOnlyList<Entry> entries) => entries.Sum(e => (long)e.Data.Length);
}
