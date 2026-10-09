namespace ScrTranslator.Core.Scene;

/// <summary>游戏画面场景。翻译按场景选各自 ROI；rois.json 的 scenes 字典键即 <see cref="GameSceneIds"/>。
/// <see cref="Other"/> = 游戏在前台但不属于三目标场景（主菜单/加载/单人/街机/设置等），此态不翻译。</summary>
public enum GameScene { Unknown, RoomBrowser, WaitingRoom, InGame, Other }

/// <summary>场景 → rois.json 里的字符串键。<see cref="RoomBrowser"/> 沿用既有 "custom_games_listing" 键以兼容老校准。</summary>
public static class GameSceneIds
{
    public const string RoomBrowser = "custom_games_listing";
    public const string WaitingRoom = "waiting_room";
    public const string InGame = "in_game";
    public const string Other = "other";

    public static string ToId(this GameScene s) => s switch
    {
        GameScene.RoomBrowser => RoomBrowser,
        GameScene.WaitingRoom => WaitingRoom,
        GameScene.InGame => InGame,
        GameScene.Other => Other,
        _ => RoomBrowser,
    };

    public static GameScene FromId(string id) => id switch
    {
        RoomBrowser => GameScene.RoomBrowser,
        WaitingRoom => GameScene.WaitingRoom,
        InGame => GameScene.InGame,
        Other => GameScene.Other,
        _ => GameScene.Unknown,
    };

    public static string Label(this GameScene s) => s switch
    {
        GameScene.RoomBrowser => "房间列表",
        GameScene.WaitingRoom => "等待房",
        GameScene.InGame => "游戏内",
        GameScene.Other => "其他界面",
        _ => "未知",
    };
}
