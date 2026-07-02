using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Settings;

public sealed record GeneralSettings
{
    public string TargetProcess { get; init; } = "StarCraft";
    public bool AutoStart { get; init; } = false;
    public bool StartMinimized { get; init; } = true;
    public bool AutoTranslateOnLaunch { get; init; } = false;
}
public sealed record OcrSettings
{
    public int ChatPollMs { get; init; } = 300;
    public int OtherPollMs { get; init; } = 800;
    public bool Upscale { get; init; } = true;
    public bool Grayscale { get; init; } = true;
    public bool Binarize { get; init; } = false;
    public int ChangeSensitivity { get; init; } = 5; // 0..10
}
public sealed record AppearanceSettings
{
    public string FontFamily { get; init; } = "Microsoft YaHei";
    public int FontSize { get; init; } = 18;
    public double Opacity { get; init; } = 0.8;
    public bool ShowOriginalAndTranslation { get; init; } = false;
}
public sealed record HotkeyBinding(uint Modifiers, uint Vk);

public static class HotkeyKeys
{
    public const uint VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;
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
