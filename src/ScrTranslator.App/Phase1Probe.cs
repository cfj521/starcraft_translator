using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
using SkiaSharp;
using Windows.Graphics.Capture;

namespace ScrTranslator.App;

public sealed record ProbeStartInfo(ProbeStart Status, string ProcessName);
public enum ProbeStart { Ok, CaptureUnsupported, WindowNotFound, NoRoi, CaptureFailed }

/// <summary>Phase 5 异常恢复事件：看门狗检测到的状态转移，App 订阅后转托盘气泡。</summary>
public enum ProbeNotice { WindowLost, Reconnected, Minimized, Restored, ResolutionChanged }

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly FileLogger _log;
    private readonly OverlayWindow _overlay;
    private RoiConfig _cfg;
    private readonly TieredTranslator _tiered;
    private readonly SettingsHolder _settings;
    private readonly string _glossaryHint;
    private readonly string _roisJsonPath;
    private const string Scene = "custom_games_listing";
    private DateTime _lastShot = DateTime.MinValue;
    private int _busy; // 0=空闲 1=处理中
    private string _lastJoined = ""; // 变化检测：上一帧全部 ROI 韩文拼接
    private bool _roiAvailable; // Scene 是否存在且 bucket 非空，避免每帧重复判断/抛异常；构造时算一次，ReloadRois 可刷新
    private bool _hasChatRoi;   // 场景级"含 Chat ROI"，决定轮询用 ChatPollMs 还是 OtherPollMs；构造/ReloadRois 时算
    private bool _roiMissingLogged; // 确保"ROI 缺失"只记一次日志，避免刷屏
    private bool _paused; // F11 暂停/继续：暂停时跳过 OnFrame 处理，不动 CaptureService（与 F9 的 Start/Stop 互不干扰）

    // ── Phase 5 异常恢复：看门狗(2s) 检测窗口丢失/最小化/分辨率切换 ──
    public event Action<ProbeNotice, string>? Notice;
    private System.Threading.Timer? _watchdog;
    private bool _windowLost;   // 已就"窗口丢失"通知过（重连成功后复位）
    private bool _wasIconic;    // 上一轮询周期是否最小化（转移时才通知）
    private bool _resNotified;  // 当前分辨率与 bucket 不匹配的提示只发一次（换回/重校准后复位）

    /// <summary>目标进程名（设置驱动，默认 StarCraft）。Start 与看门狗重连共用。</summary>
    private string TargetProcess() => string.IsNullOrWhiteSpace(_settings.Current.General.TargetProcess)
        ? "StarCraft" : _settings.Current.General.TargetProcess.Trim();

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, OverlayWindow overlay,
        string roisJsonPath, TieredTranslator tiered, SettingsHolder settings, Glossary glossary)
    {
        _capture = capture; _ocr = ocr; _log = log; _overlay = overlay;
        _roisJsonPath = roisJsonPath;
        _cfg = RoiStore.Load(roisJsonPath);
        RefreshRoiDerivedFlags();
        _tiered = tiered; _settings = settings;
        _glossaryHint = PromptBuilder.BuildGlossaryHint(glossary.Entries);
        _capture.FrameArrived += OnFrame;
    }

    /// <summary>从 _cfg 计算场景级派生标志（ROI 可用性 / 是否含 Chat ROI）。ctor 与 ReloadRois 共用。</summary>
    private void RefreshRoiDerivedFlags()
    {
        _roiAvailable = _cfg.Scenes.TryGetValue(Scene, out var buckets) && buckets.Count > 0;
        // 场景级（跨该场景所有分辨率 bucket）判定：只要可能出现聊天区就用聊天轮询值。
        _hasChatRoi = _cfg.Scenes.TryGetValue(Scene, out var bs)
            && bs.Any(b => b.Rois.Any(r => RoiTypes.Parse(r.Type) == RoiType.Chat));
    }

    /// <summary>重新从磁盘加载 rois.json：校准窗口保存后由 App.xaml.cs 调用，使运行中的探测无需重启即可用上新/首次校准的 ROI。
    /// 单飞探测（每帧同步处理，OnFrame 靠 _busy 互斥），故直接整体重赋值 _cfg/_roiAvailable 已足够安全，无需加锁。</summary>
    public void ReloadRois()
    {
        _cfg = RoiStore.Load(_roisJsonPath);
        RefreshRoiDerivedFlags();
        _roiMissingLogged = false;
        _resNotified = false; // 校准保存=新 ROI 集，重校准提示允许再发一次
        ResetChangeDetection();
        _log.Log($"[probe] ROI 已重新加载，_roiAvailable={_roiAvailable} _hasChatRoi={_hasChatRoi}");
    }

    /// <summary>启动探测。返回具体失败原因供 UI 提示（首次引导：F9 后没反应要知道为什么）。</summary>
    public ProbeStartInfo Start()
    {
        string proc = TargetProcess();
        try
        {
            _log.Log("[probe] Start 被调用");
            if (!GraphicsCaptureSession.IsSupported())
            {
                _log.Log("[probe] GraphicsCapture.IsSupported=false");
                return new ProbeStartInfo(ProbeStart.CaptureUnsupported, proc);
            }
            if (!_roiAvailable)
            {
                _log.Log("[probe] rois.json 无当前场景 ROI，请先校准");
                return new ProbeStartInfo(ProbeStart.NoRoi, proc);
            }
            var hwnd = WindowFinder.FindByProcess(proc);
            _log.Log($"[probe] FindByProcess({proc}) hwnd={hwnd}");
            if (hwnd == IntPtr.Zero)
            {
                _log.Log($"[probe] 未找到 {proc} 窗口");
                return new ProbeStartInfo(ProbeStart.WindowNotFound, proc);
            }
            if (!_capture.Start(hwnd))
            {
                _log.Log("[probe] 捕获启动失败");
                return new ProbeStartInfo(ProbeStart.CaptureFailed, proc);
            }
            // 异常恢复看门狗：2s 周期检查窗口存活/最小化；丢失后自动重试重连（游戏重开/换窗口无需手动 F9）。
            _windowLost = false; _wasIconic = false; _resNotified = false;
            _watchdog ??= new System.Threading.Timer(WatchdogTick, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _watchdog.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            _log.Log("[probe] 已开始捕获（看门狗已启动）");
            return new ProbeStartInfo(ProbeStart.Ok, proc);
        }
        catch (Exception ex)
        {
            _log.Log($"[probe] Start 异常: {ex}");
            return new ProbeStartInfo(ProbeStart.CaptureFailed, proc);
        }
    }

    /// <summary>看门狗（线程池执行）：窗口无效→释放旧捕获并尝试重连新句柄；最小化⇄还原→清屏/重译；均发事件给 UI。</summary>
    private void WatchdogTick(object? state)
    {
        if (_paused) return; // F11 暂停期间不做恢复动作（继续时若窗口已丢，由下一轮看门狗处理）
        try
        {
            var hwnd = _capture.Hwnd;
            bool alive = hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);
            if (!alive)
            {
                _capture.Stop(); // 幂等：释放挂在死句柄上的会话，避免 COM 悬挂
                if (!_windowLost)
                {
                    _windowLost = true; _wasIconic = false;
                    _log.Log("[probe] 看门狗：游戏窗口已丢失");
                    Notice?.Invoke(ProbeNotice.WindowLost,
                        $"游戏窗口已消失，翻译已暂停。检测到 {TargetProcess()} 重新开窗后将自动续译。");
                }
                var fresh = WindowFinder.FindByProcess(TargetProcess());
                if (fresh != IntPtr.Zero && _capture.Start(fresh))
                {
                    _windowLost = false;
                    ResetChangeDetection(); // 新窗口内容需重新 OCR/上屏
                    _log.Log($"[probe] 看门狗：已重连新窗口 hwnd={fresh}");
                    Notice?.Invoke(ProbeNotice.Reconnected, "已重新连接游戏窗口，翻译继续");
                }
                return;
            }
            bool iconic = NativeMethods.IsIconic(hwnd);
            if (iconic != _wasIconic)
            {
                _wasIconic = iconic;
                if (iconic)
                {
                    // 修复：最小化时旧译文会滞留悬浮在桌面上——立即清空；还原后 ResetChangeDetection 重译新位置。
                    ClearOverlay();
                    _log.Log("[probe] 看门狗：游戏最小化，悬浮已清空");
                    Notice?.Invoke(ProbeNotice.Minimized, "游戏已最小化，翻译暂停（还原后自动继续）");
                }
                else
                {
                    ResetChangeDetection();
                    _log.Log("[probe] 看门狗：游戏已还原，触发重译");
                    Notice?.Invoke(ProbeNotice.Restored, "游戏已还原，翻译继续");
                }
            }
        }
        catch (Exception ex) { _log.Log($"[probe] 看门狗异常: {ex.Message}"); }
    }

    private void ClearOverlay() =>
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));

    /// <summary>复位变化检测：切引擎/登录成功后调用，让静止画面也立即用新引擎/新登录态重译上屏。</summary>
    public void ResetChangeDetection() => _lastJoined = "";

    /// <summary>F11 暂停/继续：暂停时清空悬浮显示且后续帧直接跳过处理（不停止捕获，不与 F9 冲突）；
    /// 继续时复位变化检测，让当前静止画面立即重译。返回切换后的暂停状态。</summary>
    public bool TogglePause()
    {
        _paused = !_paused;
        if (_paused)
            _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));
        else
            _lastJoined = "";
        return _paused;
    }

    public void Stop()
    {
        _watchdog?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _capture.Stop();
        _lastJoined = ""; // 复位变化检测，下次 Start 后首帧总会重新翻译上屏
        _windowLost = false; _wasIconic = false; _resNotified = false;
        _log.Log("[probe] 已停止");
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));
    }

    private async void OnFrame(SKBitmap frame)
    {
        if (_paused) { frame.Dispose(); return; }
        // 轮询频率接线（Phase 5）：每帧实时读设置→场景含 Chat ROI 用 ChatPollMs，否则 OtherPollMs（钳制 150–5000ms）。
        // 同时刷新捕获侧节流，保证"帧转换节流 ≤ 处理节流"，改设置即时生效无需重启。
        int pollMs = PollInterval.Resolve(_settings.Current.Ocr, _hasChatRoi);
        _capture.MinIntervalMs = pollMs;
        if ((DateTime.Now - _lastShot).TotalMilliseconds < pollMs
            || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            frame.Dispose();
            return;
        }
        _lastShot = DateTime.Now;
        try
        {
            if (!_roiAvailable)
            {
                if (!_roiMissingLogged)
                {
                    _log.Log("[probe] rois.json 缺失/为空，未加载任何 ROI——请先校准（F8）");
                    _roiMissingLogged = true;
                }
                return; // finally 仍会 Dispose(frame) 并复位 _busy
            }
            var frameSize = new Size(frame.Width, frame.Height);
            var winRect = NativeMethods.GetWindowScreenRect(_capture.Hwnd);
            if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0 || winRect.Left <= -30000 || winRect.Top <= -30000)
            {
                _log.Log("[probe] 窗口矩形无效/最小化，跳过本帧上屏");
                return; // finally 仍会 Dispose(frame) 并复位 _busy
            }
            var bucket = RoiStore.SelectBucket(_cfg, Scene, frame.Width, frame.Height);
            // 分辨率切换检测：命中的 bucket 与当前捕获尺寸不一致=靠等比映射兜底，位置可能偏差，提示重校准（每个不匹配期只发一次）。
            bool resMismatch = bucket.Resolution != $"{frame.Width}x{frame.Height}";
            if (resMismatch && !_resNotified)
            {
                _resNotified = true;
                _log.Log($"[probe] 分辨率不匹配：bucket={bucket.Resolution} 当前={frame.Width}x{frame.Height}");
                Notice?.Invoke(ProbeNotice.ResolutionChanged,
                    $"游戏分辨率已变化({frame.Width}x{frame.Height})，正按旧 ROI 等比映射，建议按 F8 重新校准");
            }
            else if (!resMismatch) _resNotified = false;
            var koLines = new List<string>();
            var metas = new List<(RectangleF frameBounds, string roiName)>();
            var t = _settings.Current.Translation;
            var scope = new ContentScopeSettings(t.TranslateChat, t.TranslateRoomName, t.TranslateMapInfo, t.TranslatePlayerId);
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
                var roiType = RoiTypes.Parse(roi.Type);
                var res = await _ocr.RecognizeAsync(sub);
                foreach (var line in res.Lines)
                {
                    _log.Log($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                    double relX = (line.Bounds.X + line.Bounds.Width / 2.0) / Math.Max(1, px.Width);
                    ColumnKind? col = roi.Columns is null
                        ? (roiType == RoiType.RoomList ? ColumnKind.RoomName : (ColumnKind?)null)
                        : TranslationScope.HitColumn(roi.Columns, relX);
                    bool translate = TranslationScope.ShouldTranslate(roiType, col, scope);
                    if (!translate) continue;
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

            var engine = _settings.Current.Translation.Active;
            var ctx = new TranslationContext(Scene, _glossaryHint);
            // 两级共用这一个超时预算(非每级各一份)：快态+质量态合计不超过 TimeoutSeconds。
            // 快态本就很快,故实际质量态仍有充裕时间；超时被 TieredTranslator 视作降级并回落,不会卡死取帧。
            int timeout = Math.Max(1, _settings.Current.Translation.TimeoutSeconds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

            void Render(IReadOnlyList<LineTranslation> snap)
            {
                var items = new List<OverlayItem>(snap.Count);
                foreach (var ln in snap)
                {
                    var (frameBounds, _) = metas[ln.Index];
                    var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
                    items.Add(new OverlayItem(ln.Text, scr.X, scr.Y, ln.Stage == TranslationStage.Provisional));
                }
                _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(items));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await _tiered.TranslateAsync(koLines, ctx, Render, cts.Token);
            sw.Stop();
            for (int k = 0; k < koLines.Count; k++)
                _log.Log($"[{metas[k].roiName}][{k}] \"{koLines[k]}\" engine={engine} 两级={_settings.Current.Translation.TwoLevel} {sw.ElapsedMilliseconds}ms");

            // 仅当无“总失败”行(AllResolved)才提交变化检测,否则下一帧同内容重试。
            if (outcome.AllResolved) _lastJoined = joined;
            _log.Log($"[probe] 上屏完成 AllResolved={outcome.AllResolved}");
        }
        catch (Exception ex) { _log.Log($"[probe] 异常: {ex.Message}"); }
        finally { frame.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _capture.FrameArrived -= OnFrame;
        _watchdog?.Dispose();
        _watchdog = null;
    }
}
