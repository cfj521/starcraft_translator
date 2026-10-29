using System.Text;
using System.Text.RegularExpressions;
namespace ScrTranslator.Core.Translation.Providers;
public static class BatchProtocol
{
    public const string SystemPrompt =
        "你是《星际争霸：重制版》联机界面的韩译中助手。把每行韩文翻成简体中文。" +
        "输入每行形如 [i] 原文；只输出译文行，格式 [i] 译文，行数与序号必须与输入一一对应。" +
        "玩家ID、纯英文/数字/符号原样保留。不要解释、不要多余内容。";

    public static string BuildUser(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++) sb.Append('[').Append(i + 1).Append("] ").AppendLine(lines[i]);
        return sb.ToString().TrimEnd();
    }

    // 容忍模型给序号加 Markdown 修饰（`**[1]**`、``` `[1]` ```、`- [1]`、`### [1]`）：
    // 严格只认行首 "[n]" 时，整块输出会被判"行数不符"→ 整批回落原文并每拍重发。
    private static readonly Regex Row = new(@"^\s*(?:[*_`#>\-]+\s*)*\[(\d+)\]\s*[*_`]*\s?(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<string>? Parse(string reply, int expected)
    {
        var found = new Dictionary<int, string>();
        foreach (var raw in reply.Split('\n'))
        {
            var m = Row.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out var idx))
            {
                if (found.ContainsKey(idx)) return null; // 序号重复 → 整批降级回原文
                // 去掉行尾的 Markdown 修饰（模型常写成 "**[1]** 译文 **"）
                found[idx] = m.Groups[2].Value.Trim().Trim('*', '`').Trim();
            }
        }
        if (found.Count != expected) return null;
        var res = new string[expected];
        for (int i = 1; i <= expected; i++)
        {
            if (!found.TryGetValue(i, out var v)) return null;
            if (v.Length == 0) return null; // 空译文：不能当"已翻成"缓存空串（悬浮层出现空白行且永久缓存）
            res[i - 1] = v;
        }
        return res;
    }
}
