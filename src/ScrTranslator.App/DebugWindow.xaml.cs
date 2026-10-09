using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScrTranslator.App.Interop;

namespace ScrTranslator.App;

/// <summary>
/// 独立的 DEBUG 诊断窗口：左上角约 9×7cm，置顶、直角、不抢焦点(ShowActivated=False)。
/// 默认点击穿透（不挡游戏）；当鼠标移到面板矩形内时临时取消穿透，好让"复制"按钮可点，移开即恢复穿透。
/// 可见性由 Phase1Probe 的定时器按"游戏是否前台 + General.DebugMode"计算(用 Visibility，不 Show/Hide)。
/// </summary>
public partial class DebugWindow : Window
{
    private DispatcherTimer? _revertTimer;
    private DispatcherTimer? _hitTimer;
    private IntPtr _hwnd;
    private bool _clickThrough = true;

    public DebugWindow()
    {
        InitializeComponent();
        // 9×7cm：WPF DIP 定义 1/96 英寸，cm/2.54*96 得逻辑尺寸。
        Width = 9.0 / 2.54 * 96;   // ≈340 DIP
        Height = 7.0 / 2.54 * 96;  // ≈265 DIP
        Visibility = Visibility.Hidden;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetClickThrough(_hwnd, true); // 默认穿透
        _clickThrough = true;
        // 轮询光标位置：进入面板→可交互；离开→恢复穿透。120ms 足够跟手又不耗资源。
        _hitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hitTimer.Tick += (_, _) => UpdateClickThrough();
        _hitTimer.Start();
    }

    private void UpdateClickThrough()
    {
        if (Visibility != Visibility.Visible) return;
        bool over = false;
        if (NativeMethods.GetCursorPos(out var p))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // 窗口在屏幕左上角(Left/Top=0)，物理像素矩形 = DIP × DpiScale。
            double leftPx = Left * dpi.DpiScaleX, topPx = Top * dpi.DpiScaleY;
            double rightPx = leftPx + Width * dpi.DpiScaleX, bottomPx = topPx + Height * dpi.DpiScaleY;
            over = p.X >= leftPx && p.X < rightPx && p.Y >= topPx && p.Y < bottomPx;
        }
        bool wantThrough = !over;
        if (wantThrough != _clickThrough)
        {
            NativeMethods.SetClickThrough(_hwnd, wantThrough);
            _clickThrough = wantThrough;
        }
    }

    /// <summary>刷新诊断文本（UI 线程调用）。文本超出面板高度会被裁掉。</summary>
    public void SetText(string text) => DebugText.Text = text;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DebugText.Text);
            CopyButton.Content = "已复制✓";
            _revertTimer?.Stop();
            _revertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _revertTimer.Tick += (_, _) => { _revertTimer!.Stop(); CopyButton.Content = "复制"; };
            _revertTimer.Start();
        }
        catch { CopyButton.Content = "复制失败"; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hitTimer?.Stop(); _revertTimer?.Stop();
        base.OnClosed(e);
    }
}
