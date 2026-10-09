using System;
using System.Runtime.InteropServices;
using ScrTranslator.Core.Interop;

namespace ScrTranslator.App.Interop;

/// <summary>user32 互操作：把窗口设为点击穿透。</summary>
internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;

    // 64 位 Windows 使用 *Ptr 版本（本项目仅支持 64 位 Windows 10/11）。
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>给窗口加上 WS_EX_TRANSPARENT | WS_EX_LAYERED，使鼠标事件穿透到下层。</summary>
    public static void EnableClickThrough(IntPtr hwnd)
    {
        int exStyle = (int)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        int newStyle = WindowStyles.ApplyClickThrough(exStyle);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(newStyle));
    }

    /// <summary>开/关点击穿透。开=加 TRANSPARENT|LAYERED；关=仅去 TRANSPARENT（保留 LAYERED，WPF 透明窗需要它）。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool on)
    {
        int ex = (int)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (on) ex = WindowStyles.ApplyClickThrough(ex);
        else ex &= ~WindowStyles.WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public const int WS_EX_NOACTIVATE = unchecked((int)0x08000000);
    /// <summary>置 WS_EX_NOACTIVATE：点击窗口也不激活/不抢前台焦点（配合 WM_MOUSEACTIVATE 钩子）。</summary>
    public static void EnsureNoActivate(IntPtr hwnd)
    {
        int ex = (int)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_NOACTIVATE) == 0)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>窗口在屏幕的物理像素矩形；失败返回 Empty。</summary>
    public static System.Drawing.Rectangle GetWindowScreenRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out RECT r)
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : System.Drawing.Rectangle.Empty;

    // ── 窗口状态探测（Phase 5 异常恢复看门狗用） ─────────────────────────────
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow(); // 当前前台窗口句柄（游戏非活动时隐藏悬浮/调试窗口）

    // ── 全局热键 ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
