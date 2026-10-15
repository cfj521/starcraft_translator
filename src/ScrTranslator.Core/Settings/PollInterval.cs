namespace ScrTranslator.Core.Settings;

/// <summary>
/// 两个正交轮询节拍（钳制到 [<see cref="MinMs"/>, <see cref="MaxMs"/>]）：
///  - <see cref="SceneDetect"/> 场景检测：读几块静态区做块均值比对，很便宜，跑得快才能立刻跟上界面切换；
///  - <see cref="Translate"/>   翻译：ROI 裁剪 + OCR + LLM，贵，按自己的节拍跑。
/// 两者不再共用一个值（旧设计按"场景里有没有聊天 ROI"二选一，而其他界面根本不翻译，那个旋钮没意义）。
/// </summary>
public static class PollInterval
{
    public const int MinMs = 10;
    public const int MaxMs = 1000;

    public static int SceneDetect(OcrSettings ocr) => Math.Clamp(ocr.SceneDetectPollMs, MinMs, MaxMs);

    public static int Translate(OcrSettings ocr) => Math.Clamp(ocr.TranslatePollMs, MinMs, MaxMs);

    /// <summary>
    /// 捕获侧的帧转换节流上限。必须取两者中**更快**的那个：帧转换是识别与翻译共用的上游闸门，
    /// 若按翻译的 300ms 节流，识别就算配了 20ms 也只能每 300ms 拿到一帧，被上游拖平。
    /// </summary>
    public static int CaptureFloor(OcrSettings ocr) => Math.Min(SceneDetect(ocr), Translate(ocr));
}
