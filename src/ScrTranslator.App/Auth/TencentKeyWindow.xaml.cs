using System.Windows;
using ScrTranslator.App.Secrets;

namespace ScrTranslator.App.Auth;

/// <summary>腾讯云机器翻译密钥输入框：SecretId + SecretKey，确定后写入 SecretStore（DPAPI 加密落盘），取消不改动。</summary>
public partial class TencentKeyWindow : Window
{
    private readonly SecretStore _store;

    public TencentKeyWindow(SecretStore store)
    {
        InitializeComponent();
        _store = store;
        var t = store.Load().Tencent;
        SecretIdBox.Text = t.SecretId;
        SecretKeyBox.Password = t.SecretKey;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var data = _store.Load();
        data.Tencent = new TencentSecrets { SecretId = SecretIdBox.Text.Trim(), SecretKey = SecretKeyBox.Password };
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
