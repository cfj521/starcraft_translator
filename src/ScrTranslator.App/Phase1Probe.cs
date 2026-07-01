using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Translation;
using SkiaSharp;
using Windows.Graphics.Capture;

namespace ScrTranslator.App;

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly FileLogger _log;
    private readonly OverlayWindow _overlay;
    private readonly RoiConfig _cfg;
    private readonly TranslationService _translator;
    private readonly SettingsHolder _settings;
    private const string Scene = "custom_games_listing";
    private DateTime _lastShot = DateTime.MinValue;
    private int _busy; // 0=空闲 1=处理中
    private string _lastJoined = ""; // 变化检测：上一帧全部 ROI 韩文拼接

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, OverlayWindow overlay,
        string roisJsonPath, TranslationService translator, SettingsHolder settings)
    {
        _capture = capture; _ocr = ocr; _log = log; _overlay = overlay;
        _cfg = RoiStore.Load(roisJsonPath);
        _translator = translator; _settings = settings;
        _capture.FrameArrived += OnFrame;
    }

    public bool Start()
    {
        try
        {
            _log.Log("[probe] Start 被调用");
            bool supported = GraphicsCaptureSession.IsSupported();
            _log.Log($"[probe] GraphicsCapture.IsSupported={supported}");
            var hwnd = WindowFinder.FindByProcess("StarCraft");
            _log.Log($"[probe] FindByProcess(StarCraft) hwnd={hwnd}");
            if (hwnd == IntPtr.Zero) { _log.Log("[probe] 未找到 StarCraft 窗口"); return false; }
            if (!_capture.Start(hwnd)) { _log.Log("[probe] 捕获启动失败"); return false; }
            _log.Log("[probe] 已开始捕获");
            return true;
        }
        catch (Exception ex)
        {
            _log.Log($"[probe] Start 异常: {ex}");
            return false;
        }
    }

    /// <summary>复位变化检测：切引擎/登录成功后调用，让静止画面也立即用新引擎/新登录态重译上屏。</summary>
    public void ResetChangeDetection() => _lastJoined = "";

    public void Stop()
    {
        _capture.Stop();
        _lastJoined = ""; // 复位变化检测，下次 Start 后首帧总会重新翻译上屏
        _log.Log("[probe] 已停止");
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));
    }

    private async void OnFrame(SKBitmap frame)
    {
        if ((DateTime.Now - _lastShot).TotalMilliseconds < 400
            || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            frame.Dispose();
            return;
        }
        _lastShot = DateTime.Now;
        try
        {
            var frameSize = new Size(frame.Width, frame.Height);
            var winRect = NativeMethods.GetWindowScreenRect(_capture.Hwnd);
            if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0 || winRect.Left <= -30000 || winRect.Top <= -30000)
            {
                _log.Log("[probe] 窗口矩形无效/最小化，跳过本帧上屏");
                return; // finally 仍会 Dispose(frame) 并复位 _busy
            }
            var bucket = RoiStore.SelectBucket(_cfg, Scene, frame.Width, frame.Height);
            var koLines = new List<string>();
            var metas = new List<(RectangleF frameBounds, string roiName)>();
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
                {
                    _log.Log($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                    var frameBounds = new RectangleF(
                        line.Bounds.X + px.Left, line.Bounds.Y + px.Top,
                        line.Bounds.Width, line.Bounds.Height);
                    koLines.Add(line.Text);
                    metas.Add((frameBounds, roi.Name));
                }
                if (res.Lines.Count == 0)
                    _log.Log($"[{roi.Name}] (空)");
            }

            // 变化检测：本帧全部 ROI 韩文与上一帧完全相同则跳过翻译与上屏（省 API 费用，减少闪烁）。
            var joined = string.Join("\n", koLines);
            if (joined == _lastJoined) return; // finally 仍会 Dispose(frame) 并复位 _busy

            var engine = _settings.Current.Active;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<string> zh;
            if (koLines.Count > 0)
            {
                // 超时保护(spec §6)：超时/失败 → TranslationService 降级回原文，避免网络挂起时 overlay 冻结。
                int timeout = Math.Max(1, _settings.Current.TimeoutSeconds);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                zh = await _translator.TranslateAsync(koLines, cts.Token);
            }
            else zh = Array.Empty<string>();
            sw.Stop();

            var items = new List<OverlayItem>();
            for (int k = 0; k < koLines.Count; k++)
            {
                var (frameBounds, roiName) = metas[k];
                var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
                items.Add(new OverlayItem(zh[k], scr.X, scr.Y));
                _log.Log($"[{roiName}][{k}] \"{koLines[k]}\" -> \"{zh[k]}\" engine={engine} {sw.ElapsedMilliseconds}ms");
            }
            // 翻译与映射均成功后才提交"已展示"状态：若上面任一步抛出(被外层 catch)，
            // 本帧不标记为已展示，下一帧同内容仍会重试，避免误判"未变"导致永不上屏。
            _lastJoined = joined;
            _log.Log($"[probe] 上屏 {items.Count} 行");
            _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(items));
        }
        catch (Exception ex) { _log.Log($"[probe] 异常: {ex.Message}"); }
        finally { frame.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose() { _capture.FrameArrived -= OnFrame; }
}
