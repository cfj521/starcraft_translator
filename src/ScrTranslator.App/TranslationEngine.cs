using System.Collections.Concurrent;
using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Scene;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;
using SkiaSharp;
using Windows.Graphics.Capture;
using System.Windows.Threading;

namespace ScrTranslator.App;

public sealed record ProbeStartInfo(ProbeStart Status, string ProcessName);
public enum ProbeStart { Ok, CaptureUnsupported, WindowNotFound, NoRoi, CaptureFailed, WaitingForWindow, WaitingForModels }

/// <summary>Phase 5 异常恢复事件：看门狗检测到的状态转移，App 订阅后转托盘气泡。</summary>
public enum ProbeNotice { WindowLost, Reconnected, FirstConnected, Minimized, Restored, ResolutionChanged, NoFrames, Stalled, SceneChanged, SceneRefMissing, TranslateFailed, NeedsCalibration }

public sealed class TranslationEngine : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private SceneRecognizer _scene;   // 图像指纹场景识别（读 scene-refs.json）；现场采集后重读覆盖层需重建，故非 readonly
    private readonly string _sceneRefsPath;      // 出厂层 scene-refs.json 路径（重载时重读）
    private readonly string _sceneOverridesPath; // 覆盖层 scene-overrides.json 路径（现场采集写入后重载生效）
    private LayoutPresetStore _layout;   // 界面布局预设：聊天框与房间列表网格（可重载，配置页改了要重读盘）
    private readonly FileLogger _log;
    private readonly OverlayWindow _overlay;
    private readonly TranslationOrchestrator _orchestrator;
    private readonly HyMt2Provider _hyMt2; // 本地引擎实例：调试面板显示其加载状态（未加载/加载中/可用/不可用）
    private readonly SettingsHolder _settings;
    private readonly string _glossaryHint;
    // 启动时人在哪个界面是**未知**的，所以初值取 Other（不翻译）而不是猜"寻找房间"：
    // 猜错会拿别的场景的 ROI 去 OCR 当前画面，翻出一堆垃圾。识别每 SceneDetectPollMs 跑一次，
    // 拿到第一帧后最多 2 个节拍就会自动落到真实场景。
    private GameScene _activeScene = GameScene.Other;
    private GameScene _lastKnownScene = GameScene.Browsing; // 最近一次识别到的"目标场景"；Other 态下给校准窗当默认值
    private GameRace _activeRace = GameRace.Unknown;     // 游戏内识别到的种族（装饰板参考按种族分套，顺带给出）
    private string SceneId => _activeScene.ToId();
    public string CurrentSceneLabel => _activeScene.Label();
    /// <summary>给校准/配置页用的场景 id：Other 态（含"还没拿到帧"）退到最近一次识别到的目标场景，
    /// 避免把 ROI 存进永不翻译的 other 桶。</summary>
    public string CurrentSceneId => (_activeScene == GameScene.Other ? _lastKnownScene : _activeScene).ToId();
    /// <summary>场景参考是否可用（scene-refs.json 加载成功且非空）。配置页/托盘用它提示"识别不可用"。</summary>
    public bool SceneRefsAvailable => _scene.Store.IsUsable;

    // ── 截图（F6）：诊断用，抓"翻译同一帧流"的下一帧存 PNG，用于核对捕获方式/分辨率/黑边 ──
    // 请求方（UI 线程）置 _shotPending 并挂上 _shotTcs；OnFrame（捕获线程）消费它、深拷贝当前帧、
    // SetResult。拷贝必须在捕获线程做——原帧在本帧 finally 里 Dispose，跨线程直接引用会悬空。
    private readonly object _shotLock = new();
    private bool _shotPending;                                  // 恒在 _shotLock 内读写
    private TaskCompletionSource<SKBitmap>? _shotTcs;           // 恒在 _shotLock 内读写
    /// <summary>当前生效的布局预设（直读直写出厂文件）。校准窗/配置页要拿这个**实例**而不是 DI 单例——
    /// 校准保存后 <see cref="ReloadLayoutPresets"/> 只重赋值本字段（新实例），DI 容器里那个单例
    /// 不重读盘永远是启动时的旧值；拿 DI 单例的窗体就会把预设框画回出厂原位（"我明明改了，框没动"）。</summary>
    public LayoutPresetStore Layout => _layout;
    private DateTime _lastDetect = DateTime.MinValue;
    private GameScene _pendingScene = GameScene.Unknown; private int _pendingCount; // 迟滞：连续 N 次同判才切换
    private int _unknownCount; // 连续"指纹不达标"计数→达到阈值切到"其他界面"
    private bool _refMissingLogged; // "参考缺失"只提示一次，避免每帧刷屏
    private const int Hysteresis = 2, OtherThreshold = 3;
    // 场景识别节拍与翻译节拍各自独立（Ocr.SceneDetectPollMs / Ocr.TranslatePollMs），见 OnFrame 顶部的门控。
    private DateTime _lastShot = DateTime.MinValue;
    private int _busy; // 0=空闲 1=处理中
    // 每 ROI 独立变化检测（键=ROI 在当前有效列表里的下标）：该 ROI 韩文与上一帧相同则整块跳过（不重译、不重显）。
    // ROI 集变化（换场景/分辨率/增删）时按签名整体清空 per-ROI 状态。
    private readonly Dictionary<int, string> _lastJoinedPerRoi = new();
    // OCR 行级两帧共识（ingame 聊天浮在游戏画面上的动态背景降噪）：同一行文本连续两帧出现才提交进变化检测/翻译/上屏；
    // 已提交的行被单位遮挡一两帧不掉（宽限，bounds 沿用最近见到）。键=ROI 下标，随 ROI 集签名与变化检测一起清。
    private readonly LineStabilizer _stabilizer = new();
    /// <summary>OCR 行置信度下限：ingame 聊天背景是真实游戏画面，背景像素被读成低置信度乱行（碰巧含韩文就会过韩文闸门、
    /// 每拍刷一次池请求）。真文字白底黑边 conf≈0.98，0.75 留足余量。"真行被遮挡/读乱"由两帧共识兜住。</summary>
    private const double MinLineConfidence = 0.75;
    private readonly Dictionary<int, List<OverlayItem>> _displayedPerRoi = new();
    private string _roiSetSignature = "";
    private bool _roiAvailable; // 当前活动场景是否有预设区域；活动场景/重载预设变化时重算
    private bool _anySceneCalibrated; // 至少一个场景有预设——F9 的启动前置条件（与"当前场景"无关）
    private bool _roiMissingLogged; // 确保"ROI 缺失"只记一次日志，避免刷屏
    /// <summary>per-ROI 累计翻译行数（跨帧累计的成功翻译行数，键=ROI 名称）。调试面板逐行显示。</summary>
    private readonly ConcurrentDictionary<string, long> _cumTranslated = new();
    /// <summary>上一帧实际处理（被选用）的 ROI 名称，按有效列表顺序。整帧替换，volatile 保证调试定时器读到一致快照。</summary>
    private volatile string[] _lastEffectiveRoiNames = Array.Empty<string>();

    // ── Phase 5 异常恢复：看门狗(2s) 检测窗口丢失/最小化/分辨率切换 ──
    public event Action<ProbeNotice, string>? Notice;
    private System.Threading.Timer? _watchdog;
    private bool _windowLost;   // 已就"窗口丢失"通知过（重连成功后复位）
    private bool _waitingForWindow; // Start 时未找到窗口→看门狗等待首连（不报"丢失"，连上报"自动开始"）
    private bool _waitingForModels; // Start 时模型未就绪（OCR 初始化中 / HY-MT2 加载中）→ 推迟开抓帧，看门狗待模型就绪后自动开
    private bool _wasIconic;    // 上一轮询周期是否最小化（转移时才通知）
    /// <summary>翻译是否进行中（F9 开→true，Stop→false）。ProcessFrame 在 await 翻译的续延里读它：
    /// 翻译等待期间若被 F9 Stop()，_running 已置 false，本次结果丢弃不上屏——否则最后一帧的
    /// ShowLines 会排在 Stop 的清空之后，把刚关掉的译文重新画回悬浮层（"关翻译后字还在"的竞态根因）。</summary>
    private volatile bool _running;
    // 零帧/停滞诊断（实机排障：捕获会话活着但一帧不来 = 多半独占全屏；帧在来但处理不动 = _busy 卡死）
    private bool _firstFrameLogged;
    private long _lastFrameCount; private int _zeroFrameTicks; private bool _noFrameNotified;
    private int _processed; private int _lastProcessed; private int _stallTicks; private bool _stallNotified;

    // ── DEBUG 面板 + 窗口显隐（F8/常规页开关 + 游戏前台门控，250ms 刷新）──
    private readonly DebugWindow _debug;
    private DispatcherTimer? _debugTimer;
    private int _glossaryCount;
    private string _lastFrameWH = "—";
    private SceneRecognition _lastRec = new(SceneMatchStatus.NoReferences, GameScene.Unknown, null, null,
        GameRace.Unknown, 0, 0, Array.Empty<SceneScore>(), "尚未识别",
        Array.Empty<(SceneReference, IReadOnlyList<SceneRefRegion>)>());
    private long _lastTranslateMs; private bool _lastAllResolved; private int _lastLineCount;
    // 本批命中数（非池大小）：真正送进引擎翻译的行 / 术语表命中 / 缓存命中。调试面板显示的是这批实际数。
    private int _lastEngineHits; private int _lastCacheHits; private int _lastGlossaryHits;
    private string? _lastTranslateError;   // 最近一次 provider 失败原因（null=正常）；也是"同因只提示一次"的去重键

    /// <summary>目标进程名（设置驱动，默认 StarCraft）。Start 与看门狗重连共用。</summary>
    private string TargetProcess() => string.IsNullOrWhiteSpace(_settings.Current.General.TargetProcess)
        ? "StarCraft" : _settings.Current.General.TargetProcess.Trim();

    public TranslationEngine(CaptureService capture, IOcrEngine ocr, SceneRecognizer scene, LayoutPresetStore layout, FileLogger log, OverlayWindow overlay,
        string sceneRefsPath, string sceneOverridesPath,
        TranslationOrchestrator orchestrator, SettingsHolder settings, Glossary glossary, DebugWindow debug, HyMt2Provider hyMt2)
    {
        _capture = capture; _ocr = ocr; _scene = scene; _layout = layout; _log = log; _overlay = overlay; _debug = debug;
        _sceneRefsPath = sceneRefsPath; _sceneOverridesPath = sceneOverridesPath;
        RefreshRoiDerivedFlags();
        _orchestrator = orchestrator; _settings = settings; _hyMt2 = hyMt2;
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

    /// <summary>从布局预设算派生标志：当前场景有没有预设区域（决定翻不翻）+ 有没有任何预设
    /// （决定 F9 能不能启动）。后者必须与"当前场景"解耦——启动瞬间还没识别，当前场景是 Other，
    /// 若按前者判断就会误报"尚未校准"。预设是唯一来源。</summary>
    private void RefreshRoiDerivedFlags()
    {
        _roiAvailable = _layout.HasScene(SceneId);
        _anySceneCalibrated = _layout.Presets.Count > 0;
    }

    /// <summary>
    /// 该 (场景, 分辨率) 本轮实际要跑的识别区域 = 布局预设的 parts（若该场景有预设）。
    /// 预设是唯一来源：没有用户校准框、也没有合并，Find 不到就返回空列表（该场景不翻译）。
    /// </summary>
    private IReadOnlyList<(string Name, RelRect Rect)> presetParts(int frameWidth, int frameHeight)
    {
        return _layout.Find(SceneId, frameWidth, frameHeight)?.Parts() ?? Array.Empty<(string, RelRect)>();
    }

    /// <summary>两段文本是否"同一行"：编辑距离 ≤ 较长者的 10% 视为相同（OCR 抖动 1~2 字不触发重译）。
    /// 完全相同直接 true；任一为空则只有两者皆空才 true（空→有字是真变化）。</summary>
    private static bool TextsSimilar(string a, string b)
    {
        if (a == b) return true;
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return true;
        if (a.Length == 0 || b.Length == 0) return false; // 空↔有字 = 真变化
        return LevenshteinDistance(a, b) <= maxLen / 10;
    }

    /// <summary>编辑距离（Levenshtein）：把 a 变成 b 最少需要的 插入/删除/替换 次数。行短，O(n*m) 无压力。</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    /// <summary>
    /// 图像指纹场景识别（每 <c>Ocr.SceneDetectPollMs</c> 一次），带迟滞地更新活动场景；返回本帧是否发生了切换。
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
        // DEBUG：画当前活动场景的指纹区域框——场景指纹取哪个区域就画哪个。
        // 候选区域是 Recognize 用同一套 centerCrop+FittingRegions 算的，框坐标天然和裁剪/缩放一致。
        var activeRegions = rec.CandidateRegions
            .FirstOrDefault(cr => cr.Ref.Scene == _activeScene && cr.Ref.Race == _activeRace)
            .Regions ?? Array.Empty<SceneRefRegion>();
        _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowRegions(activeRegions));
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
                _lastKnownScene = rec.Scene; // 记住最近的目标场景：Other 态下按 F8 时用它做默认桶
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

    /// <summary>
    /// 切换活动场景（含游戏内种族）：清掉上一场景的译文、重算 ROI 标志、复位变化检测、记日志+气泡。
    /// **必须同步清空悬浮层**：旧译文是按旧场景 ROI 定位的，留在屏幕上既位置错内容也错
    /// （游戏内聊天条会浮到房间列表的坐标上），比"短暂空白"糟糕得多。
    /// 其他界面态无 ROI→自动停翻。
    /// </summary>
    private void SwitchScene(GameScene to, GameRace race = GameRace.Unknown)
    {
        var from = _activeScene;
        _activeScene = to;
        _activeRace = race;
        _pendingScene = GameScene.Unknown; _pendingCount = 0; _unknownCount = 0;
        RefreshRoiDerivedFlags();
        ClearOverlay(); // 换场景=旧译文全部失效，立即清屏（不等下一帧）
        ResetAllChangeDetection(); _roiMissingLogged = false; // 换场景=换 ROI，重新判定上屏
        _log.Info($"[scene] 切换 {SceneDisplay(from, GameRace.Unknown)} → {SceneDisplay(to, race)}");
        if (to != GameScene.Other && !_roiAvailable)
        {
            // 三个场景各自一套预设识别区域。切到一个没有预设的场景时必须当场说出来，
            // 否则用户的感受是"切了界面就什么都不翻"，像是区域在场景间混用。
            _log.Info($"[probe] 当前场景[{SceneDisplay(to, race)}]无预设识别区域");
            Notice?.Invoke(ProbeNotice.NeedsCalibration,
                $"当前场景：{SceneDisplay(to, race)}——这个场景还没框选区域，请按 F8 校准（三个场景要各校准一次）");
        }
        else Notice?.Invoke(ProbeNotice.SceneChanged, $"当前场景：{SceneDisplay(to, race)}");
    }

    /// <summary>面板/气泡用的场景显示名。游戏内带上识别到的种族——装饰板参考本就按种族分套，
    /// 顺带给出种族，既方便实机核对"识别对不对"，也为将来"按种族选聊天框"留好接口。</summary>
    static string SceneDisplay(GameScene scene, GameRace race) =>
        scene == GameScene.InGame && race != GameRace.Unknown ? $"{scene.Label()}·{race.Label()}" : scene.Label();

    /// <summary>
    /// 重新从磁盘加载布局预设（直写出厂文件，单文件）。
    /// DI 里 LayoutPresetStore 是单例，配置页改了预设（直写出厂文件）后若不重载，**运行中还用旧坐标**，
    /// 表现就是"我明明改了，画面上的框没变"。所以区域页保存后必须连着调这个。
    /// </summary>
    public void ReloadLayoutPresets()
    {
        if (string.IsNullOrEmpty(_layout.FilePath)) return;
        _layout = LayoutPresetStore.Load(_layout.FilePath);
        RefreshRoiDerivedFlags();
        ResetChangeDetection();
        _log.Log($"[probe] 布局预设已重新加载：{_layout.Presets.Count} 条" +
                 (_layout.LoadError is null ? "" : $" 错误={_layout.LoadError}"));
    }

    /// <summary>
    /// 重新从磁盘加载场景识别参考（出厂层 + 覆盖层）。校准窗"采集当前场景指纹"写入覆盖层后调用，
    /// 让运行中的识别立即用上新参考，无需重启。与 ReloadLayoutPresets 同一"DI 单例不重读盘就是旧值"的坑。
    /// </summary>
    public void ReloadSceneRefs()
    {
        _scene = new SceneRecognizer(SceneRefStore.Load(_sceneRefsPath, _sceneOverridesPath));
        _lastRec = new(SceneMatchStatus.NoReferences, GameScene.Unknown, null, null,
            GameRace.Unknown, 0, 0, Array.Empty<SceneScore>(), "已重载，待识别",
            Array.Empty<(SceneReference, IReadOnlyList<SceneRefRegion>)>());
        _log.Log($"[probe] 场景参考已重新加载：{_scene.Store.References.Count} 条（覆盖 {_scene.Store.OverrideCount}）" +
                  (_scene.Store.LoadError is null ? "" : $" 错误={_scene.Store.LoadError}"));
    }

    /// <summary>启动探测。返回具体失败原因供 UI 提示（首次引导：F9 后没反应要知道为什么）。</summary>
    /// <summary>轮询前置：所有在用的模型是否都就绪。OCR 必须就绪（读韩文）；内部组还要 HY-MT2 就绪（翻）。
    /// 外部组不用 HY-MT2，只等 OCR。未全就绪时 Start 推迟开抓帧、看门狗待就绪后自动开，避免模型加载期间空转烧 CPU。</summary>
    private bool ModelsReady()
    {
        if (!_ocr.IsReady) return false;
        if (_settings.Current.Translation.SelectedGroup == TranslationGroup.Internal
            && _hyMt2.LoadState != HyMt2LoadState.Ready) return false;
        return true;
    }

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
            if (!_anySceneCalibrated)
            {
                // 注意判断的是"有没有任何场景的预设"，不是"当前场景有没有区域"：
                // 启动瞬间还没拿到帧，当前场景恒为 Other，按后者会永远报"尚无预设"。
                _log.Log("[probe] 布局预设为空（预设加载失败），无法翻译");
                return new ProbeStartInfo(ProbeStart.NoRoi, proc);
            }
            var hwnd = WindowFinder.FindByProcess(proc);
            _log.Debug($"[probe] FindByProcess({proc}) hwnd={hwnd}");
            // 看门狗：窗口丢失重连/最小化恢复(5-3)，以及"等待游戏窗口"模式——F9 时游戏没开不再算失败，
            // 挂 2s 轮询，窗口一出现自动开始捕获翻译（也承载 General.AutoTranslateOnLaunch 的启动自动开）。
            _windowLost = false; _wasIconic = false; _running = true; // 引擎进入运行态（含等待首连：窗口出现自动续译也属运行中）
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
            // 模型就绪门：OCR 初始化中 / HY-MT2 加载未完成时，先不抓帧、不空转烧 CPU。
            // 进等待态，看门狗每 2s 查一次，模型就绪后自动开抓帧（WatchdogTick 里处理）。
            if (!ModelsReady())
            {
                _waitingForModels = true;
                _log.Log("[probe] 模型未就绪（OCR 初始化中 / HY-MT2 加载中），推迟开轮询，模型就绪后自动开始（看门狗轮询）");
                return new ProbeStartInfo(ProbeStart.WaitingForModels, proc);
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
        try
        {
            // 模型就绪门：Start 因模型未就绪推迟了开抓帧。模型好了、窗口也在，就自动开抓帧。
            if (_waitingForModels)
            {
                if (!ModelsReady()) return; // 还没就绪，下轮再看
                _waitingForModels = false;
                var mhwnd = WindowFinder.FindByProcess(TargetProcess());
                if (mhwnd != IntPtr.Zero && _capture.Start(mhwnd))
                    _log.Log("[probe] 模型已就绪，开始捕获（自动）");
                else
                {
                    _waitingForModels = true; // 窗口也没了，回等待态，等窗口+模型都齐
                    _waitingForWindow = true;
                    _log.Log("[probe] 模型已就绪但未找到游戏窗口，转入等待窗口");
                }
                return;
            }
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
        _ = _overlay.Dispatcher.InvokeAsync(() =>
        {
            _overlay.ShowLines(System.Array.Empty<OverlayItem>());
            _overlay.ClearRegions(); // 场景变化=旧场景的指纹区域框失效，一并清掉（下一拍识别会重画当前场景的框）
        });

    /// <summary>按"游戏是否前台"叠加用户意图，计算悬浮层与调试窗口的可见性（用 Visibility，不 Show/Hide 以免抢焦点）。
    /// 调试窗口显示时顺带刷新文本。</summary>
    private void ApplyWindowVisibility()
    {
        // 诊断级日志开关与 DEBUG 面板同步（单一真源=General.DebugMode，覆盖 F8/配置页/托盘所有切换路径）。
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

    /// <summary>当前诊断快照文本（供 Ctrl+F8 复制到剪贴板；与面板同源）。</summary>
    public string GetDebugSnapshot() => BuildDebugText();

    private string BuildDebugText()
    {
        var ocr = _settings.Current.Ocr;
        int detectMs = PollInterval.SceneDetect(ocr), translateMs = PollInterval.Translate(ocr);
        // 识别区域多行：当前帧预设里实际在用的 parts，逐行显示名称 + 累计翻译行数。
        var roiNames = _lastEffectiveRoiNames;
        string roiSection = roiNames.Length == 0
            ? $"ROI[{_activeScene.Label()}]: (无)\n"
            : $"ROI[{_activeScene.Label()}]:\n" +
              string.Join("\n", roiNames.Select(n => $"  {n}  累计翻译={_cumTranslated.GetValueOrDefault(n)}行")) + "\n";
        var recent = _orchestrator.RecentProviders;
        // 翻译引擎：当前池首 provider（内部/外部单选，池里就它一个）。HY-MT2 附带加载状态（未加载/加载中/可用/不可用）。
        var engine = _orchestrator.Providers.FirstOrDefault();
        string engineLine = engine is null
            ? "翻译引擎: (无)\n"
            : $"翻译引擎: {engine.Name}{(engine.Name == TranslationProviderEntry.HyMt2 ? $"（{_hyMt2.DescribeLoadState()}）" : "")}\n";
        // OCR 引擎加载状态：模型门期间帧循环不跑，这行是唯一能让你看到"OCR 还没就绪"的地方（就绪=可识别，加载中=后台初始化）。
        string ocrLine = $"OCR: {(_ocr.IsReady ? "就绪" : "加载中")}\n";
        // 匹配度（末尾）：只显示命中率最高那条（refId + 命中率）；无参考/识别暂停时为空。
        string matchSection = _lastRec.Scores.Count == 0
            ? ""
            : $"匹配度: {_lastRec.RefId} {_lastRec.HitRatio:P1}\n";
        return
            $"场景: {SceneDisplay(_activeScene, _activeRace)}\n" +
            $"帧: {_lastFrameWH}  已处理={_processed}  捕获帧={_capture.FrameCount}\n" +
            $"轮询: 检测{detectMs}ms 翻译{translateMs}ms 捕获{PollInterval.CaptureFloor(ocr)}ms\n" +
            roiSection +
            ocrLine +
            engineLine +
            $"翻译: {_lastEngineHits}行(共{_lastLineCount}行) {_lastTranslateMs}ms  resolved={(_lastAllResolved ? "yes" : "no")}" +
            (_lastTranslateError is null ? "\n" : $"  错误: {_lastTranslateError}\n") +
            $"引擎(最近3次): {(recent.Count == 0 ? "(无)" : string.Join("  ", recent))}\n" +
            $"命中缓存: {_lastCacheHits}  命中术语表: {_lastGlossaryHits}\n" +
            matchSection;
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

    /// <summary>复位全部变化检测（per-ROI 的拼接文本/显示行/ROI 集签名）。切引擎/登录成功/窗口重连/最小化还原/换场景/Stop 后调用，
    /// 让静止画面也立即用新状态重译上屏。</summary>
    private void ResetAllChangeDetection()
    {
        _lastJoinedPerRoi.Clear();
        _displayedPerRoi.Clear();
        _roiSetSignature = "";
        _stabilizer.ResetAll();
    }
    public void ResetChangeDetection() => ResetAllChangeDetection();

    public void Stop()
    {
        _running = false; // 先置运行态 false：在途的 ProcessFrame 翻译续延醒来后读到这里，丢弃结果不上屏（防"关翻译字还在"竞态）
        _watchdog?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _capture.Stop();
        ResetAllChangeDetection(); // 复位变化检测，下次 Start 后首帧总会重新翻译上屏
        _windowLost = false; _wasIconic = false; _waitingForWindow = false;
        _log.Log("[probe] 已停止");
        _ = _overlay.Dispatcher.InvokeAsync(() =>
        {
            _overlay.ShowLines(System.Array.Empty<OverlayItem>());
            _overlay.ClearRegions(); // 停翻=不再有指纹识别，区域框一并清掉
        });
    }

    /// <summary>
    /// F6 截图：把**下一帧**捕获帧存成 PNG 到 targetDir。与翻译走同一条 WGC 帧流（同源、同像素）——
    /// 这正是它的用途：截出来的图与翻译看到的逐像素一致，才能用来核对捕获方式/分辨率/黑边。
    /// 触发后立即返回，编码+落盘在线程池续延里完成；可从任意线程调用（热键落在 UI 线程）。
    /// </summary>
    public void Screenshot(string targetDir)
    {
        if (_capture.Hwnd == IntPtr.Zero)
        {
            _log.Warn("[screenshot] 捕获未启动（翻译没开），没有帧可截");
            return;
        }
        TaskCompletionSource<SKBitmap> tcs;
        lock (_shotLock)
        {
            _shotTcs = tcs = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shotPending = true;
        }
        _ = tcs.Task.ContinueWith(t =>
        {
            SKBitmap bmp = t.Result;
            try
            {
                System.IO.Directory.CreateDirectory(targetDir);
                string path = System.IO.Path.Combine(targetDir, $"scrtrans-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(path, data.ToArray());
                _log.Info($"[screenshot] 已存 {bmp.Width}x{bmp.Height} → {path}");
            }
            catch (Exception ex)
            {
                _log.Error($"[screenshot] 保存失败: {ex.Message}");
            }
            finally { bmp.Dispose(); }
        }, TaskScheduler.Default);
    }

    private async void OnFrame(SKBitmap frame)
    {
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _log.Log($"[probe] 首帧到达 {frame.Width}x{frame.Height}（捕获→处理链路已通）");
        }
        _lastFrameWH = $"{frame.Width}x{frame.Height}";
        // 分辨率不缓存：预设相对坐标直接用当前帧宽高换算像素（见下方 presetParts/ToPixels），每帧天然拿到最新分辨率。
        // 全屏窗口下 WGC 帧池最大尺寸=整屏，任何游戏内分辨率都装得下，无需检测变化/重建捕获。
        // 截图(F6)：有挂起的请求就消费它，交出本帧深拷贝（原帧在本方法 finally 里 Dispose，不能直接引用）。
        TaskCompletionSource<SKBitmap>? shotTcs;
        lock (_shotLock)
        {
            if (_shotPending) { _shotPending = false; shotTcs = _shotTcs; _shotTcs = null; }
            else shotTcs = null;
        }
        if (shotTcs is not null)
        {
            try { shotTcs.SetResult(frame.Copy()); }
            catch (Exception ex) { _log.Error($"[screenshot] 帧拷贝失败: {ex.Message}"); } // TCS 留挂起，续延不跑，无副作用
        }
        // ── 两个独立节拍：场景检测(Ocr.SceneDetectPollMs，默认 20ms) 与 翻译(Ocr.TranslatePollMs，默认 200ms)。
        // 每帧实时读设置，改设置即时生效无需重启。识别很便宜要跑得快，翻译贵(OCR+LLM)按自己节拍跑。
        var ocr = _settings.Current.Ocr;
        int detectMs = PollInterval.SceneDetect(ocr), translateMs = PollInterval.Translate(ocr);
        _capture.MinIntervalMs = PollInterval.CaptureFloor(ocr); // 上游闸门=两者较快值，否则识别被翻译拖平
        var now = DateTime.Now;
        bool detectDue = (now - _lastDetect).TotalMilliseconds >= detectMs;
        bool translateDue = (now - _lastShot).TotalMilliseconds >= translateMs;
        if (!detectDue && !translateDue) { frame.Dispose(); return; } // 两个钟都没到，直接丢帧
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { frame.Dispose(); return; } // 单飞：上一帧还在处理
        try
        {
            // ── 场景识别必须在 ROI 可用性判断之前跑 ──
            // _roiAvailable 是识别的**结果**（切到"其他界面"或某场景没校准 → 它变 false），
            // 把它放在识别前面会让这些状态变成"只进不出"的死胡同：一旦进去就再没有帧去识别回来。
            // 实测症状：开局头两次识别正常，之后永远停在"其他界面"。
            if (detectDue)
            {
                _lastDetect = now;
                // 识别到切换时 SwitchScene 已同步清屏；本帧不翻（ROI 集刚变，bucket 需重取），下一帧按新场景继续。
                if (DetectScene(frame)) return;
            }
            if (!translateDue) return; // 这一拍只该跑识别：不裁剪不 OCR，省下开销
            _lastShot = now;
            if (!_roiAvailable)
            {
                if (!_roiMissingLogged)
                {
                    if (_activeScene == GameScene.Other)
                        _log.Debug("[probe] 其他界面（主菜单/加载等），本就不翻译"); // 其他态不催校准
                    else
                        _log.Info($"[probe] 当前场景[{_activeScene.Label()}]无预设识别区域——DEBUG 模式下可用校准窗拖调（直写 layout-presets.json）");
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
            // ── 每 part 独立收集 OCR 结果（下标=该 part 在当前预设列表里的位置）──
            // offsetX/offsetY：该 part 的文字偏移（物理像素，来自 RelRect），只影响译文摆放，不影响 OCR 区域
            var rois = new List<(string name, List<string> lines, List<RectangleF> bounds, double offsetX, double offsetY)>();
            foreach (var (name, rect) in presetParts(frame.Width, frame.Height))
            {
                Rectangle px = rect.ToPixels(frame.Width, frame.Height);
                if (px.Width <= 0 || px.Height <= 0) continue;
                using var sub = new SKBitmap(px.Width, px.Height);
                if (!frame.ExtractSubset(sub, new SKRectI(px.Left, px.Top, px.Right, px.Bottom)))
                {
                    _log.Warn($"[{name}] 裁剪失败 rect={px}");
                    continue;
                }
                var res = await _ocr.RecognizeAsync(sub);
                var lines = new List<string>();
                var bounds = new List<RectangleF>();
                foreach (var line in res.Lines)
                {
                    _log.Debug($"[{name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                    if (line.Confidence < MinLineConfidence)
                    {
                        _log.Debug($"[{name}][skip-low-conf] \"{line.Text}\" conf={line.Confidence:F2}");
                        continue;
                    }
                    // 内容闸门：无韩文的行（地图名、"Top vs Bottom"、表头…）根本不进翻译管线。
                    // 提示词要求"纯英文/数字/符号原样保留"，送过去只会拿回同样的英文 → 白花 API 且悬浮层显示一堆英文。
                    if (!KoreanText.ContainsHangul(line.Text))
                    {
                        _log.Debug($"[{name}][skip-无韩文] \"{line.Text}\"");
                        continue;
                    }
                    bounds.Add(new RectangleF(
                        line.Bounds.X + px.Left, line.Bounds.Y + px.Top,
                        line.Bounds.Width, line.Bounds.Height));
                    lines.Add(line.Text);
                }
                if (res.Lines.Count == 0)
                    _log.Debug($"[{name}] (空)");
                // 两帧共识：背景噪音一帧就过去，聊天文字持续在屏——只有连续两帧同文本的行才进下游。
                var (stableLines, stableBounds) = _stabilizer.Stabilize(rois.Count, lines, bounds);
                if (stableLines.Count != lines.Count)
                    _log.Debug($"[{name}][stable] OCR={lines.Count} 稳定={stableLines.Count}");
                rois.Add((name, stableLines, stableBounds, rect.OffsetX, rect.OffsetY));
            }
            // 调试面板逐行显示"被选用"的识别区域：本帧实际进 OCR 的有效列表。
            _lastEffectiveRoiNames = rois.Select(r => r.name).ToArray();

            // ── ROI 集变化（换场景/分辨率/增删）→ 清空 per-ROI 状态与悬浮层，全量重译 ──
            // ShowLines 是全量替换，所以未变化的 ROI 必须靠 _displayedPerRoi 里存的上次显示行来合并，
            // 否则这一帧没翻的 ROI 会从悬浮层上消失。
            var sig = string.Join("|", rois.Select(r => r.name));
            if (sig != _roiSetSignature)
            {
                _roiSetSignature = sig;
                _lastJoinedPerRoi.Clear();
                _displayedPerRoi.Clear();
                _stabilizer.ResetAll();
                _ = _overlay.Dispatcher.InvokeAsync(() =>
                {
                    _overlay.ShowLines(System.Array.Empty<OverlayItem>());
                    _overlay.ClearRegions(); // ROI 集变化（换场景/分辨率）=旧指纹区域框失效，清掉
                });
            }

            // ── 每 ROI 单独做变化检测：该 ROI 韩文与上一帧相同则整块跳过（不重译、不重显）──
            // 用编辑距离而非精确比对：OCR 对同一行会因渲染/背景抖动读出略不同的文本（差异 1~2 字），
            // 精确比对会把这种抖动当成"变化"→ 每拍重译 + 先隐藏再重显 → 周期性闪烁且白烧 CPU。
            // 差异 ≤10% 视为同一行（跳过）；真实新消息/列表刷新差异大，照常重译。
            var toTranslate = new List<int>(); // 变化的 ROI 下标
            for (int r = 0; r < rois.Count; r++)
            {
                var joined = string.Join("\n", rois[r].lines);
                string last = _lastJoinedPerRoi.TryGetValue(r, out var l) ? l : "";
                if (!TextsSimilar(joined, last)) toTranslate.Add(r);
            }
            // [diag] 变化检测诊断：每帧打印"被判变化"的 ROI 数与新旧文本。
            // 若每周期 变化>0 且新旧文本几乎相同 → OCR 抖动每拍被当"真变化"（闪烁+白译根因）；
            // 若文本稳定却仍被判变 → TextsSimilar/稳定器有 bug。跑一段后把这段日志发我即可定位。
            if (toTranslate.Count > 0)
                foreach (int r in toTranslate)
                {
                    string oldT = _lastJoinedPerRoi.TryGetValue(r, out string o) ? o : "";
                    string newT = string.Join(" ", rois[r].lines);
                    _log.Debug($"[diag][change] [{rois[r].name}] 旧=\"{oldT}\" → 新=\"{newT}\"");
                }
            _log.Debug($"[diag][change] 变化={toTranslate.Count}/{rois.Count}");
            if (toTranslate.Count == 0) return; // 所有 ROI 都没变 → 整帧跳过（finally 仍会 Dispose/复位 _busy）

            // ── 变化的 ROI 先隐藏旧译文（露出原始内容，避免旧译占位挡住观看），译完再显示新译文 ──
            // ShowLines 是全量替换：这里只摆「未变化」ROI 的行，变化 ROI 的旧行立即从悬浮层消失。
            var changedSet = new System.Collections.Generic.HashSet<int>(toTranslate);
            var keepItems = new List<OverlayItem>();
            foreach (var kv in _displayedPerRoi)
                if (!changedSet.Contains(kv.Key)) keepItems.AddRange(kv.Value);
            _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(keepItems));

            // ── 只翻译变化的 ROI 的行（一批一请求）──
            var koLines = new List<string>();
            var metas = new List<(RectangleF frameBounds, string roiName, int roiIdx, double offsetX, double offsetY)>();
            foreach (int r in toTranslate)
            {
                for (int i = 0; i < rois[r].lines.Count; i++)
                {
                    koLines.Add(rois[r].lines[i]);
                    metas.Add((rois[r].bounds[i], rois[r].name, r, rois[r].offsetX, rois[r].offsetY));
                }
            }

            var ctx = new TranslationContext(SceneId, _glossaryHint);
            int timeout = Math.Max(1, _settings.Current.Translation.TimeoutSeconds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await _orchestrator.TranslateAsync(koLines, ctx, cts.Token);
            // 翻译等待期间若被 F9 Stop()，_running 已置 false：丢弃本次结果不上屏。
            // 不拦的话，这里的 ShowLines(allItems) 会排在 Stop() 的清空之后，把刚关掉的译文重新画上。
            if (!_running) return; // finally 仍会 Dispose(frame) 并复位 _busy
            sw.Stop();
            _lastTranslateMs = sw.ElapsedMilliseconds; _lastAllResolved = outcome.AllResolved; _lastLineCount = koLines.Count;
            _lastEngineHits = outcome.EngineHits; _lastCacheHits = outcome.CacheHits; _lastGlossaryHits = outcome.GlossaryHits;

            // ── 上屏：变化的 ROI 重算 items；未变化的 ROI 沿用上次显示的行；合并后整窗摆放 ──
            var itemsPerRoi = new Dictionary<int, List<OverlayItem>>();
            for (int k = 0; k < koLines.Count; k++)
            {
                string zh = outcome.Texts[k];
                _log.Debug($"[{metas[k].roiName}][{k}] \"{koLines[k]}\" → \"{zh}\" {sw.ElapsedMilliseconds}ms");
                // **原文照抄的行不上屏**：provider 失败/超时时编排会回落原文，
                // 把它当译文显示出去，用户看到的就是"识别到韩文却没翻译"，比什么都不显示更误导。
                if (zh == koLines[k]) continue;
                var (frameBounds, _, roiIdx, offsetX, offsetY) = metas[k];
                var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
                if (!itemsPerRoi.TryGetValue(roiIdx, out var list)) itemsPerRoi[roiIdx] = list = new List<OverlayItem>();
                // Source=该行韩文原文：供悬浮层"原文+译文双行"模式使用（单行模式忽略）。
                // offsetX/offsetY：该 part 的文字偏移（物理像素），叠加到译文摆放位置（0=不偏移）。
                list.Add(new OverlayItem(zh, scr.X + offsetX, scr.Y + offsetY, koLines[k]));
            }
            // 变化的 ROI：更新其显示行（没翻出任何行的 → 清掉旧显示）
            foreach (var kv in itemsPerRoi) _displayedPerRoi[kv.Key] = kv.Value;
            foreach (int r in toTranslate)
                if (!itemsPerRoi.ContainsKey(r)) _displayedPerRoi.Remove(r);
            var allItems = new List<OverlayItem>();
            foreach (var kv in _displayedPerRoi) allItems.AddRange(kv.Value);
            _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(allItems));

            // ── 仅当整批都翻出来才提交各变化 ROI 的变化检测，否则下一帧同内容重试 ──
            if (outcome.AllResolved)
            {
                foreach (int r in toTranslate)
                    _lastJoinedPerRoi[r] = string.Join("\n", rois[r].lines);
                // 累计翻译行数：整批 AllResolved 时，本批每行都算成功翻译，按行归属的 ROI 累加。
                foreach (var m in metas)
                    _cumTranslated.AddOrUpdate(m.roiName, 1L, (_, v) => v + 1);
            }
            _log.Debug($"[probe] 上屏完成 rois={rois.Count} 变化={toTranslate.Count} lines={koLines.Count} 显示={allItems.Count} {sw.ElapsedMilliseconds}ms AllResolved={outcome.AllResolved}");

            // 翻译失败：同一原因只记一次日志（否则每拍一条），原因变了再记。
            // 编排器 onError 已对"抛异常"的失败即时记 Error；这里补"被跳过/全不可用"这类没有异常、
            // 只回落原文的失败，让日志完整（异常类失败会同时出现在两处，可接受）。
            if (outcome.Error is not null && outcome.Error != _lastTranslateError)
                _log.Warn($"[probe] 翻译未成：{outcome.Error}。请检查 配置→翻译 的密钥/登录/池顺序/启用项。");
            _lastTranslateError = outcome.Error;
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
