using ScrTranslator.Core.Scene;

namespace ScrTranslator.Core.Tests.Scene;

public class SceneDetectorTests
{
    // 房间列表：含 Filter/Create/Search custom games/欢迎语；也含 Game Name/Map Name（不能据此判大厅）
    private static readonly string[] Browser =
    {
        "Custom Games", "Search custom games", "Filter", "Create",
        "Players", "Game Name", "Game Type", "Map Name",
        "1:1 투혼 매너", "Melee", "투혼 1.4",
        "Welcome to StarCraft: Remastered!", "There are 5664 players online.",
    };

    // 等待房：Select Race/Observers/Map specified/Unit Limits/Turn Rate；也含 Game Name/Map Name/Custom Games(战绩行)
    private static readonly string[] Waiting =
    {
        "1 팀 [빨강/보라/NPC]", "dudwhdeh1234", "Random", "Map specified", "Computer", "Open",
        "2 팀 [연두/파랑/NPC]", "Colex111", "Select Race",
        "Observers", "Closed",
        "Game Name", "랜크최신 1:1 #x1.5", "Map Name", "Game Type", "Use Map Settings",
        "Size: 128x128", "Speed: Fastest", "Turn Rate: 24", "Custom Hotkeys: Enabled", "Unit Limits: Original",
        "dudwhdeh1234: [Korea] Custom Games - Wins: 5, Losses: 7, Disconnects: 0.",
    };

    // 游戏内：右下 MENU + 右上 supply 4/9 + 计时 02:24 + 聊天
    private static readonly string[] InGame =
    {
        "50", "0", "4/9", "0", "02:24", "MENU",
        "Colex111: 12345678901234567890",
    };

    [Fact] public void Detect_Browser()
        => Assert.Equal(GameScene.RoomBrowser, SceneDetector.Detect(Browser).Scene);

    [Fact] public void Detect_WaitingRoom()
        => Assert.Equal(GameScene.WaitingRoom, SceneDetector.Detect(Waiting).Scene);

    [Fact] public void Detect_InGame()
        => Assert.Equal(GameScene.InGame, SceneDetector.Detect(InGame).Scene);

    [Fact] public void GameName_MapName_AreNotBrowserCues_WaitingStillWins()
    {
        // 只有 Game Name/Map Name/Game Type 这类共享词 + 少量大厅词，不应被判成浏览器
        var d = SceneDetector.Detect(new[] { "Game Name", "Map Name", "Game Type", "Select Race", "Observers" });
        Assert.Equal(GameScene.WaitingRoom, d.Scene);
    }

    [Fact] public void EmptyText_Unknown()
        => Assert.Equal(GameScene.Unknown, SceneDetector.Detect(Array.Empty<string>()).Scene);

    [Fact] public void WeakAmbiguous_Unknown()
    {
        // 只有一两个弱词，不足以越过阈值/边界 → Unknown（调用方保持上一场景）
        var d = SceneDetector.Detect(new[] { "hello world", "some random chat" });
        Assert.Equal(GameScene.Unknown, d.Scene);
    }

    [Fact] public void InGame_MenuOnly_ReachesThreshold()
        => Assert.Equal(GameScene.InGame, SceneDetector.Detect(new[] { "MENU" }).Scene);

    [Fact] public void Hits_ReportMatchedCues()
    {
        var d = SceneDetector.Detect(Browser);
        Assert.Contains("filter", d.Hits);
        Assert.Contains("welcome to starcraft", d.Hits);
    }
}
