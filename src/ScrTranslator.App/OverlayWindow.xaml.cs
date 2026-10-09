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
public record OverlayItem(string Text, double ScreenLeft, double ScreenTop, string? Source = null);

public partial class OverlayWindow : Window
{
    private readonly SettingsHolder _settings;
    /// <summary>用户是否要显示悬浮层（F10/托盘切换的意图）。实际可见性由探测按"游戏是否前台"叠加计算。</summary>
    public bool EnabledByUser { get; set; } = true;

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
    /// 字号/字体/不透明度/底衬/显示模式读自 Appearance（配置页热改，靠 App.xaml.cs 在应用设置后调用
    /// ResetChangeDetection 触发重译上屏）。ShowOriginalAndTranslation=true 时每行=原文(小字灰)在上、译文在下的双行块。
    /// 每条译文带一块圆角黑底（<see cref="AppearanceSettings.BackplateAlpha"/>），否则白字压在浅色游戏画面上读不出来。</summary>
    public void ShowLines(IReadOnlyList<OverlayItem> items)
    {
        RootCanvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        var appearance = _settings.Current.Appearance;
        FontFamily family = ResolveFontFamily(appearance.FontFamily);
        double plateAlpha = appearance.BackplateAlpha();
        foreach (var it in items)
        {
            // 文字自身的 Opacity 只表示"相对整条的深浅"，用户设的整体不透明度统一加在外层底衬上，
            // 避免两者叠乘把文字压得过淡。
            var tb = new TextBlock
            {
                Text = it.Text,
                FontFamily = family,
                FontSize = appearance.FontSize,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
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
                    Opacity = 0.8,
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
                };
                visual = new StackPanel { Children = { source, tb } };
            }
            // 底衬**逐条**一块，而不是整窗铺一层：整窗铺会把游戏画面糊掉一大片，
            // 而翻译文本只占屏幕上的几个小点——只给有字的地方垫底，其余保持完全透明。
            if (plateAlpha > 0)
                visual = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb((byte)(plateAlpha * 255), 0, 0, 0)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 2, 5, 3),
                    Child = visual,
                };
            visual.Opacity = appearance.Opacity;
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
