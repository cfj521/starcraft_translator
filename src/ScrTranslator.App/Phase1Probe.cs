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
using System.Windows.Threading;

namespace ScrTranslator.App;

public sealed record ProbeStartInfo(ProbeStart Status, string ProcessName);
public enum ProbeStart { Ok, CaptureUnsupported, WindowNotFound, NoRoi, CaptureFailed, WaitingForWindow }

/// <summary>Phase 5 异常恢复事件：看门狗检测到的状态转移，App 订阅后转托盘气泡。</summary>
public enum ProbeNotice { WindowLost, Reconnected, FirstConnected, Minimized, Restored, ResolutionChanged, NoFrames, Stalled, SceneChanged, SceneRefMissing }

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly SceneRecognizer _scene;   // 图像指纹场景识别（读 scene-refs.json）
    private readonly FileLogger _log;
    private readonly OverlayWindow _overlay;
    private RoiConfig _cfg;
    private readonly TieredTranslator _tiered;
    private readonly SettingsHolder _settings;
    private readonly string _glossaryHint;
    private readonly string _roisJsonPath;
    private GameScene _activeScene = GameScene.Browsing; // 图像指纹识别出的当前场景（初值=寻找房间，与老校准一致）
    private GameRace _activeRace = GameRace.Unknown;     // 游戏内识别到的种族（装饰板参考按种族分套，顺带给出）
    private string SceneId => _activeScene.ToId();
    public string CurrentSceneLabel => _activeScene.Label();
    public string CurrentSceneId => SceneId;
    /// <summary>场景参考是否可用（scene-refs.json 加载成功且非空）。配置页/托盘用它提示"识别不可用"。</summary>
    public bool SceneRefsAvailable => _scene.Store.IsUsable;
    private DateTime _lastDetect = DateTime.MinValue;
    private GameScene _pendingScene = GameScene.Unknown; private int _pendingCount; // 迟滞：连续 N 次同判才切换
    private int _unknownCount; // 连续"指纹不达标"计数→达到阈值切到"其他界面"
    private bool _refMissingLogged; // "参考缺失"只提示一次，避免每帧刷屏
    private const int DetectIntervalMs = 1000, Hysteresis = 2, OtherThreshold = 3;
    // 场景识别不再做 OCR：改用 scene-refs.json 里的静态区域块均值指纹（区域由参考自带，见 SceneRecognizer）。
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

    // ── DEBUG 面板 + 窗口显隐（F6/常规页开关 + 游戏前台门控，250ms 刷新）──
    private readonly DebugWindow _debug;
    private DispatcherTimer? _debugTimer;
    private int _glossaryCount;
    private string _lastFrameWH = "—";
    private SceneRecognition _lastRec = new(SceneMatchStatus.NoReferences, GameScene.Unknown, null, null,
        GameRace.Unknown, 0, 0, Array.Empty<SceneScore>(), "尚未识别");
    private long _lastTranslateMs; private bool _lastAllResolved; private int _lastLineCount;

    /// <summary>目标进程名（设置驱动，默认 StarCraft）。Start 与看门狗重连共用。</summary>
    private string TargetProcess() => string.IsNullOrWhiteSpace(_settings.Current.General.TargetProcess)
        ? "StarCraft" : _settings.Current.General.TargetProcess.Trim();

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, SceneRecognizer scene, FileLogger log, OverlayWindow overlay,
        string roisJsonPath, TieredTranslator tiered, SettingsHolder settings, Glossary glossary, DebugWindow debug)
    {
        _capture = capture; _ocr = ocr; _scene = scene; _log = log; _overlay = overlay; _debug = debug;
        _roisJsonPath = roisJsonPath;
        _cfg = RoiStore.Load(roisJsonPath);
        RefreshRoiDerivedFlags();
        _tiered = tiered; _settings = settings;
        _glossaryCount = glossary.Entries.Count;
        _glossaryHint = PromptBuilder.BuildGlossaryHint(glossary.Entries);
        _capture.FrameArrived += OnFrame;
        _capture.LogError = msg => _log.Error(msg); // 帧转换异常=业务错误，恒记
        _capture.LogDebug = msg => _log.Debug(msg); // 帧流统计=诊断，DEBUG 才记
        // 窗口显隐：250ms 刷新。游戏非前台→隐藏悬浮层+调试窗口；前台→按用户意图(overlay.EnabledByUser / General.DebugMode)显示。
        _debugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debugTimer.Tick += (_, _) => ApplyWindowVisibility();
        _debugTimer.Start();
    }

    /// <summary>从 _cfg 按当前活动场景计算派生标志（ROI 可用性 / 是否含 Chat ROI）。ctor/ReloadRois/场景切换时调用。</summary>
    private void RefreshRoiDerivedFlags()
    {
        _roiAvailable = _cfg.Scenes.TryGetValue(SceneId, out var buckets) && buckets.Count > 0;
        // 场景级（跨该场景所有分辨率 bucket）判定：只要可能出现聊天区就用聊天轮询值。
        _hasChatRoi = _cfg.Scenes.TryGetValue(SceneId, out var bs)
            && bs.Any(b => b.Rois.Any(r => RoiTypes.Parse(r.Type) == RoiType.Chat));
    }

    /// <summary>
    /// 图像指纹场景识别（每 DetectIntervalMs 一次），带迟滞地更新活动场景；返回本帧是否发生了切换。
    ///
    /// 与旧 OCR 版的区别：识别不再产生文本、不再 await，只读几块静态区域做块均值比对（&lt;1ms），
    /// 所以整条链路是同步的，也就不会再和翻译请求抢 OCR 线程。
    ///
    /// 三种"识别不出来"的处置不同，别混为一谈：
    ///  - NoReferences / NoReferenceForResolution = **参考缺失**（文件坏了 / 这个分辨率没截过图），
    ///    保持当前场景不动——此时切到"其他界面"会把一次配置问题放大成"翻译突然停了"；只提示一次。
    ///  - BelowThreshold = 画面确实是三场景之外的界面（主菜单/加载/街机），连续 OtherThreshold 次才切"其他"，
    ///    防过场动画、弹窗遮挡这类瞬时干扰误伤。
    /// </summary>
    private bool DetectScene(SKBitmap frame)
    {
        var rec = _scene.Recognize(frame.AsFramePixels());
        _lastRec = rec;
        _log.Debug($"[scene] {rec.Summary}  帧={frame.Width}x{frame.Height}  " +
                   $"候选=[{string.Join(" ", rec.Scores.Select(s => $"{s.RefId}:{s.HitRatio:P0}"))}]");

        switch (rec.Status)
        {
            case SceneMatchStatus.NoReferences:
            case SceneMatchStatus.NoReferenceForResolution:
                if (_refMissingLogged) return false; // 每帧都缺参考，只报一次
                _refMissingLogged = true;
                _log.Warn($"[scene] 参考不可用：{rec.Detail ?? rec.Status.ToString()}——保持当前场景[{_activeScene.Label()}]，识别暂停");
                Notice?.Invoke(ProbeNotice.SceneRefMissing,
                    rec.Status == SceneMatchStatus.NoReferenceForResolution
                        ? $"当前分辨率缺少场景识别参考（{rec.Detail}），翻译暂按原场景继续；请用 tools/SceneRefGen 补该分辨率截图"
                        : $"场景识别参考不可用（{rec.Detail}），请重新安装或重跑 tools/SceneRefGen");
                return false;

            case SceneMatchStatus.Matched:
                _refMissingLogged = false;
                _unknownCount = 0; // 有明确匹配→清"其他"计数
                _activeRace = rec.Race; // 同场景内也刷新种族（游戏内参考按种族分套）
                if (rec.Scene == _activeScene) { _pendingScene = GameScene.Unknown; _pendingCount = 0; return false; }
                // 迟滞：候选新场景需连续 Hysteresis 次命中才切换，防单帧误判抖动。
                if (rec.Scene == _pendingScene) _pendingCount++;
                else { _pendingScene = rec.Scene; _pendingCount = 1; }
                if (_pendingCount < Hysteresis) return false;
                SwitchScene(rec.Scene, rec.Race);
                return true;

            default: // BelowThreshold
                _refMissingLogged = false;
                _pendingScene = GameScene.Unknown; _pendingCount = 0;
                if (_activeScene == GameScene.Other) return false; // 已在其他态，无需重复
                if (_unknownCount++ < OtherThreshold) return false;
                SwitchScene(GameScene.Other, GameRace.Unknown);
                return true;
        }
    }

    /// <summary>切换活动场景（含游戏内种族）：重算 ROI 标志、复位变化检测、记日志+气泡。其他界面态无 ROI→自动停翻。</summary>
    private void SwitchScene(GameScene to, GameRace race = GameRace.Unknown)
    {
        var from = _activeScene;
        _activeScene = to;
        _activeRace = race;
        _pendingScene = GameScene.Unknown; _pendingCount = 0; _unknownCount = 0;
        RefreshRoiDerivedFlags();
        _lastJoined = ""; _roiMissingLogged = false; // 换场景=换 ROI，重新判定上屏
        _log.Info($"[scene] 切换 {SceneDisplay(from, GameRace.Unknown)} → {SceneDisplay(to, race)}");
        Notice?.Invoke(ProbeNotice.SceneChanged, $"当前场景：{SceneDisplay(to, race)}");
    }

    /// <summary>面板/气泡用的场景显示名。游戏内带上识别到的种族——装饰板参考本就按种族分套，
    /// 顺带给出种族，既方便实机核对"识别对不对"，也为将来"按种族选聊天框"留好接口。</summary>
    static string SceneDisplay(GameScene scene, GameRace race) =>
        scene == GameScene.InGame && race != GameRace.Unknown ? $"{scene.Label()}·{race.Label()}" : scene.Label();

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
            if (!SceneRefsAvailable)
                _log.Warn($"[probe] 场景识别参考不可用（{_scene.Store.LoadError}）：将停在当前场景，跨界面不会自动切换");
            if (!GraphicsCaptureSession.IsSupported())
            {
                _log.Warn("[probe] GraphicsCapture.IsSupported=false（系统不支持窗口捕获）");
                return new ProbeStartInfo(ProbeStart.CaptureUnsupported, proc);
            }
            if (!_roiAvailable)
            {
                _log.Log("[probe] rois.json 无当前场景 ROI，请先校准");
                return new ProbeStartInfo(ProbeStart.NoRoi, proc);
            }
            var hwnd = WindowFinder.FindByProcess(proc);
            _log.Debug($"[probe] FindByProcess({proc}) hwnd={hwnd}");
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
                _log.Error("[probe] 捕获启动失败");
                return new ProbeStartInfo(ProbeStart.CaptureFailed, proc);
            }
            _log.Log("[probe] 已开始捕获（看门狗已启动）");
            return new ProbeStartInfo(ProbeStart.Ok, proc);
        }
        catch (Exception ex)
        {
            _log.Error($"[probe] Start 异常: {ex}");
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
        catch (Exception ex) { _log.Error($"[probe] 看门狗异常: {ex.Message}"); }
    }

    private void ClearOverlay() =>
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));

    /// <summary>按"游戏是否前台"叠加用户意图，计算悬浮层与调试窗口的可见性（用 Visibility，不 Show/Hide 以免抢焦点）。
    /// 调试窗口显示时顺带刷新文本。</summary>
    private void ApplyWindowVisibility()
    {
        // 诊断级日志开关与 DEBUG 面板同步（单一真源=General.DebugMode，覆盖 F6/配置页/托盘所有切换路径）。
        _log.DebugEnabled = _settings.Current.General.DebugMode;
        bool gameActive = IsGameForeground();
        var overlayTarget = gameActive && _overlay.EnabledByUser ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
        if (_overlay.Visibility != overlayTarget) _overlay.Visibility = overlayTarget;

        bool debugOn = gameActive && _settings.Current.General.DebugMode;
        var debugTarget = debugOn ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
        if (_debug.Visibility != debugTarget) _debug.Visibility = debugTarget;
        if (debugOn) _debug.SetText(BuildDebugText());
    }

    /// <summary>游戏窗口是否为当前前台窗口。未 Start 时按进程名现查句柄。</summary>
    private bool IsGameForeground()
    {
        IntPtr game = _capture.Hwnd != IntPtr.Zero ? _capture.Hwnd : WindowFinder.FindByProcess(TargetProcess());
        return game != IntPtr.Zero && NativeMethods.GetForegroundWindow() == game;
    }

    /// <summary>当前诊断快照文本（供 Ctrl+F6 复制到剪贴板；与面板同源）。</summary>
    public string GetDebugSnapshot() => BuildDebugText();

    private string BuildDebugText()
    {
        int pollMs = PollInterval.Resolve(_settings.Current.Ocr, _hasChatRoi);
        int roiCount = _cfg.Scenes.TryGetValue(SceneId, out var bs2) ? bs2.Sum(b => b.Rois.Count) : 0;
        string state = _waitingForWindow ? "等待窗口" : _windowLost ? "窗口丢失" : _paused ? "已暂停" : "运行中";
        // 指纹打分明细：排查"识别不对/识别不到"时最想看的就是每个候选各多少分，直接摊开。
        string scores = _lastRec.Scores.Count == 0 ? "—"
            : string.Join("  ", _lastRec.Scores.Select(s => $"{s.RefId} {s.HitRatio:P0}"));
        var store = _scene.Store;
        string refs = store.IsUsable
            ? $"{store.References.Count}条(现场{store.OverrideCount}) 块{store.BlockSize} 容差{store.ChannelTolerance} 阈值{store.MinHitRatio:P0}"
            : $"不可用: {store.LoadError}";
        var t = _settings.Current.Translation;
        return
            $"场景: {SceneDisplay(_activeScene, _activeRace)}  判定: {_lastRec.Summary}\n" +
            $"指纹: {scores}\n" +
            $"帧: {_lastFrameWH}  已处理={_processed}  捕获帧={_capture.FrameCount}\n" +
            $"轮询: {pollMs}ms  引擎: {t.Active}  两级: {(t.TwoLevel ? "on" : "off")}\n" +
            $"ROI[{_activeScene.Label()}]: {roiCount}个  可用={(_roiAvailable ? "yes" : "no")}  busy={_busy}\n" +
            $"状态: {state}  chatRoi={(_hasChatRoi ? "yes" : "no")}\n" +
            $"翻译: {_lastLineCount}行 {_lastTranslateMs}ms  resolved={(_lastAllResolved ? "yes" : "no")}\n" +
            $"缓存: {_tiered.CacheCount}  术语表: {_glossaryCount}\n" +
            $"参考: {refs}";
    }

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
                _log.Warn("[probe] 看门狗：10s 无新帧（独占全屏或画面静止）");
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
                    _log.Warn("[probe] 看门狗：帧在到达但 10s 未完成处理，疑似 _busy 卡死");
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
        _lastFrameWH = $"{frame.Width}x{frame.Height}";
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
            // ── 场景识别必须在 ROI 可用性判断之前跑 ──
            // _roiAvailable 是识别的**结果**（切到"其他界面"或某场景没校准 → 它变 false），
            // 把它放在识别前面会让这些状态变成"只进不出"的死胡同：一旦进去就再没有帧去识别回来。
            // 实测症状：开局头两次识别正常，之后永远停在"其他界面"。
            if ((DateTime.Now - _lastDetect).TotalMilliseconds >= DetectIntervalMs)
            {
                _lastDetect = DateTime.Now;
                // 识别到场景切换 → 本帧不翻（ROI 集刚变，bucket 需重取），下一帧按新场景继续。
                if (DetectScene(frame)) return;
            }
            if (!_roiAvailable)
            {
                if (!_roiMissingLogged)
                {
                    if (_activeScene == GameScene.Other)
                        _log.Debug("[probe] 其他界面（主菜单/加载等），本就不翻译"); // 其他态不催校准
                    else
                        _log.Info($"[probe] 当前场景[{_activeScene.Label()}]无 ROI 校准——请按 F8 校准该场景");
                    _roiMissingLogged = true;
                }
                return; // finally 仍会 Dispose(frame) 并复位 _busy
            }
            var frameSize = new Size(frame.Width, frame.Height);
            var winRect = NativeMethods.GetWindowScreenRect(_capture.Hwnd);
            if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0 || winRect.Left <= -30000 || winRect.Top <= -30000)
            {
                _log.Debug("[probe] 窗口矩形无效/最小化，跳过本帧上屏");
                return; // finally 仍会 Dispose(frame) 并复位 _busy
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
                    _log.Warn($"[{roi.Name}] 裁剪失败 rect={px}");
                    continue;
                }
                var roiType = RoiTypes.Parse(roi.Type);
                var res = await _ocr.RecognizeAsync(sub);
                foreach (var line in res.Lines)
                {
                    _log.Debug($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
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
                    _log.Debug($"[{roi.Name}] (空)");
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
            _lastTranslateMs = sw.ElapsedMilliseconds; _lastAllResolved = outcome.AllResolved; _lastLineCount = koLines.Count;
            for (int k = 0; k < koLines.Count; k++)
                _log.Debug($"[{metas[k].roiName}][{k}] \"{koLines[k]}\" engine={engine} 两级={_settings.Current.Translation.TwoLevel} {sw.ElapsedMilliseconds}ms");

            // 仅当无“总失败”行(AllResolved)才提交变化检测,否则下一帧同内容重试。
            if (outcome.AllResolved) _lastJoined = joined;
            _log.Debug($"[probe] 上屏完成 lines={koLines.Count} {sw.ElapsedMilliseconds}ms AllResolved={outcome.AllResolved}");
        }
        catch (Exception ex) { _log.Error($"[probe] 处理帧异常: {ex.Message}"); }
        finally { Interlocked.Increment(ref _processed); frame.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _capture.FrameArrived -= OnFrame;
        _debugTimer?.Stop();
        _watchdog?.Dispose();
        _watchdog = null;
    }
}
