using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Scene;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
using SkiaSharp;
using Windows.Graphics.Capture;

namespace ScrTranslator.App;

public sealed record ProbeStartInfo(ProbeStart Status, string ProcessName);
public enum ProbeStart { Ok, CaptureUnsupported, WindowNotFound, NoRoi, CaptureFailed, WaitingForWindow }

/// <summary>Phase 5 异常恢复事件：看门狗检测到的状态转移，App 订阅后转托盘气泡。</summary>
public enum ProbeNotice { WindowLost, Reconnected, FirstConnected, Minimized, Restored, ResolutionChanged, NoFrames, Stalled, SceneChanged }

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
    private GameScene _activeScene = GameScene.RoomBrowser; // OCR 自动识别的当前场景（初值=房间列表，兼容老校准）
    private string SceneId => _activeScene.ToId();
    public string CurrentSceneLabel => _activeScene.Label();
    public string CurrentSceneId => SceneId;
    private DateTime _lastDetect = DateTime.MinValue;
    private GameScene _pendingScene = GameScene.Unknown; private int _pendingCount; // 迟滞：连续 N 次同判才切换
    private const int DetectIntervalMs = 2500, Hysteresis = 2;
    // 场景识别的"特征区"（相对 16:9 画面）：D1 左侧 UI（浏览器按钮/表头 + 大厅队伍/Select Race），
    // D2 右侧信息面板（大厅 Unit Limits/Turn Rate…），D3 右下 HUD（游戏内 MENU），D4 右上资源栏（supply/计时）。
    private static readonly (double X, double Y, double W, double H)[] DetectCrops =
    {
        (0.15, 0.06, 0.35, 0.50), (0.62, 0.30, 0.22, 0.32), (0.72, 0.79, 0.12, 0.08), (0.73, 0.00, 0.25, 0.05),
    };
    private DateTime _lastShot = DateTime.MinValue;
    private int _busy; // 0=空闲 1=处理中
    private string _lastJoined = ""; // 变化检测：上一帧全部 ROI 韩文拼接
    private bool _roiAvailable; // 当前活动场景是否存在且 bucket 非空；活动场景/ReloadRois 变化时重算
    private bool _hasChatRoi;   // 场景级"含 Chat ROI"，决定轮询用 ChatPollMs 还是 OtherPollMs；构造/ReloadRois 时算
    private bool _roiMissingLogged; // 确保"ROI 缺失"只记一次日志，避免刷屏
    private bool _paused; // F11 暂停/继续：暂停时跳过 OnFrame 处理，不动 CaptureService（与 F9 的 Start/Stop 互不干扰）

    // ── Phase 5 异常恢复：看门狗(2s) 检测窗口丢失/最小化/分辨率切换 ──
    public event Action<ProbeNotice, string>? Notice;
    private System.Threading.Timer? _watchdog;
    private bool _windowLost;   // 已就"窗口丢失"通知过（重连成功后复位）
    private bool _waitingForWindow; // Start 时未找到窗口→看门狗等待首连（不报"丢失"，连上报"自动开始"）
    private bool _wasIconic;    // 上一轮询周期是否最小化（转移时才通知）
    private bool _resNotified;  // 当前分辨率与 bucket 不匹配的提示只发一次（换回/重校准后复位）
    // 零帧/停滞诊断（实机排障：捕获会话活着但一帧不来 = 多半独占全屏；帧在来但处理不动 = _busy 卡死）
    private bool _firstFrameLogged;
    private long _lastFrameCount; private int _zeroFrameTicks; private bool _noFrameNotified;
    private int _processed; private int _lastProcessed; private int _stallTicks; private bool _stallNotified;

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
        _capture.Log = msg => _log.Log(msg); // 帧转换异常进文件日志（诊断）
    }

    /// <summary>从 _cfg 按当前活动场景计算派生标志（ROI 可用性 / 是否含 Chat ROI）。ctor/ReloadRois/场景切换时调用。</summary>
    private void RefreshRoiDerivedFlags()
    {
        _roiAvailable = _cfg.Scenes.TryGetValue(SceneId, out var buckets) && buckets.Count > 0;
        // 场景级（跨该场景所有分辨率 bucket）判定：只要可能出现聊天区就用聊天轮询值。
        _hasChatRoi = _cfg.Scenes.TryGetValue(SceneId, out var bs)
            && bs.Any(b => b.Rois.Any(r => RoiTypes.Parse(r.Type) == RoiType.Chat));
    }

    /// <summary>对若干"特征区"做 OCR，喂给 <see cref="SceneDetector"/>，带迟滞地更新活动场景。
    /// 返回本帧是否发生了场景切换。识别输入文本与命中线索写日志，便于对着实机画面调特征区。</summary>
    private async Task<bool> DetectSceneAsync(SKBitmap frame, CancellationToken ct)
    {
        var texts = new List<string>();
        foreach (var c in DetectCrops)
        {
            Rectangle px = RoiStore.ToPixels(new RelRect(c.X, c.Y, c.W, c.H), frame.Width, frame.Height);
            if (px.Width <= 0 || px.Height <= 0) continue;
            using var sub = new SKBitmap(px.Width, px.Height);
            if (!frame.ExtractSubset(sub, new SKRectI(px.Left, px.Top, px.Right, px.Bottom))) continue;
            var res = await _ocr.RecognizeAsync(sub);
            foreach (var line in res.Lines) texts.Add(line.Text);
            ct.ThrowIfCancellationRequested();
        }
        var d = SceneDetector.Detect(texts);
        _log.Log($"[scene] 判定={d.Scene.Label()}({_activeScene.Label()}) score={d.Score} margin={d.Margin} 线索=[{string.Join(",", d.Hits)}] 文本数={texts.Count}");

        if (d.Scene == _activeScene) { _pendingScene = GameScene.Unknown; _pendingCount = 0; return false; }
        if (d.Scene == GameScene.Unknown) return false; // 证据不足：保持当前场景

        // 迟滞：候选新场景需连续 Hysteresis 次命中才切换，防单帧误判抖动。
        if (d.Scene == _pendingScene) _pendingCount++;
        else { _pendingScene = d.Scene; _pendingCount = 1; }
        if (_pendingCount < Hysteresis) return false;

        var from = _activeScene;
        _activeScene = d.Scene;
        _pendingScene = GameScene.Unknown; _pendingCount = 0;
        RefreshRoiDerivedFlags();
        _lastJoined = ""; _roiMissingLogged = false; // 换场景=换 ROI，重新判定上屏
        _log.Log($"[scene] 切换 {from.Label()} → {_activeScene.Label()}");
        Notice?.Invoke(ProbeNotice.SceneChanged, $"当前场景：{_activeScene.Label()}");
        return true;
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
            // 看门狗：窗口丢失重连/最小化恢复(5-3)，以及"等待游戏窗口"模式——F9 时游戏没开不再算失败，
            // 挂 2s 轮询，窗口一出现自动开始捕获翻译（也承载 General.AutoTranslateOnLaunch 的启动自动开）。
            _windowLost = false; _wasIconic = false; _resNotified = false;
            _firstFrameLogged = false; _lastFrameCount = _capture.FrameCount;
            _zeroFrameTicks = 0; _noFrameNotified = false;
            _lastProcessed = _processed; _stallTicks = 0; _stallNotified = false;
            _waitingForWindow = hwnd == IntPtr.Zero;
            _watchdog ??= new System.Threading.Timer(WatchdogTick, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _watchdog.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            if (_waitingForWindow)
            {
                _log.Log($"[probe] 未找到 {proc} 窗口，进入等待模式");
                return new ProbeStartInfo(ProbeStart.WaitingForWindow, proc);
            }
            if (!_capture.Start(hwnd))
            {
                _watchdog.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _log.Log("[probe] 捕获启动失败");
                return new ProbeStartInfo(ProbeStart.CaptureFailed, proc);
            }
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
                if (!_waitingForWindow) // 等待首连阶段不误报"窗口已消失"，也不反复 Stop
                {
                    _capture.Stop(); // 幂等：释放挂在死句柄上的会话，避免 COM 悬挂
                    if (!_windowLost)
                    {
                        _windowLost = true; _wasIconic = false;
                        _log.Log("[probe] 看门狗：游戏窗口已丢失");
                        Notice?.Invoke(ProbeNotice.WindowLost,
                            $"游戏窗口已消失，翻译已暂停。检测到 {TargetProcess()} 重新开窗后将自动续译。");
                    }
                }
                var fresh = WindowFinder.FindByProcess(TargetProcess());
                if (fresh != IntPtr.Zero && _capture.Start(fresh))
                {
                    bool firstConnect = _waitingForWindow;
                    _waitingForWindow = false; _windowLost = false;
                    ResetChangeDetection(); // 新窗口内容需重新 OCR/上屏
                    _log.Log($"[probe] 看门狗：已{(firstConnect ? "首次连接" : "重连")}游戏窗口 hwnd={fresh}");
                    Notice?.Invoke(firstConnect ? ProbeNotice.FirstConnected : ProbeNotice.Reconnected,
                        firstConnect ? "已检测到游戏窗口，翻译自动开始" : "已重新连接游戏窗口，翻译继续");
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
            if (!iconic) WatchdogFrameFlow(hwnd); // 帧流健康：零帧=独占全屏/未渲染；帧在来但处理不动=卡死
        }
        catch (Exception ex) { _log.Log($"[probe] 看门狗异常: {ex.Message}"); }
    }

    private void ClearOverlay() =>
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));

    /// <summary>帧流健康检查（每看门狗周期调用）：
    /// A) 捕获会话在、但 10s 零新帧 → 大概率独占全屏盖住了 WGC，或画面完全静止；
    /// B) 有新帧但 10s 没走完一次处理 → _busy 卡死（OCR/网络未返回）。各提示一次，恢复后复位。</summary>
    private void WatchdogFrameFlow(IntPtr hwnd)
    {
        long fc = _capture.FrameCount;
        bool newFrames = fc != _lastFrameCount;
        _lastFrameCount = fc;

        if (!newFrames)
        {
            if (++_zeroFrameTicks == 5 && !_noFrameNotified) // ~10s
            {
                _noFrameNotified = true;
                _log.Log("[probe] 看门狗：10s 无新帧（独占全屏或画面静止）");
                Notice?.Invoke(ProbeNotice.NoFrames,
                    "10 秒没收到新画面：若游戏是独占全屏，请改窗口化/无边框；画面静止则动一下鼠标即可。");
            }
        }
        else
        {
            _zeroFrameTicks = 0; _noFrameNotified = false;
            bool progressed = _processed != _lastProcessed;
            _lastProcessed = _processed;
            if (!progressed)
            {
                if (++_stallTicks == 5 && !_stallNotified)
                {
                    _stallNotified = true;
                    _log.Log("[probe] 看门狗：帧在到达但 10s 未完成处理，疑似 _busy 卡死");
                    Notice?.Invoke(ProbeNotice.Stalled, "画面在刷新但翻译卡住了（可能 OCR/网络阻塞），稍后自动重试；持续则重启程序。");
                }
            }
            else { _stallTicks = 0; _stallNotified = false; }
        }
    }

    /// <summary>复位变化检测：切引擎/登录成功后调用，让静止画面也立即用新引擎/新登录态重译上屏。</summary>
    public void ResetChangeDetection() => _lastJoined = "";

    /// <summary>F11 暂停/继续：暂停时清空悬浮显示且后续帧直接跳过处理（不停止捕获，不与 F9 冲突）；
    /// 继续时复位变化检测，让当前静止画面立即重译。返回切换后的暂停状态。</summary>
    public bool TogglePause()
    {
        _paused = !_paused;
        _log.Log($"[probe] F11 切换：paused={_paused}");
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
        _windowLost = false; _wasIconic = false; _resNotified = false; _waitingForWindow = false;
        _log.Log("[probe] 已停止");
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));
    }

    private async void OnFrame(SKBitmap frame)
    {
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _log.Log($"[probe] 首帧到达 {frame.Width}x{frame.Height}（捕获→处理链路已通）");
        }
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
                    _log.Log($"[probe] 当前场景[{_activeScene.Label()}]无 ROI 校准——请按 F8 校准该场景");
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
            // 场景自动识别（每 DetectIntervalMs 一次）：识别到别的场景就切 ROI 集，本帧跳过翻译下一帧再来。
            if ((DateTime.Now - _lastDetect).TotalMilliseconds >= DetectIntervalMs)
            {
                _lastDetect = DateTime.Now;
                if (await DetectSceneAsync(frame, CancellationToken.None)) return;
            }
            var bucket = RoiStore.SelectBucket(_cfg, SceneId, frame.Width, frame.Height);
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
            var ctx = new TranslationContext(SceneId, _glossaryHint);
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
                    // Source=该行韩文原文：供悬浮层"原文+译文双行"模式使用（单行模式忽略）。
                    items.Add(new OverlayItem(ln.Text, scr.X, scr.Y,
                        ln.Stage == TranslationStage.Provisional, koLines[ln.Index]));
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
        finally { Interlocked.Increment(ref _processed); frame.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _capture.FrameArrived -= OnFrame;
        _watchdog?.Dispose();
        _watchdog = null;
    }
}
