using System.Diagnostics;

namespace ScrTranslator.App.Capture;

public static class WindowFinder
{
    /// <summary>按进程名找主窗口句柄；找不到返回 IntPtr.Zero。</summary>
    public static IntPtr FindByProcess(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
        return IntPtr.Zero;
    }
}
