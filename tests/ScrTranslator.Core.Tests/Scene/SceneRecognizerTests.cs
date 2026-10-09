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

    static FramePixels Frame(byte v) => SceneTestFrames.View(SceneTestFrames.Solid(64, 64, v, v, v), 64, 64);

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
        var buf = SceneTestFrames.Solid(64, 64, 10, 10, 10);
        SceneTestFrames.Paint(buf, 64, 4, 4, 4, 4, 250, 250, 250); // 把右下角那块涂花
        var frame = SceneTestFrames.View(buf, 64, 64);
        var scenes = new[] { ("browsing", "browsing", (string?)null, SceneRefJson.Flat(10)) };

        Assert.Equal(SceneMatchStatus.BelowThreshold, RecognizerFor(scenes).Recognize(frame).Status);

        var lenient = new SceneRecognizer(SceneRefStore.Load(SceneRefJson.TempPathWith(
            SceneRefJson.Build(scenes, minHit: 0.7))));
        var rec = lenient.Recognize(frame);
        Assert.Equal(SceneMatchStatus.Matched, rec.Status);
        Assert.Equal(0.75, rec.HitRatio, 3);
    }

    [Fact]
    public void Recognize_UnknownResolution_DoesNotGuess_AndListsWhatItHas()
    {
        var rec = RecognizerFor(("browsing", "browsing", null, SceneRefJson.Flat(10)))
            .Recognize(SceneTestFrames.View(SceneTestFrames.Solid(32, 32, 10, 10, 10), 32, 32));

        Assert.Equal(SceneMatchStatus.NoReferenceForResolution, rec.Status);
        Assert.Equal(GameScene.Unknown, rec.Scene);
        Assert.Contains("64x64", rec.Detail); // 提示"已收录哪些分辨率"，用户才知道要补什么
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
