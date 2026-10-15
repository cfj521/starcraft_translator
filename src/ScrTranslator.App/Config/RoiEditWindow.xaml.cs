using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.App.Config;

/// <summary>
/// 单个校准区域的编辑对话框：改**场景**/名称/**相对矩形**（0~1）。
///
/// 为什么值得存在：校准窗只能拖框，拖出来的位置有 1~2px 抖动，也没法精确对齐某条文字的边界；
/// 而且误在别的界面按了 F8 时，条目会落错场景——以前只能删了重画，这里改个下拉即可。
///
/// 界面与列表、校准窗同用相对坐标（0~1），分辨率无关，存进来就是最终值。
/// 列定义（房间名列表的分列）仍只能在校准窗里划，这里不动它。
/// </summary>
public partial class RoiEditWindow : Window
{
    private readonly Roi _current;

    private sealed class Option<T>(T value, string label)
    {
        public T Value { get; } = value;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// <summary>编辑结果（列定义原样保留，只有场景/名字/矩形会变）。</summary>
    public Roi Result { get; private set; }

    /// <summary>用户改选后的场景键；与进来时相同就只是"没改"。</summary>
    public string ResultSceneId { get; private set; }

    public RoiEditWindow(string sceneId, Roi current, bool isPreset = false)
    {
        InitializeComponent();
        _current = current;
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

        // 相对坐标 0~1，与列表/校准窗同一口径，直接显示。
        XBox.Text = current.Rect.X.ToString("F4", CultureInfo.InvariantCulture);
        YBox.Text = current.Rect.Y.ToString("F4", CultureInfo.InvariantCulture);
        WBox.Text = current.Rect.W.ToString("F4", CultureInfo.InvariantCulture);
        HBox.Text = current.Rect.H.ToString("F4", CultureInfo.InvariantCulture);

        // 预设模式：场景/名称固定，只允许调矩形 → 禁用这两个输入，只留下坐标可编辑。
        // （复用的就是普通校准区域的编辑窗，不为预设另搭一套 UI。）
        if (isPreset)
        {
            SceneCombo.IsEnabled = false;
            NameBox.IsEnabled = false;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryRel(XBox, out double x) || !TryRel(YBox, out double y)
            || !TryRel(WBox, out double w) || !TryRel(HBox, out double h))
        {
            ErrorText.Text = "四个值都得是 0~1 的数字";
            return;
        }
        if (w <= 0 || h <= 0) { ErrorText.Text = "宽/高必须大于 0"; return; }
        const double eps = 1e-9; // 0.3+0.7 浮点求和会冒出 2e-16 的尾差，放行
        if (x + w > 1 + eps || y + h > 1 + eps)
        {
            ErrorText.Text = "超出画面（左+宽、顶+高都必须 ≤ 1）";
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ErrorText.Text = "名称不能为空"; return; }
        if (SceneCombo.SelectedItem is not Option<string> scene) { ErrorText.Text = "请选择场景"; return; }

        Result = _current with
        {
            Name = NameBox.Text.Trim(),
            Rect = new RelRect(x, y, w, h),
        };
        ResultSceneId = scene.Value;
        DialogResult = true;
        Close();
    }

    /// <summary>相对坐标输入：点号小数，值域 [0,1]。</summary>
    private static bool TryRel(TextBox box, out double v)
        => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
           && v >= 0d && v <= 1d;
}
