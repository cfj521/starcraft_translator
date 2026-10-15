using System.Drawing;

namespace ScrTranslator.Core;

/// <summary>相对内容区的矩形（0~1 比例）。跨分辨率通用：运行时乘以当前帧宽高即得物理像素。</summary>
public record RelRect(double X, double Y, double W, double H);

public static class RelRectExtensions
{
    /// <summary>相对矩形 → 物理像素矩形，钳制进画面（与旧 RoiStore.ToPixels 同逻辑）。</summary>
    public static Rectangle ToPixels(this RelRect r, int width, int height)
    {
        int x = (int)Math.Round(r.X * width);
        int y = (int)Math.Round(r.Y * height);
        int w = (int)Math.Round(r.W * width);
        int h = (int)Math.Round(r.H * height);
        x = Math.Clamp(x, 0, width);
        y = Math.Clamp(y, 0, height);
        w = Math.Clamp(w, 0, width - x);
        h = Math.Clamp(h, 0, height - y);
        return new Rectangle(x, y, w, h);
    }
}
