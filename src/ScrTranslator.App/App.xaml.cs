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

    /// <summary>公开给外部（M4：Ctrl+F9 全局热键接线）打开配置窗口。</summary>
    public void OpenSettingsWindow() => _openSettingsAction?.Invoke();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：写 crash.log，且 UI 线程异常不再静默杀进程（诊断用）。
        string crashLog = Path.Combine(AppContext.BaseDirectory, "logs", "crash.log");
        Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
        void LogCrash(string src, object? ex) =>
            File.AppendAllText(crashLog, $"{DateTime.Now:HH:mm:ss.fff} [{src}] {ex}\n");

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
            Path.Combine(baseDir, "rois.json"),
            sp.GetRequiredService<TieredTranslator>(),
            sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<Glossary>()));

        _host = builder.Build();

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        _trayService = _host.Services.GetRequiredService<TrayService>();
        _trayService.Initialize();

        var probe = _host.Services.GetRequiredService<Phase1Probe>();
        bool running = false;
        void ToggleProbe()
        {
            if (running) { probe.Stop(); running = false; }
            else { running = probe.Start(); }
        }
        void ToggleOverlay()
        {
            if (overlay.IsVisible) overlay.Hide(); else overlay.Show();
        }
        _trayService.ToggleProbe = ToggleProbe;
        _trayService.OnTranslationStateChanged = probe.ResetChangeDetection; // 切引擎/登录成功后立即重译静止画面

        var log = _host.Services.GetRequiredService<FileLogger>();

        // 校准窗口：单例（已开则激活），F8/托盘"进入校准…"打开。窗口关闭后置空以便下次可重新 new（WPF Window 一次性）。
        Calibration.CalibrationWindow? calibrationWindow = null;
        void OpenCalibration()
        {
            if (calibrationWindow is not null) { calibrationWindow.Activate(); return; }
            calibrationWindow = new Calibration.CalibrationWindow(Path.Combine(baseDir, "rois.json"), log);
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
                Path.Combine(baseDir, "rois.json"),
                secretStoreForSettings,
                oauthForSettings,
                log,
                OpenCalibration,
                probe.ResetChangeDetection,
                _hotkeys!); // 此时尚未 new，但 OpenSettings 只会在其后被调用（字段，非局部变量捕获）
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

        // 全局热键：从 SettingsHolder.Current.Hotkeys 驱动注册全部六功能（游戏有焦点时也生效）。
        _hotkeys = new HotkeyService(overlay);
        var hotkeyActions = new Dictionary<string, Action>
        {
            ["ToggleTranslate"] = ToggleProbe,
            ["ToggleOverlay"] = ToggleOverlay,
            ["Calibrate"] = OpenCalibration,
            ["OpenConfig"] = OpenSettings,
            ["TogglePlayerId"] = TogglePlayerId,
            ["TogglePause"] = TogglePause,
        };
        var hotkeyResults = _hotkeys.RegisterAll(settingsHolder.Current.Hotkeys, hotkeyActions);
        foreach (var (func, ok) in hotkeyResults)
            log.Log($"[hotkey] 注册 {func}={ok}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _trayService?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
