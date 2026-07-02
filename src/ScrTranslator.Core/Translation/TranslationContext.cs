namespace ScrTranslator.Core.Translation;

/// <summary>翻译上下文：场景 + 术语提示（已定界）+ 最近上下文（暂占位，恒 null）。</summary>
public sealed record TranslationContext(
    string Scene,
    string? GlossaryHint,
    IReadOnlyList<string>? RecentContext = null);
