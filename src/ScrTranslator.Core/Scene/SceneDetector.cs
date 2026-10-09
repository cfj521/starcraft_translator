using System.Text;
using System.Text.RegularExpressions;

namespace ScrTranslator.Core.Scene;

/// <summary>一次场景判定的结果。<see cref="Scene"/> 为 Unknown 表示证据不足，调用方应保持上一场景。</summary>
public sealed record SceneDetection(GameScene Scene, int Score, int Margin, IReadOnlyList<string> Hits);

/// <summary>
/// 纯文本规则场景判定器（可单测）。输入 = 一帧里若干 OCR 文本；输出 = 最匹配的 <see cref="GameScene"/>。
/// 依据：SC:R 客户端 UI 恒为英文，三场景各有互斥的稳定英文标签（见 cue 表）。
/// 关键防误判：Game Name/Map Name/Custom Games 在"房间列表"与"等待房"都出现，故一律不作判别 cue。
/// 游戏内以 "menu" 为主 cue，辅以 supply "x/y"、计时 "mm:ss" 正则（弱证据）。
/// </summary>
public static class SceneDetector
{
    // cue → 权重。命中=任一文本包含该子串（小写匹配）。
    private static readonly (string Cue, int Weight)[] RoomBrowserCues =
    {
        ("search custom games", 3), ("filter", 2), ("create", 2),
        ("welcome to starcraft", 3), ("players online", 2), ("being played", 2),
        ("/help", 2), ("joined channel", 1), ("default channel", 1),
    };
    private static readonly (string Cue, int Weight)[] WaitingRoomCues =
    {
        ("select race", 3), ("observers", 3), ("map specified", 2),
        ("unit limits", 3), ("turn rate", 2), ("custom hotkeys", 2),
        ("team specified", 2), ("speed:", 1), ("size:", 1),
        ("computer", 1), ("closed", 1),
    };
    private static readonly (string Cue, int Weight)[] InGameCues =
    {
        ("menu", 3),
    };

    // 游戏内 HUD 的 supply（如 "4/9"）与计时（如 "02:24"）：弱证据，各 +1。
    private static readonly Regex SupplyRx = new(@"\b\d{1,3}\s*/\s*\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex TimerRx = new(@"\b\d{1,2}:\d{2}\b", RegexOptions.Compiled);

    /// <summary>切换阈值：最高分需 ≥ <paramref name="minScore"/> 且领先次高 ≥ <paramref name="minMargin"/> 才判为某场景，否则 Unknown。</summary>
    public static SceneDetection Detect(IEnumerable<string> texts, int minScore = 3, int minMargin = 2)
    {
        var joined = new StringBuilder();
        foreach (var t in texts) if (!string.IsNullOrWhiteSpace(t)) joined.Append(t).Append('\n');
        string all = joined.ToString().ToLowerInvariant();

        var (rb, rbHits) = Score(RoomBrowserCues, all);
        var (wr, wrHits) = Score(WaitingRoomCues, all);
        var (ig, igHits) = Score(InGameCues, all);
        var extraIg = new List<string>();
        if (SupplyRx.IsMatch(all)) { ig += 1; extraIg.Add("supply x/y"); }
        if (TimerRx.IsMatch(all)) { ig += 1; extraIg.Add("timer mm:ss"); }
        igHits = igHits.Concat(extraIg).ToList();

        var ranked = new[]
        {
            (GameScene.RoomBrowser, rb, rbHits),
            (GameScene.WaitingRoom, wr, wrHits),
            (GameScene.InGame, ig, igHits),
        }.OrderByDescending(x => x.Item2).ToArray();

        var best = ranked[0];
        int second = ranked[1].Item2;
        if (best.Item2 < minScore || best.Item2 - second < minMargin)
            return new SceneDetection(GameScene.Unknown, best.Item2, best.Item2 - second, best.Item3);

        return new SceneDetection(best.Item1, best.Item2, best.Item2 - second, best.Item3);
    }

    private static (int score, List<string> hits) Score((string Cue, int Weight)[] cues, string lowerText)
    {
        int s = 0; var hits = new List<string>();
        foreach (var (cue, w) in cues)
            if (lowerText.Contains(cue)) { s += w; hits.Add(cue); }
        return (s, hits);
    }
}
