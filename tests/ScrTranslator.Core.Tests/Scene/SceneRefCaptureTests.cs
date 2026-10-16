using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 现场场景指纹采集：缩放几何、块均值指纹、写覆盖层+合并、取基准区域。
/// 这条链是覆盖层的唯一写入入口——此前只有读没有写，"现场补新分辨率参考"是断的。
/// </summary>
public class SceneRefCaptureTests
{
    [Fact]
    public void FittingRegions_ScalesGeometryAndKeepsBlockCount()
    {
        // 统一 H/1080 拉伸：1920×1080 → 3840×2160（sy=2），无裁剪
        var baseRegions = new[] { new SceneRefRegion("hud-deco-left", 8, 235, 1148, 415, 4, 287, 104) };
        var scaled = SceneRefCapture.FittingRegions(baseRegions, 3840, 2160, centerCrop: false);
        var s = scaled.Single();

        Assert.Equal(16, s.X);          // 8×2
        Assert.Equal(470, s.Y);         // 235×2
        Assert.Equal(2296, s.Width);    // 1148×2
        Assert.Equal(830, s.Height);    // 415×2
        Assert.Equal(287, s.BlocksX);   // 块数固定（不随分辨率变）
        Assert.Equal(104, s.BlocksY);   // 块数固定
        Assert.Equal(2296 / 287, s.BlockPx); // 块宽 = 缩放后宽 / 块数
        Assert.Equal("hud-deco-left", s.Name); // 名字保留
    }

    [Fact]
    public void FittingRegions_SmallerScaleKeepsBlockCount()
    {
        // 统一 H/1080 拉伸：1920×1080 → 960×540（sy=0.5），块数仍固定，块宽 = 8/4 = 2
        var baseRegions = new[] { new SceneRefRegion("r", 0, 0, 16, 16, 4, 4, 4) };
        var scaled = SceneRefCapture.FittingRegions(baseRegions, 960, 540, centerCrop: false);
        Assert.Equal(2, scaled[0].BlockPx);
        Assert.Equal(8, scaled[0].Width);   // 16×0.5
        Assert.Equal(4, scaled[0].BlocksX); // 块数固定
        Assert.Equal(4, scaled[0].BlocksY); // 块数固定
    }

    [Fact]
    public void Capture_ComputesBlockAveragesFromFrame()
    {
        // 16×16 BGRA 帧，所有像素 R=10 G=20 B=30
        var buf = new byte[16 * 16 * 4];
        for (int i = 0; i < 16 * 16; i++)
        {
            buf[i * 4 + 0] = 30; // B
            buf[i * 4 + 1] = 20; // G
            buf[i * 4 + 2] = 10; // R
            buf[i * 4 + 3] = 255;
        }
        var frame = new FramePixels(buf, 16, 16, 16 * 4, 4, 2, 1, 0);
        // 1080p 基准区域 (0,0,16,16)，目标 1080×1080（sy=1）→ 块宽 4，4×4=16 块
        var (regions, bytes) = SceneRefCapture.Capture(frame,
            new[] { new SceneRefRegion("r", 0, 0, 16, 16, 4, 4, 4) }, 1080, 1080, centerCrop: false);

        Assert.Single(regions);
        // 16×16 像素、块宽 4 → 4×4=16 块，每块 1 个 RGB
        Assert.Equal(16, regions[0].BlocksX * regions[0].BlocksY);
        Assert.Equal(16 * 3, regions[0].ByteLength);
        Assert.Equal(16 * 3, bytes[0].Length);
        for (int i = 0; i < bytes[0].Length; i += 3)
        {
            Assert.Equal(10, bytes[0][i]);      // R
            Assert.Equal(20, bytes[0][i + 1]);  // G
            Assert.Equal(30, bytes[0][i + 2]);  // B
        }
    }

    [Fact]
    public void SaveOverride_WritesResolutionAndLoadBack()
    {
        var factoryPath = SceneRefJson.TempPathWith(SceneRefJson.Build(
            new (string, string, string?, byte[])[] { ("browsing", "browsing", null, SceneRefJson.Flat(5)) }));
        var overridePath = Path.Combine(Path.GetTempPath(), "scene-ov-" + Guid.NewGuid() + ".json");

        // 现场采集一个 128×128 的 browsing 参考
        SceneRefCapture.SaveOverride(overridePath, "browsing", "寻找房间", "browsing", null,
            128, 128, "现场采集",
            new[] { new SceneRefRegion("r", 0, 0, 16, 16, 8, 2, 2) },
            new[] { new byte[] { 10, 20, 30, 10, 20, 30, 10, 20, 30, 10, 20, 30 } });

        var store = SceneRefStore.Load(factoryPath, overridePath);
        Assert.True(store.IsUsable, store.LoadError);
        Assert.Equal(1, store.OverrideCount);
        var reference = store.Find("browsing")!;
        Assert.True(reference.TryResolution(128, 128, out var res));
        Assert.Equal("现场采集", res.Source);
        Assert.Single(res.Regions);
        // 比例键不携带分辨率，绝对像素一律按基准分辨率（1920×1080）换算：0~1 的 16/128 → 16/128×1920 = 240。
        Assert.Equal(240, res.Regions[0].Width);
        File.Delete(overridePath);
    }

