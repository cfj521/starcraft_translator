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
/// <summary>OCR 推理设备。Cpu = onnxruntime CPU（占内存少、稍慢、无 GPU 也能跑）；
/// Gpu = DirectML（onnxruntime 的 DML 执行提供者，甩到 GPU、CPU 基本不动、免装 CUDA）。</summary>
public enum OcrDevice { Cpu, Gpu }

public sealed record OcrSettings
{
    /// <summary>OCR 设备：CPU 或 GPU（DirectML）。默认 Cpu（安全：无 GPU 的机器也能跑，行为与旧版一致）。
    /// 用户在"识别"页选 GPU 后，OCR 甩到显卡、CPU 大降。改此值需重启应用（引擎单例、构造时定死设备）。</summary>
    public OcrDevice Device { get; init; } = OcrDevice.Cpu;

    /// <summary>场景检测轮询（毫秒）。默认 20：识别只读几块静态区做块均值比对（&lt;1ms），
    /// 20ms 一次开销可忽略，换来"切界面立刻跟上"——慢了就出现"人已在房间内、还在按老 ROI 翻"的窗口期。</summary>
    public int SceneDetectPollMs { get; init; } = 20;

    /// <summary>翻译轮询（毫秒）。默认 200：一轮 = 各 ROI 裁剪 + OCR + 翻译上屏。</summary>
    public int TranslatePollMs { get; init; } = 200;

    /// <summary>OCR（onnxruntime）intra-op 并行线程数。默认 4。
    /// RapidOcrNet.InitModels 的 numThread 默认 0 = 用全部核：20 核机上每次推理都把全核拉满，
    /// 是"一开翻译 CPU 全满"的根因（帧转换只占单核，对不上全核满载）。限到少量核后 CPU 大降，
    /// OCR 单次稍慢但仍远在轮询窗口内完成。OCR 引擎是单例、线程数在首次加载时定死，改此值需重启应用。</summary>
    public int OcrThreads { get; init; } = 4;

    // 曾有 ChatPollMs/OtherPollMs 两个值（按"场景里有没有聊天 ROI"二选一）：其他界面根本不翻译，
    // 给它单独配一个"翻译轮询"是无意义的假旋钮 → 收敛成上面这两个正交的轮询节拍。
    // 曾有 Upscale/Grayscale/Binarize/ChangeSensitivity 四字段：RapidOcrNet 不暴露这些旋钮、
    // 变化检测是精确文本比对无"灵敏度"可言；实测(原生裁条直读 avgConf .987 vs 2x放大 .982)
    // 证明原生分辨率放大无收益 → 诚实移除，避免假设置。旧 settings.json 多余字段反序列化时自动忽略。
}
public sealed record AppearanceSettings
{
    /// <summary>悬浮层字体族名。默认 Kostar = 内嵌的原版 SC:R 字体（拉丁复刻原字体、谚文全套、无中文，
    /// 中文回退系统默认字体，与游戏内观感一致）。经 pack URI 引用，见 OverlayWindow.ResolveFontFamily。</summary>
    public string FontFamily { get; init; } = "Kostar";
    public int FontSize { get; init; } = 18;
    /// <summary>译文字宽缩放（0.7~1.3，默认 1.0＝正常）。OverlayWindow 对译文的 TextBlock 套水平 LayoutTransform
    /// （ScaleY=1 高度不变），只影响译文行（双行模式的原文不拉伸），底衬 Border 会自适应译文宽度。</summary>
    public double FontWidthScale { get; init; } = 1.0;
    /// <summary>实际字宽缩放（0.7~1.3 钳制）。渲染层用它，避免各处各自 clamp。</summary>
    public double WidthScale() => Math.Clamp(FontWidthScale, 0.7, 1.3);
    /// <summary>译文文字颜色（WPF 可解析的英文色名或 hex）。默认 #13FF1D = 从游戏对局内聊天截图
    /// （docs\scenes pictures\ingame-w-1.png）采样的聊天正文绿色核心值（最亮像素 19,255,29）。
    /// 存英文色名/hex 而非中文——ColorConverter 不认中文色名。解析见 OverlayWindow.ResolveFontColor。</summary>
    public string FontColor { get; init; } = "#13FF1D";
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
    public const uint VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A, VK_F12 = 0x7B;
    public const uint MOD_NONE = 0x0, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
}
public static class HotkeyDefaults
{
    /// <summary>全部热键：F9 开/关翻译、Ctrl+F9 区域校准设置、F7 设置、F8 切 DEBUG 面板、Ctrl+F8 复制诊断、
    /// F6 截图保存。用户可重绑到 F6–F9（可加 Ctrl/Alt/Shift）。悬浮层显隐 / 翻译玩家ID / 暂停 已改为纯配置项，不再占热键。</summary>
    public static IReadOnlyDictionary<string, HotkeyBinding> Map { get; } =
        new Dictionary<string, HotkeyBinding>
        {
            ["ToggleTranslate"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F9),
            ["Calibrate"]       = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9),
            ["OpenConfig"]      = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["ToggleDebug"]     = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8),
            ["CopyDebug"]       = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F8),
            ["Screenshot"]      = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F6),
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
