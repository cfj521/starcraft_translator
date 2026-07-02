namespace ScrTranslator.Core.Translation;
public enum TranslationEngine { DashScope, ChatGpt }
public sealed record TranslationSettings
{
    public TranslationEngine Active { get; init; } = TranslationEngine.DashScope;
    public string DashScopeModel { get; init; } = "qwen-flash";
    public string ChatGptModel { get; init; } = "gpt-5";
    public int TimeoutSeconds { get; init; } = 10;
    public bool TwoLevel { get; init; } = false;
    public TranslationEngine FastEngine { get; init; } = TranslationEngine.DashScope;
    public string FastModel { get; init; } = "qwen-flash";
    public bool TranslateChat { get; init; } = true;
    public bool TranslateRoomName { get; init; } = true;
    public bool TranslateMapInfo { get; init; } = true;
    public bool TranslatePlayerId { get; init; } = false;
    public static TranslationSettings Default => new();
}
