using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ScrTranslator.App.Secrets;

namespace ScrTranslator.App.Auth;

/// <summary>OpenAI API Key 输入框（API 模式，sk-...）：确定后写入 SecretStore（DPAPI 加密落盘），取消不改动。
/// 与订阅 OAuth 登录的 ChatGPT 行并列，但凭据独立（OpenAiApiKey 字段）。</summary>
public partial class OpenAiApiKeyWindow : Window
{
    private readonly SecretStore _store;

    public OpenAiApiKeyWindow(SecretStore store)
    {
        InitializeComponent();
        _store = store;
        KeyBox.Password = _store.Load().OpenAiApiKey;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var data = _store.Load();
        data.OpenAiApiKey = KeyBox.Password;
        _store.Save(data);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>点"获取密钥"超链接 → 用默认浏览器打开 OpenAI 平台的 API Key 页。</summary>
    private void OnKeyLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 无默认浏览器等异常时静默，不影响密钥填写 */ }
    }
}
