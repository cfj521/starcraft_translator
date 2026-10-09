using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScrTranslator.App.Auth;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.App.Secrets;
using ScrTranslator.App.Services;
using ScrTranslator.Core;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;

namespace ScrTranslator.App.Config;

/// <summary>配置窗口：左侧竖直标签 6 页 + 关于。DataContext 语义上是 <see cref="SettingsHolder.Current"/> 的可编辑副本——
/// 控件值直到「应用/确定」才写回 <see cref="SettingsHolder.Current"/> + <see cref="SettingsStore.Save"/>；
/// 「取消」直接关闭窗口，不触碰 Holder，天然等价于丢弃副本。单例由 App.xaml.cs 管理（已开则 Activate）。</summary>
public partial class SettingsWindow : Window
{
    private const string Scene = "custom_games_listing";

    private readonly SettingsHolder _holder;
    private readonly string _settingsPath;
    private readonly string _roisJsonPath;
    private readonly SecretStore _secretStore;
    private readonly OpenAiOAuth _oauth;
    private readonly FileLogger _log;
    private readonly Action _openCalibration;
    private readonly Action? _onStateChanged;
    private readonly HotkeyService _hotkeyService;

    private sealed record RoiRow(string Name, string Type, string Position);
    private sealed record ColumnRow(string RoiName, string Range, string Kind);
    private sealed record HotkeyRow(string Key, string Function, string Current);

    // 录制中的功能名；非 null 表示正在等待下一次按键组合。
    private string? _recordingFunc;

    private static readonly (string Key, string Label)[] HotkeyOrder =
    {
        ("ToggleTranslate", "开/关 翻译"),
        ("ToggleOverlay", "显/隐 悬浮层"),
        ("Calibrate", "进入校准"),
        ("OpenConfig", "打开配置"),
        ("TogglePlayerId", "切换“翻译玩家 ID”"),
        ("TogglePause", "暂停/继续"),
    };

    /// <param name="openCalibration">复用 App.xaml.cs 里管理的单例 CalibrationWindow 打开方法（不在本窗口另建实例）。</param>
    /// <param name="onStateChanged">设置生效后的回调（复用 TrayService 同款 OnTranslationStateChanged：复位变化检测，让静止画面立即按新设置重译）。</param>
    public SettingsWindow(
        SettingsHolder holder,
        string settingsPath,
        string roisJsonPath,
        SecretStore secretStore,
        OpenAiOAuth oauth,
        FileLogger log,
        Action openCalibration,
        Action? onStateChanged,
        HotkeyService hotkeyService)
    {
        InitializeComponent();
        _holder = holder;
        _settingsPath = settingsPath;
        _roisJsonPath = roisJsonPath;
        _secretStore = secretStore;
        _oauth = oauth;
        _log = log;
        _openCalibration = openCalibration;
        _onStateChanged = onStateChanged;
        _hotkeyService = hotkeyService;

        VersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version}";

