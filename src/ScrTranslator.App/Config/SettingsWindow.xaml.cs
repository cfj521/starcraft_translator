using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using ScrTranslator.Core.Scene;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;

namespace ScrTranslator.App.Config;

/// <summary>配置窗口：左侧竖直标签 6 页 + 关于。DataContext 语义上是 <see cref="SettingsHolder.Current"/> 的可编辑副本——
/// 控件值直到「应用/确定」才写回 <see cref="SettingsHolder.Current"/> + <see cref="SettingsStore.Save"/>；
/// 「取消」直接关闭窗口，不触碰 Holder，天然等价于丢弃副本。单例由 App.xaml.cs 管理（已开则 Activate）。</summary>
public partial class SettingsWindow : Window
{
    private readonly Func<string> _sceneIdProvider; // 区域页展示/校准当前识别到的场景

    private string Scene => _sceneIdProvider();

    private readonly SettingsHolder _holder;
    private readonly string _settingsPath;
    private readonly string _roisJsonPath;
    private readonly SecretStore _secretStore;
    private readonly OpenAiOAuth _oauth;
    private readonly FileLogger _log;
    private readonly Action _openCalibration;
    private readonly Action? _onStateChanged;
    /// <summary>rois.json 被本页"编辑/删除"改动后的回调（= probe.ReloadRois），让正在跑的探测立即用新区域。</summary>
    private readonly Action? _onRoisChanged;
    /// <summary>房间列表固定网格预设（只读展示用；它不参与校准）。</summary>
    private readonly LayoutPresetStore _layout = new();
    private readonly HotkeyService _hotkeyService;

