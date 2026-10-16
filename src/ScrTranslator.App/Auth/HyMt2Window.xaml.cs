using System.Threading.Tasks;
using System.Windows;
using Sdcb.HyMT2Sharp.Backends.Vulkan;
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.App.Auth;

/// <summary>本地 Hy-MT2 配置窗：填 GGUF 权重文件路径 + 选推理设备（CPU / GPU-Vulkan）。确定后立即生效——写回 SettingsHolder 并落盘 settings.json
/// （与主窗其它设置同机：路径/设备进 settings.json，非密钥、不走 SecretStore）。取消不改动。
/// 推理设备：已存 GPU 则不再探测（直接启用 GPU 项）；否则窗口先按 CPU 渲染、GPU 项禁用，
/// 后台线程探测 <see cref="VulkanBackend.IsSupported"/>——通过则启用 GPU 项，不支持则保持禁用（选不了）。
/// backend 变更（cpu↔gpu）确定时 Unload+Load 用新设备重载权重；模型未加载时 Unload 无操作、Load 直接按新设备加载。</summary>
public partial class HyMt2Window : Window
{
    private readonly SettingsHolder _holder;
    private readonly string _settingsPath;
    private readonly HyMt2Provider _hyMt2;
    private readonly string _prevBackend; // 打开前的 backend，确定时对比是否变更

    public HyMt2Window(SettingsHolder holder, string settingsPath, HyMt2Provider hyMt2)
    {
        InitializeComponent();
        _holder = holder;
        _settingsPath = settingsPath;
        _hyMt2 = hyMt2;
        _prevBackend = _holder.Current.Translation.HyMt2Backend;
        PathBox.Text = _holder.Current.Translation.HyMt2ModelPath;

        // 推理设备：读回已存选择。GPU 需系统有可用 Vulkan 设备（探测较贵，不能阻塞窗口首帧渲染）。
        if (_prevBackend == "gpu")
        {
            // 已是 GPU 模式：不再探测（引擎能跑 GPU 说明设备可用），直接启用并选中 GPU 项。
            GpuRadio.IsChecked = true;
        }
        else
        {
            // CPU 模式：窗口先按 CPU 渲染、GPU 项禁用，后台探测通过后再启用（见 DetectVulkanAsync）。
            CpuRadio.IsChecked = true;
            _ = DetectVulkanAsync();
        }
    }

    /// <summary>后台探测 Vulkan 设备，完成后回 UI 线程启用 GPU 项（探测期间保持禁用，用户看不到半成品状态）。
    /// 探测不通过则 GPU 项保持禁用。探测不阻塞窗口首帧——窗口构造完即渲染，探测结果到位后才解锁。</summary>
    private async Task DetectVulkanAsync()
    {
        bool supported = await Task.Run(() => VulkanBackend.IsSupported());
        if (!IsLoaded) return; // 窗口已关闭，别再碰控件
        if (supported) GpuRadio.IsEnabled = true;
        // 探测不通过 → GPU 项保持禁用（本就是 CPU 模式，无需切换选中项）
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Hy-MT2 权重文件（GGUF）",
            Filter = "GGUF 权重 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string path = PathBox.Text.Trim();
        string backend = GpuRadio.IsChecked == true ? "gpu" : "cpu";
        _holder.Current = _holder.Current with { Translation = _holder.Current.Translation with { HyMt2ModelPath = path, HyMt2Backend = backend } };
        try { SettingsStore.Save(_holder.Current, _settingsPath); }
        catch { /* 落盘失败不阻断；下次主窗确定会再试 */ }
        // backend 变更 → 用新设备重载模型（Unload 释放旧权重，Load 按新 backend 冷加载）。
        if (backend != _prevBackend)
        {
            _hyMt2.Unload();
            _hyMt2.Load();
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
