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
            new FileLogger(Path.Combine(baseDir, "logs", "phase1-ocr.log")));
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
            var dashClient = sp.GetRequiredService<OpenAiCompatibleChatClient>();
            var chatGpt = sp.GetRequiredService<ChatGptSubscriptionProvider>();
            var dashQuality = sp.GetRequiredService<DashScopeProvider>();               // 绑 Current.Translation.DashScopeModel
            var dashFast = new DashScopeProvider(dashClient, () => settings.Current.Translation.FastModel);

            ITranslationProvider Quality() =>
                settings.Current.Translation.Active == TranslationEngine.DashScope ? dashQuality : chatGpt;
            ITranslationProvider Fast() =>
                settings.Current.Translation.FastEngine == TranslationEngine.DashScope ? dashFast : chatGpt;

            return new TieredTranslator(
                sp.GetRequiredService<Glossary>(),
                sp.GetRequiredService<TranslationCache>(),
                Fast, Quality,
                () => settings.Current.Translation);
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
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            roisPath,
            sp.GetRequiredService<TieredTranslator>(),
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
            string msg = info.Status switch
            {
                ProbeStart.Ok => "已开启",
                ProbeStart.WaitingForWindow => $"未找到游戏窗口（进程 {info.ProcessName}），已转入等待：窗口出现后自动开始翻译（游戏需窗口化/无边框运行）",
                ProbeStart.CaptureUnsupported => "无法开启：系统不支持窗口捕获（需 Win10 1803+，且非 WSL）",
                ProbeStart.NoRoi => "尚未校准区域：按 F8 框选游戏聊天/房间区域后再按 F9",
                ProbeStart.WindowNotFound => $"{info.ProcessName} 窗口查找异常：请确认游戏已以窗口化/无边框模式启动",
                ProbeStart.CaptureFailed => "启动屏幕捕获失败，详见 logs/phase1-ocr.log",
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
                _ => "翻译",
            };
            _trayService.ShowToast(title, msg);
        });

        var log = _host.Services.GetRequiredService<FileLogger>();

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
            try { SettingsStore.Save(settingsHolder.Current, settingsPath); }
            catch (Exception ex) { log.Log($"[hotkey] 保存 settings.json 失败: {ex.Message}"); }
            _trayService.ShowToast("DEBUG 面板", newVal ? "已开启" : "已关闭");
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
