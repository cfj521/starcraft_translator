using System.Text;
using ScrTranslator.Core.Scene;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.Core.Translation;

/// <summary>组装翻译 system prompt：基础批量协议 + 场景 + 术语提示。两 provider 共用。</summary>
public static class PromptBuilder
{
    public const int GlossaryHintMaxChars = 1200;

    public static string BuildSystemPrompt(TranslationContext ctx)
    {
        var sb = new StringBuilder(BatchProtocol.SystemPrompt);
        sb.Append(" 当前场景：").Append(SceneText(ctx.Scene)).Append('。');
        if (!string.IsNullOrWhiteSpace(ctx.GlossaryHint))
            sb.Append(" 遇到下列术语请统一采用给定译法：").Append(ctx.GlossaryHint);
        return sb.ToString();
    }

    /// <summary>把术语条目拼成 "ko=zh; ko=zh"，超过 maxChars 截断并以 … 结尾。截断责任单一在此。</summary>
    public static string BuildGlossaryHint(
        IEnumerable<KeyValuePair<string, string>> entries, int maxChars = GlossaryHintMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var kv in entries)
        {
            var piece = (sb.Length == 0 ? "" : "; ") + kv.Key + "=" + kv.Value;
            if (sb.Length + piece.Length > maxChars) { sb.Append('…'); break; }
            sb.Append(piece);
        }
        return sb.ToString();
    }

    /// <summary>场景键 → 提示词里的场景说明。先归一历史键（custom_games_listing 等），老校准/老上下文也能拿到正确提示。</summary>
    private static string SceneText(string scene) => GameSceneIds.Normalize(scene) switch
    {
        GameSceneIds.Browsing => "自定义游戏房间列表（每行是 房间名/地图名，多为对战设置与黑话）",
        GameSceneIds.Room => "房间内等待大厅（玩家列表/队伍/种族/地图设置）",
        GameSceneIds.InGame => "游戏对局内（聊天/系统消息，玩家即时口语）",
        _ => scene,
    };
}
