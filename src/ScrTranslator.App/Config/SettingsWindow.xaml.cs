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

    /// <summary>provider 池的一行。Enabled/Status 走 INotifyPropertyChanged 让列表实时刷新。</summary>
    private sealed class ProviderRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        private bool _enabled = true;
        private string _status = "";
        public bool Enabled { get => _enabled; set { _enabled = value; OnChanged(nameof(Enabled)); } }
        public string Status { get => _status; set { _status = value; OnChanged(nameof(Status)); } }
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

        LoadProviderPool(s.Translation.Providers);
        DashScopeModelBox.Text = s.Translation.DashScopeModel;
        ChatGptModelBox.Text = s.Translation.ChatGptModel;
        TranslateChatCheck.IsChecked = s.Translation.TranslateChat;
        TranslateRoomNameCheck.IsChecked = s.Translation.TranslateRoomName;
        TranslateMapInfoCheck.IsChecked = s.Translation.TranslateMapInfo;
        TranslatePlayerIdCheck.IsChecked = s.Translation.TranslatePlayerId;

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

    /// <summary>按 settings 里存的池顺序/启用态重建列表行，并刷新每行状态。</summary>
    private void LoadProviderPool(IReadOnlyList<TranslationProviderEntry> providers)
    {
        _providerRows.Clear();
        foreach (var entry in providers)
        {
            var (name, kind) = ProviderMeta(entry.Id);
            _providerRows.Add(new ProviderRow { Id = entry.Id, Enabled = entry.Enabled, Name = name, Kind = kind });
        }
        ProviderListView.ItemsSource = _providerRows;
        RefreshProviderStatuses();
    }

    /// <summary>provider id → 展示名 + 类别（免费机器翻译 / LLM）。</summary>
    private static (string Name, string Kind) ProviderMeta(string id) => id switch
    {
        TranslationProviderEntry.Tencent => ("腾讯云机器翻译", "免费"),
        TranslationProviderEntry.Aliyun => ("阿里云机器翻译", "免费"),
        TranslationProviderEntry.ChatGpt => ("OpenAI LLM（ChatGPT 订阅）", "LLM"),
        TranslationProviderEntry.DashScope => ("阿里云 LLM（DashScope）", "LLM"),
        _ => (id, "未知"),
    };

    /// <summary>从密钥/OAuth 实时算出每行状态（可用/未配置/未登录）。额度耗尽的判定在 provider 运行时做，这里只反映凭据态。</summary>
    private void RefreshProviderStatuses()
    {
        var sec = _secretStore.Load();
        var (loggedIn, email, plan) = _oauth.Status();
        foreach (var row in _providerRows)
            row.Status = row.Id switch
            {
                TranslationProviderEntry.Tencent => sec.Tencent.IsSet ? "可用" : "未配置",
                TranslationProviderEntry.Aliyun => sec.Aliyun.IsSet ? "可用" : "未配置",
                TranslationProviderEntry.ChatGpt => loggedIn ? $"已登录 {email}({plan})" : "未登录",
                TranslationProviderEntry.DashScope => string.IsNullOrWhiteSpace(sec.DashScopeApiKey) ? "未配置" : "可用",
                _ => "—",
            };
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
            foreach (var p in _layout.Presets)
            {
                if (p.Resolution != curRes) continue;
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

    private void OnRedetectClick(object sender, RoutedEventArgs e) => RefreshStatusText();

    /// <summary>每行的「配置…」：按 provider id 路由到对应的密钥输入窗 / ChatGPT 登录。完成后刷新各行状态。</summary>
    private void OnProviderConfigureClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        switch (id)
        {
            case TranslationProviderEntry.Tencent:
                new TencentKeyWindow(_secretStore) { Owner = this }.ShowDialog();
                break;
            case TranslationProviderEntry.Aliyun:
                new AliyunKeyWindow(_secretStore) { Owner = this }.ShowDialog();
                break;
            case TranslationProviderEntry.DashScope:
                new DashScopeKeyWindow(_secretStore) { Owner = this }.ShowDialog();
                break;
            case TranslationProviderEntry.ChatGpt:
                _ = LoginChatGptAsync(); // 浏览器登录是异步的，不阻塞关闭对话框后的状态刷新
                break;
        }
        RefreshProviderStatuses(); // 密钥/登录变化后刷新每行的状态显示
    }

    /// <summary>ChatGPT 订阅登录（浏览器 PKCE 授权）。成功则让静止画面立即用登录态重译。</summary>
    private async Task<bool> LoginChatGptAsync()
    {
        var chatgptRow = _providerRows.FirstOrDefault(r => r.Id == TranslationProviderEntry.ChatGpt);
        if (chatgptRow is not null) chatgptRow.Status = "登录中…（浏览器完成授权）";
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            bool ok = await _oauth.LoginAsync(cts.Token);
            _log.Log($"[settings] ChatGPT 登录 {(ok ? "成功" : "失败/未完成")}");
            if (ok) _onStateChanged?.Invoke(); // 登录成功后让静止画面立即用登录态重译
            if (!ok)
                MessageBox.Show(this, "ChatGPT 登录未完成或失败（可能：端口 1455 被占用、超时、网络问题或未在浏览器完成授权）。请重试；详情见 logs/app.log。",
                    "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return ok;
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

    private void OnProviderListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ProviderListView.SelectedItem is not ProviderRow) return;
        // 点在行内按钮（如"配置…"）上时不启动拖动：DoDragDrop 的模态循环会吞掉按钮的 Click，对应页面就打不开了
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        DragDrop.DoDragDrop(ProviderListView, ProviderListView.SelectedItem, DragDropEffects.Move);
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

    private void OnProviderListDrop(object sender, DragEventArgs e) => e.Handled = true;

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

    private void OnResetProviderOrderClick(object sender, RoutedEventArgs e)
    {
        _providerRows.Clear();
        foreach (var entry in TranslationSettings.DefaultProviders)
        {
            var (name, kind) = ProviderMeta(entry.Id);
            _providerRows.Add(new ProviderRow { Id = entry.Id, Enabled = entry.Enabled, Name = name, Kind = kind });
        }
        RefreshProviderStatuses();
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
            // 池的顺序 + 每行启用态直接回写。拖动排序、勾选/取消都落在这里。
            Providers = _providerRows.Select(r => new TranslationProviderEntry(r.Id, r.Enabled)).ToList(),
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
