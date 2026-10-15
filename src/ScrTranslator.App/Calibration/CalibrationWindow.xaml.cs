using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Scene;
using SkiaSharp;

namespace ScrTranslator.App.Calibration;

/// <summary>校准窗口：全屏半透明、可交互（非点击穿透）。拖框定义 ROI，RoomList 类型可划竖线分列。
/// F8 打开（App.xaml.cs）；Esc 取消不存、Enter 归一化写 rois.json（RoiStore.Save）。</summary>
public partial class CalibrationWindow : Window
{
    /// <summary>本次校准写入的场景键。**由校准窗自己抓一帧指纹判定**（OnLoaded 里做），
    /// 所以它不是 readonly：检测到真实场景后会覆盖初始默认值。</summary>
    private string _sceneId;
    private readonly SceneRecognizer? _scene;
    private readonly LayoutPresetStore? _layout;
    private const double HandleSize = 8;
    private const double MinBoxSize = 10;             // DIP，用于"拖角不许缩成线"
    private const double MinNewBoxPhysicalPx = 20;    // 新建框的最小边（物理像素）：小于它一律当作单击/误触

    private readonly string _roisJsonPath;
    private readonly FileLogger? _log;
    private readonly Action? _onSaved;

    private readonly List<CalibBox> _boxes = new();
    private CalibBox? _selected;
    private int _colorCounter; // 已分配色号：预设框与用户框共用，按创建顺序分配，保证屏上所有区域互不同色

    private enum Mode { Idle, Drawing, ResizingCorner, DraggingDivider, MovingBox }
    private Mode _mode = Mode.Idle;
    private Point _dragStart;
    private Rectangle? _drawPreview;
    private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
    private Rect _resizeStartRect;
    private int _dividerIndex;
    // 整框平移：按住框体（非角柄/分割线）拖动 = 平移整个框；点击不动 = 只选中
    private CalibBox? _movingBox;
    private Point _moveStart;
    private Rect _moveStartRect;
    private Border? _newBoxPanel;
    private Rect? _pendingRect;
    // 操作条拖拽：按住它自己（非控件区）可以挪动，避免挡住正在对齐的区域
    private bool _panelDragging;
    private Point _panelDragStart;
    private Point _panelOriginStart;

    private System.Drawing.Rectangle _gameRect; // 窗口物理像素矩形：绘制 + 相对坐标换算基准（同 TranslationEngine/ScreenMapper 的窗口矩形）
    private int _frameW, _frameH; // WGC 抓帧尺寸 = 游戏真实渲染分辨率：桶键 + 布局匹配 + 分辨率显示基准
    private double _dpiX = 1, _dpiY = 1;

    private sealed record Option<T>(T Value, string Label);
    private static readonly Option<ColumnKind>[] ColumnKindOptions =
    {
        new(ColumnKind.RoomName, "房间名"), new(ColumnKind.MapName, "地图"),
        new(ColumnKind.PlayerId, "玩家ID"), new(ColumnKind.Other, "其它"),
    };
    /// <summary>区域专属配色：每个区域一个**不同**颜色的边框（不再按类型着色），预设框与用户框共用此表、
    /// 预设先取色、用户框从其计数之后接着取，循环使用；无内填充，纯边框标识。</summary>
    private static readonly Brush[] RegionColors =
    {
        new SolidColorBrush(Color.FromRgb(255, 80,  80)),  // 红
        new SolidColorBrush(Color.FromRgb( 80, 255, 100)), // 绿
        new SolidColorBrush(Color.FromRgb( 80, 160, 255)), // 蓝
        new SolidColorBrush(Color.FromRgb(255, 220,  60)), // 黄
        new SolidColorBrush(Color.FromRgb(255, 100, 255)), // 品红
        new SolidColorBrush(Color.FromRgb( 80, 255, 255)), // 青
        new SolidColorBrush(Color.FromRgb(255, 160,  60)), // 橙
        new SolidColorBrush(Color.FromRgb(180, 120, 255)), // 紫
        new SolidColorBrush(Color.FromRgb(255, 140, 160)), // 粉
        new SolidColorBrush(Color.FromRgb(140, 255, 140)), // 黄绿
    };

    /// <summary>一个已框选区域的编辑态：Canvas 内 DIP 矩形 + 可选列定义（仅 RoomList 用）。</summary>
    private sealed class CalibBox
    {
        public string Name = "新区域";
        public Rect CanvasRect;
        public readonly List<double> Dividers = new(); // 相对 ROI 宽度 0..1，已排序
        public List<ColumnKind> Kinds = new() { ColumnKind.RoomName }; // Dividers.Count+1 个

        public Rectangle Visual = null!;
        public TextBlock Label = null!;
        public Rectangle[]? Handles;
        public readonly List<Line> DividerLines = new();
        public Border? ColumnPanel;
        public Brush Color = null!; // 区域专属边框色（NextColor 按创建顺序分配，保证屏上所有区域互不同色）

        /// <summary>名字带 <see cref="LayoutPreset.PresetNamePrefix"/>（%preset%）特殊标记 = 预设框。
        /// 预设框与用户框共用同一套拖拽/调角/平移逻辑，但保存时走不同落盘：
        /// 用户框写 rois.json，预设框写 layout-overrides.json（更新对应 LayoutPreset 字段）。
        /// 靠名字判断（而不是单独的 bool 字段），这样框被改名/重读盘后路由规则依然成立。</summary>
        public bool IsPreset => Name.StartsWith(LayoutPreset.PresetNamePrefix);
    }

