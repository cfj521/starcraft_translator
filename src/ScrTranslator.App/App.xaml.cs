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
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.App;

public partial class App : Application
{
    private IHost? _host;
    private TrayService? _trayService;
    private HotkeyService? _hotkeys;

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
        builder.Services.AddSingleton<SecretStore>();
        builder.Services.AddSingleton(new HttpClient());
        builder.Services.AddSingleton<OpenAiOAuth>();
        builder.Services.AddSingleton<ITokenSource>(sp => sp.GetRequiredService<OpenAiOAuth>());
        builder.Services.AddSingleton<SettingsHolder>(); // 可变，托盘切引擎改 .Current
        builder.Services.AddSingleton(_ => Glossary.LoadFromMarkdown(Path.Combine(baseDir, "GLOSSARY.md")));
        builder.Services.AddSingleton<TranslationCache>();
        builder.Services.AddSingleton(sp => new OpenAiCompatibleChatClient(
            sp.GetRequiredService<HttpClient>(),
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            () => sp.GetRequiredService<SecretStore>().Load().DashScopeApiKey));
        builder.Services.AddSingleton(sp => new CodexResponsesClient(sp.GetRequiredService<HttpClient>()));
        builder.Services.AddSingleton(sp => new DashScopeProvider(
            sp.GetRequiredService<OpenAiCompatibleChatClient>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.DashScopeModel));
        builder.Services.AddSingleton(sp => new ChatGptSubscriptionProvider(
            sp.GetRequiredService<CodexResponsesClient>(),
            sp.GetRequiredService<ITokenSource>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.ChatGptModel));
        builder.Services.AddSingleton(sp => new TranslationService(
            sp.GetRequiredService<Glossary>(),
            sp.GetRequiredService<TranslationCache>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Active == TranslationEngine.DashScope
                ? (ITranslationProvider)sp.GetRequiredService<DashScopeProvider>()
                : sp.GetRequiredService<ChatGptSubscriptionProvider>()));

        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            Path.Combine(baseDir, "rois.json"),
            sp.GetRequiredService<TranslationService>(),
            sp.GetRequiredService<SettingsHolder>()));

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

        // 全局热键：F9 开关探测、F10 显隐悬浮窗（游戏有焦点时也生效）
        var log = _host.Services.GetRequiredService<FileLogger>();
        _hotkeys = new HotkeyService(overlay);
        bool f9 = _hotkeys.Register(HotkeyService.MOD_NONE, HotkeyService.VK_F9, ToggleProbe);
        bool f10 = _hotkeys.Register(HotkeyService.MOD_NONE, HotkeyService.VK_F10, ToggleOverlay);
        log.Log($"[hotkey] 注册 F9(开关探测)={f9} F10(显隐悬浮)={f10}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _trayService?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
