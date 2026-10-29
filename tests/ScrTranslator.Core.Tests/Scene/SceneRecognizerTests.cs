using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>
/// 识别器端到端：合成参考 + 合成帧，覆盖四种判定结论。
/// 真实截图的判别力回归在 <see cref="SceneRefsShippingTests"/>，两者互补：
/// 这里测"逻辑对不对"，那里测"参考选得好不好"。
/// </summary>
public class SceneRecognizerTests
{
    static SceneRecognizer RecognizerFor(params (string id, string gameScene, string? race, byte[] data)[] scenes)
        => new(SceneRefStore.Load(SceneRefJson.TempPathWith(SceneRefJson.Build(scenes))));

    // 方案 B：参考只存 1080p，运行时等比缩放到帧分辨率。测试帧用 1920x1080（scale=1）最简单。
    static FramePixels Frame(byte v) => SceneTestFrames.View(SceneTestFrames.Solid(1920, 1080, v, v, v), 1920, 1080);

    [Fact]
    public void Recognize_PicksTheMatchingReference()
    {
        var rec = RecognizerFor(
            ("browsing", "browsing", null, SceneRefJson.Flat(10)),
            ("room", "room", null, SceneRefJson.Flat(200))).Recognize(Frame(10));

        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(GameScene.Browsing, rec.Scene);
        Assert.Equal("browsing", rec.RefId);
        Assert.Equal(1.0, rec.HitRatio, 3);
        Assert.Equal(0.0, rec.RunnerUpRatio, 3);
        Assert.Equal(2, rec.Scores.Count); // 打分明细要全给，DEBUG 面板靠它定位问题
    }

    [Fact]
    public void Recognize_InGame_ReturnsRace()
    {
        var rec = RecognizerFor(
            ("ingame_t", "ingame", "t", SceneRefJson.Flat(10)),
            ("ingame_p", "ingame", "p", SceneRefJson.Flat(110)),
            ("ingame_z", "ingame", "z", SceneRefJson.Flat(210))).Recognize(Frame(110));

        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(GameScene.InGame, rec.Scene);        // 四态归并：ROI 按 ingame 选
        Assert.Equal(GameRace.Protoss, rec.Race);         // 种族单独给出：为"按种族选聊天框"留口
        Assert.Equal("ingame_p", rec.RefId);
    }

    [Fact]
    public void Recognize_NothingMatches_ReportsOtherScene()
    {
        var rec = RecognizerFor(
            ("browsing", "browsing", null, SceneRefJson.Flat(10)),
            ("room", "room", null, SceneRefJson.Flat(200))).Recognize(Frame(105));

        Assert.Equal(SceneMatchStatus.BelowThreshold, rec.Status);
        Assert.Equal(GameScene.Other, rec.Scene); // 主菜单/加载/街机等：上层据此停翻
        Assert.Equal(0.0, rec.HitRatio, 3);
    }

