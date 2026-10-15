using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScrTranslator.Core;
using ScrTranslator.Core.Scene;

namespace ScrTranslator.App.Config;

/// <summary>
/// 单个预设区域的编辑对话框：只改**相对矩形**（0~1）。
///
/// 场景与区域名固定（预设 parts 是定死的，进来时传入，只允许调矩形）。
/// 相对坐标 0~1，与列表/校准窗同一口径，分辨率无关，存进来就是最终值。
/// </summary>
public partial class RoiEditWindow : Window
{
    private readonly string _sceneId;
    private readonly string _partName;
    private readonly RelRect _current;

    /// <summary>编辑结果：场景/区域名原样带回（固定），只有矩形会变。</summary>
    public (string SceneId, string PartName, RelRect Rect) Result { get; private set; }

    public RoiEditWindow(string sceneId, string partName, RelRect rect)
    {
        InitializeComponent();
        _sceneId = sceneId;
        _partName = partName;
        _current = rect;
        Result = (sceneId, partName, rect);

        SceneText.Text = GameSceneIds.FromId(sceneId).Label();
        PartText.Text = partName;

        XBox.Text = rect.X.ToString("F4", CultureInfo.InvariantCulture);
        YBox.Text = rect.Y.ToString("F4", CultureInfo.InvariantCulture);
        WBox.Text = rect.W.ToString("F4", CultureInfo.InvariantCulture);
        HBox.Text = rect.H.ToString("F4", CultureInfo.InvariantCulture);
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
        Result = (_sceneId, _partName, new RelRect(x, y, w, h));
        DialogResult = true;
        Close();
    }

    /// <summary>相对坐标输入：点号小数，值域 [0,1]。</summary>
    private static bool TryRel(TextBox box, out double v)
        => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
           && v >= 0d && v <= 1d;
}
