using System.Text.RegularExpressions;
namespace ScrTranslator.Core.Translation.Providers;

/// <summary>只翻韩文保护：行内非韩文/非空白串（英文/数字/标点）抽成 ⟨N⟩ 占位符，只让引擎翻韩文；回填时还原，任一占位符丢失（引擎改坏）→ 回退原文。
/// 与本地 Hy-MT2 的占位符保护同一套逻辑，供外部 LLM 引擎（走批量协议）共用。默认开启、无开关。</summary>
public static class KoreanOnlyProtector
{
    /// <summary>行内非韩文、非空白的连续串（英文/数字/标点）。韩文＝谚文（AC00–D7A3）/Jamo（1100–11FF）/兼容字母（3130–318F）。</summary>
    private static readonly Regex TokenRegex =
        new(@"[^\uAC00-\uD7A3\u1100-\u11FF\u3130-\u318F\s]+", RegexOptions.Compiled);

    /// <summary>批量保护：每行提取非韩文非空白串换 ⟨0⟩⟨1⟩…占位符。返回保护后的行和每行的原始串数组。</summary>
    public static (IReadOnlyList<string> Protected, string[][] Tokens) Protect(IReadOnlyList<string> lines)
    {
        var protectedLines = new string[lines.Count];
        var tokens = new string[lines.Count][];
        for (int i = 0; i < lines.Count; i++)
        {
            var list = new List<string>();
            protectedLines[i] = TokenRegex.Replace(lines[i], m =>
            {
                list.Add(m.Value);
                return "⟨" + (list.Count - 1) + "⟩";
            });
            tokens[i] = list.ToArray();
        }
        return (protectedLines, tokens);
    }

    /// <summary>这批行里是否有占位符（决定 system prompt 要不要加"占位符原样保留"提示）。</summary>
    public static bool HasTokens(string[][] tokens) => tokens.Any(t => t.Length > 0);

    /// <summary>把译文里的 ⟨N⟩ 占位符还原成原始串。任一占位符丢失（引擎改坏）→ 返回 null（调用方回退原文）。</summary>
    public static string? Restore(string translated, string[] tokens)
    {
        if (tokens.Length == 0) return translated;
        string restored = translated;
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            string placeholder = "⟨" + i + "⟩";
            if (!restored.Contains(placeholder)) return null;
            restored = restored.Replace(placeholder, tokens[i]);
        }
        return restored;
    }

    /// <summary>解析引擎批量回复（<see cref="BatchProtocol.Parse"/>）并逐行还原占位符。Parse 失败（行数不符/序号重复）→ 返回 null（调用方整批回退原文）。
    /// 每行：回声（==保护行，引擎把占位符原文吐回来）或占位符丢失 → 回退原文（lines[i]，引用相等，编排器判未翻成）；否则还原占位符。</summary>
    public static IReadOnlyList<string>? ParseAndRestore(string reply, IReadOnlyList<string> lines, IReadOnlyList<string> protectedLines, string[][] tokens)
    {
        var parsed = BatchProtocol.Parse(reply, lines.Count);
        if (parsed is null) return null;
        var results = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (parsed[i] == protectedLines[i]) { results[i] = lines[i]; continue; } // 回声
            string? restored = Restore(parsed[i], tokens[i]);
            results[i] = restored ?? lines[i];
        }
        return results;
    }
}
