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

    /// <summary>按 Key 序数排序后，依次写入 "key|geometry|" 与其指纹字节，取 SHA-256（小写 hex）。</summary>
    public static string Hash(IReadOnlyList<Entry> entries)
    {
        using var ms = new MemoryStream();
        foreach (var entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var header = Encoding.UTF8.GetBytes($"{entry.Key}|{entry.Geometry}|");
            ms.Write(header, 0, header.Length);
            ms.Write(entry.Data, 0, entry.Data.Length);
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    /// <summary>指纹净数据总字节数（写进 integrity.bytes，加载时一并核对）。</summary>
    public static long DataBytes(IReadOnlyList<Entry> entries) => entries.Sum(e => (long)e.Data.Length);
}
