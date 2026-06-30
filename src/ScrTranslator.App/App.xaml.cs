using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScrTranslator.App.Services;

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
        _host = builder.Build();

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        _trayService = _host.Services.GetRequiredService<TrayService>();
        _trayService.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
