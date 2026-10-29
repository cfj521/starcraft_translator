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

/// <summary>校准窗口：全屏半透明、可交互（非点击穿透）。把当前场景的布局预设建成可拖拽框，
/// 用户对着游戏画面直接调预设区域。Esc 取消不存、Enter 直写出厂 layout-presets.json。</summary>
public partial class CalibrationWindow : Window
{
    /// <summary>本次校准写入的场景键。**由校准窗自己抓一帧指纹判定**（OnLoaded 里做），
    /// 所以它不是 readonly：检测到真实场景后会覆盖初始默认值。</summary>
    private string _sceneId;
    private readonly SceneRecognizer? _scene;
    private readonly LayoutPresetStore? _layout;
    private const double HandleSize = 8;
    private const double MinBoxSize = 10;             // DIP，用于"拖角不许缩成线"

    private readonly FileLogger? _log;
    private readonly Action? _onSaved;

    private readonly List<CalibBox> _boxes = new();
    private CalibBox? _selected;
    private int _colorCounter; // 已分配色号：按创建顺序分配，保证屏上所有区域互不同色

    private enum Mode { Idle, ResizingCorner, MovingBox }
    private Mode _mode = Mode.Idle;
    private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
    private Rect _resizeStartRect;
    // 整框平移：按住框体（非角柄）拖动 = 平移整个框；点击不动 = 只选中
    private CalibBox? _movingBox;
    private Point _moveStart;
    private Rect _moveStartRect;

    private System.Drawing.Rectangle _gameRect; // 窗口物理像素矩形：绘制 + 相对坐标换算基准（同 TranslationEngine/ScreenMapper 的窗口矩形）
    private int _frameW, _frameH; // WGC 抓帧尺寸 = 游戏真实渲染分辨率：桶键 + 布局匹配 + 分辨率显示基准
    private double _dpiX = 1, _dpiY = 1;

    /// <summary>区域专属配色：每个区域一个**不同**颜色的边框，循环使用；无内填充，纯边框标识。</summary>
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

    /// <summary>一个预设区域的编辑态：Canvas 内 DIP 矩形。Name 存显示标签（=预设块名）。</summary>
    private sealed class CalibBox
    {
        public string Name = "(预设)区域";
        public Rect CanvasRect;

        public Rectangle Visual = null!;
        public TextBlock Label = null!;
        public Rectangle[]? Handles;
        public Brush Color = null!; // 区域专属边框色（NextColor 按创建顺序分配，保证屏上所有区域互不同色）
    }

