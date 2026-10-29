using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>integrity 哈希的性质：只取决于**内容集合**，与条目书写顺序、重复无关。
/// 旧实现只按 Key 排序，两条同 Key 条目的先后会改变哈希 → 生成端与加载端可能"没改却校验不过"。</summary>
public class SceneRefIntegrityTests
{
    static SceneRefIntegrity.Entry E(string key, string geom, params byte[] data) => new(key, geom, data);

    [Fact]
    public void Hash_IsIndependentOfEntryOrder_ForDistinctKeys()
    {
        string g = SceneRefIntegrity.Geometry(0, 0, 16.0 / 1920, 16.0 / 1080, 4, 2, 2);
        var a = E("browsing/16:9/r", g, 1, 2, 3);
        var b = E("room/16:9/r", g, 4, 5, 6);
        var c = E("ingame/16:9/r", g, 7, 8, 9);

        string h1 = SceneRefIntegrity.Hash(new[] { a, b, c });
        string h2 = SceneRefIntegrity.Hash(new[] { c, a, b });
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Hash_IsIndependentOfOrder_ForSameKeyDifferentData()
    {
        // 同 Key、同几何、不同指纹：旧实现 OrderBy(Key) 是稳定排序 → 结果取决于书写顺序。
        string g = SceneRefIntegrity.Geometry(0, 0, 0.5, 0.5, 4, 2, 2);
        var x = E("browsing/16:9/r", g, 1, 1, 1);
        var y = E("browsing/16:9/r", g, 2, 2, 2);

        Assert.Equal(SceneRefIntegrity.Hash(new[] { x, y }), SceneRefIntegrity.Hash(new[] { y, x }));
    }

    [Fact]
    public void Hash_IgnoresExactDuplicateEntries()
    {
        string g = SceneRefIntegrity.Geometry(0, 0, 0.5, 0.5, 4, 2, 2);
        var one = E("browsing/16:9/r", g, 1, 2, 3);
        var dup = E("browsing/16:9/r", g, 1, 2, 3);

        Assert.Equal(SceneRefIntegrity.Hash(new[] { one }), SceneRefIntegrity.Hash(new[] { one, dup }));
    }

    [Fact]
    public void Hash_ChangesWhenDataChanges()
    {
        string g = SceneRefIntegrity.Geometry(0, 0, 0.5, 0.5, 4, 2, 2);
        Assert.NotEqual(
            SceneRefIntegrity.Hash(new[] { E("browsing/16:9/r", g, 1, 2, 3) }),
            SceneRefIntegrity.Hash(new[] { E("browsing/16:9/r", g, 1, 2, 4) }));
    }
}
