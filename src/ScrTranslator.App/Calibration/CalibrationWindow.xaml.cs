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

namespace ScrTranslator.App.Calibration;

/// <summary>校准窗口：全屏半透明、可交互（非点击穿透）。拖框定义 ROI，RoomList 类型可划竖线分列。
/// F8 打开（App.xaml.cs）；Esc 取消不存、Enter 归一化写 rois.json（RoiStore.Save）。</summary>
public partial class CalibrationWindow : Window
{
    /// <summary>本次校准写入的场景键。**由校准窗自己检测得出**（打开方传的值只作兜底），
    /// 所以它不是 readonly：OnLoaded 里检测到真实场景后会覆盖。</summary>
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
    private readonly List<FrameworkElement> _presetDecorations = new(); // 预设参考框（只读，不写盘）

    private enum Mode { Idle, Drawing, ResizingCorner, DraggingDivider }
    private Mode _mode = Mode.Idle;
    private Point _dragStart;
    private Rectangle? _drawPreview;
    private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
    private Rect _resizeStartRect;
    private int _dividerIndex;
    private Border? _newBoxPanel;
    private Rect? _pendingRect;
    // 操作条拖拽：按住它自己（非控件区）可以挪动，避免挡住正在对齐的区域
    private bool _panelDragging;
    private Point _panelDragStart;
    private Point _panelOriginStart;

    private System.Drawing.Rectangle _gameRect; // 物理像素：归一化基准（同 Phase1Probe/ScreenMapper 的窗口矩形）
    private double _dpiX = 1, _dpiY = 1;

    private sealed record Option<T>(T Value, string Label);
    /// <summary>ROI 类型选项。显示名用"房间名列表"，与**场景名**"寻找房间"区分开（两者不是一回事：
    /// 场景=当前是哪个界面，ROI=该界面里要框出来翻的一块区域）。</summary>
    private static readonly Option<RoiType>[] RoiTypeOptions =
    {
        new(RoiType.Chat, "聊天"), new(RoiType.RoomList, "房间名列表"), new(RoiType.MapInfo, "地图信息"),
    };
    private static readonly Option<ColumnKind>[] ColumnKindOptions =
    {
        new(ColumnKind.RoomName, "房间名"), new(ColumnKind.MapName, "地图"),
        new(ColumnKind.PlayerId, "玩家ID"), new(ColumnKind.Other, "其它"),
    };

    /// <summary>一个已框选区域的编辑态：Canvas 内 DIP 矩形 + 可选列定义（仅 RoomList 用）。</summary>
    private sealed class CalibBox
    {
        public string Name = "新区域";
        public RoiType Type = RoiType.Chat;
        public Rect CanvasRect;
        public readonly List<double> Dividers = new(); // 相对 ROI 宽度 0..1，已排序
        public List<ColumnKind> Kinds = new() { ColumnKind.RoomName }; // Dividers.Count+1 个

        public Rectangle Visual = null!;
        public TextBlock Label = null!;
        public Rectangle[]? Handles;
        public readonly List<Line> DividerLines = new();
        public Border? ColumnPanel;
    }

    /// <param name="onSaved">保存成功（Enter 触发 SaveAndClose 走完写盘且未抛异常）后调用；
    /// 供 App.xaml.cs 接线让运行中的 Phase1Probe.ReloadRois()，使首次校准/改动无需重启即可生效。</param>
    /// <param name="sceneId">打开方认为的当前场景，仅作**兜底**：校准窗自己会抓一帧做指纹检测覆盖它，
    /// 所以 F9 没开也能定准场景。</param>
    /// <param name="scene">场景识别器（图像指纹）。为 null 时跳过检测，沿用传入的 sceneId。</param>
    /// <param name="layout">布局预设：校准窗把它们画成只读参考框，省得用户重复框已有的区域。</param>
    public CalibrationWindow(string roisJsonPath, FileLogger? log = null, Action? onSaved = null, string? sceneId = null,
        SceneRecognizer? scene = null, LayoutPresetStore? layout = null)
    {
        InitializeComponent();
        _roisJsonPath = roisJsonPath;
        _log = log;
        _onSaved = onSaved;
        _sceneId = sceneId ?? GameSceneIds.Browsing;
        _scene = scene;
        _layout = layout;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;

        var hwnd = WindowFinder.FindByProcess("StarCraft");
        var rect = hwnd == IntPtr.Zero ? System.Drawing.Rectangle.Empty : NativeMethods.GetWindowScreenRect(hwnd);
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            _log?.Log("[calibration] 未找到 StarCraft 窗口，退化为使用主屏幕范围作为校准基准");
            rect = new System.Drawing.Rectangle(0, 0,
                (int)Math.Round(SystemParameters.PrimaryScreenWidth * _dpiX),
                (int)Math.Round(SystemParameters.PrimaryScreenHeight * _dpiY));
        }
        _gameRect = rect;

