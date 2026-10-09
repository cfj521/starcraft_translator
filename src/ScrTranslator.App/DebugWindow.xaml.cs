using System;
using System.Windows;
using System.Windows.Threading;

namespace ScrTranslator.App;

/// <summary>
/// 独立的 DEBUG 诊断窗口：左上角约 10×8cm，置顶、不抢焦点(ShowActivated=False)、可交互(带"复制"按钮)。
/// 与点击穿透的悬浮层分开——悬浮层收不到鼠标点击，故复制按钮必须在这个非穿透窗口里。
/// 可见性由 Phase1Probe 的定时器按"游戏是否前台 + General.DebugMode"计算(用 Visibility，不 Show/Hide)。
/// </summary>
public partial class DebugWindow : Window
{
    private DispatcherTimer? _revertTimer;

    public DebugWindow()
    {
        InitializeComponent();
        // 10×8cm：WPF DIP 定义 1/96 英寸，cm/2.54*96 得逻辑尺寸，典型缩放下接近真实物理大小。
        Width = 10.0 / 2.54 * 96;   // ≈378 DIP
        Height = 8.0 / 2.54 * 96;   // ≈302 DIP
        Visibility = Visibility.Hidden;
    }

    /// <summary>刷新诊断文本（UI 线程调用）。文本超出面板高度会被裁掉。</summary>
    public void SetText(string text) => DebugText.Text = text;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DebugText.Text);
            CopyButton.Content = "已复制✓";
            // 1.2s 后还原按钮文案（一次性定时器）。
            _revertTimer?.Stop();
            _revertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _revertTimer.Tick += (_, _) => { _revertTimer!.Stop(); CopyButton.Content = "复制"; };
            _revertTimer.Start();
        }
        catch { CopyButton.Content = "复制失败"; }
    }
}
