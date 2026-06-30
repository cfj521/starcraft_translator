namespace ScrTranslator.Core.Interop;

/// <summary>
/// Win32 扩展窗口样式中与"点击穿透"相关的位及其计算（纯逻辑，可单测）。
/// 实际的 GetWindowLong/SetWindowLong 调用在 App 层完成。
/// </summary>
public static class WindowStyles
{
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>在已有扩展样式上叠加点击穿透所需位；幂等。</summary>
    public static int ApplyClickThrough(int exStyle)
        => exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED;

    /// <summary>移除点击穿透位（为后续校准模式预留）。</summary>
    public static int RemoveClickThrough(int exStyle)
        => exStyle & ~(WS_EX_TRANSPARENT | WS_EX_LAYERED);
}
