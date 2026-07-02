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

namespace ScrTranslator.App.Calibration;

/// <summary>校准窗口：全屏半透明、可交互（非点击穿透）。拖框定义 ROI，RoomList 类型可划竖线分列。
/// F8 打开（App.xaml.cs）；Esc 取消不存、Enter 归一化写 rois.json（RoiStore.Save）。</summary>
public partial class CalibrationWindow : Window
{
    private const string Scene = "custom_games_listing";
    private const double HandleSize = 8;
    private const double MinBoxSize = 10; // DIP

    private readonly string _roisJsonPath;
    private readonly FileLogger? _log;
    private readonly Action? _onSaved;

    private readonly List<CalibBox> _boxes = new();
    private CalibBox? _selected;

    private enum Mode { Idle, Drawing, ResizingCorner, DraggingDivider }
    private Mode _mode = Mode.Idle;
    private Point _dragStart;
    private Rectangle? _drawPreview;
    private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
    private Rect _resizeStartRect;
    private int _dividerIndex;
    private Border? _newBoxPanel;
    private Rect? _pendingRect;

    private System.Drawing.Rectangle _gameRect; // 物理像素：归一化基准（同 Phase1Probe/ScreenMapper 的窗口矩形）
    private double _dpiX = 1, _dpiY = 1;

    private sealed record Option<T>(T Value, string Label);
    private static readonly Option<RoiType>[] RoiTypeOptions =
    {
        new(RoiType.Chat, "聊天"), new(RoiType.RoomList, "房间列表"), new(RoiType.MapInfo, "地图信息"),
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
    public CalibrationWindow(string roisJsonPath, FileLogger? log = null, Action? onSaved = null)
    {
        InitializeComponent();
        _roisJsonPath = roisJsonPath;
        _log = log;
        _onSaved = onSaved;
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

        DrawGameOutline();
        LoadExisting();

        Focusable = true;
        Focus();
        Keyboard.Focus(this);
    }

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
        SceneBucket? bucket = null;
        if (cfg.Scenes.TryGetValue(Scene, out var buckets))
            bucket = buckets.FirstOrDefault(b => b.Resolution == resKey);

        if (bucket is null)
        {
            HintText.Text = $"未找到当前分辨率({resKey})的校准数据 — 新建校准：拖动鼠标框选区域 (Esc 退出 / Enter 保存)";
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
        if (w < MinBoxSize || h < MinBoxSize) return; // 太小视为误触/单击，不弹面板
        ShowNewBoxPanel(new Rect(left, top, w, h));
    }

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
        var cancelBtn = new Button { Content = "取消", Width = 50, Margin = new Thickness(0, 0, 0, 0), IsCancel = true };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        content.Children.Add(new TextBlock { Text = "名称:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.White });
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock { Text = "类型:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Foreground = Brushes.White });
        content.Children.Add(typeCombo);
        content.Children.Add(okBtn);
        content.Children.Add(cancelBtn);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Child = content,
        };

        double left = Math.Min(rect.Right + 8, Math.Max(0, RootCanvas.ActualWidth - 360));
        double top = Math.Max(0, Math.Min(rect.Top, Math.Max(0, RootCanvas.ActualHeight - 60)));
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        RootCanvas.Children.Add(border);
        _newBoxPanel = border;
        _pendingRect = rect;

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

    private static string TypeLabel(RoiType t) => t switch
    {
        RoiType.Chat => "聊天",
        RoiType.RoomList => "房间列表",
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
            var buckets = scenes.TryGetValue(Scene, out var existingBuckets)
                ? existingBuckets.Where(b => b.Resolution != resKey).ToList()
                : new List<SceneBucket>();
            buckets.Add(newBucket);
            scenes[Scene] = buckets;

            RoiStore.Save(new RoiConfig(scenes), _roisJsonPath);
            _log?.Log($"[calibration] 保存 {rois.Count} 个区域到 {_roisJsonPath}（{resKey}）");
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
