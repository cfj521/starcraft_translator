using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace ScrTranslator.Core.Tests;

/// <summary>
/// GraphicsCaptureItem 的 IGraphicsCaptureItemInterop::CreateForWindow interop 回归测试。
/// 复刻 CaptureService.CreateItemForWindow 的机制（RoGetActivationFactory + COM vtable 函数指针直调），
/// 对一个真实注册创建的顶层窗口分别传「错的运行时类 GUID」与「对的 IGraphicsCaptureItem 接口 IID」，
/// 证明：类 GUID → 失败（实机是 E_NOINTERFACE），接口 IID → S_OK 返回有效指针（修复后）。
/// 不依赖 SC:R 在场。
/// </summary>
public sealed class CaptureInteropTests(ITestOutputHelper output)
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    // CsWinRT 为运行时类 GraphicsCaptureItem 合成的名字哈希 GUID（= typeof(...).GUID，实机崩溃时误传的值）。
    private static readonly Guid ClassGuid_Wrong = new("cc7b16ab-e4bc-3d0e-a4eb-4fdb9ce0a1ff");

    [Fact]
    public void CreateForWindow_ItemIid_Succeeds_ClassGuid_Fails()
    {
        IntPtr hwnd = CreateOffscreenTopLevelWindow();
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            // 对的 IID（修复值）→ S_OK 且返回有效 item 指针
            int hrOk = TryCreateForWindow(hwnd, IID_IGraphicsCaptureItem, out IntPtr okPtr);
            output.WriteLine($"itemIid  79C3F95B... hr=0x{hrOk:X8} ptr={okPtr}");

            // 错的 IID（旧代码用的运行时类 GUID）→ 失败（实机为 E_NOINTERFACE）
            int hrWrong = TryCreateForWindow(hwnd, ClassGuid_Wrong, out IntPtr wrongPtr);
            output.WriteLine($"classGuid cc7b16ab... hr=0x{hrWrong:X8} ptr={wrongPtr}");
            if (wrongPtr != IntPtr.Zero) Marshal.Release(wrongPtr);

            try
            {
                Assert.Equal(0, hrOk);                 // 修复：正确 IID → S_OK
                Assert.NotEqual(IntPtr.Zero, okPtr);   // 返回了有效 GraphicsCaptureItem
                Assert.True(hrWrong < 0);              // 错误 IID → 失败（复现崩溃根因）
            }
            finally { if (okPtr != IntPtr.Zero) Marshal.Release(okPtr); }
        }
        finally { DestroyWindow(hwnd); }
    }

    // ── 复刻生产 interop：RoGetActivationFactory 拿 interop 工厂，vtable[3]=CreateForWindow 直调 ──
    private static unsafe int TryCreateForWindow(IntPtr hwnd, Guid itemIid, out IntPtr itemPtr)
    {
        itemPtr = IntPtr.Zero;
        const string clsid = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(clsid, clsid.Length, out IntPtr hstring));
        try
        {
            Guid interopIid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopIid, out IntPtr factoryPtr));
            try
            {
                void** vtbl = *(void***)factoryPtr;
                var createForWindow =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                IntPtr result;
                int hr = createForWindow(factoryPtr, hwnd, &itemIid, &result);
                if (hr >= 0) itemPtr = result;
                return hr;
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    // ── 注册真实窗口类并创建一个离屏、不可见的顶层 overlapped 窗口 ──
    private static unsafe IntPtr CreateOffscreenTopLevelWindow()
    {
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        IntPtr hInstance = GetModuleHandleW(null);
        string className = "ScrCapTestWnd";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc); // 已注册则返回 0 + ERROR_CLASS_ALREADY_EXISTS，无碍
        IntPtr hwnd = CreateWindowExW(0, className, "scr-cap-test", WS_OVERLAPPEDWINDOW,
            -20000, -20000, 200, 200, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        const int SW_SHOWNA = 8; // 显示但不激活；仍在离屏坐标，用户不可见。CreateForWindow 需窗口被显示过。
        ShowWindow(hwnd, SW_SHOWNA);
        PumpMessages();
        return hwnd;
    }

    private static void PumpMessages()
    {
        while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int pt_x; public int pt_y;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l) => DefWindowProcW(h, msg, w, l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
