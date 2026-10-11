using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.App.Config;

/// <summary>
/// 单个校准区域的编辑对话框：改**场景**/名称/类型/**像素矩形**。
///
/// 为什么值得存在：校准窗只能拖框，拖出来的位置有 1~2px 抖动，也没法精确对齐某条文字的边界；
/// 而且误在别的界面按了 F8 时，条目会落错场景——以前只能删了重画，这里改个下拉即可。
///
/// 存储仍是相对坐标(0~1)，但**界面一律用像素**——像素是用户唯一读得懂的口径；
/// 换算依据是该 bucket 自己的分辨率（显示在标题行，不会拿错尺寸换算）。
/// 列定义（房间名列表的分列）仍只能在校准窗里划，这里不动它。
/// </summary>
public partial class RoiEditWindow : Window
{
    private readonly int _bucketWidth, _bucketHeight;
    private readonly Roi _current;

    private sealed class Option<T>(T value, string label)
    {
        public T Value { get; } = value;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// <summary>编辑结果（列定义原样保留，只有场景/名字/类型/矩形会变）。</summary>
    public Roi Result { get; private set; }

    /// <summary>用户改选后的场景键；与进来时相同就只是"没改"。</summary>
    public string ResultSceneId { get; private set; }

    public RoiEditWindow(string resolution, int bucketWidth, int bucketHeight,
        string sceneId, Roi current, bool isPreset = false)
    {
        InitializeComponent();
        _current = current;
        _bucketWidth = Math.Max(1, bucketWidth);
        _bucketHeight = Math.Max(1, bucketHeight);
        Result = current;
        ResultSceneId = sceneId;

        NameBox.Text = current.Name;

        var sceneOptions = new[]
        {
            new Option<string>(GameSceneIds.Browsing, GameScene.Browsing.Label()),
            new Option<string>(GameSceneIds.Room, GameScene.Room.Label()),
            new Option<string>(GameSceneIds.InGame, GameScene.InGame.Label()),
        };
        SceneCombo.ItemsSource = sceneOptions;
        SceneCombo.SelectedItem = sceneOptions.FirstOrDefault(o => o.Value == sceneId) ?? sceneOptions[0];

        TypeCombo.ItemsSource = new[]
        {
            new Option<RoiType>(RoiType.Chat, "聊天"), new Option<RoiType>(RoiType.RoomNameList, "房间名列表"),
            new Option<RoiType>(RoiType.MapInfo, "地图信息"), new Option<RoiType>(RoiType.GameName, "房间名称"),
            new Option<RoiType>(RoiType.MapName, "地图名称"),
        };
        TypeCombo.SelectedItem = ((Option<RoiType>[])TypeCombo.ItemsSource).First(o => o.Value == RoiTypes.Parse(current.Type));

        var px = ToPixels(current.Rect);
        XBox.Text = px.X.ToString(CultureInfo.InvariantCulture);
        YBox.Text = px.Y.ToString(CultureInfo.InvariantCulture);
        WBox.Text = px.W.ToString(CultureInfo.InvariantCulture);
        HBox.Text = px.H.ToString(CultureInfo.InvariantCulture);

        // 预设模式：场景/名称/类型固定，只允许调矩形 → 禁用这三个输入，只留下坐标可编辑。
        // （复用的就是普通校准区域的编辑窗，不为预设另搭一套 UI。）
        if (isPreset)
        {
            SceneCombo.IsEnabled = false;
            NameBox.IsEnabled = false;
            TypeCombo.IsEnabled = false;
        }
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
        if (TypeCombo.SelectedItem is not Option<RoiType> type) { ErrorText.Text = "请选择类型"; return; }
        if (SceneCombo.SelectedItem is not Option<string> scene) { ErrorText.Text = "请选择场景"; return; }

        // 相对坐标按 0~1 夹住：整数像素除回去可能有 1e-7 的尾差，不夹会写出 1.0000001 这种脏值。
        Result = _current with
        {
            Name = NameBox.Text.Trim(),
            Type = type.Value.ToString(),
            Rect = new RelRect(
                Math.Clamp(x / (double)_bucketWidth, 0d, 1d),
                Math.Clamp(y / (double)_bucketHeight, 0d, 1d),
                Math.Clamp(w / (double)_bucketWidth, 0d, 1d),
                Math.Clamp(h / (double)_bucketHeight, 0d, 1d)),
        };
        ResultSceneId = scene.Value;
        DialogResult = true;
        Close();
    }
}
