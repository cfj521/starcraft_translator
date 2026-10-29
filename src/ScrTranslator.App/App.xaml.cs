using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScrTranslator.App.Auth;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.App.Secrets;
using ScrTranslator.App.Services;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Scene;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.App;

public partial class App : Application
{
    private IHost? _host;
    private TrayService? _trayService;
    private HotkeyService? _hotkeys;
    private Action? _openSettingsAction;
    private System.Threading.Mutex? _instanceMutex; // 单实例守护：托盘应用重复启动会多悬浮层/抢热键
    private readonly object crashLogGate = new();   // crash.log 并发写保护（UI 线程 + 线程池同时崩溃时会撞文件锁）

    /// <summary>公开给外部（M4：Ctrl+F9 全局热键接线）打开配置窗口。</summary>
    public void OpenSettingsWindow() => _openSettingsAction?.Invoke();

    // 单实例锁：必须用 WaitOne 判"是否拿到所有权"，不能用 new Mutex(initiallyOwned:true,...,createdNew:out ...)。
    // createdNew 只表示"内核对象是不是新建的"：重启时旧进程 ReleaseMutex 只放所有权、句柄仍在，
    // 新进程拿到的 createdNew=false（却确实持有锁）→ 被误判成"已在运行"→ 弹窗 + Exit(0)，
    // 而旧进程已 Shutdown → 两个进程都没了，应用直接消失。
    private bool _ownsMutex;

