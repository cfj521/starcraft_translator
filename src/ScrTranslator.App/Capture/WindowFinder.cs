using System;
using System.Diagnostics;

namespace ScrTranslator.App.Capture;

public static class WindowFinder
{
    /// <summary>按进程名找主窗口句柄；找不到返回 IntPtr.Zero。
    /// 提前 return / 抛异常时也必须把 Process.GetProcessesByName 返回的**其余元素** Dispose，
    /// 否则每次轮询泄漏一批 Process 句柄（原实现只 dispose 当前循环项）。</summary>
    public static IntPtr FindByProcess(string processName)
    {
        Process[] all = Process.GetProcessesByName(processName);
        try
        {
            foreach (var p in all)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch (InvalidOperationException)
                {
                    // 进程在枚举与读取之间退出：跳过即可。
                }
            }
            return IntPtr.Zero;
        }
        finally
        {
            foreach (var p in all)
            {
                try { p.Dispose(); } catch { /* 已释放/已失效，忽略 */ }
            }
        }
    }
}
