using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScrTranslator.App.Auth;
using SkiaSharp;
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
    private readonly SecretStore _secretStore;
    private readonly OpenAiOAuth _oauth;
    private readonly FileLogger _log;
    private readonly Action _openCalibration;
    private readonly Action? _onStateChanged;
    /// <summary>预设被本页"编辑/删除"改动后的回调（= probe.ReloadLayoutPresets），让正在跑的探测立即用新区域。</summary>
    private readonly Action? _onRoisChanged;
    /// <summary>界面布局预设（直写出厂文件，可重载：保存后必须重读，否则表里还是旧值）。</summary>
    private LayoutPresetStore _layout = new();
    private string _layoutFactoryPath = "";
    /// <summary>250ms 同步区域 tab 可见性：本窗非模态，F8/托盘在本窗口打开期间切 DEBUG 也要反映。</summary>
    private DispatcherTimer? _regionTabTimer;
    private readonly HotkeyService _hotkeyService;
    /// <summary>provider 池的行（顺序即优先级）。拖动改顺序、勾选改启用；「应用/确定」时回写成 TranslationSettings.Providers。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<ProviderRow> _providerRows = new();

    /// <summary>区域表的一行：预设识别区域的一块（预设是唯一来源）。Name 存块名（chat/roomNameList/gameName/mapName）。</summary>
    private sealed record RoiRow(string SceneId, string Scene, string Resolution, string Name, string Rect);
    private sealed record HotkeyRow(string Key, string Function, string Current);

    /// <summary>provider 池的一行。Enabled/Status/StatusTooltip/Model 走 INotifyPropertyChanged 让列表实时刷新。
    /// ShowModel 控制该行是否显示模型输入框（仅 ChatGPT/DashScope 两个 LLM 行）。</summary>
    private sealed class ProviderRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        // settable（非 init）：LoadProviderPool 在对象创建后按 provider 类型补设
        public bool ShowModel { get; set; }
        /// <summary>配置按钮文字，按凭据类型区分：订阅登录（ChatGPT）/ 配置KEY（API KEY 类）。</summary>
        public string ConfigButtonText { get; set; } = "配置";
        private bool _enabled = true;
        private string _status = "";
        private string _statusTooltip = "";
        private string _model = "";
        public bool Enabled { get => _enabled; set { _enabled = value; OnChanged(nameof(Enabled)); } }
        public string Status { get => _status; set { _status = value; OnChanged(nameof(Status)); } }
        /// <summary>「订阅登录」按钮的悬浮提示（ChatGPT 行已登录时显示登录账号，未登录为空=不显示）。</summary>
        public string StatusTooltip { get => _statusTooltip; set { _statusTooltip = value; OnChanged(nameof(StatusTooltip)); } }
        /// <summary>该行使用的模型名（ChatGPT/DashScope 行可编辑）。</summary>
        public string Model { get => _model; set { _model = value; OnChanged(nameof(Model)); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    // 录制中的功能名；非 null 表示正在等待下一次按键组合。
    private string? _recordingFunc;

    private static readonly (string Key, string Label)[] HotkeyOrder =
    {
        ("ToggleTranslate", "开/关 翻译"),
        ("Calibrate", "区域校准设置"),
        ("OpenConfig", "设置"),
        ("ToggleDebug", "切换 DEBUG 面板"),
        ("CopyDebug", "复制诊断信息"),
        ("Screenshot", "截图保存"),
    };

    /// <param name="openCalibration">复用 App.xaml.cs 里管理的单例 CalibrationWindow 打开方法（不在本窗口另建实例）。</param>
    /// <param name="onStateChanged">设置生效后的回调：复位变化检测，让静止画面立即按新设置重译。</param>
    public SettingsWindow(
        SettingsHolder holder,
        string settingsPath,
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
        _secretStore = secretStore;
        _oauth = oauth;
        _log = log;
        _openCalibration = openCalibration;
        _log.Log("[settings] 配置窗口打开");
        _onStateChanged = onStateChanged;
        _onRoisChanged = onRoisChanged;
        _layoutFactoryPath = Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName);
        _layout = layout ?? LayoutPresetStore.Load(_layoutFactoryPath);
        _hotkeyService = hotkeyService;

        VersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version}";

        LoadFromSettings(_holder.Current);
        LoadLogView();          // 载入本次运行以来的全部日志
        _log.LogWritten += OnLogWritten; // 实时追加（marshal 到 UI 线程）
        Activated += (_, _) =>
        {
            LoadRoiList(); // 从校准窗口返回时刷新区域页
            RefreshStatusText(); // "重新检测"按钮移除后，靠窗口激活时刷新连接状态（游戏窗口本身由 2s 看门狗自动探测）
        };
        Closed += OnWindowClosed;
        Loaded += (_, _) => AlignRestoreButtonWithContent(); // 布局完成后量标签栏宽，对齐"恢复默认"按钮
        ApplyRegionTabVisibility(_holder.Current.General.DebugMode); // 区域 tab 仅 DEBUG 可见
        _regionTabTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _regionTabTimer.Tick += (_, _) => ApplyRegionTabVisibility(_holder.Current.General.DebugMode); // F8/托盘切 DEBUG 实时反映
        _regionTabTimer.Start();
    }

    // 若用户在"录制中"（已点修改、尚未按键/Esc）关闭本窗口，PreviewKeyDown 订阅本会一直挂着；
    // 这里兜底注销，避免 handler 残留、_recordingFunc 悬空。
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _log.Log("[settings] 配置窗口关闭");
        _log.LogWritten -= OnLogWritten; // 窗口关了不再追加快照，避免 handler 悬空
        _regionTabTimer?.Stop(); // 停区域 tab 可见性同步定时器
        if (_recordingFunc is null) return;
        PreviewKeyDown -= OnRecordKeyDown;
        _recordingFunc = null;
    }

    // ── 日志页 ───────────────────────────────────────────────────────────

    /// <summary>载入本次运行以来的全部日志（FileLogger 内存缓冲，最新在后）。</summary>
    private void LoadLogView()
    {
        LogListBox.Items.Clear();
        foreach (var line in _log.RecentLines) LogListBox.Items.Add(line);
    }

    /// <summary>实时追加一行。仅在用户正停留在底部时自动滚到新行（否则不打断其翻看历史）。</summary>
    private void OnLogWritten(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            bool follow = IsLogAtBottom();
            LogListBox.Items.Add(line);
            if (follow && LogListBox.Items.Count > 0)
                LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
        });
    }

    /// <summary>日志列表是否正显示在底部（用户在看最新行）。未找到内部 ScrollViewer 时按"跟随"处理。</summary>
    private bool IsLogAtBottom()
    {
        var sv = FindScrollViewer(LogListBox);
        if (sv is null) return true;
        double max = sv.ExtentHeight - sv.ViewportHeight;
        return max <= 1 || sv.VerticalOffset >= max - 2;
    }

    /// <summary>沿视觉树第一个子节点向下找 ScrollViewer（ListBox 模板：ListBox→Border→ScrollViewer，几层内必达，不触碰列表项）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        DependencyObject? d = root;
        for (int depth = 0; d is not null && depth < 10; depth++)
        {
            if (d is ScrollViewer sv) return sv;
            int n = VisualTreeHelper.GetChildrenCount(d);
            d = n > 0 ? VisualTreeHelper.GetChild(d, 0) : null;
        }
        return null;
    }

    /// <summary>把当前日志页全部行存成 txt 文件（用户自选路径）。日志项均为字符串行。</summary>
    private void OnExportLogClick(object sender, RoutedEventArgs e)
    {
        if (LogListBox.Items.Count == 0)
        {
            MessageBox.Show(this, "暂无日志可导出。", "导出日志", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"ScrTranslator_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var lines = new List<string>();
            foreach (var item in LogListBox.Items)
                lines.Add(item?.ToString() ?? "");
            File.WriteAllLines(dlg.FileName, lines);
            _log.Log($"[settings] 日志已导出到 {dlg.FileName}（{lines.Count} 行）");
        }
        catch (Exception ex)
        {
            _log.Error($"[settings] 导出日志失败: {ex.Message}");
            MessageBox.Show(this, $"导出日志失败：{ex.Message}", "导出日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 加载 ─────────────────────────────────────────────────────────────

    private void LoadFromSettings(AppSettings s)
    {
        TargetProcessBox.Text = s.General.TargetProcess;
        AutoStartCheck.IsChecked = s.General.AutoStart;
        AutoTranslateCheck.IsChecked = s.General.AutoTranslateOnLaunch;
        DebugModeCheck.IsChecked = s.General.DebugMode;
        RefreshStatusText();

        LoadProviderPool(s.Translation);

        SceneDetectPollBox.Text = s.Ocr.SceneDetectPollMs.ToString();
        TranslatePollBox.Text = s.Ocr.TranslatePollMs.ToString();

        FontFamilyBox.Text = s.Appearance.FontFamily;
        FontSizeBox.Text = s.Appearance.FontSize.ToString();
        SelectFontColor(s.Appearance.FontColor);
        OpacitySlider.Value = s.Appearance.Opacity;
        BackplateSlider.Value = s.Appearance.BackplateOpacity;
        WidthScaleSlider.Value = s.Appearance.FontWidthScale;

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

    /// <summary>按 settings 里存的池顺序/启用态重建列表行，并刷新每行状态。</summary>
    private void LoadProviderPool(TranslationSettings t)
    {
        _providerRows.Clear();
        foreach (var entry in t.Providers)
        {
            var (name, kind) = ProviderMeta(entry.Id);
            var row = new ProviderRow { Id = entry.Id, Enabled = entry.Enabled, Name = name, Kind = kind };
            switch (entry.Id)
            {
                case TranslationProviderEntry.ChatGpt:
                    row.ShowModel = true;
                    row.Model = t.ChatGptModel;
                    row.ConfigButtonText = "订阅登录"; // 订阅授权登录，非 KEY
                    break;
                case TranslationProviderEntry.DashScope:
                    row.ShowModel = true;
                    row.Model = t.DashScopeModel;
                    row.ConfigButtonText = "配置KEY"; // DashScope API Key
                    break;
                case TranslationProviderEntry.OpenAiApi:
                    row.ShowModel = true;
                    row.Model = t.OpenAiApiModel;
                    row.ConfigButtonText = "配置KEY"; // OpenAI API Key（sk-...）
                    break;
                case TranslationProviderEntry.Aliyun:
                    row.ConfigButtonText = "配置KEY"; // AccessKeyId + AccessKeySecret
                    break;
            }
            _providerRows.Add(row);
        }
        ProviderListView.ItemsSource = _providerRows;
        // 诊断级：池顺序+启用态是"翻译取哪个 provider"的直接依据，只在 DEBUG 模式记（加载/重绑/恢复默认都会触发，业务级会刷屏）
        _log.Debug($"[settings] 加载 provider 池：{string.Join(" → ", _providerRows.Select(r => $"{r.Name}({(r.Enabled ? "启用" : "停用")})"))}");
        RefreshProviderStatuses();
    }

    /// <summary>provider id → 展示名 + 类别（免费机器翻译 / LLM）。OpenAI/阿里云 LLM 行不带括号副标题。</summary>
    private static (string Name, string Kind) ProviderMeta(string id) => id switch
    {
        TranslationProviderEntry.Aliyun => ("阿里云机器翻译", "免费"),
        TranslationProviderEntry.ChatGpt => ("OpenAI LLM（订阅）", "LLM"),
        TranslationProviderEntry.DashScope => ("阿里云 LLM", "LLM"),
        TranslationProviderEntry.OpenAiApi => ("OpenAI LLM（API）", "LLM"),
        _ => (id, "未知"),
    };

    /// <summary>从密钥/OAuth 实时算出每行状态（已配置/未配置/未登录）。只反映「凭据是否填了」，**不代表密钥有效**——
    /// 密钥不对时该 provider 运行时翻译失败，报错记日志，编排器跳到池内下一个。ChatGPT 行的登录账号不直接显示在状态列，改放「订阅登录」按钮的 tooltip（已登录后显示，未登录为空）。</summary>
    private void RefreshProviderStatuses()
    {
        var sec = _secretStore.Load();
        var (loggedIn, email, plan) = _oauth.Status();
        foreach (var row in _providerRows)
        {
            row.Status = row.Id switch
            {
                TranslationProviderEntry.Aliyun => sec.Aliyun.IsSet ? "已配置" : "未配置",
                TranslationProviderEntry.ChatGpt => loggedIn ? "已登录" : "未登录",
                TranslationProviderEntry.DashScope => string.IsNullOrWhiteSpace(sec.DashScopeApiKey) ? "未配置" : "已配置",
                TranslationProviderEntry.OpenAiApi => string.IsNullOrWhiteSpace(sec.OpenAiApiKey) ? "未配置" : "已配置",
                _ => "—",
            };
            // 只有「订阅登录」按钮（ChatGPT 行）的 tooltip 有内容（登录账号）；其余行的 tooltip 置 null——
            // 绑 null 不弹框，绑空串会弹一个空 tooltip 框。
            row.StatusTooltip = row.Id == TranslationProviderEntry.ChatGpt && loggedIn
                ? $"{email}（{plan}）"
                : null;
        }
    }

    /// <summary>
    /// 区域页数据源 = 校准的 ROI ＋ 布局预设（出厂值被覆盖层替换后的合并结果），同表展示、同表核对。
    /// 坐标一律显示相对值（0~1），分辨率无关；两张表都可双击编辑。
    /// </summary>
    private void LoadRoiList()
    {
        try
        {
            var (curW, curH) = GameRenderResolution();
            string curRes = $"{curW}x{curH}";
            var rows = new List<RoiRow>();

            // 预设识别区域（唯一来源）：每个场景列 chat/roomNameList/gameName/mapName 四块（有多少列多少）。
            // 预设按宽高比匹配（Find 内部做最近档），显示相对值。
            foreach (var sceneId in new[] { "browsing", "room", "ingame" })
            {
                var p = _layout.Find(sceneId, curW, curH);
                if (p is null) continue;
                string sceneLabel = GameSceneIds.FromId(sceneId).Label();
                foreach (var (name, rect) in p.Parts())
                    rows.Add(new RoiRow(p.Scene, sceneLabel, p.Resolution, name,
                        $"{rect.X:F4}, {rect.Y:F4}, {rect.W:F4}, {rect.H:F4}"));
            }

            RegionHintText.Text = $"当前分辨率 {curRes}，比例 {AspectRatio(curW, curH)}";
            RoiListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            RegionHintText.Text = $"读取区域配置失败: {ex.Message}";
        }
    }

    private (int W, int H)? _gameRes; // 游戏真实渲染分辨率（WGC 抓帧尺寸）缓存；只缓存成功取到的，取不到不缓存、下次重试

    /// <summary>游戏真实渲染分辨率 = WGC 抓帧尺寸（同校准窗的取帧逻辑）。
    /// 屏幕物理像素≠渲染分辨率：DPI-unaware 游戏按内部固定分辨率渲染，全屏时屏幕像素是整屏尺寸
    /// （如 3840×2160），不是渲染分辨率（如 1280×768）。取不到帧时兜底回屏幕物理像素（与旧行为一致）。</summary>
    private (int W, int H) GameRenderResolution()
    {
        if (_gameRes is { } cached) return cached;
        (int W, int H) fallback = PrimaryScreenPixels(); // 兜底：屏幕物理像素
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) return fallback;
        var cap = new CaptureService();
        var tcs = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        cap.FrameArrived += sk => tcs.TrySetResult(sk);
        if (!cap.Start(hwnd)) { cap.Dispose(); return fallback; }
        try
        {
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(3))) return fallback;
            var frame = tcs.Task.Result;
            var res = (frame.Width, frame.Height);
            frame.Dispose();
            _gameRes = res; // 只缓存成功取到的；失败不缓存，下次调用重试
            return res;
        }
        catch { return fallback; }
        finally { cap.Dispose(); }
    }

    /// <summary>
    /// 主显示器的**物理像素**尺寸。SystemParameters.PrimaryScreenWidth 给的是 WPF DIP，
    /// 150%/200% 缩放下会小于真实分辨率，必须乘回 DPI 缩放，否则跟捕获帧（物理像素）对不上号。
    /// </summary>
    private (int W, int H) PrimaryScreenPixels()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        return w > 0 && h > 0 ? (w, h) : (1920, 1080);
    }

    /// <summary>宽高比 → 最近的预设档位标签（"16:9" / "16:10" / "4:3"）。</summary>
    private static string AspectRatio(int w, int h)
    {
        if (h <= 0) return "?";
        double ratio = (double)w / h;
        if (Math.Abs(ratio - 16.0 / 9) < 0.08) return "16:9";
        if (Math.Abs(ratio - 16.0 / 10) < 0.08) return "16:10";
        if (Math.Abs(ratio - 4.0 / 3) < 0.10) return "4:3";
        return $"{ratio:F2}:1";
    }





    /// <summary>重新从磁盘读布局预设（直写出厂文件）。保存后必须调，否则表里还是旧值。</summary>
    private void ReloadLayoutStore()
        => _layout = LayoutPresetStore.Load(_layoutFactoryPath);

    /// <summary>区域 tab 只在 DEBUG 模式可见（预设直写出厂文件，普通用户不该碰）。</summary>
    private void ApplyRegionTabVisibility(bool debugMode)
        => RegionTab.Visibility = debugMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"关于"页的 DEBUG 勾选切换 → 立即反映区域 tab 可见性（不等确定）。</summary>
    private void OnDebugModeToggle(object sender, RoutedEventArgs e)
        => ApplyRegionTabVisibility(DebugModeCheck.IsChecked == true);

    /// <summary>双击主表 = 编辑选中区域。点在表头上则忽略（表头双击的自适应列宽是 GridViewColumnHeader 的内置行为，事件会冒泡到这里）。</summary>
    private void OnRoiListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsOnGridViewHeader(e)) return;
        OnEditRoiClick(sender, e);
    }

    /// <summary>双击点是否落在列头上（沿视觉树向上找 GridViewColumnHeader）。</summary>
    private static bool IsOnGridViewHeader(MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is GridViewColumnHeader) return true;
        return false;
    }

    /// <summary>主表选中态 → 编辑/删除按钮可用性。无选中项时按钮置灰，点不到。</summary>
    private void OnRoiListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSel = RoiListView.SelectedItem is not null;
        EditRoiButton.IsEnabled = hasSel;
        DeleteRoiButton.IsEnabled = hasSel;
    }

    /// <summary>编辑选中预设区域：打开编辑窗改矩形，直写出厂预设文件。</summary>
    private void OnEditRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "编辑区域"); return; }
        var p = FindPreset(row.SceneId, row.Resolution);
        if (p is null) { MessageBox.Show(this, "找不到该预设。", "编辑区域"); return; }
        var rect = GetPartRect(p, row.Name);
        if (rect is null) { MessageBox.Show(this, "该预设块已不存在。", "编辑区域"); return; }

        var dlg = new RoiEditWindow(row.SceneId, row.Name, rect) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            LayoutPresetStore.Save(SetPartRect(p, row.Name, dlg.Result.Rect), _layoutFactoryPath);
            ReloadLayoutStore();
            LoadRoiList();
            _onRoisChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "编辑预设");
        }
    }

    /// <summary>按 (场景, 分辨率档) 在已加载预设里定位一条。</summary>
    private LayoutPreset? FindPreset(string sceneId, string resolution)
    {
        foreach (var p in _layout.Presets)
            if (p.Scene == sceneId && p.Resolution == resolution) return p;
        return null;
    }

    /// <summary>取预设里某一块的相对矩形（块名：chat/roomNameList/gameName/mapName）。</summary>
    private static RelRect? GetPartRect(LayoutPreset p, string partName) => partName switch
    {
        "chat" => p.Chat,
        "roomNameList" => p.RoomNameList,
        "gameName" => p.GameName,
        "mapName" => p.MapName,
        _ => null,
    };

    /// <summary>把某一块的相对矩形设进预设（record 不可变，返回新实例）。</summary>
    private static LayoutPreset SetPartRect(LayoutPreset p, string partName, RelRect rect) => partName switch
    {
        "chat" => p with { Chat = rect },
        "roomNameList" => p with { RoomNameList = rect },
        "gameName" => p with { GameName = rect },
        "mapName" => p with { MapName = rect },
        _ => p,
    };

    /// <summary>删掉某一块（置 null）——预设 part 可删，删光某场景全部块 = 该场景不翻译。</summary>
    private static LayoutPreset RemovePartRect(LayoutPreset p, string partName) => partName switch
    {
        "chat" => p with { Chat = null },
        "roomNameList" => p with { RoomNameList = null },
        "gameName" => p with { GameName = null },
        "mapName" => p with { MapName = null },
        _ => p,
    };

    /// <summary>删除选中预设区域：把该块置 null 直写出厂文件（预设 part 可删，删光某场景全部块 = 该场景不翻译）。</summary>
    private void OnDeleteRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "删除区域"); return; }
        var p = FindPreset(row.SceneId, row.Resolution);
        if (p is null) { MessageBox.Show(this, "找不到该预设。", "删除区域"); return; }
        if (MessageBox.Show(this,
                $"确定删除 {row.Scene} / {row.Resolution} 的区域“{row.Name}”？\n（删除后这一块不再翻译；其它场景与块不受影响）",
                "删除区域", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        try
        {
            LayoutPresetStore.Save(RemovePartRect(p, row.Name), _layoutFactoryPath);
            ReloadLayoutStore();
            LoadRoiList();
            _onRoisChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除失败：{ex.Message}", "删除区域");
        }
    }

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
        // 区域页"进入校准"按钮显示当前绑定的热键（而非写死的 F10）
        if (EnterCalibrationButton is not null && hotkeys.TryGetValue("Calibrate", out var cal))
            EnterCalibrationButton.Content = $"进入校准 ({FormatHotkey(cal)})";
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
            HotkeyKeys.VK_F6 => "F6",
            HotkeyKeys.VK_F7 => "F7",
            HotkeyKeys.VK_F8 => "F8",
            HotkeyKeys.VK_F9 => "F9",
            HotkeyKeys.VK_F10 => "F10",
            HotkeyKeys.VK_F11 => "F11",
            HotkeyKeys.VK_F12 => "F12",
            _ => $"VK{b.Vk:X2}",
        });
        return string.Join("+", parts);
    }

    // ── 交互 ─────────────────────────────────────────────────────────────

    /// <summary>每行的「配置…」：按 provider id 路由到对应的密钥输入窗 / ChatGPT 登录。完成后刷新各行状态。</summary>
    private void OnProviderConfigureClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id)
        {
            _log.Log($"[settings] 配置按钮点击但未识别：sender 类型={sender?.GetType().Name}");
            return;
        }
        _log.Log($"[settings] 点「配置」按钮 provider={id}");
        switch (id)
        {
            case TranslationProviderEntry.Aliyun:
                LogKeyDialogResult("阿里云", new AliyunKeyWindow(_secretStore) { Owner = this }.ShowDialog());
                break;
            case TranslationProviderEntry.DashScope:
                LogKeyDialogResult("DashScope", new DashScopeKeyWindow(_secretStore) { Owner = this }.ShowDialog());
                break;
            case TranslationProviderEntry.OpenAiApi:
                LogKeyDialogResult("OpenAI API", new OpenAiApiKeyWindow(_secretStore) { Owner = this }.ShowDialog());
                break;
            case TranslationProviderEntry.ChatGpt:
                _ = LoginChatGptAsync(); // 浏览器登录是异步的，不阻塞关闭对话框后的状态刷新
                break;
        }
        RefreshProviderStatuses(); // 密钥/登录变化后刷新每行的状态显示
    }

    /// <summary>记密钥输入窗的关闭结果（已保存/已取消/X关闭）。排查"点了没反应"的钥匙：
    /// 若上面记了"点配置按钮"却永远没有这行，说明 ShowDialog 没返回（窗口没弹出或被阻塞）。</summary>
    private void LogKeyDialogResult(string provider, bool? dialogResult)
    {
        string result = dialogResult == true ? "已保存" : dialogResult == false ? "已取消" : "X关闭";
        _log.Log($"[settings] {provider} 密钥窗关闭：{result}");
    }

    /// <summary>ChatGPT 订阅登录（浏览器 PKCE 授权）。成功则让静止画面立即用登录态重译。
    /// 关授权页/超时＝Cancelled（关页后同样收不到回调、只会等到超时，故二者无法区分；仅记详细日志、不弹框）；
    /// 端口占用/网络等＝Failed（弹框报失败）。</summary>
    private async Task<bool> LoginChatGptAsync()
    {
        var chatgptRow = _providerRows.FirstOrDefault(r => r.Id == TranslationProviderEntry.ChatGpt);
        if (chatgptRow is not null) chatgptRow.Status = "登录中…（浏览器完成授权）";
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(1));
            var result = await _oauth.LoginAsync(cts.Token);
            if (result == LoginResult.Success)
            {
                _log.Log("[settings] ChatGPT 登录成功");
                _onStateChanged?.Invoke(); // 登录成功后让静止画面立即用登录态重译
                return true;
            }
            if (result == LoginResult.Cancelled)
            {
                // 等待 1 分钟内未收到浏览器回调（关页/未完成，二者无法区分）。不弹框，只记详细日志指引重试。
                _log.Log("[settings] ChatGPT 登录未完成：等待 1 分钟内未收到浏览器回调（用户可能关闭了授权页，或未在时限内完成授权）。如需登录，重新点「订阅登录」会再次打开授权页。");
                return false;
            }
            _log.Log("[settings] ChatGPT 登录失败（可能：端口 1455 被占用、网络问题，或授权页被拒）");
            MessageBox.Show(this, "ChatGPT 登录失败（可能：端口 1455 被占用、网络问题，或授权页被拒）。请重试；详情见日志。",
                "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"[settings] ChatGPT 登录异常: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, $"ChatGPT 登录出错：{ex.Message}", "登录失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            RefreshProviderStatuses();
        }
    }

    // ── provider 池：拖动排序 ────────────────────────────────────────────

    /// <summary>拖拽源＝光标下的行。用隧道事件 PreviewMouseLeftButtonDown 启动：冒泡事件会被
    /// ListViewItem 的选中逻辑抢先标记 Handled，导致列表级 handler 不跑、DoDragDrop 没启动。</summary>
    private void OnProviderListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var src = e.OriginalSource as DependencyObject;
        // 点在行内按钮/勾选框/模型输入框上时不启动拖动：DoDragDrop 的模态循环会吞掉它们的 Click/Toggle/Focus，对应功能就用不了
        if (FindAncestor<Button>(src) is not null || FindAncestor<CheckBox>(src) is not null || FindAncestor<TextBox>(src) is not null) return;
        // 直接抓光标下的行作为拖拽源：不必先选中再拖
        if (FindAncestor<ListViewItem>(src)?.DataContext is not ProviderRow row) return;
        ProviderListView.SelectedItem = row;
        DragDrop.DoDragDrop(ProviderListView, row, DragDropEffects.Move);
    }

    private void OnProviderListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        if (e.Data.GetData(typeof(ProviderRow)) is not ProviderRow source)
        { e.Effects = DragDropEffects.None; return; }
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ProviderRow target) { e.Effects = DragDropEffects.None; return; }
        if (ReferenceEquals(target, source)) { e.Effects = DragDropEffects.None; return; }
        int from = _providerRows.IndexOf(source);
        int to = _providerRows.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
            _providerRows.Move(from, to); // 即时移动，拖动过程中列表实时换位
        e.Handled = true;
    }

    private void OnProviderListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // 诊断级：拖拽排序改变 provider 优先级，只在 DEBUG 模式记（拖拽过程会高频触发 DragOver，业务级会刷屏）
        _log.Debug($"[settings] provider 顺序已调整：{string.Join(" → ", _providerRows.Select(r => r.Name))}");
    }

    /// <summary>从光标下的最深元素沿视觉树往上找最近的 ListViewItem。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T t) return t;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
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

    /// <summary>点「关于」页的 GitHub 仓库超链接 → 用默认浏览器打开仓库首页。</summary>
    private void OnRepoLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 无默认浏览器等异常时静默 */ }
    }

    private void OnEnterCalibrationClick(object sender, RoutedEventArgs e)
    {
        _log.Log("[settings] 进入区域校准");
        _openCalibration(); // 具体能否校准/结果由 App 的 OpenCalibration 记日志
    }

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
            MessageBox.Show(this, $"不支持的按键：{key}（当前支持 F6–F9，可加 Ctrl/Alt/Shift 组合）",
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
        _log.Log($"[settings] 热键已重绑：{FuncLabel(func)} = {FormatHotkey(binding)}");
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
            case Key.F6: vk = HotkeyKeys.VK_F6; return true;
            case Key.F7: vk = HotkeyKeys.VK_F7; return true;
            case Key.F8: vk = HotkeyKeys.VK_F8; return true;
            case Key.F9: vk = HotkeyKeys.VK_F9; return true;
            default: vk = 0; return false;
        }
    }

    /// <summary>热键恢复默认：即时重绑 + 写盘 + 写回 Holder（热键是活绑定的，不经确定/取消）。</summary>
    private void ResetHotkeysToDefaults()
    {
        _hotkeyService.ResetToDefaults(HotkeyDefaults.Map);
        _holder.Current = _holder.Current with { Hotkeys = HotkeyDefaults.Map };
        SaveHotkeys();
        LoadHotkeyList(_holder.Current.Hotkeys);
        _log.Log("[settings] 热键已恢复默认");
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

    /// <summary>与 OnOpacityChanged / OnBackplateChanged 同形：滑块的 Value 在 InitializeComponent 期间就被赋值
    /// （LoadSettings 里），那时 TextBlock 还没好，所以只能判 null 后再写。</summary>
    private void OnWidthScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidthScaleValueText is not null)
            WidthScaleValueText.Text = e.NewValue.ToString("P0");
    }

    /// <summary>调色板选中态：当前被选中的色块（蓝框标示）。null＝未选。</summary>
    private Border? _selectedFontSwatch;
    /// <summary>色块默认边框（未选中）。</summary>
    private static readonly Brush SwatchDefaultBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    /// <summary>色块选中边框（蓝）。</summary>
    private static readonly Brush SwatchSelectedBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));

    /// <summary>点色块选色：清旧框、给新块画蓝框。（Border 无 Click 事件，用 MouseLeftButtonUp）</summary>
    private void OnFontColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border swatch) return;
        ClearFontSwatchSelection();
        _selectedFontSwatch = swatch;
        swatch.BorderBrush = SwatchSelectedBrush;
        swatch.BorderThickness = new Thickness(2);
    }

    /// <summary>清掉当前选中色块的蓝框（还原默认灰框）。</summary>
    private void ClearFontSwatchSelection()
    {
        if (_selectedFontSwatch is not null)
        {
            _selectedFontSwatch.BorderBrush = SwatchDefaultBrush;
            _selectedFontSwatch.BorderThickness = new Thickness(1);
            _selectedFontSwatch = null;
        }
    }

    /// <summary>把设置里存的字体颜色选到调色板对应色块。按 Tag（真实 hex）匹配，
    /// 无匹配项（如老配置里的非法值）则不选中，由 <see cref="SelectedFontColor"/> 兜底回默认。</summary>
    private void SelectFontColor(string colorName)
    {
        if (FontColorPalette is null) return;
        ClearFontSwatchSelection();
        foreach (var child in FontColorPalette.Children)
        {
            if (child is Border b && string.Equals(b.Tag as string, colorName, StringComparison.OrdinalIgnoreCase))
            {
                b.BorderBrush = SwatchSelectedBrush;
                b.BorderThickness = new Thickness(2);
                _selectedFontSwatch = b;
                return;
            }
        }
    }

    /// <summary>读调色板当前选中色块的 Tag（真实色值）；未选返回 null（由调用方兜底）。</summary>
    private string? SelectedFontColor() => _selectedFontSwatch?.Tag as string;

    // ── 恢复默认 / 确定 / 取消 ───────────────────────────────────────────

    /// <summary>按当前页签恢复该页默认值。日志/关于页不显示此按钮（见 OnTabSelectionChanged）。</summary>
    /// <summary>把"恢复默认"按钮左缘对齐到内容区（GroupBox）左缘。
    /// 按钮贴在底栏左缘（x=窗口边距），而 GroupBox 在标签栏右侧（x=边距+标签栏宽+内容边距8），
    /// 故给按钮补 标签栏宽+8 的左边距。标签栏宽在运行时量出（视觉树里 IsItemsHost 容器）。</summary>
    private void AlignRestoreButtonWithContent()
    {
        if (RestoreDefaultsButton is null) return;
        double tabStrip = MeasureTabStripWidth(PagesTab);
        RestoreDefaultsButton.Margin = new Thickness(tabStrip + 8, 0, 8, 0);
    }

    /// <summary>递归找 TabControl 里 IsItemsHost 的 Panel（标签栏，TabPanel 派生自它），返回其宽；找不到返回 0。</summary>
    private static double MeasureTabStripWidth(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel p && p.IsItemsHost)
                return p.ActualWidth;
            double w = MeasureTabStripWidth(child);
            if (w > 0) return w;
        }
        return 0;
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (PagesTab.SelectedItem is not TabItem item) return;
        string page = item.Header?.ToString() ?? "";
        if (MessageBox.Show(this, $"确定将「{page}」页恢复为默认值？", "恢复默认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RestorePageDefaults(page);
    }

    /// <summary>切页时控制"恢复默认"按钮显隐：日志/关于/区域页无默认值可恢复（区域 tab 直读直写出厂预设，"恢复默认"无意义），隐藏。</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreDefaultsButton is null) return;
        if (PagesTab.SelectedItem is not TabItem item) return;
        string page = item.Header?.ToString() ?? "";
        RestoreDefaultsButton.Visibility = page is "日志" or "关于" or "区域" ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>按页名恢复该页默认值。表单页（常规/翻译/OCR/外观）只重置控件，需点「确定」才写盘；
    /// 热键即时重绑、区域即时落盘（二者本就不经确定/取消）。</summary>
    private void RestorePageDefaults(string page)
    {
        _log.Log($"[settings] 恢复「{page}」页默认值");
        var d = new AppSettings(); // 全部默认值
        switch (page)
        {
            case "常规":
                TargetProcessBox.Text = d.General.TargetProcess;
                AutoStartCheck.IsChecked = d.General.AutoStart;
                AutoTranslateCheck.IsChecked = d.General.AutoTranslateOnLaunch;
                DebugModeCheck.IsChecked = d.General.DebugMode;
                break;
            case "翻译":
                LoadProviderPool(d.Translation); // 含 LLM 行的模型名重置
                break;
            case "识别":
                SceneDetectPollBox.Text = d.Ocr.SceneDetectPollMs.ToString();
                TranslatePollBox.Text = d.Ocr.TranslatePollMs.ToString();
                break;
            case "热键":
                ResetHotkeysToDefaults();
                break;
            case "外观":
                FontFamilyBox.Text = d.Appearance.FontFamily;
                FontSizeBox.Text = d.Appearance.FontSize.ToString();
                SelectFontColor(d.Appearance.FontColor);
                OpacitySlider.Value = d.Appearance.Opacity;
                BackplateSlider.Value = d.Appearance.BackplateOpacity;
                WidthScaleSlider.Value = d.Appearance.FontWidthScale;
                break;
        }
    }

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
            _log.Log("[settings] 设置已保存（settings.json）");
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

        // StartMinimized 不再有 UI 控件（恒为真，程序无主窗口），沿用当前值不覆盖。
        var general = original.General with
        {
            TargetProcess = OrDefault(TargetProcessBox.Text, original.General.TargetProcess),
            AutoStart = AutoStartCheck.IsChecked == true,
            AutoTranslateOnLaunch = AutoTranslateCheck.IsChecked == true,
            DebugMode = DebugModeCheck.IsChecked == true,
        };

        // 模型名从对应行读取（ChatGPT/DashScope/OpenAI API 行的 Model 字段；行缺失时用当前值兜底）
        string chatGptModel = OrDefault(_providerRows.FirstOrDefault(r => r.Id == TranslationProviderEntry.ChatGpt)?.Model, original.Translation.ChatGptModel);
        string dashScopeModel = OrDefault(_providerRows.FirstOrDefault(r => r.Id == TranslationProviderEntry.DashScope)?.Model, original.Translation.DashScopeModel);
        string openAiApiModel = OrDefault(_providerRows.FirstOrDefault(r => r.Id == TranslationProviderEntry.OpenAiApi)?.Model, original.Translation.OpenAiApiModel);
        var translation = original.Translation with
        {
            // 池的顺序 + 每行启用态直接回写。拖动排序、勾选/取消都落在这里。
            Providers = _providerRows.Select(r => new TranslationProviderEntry(r.Id, r.Enabled)).ToList(),
            DashScopeModel = dashScopeModel,
            ChatGptModel = chatGptModel,
            OpenAiApiModel = openAiApiModel,
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
            FontColor = SelectedFontColor() ?? original.Appearance.FontColor,
            Opacity = OpacitySlider.Value,
            BackplateOpacity = BackplateSlider.Value,
            FontWidthScale = WidthScaleSlider.Value,
            // 显示模式已移除，恒为"仅译文"（ShowOriginalAndTranslation 保持 false）
            ShowOriginalAndTranslation = false,
        };

        // Hotkeys：录制/重绑/恢复默认走 OnRecordKeyDown/ResetHotkeysToDefaults 各自即时 Rebind+Save+写回 Holder，
        // 不经由本页确定/取消——with 未显式覆盖 Hotkeys，故沿用 original(=当前 Holder，天然包含最新重绑结果)。
        return original with { General = general, Translation = translation, Ocr = ocr, Appearance = appearance };
    }

    private static int ParseIntOr(string? text, int fallback) => int.TryParse(text, out var v) ? v : fallback;
    private static string OrDefault(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
