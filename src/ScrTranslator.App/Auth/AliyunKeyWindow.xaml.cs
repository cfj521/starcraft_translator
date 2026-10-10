using System.Windows;
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
}
