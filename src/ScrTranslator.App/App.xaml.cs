using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Logging;
using ScrTranslator.App.Services;
using ScrTranslator.Core.Ocr;

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
        builder.Services.AddSingleton<TrayService>();

        string baseDir = AppContext.BaseDirectory;
        builder.Services.AddSingleton<FileLogger>(_ =>
            new FileLogger(Path.Combine(baseDir, "logs", "phase1-ocr.log")));
        builder.Services.AddSingleton<IOcrEngine>(_ => new PaddleOcrEngine(Path.Combine(baseDir, "models")));
        builder.Services.AddSingleton<CaptureService>();
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            Path.Combine(baseDir, "rois.json")));

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
