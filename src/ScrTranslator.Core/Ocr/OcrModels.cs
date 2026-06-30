using System.Drawing;
using SkiaSharp;

namespace ScrTranslator.Core.Ocr;

public record OcrLine(string Text, RectangleF Bounds, float Confidence);
public record OcrResult(IReadOnlyList<OcrLine> Lines);

public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default);
}
