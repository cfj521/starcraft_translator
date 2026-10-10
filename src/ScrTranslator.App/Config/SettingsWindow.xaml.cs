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
    /// <summary>界面布局预设（可重载：保存覆盖层后必须重读，否则表里还是旧值）。</summary>
    private LayoutPresetStore _layout = new();
    private string _layoutFactoryPath = "";
    private string _layoutOverridesPath = "";
    private readonly HotkeyService _hotkeyService;
    /// <summary>provider 池的行（顺序即优先级）。拖动改顺序、勾选改启用；「应用/确定」时回写成 TranslationSettings.Providers。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<ProviderRow> _providerRows = new();

    /// <summary>区域表的一行。Preset=true 表示这是出厂/覆盖层预设（改它写 layout-overrides.json）；
    /// PresetPart 标明改的是预设的哪一块（chat / roomList），校准行为空串。</summary>
    private sealed record RoiRow(string SceneId, string Scene, string Resolution, int Index,
                                 string Name, string Type, string TopLeft, string Size, bool Preset, string PresetPart);
    /// <summary>列区域表的一行。RoiIndex/ColIndex 定位所属框的第几列；Preset=true 时改它写覆盖层。</summary>
    private sealed record ColumnRow(string Owner, string SceneId, string Resolution, int RoiIndex, int ColIndex,
                                    bool Preset, string Name, string TopLeft, string Size);
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
    };

    /// <param name="openCalibration">复用 App.xaml.cs 里管理的单例 CalibrationWindow 打开方法（不在本窗口另建实例）。</param>
    /// <param name="onStateChanged">设置生效后的回调：复位变化检测，让静止画面立即按新设置重译。</param>
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
        _log.Log("[settings] 配置窗口打开");
        _onStateChanged = onStateChanged;
        _onRoisChanged = onRoisChanged;
        _layoutFactoryPath = Path.Combine(AppContext.BaseDirectory, LayoutPresetStore.DefaultFileName);
        // 覆盖层与 rois.json 同目录（%APPDATA%\ScrTranslator），升级 exe 不会丢
        _layoutOverridesPath = Path.Combine(Path.GetDirectoryName(roisJsonPath) ?? AppContext.BaseDirectory,
            LayoutPresetStore.OverridesFileName);
        _layout = layout ?? LayoutPresetStore.Load(_layoutFactoryPath, _layoutOverridesPath);
        _hotkeyService = hotkeyService;

        VersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version}";

        LoadFromSettings(_holder.Current);
        LoadLogView();          // 载入本次运行以来的全部日志
        _log.LogWritten += OnLogWritten; // 实时追加（marshal 到 UI 线程）
        Activated += (_, _) =>
        {
            LoadRoiList(); // 从校准窗口返回时刷新区域页
            ReloadOutOfBandTranslationControls(); // F7/托盘可能在本窗口打开期间改了这三项，重新前台时同步，避免"应用"用陈旧值覆盖
            RefreshStatusText(); // "重新检测"按钮移除后，靠窗口激活时刷新连接状态（游戏窗口本身由 2s 看门狗自动探测）
        };
        Closed += OnWindowClosed;
        Loaded += (_, _) => AlignRestoreButtonWithContent(); // 布局完成后量标签栏宽，对齐"恢复默认"按钮
    }

    // 若用户在"录制中"（已点修改、尚未按键/Esc）关闭本窗口，PreviewKeyDown 订阅本会一直挂着；
    // 这里兜底注销，避免 handler 残留、_recordingFunc 悬空。
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _log.Log("[settings] 配置窗口关闭");
        _log.LogWritten -= OnLogWritten; // 窗口关了不再追加快照，避免 handler 悬空
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
        TranslateChatCheck.IsChecked = s.Translation.TranslateChat;
        TranslateRoomNameCheck.IsChecked = s.Translation.TranslateRoomName;
        TranslateMapInfoCheck.IsChecked = s.Translation.TranslateMapInfo;
        TranslatePlayerIdCheck.IsChecked = s.Translation.TranslatePlayerId;

        SceneDetectPollBox.Text = s.Ocr.SceneDetectPollMs.ToString();
        TranslatePollBox.Text = s.Ocr.TranslatePollMs.ToString();

        FontFamilyBox.Text = s.Appearance.FontFamily;
        FontSizeBox.Text = s.Appearance.FontSize.ToString();
        SelectFontColor(s.Appearance.FontColor);
        OpacitySlider.Value = s.Appearance.Opacity;
        BackplateSlider.Value = s.Appearance.BackplateOpacity;

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
    /// 只显示主显示器当前分辨率的行；坐标一律整数像素；两张表都可双击编辑。
    /// </summary>
    private void LoadRoiList()
    {
        try
        {
            var cfg = RoiStore.Load(_roisJsonPath);
            var (curW, curH) = PrimaryScreenPixels();
            string curRes = $"{curW}x{curH}";
            // 预设参考分辨率：精确命中则用自身，否则取最接近的预设档（当前分辨率不在预设里时仍有参考行，列表不空）
            string refRes = ClosestPresetResolution(curW, curH, _layout.Presets.Select(p => p.Resolution));
            var rows = new List<RoiRow>();
            var colRows = new List<ColumnRow>();

            // --- 校准的 ROI ---
            foreach (var (sceneId, buckets) in cfg.Scenes)
            {
                string sceneLabel = GameSceneIds.FromId(sceneId).Label();
                foreach (var bucket in buckets)
                {
                    if (bucket.Resolution != curRes) continue;
                    var (bw, bh) = ParseResolution(bucket.Resolution);
                    for (int idx = 0; idx < bucket.Rois.Count; idx++)
                    {
                        var roi = bucket.Rois[idx];
                        var t = RoiTypes.Parse(roi.Type);
                        int boxX = Px(roi.Rect.X, bw), boxY = Px(roi.Rect.Y, bh);
                        int boxW = Px(roi.Rect.W, bw), boxH = Px(roi.Rect.H, bh);
                        rows.Add(new RoiRow(sceneId, sceneLabel, bucket.Resolution, idx, roi.Name, TypeLabel(t),
                            $"{boxX}, {boxY}", $"{boxW} × {boxH}", false, ""));

                        // 只有房间名列表型的框才有"列"，其余 ROI 的列定义不参与这张表
                        if (t == RoiType.RoomList && roi.Columns is { Count: > 0 })
                            for (int ci = 0; ci < roi.Columns.Count; ci++)
                            {
                                var c = roi.Columns[ci];
                                int cx = Px(c.RelStart, bw), cw = Px(c.RelEnd - c.RelStart, bw);
                                colRows.Add(new ColumnRow($"{sceneLabel} / {roi.Name}", sceneId, bucket.Resolution,
                                    idx, ci, false, $"{ColumnKindLabel(c.Kind)}（校准）", $"{cx}, {boxY}", $"{cw} × {boxH}"));
                            }
                    }
                }
            }

            // --- 布局预设（聊天框 / 房间列表网格）---
            // 预设按"最接近的预设分辨率"匹配（refRes），而非当前分辨率精确匹配：
            // 当前分辨率不在预设里时仍展示最近档，给用户校准参考，避免整表空白。
            foreach (var p in _layout.Presets)
            {
                if (p.Resolution != refRes) continue;
                string sceneLabel = GameSceneIds.FromId(p.Scene).Label();
                if (p.Chat is { } chat)
                    rows.Add(new RoiRow(p.Scene, sceneLabel, p.Resolution, -1, $"{p.Scene}·聊天(预设)", "聊天",
                        $"{chat.X}, {chat.Y}", $"{chat.Width} × {chat.Height}", true, "chat"));
                if (p.RoomList is { } g)
                {
                    rows.Add(new RoiRow(p.Scene, sceneLabel, p.Resolution, -1, $"{p.Scene}·房间列表(预设)",
                        "房间名列表", $"{g.X}, {g.Y}", $"{g.Width} × {g.Height}", true, "roomList"));
                    for (int ci = 0; ci < g.Columns.Count; ci++)
                    {
                        var c = g.Columns[ci];
                        colRows.Add(new ColumnRow($"{sceneLabel} / 房间列表(预设)", p.Scene, p.Resolution,
                            -1, ci, true, $"{ColumnKindLabel(c.Kind)}（预设）", $"{g.X + c.X}, {g.Y}", $"{c.Width} × {g.Height}"));
                    }
                }
            }

            RegionHintText.Text = $"当前分辨率 {curRes}";
            RoiListView.ItemsSource = rows;
            ColumnsListView.ItemsSource = colRows;
        }
        catch (Exception ex)
        {
            RegionHintText.Text = $"读取区域配置失败: {ex.Message}";
        }
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

    /// <summary>分辨率键（"3840x2160"）→ 宽高像素；非法一律回 1，防除零。</summary>
    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            return (w, h);
        return (1, 1);
    }

    /// <summary>当前分辨率最接近的预设分辨率（精确命中则返回自身）。
    /// 当前分辨率不在预设里时返回最近的那一档，让区域表仍有参考行可供校准，而不是整表空白。</summary>
    private static string ClosestPresetResolution(int curW, int curH, IEnumerable<string> presetResolutions)
    {
        string best = "";
        int bestDist = int.MaxValue;
        foreach (var res in presetResolutions.Distinct())
        {
            var (pw, ph) = ParseResolution(res);
            int dx = curW - pw, dy = curH - ph;
            int dist = dx * dx + dy * dy; // 欧氏距离平方，免开方
            if (dist < bestDist) { bestDist = dist; best = res; }
        }
        return best;
    }

    private static int Px(double rel, int total) => (int)Math.Round(rel * total);

    private static string TypeLabel(RoiType t) => t switch
    {
        RoiType.Chat => "聊天",
        RoiType.RoomList => "房间名列表",
        RoiType.MapInfo => "地图信息",
        _ => t.ToString(),
    };

    /// <summary>重新从磁盘读布局预设（出厂层 + 覆盖层）。保存覆盖层后必须调，否则表里还是旧值。</summary>
    private void ReloadLayoutStore()
        => _layout = LayoutPresetStore.Load(_layoutFactoryPath, _layoutOverridesPath);

    /// <summary>双击主表 = 编辑选中区域。点在表头上则忽略（表头双击的自适应列宽是 GridViewColumnHeader 的内置行为，事件会冒泡到这里）。</summary>
    private void OnRoiListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsOnGridViewHeader(e)) return;
        OnEditRoiClick(sender, e);
    }

    /// <summary>双击列表 = 编辑选中列。点在表头上则忽略（表头双击的自适应列宽是 GridViewColumnHeader 的内置行为，事件会冒泡到这里）。</summary>
    private void OnColumnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsOnGridViewHeader(e)) return;
        OnEditColumnClick(sender, e);
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

    /// <summary>列表表选中态 → 编辑/删除按钮可用性。无选中项时按钮置灰，点不到。</summary>
    private void OnColumnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSel = ColumnsListView.SelectedItem is not null;
        EditColumnButton.IsEnabled = hasSel;
        DeleteColumnButton.IsEnabled = hasSel;
    }

    /// <summary>编辑选中区域：预设行走覆盖层，校准行走 rois.json。</summary>
    private void OnEditRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "编辑区域"); return; }
        if (row.Preset) { EditPresetRegion(row); return; }

        var cfg = RoiStore.Load(_roisJsonPath);
        var current = cfg.Scenes[row.SceneId].First(b => b.Resolution == row.Resolution).Rois[row.Index];
        var (bw, bh) = ParseResolution(row.Resolution);

        var dlg = new RoiEditWindow(row.Resolution, bw, bh, row.SceneId, current) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // 先原地替换内容，再按需换场景（MoveRoi 搬的是替换后的那条，顺序反了会丢掉编辑）
        var updated = RoiStore.ReplaceRoi(cfg, row.SceneId, row.Resolution, row.Index, dlg.Result);
        if (updated is not null && dlg.ResultSceneId != row.SceneId)
            updated = RoiStore.MoveRoi(updated, row.SceneId, row.Resolution, row.Index, dlg.ResultSceneId);
        if (updated is null) { MessageBox.Show(this, "定位失败：列表可能已过期，请重新打开本页。", "编辑区域"); return; }
        CommitRois(updated, $"区域“{row.Name}”未能写入 rois.json。");
    }

    /// <summary>
    /// 编辑预设区域 → 写覆盖层。
    /// 出厂文件 layout-presets.json 是量测产物、程序绝不改写；改后的值进
    /// %APPDATA%\ScrTranslator\layout-overrides.json，删掉该文件即恢复出厂值。
    /// </summary>
    private void EditPresetRegion(RoiRow row)
    {
        var p = FindPreset(row);
        if (p is null) { MessageBox.Show(this, "找不到该预设。", "编辑区域"); return; }

        bool isChat = row.PresetPart == "chat";
        var rect = isChat
            ? (p.Chat is { } c ? (c.X, c.Y, c.Width, c.Height) : (0, 0, 0, 0))
            : (p.RoomList!.X, p.RoomList.Y, p.RoomList.Width, p.RoomList.Height);
        var dlg = new PresetEditWindow(row.Scene, row.Resolution, row.Name, rect, isChat ? null : p.RoomList!.RowHeight)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        var (x, y, w, h) = dlg.Rect;
        var next = isChat
            ? p with { Chat = new PixelRect(x, y, w, h) }
            : p with { RoomList = p.RoomList! with { X = x, Y = y, Width = w, Height = h, RowHeight = dlg.RowHeight ?? p.RoomList.RowHeight } };

        try
        {
            LayoutPresetStore.SaveOverride(_layoutOverridesPath, next);
            ReloadLayoutStore();
            LoadRoiList();
            _onRoisChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "编辑预设");
        }
    }

    LayoutPreset? FindPreset(RoiRow row)
    {
        foreach (var p in _layout.Presets)
            if (p.Scene == row.SceneId && p.Resolution == row.Resolution) return p;
        return null;
    }

    /// <summary>删除选中区域。预设行不能"删"（它不在 rois.json 里），只能改或删覆盖层恢复出厂。</summary>
    private void OnDeleteRoiClick(object sender, RoutedEventArgs e)
    {
        if (RoiListView.SelectedItem is not RoiRow row) { MessageBox.Show(this, "请先在列表里选中一个区域。", "删除区域"); return; }
        if (row.Preset)
        {
            MessageBox.Show(this, "这一条是出厂预设，不能删除；要停用请把它改到画面外，或删掉 layout-overrides.json 恢复出厂值。",
                "删除区域", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"确定删除 {row.Scene} / {row.Resolution} 的区域“{row.Name}”？\n（只删这一分辨率这一条，其它场景与分辨率不受影响）",
                "删除区域", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var cfg = RoiStore.Load(_roisJsonPath);
        var updated = RoiStore.RemoveRoi(cfg, row.SceneId, row.Resolution, row.Index);
        if (updated is null) { MessageBox.Show(this, "定位失败：列表可能已过期，请重新打开本页。", "删除区域"); return; }
        CommitRois(updated, $"区域“{row.Name}”未能删除。");
    }

    /// <summary>编辑选中的列区域：预设列写覆盖层，校准列写 rois.json。</summary>
    private void OnEditColumnClick(object sender, RoutedEventArgs e)
    {
        if (ColumnsListView.SelectedItem is not ColumnRow row) { MessageBox.Show(this, "请先在列表里选中一列。", "编辑列区域"); return; }

        if (row.Preset)
        {
            var p = FindPresetByName(row);
            if (p?.RoomList is not { } g) { MessageBox.Show(this, "找不到所属列表框。", "编辑列区域"); return; }
            var col = g.Columns[row.ColIndex];
            var dlg = new ColumnEditWindow(row.Owner, col.Kind.ToString(), g.X + col.X, col.Width, g.X, g.Width) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var cols = g.Columns.ToList();
            cols[row.ColIndex] = col with { Kind = ColumnKinds.Parse(dlg.Kind), X = dlg.Relative.X, Width = dlg.Relative.Width };
            try
            {
                LayoutPresetStore.SaveOverride(_layoutOverridesPath, p with { RoomList = g with { Columns = cols } });
                ReloadLayoutStore();
                LoadRoiList();
                _onRoisChanged?.Invoke();
            }
            catch (Exception ex) { MessageBox.Show(this, $"保存失败：{ex.Message}", "编辑列区域"); }
            return;
        }

        var cfg = RoiStore.Load(_roisJsonPath);
        var roi = cfg.Scenes[row.SceneId].First(b => b.Resolution == row.Resolution).Rois[row.RoiIndex];
        if (roi.Columns is not { Count: > 0 }) { MessageBox.Show(this, "该区域没有列定义。", "编辑列区域"); return; }
        var (bw, bh) = ParseResolution(row.Resolution);
        int boxX = Px(roi.Rect.X, bw), boxW = Px(roi.Rect.W, bw);
        var target = roi.Columns[row.ColIndex];
        var dlg2 = new ColumnEditWindow(row.Owner, target.Kind.ToString(),
            boxX + (int)Math.Round(target.RelStart * boxW), (int)Math.Round((target.RelEnd - target.RelStart) * boxW),
            boxX, boxW)
        {
            Owner = this,
        };
        if (dlg2.ShowDialog() != true) return;

        // 校准的列存的是"相对框宽"的比例，改完换算回去
        var list = roi.Columns.ToList();
        list[row.ColIndex] = new RoiColumn(
            dlg2.Relative.X / (double)boxW,
            (dlg2.Relative.X + dlg2.Relative.Width) / (double)boxW,
            ColumnKinds.Parse(dlg2.Kind));
        var updated = RoiStore.ReplaceRoi(cfg, row.SceneId, row.Resolution, row.RoiIndex, roi with { Columns = list });
        if (updated is null) { MessageBox.Show(this, "定位失败：列表可能已过期。", "编辑列区域"); return; }
        CommitRois(updated, "列区域未能写入 rois.json。");
    }

    /// <summary>删除选中的列区域。</summary>
    private void OnDeleteColumnClick(object sender, RoutedEventArgs e)
    {
        if (ColumnsListView.SelectedItem is not ColumnRow row) { MessageBox.Show(this, "请先在列表里选中一列。", "删除列区域"); return; }
        if (MessageBox.Show(this, $"确定删除 {row.Owner} 的第 {row.ColIndex + 1} 列（{row.Name}）？",
                "删除列区域", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        if (row.Preset)
        {
            var p = FindPresetByName(row);
            if (p?.RoomList is not { } g) return;
            var cols = g.Columns.ToList();
            cols.RemoveAt(row.ColIndex);
            try
            {
                LayoutPresetStore.SaveOverride(_layoutOverridesPath, p with { RoomList = g with { Columns = cols } });
                ReloadLayoutStore();
                LoadRoiList();
                _onRoisChanged?.Invoke();
            }
            catch (Exception ex) { MessageBox.Show(this, $"保存失败：{ex.Message}", "删除列区域"); }
            return;
        }

        var cfg = RoiStore.Load(_roisJsonPath);
        var roi = cfg.Scenes[row.SceneId].First(b => b.Resolution == row.Resolution).Rois[row.RoiIndex];
        if (roi.Columns is not { Count: > 0 }) return;
        var list = roi.Columns.ToList();
        list.RemoveAt(row.ColIndex);
        var updated = RoiStore.ReplaceRoi(cfg, row.SceneId, row.Resolution, row.RoiIndex, roi with { Columns = list });
        if (updated is null) return;
        CommitRois(updated, "列区域未能删除。");
    }

    LayoutPreset? FindPresetByName(ColumnRow row)
    {
        foreach (var p in _layout.Presets)
            if (p.Scene == row.SceneId && p.Resolution == row.Resolution) return p;
        return null;
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

    /// <summary>只重载可在本窗口打开期间"带外"改变的字段（玩家ID翻译），
    /// 不重载其它控件——避免打断用户正在本窗口内编辑但尚未应用的其它字段。</summary>
    private void ReloadOutOfBandTranslationControls()
    {
        var t = _holder.Current.Translation;
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
            MessageBox.Show(this, $"不支持的按键：{key}（当前支持 F6–F12，可加 Ctrl/Alt/Shift 组合）",
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
            case Key.F10: vk = HotkeyKeys.VK_F10; return true;
            case Key.F11: vk = HotkeyKeys.VK_F11; return true;
            case Key.F12: vk = HotkeyKeys.VK_F12; return true;
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

    /// <summary>切页时控制"恢复默认"按钮显隐：日志/关于页无默认值可恢复，隐藏。</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreDefaultsButton is null) return;
        if (PagesTab.SelectedItem is not TabItem item) return;
        string page = item.Header?.ToString() ?? "";
        RestoreDefaultsButton.Visibility = page is "日志" or "关于" ? Visibility.Collapsed : Visibility.Visible;
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
                TranslateChatCheck.IsChecked = d.Translation.TranslateChat;
                TranslateRoomNameCheck.IsChecked = d.Translation.TranslateRoomName;
                TranslateMapInfoCheck.IsChecked = d.Translation.TranslateMapInfo;
                TranslatePlayerIdCheck.IsChecked = d.Translation.TranslatePlayerId;
                break;
            case "识别":
                SceneDetectPollBox.Text = d.Ocr.SceneDetectPollMs.ToString();
                TranslatePollBox.Text = d.Ocr.TranslatePollMs.ToString();
                break;
            case "区域":
                RestoreRegionDefaults();
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
                break;
        }
    }

    /// <summary>区域页恢复默认：删除全部用户自定义区域（rois.json 清空），并恢复出厂预设
    /// （删掉覆盖层文件即恢复出厂值）。与单个删除不同，这里是一次性全清，需用户确认后才执行。</summary>
    private void RestoreRegionDefaults()
    {
        try
        {
            RoiStore.Save(new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>()), _roisJsonPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除用户区域失败：{ex.Message}", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try
        {
            if (File.Exists(_layoutOverridesPath)) File.Delete(_layoutOverridesPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"恢复预设出厂值失败：{ex.Message}", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ReloadLayoutStore();
        LoadRoiList();
        _onRoisChanged?.Invoke();
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
            FontColor = SelectedFontColor() ?? original.Appearance.FontColor,
            Opacity = OpacitySlider.Value,
            BackplateOpacity = BackplateSlider.Value,
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
