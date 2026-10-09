using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.App.Config;

/// <summary>
/// 单个 ROI 的编辑对话框：改名字/类型/**像素矩形**。
///
/// 为什么值得存在：校准窗只能"拖框"，拖出来的位置有 1~2px 抖动，也没法精确对齐某条文字的边界。
/// 这里直接给数字框，能把聊天框的上下沿卡准。列定义（RoomList 的分列）仍只能在校准窗里划。
///
/// 存储仍是相对坐标(0~1)，但**界面一律用像素**——像素是用户唯一读得懂的口径；
/// 换算依据是该 bucket 自己的分辨率（打开时就固定显示在标题行，不会拿错尺寸换算）。
/// </summary>
public partial class RoiEditWindow : Window
{
    private readonly int _bucketWidth, _bucketHeight;
    private readonly Roi _current;

    private sealed class TypeOption(RoiType value, string label)
    {
        public RoiType Value { get; } = value;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// <summary>编辑结果（列定义原样保留，只有名字/类型/矩形会变）。</summary>
    public Roi Result { get; private set; }

    public RoiEditWindow(string sceneLabel, string resolution, int bucketWidth, int bucketHeight, Roi current)
    {
        InitializeComponent();
        _current = current;
        _bucketWidth = Math.Max(1, bucketWidth);
        _bucketHeight = Math.Max(1, bucketHeight);
        Result = current;

        OwnerText.Text = $"{sceneLabel} · {resolution}（像素按此分辨率计）";
        NameBox.Text = current.Name;
        TypeCombo.ItemsSource = new[]
        {
            new TypeOption(RoiType.Chat, "聊天"), new TypeOption(RoiType.RoomList, "房间名列表"),
            new TypeOption(RoiType.MapInfo, "地图信息"),
        };
        TypeCombo.SelectedItem = ((TypeOption[])TypeCombo.ItemsSource).First(o => o.Value == RoiTypes.Parse(current.Type));

        var px = ToPixels(current.Rect);
        XBox.Text = px.X.ToString(CultureInfo.InvariantCulture);
        YBox.Text = px.Y.ToString(CultureInfo.InvariantCulture);
        WBox.Text = px.W.ToString(CultureInfo.InvariantCulture);
        HBox.Text = px.H.ToString(CultureInfo.InvariantCulture);
    }

    private (int X, int Y, int W, int H) ToPixels(RelRect r) =>
        ((int)Math.Round(r.X * _bucketWidth), (int)Math.Round(r.Y * _bucketHeight),
         (int)Math.Round(r.W * _bucketWidth), (int)Math.Round(r.H * _bucketHeight));

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(XBox.Text.Trim(), out int x) || !int.TryParse(YBox.Text.Trim(), out int y)
            || !int.TryParse(WBox.Text.Trim(), out int w) || !int.TryParse(HBox.Text.Trim(), out int h))
        {
            ErrorText.Text = "四个值都得是整数像素";
            return;
        }
        if (w <= 0 || h <= 0) { ErrorText.Text = "宽/高必须大于 0"; return; }
        if (x < 0 || y < 0 || x + w > _bucketWidth || y + h > _bucketHeight)
        {
            ErrorText.Text = $"超出画面（{_bucketWidth}×{_bucketHeight}）";
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ErrorText.Text = "名称不能为空"; return; }
        if (TypeCombo.SelectedItem is not TypeOption opt) { ErrorText.Text = "请选择类型"; return; }

        // 相对坐标按 0~1 夹住：四舍五入到整数像素再除回去，可能差出 1e-7，不夹会写出 1.0000001 这种脏值。
        Result = _current with
        {
            Name = NameBox.Text.Trim(),
            Type = opt.Value.ToString(),
            Rect = new RelRect(
                Math.Clamp(x / (double)_bucketWidth, 0d, 1d),
                Math.Clamp(y / (double)_bucketHeight, 0d, 1d),
                Math.Clamp(w / (double)_bucketWidth, 0d, 1d),
                Math.Clamp(h / (double)_bucketHeight, 0d, 1d)),
        };
        DialogResult = true;
        Close();
    }
}
