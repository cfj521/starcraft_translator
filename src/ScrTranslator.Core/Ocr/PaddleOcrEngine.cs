using System.Drawing;
using RapidOcrNet;
using SkiaSharp;
using OcrResult = ScrTranslator.Core.Ocr.OcrResult;

namespace ScrTranslator.Core.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly RapidOcr _ocr;

    public PaddleOcrEngine(string modelsDir)
    {
        string det = Path.Combine(modelsDir, "det.onnx");
        string rec = Path.Combine(modelsDir, "rec.onnx");
        string cls = Path.Combine(modelsDir, "cls.onnx");
        string keys = Path.Combine(modelsDir, "dict.txt");
        foreach (var f in new[] { det, rec, cls, keys })
            if (!File.Exists(f)) throw new FileNotFoundException("缺模型文件", f);

        _ocr = new RapidOcr();
        try { _ocr.InitModels(detPath: det, clsPath: cls, recPath: rec, keysPath: keys); }
        catch { _ocr.Dispose(); throw; }
    }

    public Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default)
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
            return new OcrResult(lines);
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
