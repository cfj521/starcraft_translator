using System;
using ScrTranslator.Core.Scene;
using SkiaSharp;

namespace ScrTranslator.App.Capture;

/// <summary>
/// 把捕获帧（SkiaSharp 的 SKBitmap）零拷贝包成 Core 的 <see cref="FramePixels"/>。
/// Core 不引用图像库，所以"像素从哪来"这件事留在 App 侧；指纹算法本身在 Core，可单测。
/// 返回的视图直接指向位图缓冲，**不得比位图活得更久**——只在识别调用内同步使用。
/// </summary>
public static class FramePixelsSource
{
    /// <summary>BGRA→R/G/B 的字节偏移。捕获层固定用 Bgra8888（见 CaptureService.ToSkBitmap）。</summary>
    static (int R, int G, int B) ChannelOffsets(SKColorType type) => type switch
    {
        SKColorType.Bgra8888 => (2, 1, 0),
        SKColorType.Rgba8888 => (0, 1, 2),
        _ => throw new NotSupportedException($"场景识别不支持的颜色类型 {type}"),
    };

    public static unsafe FramePixels AsFramePixels(this SKBitmap frame)
    {
        var (r, g, b) = ChannelOffsets(frame.ColorType);
        IntPtr pixels = frame.GetPixels();
        // GetPixels() 为 IntPtr.Zero 时构造出的 Span 长度非零但指针为空，首次读取即
        // AccessViolationException（CLR 无法 catch，会直接杀进程）。必须显式拦截。
        if (pixels == IntPtr.Zero)
            throw new InvalidOperationException($"位图无像素缓冲（{frame.Width}x{frame.Height}, {frame.ColorType}），可能是空或已释放的 SKBitmap");
        // 用 RowBytes*Height 而非 ByteCount：后者在部分 Skia 版本会按列对齐算，容易少最后一行。
        var buffer = new Span<byte>((void*)pixels, frame.RowBytes * frame.Height);
        return new FramePixels(buffer, frame.Width, frame.Height, frame.RowBytes, frame.BytesPerPixel, r, g, b);
    }
}