    /// <param name="onSaved">保存成功（Enter 触发 SaveAndClose 走完写盘且未抛异常）后调用；
    /// 供 App.xaml.cs 接线让运行中的探测重载布局预设，改动无需重启即可生效。</param>
    /// <param name="scene">场景识别器（图像指纹）。为 null 时跳过检测，用默认场景。</param>
    /// <param name="layout">布局预设：校准窗把它们建成可交互编辑框，保存时直写出厂 layout-presets.json。</param>
    public CalibrationWindow(FileLogger? log = null, Action? onSaved = null,
        SceneRecognizer? scene = null, LayoutPresetStore? layout = null)
    {
        InitializeComponent();
        _log = log;
        _onSaved = onSaved;
        // 不用打开方的历史场景：默认"其他界面"（=未判定），窗口一创建就用 WGC 抓一帧自己判定（DetectScene），匹配才覆盖
        _sceneId = GameSceneIds.Other;
        _scene = scene;
        _layout = layout;
        // 铺满**虚拟桌面**（含副屏与负坐标屏）：游戏常开在副屏，只用 PrimaryScreen* 会让校准框/暗罩
        // 落不到游戏所在屏，用户看不到也点不到。物理→DIP 换算见 PhysicalToDipRect（会减掉本窗左上角）。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        // 场景由窗口创建后自己判定（WGC 窗口捕获，不受遮挡影响）——详见 DetectScene
        FindGameRect();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;

        DrawGameOutline();
        string sceneLabel = GameSceneIds.FromId(_sceneId).Label();
        HintText.Text = $"场景【{sceneLabel}】 · 分辨率 {_frameW}×{_frameH} · 正在判定场景…";

        // 先判定场景再画预设框：抓帧是异步的（最多 3 秒），同步 Wait 会把窗口冻住；
        // 但顺序不能反——框按 _sceneId 取预设，判定前画的会是"其他界面"的框（通常为空）。
        await DetectSceneAsync();
        if (_closed) return;
        DrawPresetBoxes();
        // 顶部常驻显示"正在校准哪个场景"——它决定 Enter 保存后这些框落到哪个场景，
        // 不显示的话用户根本不知道自己其实在调哪个场景的预设。
        sceneLabel = GameSceneIds.FromId(_sceneId).Label();
        HintText.Text = $"场景【{sceneLabel}】 · 分辨率 {_frameW}×{_frameH} · 拖动预设框调整 (Esc 退出 / Enter 保存)";

        Focusable = true;
        Focus();
        Keyboard.Focus(this);
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
    /// 全程 await（抓帧最多等 3 秒），不阻塞 UI 线程；关闭窗口后直接放弃。
    /// </summary>
    private async Task DetectSceneAsync()
    {
        var scene = _scene;
        if (scene is null) return;
        SKBitmap? frame = null;
        try
        {
            frame = await CaptureOneFrameWgcAsync();
            if (IsClosedOrDisposed) return;
            if (frame is null)
            {
                _log?.Log("[calibration] WGC 捕获不可用，场景未判定，保持其他界面");
                return; // _frameW/_frameH 保持 FindGameRect 的窗口兜底值
            }
            // WGC 帧尺寸 = 游戏真实渲染分辨率，覆盖窗口兜底值，作桶键 + 布局匹配基准
            _frameW = frame.Width;
            _frameH = frame.Height;
            var rec = scene.Recognize(frame.AsFramePixels()); // 零拷贝视图，只在 Recognize 调用内同步使用
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

    /// <summary>关闭后不再碰 UI（异步续体）。</summary>
    private bool IsClosedOrDisposed => _closed;

    private bool _closed;
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>WGC 窗口捕获：抓一帧游戏窗口的 D3D 渲染输出（复用探测的 CaptureService，抓完即弃）。
    /// 不受遮挡影响——直接拿窗口渲染输出，本窗盖不盖住屏幕都无所谓。
    /// 返回 null 的情况：找不到游戏窗口 / 系统不支持窗口捕获 / 3 秒内没等到帧
    /// （用户首次还没允许捕获时系统会弹同意框，等不到帧；同意过后下次即达）。</summary>
    private async Task<SKBitmap?> CaptureOneFrameWgcAsync()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) return null;
        var cap = new CaptureService();
        var tcs = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        cap.FrameArrived += sk => tcs.TrySetResult(sk);
        if (!cap.Start(hwnd)) { cap.Dispose(); return null; }
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (done != tcs.Task) return null;
            return await tcs.Task;
        }
        catch { return null; }
        finally { cap.Dispose(); }
    }

    /// <summary>把当前场景/分辨率的布局预设建成**可交互编辑框**（可选中/整框平移/拖角改大小），
    /// 改动随 Enter 直写出厂 layout-presets.json。目的：用户对着游戏画面直接拖调预设区域，不必去配置页改坐标。</summary>
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
    /// Name 存显示标签（=预设块名），保存时据此路由回对应 LayoutPreset 字段。</summary>
    private void AddPresetBox(string label, RelRect rect)
    {
        var dip = PhysicalToDipRect(new System.Drawing.Rectangle(
            _gameRect.Left + (int)Math.Round(rect.X * _gameRect.Width),
            _gameRect.Top + (int)Math.Round(rect.Y * _gameRect.Height),
            (int)Math.Round(rect.W * _gameRect.Width),
            (int)Math.Round(rect.H * _gameRect.Height)));
        var box = new CalibBox
        {
            Name = label,
            CanvasRect = dip,
        };
        _boxes.Add(box);
        AddBoxVisual(box);
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

    // ── 坐标换算（DIP canvas <-> 物理屏幕像素，基准=游戏窗口矩形，与 ScreenMapper/TranslationEngine 一致）──

    /// <summary>物理屏幕矩形 → 画布 DIP 矩形。画布原点在窗口左上角（窗口现铺满虚拟桌面，Left/Top 可能为负），
    /// 必须减掉窗口原点 DIP，否则副屏/负坐标屏上的游戏框会画到窗口外。</summary>
    private Rect PhysicalToDipRect(System.Drawing.Rectangle r)
        => new(r.Left / _dpiX - Left, r.Top / _dpiY - Top, r.Width / _dpiX, r.Height / _dpiY);

    // ── 鼠标：画布级状态机 ───────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点空画布 = 取消选中（点框体由框自己的 Visual 处理器接走：选中 + 平移）
        Focus();
        DeselectCurrent();
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
            case Mode.ResizingCorner: UpdateResize(p); break;
            case Mode.MovingBox: UpdateMove(p); break;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (RootCanvas.IsMouseCaptured) RootCanvas.ReleaseMouseCapture();
        switch (_mode)
        {
            case Mode.MovingBox:
                _movingBox = null;
                break; // 平移已随 UpdateMove 实时提交，松开只需回到 Idle
            case Mode.ResizingCorner:
                break; // 状态已随 Update* 实时提交，松开只需回到 Idle
        }
        _mode = Mode.Idle;
    }

