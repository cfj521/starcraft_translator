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
        catch (Exception ex)
        {
            // 旧实现空 catch：URL/模型只改内存、磁盘仍是旧值，重启后静默回滚且日志一行都没有。
            MessageBox.Show(this, $"配置已生效但写入 settings.json 失败（重启后会回到旧值）：{ex.Message}", "自定义模型");
        }
        try
        {
            // Trim：密钥首尾空格直接进 Authorization 头会 401。
            _store.Update(d => d.CustomModelApiKey = KeyBox.Password.Trim());
        }
        catch (Exception ex)
        {
            // 旧实现不捕获：密钥没存成功，窗口却照常关闭，用户以为已配置。
            MessageBox.Show(this, $"API Key 保存失败：{ex.Message}", "自定义模型");
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
}
