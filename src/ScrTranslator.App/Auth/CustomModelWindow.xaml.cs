using System.Windows;
using ScrTranslator.App.Secrets;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;

namespace ScrTranslator.App.Auth;

/// <summary>自定义模型（OpenAI 兼容模式）配置窗：填 Base URL / 模型 ID / API KEY。
/// 确定后三者**立即生效**：URL 与模型 ID 写回 SettingsHolder 并落盘 settings.json（跟 KEY 一起即时提交，
/// 避免"KEY 已存、URL/模型 却因主窗取消而丢"的半配置态），KEY 写 SecretStore（DPAPI）。取消不改动。
/// URL/模型 ID 进 settings.json，KEY 只进 secrets.dat（不写 settings.json，跟其它密钥一致）。</summary>
public partial class CustomModelWindow : Window
{
    private readonly SettingsHolder _holder;
    private readonly SecretStore _store;
    private readonly string _settingsPath;

    public CustomModelWindow(SettingsHolder holder, SecretStore store, string settingsPath)
    {
        InitializeComponent();
        _holder = holder;
        _store = store;
        _settingsPath = settingsPath;
        UrlBox.Text = _holder.Current.Translation.CustomModelUrl;
        ModelBox.Text = _holder.Current.Translation.CustomModelId;
        KeyBox.Password = _store.Load().CustomModelApiKey;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        string model = ModelBox.Text.Trim();
        // URL/模型 ID 写回 Holder 并立即落盘（与 KEY 的即时生效一致，三者同时提交、不出现半配置态）。
        var cur = _holder.Current;
        cur = cur with { Translation = cur.Translation with { CustomModelUrl = url, CustomModelId = model } };
        _holder.Current = cur;
        try { SettingsStore.Save(cur, _settingsPath); }
        catch { /* 落盘失败不阻断 KEY 保存；下次主窗确定会再试 */ }
        var data = _store.Load();
        data.CustomModelApiKey = KeyBox.Password;
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
