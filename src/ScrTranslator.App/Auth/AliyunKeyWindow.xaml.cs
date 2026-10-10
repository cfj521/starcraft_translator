using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ScrTranslator.App.Secrets;

namespace ScrTranslator.App.Auth;

/// <summary>阿里云机器翻译密钥输入框：AccessKeyId + AccessKeySecret，确定后写入 SecretStore（DPAPI 加密落盘），取消不改动。</summary>
public partial class AliyunKeyWindow : Window
{
    private readonly SecretStore _store;

    public AliyunKeyWindow(SecretStore store)
    {
        InitializeComponent();
        _store = store;
        var a = store.Load().Aliyun;
        AccessKeyIdBox.Text = a.AccessKeyId;
        AccessKeySecretBox.Password = a.AccessKeySecret;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var data = _store.Load();
        data.Aliyun = new AliyunSecrets { AccessKeyId = AccessKeyIdBox.Text.Trim(), AccessKeySecret = AccessKeySecretBox.Password };
        _store.Save(data);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>点"获取密钥"超链接 → 用默认浏览器打开阿里云 RAM AccessKey 管理页。</summary>
    private void OnKeyLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 无默认浏览器等异常时静默，不影响密钥填写 */ }
    }
}
