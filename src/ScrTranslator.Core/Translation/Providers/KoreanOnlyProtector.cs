using System.Text.RegularExpressions;
namespace ScrTranslator.Core.Translation.Providers;

/// <summary>只翻韩文保护：行内非韩文/非空白串（英文/数字/标点）抽成 ⟨N⟩ 占位符，只让引擎翻韩文；回填时还原，任一占位符丢失（引擎改坏）→ 回退原文。
/// 与本地 Hy-MT2 的占位符保护同一套逻辑，供外部 LLM 引擎（走批量协议）共用。默认开启、无开关。</summary>
public static class KoreanOnlyProtector
{
    /// <summary>行内非韩文、非空白的连续串（英文/数字/标点）。韩文＝谚文（AC00–D7A3）/Jamo（1100–11FF）/兼容字母（3130–318F）。</summary>
    private static readonly Regex TokenRegex =
        new(@"[^\uAC00-\uD7A3\u1100-\u11FF\u3130-\u318F\s]+", RegexOptions.Compiled);

    /// <summary>指令回声标记词。占位符指令（PromptBuilder："行内的 ⟨N⟩ 是占位符…请原样保留，不要翻译或修改"）
    /// 有时被 LLM 改述一遍当某行的"译文"输出（尤其重复的英文+符号房间名），回填后变成
    /// "诸如MAP和+这样的标记是…占位符…"被当译文显示。"占位符"是这句指令的核心名词，
    /// 正常的房间名/聊天韩译中绝不会含它 → 译文行里出现"占位符"即为指令回声，回退原文。</summary>
    private const string InstructionEchoMarker = "占位符";

    /// <summary>回填占位符后清理：占位符保护时前后各加了一个空格让模型分词，回填后会留下连续空格，收成单个空格并 trim 首尾。</summary>
    private static readonly Regex MultiSpaceRegex =
        new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>批量保护：每行提取非韩文非空白串换 ⟨0⟩⟨1⟩…占位符。占位符**前后各加一个空格**（`⟨0⟩빨...⟨1⟩`→` ⟨0⟩ 빨...⟨1⟩ `），
    /// 让模型把占位符与相邻韩文段分隔开、尽量翻译韩文（纯韩文无空格段内部仍无法拆，那属分词器职责）。
    /// 返回保护后的行和每行的原始串数组。</summary>
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
                return " ⟨" + (list.Count - 1) + "⟩ ";
            });
            tokens[i] = list.ToArray();
        }
        return (protectedLines, tokens);
    }

    /// <summary>这批行里是否有占位符（决定 system prompt 要不要加"占位符原样保留"提示）。</summary>
    public static bool HasTokens(string[][] tokens) => tokens.Any(t => t.Length > 0);

    /// <summary>把译文里的 ⟨N⟩ 占位符还原成原始串。任一占位符丢失（引擎改坏）→ 返回 null（调用方回退原文）。
    /// 占位符保护时前后各加了一个空格，回填后会留下连续空格，最后收成单个空格并 trim 首尾。</summary>
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
        return MultiSpaceRegex.Replace(restored, " ").Trim();
    }

    /// <summary>解析引擎批量回复（<see cref="BatchProtocol.Parse"/>）并逐行还原占位符。Parse 失败（行数不符/序号重复）→ 返回 null（调用方整批回退原文）。
    /// 每行：回声（==保护行，引擎把占位符原文吐回来，专有名词故意不翻）→ 原文的**新字符串**（引用不等，编排器据此判 Resolved，不再每拍重试）；
    /// 指令回声（模型把占位符指令改述当译文，行内含"占位符"）→ 原文的**新字符串**（引用不等，编排器判 Resolved、缓存原文、不再重试）；
    /// 占位符丢失（引擎改坏占位符）→ 回退原文（lines[i]，引用相等，编排器判未翻成、下一拍重试）；否则还原占位符。</summary>
    public static IReadOnlyList<string>? ParseAndRestore(string reply, IReadOnlyList<string> lines, IReadOnlyList<string> protectedLines, string[][] tokens)
    {
        var parsed = BatchProtocol.Parse(reply, lines.Count);
        if (parsed is null) return null;
        var results = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (parsed[i] == protectedLines[i]) { results[i] = new string(lines[i].ToCharArray()); continue; } // 回声＝专有名词故意保留 → 新字符串（引用不等），编排器判 Resolved
            if (parsed[i].Contains(InstructionEchoMarker)) { results[i] = new string(lines[i].ToCharArray()); continue; } // 指令回声（模型改述占位符指令当译文）→ 回退原文，不显示指令
            string? restored = Restore(parsed[i], tokens[i]);
            results[i] = restored ?? lines[i];
        }
        return results;
    }
}