    /// <param name="onSaved">保存成功（Enter 触发 SaveAndClose 走完写盘且未抛异常）后调用；
    /// 供 App.xaml.cs 接线让运行中的探测重载 rois.json 与布局预设（校准窗两个文件都写），改动无需重启即可生效。</param>
    /// <param name="scene">场景识别器（图像指纹）。为 null 时跳过检测，用默认场景。</param>
    /// <param name="layout">布局预设：校准窗把它们建成可交互编辑框（名字带 %preset% 标记），保存时写回 layout-overrides.json。</param>
    public CalibrationWindow(string roisJsonPath, FileLogger? log = null, Action? onSaved = null,
        SceneRecognizer? scene = null, LayoutPresetStore? layout = null)
    {
        InitializeComponent();
        _roisJsonPath = roisJsonPath;
        _log = log;
        _onSaved = onSaved;
        // 不用打开方的历史场景：默认"其他界面"（=未判定），窗口一创建就用 WGC 抓一帧自己判定（DetectScene），匹配才覆盖
        _sceneId = GameSceneIds.Other;
        _scene = scene;
        _layout = layout;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        // 场景由窗口创建后自己判定（WGC 窗口捕获，不受遮挡影响）——详见 DetectScene
        FindGameRect();
        DetectScene();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;

        // 场景已在构造器里判定完（DetectScene）。这里只画
        DrawGameOutline();
        DrawPresetBoxes();
        LoadExisting();

        Focusable = true;
        Focus();
        Keyboard.Focus(this);

        // 操作条打开期间，整个窗口失活（Alt+Tab 切走）也响一声——它还在等输入名称/类型
        Deactivated += (_, _) => { if (_newBoxPanel is not null) System.Media.SystemSounds.Beep.Play(); };
    }

