using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using ScrTranslator.App.Logging;

namespace ScrTranslator.App.Services;

/// <summary>系统托盘图标：开/关翻译（文本随状态变化）、区域校准设置、设置、退出。
/// （翻译引擎、ChatGPT 登录、各 provider 密钥都在 配置→翻译 页的 provider 池里管，托盘不再单列）。</summary>
public sealed class TrayService : IDisposable
{
    private readonly FileLogger _log;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _probeItem;

    public TrayService(FileLogger log)
    {
        _log = log;
    }

    public Action? ToggleProbe { get; set; }
    /// <summary>托盘"区域校准设置"菜单项触发（打开 CalibrationWindow，由 App.xaml.cs 提供实现）。</summary>
    public Action? OpenCalibration { get; set; }
    /// <summary>托盘"设置"菜单项 + 双击托盘图标触发（打开 SettingsWindow，由 App.xaml.cs 提供实现）。</summary>
    public Action? OpenSettings { get; set; }

    public void Initialize()
    {
        _probeItem = new MenuItem { Header = "开启翻译 F9" };
        _probeItem.Click += (_, _) => ToggleProbe?.Invoke();

        var calibrateItem = new MenuItem { Header = "区域校准设置 Ctrl+F9" };
        calibrateItem.Click += (_, _) => OpenCalibration?.Invoke();

        var settingsItem = new MenuItem { Header = "设置 F7" };
        settingsItem.Click += (_, _) => OpenSettings?.Invoke();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_probeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(calibrateItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "星际翻译",
            IconSource = LoadTrayIcon(),
            ContextMenu = menu,
        };
        _trayIcon.TrayLeftMouseDoubleClick += (_, _) => OpenSettings?.Invoke(); // 双击托盘图标 = 打开设置窗口
        _trayIcon.ForceCreate();
    }

    /// <summary>按翻译运行状态刷新菜单项文本：开→"关闭翻译 F9"，关→"开启翻译 F9"。</summary>
    public void SetProbeRunning(bool running)
    {
        if (_probeItem is not null) _probeItem.Header = running ? "关闭翻译 F9" : "开启翻译 F9";
    }

    /// <summary>托盘图标：优先用随包 app.ico（与 exe 图标一致，한↓中）；缺失/解码失败回落生成式"译"字并补深色背景（原白字透明底=不可见 bug）。</summary>
    private ImageSource LoadTrayIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(ico))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 不锁文件
                bmp.UriSource = new Uri(ico);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            _log.Log("[tray] app.ico 缺失，回落生成图标");
        }
        catch (Exception ex) { _log.Log($"[tray] 加载 app.ico 失败，回落生成图标: {ex.Message}"); }
        return new GeneratedIconSource
        {
            Text = "译",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x40, 0x60)),
        };
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
