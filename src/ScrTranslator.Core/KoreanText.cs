namespace ScrTranslator.Core;

/// <summary>
/// 韩文（谚文）判定，用作翻译管线的**内容闸门**。
///
/// 为什么需要它：SC:R 客户端 UI 恒为英文——地图名("Monkey Business"、"2002 AP+")、
/// 设置项("Top vs Bottom"、"Use Map Settings"、"Faster")、表头("Players/Game Name") 全会被 OCR 读到。
/// 而翻译提示词明确要求"纯英文/数字/符号原样保留"，于是 LLM 老老实实把英文原样吐回来，
/// 悬浮层就显示成"复述英文"——既白花 API 调用，又是纯噪声。
///
/// 规则：**含任一 Hangul 字符**才进翻译管线。混合行（如 "2002 AP+ 러시"）仍照翻，
/// 因为里面有韩文要处理；纯英文/纯数字/纯符号行直接跳过。
/// </summary>
public static class KoreanText
{
    /// <summary>文本里是否出现韩文字符。</summary>
    public static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsHangul(c)) return true;
        return false;
    }

    /// <summary>谚文相关 Unicode 区段：音节正文 + 初声/终声 Jamo + 兼容 Jamo + 扩展 A/B。</summary>
    public static bool IsHangul(char c)
        => (c >= '\uAC00' && c <= '\uD7A3')    // Hangul Syllables —— 实际韩文句子的绝对主体
        || (c >= '\u1100' && c <= '\u11FF')    // Hangul Jamo（构字用初/终声）
        || (c >= '\u3130' && c <= '\u318F')    // Hangul Compatibility Jamo（含旧式/转写符号）
        || (c >= '\uA960' && c <= '\uA97F')    // Hangul Jamo Extended-A
        || (c >= '\uD7B0' && c <= '\uD7FB');   // Hangul Jamo Extended-B
}
