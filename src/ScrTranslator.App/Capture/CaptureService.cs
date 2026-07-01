using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ScrTranslator.App.Capture;

public sealed class CaptureService : IDisposable
{
    public event Action<SKBitmap>? FrameArrived;

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private DateTime _lastFrame = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(400);

    // ── COM 互操作接口 ──────────────────────────────────────────────────────

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess { unsafe void GetBuffer(out byte* buffer, out uint capacity); }

    // ── D3D11 / WinRT 原生函数 ───────────────────────────────────────────────

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    // WinRT Activation 原生 API（绕过 CsWinRT 版本差异）
    [DllImport("combase.dll", EntryPoint = "WindowsCreateString",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(
        string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", EntryPoint = "WindowsDeleteString", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // ── 公开接口 ─────────────────────────────────────────────────────────────

    public IntPtr Hwnd { get; private set; }

    public bool Start(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Hwnd = hwnd;

        IntPtr itemPtr = CreateItemForWindow(hwnd, IID_IGraphicsCaptureItem);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        _device = CreateDirect3DDevice();
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
        return true;
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var now = DateTime.Now;
            if (now - _lastFrame < MinInterval) return;   // 节流：取帧已释放(using),跳过转换
            _lastFrame = now;

            using var sbmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface, BitmapAlphaMode.Premultiplied);
            var sk = ToSkBitmap(sbmp);
            FrameArrived?.Invoke(sk);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CaptureService] frame error: {ex}"); }
    }

    private static unsafe SKBitmap ToSkBitmap(SoftwareBitmap sbmp)
    {
        using var conv = SoftwareBitmap.Convert(sbmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var buf = conv.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buf.CreateReference();
        var desc = buf.GetPlaneDescription(0);
        int stride = desc.Stride;
        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* src, out uint cap);
        byte* dst = (byte*)sk.GetPixels();
        int rowBytes = w * 4;
        for (int y = 0; y < h; y++)
            Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * rowBytes, rowBytes, rowBytes);
        return sk;
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out IntPtr devicePtr, out _, out IntPtr ctxPtr));
        try
        {
            Guid dxgi = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(devicePtr, ref dxgi, out IntPtr dxgiPtr));
            try
            {
                CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectable);
                try { return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgiPtr); }
        }
        finally
        {
            Marshal.Release(devicePtr);
            if (ctxPtr != IntPtr.Zero) Marshal.Release(ctxPtr);
        }
    }

    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // CreateForWindow 需要 IGraphicsCaptureItem *接口* 的 IID，而非运行时类 GUID。
    // typeof(GraphicsCaptureItem).GUID / GuidGenerator.GetIID 返回的是 CsWinRT 合成的
    // 类 GUID(cc7b16ab-...)，传给 CreateForWindow 会得 E_NOINTERFACE。必须用官方接口 IID。
    internal static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// RoGetActivationFactory 拿到 GraphicsCaptureItem 的 IGraphicsCaptureItemInterop 工厂指针，
    /// 再按 COM vtable 用函数指针直接调用 CreateForWindow（不经 RCW，绕开封送不确定性）。
    /// vtable: [0]QI [1]AddRef [2]Release [3]CreateForWindow。
    /// </summary>
    private static unsafe IntPtr CreateItemForWindow(IntPtr hwnd, Guid itemIid)
    {
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
                IntPtr itemPtr;
                Marshal.ThrowExceptionForHR(createForWindow(factoryPtr, hwnd, &itemIid, &itemPtr));
                return itemPtr;
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public void Stop()
    {
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => Stop();
}
