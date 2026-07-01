using System.Windows;
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
}
