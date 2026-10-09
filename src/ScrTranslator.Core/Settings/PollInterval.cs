namespace ScrTranslator.Core.Settings;

/// <summary>
/// 轮询频率解析（Phase 5 接线）：管线是"单捕获循环→逐 ROI OCR"，无法对同一帧按 ROI 各用各的周期，
/// 故按**场景级**语义取值——场景含 Chat 类型 ROI 时用 <see cref="OcrSettings.ChatPollMs"/>（聊天要快），
/// 否则用 <see cref="OcrSettings.OtherPollMs"/>；结果钳制到 [<see cref="MinMs"/>, <see cref="MaxMs"/>]。
/// </summary>
public static class PollInterval
{
    public const int MinMs = 150;
    public const int MaxMs = 5000;

    public static int Resolve(OcrSettings ocr, bool hasChat)
        => Math.Clamp(hasChat ? ocr.ChatPollMs : ocr.OtherPollMs, MinMs, MaxMs);
}