    [Fact]
    public void Recognize_ThresholdIsTheSingleKnob()
    {
        // 4 块里 3 块命中 = 0.75：阈值 0.9 判不匹配，降到 0.7 就该匹配。
        var buf = SceneTestFrames.Solid(1920, 1080, 10, 10, 10);
        SceneTestFrames.Paint(buf, 1920, 4, 4, 4, 4, 250, 250, 250); // 把右下角那块涂花
        var frame = SceneTestFrames.View(buf, 1920, 1080);
        var scenes = new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) };

        Assert.Equal(SceneMatchStatus.BelowThreshold, RecognizerFor(scenes).Recognize(frame).Status);

        var lenient = new SceneRecognizer(SceneRefStore.Load(SceneRefJson.TempPathWith(
            SceneRefJson.Build(scenes, minHit: 0.7))));
        var rec = lenient.Recognize(frame);
        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(0.75, rec.HitRatio, 3);
    }

    [Fact]
    public void Recognize_ScalesReferenceToFrameResolution_StillMatches()
    {
        // 方案 B：参考只存 1080p，运行时把基准区域等比缩放到帧分辨率。
        // 3840x2160 帧（scale=2）不是"未知分辨率"，照样能命中——不再需要补截图。
        var rec = RecognizerFor(("browsing", "browsing", null, SceneRefJson.Flat(10)))
            .Recognize(SceneTestFrames.View(SceneTestFrames.Solid(3840, 2160, 10, 10, 10), 3840, 2160));

        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(GameScene.Browsing, rec.Scene);
        Assert.Equal("browsing", rec.RefId);
    }

    [Fact]
    public void Recognize_UsesFrameRatioOverride_NotOnlyBaseRatio()
    {
        // 现场采集按"采集时的比例"作键（16:10 / 4:3 …）。旧实现识别器只查基准键 "16:9"，
        // 于是这些覆盖参考永远读不到——采集提示成功、识别毫无变化（功能假成功）。
        var data = SceneRefJson.Flat(10);
        // 键就是 RatioKey 的归约结果（16:10 归约成 8:5），故直接调它，不手写字符串。
        string resKey = SceneRefStore.RatioKey(1680, 1050);
        Assert.NotEqual("16:9", resKey);
        var entries = new List<SceneRefIntegrity.Entry>
        {
            new(SceneRefIntegrity.Key("browsing", resKey, "r"),
                SceneRefIntegrity.Geometry(0, 0, SceneRefJson.RelW, SceneRefJson.RelH, 4, 2, 2), data),
        };
        string json = $$"""
            { "version": 1,
              "fingerprint": { "baseResolution": "1920x1080", "blockSize": 4, "channelTolerance": 16, "minHitRatio": 0.9 },
              "integrity": { "algorithm": "sha256", "bytes": {{SceneRefIntegrity.DataBytes(entries)}},
                             "sha256": "{{SceneRefIntegrity.Hash(entries)}}" },
              "scenes": { "browsing": { "label": "browsing", "gameScene": "browsing",
                "regions": { "name": "r", "rect": [0,0,{{SceneRefJson.RelW:R}},{{SceneRefJson.RelH:R}}] },
                "data": [ { "gameScene": "browsing", "ratio": "{{resKey}}", "source_file": "test",
                  "blockPx": 4, "blocks": [2,2],
                  "data": "{{Convert.ToBase64String(data)}}" } ] } } }
            """;
        var rec = new SceneRecognizer(SceneRefStore.Load(SceneRefJson.TempPathWith(json)))
            .Recognize(SceneTestFrames.View(SceneTestFrames.Solid(1680, 1050, 10, 10, 10), 1680, 1050));

        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(GameScene.Browsing, rec.Scene);
    }

    [Fact]
    public void Recognize_StoreFailedToLoad_ReportsNoReferencesWithReason()
    {
        var rec = new SceneRecognizer(SceneRefStore.Load(Path.GetTempPath() + "/absent-" + Guid.NewGuid() + ".json"))
            .Recognize(Frame(10));

        Assert.Equal(SceneMatchStatus.NoReferences, rec.Status);
        Assert.Equal(GameScene.Unknown, rec.Scene);
        Assert.NotNull(rec.Detail);
    }

    [Fact]
    public void Recognize_SameGeometryCandidatesShareOneLiveFingerprint()
    {
        // ingame_t/p/z 区域几何相同 → 实时指纹只算一次。用可观察的副作用验证：
        // 三族参考都放同一份数据，任一帧都应 100% 命中其中一条且分数一致。
        var rec = RecognizerFor(
            ("ingame_t", "ingame", "t", SceneRefJson.Flat(77)),
            ("ingame_p", "ingame", "p", SceneRefJson.Flat(77)),
            ("ingame_z", "ingame", "z", SceneRefJson.Flat(77))).Recognize(Frame(77));

        Assert.Equal(3, rec.Scores.Count);
        Assert.All(rec.Scores, s => Assert.Equal(1.0, s.HitRatio, 3));
    }
}
