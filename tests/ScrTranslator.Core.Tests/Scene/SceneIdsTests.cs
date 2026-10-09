using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

/// <summary>场景/种族字符串键：统一命名 browsing/room/ingame，同时兼容早期校准文件留下的旧键。</summary>
public class SceneIdsTests
{
    [Theory]
    [InlineData("browsing", GameScene.Browsing)]
    [InlineData("room", GameScene.Room)]
    [InlineData("ingame", GameScene.InGame)]
    [InlineData("other", GameScene.Other)]
    [InlineData("custom_games_listing", GameScene.Browsing)] // 旧键
    [InlineData("waiting_room", GameScene.Room)]             // 旧键
    [InlineData("in_game", GameScene.InGame)]                // 旧键
    [InlineData("totally-unknown", GameScene.Unknown)]
    public void FromId_AcceptsNewAndLegacyKeys(string id, GameScene expected)
        => Assert.Equal(expected, GameSceneIds.FromId(id));

    [Fact]
    public void ToId_EmitsUnifiedNames()
    {
        Assert.Equal("browsing", GameScene.Browsing.ToId());
        Assert.Equal("room", GameScene.Room.ToId());
        Assert.Equal("ingame", GameScene.InGame.ToId());
        Assert.Equal("other", GameScene.Other.ToId());
    }

    [Fact]
    public void Normalize_LegacyBecomesNew_NewStaysOther_UnknownUntouched()
    {
        Assert.Equal("browsing", GameSceneIds.Normalize("custom_games_listing"));
        Assert.Equal("room", GameSceneIds.Normalize("waiting_room"));
        Assert.Equal("ingame", GameSceneIds.Normalize("in_game"));
        Assert.Equal("browsing", GameSceneIds.Normalize("browsing"));
        // 未知键必须原样返回，让上层能发现拼写错误而不是被静默归到某个场景
        Assert.Equal("whatever", GameSceneIds.Normalize("whatever"));
    }

    [Fact]
    public void Labels_MatchProductNaming()
    {
        Assert.Equal("寻找房间", GameScene.Browsing.Label());
        Assert.Equal("房间内", GameScene.Room.Label());
        Assert.Equal("游戏内", GameScene.InGame.Label());
        Assert.Equal("其他界面", GameScene.Other.Label());
    }

    [Theory]
    [InlineData("t", GameRace.Terran)]
    [InlineData("p", GameRace.Protoss)]
    [InlineData("z", GameRace.Zerg)]
    [InlineData("a", GameRace.Unknown)]   // 'a'=another 是截图命名用的，不参与识别
    [InlineData(null, GameRace.Unknown)]
    public void Race_FromLetter(string? letter, GameRace expected)
        => Assert.Equal(expected, GameRaces.FromLetter(letter));
}
