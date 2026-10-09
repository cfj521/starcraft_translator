using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ScrTranslator.App.Config;

/// <summary>
/// 编辑一条出厂布局预设的矩形（聊天框 / 房间列表框）。
///
/// 预设文件 layout-presets.json 是量测产物、程序不改写；这里的修改落到
/// %APPDATA%\ScrTranslator\layout-overrides.json，同 (场景,分辨率) 覆盖出厂值，删掉即可恢复出厂。
/// 界面只谈像素，不谈百分比。
///
/// 用纯代码搭 UI 而不是 XAML：这种四五个框的小对话框，一份 XAML + 一份 cs 的配对成本不值当，
/// 而且两个对话框共用同一套行布局辅助方法。
/// </summary>
public sealed class PresetEditWindow : Window
{
    private readonly bool _hasRowHeight;
    private readonly TextBox _x = Row("X"), _y = Row("Y"), _w = Row("宽"), _h = Row("高");
    private readonly TextBox _rowHeight = Row("行高");
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Red, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>编辑后的像素矩形。</summary>
    public (int X, int Y, int W, int H) Rect { get; private set; }

    /// <summary>行高（仅房间列表框有；其余为 null）。</summary>
    public int? RowHeight { get; private set; }

    public PresetEditWindow(string sceneLabel, string resolution, string regionName,
        (int X, int Y, int W, int H) rect, int? rowHeight)
    {
        Title = "编辑预设区域";
        Width = 340; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _hasRowHeight = rowHeight is not null;
        _x.Text = Str(rect.X); _y.Text = Str(rect.Y); _w.Text = Str(rect.W); _h.Text = Str(rect.H);
        Rect = rect;

        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(new TextBlock
        {
            Text = $"{sceneLabel} · {resolution} · {regionName}（整数像素）",
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8),
        });
        body.Children.Add(Pair("左上角", _x, _y));
        body.Children.Add(Pair("宽 / 高", _w, _h));
        if (_hasRowHeight)
        {
            _rowHeight.Text = Str(rowHeight!.Value);
            body.Children.Add(Labeled("行高", _rowHeight));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_error);
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(12, 0, 8, 0), IsDefault = true };
        ok.Click += OnOk;
        var cancel = new Button { Content = "取消", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        Content = body;
    }

    static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);
    static TextBox Row(string _) => new() { Width = 80, Margin = new Thickness(0, 0, 12, 0) };

    static FrameworkElement Labeled(string label, TextBox box)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock { Text = label, Width = 60, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(box);
        return sp;
    }

    static FrameworkElement Pair(string label, TextBox a, TextBox b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock { Text = label, Width = 60, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(a);
        sp.Children.Add(b);
        return sp;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_x.Text.Trim(), out int x) || !int.TryParse(_y.Text.Trim(), out int y)
            || !int.TryParse(_w.Text.Trim(), out int w) || !int.TryParse(_h.Text.Trim(), out int h))
        {
            _error.Text = "四个值都得是整数"; return;
        }
        if (w <= 0 || h <= 0 || x < 0 || y < 0) { _error.Text = "宽高需为正、坐标非负"; return; }

        int? rh = null;
        if (_hasRowHeight)
        {
            if (!int.TryParse(_rowHeight.Text.Trim(), out int v) || v <= 0) { _error.Text = "行高必须是正整数"; return; }
            rh = v;
        }
        Rect = (x, y, w, h);
        RowHeight = rh;
        DialogResult = true;
        Close();
    }
}

/// <summary>编辑房间列表里的一列：语义 + 绝对像素 X + 宽（Y 与高跟随列表框，不单独给）。</summary>
public sealed class ColumnEditWindow : Window
{
    private readonly int _boxLeft, _boxWidth;
    private readonly ComboBox _kind = new() { Width = 150 };
    private readonly TextBox _x = new() { Width = 80, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox _w = new() { Width = 80 };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Red, VerticalAlignment = VerticalAlignment.Center };

    private sealed class KindOption(string value, string label)
    {
        public string Value { get; } = value;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// <summary>语义（RoomName / MapName / …）。</summary>
    public string Kind { get; private set; } = "";

    /// <summary>换算回"相对列表框左边缘"的 x 与宽度——预设里存的就是这个口径。</summary>
    public (int X, int Width) Relative { get; private set; }

    public ColumnEditWindow(string owner, string kind, int absoluteX, int width, int boxLeft, int boxWidth)
    {
        Title = "编辑列区域";
        Width = 320; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _boxLeft = boxLeft;
        _boxWidth = Math.Max(1, boxWidth);
        var options = new[]
        {
            new KindOption("RoomName", "房间名"), new KindOption("MapName", "地图"),
            new KindOption("PlayerId", "玩家ID"), new KindOption("Other", "其它"),
        };
        _kind.ItemsSource = options;
        _kind.SelectedItem = options.FirstOrDefault(o => o.Value == kind) ?? options[0];
        _x.Text = absoluteX.ToString(CultureInfo.InvariantCulture);
        _w.Text = width.ToString(CultureInfo.InvariantCulture);

        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(new TextBlock { Text = owner, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { new TextBlock { Text = "语义", Width = 60, VerticalAlignment = VerticalAlignment.Center }, _kind },
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { new TextBlock { Text = "X / 宽", Width = 60, VerticalAlignment = VerticalAlignment.Center }, _x, _w },
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_error);
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(12, 0, 8, 0), IsDefault = true };
        ok.Click += OnOk;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "取消", Width = 70, IsCancel = true });
        body.Children.Add(buttons);
        Content = body;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (_kind.SelectedItem is not KindOption opt) { _error.Text = "请选择语义"; return; }
        if (!int.TryParse(_x.Text.Trim(), out int absX) || !int.TryParse(_w.Text.Trim(), out int w))
        {
            _error.Text = "X 与宽都得是整数"; return;
        }
        if (w <= 0) { _error.Text = "宽必须大于 0"; return; }
        int relX = absX - _boxLeft;
        if (relX < 0 || relX + w > _boxWidth)
        {
            _error.Text = $"该列必须落在列表框内（{_boxLeft}..{_boxLeft + _boxWidth}）";
            return;
        }
        Kind = opt.Value;
        Relative = (relX, w);
        DialogResult = true;
        Close();
    }
}
