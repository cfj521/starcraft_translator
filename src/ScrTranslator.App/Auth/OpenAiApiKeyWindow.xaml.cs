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
        try
        {
            // Lock 内读改写：避免与其它密钥窗口/后台 token 刷新并发时后写覆盖先写。
            // Trim：密钥首尾空格进 Authorization 头会 401。
            _store.Update(d => d.OpenAiApiKey = KeyBox.Password.Trim());
        }
        catch (Exception ex)
        {
            // 旧实现不捕获：异常被 DispatcherUnhandledException 静默吞掉，窗口停在原地无任何提示。
            MessageBox.Show(this, $"保存失败：{ex.Message}", "OpenAI API Key");
            return;
        }
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
