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
        try
        {
            // 在 store 的锁内"读→改→写"：避免与别的窗口/后台 token 刷新并发时互相覆盖整份 secrets。
            // Trim：密钥首尾空格进 Authorization 头会 401，症状是"明明配了却鉴权失败"，极难定位。
            _store.Update(d => d.DashScopeApiKey = KeyBox.Password.Trim());
        }
        catch (Exception ex)
        {
            // 旧实现不捕获：异常冒到 DispatcherUnhandledException 被静默吞掉 → 窗口停在原地、用户以为"按钮坏了"。
            MessageBox.Show(this, $"保存失败：{ex.Message}", "DashScope Key");
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

    /// <summary>点"获取密钥"超链接 → 用默认浏览器打开阿里云百炼 API Key 页（旧 DashScope 控制台已下线）。</summary>
    private void OnKeyLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 无默认浏览器等异常时静默，不影响密钥填写 */ }
    }
}
