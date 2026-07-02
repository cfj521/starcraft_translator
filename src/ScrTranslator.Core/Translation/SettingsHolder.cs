using ScrTranslator.Core.Settings;

namespace ScrTranslator.Core.Translation;

/// <summary>可变包装：AppSettings 本身是不可变 record，靠此类支持运行时切引擎（托盘菜单）/热改设置。</summary>
public sealed class SettingsHolder
{
    public AppSettings Current { get; set; } = new AppSettings();
}
