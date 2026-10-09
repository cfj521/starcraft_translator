using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ScrTranslator.App.Interop;
using ScrTranslator.Core.Translation;

namespace ScrTranslator.App;

/// <summary>一条上屏文本：物理屏幕像素坐标（左上角）。Source=对应韩文原文，供"原文+译文双行"显示（可空）。</summary>
public record OverlayItem(string Text, double ScreenLeft, double ScreenTop, bool Provisional = false, string? Source = null);

public partial class OverlayWindow : Window
{
    private readonly SettingsHolder _settings;

    public OverlayWindow(SettingsHolder settings)
    {
        InitializeComponent();
        _settings = settings;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;   // DIP
        Height = SystemParameters.PrimaryScreenHeight; // DIP
        // 调试面板 ≈ 8×5cm：WPF DIP 定义为 1/96 英寸，故 cm/2.54*96 得逻辑尺寸，在典型缩放下接近真实物理大小。
        DebugPanel.Width = 8.0 / 2.54 * 96;   // ≈302 DIP
        DebugPanel.Height = 5.0 / 2.54 * 96;  // ≈189 DIP
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableClickThrough(hwnd);
    }

    /// <summary>显示/隐藏左上角调试面板（UI 线程调用）。</summary>
    public void SetDebugVisible(bool on) =>
        DebugPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>刷新调试面板文本（UI 线程调用）。面板固定尺寸，文本超出行会被裁掉。</summary>
    public void UpdateDebug(string text) => DebugText.Text = text;

    /// <summary>清屏并按物理屏幕坐标（内部换算为 DIP）摆放文本。必须在 UI 线程调用。
    /// 字号/字体/不透明度/显示模式读自 Appearance（配置页热改，靠 App.xaml.cs 在应用设置后调用 ResetChangeDetection 触发重译上屏）。
    /// ShowOriginalAndTranslation=true 时每行=原文(小字灰)在上、译文在下的双行块。</summary>
    public void ShowLines(IReadOnlyList<OverlayItem> items)
    {
        RootCanvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        var appearance = _settings.Current.Appearance;
        FontFamily family = ResolveFontFamily(appearance.FontFamily);
        foreach (var it in items)
        {
            var tb = new TextBlock
            {
                Text = it.Text,
                FontFamily = family,
                FontSize = appearance.FontSize,
                FontWeight = FontWeights.Bold,
                Foreground = it.Provisional ? new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) : Brushes.White,
                FontStyle = it.Provisional ? FontStyles.Italic : FontStyles.Normal,
                Opacity = it.Provisional ? appearance.Opacity * 0.65 : appearance.Opacity,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
            };
            UIElement visual = tb;
            if (appearance.ShowOriginalAndTranslation && !string.IsNullOrWhiteSpace(it.Source))
            {
                // 双行块：原文小字(0.72x,灰)在上。StackPanel 整体定位，译文相对块顶下移一行原文的高度。
                var source = new TextBlock
                {
                    Text = it.Source,
                    FontFamily = family,
                    FontSize = appearance.FontSize * 0.72,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
                    Opacity = appearance.Opacity * 0.8,
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
                };
                visual = new StackPanel { Children = { source, tb } };
            }
            Canvas.SetLeft(visual, it.ScreenLeft / dpi.DpiScaleX);
            Canvas.SetTop(visual, it.ScreenTop / dpi.DpiScaleY);
            RootCanvas.Children.Add(visual);
        }
    }

    /// <summary>字体名解析：空/非法一律回落微软雅黑（WPF 对未知 FamilyName 不抛错但渲染回落系统默认，宁可显式兜底）。</summary>
    private static FontFamily ResolveFontFamily(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new FontFamily("Microsoft YaHei");
        try
        {
            var f = new FontFamily(name);
            return f.FamilyNames.Count > 0 || !name.Contains(' ') ? f : new FontFamily("Microsoft YaHei");
        }
        catch { return new FontFamily("Microsoft YaHei"); }
    }
}
