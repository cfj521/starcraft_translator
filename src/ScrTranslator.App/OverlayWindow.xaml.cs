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

/// <summary>一条上屏文本：物理屏幕像素坐标（左上角）。</summary>
public record OverlayItem(string Text, double ScreenLeft, double ScreenTop, bool Provisional = false);

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
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableClickThrough(hwnd);
    }

    /// <summary>清屏并按物理屏幕坐标（内部换算为 DIP）摆放文本。必须在 UI 线程调用。
    /// 字号/不透明度读自 Appearance（配置页热改，靠 App.xaml.cs 在应用设置后调用 ResetChangeDetection 触发重译上屏）。</summary>
    public void ShowLines(IReadOnlyList<OverlayItem> items)
    {
        RootCanvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        var appearance = _settings.Current.Appearance;
        foreach (var it in items)
        {
            var tb = new TextBlock
            {
                Text = it.Text,
                FontSize = appearance.FontSize,
                FontWeight = FontWeights.Bold,
                Foreground = it.Provisional ? new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) : Brushes.White,
                FontStyle = it.Provisional ? FontStyles.Italic : FontStyles.Normal,
                Opacity = it.Provisional ? appearance.Opacity * 0.65 : appearance.Opacity,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
            };
            Canvas.SetLeft(tb, it.ScreenLeft / dpi.DpiScaleX);
            Canvas.SetTop(tb, it.ScreenTop / dpi.DpiScaleY);
            RootCanvas.Children.Add(tb);
        }
    }
}
