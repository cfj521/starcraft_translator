namespace ScrTranslator.Core.Scene;

/// <summary>
/// 游戏画面场景（四态）。翻译按场景选预设识别区域（Parts）；layout-presets.json 的 scenes 字典键即 <see cref="GameSceneIds"/>。
/// <see cref="Other"/> = 游戏在前台但不属于三目标场景（主菜单/加载/单人战役/街机/设置等），此态不翻译。
/// 场景识别由 <see cref="SceneRecognizer"/> 用图像指纹完成；<see cref="InGame"/> 的指纹按种族分套
/// （人族/神族/虫族的底部装饰板不同），故识别命中种族时会一并回填 <see cref="SceneRecognition.Race"/>。
/// </summary>
public enum GameScene { Unknown, Browsing, Room, InGame, Other }

/// <summary>
/// 场景 ↔ 字符串键。统一命名 browsing(寻找房间) / room(房间内) / ingame(游戏内) / other(其他界面)。
/// 历史遗留键见 <see cref="LegacyToNewId"/>：早期版本用 custom_games_listing / waiting_room / in_game，
/// 老校准文件由 <see cref="Normalize"/> 一次性映射，用户不必重校准。
/// </summary>
public static class GameSceneIds
{
    public const string Browsing = "browsing";
    public const string Room = "room";
    public const string InGame = "ingame";
    public const string Other = "other";

    /// <summary>旧提示词键 → 新键（老校准文件的场景键映射同源）。</summary>
    static readonly Dictionary<string, string> LegacyToNewId = new()
    {
        ["custom_games_listing"] = Browsing,
        ["waiting_room"] = Room,
        ["in_game"] = InGame,
    };

    /// <summary>把任意（新/旧）场景键归一为新键；未知键原样返回（交给上层判空，不静默吞）。</summary>
    public static string Normalize(string id) => LegacyToNewId.TryGetValue(id, out var @new) ? @new : id;

    public static string ToId(this GameScene s) => s switch
    {
        GameScene.Browsing => Browsing,
        GameScene.Room => Room,
        GameScene.InGame => InGame,
        GameScene.Other => Other,
        _ => Browsing,
    };

    public static GameScene FromId(string id) => Normalize(id) switch
    {
        Browsing => GameScene.Browsing,
        Room => GameScene.Room,
        InGame => GameScene.InGame,
        Other => GameScene.Other,
        _ => GameScene.Unknown,
    };

    public static string Label(this GameScene s) => s switch
    {
        GameScene.Browsing => "寻找房间",
        GameScene.Room => "房间内",
        GameScene.InGame => "游戏内",
        GameScene.Other => "其他界面",
        _ => "未知",
    };
}

/// <summary>种族（仅游戏内场景有意义）。字母与 docs/scenes pictures 的命名规则一致：t=人族 p=神族 z=虫族。</summary>
public enum GameRace { Unknown, Terran, Protoss, Zerg }

public static class GameRaces
{
    /// <summary>指纹参考 id 里的种族字母（"t"/"p"/"z"）；未知返回 <see cref="GameRace.Unknown"/>。</summary>
    public static GameRace FromLetter(string? letter) => letter switch
    {
        "t" => GameRace.Terran,
        "p" => GameRace.Protoss,
        "z" => GameRace.Zerg,
        _ => GameRace.Unknown,
    };

    public static string Label(this GameRace r) => r switch
    {
        GameRace.Terran => "人族",
        GameRace.Protoss => "神族",
        GameRace.Zerg => "虫族",
        _ => "未知种族",
    };
}
