using System.Drawing;
using SkiaSharp;

namespace ScrTranslator.Core.Ocr;

public record OcrLine(string Text, RectangleF Bounds, float Confidence);
public record OcrResult(IReadOnlyList<OcrLine> Lines);

public interface IOcrEngine
{
    /// <summary>引擎是否已就绪（后台初始化完成）。未就绪时 RecognizeAsync 返回空结果；调用方可据此决定是否开轮询。</summary>
    bool IsReady { get; }

    Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default);
}