    /// <summary>区域列表的一行。SceneId/Resolution/Index 三元组定位 rois.json 里的哪一条
    /// （名字会被改、序号才是稳定坐标，所以编辑与删除都靠这三元组回写）。</summary>
    private sealed record RoiRow(string SceneId, string Scene, string Resolution, int Index,
                                 string Name, string Type, string TopLeft, string Size, bool Preset);
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
        ("ToggleDebug", "切换 DEBUG 面板"),
        ("CopyDebug", "复制诊断信息"),
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
        HotkeyService hotkeyService,
        Func<string>? sceneIdProvider = null,
        Action? onRoisChanged = null,
        LayoutPresetStore? layout = null)
    {
        InitializeComponent();
        _sceneIdProvider = sceneIdProvider ?? new Func<string>(() => ScrTranslator.Core.Scene.GameSceneIds.Browsing);
        _holder = holder;
        _settingsPath = settingsPath;
        _roisJsonPath = roisJsonPath;
        _secretStore = secretStore;
        _oauth = oauth;
        _log = log;
        _openCalibration = openCalibration;
        _onStateChanged = onStateChanged;
        _onRoisChanged = onRoisChanged;
        _layout = layout ?? LayoutPresetStore.Load(Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName));
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
        DebugModeCheck.IsChecked = s.General.DebugMode;
        RefreshStatusText();

        DashScopeEngineRadio.IsChecked = s.Translation.Active == TranslationEngine.DashScope;
        ChatGptEngineRadio.IsChecked = s.Translation.Active == TranslationEngine.ChatGpt;
        DashScopeModelBox.Text = s.Translation.DashScopeModel;
        ChatGptModelBox.Text = s.Translation.ChatGptModel;
        TranslateChatCheck.IsChecked = s.Translation.TranslateChat;
        TranslateRoomNameCheck.IsChecked = s.Translation.TranslateRoomName;
        TranslateMapInfoCheck.IsChecked = s.Translation.TranslateMapInfo;
        TranslatePlayerIdCheck.IsChecked = s.Translation.TranslatePlayerId;
        RefreshChatGptStatus();

        SceneDetectPollBox.Text = s.Ocr.SceneDetectPollMs.ToString();
        TranslatePollBox.Text = s.Ocr.TranslatePollMs.ToString();

        FontFamilyBox.Text = s.Appearance.FontFamily;
        FontSizeBox.Text = s.Appearance.FontSize.ToString();
        OpacitySlider.Value = s.Appearance.Opacity;
        BackplateSlider.Value = s.Appearance.BackplateOpacity;
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

    /// <summary>
    /// 区域页数据源 = 校准的 ROI ＋ 出厂布局预设，两类条目同表展示、同表核对。
    ///
    /// 三条口径：
    ///  1. **只显示主显示器当前分辨率**的行——同一场景在 1080p/2K/4K 各有一套数值，
    ///     全列出来既刷屏又容易照着错的分辨率改。顶部提示只报当前分辨率，别的统计都不放（用户要求精简）。
    ///  2. 坐标一律**整数像素**（该分辨率下），相对百分比人读不动。
    ///  3. 首列是场景，且**可编辑**（编辑对话框里改场景 = 把这条 ROI 挪到另一个场景的桶）——
    ///     误在别的界面校准的条目不必删了重画。预设行的场景是它本来的归属，不可改。
    /// </summary>
    private void LoadRoiList()
    {
        try
        {
            var cfg = RoiStore.Load(_roisJsonPath);
            var (curW, curH) = PrimaryScreenPixels();
            string curRes = $"{curW}x{curH}";
            var rows = new List<RoiRow>();
            var colRows = new List<ColumnRow>();

            // --- 校准的 ROI ---
            foreach (var (sceneId, buckets) in cfg.Scenes)
            {
                string sceneLabel = GameSceneIds.FromId(sceneId).Label();
                foreach (var bucket in buckets)
                {
                    if (bucket.Resolution != curRes) continue;      // 别的分辨率不进表，也不计数
                    var (bw, bh) = ParseResolution(bucket.Resolution);
                    for (int idx = 0; idx < bucket.Rois.Count; idx++)
                    {
                        var roi = bucket.Rois[idx];
                        var t = RoiTypes.Parse(roi.Type);
                        rows.Add(new RoiRow(sceneId, sceneLabel, bucket.Resolution, idx, roi.Name, TypeLabel(t),
                            $"{Px(roi.Rect.X, bw)}, {Px(roi.Rect.Y, bh)}",
                            $"{Px(roi.Rect.W, bw)} × {Px(roi.Rect.H, bh)}", false));
                        if (roi.Columns is { Count: > 0 })
                            foreach (var c in roi.Columns)
                                colRows.Add(new ColumnRow($"{sceneLabel} / {roi.Name}",
                                    $"{c.RelStart:P0}–{c.RelEnd:P0}（占该框宽度）", ColumnKindLabel(c.Kind)));
                    }
                }
            }

            // --- 出厂布局预设（聊天框 / 房间列表网格）---
            foreach (var p in _layout.Presets)
            {
                if (p.Resolution != curRes) continue;
                var (bw, bh) = ParseResolution(p.Resolution);
                string sceneLabel = GameSceneIds.FromId(p.Scene).Label();
                if (p.Chat is { } chat)
                    rows.Add(new RoiRow(p.Scene, sceneLabel, p.Resolution, -1, $"{p.Scene}·聊天(预设)", "聊天",
                        $"{chat.X}, {chat.Y}", $"{chat.Width} × {chat.Height}", true));
                if (p.RoomList is { } g)
                    rows.Add(new RoiRow(p.Scene, sceneLabel, p.Resolution, -1, $"{p.Scene}·房间列表(预设)",
                        "房间名列表", $"{g.X}, {g.Y}", $"{g.Width} × {g.Height}", true));
                foreach (var c in p.RoomList?.Columns ?? Array.Empty<ListColumn>())
                    colRows.Add(new ColumnRow($"{sceneLabel} / 房间列表(预设)",
                        $"{c.X}px 起、宽 {c.Width}px（占框内像素）", ColumnKindLabel(c.Kind)));
            }

            RegionHintText.Text = rows.Count == 0
                ? $"当前分辨率 {curRes} · 无区域"
                : $"当前分辨率 {curRes}";
            RoiListView.ItemsSource = rows;
            ColumnsListView.ItemsSource = colRows;
        }
        catch (Exception ex)
        {
            RegionHintText.Text = $"读取 rois.json 失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 主显示器的**物理像素**尺寸。SystemParameters.PrimaryScreenWidth 给的是 WPF DIP，
    /// 150%/200% 缩放下会小于真实分辨率，必须乘回 DPI 缩放，否则跟捕获帧(物理像素)对不上号。
    /// </summary>
    private (int W, int H) PrimaryScreenPixels()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        return w > 0 && h > 0 ? (w, h) : (1920, 1080);
    }

    /// <summary>bucket 的分辨率键（"3840x2160"）→ 宽高像素；非法一律回 1，防除零。</summary>
    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            return (w, h);
        return (1, 1);
    }

    private static int Px(double rel, int total) => (int)Math.Round(rel * total);

    private static string TypeLabel(RoiType t) => t switch
    {
        RoiType.Chat => "聊天",
        RoiType.RoomList => "房间名列表",
        RoiType.MapInfo => "地图信息",
        _ => t.ToString(),
    };

    /// <summary>编辑选中区域：改名/类型/像素矩形。改完立即落盘并让探测热重载，不必重启。</summary>
    private void OnEditRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "编辑区域"); return; }
        if (row.Preset) { MessageBox.Show(this, "这一条是出厂布局预设，不在 rois.json 里——要改请编辑 layout-presets.json。", "编辑区域"); return; }
        var cfg = RoiStore.Load(_roisJsonPath);
        var current = cfg.Scenes[row.SceneId].First(b => b.Resolution == row.Resolution).Rois[row.Index];
        var (bw, bh) = ParseResolution(row.Resolution);

        var dlg = new RoiEditWindow(row.Resolution, bw, bh, row.SceneId, current) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // 两步都在这份 cfg 上做，最后一次性落盘：先原地替换内容，再按需换场景
        // （MoveRoi 搬的是"替换后"的那条，顺序反了会把编辑丢掉）。
        var updated = RoiStore.ReplaceRoi(cfg, row.SceneId, row.Resolution, row.Index, dlg.Result);
        if (updated is not null && dlg.ResultSceneId != row.SceneId)
            updated = RoiStore.MoveRoi(updated, row.SceneId, row.Resolution, row.Index, dlg.ResultSceneId);
        if (updated is null) { MessageBox.Show(this, "定位失败：列表可能已过期，请重新打开本页。", "编辑区域"); return; }
        CommitRois(updated, $"区域“{row.Name}”未能写入 rois.json。");
    }

    /// <summary>删除选中区域（含确认）。只删该 (场景,分辨率) 桶里的那一条，不影响其它分辨率。</summary>
    private void OnDeleteRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "删除区域"); return; }
        if (row.Preset) { MessageBox.Show(this, "这一条是出厂布局预设，不在 rois.json 里——要停用请改 layout-presets.json。", "删除区域"); return; }
        if (MessageBox.Show(this,
                $"确定删除 {row.Scene} / {row.Resolution} 的区域“{row.Name}”？\n（只删这一分辨率这一条，其它场景与分辨率不受影响）",
                "删除区域", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var cfg = RoiStore.Load(_roisJsonPath);
        var updated = RoiStore.RemoveRoi(cfg, row.SceneId, row.Resolution, row.Index);
        if (updated is null) { MessageBox.Show(this, "定位失败：列表可能已过期，请重新打开本页。", "删除区域"); return; }
        CommitRois(updated, $"区域“{row.Name}”未能删除。");
    }

    /// <summary>落盘 + 重画列表 + 让正在跑的探测热重载（与校准窗保存后同一条路径）。</summary>
    private void CommitRois(RoiConfig cfg, string failMessage)
    {
        try
        {
            RoiStore.Save(cfg, _roisJsonPath);
            LoadRoiList();
            _onRoisChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, failMessage + "\n" + ex.Message, "保存失败");
        }
    }

    /// <summary>只重载 F7(切换玩家ID翻译)/托盘切引擎这两个可在本窗口打开期间"带外"改变的字段，
    /// 不重载其它控件——避免打断用户正在本窗口内编辑但尚未应用的其它字段。</summary>
    private void ReloadOutOfBandTranslationControls()
    {
        var t = _holder.Current.Translation;
        DashScopeEngineRadio.IsChecked = t.Active == TranslationEngine.DashScope;
        ChatGptEngineRadio.IsChecked = t.Active == TranslationEngine.ChatGpt;
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
                MessageBox.Show(this, "ChatGPT 登录未完成或失败（可能：端口 1455 被占用、超时、网络问题或未在浏览器完成授权）。请重试；详情见 logs/app.log。",
                    "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Error($"[settings] ChatGPT 登录异常: {ex.GetType().Name}: {ex.Message}");
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
            _log.Error($"[settings] 保存热键失败: {ex.Message}");
            MessageBox.Show(this, $"保存热键失败：{ex.Message}", "配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText is not null)
            OpacityValueText.Text = e.NewValue.ToString("P0");
    }

    /// <summary>与 OnOpacityChanged 同形：滑块的 Value 在 InitializeComponent 期间就被赋值
    /// （LoadSettings 里），那时 TextBlock 还没好，所以只能判 null 后再写。</summary>
    private void OnBackplateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BackplateValueText is not null)
            BackplateValueText.Text = e.NewValue.ToString("P0");
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
            _log.Error($"[settings] 保存 settings.json 失败: {ex.Message}");
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
            DebugMode = DebugModeCheck.IsChecked == true,
        };

        var translation = original.Translation with
        {
            Active = ChatGptEngineRadio.IsChecked == true ? TranslationEngine.ChatGpt : TranslationEngine.DashScope,
            DashScopeModel = OrDefault(DashScopeModelBox.Text, original.Translation.DashScopeModel),
            ChatGptModel = OrDefault(ChatGptModelBox.Text, original.Translation.ChatGptModel),
            TranslateChat = TranslateChatCheck.IsChecked == true,
            TranslateRoomName = TranslateRoomNameCheck.IsChecked == true,
            TranslateMapInfo = TranslateMapInfoCheck.IsChecked == true,
            TranslatePlayerId = TranslatePlayerIdCheck.IsChecked == true,
        };

        var ocr = original.Ocr with
        {
            SceneDetectPollMs = ParseIntOr(SceneDetectPollBox.Text, original.Ocr.SceneDetectPollMs),
            TranslatePollMs = ParseIntOr(TranslatePollBox.Text, original.Ocr.TranslatePollMs),
        };

        var appearance = original.Appearance with
        {
            FontFamily = OrDefault(FontFamilyBox.Text, original.Appearance.FontFamily),
            FontSize = ParseIntOr(FontSizeBox.Text, original.Appearance.FontSize),
            Opacity = OpacitySlider.Value,
            BackplateOpacity = BackplateSlider.Value,
            ShowOriginalAndTranslation = BothRadio.IsChecked == true,
        };

        // Hotkeys：录制/重绑走 OnRecordKeyDown/OnResetHotkeysClick 各自即时 Rebind+Save+写回 Holder，
        // 不经由本页应用/确定/取消——with 未显式覆盖 Hotkeys，故沿用 original(=当前 Holder，天然包含最新重绑结果)。
        return original with { General = general, Translation = translation, Ocr = ocr, Appearance = appearance };
    }

    private static int ParseIntOr(string? text, int fallback) => int.TryParse(text, out var v) ? v : fallback;
    private static string OrDefault(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
