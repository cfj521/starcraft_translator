using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;

namespace ScrTranslator.App.Services;

/// <summary>系统托盘图标：显隐悬浮窗、退出。</summary>
public sealed class TrayService : IDisposable
{
    private readonly OverlayWindow _overlay;
    private TaskbarIcon? _trayIcon;

    public TrayService(OverlayWindow overlay) => _overlay = overlay;

    public Action? ToggleProbe { get; set; }

    public void Initialize()
    {
        var toggleItem = new MenuItem { Header = "显示/隐藏悬浮窗" };
        toggleItem.Click += (_, _) => ToggleOverlay();

        var probeItem = new MenuItem { Header = "开始/停止 Phase1 探测" };
        probeItem.Click += (_, _) => ToggleProbe?.Invoke();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(toggleItem);
        menu.Items.Add(probeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "星际翻译",
            IconSource = new GeneratedIconSource
            {
                Text = "译",
                Foreground = Brushes.White,
            },
            ContextMenu = menu,
        };
        _trayIcon.ForceCreate();
    }

    private void ToggleOverlay()
    {
        if (_overlay.IsVisible)
            _overlay.Hide();
        else
            _overlay.Show();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
