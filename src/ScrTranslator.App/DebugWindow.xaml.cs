using System;
using System.Windows;
using System.Windows.Interop;
using ScrTranslator.App.Interop;

namespace ScrTranslator.App;

/// <summary>
/// 独立的 DEBUG 诊断窗口：左上角约 9×7cm，直角，置顶，纯信息展示。
/// 永久点击穿透（IsHitTestVisible=False + WS_EX_TRANSPARENT）→ 不接收鼠标、绝不抢游戏焦点。
/// 复制改由全局热键 Ctrl+F6 完成（App 拷 probe 的诊断快照到剪贴板），故本窗无需任何可交互控件。
/// 可见性由 Phase1Probe 的定时器按"游戏是否前台 + General.DebugMode"计算（用 Visibility，不 Show/Hide）。
/// </summary>
public partial class DebugWindow : Window
{
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
        // 永久穿透：加 WS_EX_TRANSPARENT|WS_EX_LAYERED，鼠标事件全部落到下层游戏。
        NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, on: true);
    }

    /// <summary>刷新诊断文本（UI 线程调用）。文本超出面板高度会被裁掉。</summary>
    public void SetText(string text) => DebugText.Text = text;
}