        LoadFromSettings(_holder.Current);
        Activated += (_, _) =>
        {
            LoadRoiList(); // 从校准窗口返回时刷新区域页
            ReloadOutOfBandTranslationControls(); // F7/托盘可能在本窗口打开期间改了这三项，重新前台时同步，避免"应用"用陈旧值覆盖
        };
        Closed += OnWindowClosed;
    }

    // 若用户在"录制中"（已点修改、尚未按键/Esc）关闭本窗口，PreviewKeyDown 订阅本会一直挂着；
    // 这里兜底注销，避免 handler 残留、_recordingFunc 悬空。
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_recordingFunc is null) return;
        PreviewKeyDown -= OnRecordKeyDown;
        _recordingFunc = null;
    }

    // ── 加载 ─────────────────────────────────────────────────────────────

    private void LoadFromSettings(AppSettings s)
    {
        TargetProcessBox.Text = s.General.TargetProcess;
        AutoStartCheck.IsChecked = s.General.AutoStart;
        StartMinimizedCheck.IsChecked = s.General.StartMinimized;
        AutoTranslateCheck.IsChecked = s.General.AutoTranslateOnLaunch;
        RefreshStatusText();

        DashScopeEngineRadio.IsChecked = s.Translation.Active == TranslationEngine.DashScope;
        ChatGptEngineRadio.IsChecked = s.Translation.Active == TranslationEngine.ChatGpt;
        TwoLevelCheck.IsChecked = s.Translation.TwoLevel;
        DashScopeModelBox.Text = s.Translation.DashScopeModel;
        ChatGptModelBox.Text = s.Translation.ChatGptModel;
        FastModelBox.Text = s.Translation.FastModel;
        TranslateChatCheck.IsChecked = s.Translation.TranslateChat;
        TranslateRoomNameCheck.IsChecked = s.Translation.TranslateRoomName;
        TranslateMapInfoCheck.IsChecked = s.Translation.TranslateMapInfo;
        TranslatePlayerIdCheck.IsChecked = s.Translation.TranslatePlayerId;
        RefreshChatGptStatus();

        ChatPollBox.Text = s.Ocr.ChatPollMs.ToString();
        OtherPollBox.Text = s.Ocr.OtherPollMs.ToString();
        UpscaleCheck.IsChecked = s.Ocr.Upscale;
        GrayscaleCheck.IsChecked = s.Ocr.Grayscale;
        BinarizeCheck.IsChecked = s.Ocr.Binarize;
        SensitivitySlider.Value = s.Ocr.ChangeSensitivity;

        FontFamilyBox.Text = s.Appearance.FontFamily;
        FontSizeBox.Text = s.Appearance.FontSize.ToString();
        OpacitySlider.Value = s.Appearance.Opacity;
        OnlyTranslationRadio.IsChecked = !s.Appearance.ShowOriginalAndTranslation;
        BothRadio.IsChecked = s.Appearance.ShowOriginalAndTranslation;

        LoadRoiList();
        LoadHotkeyList(s.Hotkeys);
    }

    private void RefreshStatusText()
    {
        string proc = string.IsNullOrWhiteSpace(TargetProcessBox.Text) ? "StarCraft" : TargetProcessBox.Text.Trim();
        var hwnd = WindowFinder.FindByProcess(proc);
        if (hwnd == IntPtr.Zero) { StatusText.Text = "○ 未连接"; return; }
        var rect = NativeMethods.GetWindowScreenRect(hwnd);
        StatusText.Text = rect.IsEmpty || rect.Width <= 0
            ? "○ 已找到窗口但无法读取矩形"
            : $"● 已连接 ({rect.Width}×{rect.Height})";
    }

    private void RefreshChatGptStatus()
    {
        var (loggedIn, email, plan) = _oauth.Status();
        ChatGptStatusText.Text = loggedIn ? $"已登录: {email}({plan})" : "未登录";
    }

    private void LoadRoiList()
    {
        try
        {
            var cfg = RoiStore.Load(_roisJsonPath);
            string proc = string.IsNullOrWhiteSpace(TargetProcessBox.Text) ? "StarCraft" : TargetProcessBox.Text.Trim();
            var hwnd = WindowFinder.FindByProcess(proc);
            var rect = hwnd == IntPtr.Zero ? System.Drawing.Rectangle.Empty : NativeMethods.GetWindowScreenRect(hwnd);

            SceneBucket? bucket = null;
            string resLabel;
            if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            {
                resLabel = $"{rect.Width}x{rect.Height}";
                try { bucket = RoiStore.SelectBucket(cfg, Scene, rect.Width, rect.Height); }
                catch { bucket = null; }
            }
            else
            {
                resLabel = "游戏未运行";
                if (cfg.Scenes.TryGetValue(Scene, out var buckets) && buckets.Count > 0) bucket = buckets[0];
            }

            var roiRows = new List<RoiRow>();
            var colRows = new List<ColumnRow>();
            if (bucket is not null)
            {
                foreach (var roi in bucket.Rois)
                {
                    roiRows.Add(new RoiRow(roi.Name, RoiTypes.Parse(roi.Type).ToString(),
                        $"X{roi.Rect.X:P0} Y{roi.Rect.Y:P0} {roi.Rect.W:P0}×{roi.Rect.H:P0}"));
                    if (roi.Columns is { Count: > 0 })
                        foreach (var c in roi.Columns)
                            colRows.Add(new ColumnRow(roi.Name, $"{c.RelStart:P0}–{c.RelEnd:P0}", ColumnKindLabel(c.Kind)));
                }
                RegionHintText.Text = $"当前分辨率({resLabel})共 {roiRows.Count} 个区域";
            }
            else
            {
                RegionHintText.Text = $"未找到 {resLabel} 分辨率的校准数据，请点击“进入校准”新建";
            }
            RoiListView.ItemsSource = roiRows;
            ColumnsListView.ItemsSource = colRows;
        }
        catch (Exception ex)
        {
            RegionHintText.Text = $"读取 rois.json 失败: {ex.Message}";
        }
    }

    /// <summary>只重载 F7(TogglePlayerId)/托盘 SetEngine/ToggleTwoLevel 这三个可在本窗口打开期间"带外"改变的字段，
    /// 不重载其它控件——避免打断用户正在本窗口内编辑但尚未应用的其它字段。</summary>
    private void ReloadOutOfBandTranslationControls()
    {
        var t = _holder.Current.Translation;
        DashScopeEngineRadio.IsChecked = t.Active == TranslationEngine.DashScope;
        ChatGptEngineRadio.IsChecked = t.Active == TranslationEngine.ChatGpt;
        TwoLevelCheck.IsChecked = t.TwoLevel;
        TranslatePlayerIdCheck.IsChecked = t.TranslatePlayerId;
    }

    private static string ColumnKindLabel(ColumnKind k) => k switch
    {
        ColumnKind.RoomName => "房间名",
        ColumnKind.MapName => "地图",
        ColumnKind.PlayerId => "玩家ID",
        _ => "其它",
    };

    private void LoadHotkeyList(IReadOnlyDictionary<string, HotkeyBinding> hotkeys)
    {
        var rows = new List<HotkeyRow>();
        foreach (var (key, label) in HotkeyOrder)
        {
            string current = hotkeys.TryGetValue(key, out var b) ? FormatHotkey(b) : "未绑定";
            if (!_hotkeyService.IsRegistered(key)) current += "（未生效）"; // 例如启动时该组合键已被其它程序占用
            rows.Add(new HotkeyRow(key, label, current));
        }
        HotkeyListView.ItemsSource = rows;
    }

    private static string FuncLabel(string func) =>
        HotkeyOrder.FirstOrDefault(x => x.Key == func).Label ?? func;

    private static string FormatHotkey(HotkeyBinding b)
    {
        var parts = new List<string>();
        if ((b.Modifiers & HotkeyKeys.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((b.Modifiers & HotkeyKeys.MOD_ALT) != 0) parts.Add("Alt");
        if ((b.Modifiers & HotkeyKeys.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(b.Vk switch
        {
            HotkeyKeys.VK_F7 => "F7",
            HotkeyKeys.VK_F8 => "F8",
            HotkeyKeys.VK_F9 => "F9",
            HotkeyKeys.VK_F10 => "F10",
            HotkeyKeys.VK_F11 => "F11",
            _ => $"VK{b.Vk:X2}",
        });
        return string.Join("+", parts);
    }

    // ── 交互 ─────────────────────────────────────────────────────────────

    private void OnRedetectClick(object sender, RoutedEventArgs e) => RefreshStatusText();

    private void OnSetDashScopeKeyClick(object sender, RoutedEventArgs e)
    {
        var win = new DashScopeKeyWindow(_secretStore) { Owner = this };
        win.ShowDialog();
    }

    private async void OnLoginChatGptClick(object sender, RoutedEventArgs e)
    {
        ChatGptLoginButton.IsEnabled = false;
        ChatGptStatusText.Text = "登录中…（浏览器完成授权）";
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            bool ok = await _oauth.LoginAsync(cts.Token);
            _log.Log($"[settings] ChatGPT 登录 {(ok ? "成功" : "失败/未完成")}");
            if (ok) _onStateChanged?.Invoke(); // 登录成功后让静止画面立即用登录态重译
            if (!ok)
                MessageBox.Show(this, "ChatGPT 登录未完成或失败（可能：端口 1455 被占用、超时、网络问题或未在浏览器完成授权）。请重试；详情见 logs/phase1-ocr.log。",
                    "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Log($"[settings] ChatGPT 登录异常: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, $"ChatGPT 登录出错：{ex.Message}", "登录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshChatGptStatus();
            ChatGptLoginButton.IsEnabled = true;
        }
    }

    private void OnEditGlossaryClick(object sender, RoutedEventArgs e)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "GLOSSARY.md");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Log($"[settings] 打开术语表失败: {ex.Message}");
            MessageBox.Show(this, $"无法打开术语表文件：{path}\n{ex.Message}", "术语表", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnEnterCalibrationClick(object sender, RoutedEventArgs e) => _openCalibration();

    // ── 热键录制/重绑 ────────────────────────────────────────────────────

    private void OnRebindHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string func) return;
        if (_recordingFunc is not null) return; // 已在录制另一项，忽略
        _recordingFunc = func;
        btn.Content = "按下组合键…(Esc取消)";
        PreviewKeyDown += OnRecordKeyDown;
        Focus();
    }

    private void OnRecordKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingFunc is null) return;
        e.Handled = true;
        string func = _recordingFunc;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        // 纯修饰键按下时先不处理，等真正的功能键。
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (!TryMapKey(key, out uint vk))
        {
            MessageBox.Show(this, $"不支持的按键：{key}（当前支持 F7–F11，可加 Ctrl/Alt/Shift 组合）",
                "热键", MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return;
        }

        uint mods = HotkeyKeys.MOD_NONE;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyKeys.MOD_CONTROL;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyKeys.MOD_ALT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyKeys.MOD_SHIFT;
        var binding = new HotkeyBinding(mods, vk);

        var prospective = new Dictionary<string, HotkeyBinding>(_holder.Current.Hotkeys) { [func] = binding };
        var conflictGroup = HotkeyConflict.Find(prospective).FirstOrDefault(g => g.Contains(func));
        if (conflictGroup is not null)
        {
            string others = string.Join("、", conflictGroup.Where(f => f != func).Select(FuncLabel));
            MessageBox.Show(this, $"该组合键已被“{others}”占用，请换一个组合。", "热键冲突",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return;
        }

        if (!_hotkeyService.Rebind(func, binding))
        {
            MessageBox.Show(this, "注册失败：该组合键可能已被其它程序占用，请换一个组合。", "热键",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return;
        }

        _holder.Current = _holder.Current with { Hotkeys = prospective };
        SaveHotkeys();
        StopRecording();
    }

    private void StopRecording()
    {
        PreviewKeyDown -= OnRecordKeyDown;
        _recordingFunc = null;
        LoadHotkeyList(_holder.Current.Hotkeys); // 重画整表，恢复按钮文案
    }

    private static bool TryMapKey(Key key, out uint vk)
    {
        switch (key)
        {
            case Key.F7: vk = HotkeyKeys.VK_F7; return true;
            case Key.F8: vk = HotkeyKeys.VK_F8; return true;
            case Key.F9: vk = HotkeyKeys.VK_F9; return true;
            case Key.F10: vk = HotkeyKeys.VK_F10; return true;
            case Key.F11: vk = HotkeyKeys.VK_F11; return true;
            default: vk = 0; return false;
        }
    }

    private void OnResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        _hotkeyService.ResetToDefaults(HotkeyDefaults.Map);
        _holder.Current = _holder.Current with { Hotkeys = HotkeyDefaults.Map };
        SaveHotkeys();
        LoadHotkeyList(_holder.Current.Hotkeys);
    }

    private void SaveHotkeys()
    {
        try { SettingsStore.Save(_holder.Current, _settingsPath); }
        catch (Exception ex)
        {
            _log.Log($"[settings] 保存热键失败: {ex.Message}");
            MessageBox.Show(this, $"保存热键失败：{ex.Message}", "配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensitivityValueText is not null)
            SensitivityValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText is not null)
            OpacityValueText.Text = e.NewValue.ToString("P0");
    }

    // ── 应用/确定/取消 ───────────────────────────────────────────────────

    private void OnApplyClick(object sender, RoutedEventArgs e) => ApplyChanges();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        ApplyChanges();
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close(); // 未写回 Holder，等价于丢弃编辑副本

    private void ApplyChanges()
    {
        var settings = BuildSettingsFromControls();
        _holder.Current = settings;
        try
        {
            SettingsStore.Save(settings, _settingsPath);
        }
        catch (Exception ex)
        {
            _log.Log($"[settings] 保存 settings.json 失败: {ex.Message}");
            MessageBox.Show(this, $"保存设置失败：{ex.Message}", "配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 开机自启接线 HKCU Run 键（5-4）：失败不影响其他设置，单独提示。
        if (!Services.AutoStartService.Apply(settings.General.AutoStart, Environment.ProcessPath, out var autoStartErr))
        {
            _log.Log($"[settings] 开机自启应用失败: {autoStartErr}");
            MessageBox.Show(this, $"开机自启设置应用失败：{autoStartErr}", "配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _onStateChanged?.Invoke(); // 复位变化检测，让静止画面立即按新设置重译
    }

    private AppSettings BuildSettingsFromControls()
    {
        var original = _holder.Current;

        var general = original.General with
        {
            TargetProcess = OrDefault(TargetProcessBox.Text, original.General.TargetProcess),
            AutoStart = AutoStartCheck.IsChecked == true,
            StartMinimized = StartMinimizedCheck.IsChecked == true,
            AutoTranslateOnLaunch = AutoTranslateCheck.IsChecked == true,
        };

        var translation = original.Translation with
        {
            Active = ChatGptEngineRadio.IsChecked == true ? TranslationEngine.ChatGpt : TranslationEngine.DashScope,
            TwoLevel = TwoLevelCheck.IsChecked == true,
            DashScopeModel = OrDefault(DashScopeModelBox.Text, original.Translation.DashScopeModel),
            ChatGptModel = OrDefault(ChatGptModelBox.Text, original.Translation.ChatGptModel),
            FastModel = OrDefault(FastModelBox.Text, original.Translation.FastModel),
            TranslateChat = TranslateChatCheck.IsChecked == true,
            TranslateRoomName = TranslateRoomNameCheck.IsChecked == true,
            TranslateMapInfo = TranslateMapInfoCheck.IsChecked == true,
            TranslatePlayerId = TranslatePlayerIdCheck.IsChecked == true,
        };

        var ocr = original.Ocr with
        {
            ChatPollMs = ParseIntOr(ChatPollBox.Text, original.Ocr.ChatPollMs),
            OtherPollMs = ParseIntOr(OtherPollBox.Text, original.Ocr.OtherPollMs),
            Upscale = UpscaleCheck.IsChecked == true,
            Grayscale = GrayscaleCheck.IsChecked == true,
            Binarize = BinarizeCheck.IsChecked == true,
            ChangeSensitivity = (int)Math.Round(SensitivitySlider.Value),
        };

        var appearance = original.Appearance with
        {
            FontFamily = OrDefault(FontFamilyBox.Text, original.Appearance.FontFamily),
            FontSize = ParseIntOr(FontSizeBox.Text, original.Appearance.FontSize),
            Opacity = OpacitySlider.Value,
            ShowOriginalAndTranslation = BothRadio.IsChecked == true,
        };

        // Hotkeys：录制/重绑走 OnRecordKeyDown/OnResetHotkeysClick 各自即时 Rebind+Save+写回 Holder，
        // 不经由本页应用/确定/取消——with 未显式覆盖 Hotkeys，故沿用 original(=当前 Holder，天然包含最新重绑结果)。
        return original with { General = general, Translation = translation, Ocr = ocr, Appearance = appearance };
    }

    private static int ParseIntOr(string? text, int fallback) => int.TryParse(text, out var v) ? v : fallback;
    private static string OrDefault(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
