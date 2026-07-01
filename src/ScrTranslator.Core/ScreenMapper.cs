using System.Drawing;

namespace ScrTranslator.Core;

/// <summary>把帧内像素矩形映射为物理屏幕像素矩形（纯逻辑，可单测）。DIP 换算在 UI 层做。</summary>
public static class ScreenMapper
{
    public static RectangleF MapFrameToScreen(RectangleF frameBounds, Size frameSize, Rectangle windowScreenRect)
    {
        float sx = frameSize.Width == 0 ? 1f : (float)windowScreenRect.Width / frameSize.Width;
        float sy = frameSize.Height == 0 ? 1f : (float)windowScreenRect.Height / frameSize.Height;
        return new RectangleF(
            windowScreenRect.Left + frameBounds.X * sx,
            windowScreenRect.Top + frameBounds.Y * sy,
            frameBounds.Width * sx,
            frameBounds.Height * sy);
    }
}
