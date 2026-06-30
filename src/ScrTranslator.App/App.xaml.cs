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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<OverlayWindow>();
        builder.Services.AddSingleton<TrayService>();

        string baseDir = AppContext.BaseDirectory;
        builder.Services.AddSingleton(new FileLogger(Path.Combine(baseDir, "logs", "phase1-ocr.log")));
        builder.Services.AddSingleton<IOcrEngine>(_ => new PaddleOcrEngine(Path.Combine(baseDir, "models")));
        builder.Services.AddSingleton<CaptureService>();
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            Path.Combine(baseDir, "rois.json")));

        _host = builder.Build();

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        _trayService = _host.Services.GetRequiredService<TrayService>();
        _trayService.Initialize();

        var probe = _host.Services.GetRequiredService<Phase1Probe>();
        bool running = false;
        _trayService.ToggleProbe = () =>
        {
            if (running) { probe.Stop(); } else { probe.Start(); }
            running = !running;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
