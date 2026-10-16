using ScrTranslator.Core.Ocr;
using SkiaSharp;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class PaddleOcrEngineTests
{
    private static string ModelsDir => Path.Combine(AppContext.BaseDirectory, "models");
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "assets", "korean-sample.png");

    [Fact]
    public async Task RecognizeAsync_ReadsKoreanFromSampleImage()
    {
        Assert.True(File.Exists(Path.Combine(ModelsDir, "rec.onnx")), "缺韩文模型");
        using var engine = new PaddleOcrEngine(ModelsDir);
        await engine.Ready; // 后台初始化完成后再识别，否则拿到空结果
        using var bmp = SKBitmap.Decode(SamplePath);
        var result = await engine.RecognizeAsync(bmp);
        string all = string.Concat(result.Lines.Select(l => l.Text));
        Assert.Contains("로", all);
        Assert.All(result.Lines, l => Assert.True(l.Confidence > 0.6f));
    }
}
