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
                var ocr = new RapidOcr();
                try
                {
                    InitModels(ocr, det, cls, rec, keys, gpu: _device == OcrDevice.Gpu);
                }
                catch (Exception ex) when (_device == OcrDevice.Gpu)
                {
                    // GPU（DirectML）不可用（驱动/显卡/DirectML.dll 缺失）：回落 CPU 重来一次，
                    // 否则 _ready 永远 false、OCR 静默返回空结果，等于整个功能失效。
                    try { ocr.Dispose(); } catch { }
                    ocr = new RapidOcr();
                    try
                    {
                        InitModels(ocr, det, cls, rec, keys, gpu: false);
                        _onError?.Invoke($"OCR GPU（DirectML）初始化失败，已回落 CPU：{ex.Message}");
                    }
                    catch
                    {
                        try { ocr.Dispose(); } catch { }
                        throw;
                    }
                }

                // 初始化窗口内被 Dispose：不能把已销毁实例写回字段（否则 3 个 onnx session 永不释放）。
                if (Volatile.Read(ref _disposed) != 0)
                {
                    try { ocr.Dispose(); } catch { }
                    return;
                }
                _ocr = ocr;
                _ready = true;
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

    /// <summary>建 SessionOptions → 挂（或不挂）DML → InitModels，异常时确保 SessionOptions 释放。</summary>
    private void InitModels(RapidOcr ocr, string det, string cls, string rec, string keys, bool gpu)
    {
        var so = RapidOcr.GetDefaultSessionOptions(Math.Max(1, _numThreads));
        try
        {
            if (gpu) so.AppendExecutionProvider_DML(0); // GPU：DirectML（设备 0），免装 CUDA
            // CPU：不挂 DML provider → onnxruntime 默认走 CPU
            ocr.InitModels(det, cls, rec, keys, so);   // 第 5 参为 SessionOptions（挂 DML 与否由此定）
        }
        finally
        {
            so.Dispose();                             // SessionOptions 由调用方持有，InitModels 返回后即释放
        }
    }

    public Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default)
    {
        // 先判取消：Task.Run 预取消的 token 会直接返回已取消任务、不执行委托，但仍占着下面那个 +1，
        // _detects 永久多 1 → Dispose 白等 2s，且计数越积越多。
        if (ct.IsCancellationRequested) return Task.FromCanceled<OcrResult>(ct);
        var ocr = _ocr;
        if (ocr is null || !_ready || Volatile.Read(ref _disposed) != 0)
            return Task.FromResult(new OcrResult(Array.Empty<OcrLine>())); // 初始化未完成/已销毁 → 空（调用方下一拍重试）
        Interlocked.Increment(ref _detects);
        // 自增后再确认一次：Dispose 可能恰好在我们读 _ocr 与计数自增之间把 _detects 等成 0 并释放了模型。
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Decrement(ref _detects);
            return Task.FromResult(new OcrResult(Array.Empty<OcrLine>()));
        }
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
                        // 空文本/空几何（BoxPoints 缺失）会产生零尺寸矩形，令下游按位置认行的稳定器永不认领、
                        // 却占着一次 OCR 行计数。这里直接丢掉。
                        if (string.IsNullOrWhiteSpace(block.Text)) continue;
                        var rect = ToRect(block.BoxPoints);
                        if (rect.Width <= 0 || rect.Height <= 0) continue;
                        float conf = block.CharScores is { Length: > 0 }
                            ? block.CharScores.Average()
                            : 0f;
                        lines.Add(new OcrLine(block.Text, rect, conf));
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