    // ── 已有框：选中/拖角调整/整框平移 ───────────────────────────────────

    /// <summary>按创建顺序取下一个区域专属色（循环使用），保证屏上所有区域互不同色。</summary>
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
            Text = box.Name,
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
    }


    // ── 键盘：Esc 取消 / Enter 保存 / Del 删除选中框 ────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
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

    /// <summary>框（画布 DIP 坐标）→ 游戏画面相对矩形（0~1）。gameRect 是游戏窗口物理像素矩形。
    /// 纯换算（与 Core 测试共享同一逻辑）走 RelRect.FromCanvasDip。
    /// 画布原点是窗口左上角（窗口铺满虚拟桌面，Left/Top 可能为负），先加回窗口原点才是屏幕 DIP。</summary>
    private RelRect BoxToRelRect(CalibBox box, System.Drawing.Rectangle gameRect, double dpiX, double dpiY)
        => RelRect.FromCanvasDip(box.CanvasRect.Left + Left, box.CanvasRect.Top + Top,
            box.CanvasRect.Width, box.CanvasRect.Height,
            dpiX, dpiY, gameRect.Left, gameRect.Top, gameRect.Width, gameRect.Height);

    private void SaveAndClose()
    {
        try
        {
            // 全是预设框：以当前生效的预设为基线。先把四个 part 全置 null（被删的框不保留原值，
            // 置 null 持久化——与配置页"删除置 null"路径一致），再把现存的框逐个应用回去，整条直写出厂文件。
            var preset = _layout?.Find(_sceneId, _frameW, _frameH);
            if (preset is not null)
            {
                preset = preset with { Chat = null, RoomNameList = null, GameName = null, MapName = null };
                foreach (var box in _boxes)
                    preset = ApplyPresetBox(preset, box, _gameRect, _dpiX, _dpiY);
            }
            if (preset is not null)
                LayoutPresetStore.Save(preset, _layout!.FilePath);

            _log?.Log($"[calibration] 保存 {_boxes.Count} 个预设区域 → layout-presets.json 到场景[{GameSceneIds.FromId(_sceneId).Label()}]（{_frameW}x{_frameH}）");
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

    /// <summary>把一个预设框的编辑（位置/尺寸）写回对应 LayoutPreset 字段，返回新预设（record 不可变）。
    /// 换算同 BoxToRelRect（画布 DIP → 物理像素 → 相对坐标），再夹进 0~1。</summary>
    private LayoutPreset ApplyPresetBox(LayoutPreset preset, CalibBox box, System.Drawing.Rectangle gameRect, double dpiX, double dpiY)
    {
        var r = BoxToRelRect(box, gameRect, dpiX, dpiY);
        var rel = new RelRect(
            Math.Clamp(r.X, 0d, 1d),
            Math.Clamp(r.Y, 0d, 1d),
            Math.Clamp(r.W, 0d, 1d),
            Math.Clamp(r.H, 0d, 1d));

        // 靠名字路由到对应预设字段（预设框名字是 AddPresetBox 时定的）
        return box.Name switch
        {
            "(预设)聊天区域" => preset with { Chat = rel },
            "(预设)房间名称区域" => preset with { GameName = rel },
            "(预设)地图名称区域" => preset with { MapName = rel },
            "(预设)房间名列表区域" => preset with { RoomNameList = rel },
            _ => preset,
        };
    }
}
