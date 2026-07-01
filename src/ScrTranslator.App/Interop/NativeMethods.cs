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
}