    /// <summary>重启 App：释放单实例锁 → 启动新进程 → 关闭当前进程。
    /// 用于"改 CPU/GPU 设备需重启生效"——引擎是单例、构造时定死执行器，只有重启才能换。</summary>
    public void RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;
        ReleaseInstanceMutex();   // 让新进程能拿到锁（只释放一次；OnExit 不会再释放）
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,   // 脱离当前进程树独立启动
            });
        }
        catch { /* 启动失败不影响当前进程继续退出 */ }
        Shutdown();   // 走完整 WPF 关闭流程（释放捕获/热键/托盘）
    }

    /// <summary>释放单实例锁（幂等）：ReleaseMutex 对未持有者会抛 ApplicationException，
    /// 旧实现在 RestartApp 与 OnExit 各释放一次 → OnExit 抛异常，Dispose/base.OnExit 都不再执行。</summary>
    private void ReleaseInstanceMutex()
    {
        if (_instanceMutex is null || !_ownsMutex) return;
        _ownsMutex = false;
        try { _instanceMutex.ReleaseMutex(); } catch { /* 已释放/所有权丢失：忽略 */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：命名 Mutex 持有整个进程生命周期。第二个实例提示后强制退出，避免重复悬浮窗/热键/捕获。
        // 进程崩溃时 OS 自动释放(abandoned mutex)，不会留死锁。
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: false, name: "ScrTranslator_SingleInstance");
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true; // 前一个进程崩溃留下的 abandoned mutex：所有权照样拿到
        }
        if (!_ownsMutex)
        {
            MessageBox.Show("SC:R 翻译器已在运行（请查看系统托盘图标）。", "已在运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0); // 强制终止，不依赖 OnStartup 阶段 Shutdown 的边界行为
            return;
        }

        // 全局异常兜底：写 crash.log，且 UI 线程异常不再静默杀进程（诊断用）。
        string crashLog = Path.Combine(AppContext.BaseDirectory, "logs", "crash.log");
        Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
        // UTF-8 带 BOM：与 FileLogger 一致，Get-Content/记事本自动识别（新文件写 BOM，追加时 position>0 自动跳过）。
        var crashEnc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        void LogCrash(string src, object? ex)
        {
            // 加锁 + 失败静默：File.AppendAllText 无锁，UI 线程与线程池同时崩溃时会互相抢文件锁 →
            // 异常从 DispatcherUnhandledException 处理器里再抛 → 原始异常丢失、进程直接终止。
            lock (crashLogGate)
            {
                try { File.AppendAllText(crashLog, $"{DateTime.Now:HH:mm:ss.fff} [{src}] {ex}\n", crashEnc); }
                catch { /* 写不进去也不能在异常处理器里再抛 */ }
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true; // 保持进程存活，让日志可读、悬浮/托盘不被杀
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain", args.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<OverlayWindow>();
        builder.Services.AddSingleton<DebugWindow>();

        string baseDir = AppContext.BaseDirectory;
        builder.Services.AddSingleton<FileLogger>(_ =>
            new FileLogger(Path.Combine(baseDir, "logs", "app.log")));
        // numThread 取实时配置的 OcrThreads（默认 4）、device 取 Ocr.Device（CPU/GPU）。OCR 引擎是单例、
        // 线程数与设备在首次解析时定死，改 OcrThreads/Device 需重启应用生效（RapidOcrNet.InitModels 在构造时一次性设定）。
        builder.Services.AddSingleton<IOcrEngine>(sp =>
        {
            var ocr = sp.GetRequiredService<SettingsHolder>().Current.Ocr;
            // 后台初始化失败（InitModels 抛错）走日志；缺模型文件仍在构造同步快速失败。
            return new PaddleOcrEngine(Path.Combine(baseDir, "models"), ocr.OcrThreads, ocr.Device,
                msg => sp.GetRequiredService<FileLogger>().Error(msg));
        });
        builder.Services.AddSingleton<CaptureService>();

        // --- 翻译管线 DI（B3）---
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        // 场景识别参考（两层，故意分文件）：
        //  出厂层 exe 旁 scene-refs.json，由 tools/SceneRefGen 生成，带 sha256 校验，程序只读；
        //  覆盖层 %APPDATA%\ScrTranslator\scene-overrides.json，放用户现场采集的参考，升级 exe 不会丢。
        string sceneRefsPath = Path.Combine(baseDir, SceneRefStore.DefaultFileName);
        string sceneOverridesPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, SceneRefStore.OverridesFileName);
        builder.Services.AddSingleton(_ => SceneRefStore.Load(sceneRefsPath, sceneOverridesPath));
        builder.Services.AddSingleton<SceneRecognizer>(sp => new SceneRecognizer(sp.GetRequiredService<SceneRefStore>()));

        // 界面布局预设（exe 旁 layout-presets.json）：出厂即唯一权威，配置页/校准窗直写本文件。
        // 缺文件/损坏时 LayoutPresetStore.LoadError 有值，运行时该场景不翻译。
        builder.Services.AddSingleton(_ => LayoutPresetStore.Load(
            Path.Combine(baseDir, LayoutPresetStore.DefaultFileName)));

        // 注入 FileLogger：secrets.dat 读/写失败、OAuth 登录/刷新失败这些"静默吞异常"点从此有痕可查。
        builder.Services.AddSingleton(sp => new SecretStore(sp.GetRequiredService<FileLogger>()));
        builder.Services.AddSingleton(new HttpClient());
        builder.Services.AddSingleton(sp => new OpenAiOAuth(
            sp.GetRequiredService<SecretStore>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<FileLogger>()));
        builder.Services.AddSingleton<ITokenSource>(sp => sp.GetRequiredService<OpenAiOAuth>());
        // 可变，托盘切引擎改 .Current；启动时从磁盘加载，防 Save-without-Load。
        builder.Services.AddSingleton(new SettingsHolder { Current = SettingsStore.Load(settingsPath) });
        builder.Services.AddSingleton(_ => Glossary.LoadFromMarkdown(Path.Combine(baseDir, "GLOSSARY.md")));
        builder.Services.AddSingleton<TranslationCache>();
        builder.Services.AddSingleton(sp => new OpenAiCompatibleChatClient(
            sp.GetRequiredService<HttpClient>(),
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            () => sp.GetRequiredService<SecretStore>().Load().DashScopeApiKey));
        builder.Services.AddSingleton(sp => new CodexResponsesClient(sp.GetRequiredService<HttpClient>()));
        // 四个翻译 provider 全按 ITranslationProvider 注册进池。密钥都走 SecretStore（DPAPI 加密），不进 settings.json。
        builder.Services.AddSingleton<ITranslationProvider>(sp => new DashScopeProvider(
            sp.GetRequiredService<OpenAiCompatibleChatClient>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.DashScopeModel,
            () => sp.GetRequiredService<SecretStore>().Load().DashScopeApiKey)); // 第三个参数供 GetStatus 判 key 是否已配
        // OpenAI API Key 模式：与 DashScope 同款 OpenAiCompatibleChatClient，仅 baseUrl 指向官方 api.openai.com/v1。
        // client 内联在此（不另注册单例），否则 GetRequiredService<OpenAiCompatibleChatClient> 会二义。
        builder.Services.AddSingleton<ITranslationProvider>(sp => new OpenAiApiProvider(
            new OpenAiCompatibleChatClient(
                sp.GetRequiredService<HttpClient>(),
                "https://api.openai.com/v1",
                () => sp.GetRequiredService<SecretStore>().Load().OpenAiApiKey),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.OpenAiApiModel,
            () => sp.GetRequiredService<SecretStore>().Load().OpenAiApiKey)); // 第三个参数供 GetStatus 判 key 是否已配
        // 自定义模型（OpenAI 兼容）：baseUrl/模型 ID 取自实时配置（用户可随时改），KEY 走 SecretStore。
        // provider 内部每次调用现建 client（baseUrl 可变，不复用单例）。
        builder.Services.AddSingleton<ITranslationProvider>(sp => new CustomModelProvider(
            sp.GetRequiredService<HttpClient>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.CustomModelId,
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.CustomModelUrl,
            () => sp.GetRequiredService<SecretStore>().Load().CustomModelApiKey)); // 第三个参数供 GetStatus 判 key 是否已配
        builder.Services.AddSingleton<ITranslationProvider>(sp => new ChatGptSubscriptionProvider(
            sp.GetRequiredService<CodexResponsesClient>(),
            sp.GetRequiredService<ITokenSource>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.ChatGptModel));
        builder.Services.AddSingleton<ITranslationProvider>(sp => new AliyunMtProvider(
            sp.GetRequiredService<HttpClient>(),
            () => { var a = sp.GetRequiredService<SecretStore>().Load().Aliyun; return (a.AccessKeyId, a.AccessKeySecret); }));
        // 本地 Hy-MT2（腾讯翻译模型，纯 C# 进程内推理，库模式）。模型路径取自实时配置（用户可随时改）；
        // 懒加载：首次翻译才加载 GGUF 权重，禁用/未配置时不加载、不占内存。
        builder.Services.AddSingleton(sp => new HyMt2Provider(
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.HyMt2ModelPath,
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.HyMt2Backend,
            msg => sp.GetRequiredService<FileLogger>().Error(msg),
            msg => sp.GetRequiredService<FileLogger>().Info(msg)));
        builder.Services.AddSingleton<ITranslationProvider>(sp => sp.GetRequiredService<HyMt2Provider>());
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsHolder>();
            // 全部 provider 实例（按接口取），建 Id→实例 映射，供池按 settings 顺序/启用过滤。
            var all = sp.GetServices<ITranslationProvider>().ToList();
            var byId = all.ToDictionary(p => p.Name);

            // provider 池：按 settings.Providers 的顺序返回「选中组」里已启用的 provider。用 Func 每次现取，
            // 配置页拖拽排序/启用禁用/切换组即时生效，不必重建本对象。
            // 内部/外部**单选**：只有 SelectedGroup 那组的 provider 参与，另一组完全不参与（不是回退链）。
            IReadOnlyList<ITranslationProvider> Pool() =>
                settings.Current.Translation.Providers
                    .Where(p => p.Enabled && byId.ContainsKey(p.Id)
                        && TranslationSettings.GroupOf(p.Id) == settings.Current.Translation.SelectedGroup)
                    .Select(p => byId[p.Id])
                    .ToList();

            return new TranslationOrchestrator(
                sp.GetRequiredService<Glossary>(),
                sp.GetRequiredService<TranslationCache>(),
                Pool,
                // provider 异常必须冒到日志：以前是 catch 掉直接回落原文，
                // 症状就是"识别到韩文但显示的还是原文"，且无处可查原因（实测是空 API Key）。
                msg => sp.GetRequiredService<FileLogger>().Error(msg));
        });

        builder.Services.AddSingleton(sp => new TrayService(sp.GetRequiredService<FileLogger>()));
        builder.Services.AddSingleton(sp => new TranslationEngine(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<SceneRecognizer>(),
            sp.GetRequiredService<LayoutPresetStore>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            sceneRefsPath,
            sceneOverridesPath,
            sp.GetRequiredService<TranslationOrchestrator>(),
            sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<Glossary>(),
            sp.GetRequiredService<DebugWindow>(),
            sp.GetRequiredService<HyMt2Provider>()));

        _host = builder.Build();

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        // 调试窗口：只建句柄、不 Show()，杜绝启动瞬间的闪现。之后由探测定时器按前台+DebugMode 切 Visibility。
        // Show() 会先让窗口出现在屏幕上再被置 Hidden → 启动闪一下；EnsureHandle() 只建 HWND，窗口始终处于 Hidden。
        var debug = _host.Services.GetRequiredService<DebugWindow>();
        new System.Windows.Interop.WindowInteropHelper(debug).EnsureHandle();

        _trayService = _host.Services.GetRequiredService<TrayService>();
        _trayService.Initialize();

        var log = _host.Services.GetRequiredService<FileLogger>();
        var startupSettings = _host.Services.GetRequiredService<SettingsHolder>();
        log.DebugEnabled = startupSettings.Current.General.DebugMode; // 启动即同步，避免首帧诊断漏记
        log.Info($"──── SC:R Translator v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} 启动 ────");
        log.Info($"[app][basic] 配置={settingsPath} | 调试日志={(log.DebugEnabled ? "开(全量诊断)" : "关(仅业务)")}");

        var probe = _host.Services.GetRequiredService<TranslationEngine>();
        bool running = false;
        void ToggleProbe()
        {
            if (running)
            {
                probe.Stop();
                running = false;
                _trayService.SetProbeRunning(false);
                log.Info("[app][basic] 翻译已关闭（F9）");
                return;
            }
            var info = probe.Start();
            // WaitingForWindow / WaitingForModels 时引擎已经在跑（看门狗守窗口、后台加载模型），
            // 旧实现只认 Ok → 这两态下托盘显示"开启翻译"，再按 F9 走的是 Start 而不是 Stop，关不掉。
            running = info.Status is ProbeStart.Ok or ProbeStart.WaitingForWindow or ProbeStart.WaitingForModels;

            // 凭据体检：OCR 认得出韩文、但 provider 池里没有任何可用 provider 时，症状是"字出来了却没翻"，
            // 而且失败发生在网络层、离配置页很远——必须在开翻译这一刻就说破（只进日志，不弹气泡）。
            if (running)
            {
                var t = _host.Services.GetRequiredService<SettingsHolder>().Current.Translation;
                // 只体检「选中组」里启用的 provider（内部/外部单选，另一组根本不参与翻译，不该被点名）。
                var enabled = _host.Services.GetServices<ITranslationProvider>()
                    .Where(p => t.Providers.Any(pe => pe.Id == p.Name && pe.Enabled
                        && TranslationSettings.GroupOf(pe.Id) == t.SelectedGroup))
                    .ToList();
                var usable = enabled.Where(p => p.GetStatus().Usable).ToList();
                if (usable.Count == 0 && enabled.Count > 0)
                {
                    string reasons = string.Join("；", enabled.Select(p =>
                        $"{p.Name}（{p.GetStatus().Detail}）"));
                    log.Warn($"[app][basic] 开翻译但选中组里启用的 provider 全都不可用：{reasons}。韩文能识别但翻不成中文，请到 配置→翻译 配密钥/登录或启用其它 provider（或切换到另一组）。");
                }
            }

            string result = info.Status switch
            {
                ProbeStart.Ok => "已开启",
                ProbeStart.WaitingForWindow => $"未找到游戏窗口（进程 {info.ProcessName}），转入等待：窗口出现后自动开始翻译（游戏需窗口化/无边框）",
                ProbeStart.WaitingForModels => "模型加载中（OCR/HY-MT2），就绪后自动开始翻译",
                ProbeStart.CaptureUnsupported => "无法开启：系统不支持窗口捕获（需 Win10 1803+，且非 WSL）",
                ProbeStart.NoRoi => "预设加载失败（layout-presets.json 不可用或为空）：请确认文件存在且完好，详见启动日志",
                ProbeStart.WindowNotFound => $"{info.ProcessName} 窗口查找异常：请确认游戏已以窗口化/无边框模式启动",
                ProbeStart.CaptureFailed => "启动屏幕捕获失败，详见日志",
                _ => "无法开启",
            };
            log.Info($"[app][basic] 翻译开启结果：{result}");
            _trayService.SetProbeRunning(running);
        }
        _trayService.ToggleProbe = ToggleProbe;

        // Phase 5 异常恢复：看门狗/识别事件不再弹气泡（游戏前台看不到），统一靠 TranslationEngine 自己的
        // 日志（_log.Info/Warn/Error，含翻译失败）+ 配置→日志页 查看。这里只做一件事：
        // 托盘"开启/关闭翻译"文本随实际运行态变化——自动续译(首连/重连)→开，窗口丢失→关。
        // （最小化/还原不改运行态——探测仍在捕获翻译，只是暂不显示）
        probe.Notice += (kind, _) => Dispatcher.InvokeAsync(() =>
        {
            if (kind is ProbeNotice.FirstConnected or ProbeNotice.Reconnected) { running = true; _trayService.SetProbeRunning(true); }
            else if (kind is ProbeNotice.WindowLost) { running = false; _trayService.SetProbeRunning(false); }
        });

        // 场景识别参考的启动体检：缺文件/缺分辨率是"翻译突然不工作"的头号原因，必须在日志第一屏说清楚。
        var sceneStore = _host.Services.GetRequiredService<SceneRefStore>();
        if (sceneStore.IsUsable)
        {
            var resList = string.Join(",", sceneStore.References
                .SelectMany(r => r.Resolutions.Keys).Distinct().OrderBy(k => k));
            log.Info($"[app][basic] 场景参考={sceneStore.References.Count}条[{string.Join(" ", sceneStore.References.Select(r => r.Id))}] " +
                     $"| 已收录分辨率 {resList} | 现场覆盖 {sceneStore.OverrideCount} 条 | 块{sceneStore.BlockSize} 容差{sceneStore.ChannelTolerance} 阈值{sceneStore.MinHitRatio:P0}");
            foreach (var w in sceneStore.Warnings) log.Warn($"[app][basic] 场景参考告警: {w}");
        }
        else
        {
            log.Error($"[app][basic] 场景参考不可用：{sceneStore.LoadError} —— 场景识别将停在“其他界面”，翻译不会上屏。" +
                      "请确认 exe 旁存在 scene-refs.json（或重跑 tools/SceneRefGen 生成）");
        }

        // 校准窗口：单例（已开则激活），Ctrl+F9/托盘"进入校准…"打开。窗口关闭后置空以便下次可重新 new（WPF Window 一次性）。
        Calibration.CalibrationWindow? calibrationWindow = null;
        void OpenCalibration()
        {
            var holder = _host!.Services.GetRequiredService<SettingsHolder>();
            // DEBUG 门控：校准直写出厂预设，仅 DEBUG 模式可用（F8 切换）
            if (!holder.Current.General.DebugMode)
            {
                log.Info("[app][basic] 校准仅 DEBUG 模式可用（按 F8 切换）");
                return;
            }
            // 校准的前提是"看得见游戏画面"：校准窗是半透明覆盖层，且要靠抓屏做场景检测。
            // 游戏没开 / 在后台 / 最小化时框出来的东西全是凭空的 → 直接拒绝并说清原因。
            string proc = string.IsNullOrWhiteSpace(holder.Current.General.TargetProcess)
                ? "StarCraft" : holder.Current.General.TargetProcess.Trim();
            var hwnd = WindowFinder.FindByProcess(proc);
            if (hwnd == IntPtr.Zero)
            {
                log.Info($"[app][basic] 无法校准：未找到游戏窗口（进程 {proc}）。请先启动 SC:R（窗口化/无边框）。");
                return;
            }
            if (NativeMethods.IsIconic(hwnd))
            {
                log.Info("[app][basic] 无法校准：游戏处于最小化状态，请先还原窗口再按 Ctrl+F9。");
                return;
            }
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                log.Info("[app][basic] 无法校准：游戏不在前台，校准会看不清要对齐的画面。请点一下游戏窗口再按 Ctrl+F9。");
                return;
            }

            if (calibrationWindow is not null) { calibrationWindow.Activate(); return; }
            calibrationWindow = new Calibration.CalibrationWindow(
                log,
                () => probe.ReloadLayoutPresets(), // 校准窗直写预设（layout-presets.json），重载即生效
                _host.Services.GetRequiredService<SceneRecognizer>(),
                probe.Layout); // 传 probe 重载后的实例，而非 DI 单例——保存后 ReloadLayoutPresets 只更新 probe 字段，DI 单例是启动时的旧值
            calibrationWindow.Closed += (_, _) => calibrationWindow = null;
            calibrationWindow.Show();
            calibrationWindow.Activate();
        }
        _trayService.OpenCalibration = OpenCalibration;

        // 配置窗口：单例（已开则激活），托盘"配置…"/双击托盘图标打开；窗口关闭后置空以便下次可重新 new。
        var settingsHolder = _host.Services.GetRequiredService<SettingsHolder>();
        var secretStoreForSettings = _host.Services.GetRequiredService<SecretStore>();
        var oauthForSettings = _host.Services.GetRequiredService<OpenAiOAuth>();
        Config.SettingsWindow? settingsWindow = null;
        void OpenSettings()
        {
            if (settingsWindow is not null) { settingsWindow.Activate(); return; }
            settingsWindow = new Config.SettingsWindow(
                settingsHolder,
                settingsPath,
                secretStoreForSettings,
                oauthForSettings,
                log,
                OpenCalibration,
                probe.OnTranslationSettingsChanged,
                _hotkeys!, // 此时尚未 new，但 OpenSettings 只会在其后被调用（字段，非局部变量捕获）
                () => probe.CurrentSceneId,
                // 区域页保存后重载预设（直写出厂 layout-presets.json）；DI 里的 store 是单例，不重读盘就是旧值。
                () => probe.ReloadLayoutPresets(),
                probe.Layout,   // 区域页要展示布局预设行；传 probe 重载后的实例而非 DI 单例（保存后 DI 单例是启动旧值）
                _host.Services.GetRequiredService<HyMt2Provider>()); // 保存时按选中组冷加载/卸载本地模型
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }
        _trayService.OpenSettings = OpenSettings;
        _openSettingsAction = OpenSettings;

        // DEBUG 面板开关（F8）：翻转 General.DebugMode + 落盘；探测的 500ms 定时器随即显隐面板。
        void ToggleDebug()
        {
            var cur = settingsHolder.Current;
            bool newVal = !cur.General.DebugMode;
            settingsHolder.Current = cur with { General = cur.General with { DebugMode = newVal } };
            log.DebugEnabled = newVal; // 即时生效，诊断日志与面板同步切换（探测定时器亦会兜底同步）
            try { SettingsStore.Save(settingsHolder.Current, settingsPath); }
            catch (Exception ex) { log.Error($"[app][hotkey] 保存 settings.json 失败: {ex.Message}"); }
            log.Info($"[app][basic] DEBUG 模式: {(newVal ? "开（全量诊断日志+面板）" : "关（仅业务日志）")}");
        }

        // 复制诊断快照（Ctrl+F8）：把面板同源文本拷到剪贴板，无需面板可点/不抢焦点。
        void CopyDebug()
        {
            try { Clipboard.SetText(probe.GetDebugSnapshot()); log.Info("[app][basic] 诊断快照已复制到剪贴板（Ctrl+F8）"); }
            catch (Exception ex) { log.Warn($"[app][basic] 复制诊断失败: {ex.Message}"); }
        }

        // 截图保存（F6）：把下一帧捕获帧存成 PNG 到 <项目根>/screenshots/。
        // 图与翻译看到的逐像素一致（同一 WGC 帧流），可拿来跟游戏自带 F12 截图并排核对捕获方式/分辨率/黑边。
        void Screenshot()
        {
            string dir;
            try { dir = System.IO.Path.Combine(FindProjectRoot(), "screenshots"); }
            catch (Exception ex) { log.Warn($"[app][basic] 无法确定项目目录，跳过截图: {ex.Message}"); return; }
            log.Info($"[app][basic] 请求截图（F6），下一帧存到 {dir}");
            probe.Screenshot(dir);
        }

        // 项目根 = exe 往上第一个含 .git 的目录（repo 根）。发布版无 .git 则退回 exe 所在目录。
        static string FindProjectRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return System.AppContext.BaseDirectory;
        }

        // 全局热键：从 SettingsHolder.Current.Hotkeys 驱动注册全部五功能（游戏有焦点时也生效）。
        _hotkeys = new HotkeyService(overlay);
        var hotkeyActions = new Dictionary<string, Action>
        {
            ["ToggleTranslate"] = ToggleProbe,
            ["Calibrate"] = OpenCalibration,
            ["OpenConfig"] = OpenSettings,
            ["ToggleDebug"] = ToggleDebug,
            ["CopyDebug"] = CopyDebug,
            ["Screenshot"] = Screenshot,
        };
        var hotkeyResults = _hotkeys.RegisterAll(settingsHolder.Current.Hotkeys, hotkeyActions);
        foreach (var (func, ok) in hotkeyResults)
            log.Log($"[app][hotkey] 注册 {func}={ok}");

        // 启动自动开翻译（General.AutoTranslateOnLaunch）：游戏没开也不报错——看门狗会等待窗口出现自动续译。
        if (settingsHolder.Current.General.AutoTranslateOnLaunch)
        {
            log.Log("[app][startup] AutoTranslateOnLaunch=on，自动开启翻译");
            ToggleProbe();
        }

        // 本地 Hy-MT2 模型后台预加载：选中内部组时，app 启动完成后立即后台加载权重（延迟加载→预加载），
        // 让用户开始翻译时模型已就绪、无"首帧翻译加载等待"。启动时只跑这一次；配置页保存时切组的加载/卸载由 SettingsWindow 处理。
        var hyMt2 = _host.Services.GetRequiredService<HyMt2Provider>();
        if (settingsHolder.Current.Translation.SelectedGroup == TranslationGroup.Internal)
        {
            hyMt2.Load();
            log.Info("[app][startup] 选中内部组，Hy-MT2 模型后台加载中");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 先停翻译循环（停捕获、停看门狗、清屏），确保没有正在跑的 OCR 帧再销毁 OCR 引擎，避免边跑边销毁。
        try { _host?.Services.GetRequiredService<TranslationEngine>().Stop(); } catch { }
        _hotkeys?.Dispose();
        _trayService?.Dispose();
        _host?.Dispose(); // OCR 引擎的重活销毁已甩后台线程（PaddleOcrEngine.Dispose 幂等、后台执行），不再卡 UI 线程
        ReleaseInstanceMutex(); // 幂等：RestartApp 里若已释放过，这里不再重复（重复 ReleaseMutex 会抛 ApplicationException）
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
