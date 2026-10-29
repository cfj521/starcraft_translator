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
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>窗口在屏幕的物理像素矩形；失败返回 Empty。</summary>
    public static System.Drawing.Rectangle GetWindowScreenRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out RECT r)
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : System.Drawing.Rectangle.Empty;

    /// <summary>系统主显示器 DPI（96=100%）。找不到游戏窗口时用主屏物理矩形兜底——那时尚无窗口句柄拿不到按窗口 DPI。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    // ── 窗口所在显示器物理尺寸（捕获帧池上限用）────────────────────────────────
    // 帧池最大尺寸必须 ≥ 窗口可能的最大尺寸。全屏窗口=整屏，窗口再大也不超过显示器，
    // 所以用显示器物理尺寸做上限：任何窗口/全屏尺寸都装得下，WGC 逐帧交真实尺寸，无需重建会话。
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>窗口所在显示器的物理像素尺寸 (宽,高)。取不到（窗口无效/无显示器）时返回 (0,0)。</summary>
    public static (int W, int H) GetMonitorSize(IntPtr hwnd)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return (0, 0);
        // cbSize 必须先赋值：GetMonitorInfo 用它校验结构体大小，为 0 时直接返回失败(err=87)。
        // 漏了这一步 → 本方法恒返回 (0,0) → 帧池上限退回 item.Size → "切分辨率不刷新"的修复形同虚设。
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return (0, 0);
        return (mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
    }

    // ── 窗口事件钩（事件驱动，取代尺寸轮询）─────────────────────────────────
    // 游戏窗口 resize 时 WGC 仍按建会话那一刻的尺寸交帧，须重建会话。这里用 WinEventHook
    // 监听目标窗口的尺寸/显隐变化，变了才回调重建，不轮询、不靠 watchdog。
    public const uint EVENT_OBJECT_SHOW = 0x8002;      // 窗口显示（创建/恢复）
    public const uint EVENT_OBJECT_HIDE = 0x8003;      // 窗口隐藏（最小化/销毁）
    public const uint EVENT_OBJECT_LOCATIONCHANGED = 0x8007; // 窗口位置/尺寸变化
    public const uint EVENT_OBJECT_RESIZED = 0x8008;   // 窗口尺寸变化
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(IntPtr hWinEventHook, uint dwEvent, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwTimeStamp);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, IntPtr gpwinEventProc,
        uint idEvent, uint idWinEventProc, uint wflags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ── 消息泵：WINEVENT_OUTOFCONTEXT 钩子装在哪个线程，事件就投递到那个线程的隐藏窗口。
    //    装钩线程必须泵消息（GetMessage/PeekMessage 循环）才能收到事件。──
    public const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

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
