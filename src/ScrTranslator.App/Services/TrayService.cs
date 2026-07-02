using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using ScrTranslator.App.Auth;
using ScrTranslator.App.Logging;
using ScrTranslator.App.Secrets;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;

namespace ScrTranslator.App.Services;

/// <summary>系统托盘图标：显隐悬浮窗、开关探测、登录 ChatGPT/填 DashScope Key/选翻译引擎、退出。</summary>
public sealed class TrayService : IDisposable
{
    private readonly OverlayWindow _overlay;
    private readonly OpenAiOAuth _oauth;
    private readonly SettingsHolder _settings;
    private readonly SecretStore _store;
    private readonly FileLogger _log;
    private readonly string _settingsPath;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _loginItem;
    private MenuItem? _engineDashScopeItem;
    private MenuItem? _engineChatGptItem;
    private MenuItem? _twoLevelItem;

    public TrayService(OverlayWindow overlay, OpenAiOAuth oauth, SettingsHolder settings, SecretStore store, FileLogger log, string settingsPath)
    {
        _overlay = overlay; _oauth = oauth; _settings = settings; _store = store; _log = log; _settingsPath = settingsPath;
    }

    public Action? ToggleProbe { get; set; }
    /// <summary>切引擎/登录成功后触发（用于复位管线变化检测，让静止画面立即重译）。</summary>
    public Action? OnTranslationStateChanged { get; set; }
    /// <summary>托盘"进入校准…"菜单项触发（打开 CalibrationWindow，由 App.xaml.cs 提供实现）。</summary>
    public Action? OpenCalibration { get; set; }
    /// <summary>托盘"配置…"菜单项 + 双击托盘图标触发（打开 SettingsWindow，由 App.xaml.cs 提供实现）。</summary>
    public Action? OpenSettings { get; set; }

    public void Initialize()
    {
        var toggleItem = new MenuItem { Header = "显示/隐藏悬浮窗" };
        toggleItem.Click += (_, _) => ToggleOverlay();

        var probeItem = new MenuItem { Header = "开始/停止 Phase1 探测" };
        probeItem.Click += (_, _) => ToggleProbe?.Invoke();

        var calibrateItem = new MenuItem { Header = "进入校准…" };
        calibrateItem.Click += (_, _) => OpenCalibration?.Invoke();

        var settingsItem = new MenuItem { Header = "配置…" };
        settingsItem.Click += (_, _) => OpenSettings?.Invoke();

        _loginItem = new MenuItem { Header = "登录 ChatGPT（订阅）" };
        _loginItem.Click += async (_, _) => await OnLoginChatGptAsync();
        RefreshLoginText();

        var keyItem = new MenuItem { Header = "设置阿里云 Key" };
        keyItem.Click += (_, _) => OnSetDashScopeKey();

        _engineDashScopeItem = new MenuItem { Header = "DashScope（阿里云）", IsCheckable = true };
        _engineDashScopeItem.Click += (_, _) => SetEngine(TranslationEngine.DashScope);
        _engineChatGptItem = new MenuItem { Header = "ChatGPT（订阅）", IsCheckable = true };
        _engineChatGptItem.Click += (_, _) => SetEngine(TranslationEngine.ChatGpt);
        RefreshEngineChecks();

        var engineMenu = new MenuItem { Header = "翻译引擎" };
        engineMenu.Items.Add(_engineDashScopeItem);
        engineMenu.Items.Add(_engineChatGptItem);

        _twoLevelItem = new MenuItem { Header = "两级翻译（快译临时→精译定稿）", IsCheckable = true };
        _twoLevelItem.Click += (_, _) => ToggleTwoLevel();
        RefreshTwoLevelCheck();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(toggleItem);
        menu.Items.Add(probeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(calibrateItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_loginItem);
        menu.Items.Add(keyItem);
        menu.Items.Add(engineMenu);
        menu.Items.Add(_twoLevelItem);
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
        _trayIcon.TrayLeftMouseDoubleClick += (_, _) => OpenSettings?.Invoke(); // 双击托盘图标 = 打开配置窗口
        _trayIcon.ForceCreate();
    }

    private async System.Threading.Tasks.Task OnLoginChatGptAsync()
    {
        if (_loginItem is null) return;
        _loginItem.IsEnabled = false;
        _loginItem.Header = "登录中…（浏览器完成授权）";
        try
        {
            // 带超时，避免用户中途放弃登录时无限等待浏览器回调。
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            bool ok = await _oauth.LoginAsync(cts.Token);
            _log.Log($"[tray] ChatGPT 登录 {(ok ? "成功" : "失败/未完成")}");
            if (ok) OnTranslationStateChanged?.Invoke(); // 登录成功后让静止画面立即用登录态重译
            if (!ok)
                MessageBox.Show("ChatGPT 登录未完成或失败（可能：端口 1455 被占用、超时、网络问题或未在浏览器完成授权）。请重试；详情见 logs/phase1-ocr.log。",
                    "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Log($"[tray] ChatGPT 登录异常: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"ChatGPT 登录出错：{ex.Message}", "登录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshLoginText();
            _loginItem.IsEnabled = true;
        }
    }

    private void RefreshLoginText()
    {
        if (_loginItem is null) return;
        var (loggedIn, email, plan) = _oauth.Status();
        _loginItem.Header = loggedIn ? $"已登录: {email}({plan})" : "登录 ChatGPT（订阅）";
    }

    private void OnSetDashScopeKey()
    {
        var win = new DashScopeKeyWindow(_store) { Owner = null };
        win.ShowDialog();
    }

    private void SetEngine(TranslationEngine engine)
    {
        _settings.Current = _settings.Current with { Translation = _settings.Current.Translation with { Active = engine } };
        RefreshEngineChecks();
        SaveSettings();
        OnTranslationStateChanged?.Invoke(); // 切引擎后让静止画面立即用新引擎重译
    }

    private void RefreshEngineChecks()
    {
        if (_engineDashScopeItem is null || _engineChatGptItem is null) return;
        var active = _settings.Current.Translation.Active;
        _engineDashScopeItem.IsChecked = active == TranslationEngine.DashScope;
        _engineChatGptItem.IsChecked = active == TranslationEngine.ChatGpt;
    }

    private void ToggleTwoLevel()
    {
        _settings.Current = _settings.Current with
        {
            Translation = _settings.Current.Translation with { TwoLevel = !_settings.Current.Translation.TwoLevel }
        };
        RefreshTwoLevelCheck();
        SaveSettings();
        OnTranslationStateChanged?.Invoke(); // 切换后让静止画面按新模式立即重译
    }

    private void RefreshTwoLevelCheck()
    {
        if (_twoLevelItem is not null) _twoLevelItem.IsChecked = _settings.Current.Translation.TwoLevel;
    }

    /// <summary>托盘改设置后立即落盘（防 Save-without-Load / 下次启动丢失）。</summary>
    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings.Current, _settingsPath); }
        catch (Exception ex) { _log.Log($"[tray] 保存 settings.json 失败: {ex.Message}"); }
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