    /// <summary>找游戏窗口的物理像素矩形（找不到退化为整个主屏）。判定全在物理像素里跑，不依赖 DIP。</summary>
    private void FindGameRect()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        var rect = hwnd == IntPtr.Zero ? System.Drawing.Rectangle.Empty : NativeMethods.GetWindowScreenRect(hwnd);
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            _log?.Log("[calibration] 未找到 StarCraft 窗口，退化为使用主屏幕范围作为校准基准");
            double scale = NativeMethods.GetDpiForSystem() / 96.0;
            rect = new System.Drawing.Rectangle(0, 0,
                (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale),
                (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale));
        }
        _gameRect = rect;
        // 兜底：WGC 拿不到帧时，用窗口尺寸当分辨率基准（宁可错也比空好）；DetectScene 拿到帧后覆盖。
        _frameW = rect.Width;
        _frameH = rect.Height;
    }

    /// <summary>
    /// 抓一帧游戏画面做指纹场景判定。WGC 窗口捕获——与探测同一套 CaptureService，
    /// 直接拿游戏窗口的 D3D 渲染输出，本窗的暗罩盖住屏幕也无所谓（探测没有屏幕捕获
    /// 兜底，校准窗也不设）。
    /// 这样校准**不依赖 F9 是否开启**——没开翻译也能定准当前场景，框的区域才会落到对的场景桶里。
    /// </summary>
    private void DetectScene()
    {
        if (_scene is null) return;
        SKBitmap? frame = null;
        try
        {
            frame = CaptureOneFrameWgc();
            if (frame is null)
            {
                _log?.Log("[calibration] WGC 捕获不可用，场景未判定，保持其他界面");
                return; // _frameW/_frameH 保持 FindGameRect 的窗口兜底值
            }
            // WGC 帧尺寸 = 游戏真实渲染分辨率，覆盖窗口兜底值，作桶键 + 布局匹配基准
            _frameW = frame.Width;
            _frameH = frame.Height;
            var rec = _scene.Recognize(frame.AsFramePixels()); // 零拷贝视图，只在 Recognize 调用内同步使用
            _log?.Log($"[calibration] 场景检测（WGC）：{rec.Summary}");
            // 只有匹配才覆盖默认值；未匹配（低于阈值/该分辨率无参考）保持"其他界面"
            if (rec.IsMatched) _sceneId = rec.Scene.ToId();
        }
        catch (Exception ex)
        {
            _log?.Log($"[calibration] 场景检测失败，使用默认场景 {_sceneId}: {ex.Message}");
        }
        finally { frame?.Dispose(); }
    }

    /// <summary>WGC 窗口捕获：抓一帧游戏窗口的 D3D 渲染输出（复用探测的 CaptureService，抓完即弃）。
    /// 不受遮挡影响——直接拿窗口渲染输出，本窗盖不盖住屏幕都无所谓。
    /// 返回 null 的情况：找不到游戏窗口 / 系统不支持窗口捕获 / 3 秒内没等到帧
    /// （用户首次还没允许捕获时系统会弹同意框，等不到帧；同意过后下次即达）。</summary>
    private SKBitmap? CaptureOneFrameWgc()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) return null;
        var cap = new CaptureService();
        var tcs = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        cap.FrameArrived += sk => tcs.TrySetResult(sk);
        if (!cap.Start(hwnd)) { cap.Dispose(); return null; }
        try
        {
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(3))) return null;
            return tcs.Task.Result;
        }
        finally { cap.Dispose(); }
    }

    /// <summary>把当前场景/分辨率的布局预设建成**可交互编辑框**（可选中/整框平移/拖角改大小），
    /// 改动随 Enter 写进 layout-overrides.json。目的：用户对着游戏画面直接拖调预设区域，不必去配置页改坐标。</summary>
    private void DrawPresetBoxes()
    {
        var p = _layout?.Find(_sceneId, _frameW, _frameH);
        if (p is null) return;
        if (p.Chat is { } chat) AddPresetBox("(预设)聊天区域", chat);
        if (p.RoomNameList is { } rnl) AddPresetBox("(预设)房间名列表区域", rnl);
        if (p.GameName is { } gn) AddPresetBox("(预设)房间名称区域", gn);
        if (p.MapName is { } mn) AddPresetBox("(预设)地图名称区域", mn);
    }

    /// <summary>把一个预设区域建成可交互 CalibBox，加入 _boxes 并画出来。
    /// 名字带 <see cref="LayoutPreset.PresetNamePrefix"/> 标记（%preset% + 显示标签），
    /// 保存时据此分流到 layout-overrides.json，与用户框（写 rois.json）分开。</summary>
    private void AddPresetBox(string label, RelRect rect)
    {
        var dip = PhysicalToDipRect(new System.Drawing.Rectangle(
            _gameRect.Left + (int)Math.Round(rect.X * _gameRect.Width),
            _gameRect.Top + (int)Math.Round(rect.Y * _gameRect.Height),
            (int)Math.Round(rect.W * _gameRect.Width),
            (int)Math.Round(rect.H * _gameRect.Height)));
        var box = new CalibBox
        {
            Name = LayoutPreset.PresetNamePrefix + label,
            CanvasRect = dip,
        };
        _boxes.Add(box);
        AddBoxVisual(box);
    }

    private static string ColumnKindLabel(ColumnKind k) => k switch
    {
        ColumnKind.RoomName => "房间名",
        ColumnKind.MapName => "地图",
        ColumnKind.PlayerId => "玩家ID",
        _ => "其它",
    };

    // ── 加载 ─────────────────────────────────────────────────────────────

    private void DrawGameOutline()
    {
        var dip = PhysicalToDipRect(_gameRect);
        var outline = new Rectangle
        {
            Width = dip.Width,
            Height = dip.Height,
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, dip.X);
        Canvas.SetTop(outline, dip.Y);
        RootCanvas.Children.Add(outline);
    }

    private void LoadExisting()
    {
        RoiConfig cfg;
        try { cfg = RoiStore.Load(_roisJsonPath); }
        catch (Exception ex)
        {
            _log?.Log($"[calibration] 加载 rois.json 失败: {ex.Message}");
            cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>>());
        }

        string resKey = $"{_frameW}x{_frameH}";
        string sceneLabel = GameSceneIds.FromId(_sceneId).Label();
        // 顶部常驻显示"正在校准哪个场景"——它决定 Enter 保存后这些框落到哪个场景，
        // 不显示的话用户根本不知道自己其实是在给"游戏内"画框。
        // 相对坐标跨分辨率通用，不再按分辨率分档。
        HintText.Text = $"场景【{sceneLabel}】 · 分辨率 {resKey} · 拖动鼠标框选 (Esc 退出 / Enter 保存)";
        if (!cfg.Scenes.TryGetValue(_sceneId, out var rois))
            return;

        foreach (var roi in rois)
        {
            var px = new System.Drawing.Rectangle(
                _gameRect.Left + (int)Math.Round(roi.Rect.X * _gameRect.Width),
                _gameRect.Top + (int)Math.Round(roi.Rect.Y * _gameRect.Height),
                (int)Math.Round(roi.Rect.W * _gameRect.Width),
                (int)Math.Round(roi.Rect.H * _gameRect.Height));
            var box = new CalibBox
            {
                Name = roi.Name,
                CanvasRect = PhysicalToDipRect(px),
            };
            if (roi.Columns is { Count: > 0 })
            {
                box.Dividers.Clear();
                box.Dividers.AddRange(roi.Columns.Skip(1).Select(c => c.RelStart));
                box.Kinds = roi.Columns.Select(c => c.Kind).ToList();
            }
            _boxes.Add(box);
            AddBoxVisual(box);
        }
    }

    // ── 坐标换算（DIP canvas <-> 物理屏幕像素，基准=游戏窗口矩形，与 ScreenMapper/TranslationEngine 一致）──

    private Rect PhysicalToDipRect(System.Drawing.Rectangle r)
        => new(r.Left / _dpiX, r.Top / _dpiY, r.Width / _dpiX, r.Height / _dpiY);

    // ── 鼠标：画布级状态机 ───────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 焦点先让给窗口：操作条还开着时点画布，nameBox 失焦 → 触发它的 LostFocus 提示音
        Focus();
        if (_newBoxPanel is not null) return; // 新建面板打开时，画布点击不生效
        DeselectCurrent();

        var p = e.GetPosition(RootCanvas);
        _mode = Mode.Drawing;
        _dragStart = p;
        _drawPreview = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
        };
        Canvas.SetLeft(_drawPreview, p.X);
        Canvas.SetTop(_drawPreview, p.Y);
        RootCanvas.Children.Add(_drawPreview);
        RootCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_mode == Mode.Idle) return;
        var p = e.GetPosition(RootCanvas);
        p = new Point(
            Math.Clamp(p.X, 0, Math.Max(0, RootCanvas.ActualWidth)),
            Math.Clamp(p.Y, 0, Math.Max(0, RootCanvas.ActualHeight)));

        switch (_mode)
        {
            case Mode.Drawing: UpdateDrawPreview(p); break;
            case Mode.ResizingCorner: UpdateResize(p); break;
            case Mode.DraggingDivider: UpdateDividerDrag(p); break;
            case Mode.MovingBox: UpdateMove(p); break;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (RootCanvas.IsMouseCaptured) RootCanvas.ReleaseMouseCapture();
        var upPos = e.GetPosition(RootCanvas);
        switch (_mode)
        {
            case Mode.Drawing:
                if (upPos == _dragStart) // 真单击（鼠标一个像素都没动）：直接忽略，不弹操作条也不留半个框
                {
                    if (_drawPreview is not null) { RootCanvas.Children.Remove(_drawPreview); _drawPreview = null; }
                    break;
                }
                FinishDrawing();
                break;
            case Mode.MovingBox:
                _movingBox = null;
                break; // 平移已随 UpdateMove 实时提交，松开只需回到 Idle
            case Mode.ResizingCorner:
            case Mode.DraggingDivider:
                break; // 状态已随 Update* 实时提交，松开只需回到 Idle
        }
        _mode = Mode.Idle;
    }

    // ── 画框 ─────────────────────────────────────────────────────────────

    private void UpdateDrawPreview(Point p)
    {
        if (_drawPreview is null) return;
        double x = Math.Min(_dragStart.X, p.X), y = Math.Min(_dragStart.Y, p.Y);
        double w = Math.Abs(p.X - _dragStart.X), h = Math.Abs(p.Y - _dragStart.Y);
        Canvas.SetLeft(_drawPreview, x);
        Canvas.SetTop(_drawPreview, y);
        _drawPreview.Width = w;
        _drawPreview.Height = h;
    }

    private void FinishDrawing()
    {
        if (_drawPreview is null) return;
        double left = Canvas.GetLeft(_drawPreview), top = Canvas.GetTop(_drawPreview);
        double w = _drawPreview.Width, h = _drawPreview.Height;
        RootCanvas.Children.Remove(_drawPreview);
        _drawPreview = null;
        if (w * _dpiX < MinNewBoxPhysicalPx || h * _dpiY < MinNewBoxPhysicalPx)
        {
            // 太小（含纯单击）一律当误触：不弹操作条，也不留半个框。
            // 用物理像素判定而不是 DIP：用户说的"20 像素"是屏幕像素，且高 DPI 下 DIP 会把门槛放大。
            _log?.Debug($"[calibration] 框选 {w * _dpiX:F0}×{h * _dpiY:F0}px 小于 {MinNewBoxPhysicalPx}px，忽略");
            return;
        }
        ShowNewBoxPanel(new Rect(left, top, w, h));
    }

    /// <summary>
    /// 框选完成后的操作条。三件事：
    ///  1. 第二行显示**这次框的坐标/尺寸 + 当前场景 + 分辨率**（物理像素）——不显示的话用户无从知道自己框到了哪；
    ///  2. 摆放先量自己要多宽，放不下就翻到框的左侧，并夹在画布内 —— 修"贴右边缘时溢出窗口"；
    ///  3. 按住操作条空白处可拖走 —— 免得它压住正要对齐的区域。
    /// </summary>
    private void ShowNewBoxPanel(Rect rect)
    {
        var nameBox = new TextBox { Width = 160, Text = "新区域" };
        var okBtn = new Button { Content = "确定", Width = 50, Margin = new Thickness(8, 0, 4, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "取消", Width = 50, IsCancel = true };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 2) };
        row.Children.Add(new TextBlock { Text = "名称:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.White });
        row.Children.Add(nameBox);
        row.Children.Add(okBtn);
        row.Children.Add(cancelBtn);

        var info = new TextBlock
        {
            Text = DescribeRect(rect),
            Foreground = Brushes.LightGray,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 4),
        };
        var content = new StackPanel();
        content.Children.Add(row);
        content.Children.Add(info);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            Child = content,
        };

        // 拖拽：按住空白处（不是输入框/按钮/下拉）才能挪，否则会把控件的点击吃掉
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Source is TextBox or ComboBox or Button) return;
            _panelDragging = true;
            _panelDragStart = e.GetPosition(RootCanvas);
            _panelOriginStart = new Point(Canvas.GetLeft(border), Canvas.GetTop(border));
            border.CaptureMouse();
            e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (!_panelDragging) return;
            var p = e.GetPosition(RootCanvas);
            PlacePanel(border, _panelOriginStart.X + (p.X - _panelDragStart.X), _panelOriginStart.Y + (p.Y - _panelDragStart.Y));
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (!_panelDragging) return;
            _panelDragging = false;
            border.ReleaseMouseCapture();
            e.Handled = true;
        };

        // 操作条打开期间丢了焦点（点到了画布别处、Alt+Tab 切走）就响一声提醒：它还在等输入名称/类型。
        // 焦点事件沿视觉树冒泡，Border 上挂一个 LostFocus 就能接住内部所有控件的失焦；
        // 焦点在条内移动（输入框→下拉框）不算"丢"，不响。
        border.LostFocus += (_, _) =>
        {
            if (_newBoxPanel != border) return; // 面板已关闭（确定/取消），忽略
            var fe = Keyboard.FocusedElement;
            if (fe is DependencyObject feDo && IsWithinSubtree(feDo, border)) return;
            System.Media.SystemSounds.Beep.Play();
        };

        RootCanvas.Children.Add(border);
        _newBoxPanel = border;
        _pendingRect = rect;

        // 先量自身尺寸再定位：右侧放不下就翻到框左边，最后夹进画布，绝不溢出窗口
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double pw = border.DesiredSize.Width, ph = border.DesiredSize.Height;
        double left = rect.Right + 8;
        if (left + pw > RootCanvas.ActualWidth) left = Math.Max(0, rect.Left - pw - 8);
        double top = Math.Min(rect.Top, Math.Max(0, RootCanvas.ActualHeight - ph));
        PlacePanel(border, left, top);

        okBtn.Click += (_, _) =>
        {
            string name = string.IsNullOrWhiteSpace(nameBox.Text) ? "新区域" : nameBox.Text.Trim();
            var box = new CalibBox { Name = name, CanvasRect = _pendingRect!.Value };
            _boxes.Add(box);
            AddBoxVisual(box);
            ClosePendingPanel();
            SelectBox(box);
        };
        cancelBtn.Click += (_, _) => ClosePendingPanel();

        nameBox.Focus();
        nameBox.SelectAll();
    }

    /// <summary>把操作条摆到 (x,y) 并夹在画布内（左上角不越 0，右下不越画布）。</summary>
    private void PlacePanel(FrameworkElement panel, double x, double y)
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double maxX = Math.Max(0, RootCanvas.ActualWidth - panel.DesiredSize.Width);
        double maxY = Math.Max(0, RootCanvas.ActualHeight - panel.DesiredSize.Height);
        Canvas.SetLeft(panel, Math.Clamp(x, 0, maxX));
        Canvas.SetTop(panel, Math.Clamp(y, 0, maxY));
    }

    /// <summary>判断 el 是不是 root 本身或它的后代（沿视觉树逐级上溯）。</summary>
    private static bool IsWithinSubtree(DependencyObject el, DependencyObject root)
    {
        for (var d = el; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == root) return true;
        return false;
    }

    /// <summary>DIP 画布坐标 → 游戏窗口内的相对坐标（0~1），拼成"坐标/尺寸/场景/比例"一行。</summary>
    private string DescribeRect(Rect dip)
    {
        double rx = (dip.X * _dpiX - _gameRect.Left) / _gameRect.Width;
        double ry = (dip.Y * _dpiY - _gameRect.Top) / _gameRect.Height;
        double rw = (dip.Width * _dpiX) / _gameRect.Width;
        double rh = (dip.Height * _dpiY) / _gameRect.Height;
        return $"坐标 ({rx:F4}, {ry:F4})   尺寸 {rw:F4} × {rh:F4}   场景【{GameSceneIds.FromId(_sceneId).Label()}】   分辨率 {_frameW}×{_frameH}";
    }

    private void ClosePendingPanel()
    {
        if (_newBoxPanel is not null) { RootCanvas.Children.Remove(_newBoxPanel); _newBoxPanel = null; }
        _pendingRect = null;
        Focus();
    }

    // ── 已有框：选中/拖角调整/删除 ───────────────────────────────────────

    /// <summary>按创建顺序取下一个区域专属色（预设框与用户框共用，循环使用），保证屏上所有区域互不同色。</summary>
    private Brush NextColor() => RegionColors[_colorCounter++ % RegionColors.Length];

    private void AddBoxVisual(CalibBox box)
    {
        box.Color = NextColor();
        box.Visual = new Rectangle
        {
            Width = box.CanvasRect.Width,
            Height = box.CanvasRect.Height,
            Stroke = box.Color,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Cursor = Cursors.Hand,
        };
        // 点框体 = 选中 + 准备平移；拖走 = 整框平移（UpdateMove），点住不动 = 只选中
        box.Visual.MouseLeftButtonDown += (_, e) =>
        {
            SelectBox(box);
            StartMove(box, e.GetPosition(RootCanvas));
            e.Handled = true;
        };
        Canvas.SetLeft(box.Visual, box.CanvasRect.X);
        Canvas.SetTop(box.Visual, box.CanvasRect.Y);
        RootCanvas.Children.Add(box.Visual);

        box.Label = new TextBlock
        {
            Text = LabelFor(box),
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box.Label, box.CanvasRect.X + 2);
        Canvas.SetTop(box.Label, Math.Max(0, box.CanvasRect.Y - 18));
        RootCanvas.Children.Add(box.Label);
    }

    private void SelectBox(CalibBox box)
    {
        if (_selected == box) return;
        DeselectCurrent();
        _selected = box;
        box.Visual.Stroke = Brushes.Yellow;
        box.Visual.StrokeThickness = 3;
        CreateHandles(box);
    }

    /// <summary>开始整框平移：记下起点与原始矩形，随后鼠标移动由 <see cref="UpdateMove"/> 实时拖动。</summary>
    private void StartMove(CalibBox box, Point p)
    {
        _movingBox = box;
        _moveStart = p;
        _moveStartRect = box.CanvasRect;
        _mode = Mode.MovingBox;
        RootCanvas.CaptureMouse();
    }

    private void DeselectCurrent()
    {
        if (_selected is null) return;
        var box = _selected;
        box.Visual.Stroke = box.Color;
        box.Visual.StrokeThickness = 2;
        RemoveHandles(box);
        RemoveColumnUi(box);
        _selected = null;
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        var box = _selected;
        DeselectCurrent();
        RootCanvas.Children.Remove(box.Visual);
        RootCanvas.Children.Remove(box.Label);
        _boxes.Remove(box);
    }

    private void CreateHandles(CalibBox box)
    {
        box.Handles = new Rectangle[4];
        for (int i = 0; i < 4; i++)
        {
            int corner = i;
            var h = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.Yellow,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Cursor = corner is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW,
            };
            h.MouseLeftButtonDown += (_, e) =>
            {
                _mode = Mode.ResizingCorner;
                _resizeCorner = corner;
                _resizeStartRect = box.CanvasRect;
                RootCanvas.CaptureMouse();
                e.Handled = true;
            };
            box.Handles[i] = h;
            RootCanvas.Children.Add(h);
        }
        PositionHandles(box);
    }

    private void PositionHandles(CalibBox box)
    {
        if (box.Handles is null) return;
        var r = box.CanvasRect;
        var pts = new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom) };
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(box.Handles[i], pts[i].X - HandleSize / 2);
            Canvas.SetTop(box.Handles[i], pts[i].Y - HandleSize / 2);
        }
    }

    private void RemoveHandles(CalibBox box)
    {
        if (box.Handles is null) return;
        foreach (var h in box.Handles) RootCanvas.Children.Remove(h);
        box.Handles = null;
    }

    private void UpdateResize(Point p)
    {
        if (_selected is null) return;
        var r = _resizeStartRect;
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        switch (_resizeCorner)
        {
            case 0: left = p.X; top = p.Y; break;    // TL
            case 1: right = p.X; top = p.Y; break;   // TR
            case 2: left = p.X; bottom = p.Y; break; // BL
            case 3: right = p.X; bottom = p.Y; break; // BR
        }
        if (right - left < MinBoxSize)
        {
            if (_resizeCorner is 0 or 2) left = right - MinBoxSize; else right = left + MinBoxSize;
        }
        if (bottom - top < MinBoxSize)
        {
            if (_resizeCorner is 0 or 1) top = bottom - MinBoxSize; else bottom = top + MinBoxSize;
        }
        var newRect = new Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
        _selected.CanvasRect = newRect;
        ApplyBoxVisual(_selected);
        PositionHandles(_selected);
    }

    /// <summary>整框平移：按住框体拖动，整个框跟着走（夹在画布内）。与拖角调整不同——只挪位置不改尺寸。</summary>
    private void UpdateMove(Point p)
    {
        if (_movingBox is null) return;
        double dx = p.X - _moveStart.X, dy = p.Y - _moveStart.Y;
        double maxX = Math.Max(0, RootCanvas.ActualWidth - _moveStartRect.Width);
        double maxY = Math.Max(0, RootCanvas.ActualHeight - _moveStartRect.Height);
        _movingBox.CanvasRect = new Rect(
            Math.Clamp(_moveStartRect.X + dx, 0, maxX),
            Math.Clamp(_moveStartRect.Y + dy, 0, maxY),
            _moveStartRect.Width, _moveStartRect.Height);
        ApplyBoxVisual(_movingBox);
        PositionHandles(_movingBox);
    }

    private void ApplyBoxVisual(CalibBox box)
    {
        Canvas.SetLeft(box.Visual, box.CanvasRect.X);
        Canvas.SetTop(box.Visual, box.CanvasRect.Y);
        box.Visual.Width = box.CanvasRect.Width;
        box.Visual.Height = box.CanvasRect.Height;
        Canvas.SetLeft(box.Label, box.CanvasRect.X + 2);
        Canvas.SetTop(box.Label, Math.Max(0, box.CanvasRect.Y - 18));
        if (box.DividerLines.Count > 0) RedrawDividerLines(box);
    }

    /// <summary>预设框剥掉 %preset% 标记再显示（标记只用于路由，不给人看）。</summary>
    private static string LabelFor(CalibBox box)
        => box.IsPreset ? box.Name[LayoutPreset.PresetNamePrefix.Length..] : box.Name;


    // ── RoomList：竖线划列 ───────────────────────────────────────────────

    private void CreateColumnUi(CalibBox box)
    {
        for (int i = 0; i < box.Dividers.Count; i++) AddDividerLineVisual(box, i);
        RedrawDividerLines(box);
        BuildColumnPanel(box);
    }

    private void RemoveColumnUi(CalibBox box)
    {
        if (box.ColumnPanel is not null) { RootCanvas.Children.Remove(box.ColumnPanel); box.ColumnPanel = null; }
        foreach (var l in box.DividerLines) RootCanvas.Children.Remove(l);
        box.DividerLines.Clear();
    }

    private void AddDividerLineVisual(CalibBox box, int index)
    {
        var line = new Line { Stroke = Brushes.Orange, StrokeThickness = 3, Cursor = Cursors.SizeWE };
        line.MouseLeftButtonDown += (_, e) =>
        {
            _mode = Mode.DraggingDivider;
            _dividerIndex = box.DividerLines.IndexOf(line);
            RootCanvas.CaptureMouse();
            e.Handled = true;
        };
        box.DividerLines.Insert(index, line);
        RootCanvas.Children.Add(line);
    }

    private void RedrawDividerLines(CalibBox box)
    {
        for (int i = 0; i < box.Dividers.Count; i++)
        {
            double x = box.CanvasRect.Left + box.Dividers[i] * box.CanvasRect.Width;
            var line = box.DividerLines[i];
            line.X1 = x; line.Y1 = box.CanvasRect.Top;
            line.X2 = x; line.Y2 = box.CanvasRect.Bottom;
        }
    }

    private void UpdateDividerDrag(Point p)
    {
        if (_selected is null || _dividerIndex < 0 || _dividerIndex >= _selected.Dividers.Count) return;
        var box = _selected;
        double relX = box.CanvasRect.Width <= 0 ? 0 : (p.X - box.CanvasRect.Left) / box.CanvasRect.Width;
        const double minGap = 0.03;
        double lo = _dividerIndex == 0 ? 0.0 : box.Dividers[_dividerIndex - 1] + minGap;
        double hi = _dividerIndex == box.Dividers.Count - 1 ? 1.0 : box.Dividers[_dividerIndex + 1] - minGap;
        if (hi < lo) hi = lo;
        relX = Math.Clamp(relX, lo, hi);
        box.Dividers[_dividerIndex] = relX;
        RedrawDividerLines(box);
    }

    private void BuildColumnPanel(CalibBox box)
    {
        if (box.ColumnPanel is not null) { RootCanvas.Children.Remove(box.ColumnPanel); box.ColumnPanel = null; }

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        for (int i = 0; i < box.Kinds.Count; i++)
        {
            int ci = i;
            var combo = new ComboBox
            {
                Width = 84,
                Margin = new Thickness(2, 0, 2, 0),
                ItemsSource = ColumnKindOptions,
                DisplayMemberPath = "Label",
                SelectedValuePath = "Value",
                SelectedValue = box.Kinds[i],
            };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is ColumnKind k) box.Kinds[ci] = k; };
            sp.Children.Add(combo);
        }
        var addBtn = new Button { Content = "+分割线", Margin = new Thickness(6, 0, 2, 0) };
        addBtn.Click += (_, _) => AddDivider(box);
        var removeBtn = new Button { Content = "－分割线", Margin = new Thickness(2, 0, 0, 0), IsEnabled = box.Dividers.Count > 0 };
        removeBtn.Click += (_, _) => RemoveLastDivider(box);
        sp.Children.Add(addBtn);
        sp.Children.Add(removeBtn);

        box.ColumnPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
            BorderBrush = Brushes.Orange,
            BorderThickness = new Thickness(1),
            Child = sp,
        };
        RootCanvas.Children.Add(box.ColumnPanel);
        RepositionColumnUi(box);
    }

    private void RepositionColumnUi(CalibBox box)
    {
        if (box.ColumnPanel is null) return;
        Canvas.SetLeft(box.ColumnPanel, box.CanvasRect.Left);
        Canvas.SetTop(box.ColumnPanel, Math.Min(box.CanvasRect.Bottom + 4, Math.Max(0, RootCanvas.ActualHeight - 34)));
    }

    private void AddDivider(CalibBox box)
    {
        var bounds = new List<double> { 0.0 };
        bounds.AddRange(box.Dividers);
        bounds.Add(1.0);
        int widestIdx = 0;
        double widest = -1;
        for (int i = 0; i < bounds.Count - 1; i++)
        {
            double w = bounds[i + 1] - bounds[i];
            if (w > widest) { widest = w; widestIdx = i; }
        }
        if (widest < 0.08) return; // 太窄不再拆分

        double mid = (bounds[widestIdx] + bounds[widestIdx + 1]) / 2.0;
        box.Dividers.Insert(widestIdx, mid);
        box.Kinds.Insert(widestIdx + 1, box.Kinds[widestIdx]); // 新列先复制原 kind，用户可再改
        AddDividerLineVisual(box, widestIdx);
        RedrawDividerLines(box);
        BuildColumnPanel(box);
    }

    private void RemoveLastDivider(CalibBox box)
    {
        if (box.Dividers.Count == 0) return;
        int last = box.Dividers.Count - 1;
        box.Dividers.RemoveAt(last);
        box.Kinds.RemoveAt(last + 1);
        RootCanvas.Children.Remove(box.DividerLines[last]);
        box.DividerLines.RemoveAt(last);
        BuildColumnPanel(box);
    }

    // ── 键盘：Esc 取消 / Enter 保存 / Del 删除选中框 ────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_newBoxPanel is not null) return; // 面板自己的 IsDefault/IsCancel 按钮处理 Enter/Esc

        if (e.Key == Key.Escape)
        {
            Close(); // 取消：不保存
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            SaveAndClose();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _selected is not null)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    // ── 保存 ─────────────────────────────────────────────────────────────

    /// <summary>把框（画布相对坐标）换算成 rois.json 里的 ROI（游戏画面相对坐标）。gameRect 是游戏窗口的物理像素矩形。</summary>
    private static Roi BoxToRoi(CalibBox box, System.Drawing.Rectangle gameRect, double dpiX, double dpiY)
    {
        double physLeft = box.CanvasRect.Left * dpiX;
        double physTop = box.CanvasRect.Top * dpiY;
        double physW = box.CanvasRect.Width * dpiX;
        double physH = box.CanvasRect.Height * dpiY;

        var rect = new RelRect(
            (physLeft - gameRect.Left) / gameRect.Width,
            (physTop - gameRect.Top) / gameRect.Height,
            physW / gameRect.Width,
            physH / gameRect.Height);

        return new Roi(box.Name, rect);
    }

    private void SaveAndClose()
    {
        try
        {
            // 两文件分流，靠名字里的 %preset% 标记区分（不靠别的字段）：
            // 用户框 → rois.json 的单 rois 列表；预设框 → layout-overrides.json（更新对应 LayoutPreset 字段）。
            var presetBoxes = new List<CalibBox>();
            var userRois = new List<Roi>();
            foreach (var box in _boxes)
                if (box.IsPreset) presetBoxes.Add(box);
                else userRois.Add(BoxToRoi(box, _gameRect, _dpiX, _dpiY));

            // 相对坐标跨分辨率通用，不再按分辨率分档；直接覆盖该场景的 ROI 列表。
            string resKey = $"{_frameW}x{_frameH}";
            RoiConfig existing;
            try { existing = RoiStore.Load(_roisJsonPath); }
            catch { existing = new RoiConfig(new Dictionary<string, IReadOnlyList<Roi>>()); }

            var scenes = new Dictionary<string, IReadOnlyList<Roi>>(existing.Scenes);
            scenes[_sceneId] = userRois;

            RoiStore.Save(new RoiConfig(scenes), _roisJsonPath);

            // 预设框 → 直写出厂文件：以当前生效的预设为基线，逐个应用编辑后整条写回。
            var preset = _layout?.Find(_sceneId, _frameW, _frameH);
            if (preset is not null)
                foreach (var box in presetBoxes)
                    preset = ApplyPresetBox(preset, box, _gameRect, _dpiX, _dpiY);
            if (preset is not null && presetBoxes.Count > 0)
                LayoutPresetStore.Save(preset, _layout!.FilePath);

            _log?.Log($"[calibration] 保存 {userRois.Count + presetBoxes.Count} 个区域（用户 {userRois.Count}→rois.json，预设 {presetBoxes.Count}→layout-overrides.json）到场景[{GameSceneIds.FromId(_sceneId).Label()}]（{resKey}）@ {_roisJsonPath}");
            _onSaved?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Log($"[calibration] 保存失败: {ex.Message}");
            MessageBox.Show($"保存校准失败：{ex.Message}", "校准", MessageBoxButton.OK, MessageBoxImage.Error);
            return; // 不关窗口，允许重试或 Esc 退出
        }
        Close();
    }

    /// <summary>把一个预设框的编辑（位置/尺寸/RoomList 列）写回对应 LayoutPreset 字段，返回新预设（record 不可变）。
    /// 像素 = 画布 DIP → 物理像素 → 游戏窗口内相对像素，与 BoxToRoi 同一套换算。</summary>
    private static LayoutPreset ApplyPresetBox(LayoutPreset preset, CalibBox box, System.Drawing.Rectangle gameRect, double dpiX, double dpiY)
    {
        int px = (int)Math.Round(box.CanvasRect.Left * dpiX - gameRect.Left);
        int py = (int)Math.Round(box.CanvasRect.Top * dpiY - gameRect.Top);
        int pw = Math.Max(1, (int)Math.Round(box.CanvasRect.Width * dpiX));
        int ph = Math.Max(1, (int)Math.Round(box.CanvasRect.Height * dpiY));

        // 相对坐标 = 物理像素 / 游戏窗口物理尺寸，与 rois.json 同一口径（0~1 夹住）。
        var rel = new RelRect(
            Math.Clamp(px / (double)gameRect.Width, 0d, 1d),
            Math.Clamp(py / (double)gameRect.Height, 0d, 1d),
            Math.Clamp(pw / (double)gameRect.Width, 0d, 1d),
            Math.Clamp(ph / (double)gameRect.Height, 0d, 1d));

        // 靠名字路由到对应预设字段（预设框名字是 AddPresetBox 时定的）
        string label = box.Name[LayoutPreset.PresetNamePrefix.Length..];
        return label switch
        {
            "(预设)聊天区域" => preset with { Chat = rel },
            "(预设)房间名称区域" => preset with { GameName = rel },
            "(预设)地图名称区域" => preset with { MapName = rel },
            "(预设)房间名列表区域" => preset with { RoomNameList = rel },
            _ => preset,
        };
    }
}