    [Fact]
    public void SaveOverride_AppendsNewSceneWithoutClobbering()
    {
        var factoryPath = SceneRefJson.TempPathWith(SceneRefJson.Build(
            new (string, string, string?, byte[])[] { ("browsing", "browsing", null, SceneRefJson.Flat(5)) }));
        var overridePath = Path.Combine(Path.GetTempPath(), "scene-ov-" + Guid.NewGuid() + ".json");

        // 先采一个新场景 room（出厂里没有），再采一个已有场景 browsing 的另一分辨率
        SceneRefCapture.SaveOverride(overridePath, "room", "房间内", "room", null,
            128, 128, "现场采集",
            new[] { new SceneRefRegion("r", 0, 0, 8, 8, 4, 2, 2) },
            new[] { new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 } });
        SceneRefCapture.SaveOverride(overridePath, "browsing", "寻找房间", "browsing", null,
            256, 256, "现场采集",
            new[] { new SceneRefRegion("r", 0, 0, 32, 32, 16, 2, 2) },
            new[] { new byte[] { 9, 9, 9, 8, 8, 8, 7, 7, 7, 6, 6, 6 } });

        var store = SceneRefStore.Load(factoryPath, overridePath);
        Assert.True(store.IsUsable, store.LoadError);
        Assert.Equal(2, store.OverrideCount);
        // 新场景 room 被追加
        Assert.True(store.Find("room")!.TryResolution(128, 128, out _));
        // browsing 既有出厂 1920×1080（基准），又有现场 256×256
        var browsing = store.Find("browsing")!;
        Assert.True(browsing.TryResolution(1920, 1080, out _));
        Assert.True(browsing.TryResolution(256, 256, out _));
        File.Delete(overridePath);
    }

    [Fact]
    public void SaveOverride_OverwritesSameSceneResolution()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "scene-ov-" + Guid.NewGuid() + ".json");

        SceneRefCapture.SaveOverride(overridePath, "browsing", "寻找房间", "browsing", null,
            128, 128, "第一次",
            new[] { new SceneRefRegion("r", 0, 0, 16, 16, 8, 2, 2) },
            new[] { new byte[] { 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4 } });
        // 同 (场景,分辨率) 再采一次 → 覆盖，不追加
        SceneRefCapture.SaveOverride(overridePath, "browsing", "寻找房间", "browsing", null,
            128, 128, "第二次",
            new[] { new SceneRefRegion("r", 0, 0, 16, 16, 8, 2, 2) },
            new[] { new byte[] { 9, 9, 9, 8, 8, 8, 7, 7, 7, 6, 6, 6 } });

        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(SceneRefJson.Build(
            new (string, string, string?, byte[])[] { ("browsing", "browsing", null, SceneRefJson.Flat(5)) })), overridePath);
        Assert.Equal(1, store.OverrideCount); // 仍是 1 条，不是 2
        var res = store.Find("browsing")!.TryResolution(128, 128, out var r) ? r! : null!;
        Assert.Equal("第二次", res.Source);
        File.Delete(overridePath);
    }

    [Fact]
    public void BaseRegions_ReturnsBaseResolutionRegions()
    {
        // BaseRegions 找的是 16:9 基准（1920x1080 归约），所以样本必须建在那个比例上（SceneRefJson 默认是 64x64，故内联造）
        string resKey = "16:9";
        var data = Enumerable.Repeat((byte)5, 4 * 4 * 3).ToArray();
        var entries = new List<SceneRefIntegrity.Entry>
        {
            new(SceneRefIntegrity.Key("browsing", resKey, "r"), SceneRefIntegrity.Geometry(0, 0, 16, 16, 4, 4, 4), data),
        };
        string json = $$"""
            { "version": 1,
              "fingerprint": { "baseResolution": "1920x1080", "blockSize": 4, "channelTolerance": 16, "minHitRatio": 0.9 },
              "integrity": { "algorithm": "sha256", "bytes": {{SceneRefIntegrity.DataBytes(entries)}},
                             "sha256": "{{SceneRefIntegrity.Hash(entries)}}" },
              "scenes": { "browsing": { "label": "browsing", "gameScene": "browsing",
                "resolutions": { "{{resKey}}": { "source": "test", "regions": [
                  { "name": "r", "rect": [0,0,16,16], "blockPx": 4, "blocks": [4,4],
                    "data": "{{Convert.ToBase64String(data)}}" } ] } } } } }
            """;
        var store = SceneRefStore.Load(SceneRefJson.TempPathWith(json));
        var regions = SceneRefCapture.BaseRegions(store, "browsing");
        Assert.NotNull(regions);
        Assert.Single(regions!);
        Assert.Equal("r", regions![0].Name);
        // 没有的场景返回 null
        Assert.Null(SceneRefCapture.BaseRegions(store, "room"));
    }
}
