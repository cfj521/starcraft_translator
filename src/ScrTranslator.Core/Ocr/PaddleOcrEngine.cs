using System.Drawing;
using RapidOcrNet;
using SkiaSharp;

namespace ScrTranslator.Core.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly RapidOcr _ocr;

    public PaddleOcrEngine(string modelsDir)
    {
        _ocr = new RapidOcr();

        string detPath = Path.Combine(modelsDir, "det.onnx");
        string clsPath = Path.Combine(modelsDir, "cls.onnx");
        string recPath = Path.Combine(modelsDir, "rec.onnx");
        string keysPath = Path.Combine(modelsDir, "dict.txt");

        if (!File.Exists(clsPath))
            throw new FileNotFoundException($"cls.onnx 未找到，请将分类模型放到 {modelsDir}", clsPath);
        _ocr.InitModels(
            detPath: detPath,
            clsPath: clsPath,
            recPath: recPath,
            keysPath: keysPath);
    }

    public Task<Core.Ocr.OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var options = RapidOcrOptions.Default with { DoAngle = true };
            var res = _ocr.Detect(image, options);

            var lines = new List<OcrLine>();
            if (res?.TextBlocks is not null)
            {
                foreach (var block in res.TextBlocks)
                {
                    float conf = block.CharScores is { Length: > 0 }
                        ? block.CharScores.Average()
                        : 0f;
                    lines.Add(new OcrLine(block.Text, ToRect(block.BoxPoints), conf));
                }
            }
            return new Core.Ocr.OcrResult(lines);
        }, ct);

    private static RectangleF ToRect(SKPointI[] pts)
    {
        if (pts is null || pts.Length == 0)
            return RectangleF.Empty;
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        float maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    public void Dispose() => _ocr.Dispose();
}