        // 场景由校准窗自己检测（不依赖 F9 是否开着），检测不到才沿用打开方传进来的值
        DetectSceneOrKeep();

        DrawGameOutline();
        DrawPresetBoxes();
        LoadExisting();

        Focusable = true;
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>
    /// 抓游戏窗口那块屏幕做指纹场景检测。
    ///
    /// 用 CopyFromScreen 而不是 PrintWindow：本窗打开的前提就是"游戏在前台"（App.xaml.cs 已挡），
    /// 所以那块像素就是游戏画面本身，所见即所测，还省掉一套 GDI 互操作。
    /// 这样校准**不依赖 F9 是否开启**——没开翻译也能定准当前场景，框的区域才会落到对的场景桶里。
    /// </summary>
    private void DetectSceneOrKeep()
    {
        if (_scene is null) return;
        try
        {
            using var bmp = new System.Drawing.Bitmap(_gameRect.Width, _gameRect.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int stride;
            byte[] buf;
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(_gameRect.Left, _gameRect.Top, 0, 0, bmp.Size);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            stride = Math.Abs(data.Stride);
            buf = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(data);

            var rec = _scene.Recognize(new FramePixels(buf, bmp.Width, bmp.Height, stride, 4, 2, 1, 0));
            _log?.Log($"[calibration] 场景检测：{rec.Summary}");
            if (rec.IsMatched) _sceneId = rec.Scene.ToId();
        }
        catch (Exception ex)
        {
            _log?.Log($"[calibration] 场景检测失败，沿用传入场景 {_sceneId}: {ex.Message}");
        }
    }

    /// <summary>把当前场景/分辨率的布局预设画成**只读参考框**（虚线 + 标签）。
    /// 目的：让用户一眼看见"这块系统已经知道了"，不必重复框；它们不参与拖拽也不写盘。</summary>
    private void DrawPresetBoxes()
    {
        var p = _layout?.Find(_sceneId, _gameRect.Width, _gameRect.Height);
        if (p is null) return;
        var parts = new List<(PixelRect Rect, string Label)>();
        if (p.Chat is { } c) parts.Add((c, "聊天(预设)"));
        if (p.RoomList is { } g)
        {
            parts.Add((new PixelRect(g.X, g.Y, g.Width, g.Height), "房间列表(预设)"));
            foreach (var col in g.Columns)
                parts.Add((new PixelRect(g.X + col.X, g.Y, col.Width, g.Height), $"列·{ColumnKindLabel(col.Kind)}"));
        }
        foreach (var (r, label) in parts)
        {
            var dip = PhysicalToDipRect(new System.Drawing.Rectangle(_gameRect.Left + r.X, _gameRect.Top + r.Y, r.Width, r.Height));
            var box = new Rectangle
            {
                Width = Math.Max(1, dip.Width),
                Height = Math.Max(1, dip.Height),
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(28, 0, 191, 255)),
                IsHitTestVisible = false,   // 参考框不吃鼠标，别挡住下面的框选
            };
            Canvas.SetLeft(box, dip.X);
            Canvas.SetTop(box, dip.Y);
            var tag = new TextBlock
            {
                Text = label,
                Foreground = Brushes.DeepSkyBlue,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(3, 0, 3, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tag, dip.X);
            Canvas.SetTop(tag, Math.Max(0, dip.Y - 16));
            RootCanvas.Children.Add(box);
            RootCanvas.Children.Add(tag);
            _presetDecorations.Add(box);
            _presetDecorations.Add(tag);
        }
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
            cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>());
        }

