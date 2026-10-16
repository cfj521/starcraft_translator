using System.Drawing;
using System.Threading;
using RapidOcrNet;
using SkiaSharp;
using Microsoft.ML.OnnxRuntime;
using ScrTranslator.Core.Settings;
using OcrResult = ScrTranslator.Core.Ocr.OcrResult;

namespace ScrTranslator.Core.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly string _modelsDir;
    private readonly int _numThreads;
    private readonly OcrDevice _device;
    private readonly Action<string>? _onError;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RapidOcr? _ocr;          // 后台初始化才创建（RapidOcr + InitModels 加载 3 个 onnx，较重）
    private volatile bool _ready;    // 后台初始化完成后置 true；此前 RecognizeAsync 返回空结果（调用方下一拍重试）
    private int _detects;            // 正在跑的 Detect 数（Dispose 等它归零再销毁，避免销毁被翻译帧正在用的模型）
    private int _disposed;           // Dispose 幂等标志（Interlocked 保证只真正销毁一次）

    /// <param name="modelsDir">模型目录（det/rec/cls onnx + dict.txt）。</param>
    /// <param name="numThreads">onnxruntime intra-op 并行线程数。≤0 视为 1（避免 onnxruntime 把 0 解释成"全核"拉满 CPU）。</param>
    /// <param name="device">推理设备。Cpu = onnxruntime CPU（不挂 DML provider）；Gpu = DirectML（甩显卡、免装 CUDA）。引擎单例、构造时定死设备，改需重启。</param>
    /// <param name="onError">后台初始化失败上报。缺模型文件仍在构造同步快速失败（File.Exists 很轻、不阻塞）。</param>
    public PaddleOcrEngine(string modelsDir, int numThreads = 0, OcrDevice device = OcrDevice.Cpu, Action<string>? onError = null)
    {
        _modelsDir = modelsDir;
        _numThreads = numThreads;
        _device = device;
        _onError = onError;

        // 缺文件是硬错误（缺一个就不能用），同步快速失败（File.Exists 很轻，不阻塞调用线程/UI）。
        string det = Path.Combine(modelsDir, "det.onnx");
        string rec = Path.Combine(modelsDir, "rec.onnx");
        string cls = Path.Combine(modelsDir, "cls.onnx");
        string keys = Path.Combine(modelsDir, "dict.txt");
        foreach (var f in new[] { det, rec, cls, keys })
            if (!File.Exists(f)) throw new FileNotFoundException("缺模型文件", f);

        InitBackground(); // 重活（RapidOcr + InitModels 加载 onnx）甩后台线程，不卡调用线程（启动时 UI 线程）
    }

    /// <summary>后台初始化完成（成功或失败）后完成。测试/调用方可 await 它再调用 RecognizeAsync，避免拿到初始化中的空结果。</summary>
    public Task Ready => _readyTcs.Task;

    /// <summary>是否已就绪（后台初始化完成）。未就绪时 RecognizeAsync 返回空结果。</summary>
    public bool IsReady => _ready;

    private void InitBackground()
    {
        string det = Path.Combine(_modelsDir, "det.onnx");
        string rec = Path.Combine(_modelsDir, "rec.onnx");
        string cls = Path.Combine(_modelsDir, "cls.onnx");
        string keys = Path.Combine(_modelsDir, "dict.txt");
        Task.Run(() =>
        {
            try
            {
                // 官方推荐创建 SessionOptions 的方式：GetDefaultSessionOptions 自带 ORT_ENABLE_EXTENDED 等合理默认，
                // 并把 Inter/Intra-op 线程钉在 numThreads（≤0 兜底 1，避免 onnxruntime 把 0 解释成"全核"拉满 CPU）。
                var so = RapidOcr.GetDefaultSessionOptions(Math.Max(1, _numThreads));
                try
                {
                    if (_device == OcrDevice.Gpu)
                        so.AppendExecutionProvider_DML(0);            // GPU：DirectML（设备 0），免装 CUDA
                    // CPU：不挂 DML provider → onnxruntime 默认走 CPU
                    var ocr = new RapidOcr();
                    ocr.InitModels(det, cls, rec, keys, so);         // 第 5 参为 SessionOptions（挂 DML 与否由此定）
                    _ocr = ocr;
                    _ready = true;
                }
                finally
                {
                    so.Dispose();                                     // SessionOptions 由调用方持有，InitModels 返回后即释放
                }
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"OCR 引擎初始化失败（后台）: {ex.Message}");
            }
            finally
            {
                _readyTcs.TrySetResult(); // 初始化结束（成功或失败）→ Ready 完成
            }
        });
    }

    public Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default)
    {
        var ocr = _ocr;
        if (ocr is null || !_ready) return Task.FromResult(new OcrResult(Array.Empty<OcrLine>())); // 初始化未完成 → 空（启动窗口，调用方下一拍重试）
        Interlocked.Increment(ref _detects);
        return Task.Run(() =>
        {
            try
            {
                var options = RapidOcrOptions.Default with { DoAngle = true };
                var res = ocr.Detect(image, options);

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
            }
            finally { Interlocked.Decrement(ref _detects); }
        }, ct);
    }

    private static RectangleF ToRect(SKPointI[] pts)
    {
        if (pts is null || pts.Length == 0)
            return RectangleF.Empty;
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        float maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    public void Dispose()
    {
        // 幂等：只真正销毁一次（host.Dispose 与显式调用不会双销毁）。
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // 等正在跑的 Detect 结束（它们很短，~ms），避免销毁正在被翻译帧用的模型。最多等 2s，超时也销毁（防卡死退出）。
        var deadline = Environment.TickCount + 2000;
        while (Interlocked.CompareExchange(ref _detects, 0, 0) > 0 && Environment.TickCount - deadline < 2000)
            Thread.Sleep(5);
        var ocr = _ocr;
        _ocr = null;
        if (ocr is not null)
            Task.Run(() => { try { ocr.Dispose(); } catch { } }); // 重活（释放 onnx session）甩后台线程，不阻塞调用线程（退出时 UI 线程）
    }
}
