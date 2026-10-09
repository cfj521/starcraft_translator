namespace ScrTranslator.Core.Translation;
public enum TranslationEngine { DashScope, ChatGpt }
public sealed record TranslationSettings
{
    public TranslationEngine Active { get; init; } = TranslationEngine.DashScope;
    public string DashScopeModel { get; init; } = "qwen-flash";
    public string ChatGptModel { get; init; } = "gpt-5";
    public int TimeoutSeconds { get; init; } = 10;
    // 曾有 TwoLevel / FastEngine / FastModel 三字段（两级翻译：快引擎出临时态、主力引擎定稿）。
    // 游戏聊天短句是很窄的翻译任务，低价模型单发就够好，两级只让每个未命中批次多打一次请求，
    // 还带来一套"临时态"视觉与状态分支 → 整体移除。旧 settings.json 里的这三个字段反序列化时自动忽略。
    public bool TranslateChat { get; init; } = true;
    public bool TranslateRoomName { get; init; } = true;
    public bool TranslateMapInfo { get; init; } = true;
    public bool TranslatePlayerId { get; init; } = false;
    public static TranslationSettings Default => new();
}
