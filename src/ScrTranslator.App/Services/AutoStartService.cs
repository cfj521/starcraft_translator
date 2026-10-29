using System.IO;
using Microsoft.Win32;

namespace ScrTranslator.App.Services;

/// <summary>
/// 开机自启：HKCU\...\CurrentVersion\Run 下写一个指向"当前 exe"的值（不要求管理员）。
/// 注意路径语义：开发期(dotnet run/Debug exe)写入的是 bin 下路径，仅对"本机自用"有意义；
/// Phase 5 打包发布后安装版 exe 路径才是用户期望的持久值。开关关闭时删除该值。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScrTranslator";

    /// <summary>把开关落到注册表；返回是否成功。enable=false 也幂等（值不存在视为成功）。</summary>
    public static bool Apply(bool enable, string? exePath, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) { error = "无法打开注册表 Run 键"; return false; }

            if (enable)
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    error = $"exe 路径不可用: {exePath}";
                    return false;
                }
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读注册表当前状态（用于配置窗口回显；异常按"未启用"处理）。
    /// 传入 <paramref name="expectedExePath"/> 时只认**指向当前 exe** 的值——安装目录变过、
    /// 旧版本残留、或用户用工具清过注册表时，不能把"键存在但指向别处"当作已启用。</summary>
    public static bool IsEnabled(string? expectedExePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string raw) return false;
            if (string.IsNullOrEmpty(expectedExePath)) return true;
            string actual = raw.Trim().Trim('"');
            return string.Equals(
                System.IO.Path.GetFullPath(actual),
                System.IO.Path.GetFullPath(expectedExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
