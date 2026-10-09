using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScrTranslator.App.Auth;
using ScrTranslator.App.Capture;
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

    /// <summary>公开给外部（M4：Ctrl+F9 全局热键接线）打开配置窗口。</summary>
    public void OpenSettingsWindow() => _openSettingsAction?.Invoke();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：命名 Mutex 持有整个进程生命周期。第二个实例提示后强制退出，避免重复悬浮窗/热键/捕获。
        // 进程崩溃时 OS 自动释放(abandoned mutex)，不会留死锁。
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, name: "ScrTranslator_SingleInstance", createdNew: out bool isFirst);
        if (!isFirst)
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
        void LogCrash(string src, object? ex) =>
            File.AppendAllText(crashLog, $"{DateTime.Now:HH:mm:ss.fff} [{src}] {ex}\n", crashEnc);

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
        builder.Services.AddSingleton<IOcrEngine>(_ => new PaddleOcrEngine(Path.Combine(baseDir, "models")));
        builder.Services.AddSingleton<CaptureService>();

        // --- 翻译管线 DI（B3）---
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        // ROI 校准数据也放 %APPDATA%（此前在 exe 旁，每次重建/发布被 PreserveNewest 覆盖丢失）。
        // 一次性迁移：APPDATA 无、但旧位置有 → 复制过来，保住既有校准。
        string roisPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "rois.json");
        string legacyRois = Path.Combine(baseDir, "rois.json");
        if (!File.Exists(roisPath) && File.Exists(legacyRois))
            try { File.Copy(legacyRois, roisPath); } catch { /* 迁移失败则按无校准处理，F8 重建即可 */ }

        // 场景识别参考（两层，故意分文件）：
        //  出厂层 exe 旁 scene-refs.json，由 tools/SceneRefGen 生成，带 sha256 校验，程序只读；
        //  覆盖层 %APPDATA%\ScrTranslator\scene-overrides.json，放用户现场采集的参考，升级 exe 不会丢。
        string sceneRefsPath = Path.Combine(baseDir, SceneRefStore.DefaultFileName);
        string sceneOverridesPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, SceneRefStore.OverridesFileName);
        builder.Services.AddSingleton(_ => SceneRefStore.Load(sceneRefsPath, sceneOverridesPath));
        builder.Services.AddSingleton<SceneRecognizer>(sp => new SceneRecognizer(sp.GetRequiredService<SceneRefStore>()));

        builder.Services.AddSingleton<SecretStore>();
        builder.Services.AddSingleton(new HttpClient());
        builder.Services.AddSingleton<OpenAiOAuth>();
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
        builder.Services.AddSingleton(sp => new DashScopeProvider(
            sp.GetRequiredService<OpenAiCompatibleChatClient>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.DashScopeModel));
        builder.Services.AddSingleton(sp => new ChatGptSubscriptionProvider(
            sp.GetRequiredService<CodexResponsesClient>(),
            sp.GetRequiredService<ITokenSource>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Translation.ChatGptModel));
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsHolder>();
            var dashScope = sp.GetRequiredService<DashScopeProvider>();       // 绑 Current.Translation.DashScopeModel
            var chatGpt = sp.GetRequiredService<ChatGptSubscriptionProvider>();

            // 单活跃引擎：用 Func 每次现取，托盘/配置切引擎即时生效，不必重建本对象。
            ITranslationProvider Active() =>
                settings.Current.Translation.Active == TranslationEngine.DashScope ? dashScope : chatGpt;

            return new TranslationOrchestrator(
                sp.GetRequiredService<Glossary>(),
                sp.GetRequiredService<TranslationCache>(),
                Active,
                // provider 异常必须冒到日志：以前是 catch 掉直接回落原文，
                // 症状就是"识别到韩文但显示的还是原文"，且无处可查原因（实测是空 API Key）。
                msg => sp.GetRequiredService<FileLogger>().Error(msg));
        });

        builder.Services.AddSingleton(sp => new TrayService(
            sp.GetRequiredService<OverlayWindow>(),
            sp.GetRequiredService<OpenAiOAuth>(),
            sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<SecretStore>(),
            sp.GetRequiredService<FileLogger>(),
            settingsPath));
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<SceneRecognizer>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            roisPath,
            sp.GetRequiredService<TranslationOrchestrator>(),
            sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<Glossary>(),
            sp.GetRequiredService<DebugWindow>()));

        _host = builder.Build();

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        // 调试窗口：先 Show() 建句柄再置 Hidden（ShowActivated=False 不抢焦点）。之后由探测定时器按前台+DebugMode 切 Visibility。
        var debug = _host.Services.GetRequiredService<DebugWindow>();
        debug.Show();
        debug.Visibility = Visibility.Hidden;

        _trayService = _host.Services.GetRequiredService<TrayService>();
        _trayService.Initialize();

        var probe = _host.Services.GetRequiredService<Phase1Probe>();
        bool running = false;
        void ToggleProbe()
        {
            if (running)
            {
                probe.Stop();
                running = false;
                _trayService.ShowToast("翻译", "已关闭");
                return;
            }
            var info = probe.Start(); // 首次引导：失败原因不再只进日志，托盘气泡直出
            running = info.Status == ProbeStart.Ok;

            // 凭据体检：OCR 认得出韩文、但翻译引擎没钥匙时，症状是"字出来了却没翻"，
            // 而且失败发生在网络层、离配置页很远——必须在开翻译这一刻就说破。
            if (running)
            {
                var tr = _host!.Services.GetRequiredService<SettingsHolder>().Current.Translation;
                var sec = _host.Services.GetRequiredService<SecretStore>().Load();
                string? missing = tr.Active switch
                {
                    TranslationEngine.DashScope when string.IsNullOrWhiteSpace(sec.DashScopeApiKey)
                        => "当前引擎＝阿里云 DashScope，但 API Key 是空的",
                    TranslationEngine.ChatGpt when string.IsNullOrWhiteSpace(sec.OpenAi.AccessToken)
                        => "当前引擎＝ChatGPT 订阅，但还没登录",
                    _ => null,
                };
                if (missing is not null)
                {
                    _trayService.ShowToast("翻译翻不出来",
                        $"{missing}。韩文能识别出来，但翻不成中文。请到 配置→翻译 填 Key / 登录，或切换引擎。");
                }
            }

            string msg = info.Status switch
            {
                ProbeStart.Ok => "已开启",
                ProbeStart.WaitingForWindow => $"未找到游戏窗口（进程 {info.ProcessName}），已转入等待：窗口出现后自动开始翻译（游戏需窗口化/无边框运行）",
                ProbeStart.CaptureUnsupported => "无法开启：系统不支持窗口捕获（需 Win10 1803+，且非 WSL）",
                ProbeStart.NoRoi => "尚未校准区域：按 F8 框选游戏聊天/房间区域后再按 F9",
                ProbeStart.WindowNotFound => $"{info.ProcessName} 窗口查找异常：请确认游戏已以窗口化/无边框模式启动",
                ProbeStart.CaptureFailed => "启动屏幕捕获失败，详见 logs/app.log",
                _ => "无法开启",
            };
            _trayService.ShowToast("翻译", msg);
        }
        void ToggleOverlay()
        {
            overlay.EnabledByUser = !overlay.EnabledByUser; // 意图开关；实际显隐由探测按"游戏前台"叠加计算
        }
        _trayService.ToggleProbe = ToggleProbe;
        _trayService.OnTranslationStateChanged = probe.ResetChangeDetection; // 切引擎/登录成功后立即重译静止画面

        // Phase 5 异常恢复：看门狗(线程池)事件 → marshal UI 线程 → 托盘气泡。
        probe.Notice += (kind, msg) => Dispatcher.InvokeAsync(() =>
        {
            string title = kind switch
            {
                ProbeNotice.WindowLost => "翻译已暂停",
                ProbeNotice.Reconnected => "翻译已恢复",
                ProbeNotice.FirstConnected => "翻译已开启",
                ProbeNotice.Minimized => "翻译已暂停",
                ProbeNotice.Restored => "翻译已恢复",
                ProbeNotice.ResolutionChanged => "需要重新校准",
                ProbeNotice.NoFrames => "收不到画面",
                ProbeNotice.Stalled => "翻译卡住",
                ProbeNotice.SceneChanged => "场景",
                ProbeNotice.SceneRefMissing => "场景识别不可用",
                ProbeNotice.TranslateFailed => "翻译失败",
                ProbeNotice.NeedsCalibration => "需要校准",
                _ => "翻译",
            };
            _trayService.ShowToast(title, msg);
        });

        var log = _host.Services.GetRequiredService<FileLogger>();
        var startupSettings = _host.Services.GetRequiredService<SettingsHolder>();
        log.DebugEnabled = startupSettings.Current.General.DebugMode; // 启动即同步，避免首帧诊断漏记
        log.Info($"──── SC:R Translator v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} 启动 ────");
        log.Info($"[app] 配置={settingsPath} | ROI={roisPath} | 调试日志={(log.DebugEnabled ? "开(全量诊断)" : "关(仅业务)")}");

        // 场景识别参考的启动体检：缺文件/缺分辨率是"翻译突然不工作"的头号原因，必须在日志第一屏说清楚。
        var sceneStore = _host.Services.GetRequiredService<SceneRefStore>();
        if (sceneStore.IsUsable)
        {
            var resList = string.Join(",", sceneStore.References
                .SelectMany(r => r.Resolutions.Keys).Distinct().OrderBy(k => k));
            log.Info($"[app] 场景参考={sceneStore.References.Count}条[{string.Join(" ", sceneStore.References.Select(r => r.Id))}] " +
                     $"| 已收录分辨率 {resList} | 现场覆盖 {sceneStore.OverrideCount} 条 | 块{sceneStore.BlockSize} 容差{sceneStore.ChannelTolerance} 阈值{sceneStore.MinHitRatio:P0}");
            foreach (var w in sceneStore.Warnings) log.Warn($"[app] 场景参考告警: {w}");
        }
        else
        {
            log.Error($"[app] 场景参考不可用：{sceneStore.LoadError} —— 场景识别将停在“其他界面”，翻译不会上屏。" +
                      "请确认 exe 旁存在 scene-refs.json（或重跑 tools/SceneRefGen 生成）");
        }

        // 校准窗口：单例（已开则激活），F8/托盘"进入校准…"打开。窗口关闭后置空以便下次可重新 new（WPF Window 一次性）。
        Calibration.CalibrationWindow? calibrationWindow = null;
        void OpenCalibration()
        {
            if (calibrationWindow is not null) { calibrationWindow.Activate(); return; }
            calibrationWindow = new Calibration.CalibrationWindow(roisPath, log, probe.ReloadRois, probe.CurrentSceneId);
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
                roisPath,
                secretStoreForSettings,
                oauthForSettings,
                log,
                OpenCalibration,
                probe.ResetChangeDetection,
                _hotkeys!, // 此时尚未 new，但 OpenSettings 只会在其后被调用（字段，非局部变量捕获）
                () => probe.CurrentSceneId);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }
        _trayService.OpenSettings = OpenSettings;
        _openSettingsAction = OpenSettings;

        // 玩家 ID 翻译开关（F7）：翻转 Translation.TranslatePlayerId + 落盘 + 复位变化检测 + 短提示。
        void TogglePlayerId()
        {
            var cur = settingsHolder.Current;
            bool newVal = !cur.Translation.TranslatePlayerId;
            settingsHolder.Current = cur with { Translation = cur.Translation with { TranslatePlayerId = newVal } };
            try { SettingsStore.Save(settingsHolder.Current, settingsPath); }
            catch (Exception ex) { log.Log($"[hotkey] 保存 settings.json 失败: {ex.Message}"); }
            probe.ResetChangeDetection();
            _trayService.ShowToast("玩家ID翻译", newVal ? "已开启" : "已关闭");
        }

        // 暂停/继续（F11）：不与 F9 的开关探测互相干扰，交给 Phase1Probe 内部的处理跳过标记。
        void TogglePause()
        {
            bool nowPaused = probe.TogglePause();
            _trayService.ShowToast("翻译", nowPaused ? "已暂停" : "已继续");
        }

        // DEBUG 面板开关（F6）：翻转 General.DebugMode + 落盘；探测的 500ms 定时器随即显隐面板。
        void ToggleDebug()
        {
            var cur = settingsHolder.Current;
            bool newVal = !cur.General.DebugMode;
            settingsHolder.Current = cur with { General = cur.General with { DebugMode = newVal } };
            log.DebugEnabled = newVal; // 即时生效，诊断日志与面板同步切换（探测定时器亦会兜底同步）
            try { SettingsStore.Save(settingsHolder.Current, settingsPath); }
            catch (Exception ex) { log.Error($"[hotkey] 保存 settings.json 失败: {ex.Message}"); }
            log.Info($"[app] DEBUG 模式: {(newVal ? "开（全量诊断日志+面板）" : "关（仅业务日志）")}");
            _trayService.ShowToast("DEBUG 面板", newVal ? "已开启" : "已关闭");
        }

        // 复制诊断快照（Ctrl+F6）：把面板同源文本拷到剪贴板，无需面板可点/不抢焦点。
        void CopyDebug()
        {
            try { Clipboard.SetText(probe.GetDebugSnapshot()); _trayService.ShowToast("诊断信息", "已复制到剪贴板"); }
            catch (Exception ex) { log.Warn($"[hotkey] 复制诊断失败: {ex.Message}"); _trayService.ShowToast("诊断信息", "复制失败"); }
        }

        // 全局热键：从 SettingsHolder.Current.Hotkeys 驱动注册全部七功能（游戏有焦点时也生效）。
        _hotkeys = new HotkeyService(overlay);
        var hotkeyActions = new Dictionary<string, Action>
        {
            ["ToggleTranslate"] = ToggleProbe,
            ["ToggleOverlay"] = ToggleOverlay,
            ["Calibrate"] = OpenCalibration,
            ["OpenConfig"] = OpenSettings,
            ["TogglePlayerId"] = TogglePlayerId,
            ["TogglePause"] = TogglePause,
            ["ToggleDebug"] = ToggleDebug,
            ["CopyDebug"] = CopyDebug,
        };
        var hotkeyResults = _hotkeys.RegisterAll(settingsHolder.Current.Hotkeys, hotkeyActions);
        foreach (var (func, ok) in hotkeyResults)
            log.Log($"[hotkey] 注册 {func}={ok}");

        // 启动自动开翻译（General.AutoTranslateOnLaunch）：游戏没开也不报错——看门狗会等待窗口出现自动续译。
        if (settingsHolder.Current.General.AutoTranslateOnLaunch)
        {
            log.Log("[startup] AutoTranslateOnLaunch=on，自动开启翻译");
            ToggleProbe();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _trayService?.Dispose();
        _host?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
