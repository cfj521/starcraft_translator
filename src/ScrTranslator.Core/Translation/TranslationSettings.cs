namespace ScrTranslator.Core.Translation;
public enum TranslationEngine { DashScope, ChatGpt }
public sealed record TranslationSettings
{
    public TranslationEngine Active { get; init; } = TranslationEngine.DashScope;
    public string DashScopeModel { get; init; } = "qwen-flash";
    public string ChatGptModel { get; init; } = "gpt-5";
    public int TimeoutSeconds { get; init; } = 10;
    public static TranslationSettings Default => new();
}
