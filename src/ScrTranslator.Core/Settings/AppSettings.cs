using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Settings;

public sealed record GeneralSettings
{
    public string TargetProcess { get; init; } = "StarCraft";
    public bool AutoStart { get; init; } = false;
    public bool StartMinimized { get; init; } = true;
    public bool AutoTranslateOnLaunch { get; init; } = false;
    /// <summary>DEBUG 模式：左上角显示诊断面板（场景识别/帧/ROI/翻译状态），便于实机排障。默认关。</summary>
    public bool DebugMode { get; init; } = false;
}
public sealed record OcrSettings
{
    /// <summary>场景检测轮询（毫秒）。默认 200：识别只读几块静态区做块均值比对（&lt;1ms），
    /// 200ms 一次开销可忽略，换来"切界面立刻跟上"——慢了就出现"人已在房间内、还在按老 ROI 翻"的窗口期。</summary>
    public int SceneDetectPollMs { get; init; } = 200;

    /// <summary>翻译轮询（毫秒）。默认 500：一轮 = 各 ROI 裁剪 + OCR + 翻译上屏。</summary>
    public int TranslatePollMs { get; init; } = 500;

    // 曾有 ChatPollMs/OtherPollMs 两个值（按"场景里有没有聊天 ROI"二选一）：其他界面根本不翻译，
    // 给它单独配一个"翻译轮询"是无意义的假旋钮 → 收敛成上面这两个正交的轮询节拍。
    // 曾有 Upscale/Grayscale/Binarize/ChangeSensitivity 四字段：RapidOcrNet 不暴露这些旋钮、
    // 变化检测是精确文本比对无"灵敏度"可言；实测(原生裁条直读 avgConf .987 vs 2x放大 .982)
    // 证明原生分辨率放大无收益 → 诚实移除，避免假设置。旧 settings.json 多余字段反序列化时自动忽略。
}
public sealed record AppearanceSettings
{
    public string FontFamily { get; init; } = "Microsoft YaHei";
    public int FontSize { get; init; } = 18;
    public double Opacity { get; init; } = 0.8;

    /// <summary>文字底衬（每条译文一块圆角黑底）的不透明度。默认 0.8。
    /// 为什么需要：游戏画面明暗复杂，纯白字+投影在浅色/高亮背景上几乎读不出来。
    /// 设 0 = 不画底衬（回到旧观感）。整体"不透明度"仍统一作用在整条（文字+底衬）上。</summary>
    public double BackplateOpacity { get; init; } = 0.8;

    public bool ShowOriginalAndTranslation { get; init; } = false;

    /// <summary>底衬实际 alpha（0–1 钳制）。渲染层用它决定画不画、画多浓，避免各处各自 clamp。</summary>
    public double BackplateAlpha() => Math.Clamp(BackplateOpacity, 0d, 1d);
}
public sealed record HotkeyBinding(uint Modifiers, uint Vk);

public static class HotkeyKeys
{
    public const uint VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;
    public const uint MOD_NONE = 0x0, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
}
public static class HotkeyDefaults
{
    public static IReadOnlyDictionary<string, HotkeyBinding> Map { get; } =
        new Dictionary<string, HotkeyBinding>
        {
            ["ToggleTranslate"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F9),
            ["ToggleOverlay"]   = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F10),
            ["Calibrate"]       = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8),
            ["OpenConfig"]      = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9),
            ["TogglePlayerId"]  = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["TogglePause"]     = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F11),
            ["ToggleDebug"]     = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F6),
            ["CopyDebug"]       = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F6),
        };
}

public sealed record AppSettings
{
    public GeneralSettings General { get; init; } = new();
    public TranslationSettings Translation { get; init; } = TranslationSettings.Default;
    public OcrSettings Ocr { get; init; } = new();
    public AppearanceSettings Appearance { get; init; } = new();
    public IReadOnlyDictionary<string, HotkeyBinding> Hotkeys { get; init; } = HotkeyDefaults.Map;
}
