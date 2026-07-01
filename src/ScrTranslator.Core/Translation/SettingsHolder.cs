namespace ScrTranslator.Core.Translation;

/// <summary>可变包装：TranslationSettings 本身是不可变 record，靠此类支持运行时切引擎（托盘菜单）。</summary>
public sealed class SettingsHolder
{
    public TranslationSettings Current { get; set; } = TranslationSettings.Default;
}
