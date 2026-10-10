using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ScrTranslator.App.Secrets;

namespace ScrTranslator.App.Auth;

/// <summary>最简 Key 输入框：确定后写入 SecretStore（DPAPI 加密落盘），取消不改动。</summary>
public partial class DashScopeKeyWindow : Window
{
    private readonly SecretStore _store;

    public DashScopeKeyWindow(SecretStore store)
    {
        InitializeComponent();
        _store = store;
        KeyBox.Password = _store.Load().DashScopeApiKey;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var data = _store.Load();
        data.DashScopeApiKey = KeyBox.Password;
        _store.Save(data);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>点"获取密钥"超链接 → 用默认浏览器打开阿里云百炼 API Key 页（旧 DashScope 控制台已下线）。</summary>
    private void OnKeyLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 无默认浏览器等异常时静默，不影响密钥填写 */ }
    }
}