        string resKey = $"{_gameRect.Width}x{_gameRect.Height}";
        string sceneLabel = GameSceneIds.FromId(_sceneId).Label();
        // 顶部常驻显示"正在校准哪个场景/分辨率"——它决定 Enter 保存后这些框落到哪个桶，
        // 不显示的话用户根本不知道自己其实是在给"游戏内"画框。
        HintText.Text = $"场景【{sceneLabel}】 · 分辨率 {resKey} · 拖动鼠标框选 (Esc 退出 / Enter 保存)";
        SceneBucket? bucket = null;
        if (cfg.Scenes.TryGetValue(_sceneId, out var buckets))
            bucket = buckets.FirstOrDefault(b => b.Resolution == resKey);

        if (bucket is null)
        {
            HintText.Text += " — 该场景在此分辨率下还没有校准区域";
            return;
        }

        foreach (var roi in bucket.Rois)
        {
            var px = new System.Drawing.Rectangle(
                _gameRect.Left + (int)Math.Round(roi.Rect.X * _gameRect.Width),
                _gameRect.Top + (int)Math.Round(roi.Rect.Y * _gameRect.Height),
                (int)Math.Round(roi.Rect.W * _gameRect.Width),
                (int)Math.Round(roi.Rect.H * _gameRect.Height));
            var box = new CalibBox
            {
                Name = roi.Name,
                Type = RoiTypes.Parse(roi.Type),
                CanvasRect = PhysicalToDipRect(px),
            };
            if (roi.Columns is { Count: > 0 })
            {
                box.Dividers.Clear();
                box.Dividers.AddRange(roi.Columns.Skip(1).Select(c => c.RelStart));
                box.Kinds = roi.Columns.Select(c => c.Kind).ToList();
            }
            AddBoxVisual(box);
            _boxes.Add(box);
        }
    }

    // ── 坐标换算（DIP canvas <-> 物理屏幕像素，基准=游戏窗口矩形，与 ScreenMapper/Phase1Probe 一致）──

    private Rect PhysicalToDipRect(System.Drawing.Rectangle r)
        => new(r.Left / _dpiX, r.Top / _dpiY, r.Width / _dpiX, r.Height / _dpiY);

    // ── 鼠标：画布级状态机 ───────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_newBoxPanel is not null) return; // 新建面板打开时，画布点击不生效
        Focus();
        DeselectCurrent();

        var p = e.GetPosition(RootCanvas);
        _mode = Mode.Drawing;
        _dragStart = p;
        _drawPreview = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
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
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (RootCanvas.IsMouseCaptured) RootCanvas.ReleaseMouseCapture();
        switch (_mode)
        {
            case Mode.Drawing: FinishDrawing(); break;
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
        var typeCombo = new ComboBox
        {
            Width = 100,
            ItemsSource = RoiTypeOptions,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Value",
            SelectedIndex = 0,
        };
        var okBtn = new Button { Content = "确定", Width = 50, Margin = new Thickness(8, 0, 4, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "取消", Width = 50, IsCancel = true };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 2) };
        row.Children.Add(new TextBlock { Text = "名称:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.White });
        row.Children.Add(nameBox);
        row.Children.Add(new TextBlock { Text = "类型:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Foreground = Brushes.White });
        row.Children.Add(typeCombo);
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
            var type = typeCombo.SelectedValue is RoiType t ? t : RoiType.Chat;
            var box = new CalibBox { Name = name, Type = type, CanvasRect = _pendingRect!.Value };
            AddBoxVisual(box);
            _boxes.Add(box);
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

    /// <summary>DIP 画布坐标 → 游戏窗口内的物理像素，拼成"坐标/尺寸/场景/分辨率"一行。</summary>
    private string DescribeRect(Rect dip)
    {
        int px = (int)Math.Round(dip.X * _dpiX) - _gameRect.Left;
        int py = (int)Math.Round(dip.Y * _dpiY) - _gameRect.Top;
        int pw = (int)Math.Round(dip.Width * _dpiX);
        int ph = (int)Math.Round(dip.Height * _dpiY);
        return $"坐标 ({px}, {py})   尺寸 {pw} × {ph}   场景【{GameSceneIds.FromId(_sceneId).Label()}】   分辨率 {_gameRect.Width}×{_gameRect.Height}";
    }

    private void ClosePendingPanel()
    {
        if (_newBoxPanel is not null) { RootCanvas.Children.Remove(_newBoxPanel); _newBoxPanel = null; }
        _pendingRect = null;
        Focus();
    }

    // ── 已有框：选中/拖角调整/删除 ───────────────────────────────────────

    private void AddBoxVisual(CalibBox box)
    {
        box.Visual = new Rectangle
        {
            Width = box.CanvasRect.Width,
            Height = box.CanvasRect.Height,
            Stroke = TypeColor(box.Type),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Cursor = Cursors.Hand,
        };
        box.Visual.MouseLeftButtonDown += (_, e) => { SelectBox(box); e.Handled = true; };
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
        if (box.Type == RoiType.RoomList) CreateColumnUi(box);
    }

    private void DeselectCurrent()
    {
        if (_selected is null) return;
        var box = _selected;
        box.Visual.Stroke = TypeColor(box.Type);
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
        if (_selected.Type == RoiType.RoomList) RepositionColumnUi(_selected);
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

    private static string LabelFor(CalibBox box) => $"{box.Name} [{TypeLabel(box.Type)}]";

    private static Brush TypeColor(RoiType t) => t switch
    {
        RoiType.Chat => Brushes.DeepSkyBlue,
        RoiType.RoomList => Brushes.Orange,
        RoiType.MapInfo => Brushes.YellowGreen,
        _ => Brushes.White,
    };

    /// <summary>已框区域的类型标签（与 <see cref="RoiTypeOptions"/> 的显示名保持一致）。</summary>
    private static string TypeLabel(RoiType t) => t switch
    {
        RoiType.Chat => "聊天",
        RoiType.RoomList => "房间名列表",
        RoiType.MapInfo => "地图信息",
        _ => "?",
    };

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

    private void SaveAndClose()
    {
        try
        {
            var rois = new List<Roi>();
            foreach (var box in _boxes)
            {
                double physLeft = box.CanvasRect.Left * _dpiX;
                double physTop = box.CanvasRect.Top * _dpiY;
                double physW = box.CanvasRect.Width * _dpiX;
                double physH = box.CanvasRect.Height * _dpiY;

                var rect = new RelRect(
                    (physLeft - _gameRect.Left) / _gameRect.Width,
                    (physTop - _gameRect.Top) / _gameRect.Height,
                    physW / _gameRect.Width,
                    physH / _gameRect.Height);

                IReadOnlyList<RoiColumn>? columns = null;
                if (box.Type == RoiType.RoomList)
                {
                    var bounds = new List<double> { 0.0 };
                    bounds.AddRange(box.Dividers);
                    bounds.Add(1.0);
                    var cols = new List<RoiColumn>();
                    for (int i = 0; i < bounds.Count - 1; i++)
                        cols.Add(new RoiColumn(bounds[i], bounds[i + 1], box.Kinds[i]));
                    columns = cols;
                }
                rois.Add(new Roi(box.Name, box.Type.ToString(), rect, columns));
            }

            string resKey = $"{_gameRect.Width}x{_gameRect.Height}";
            var newBucket = new SceneBucket(resKey, rois);

            RoiConfig existing;
            try { existing = RoiStore.Load(_roisJsonPath); }
            catch { existing = new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>()); }

            var scenes = new Dictionary<string, IReadOnlyList<SceneBucket>>(existing.Scenes);
            var buckets = scenes.TryGetValue(_sceneId, out var existingBuckets)
                ? existingBuckets.Where(b => b.Resolution != resKey).ToList()
                : new List<SceneBucket>();
            buckets.Add(newBucket);
            scenes[_sceneId] = buckets;

            RoiStore.Save(new RoiConfig(scenes), _roisJsonPath);
            _log?.Log($"[calibration] 保存 {rois.Count} 个区域到场景[{GameSceneIds.FromId(_sceneId).Label()}]（{resKey}）@ {_roisJsonPath}");
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
}
