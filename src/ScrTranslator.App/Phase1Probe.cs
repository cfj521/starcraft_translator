using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using SkiaSharp;

namespace ScrTranslator.App;

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly FileLogger _log;
    private readonly RoiConfig _cfg;
    private const string Scene = "custom_games_listing";
    private DateTime _lastShot = DateTime.MinValue;
    private volatile bool _busy;

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, string roisJsonPath)
    {
        _capture = capture; _ocr = ocr; _log = log;
        _cfg = RoiStore.Load(roisJsonPath);
        _capture.FrameArrived += OnFrame;
    }

    public void Start()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) { _log.Log("[probe] 未找到 StarCraft 窗口"); return; }
        if (!_capture.Start(hwnd)) _log.Log("[probe] 捕获启动失败");
        else _log.Log("[probe] 已开始捕获");
    }

    public void Stop() { _capture.Stop(); _log.Log("[probe] 已停止"); }

    private async void OnFrame(SKBitmap frame)
    {
        if (_busy || (DateTime.Now - _lastShot).TotalMilliseconds < 400) { frame.Dispose(); return; }
        _busy = true; _lastShot = DateTime.Now;
        try
        {
            var bucket = RoiStore.SelectBucket(_cfg, Scene, frame.Width, frame.Height);
            foreach (var roi in bucket.Rois)
            {
                Rectangle px = RoiStore.ToPixels(roi.Rect, frame.Width, frame.Height);
                if (px.Width <= 0 || px.Height <= 0) continue;
                using var sub = new SKBitmap(px.Width, px.Height);
                if (!frame.ExtractSubset(sub, new SKRectI(px.Left, px.Top, px.Right, px.Bottom)))
                {
                    _log.Log($"[{roi.Name}] 裁剪失败 rect={px}");
                    continue;
                }
                var res = await _ocr.RecognizeAsync(sub);
                foreach (var line in res.Lines)
                    _log.Log($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                if (res.Lines.Count == 0)
                    _log.Log($"[{roi.Name}] (空)");
            }
        }
        catch (Exception ex) { _log.Log($"[probe] 异常: {ex.Message}"); }
        finally { frame.Dispose(); _busy = false; }
    }

    // 仅退订帧回调；CaptureService 是 DI 单例，其 Stop/Dispose 由容器管理（有意设计）。
    public void Dispose() { _capture.FrameArrived -= OnFrame; }
}
