using System.Runtime.InteropServices;
using SkiaSharp;
using ScrTranslator.App.Interop;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ScrTranslator.App.Capture;

public sealed class CaptureService : IDisposable
{
    public event Action<SKBitmap>? FrameArrived;
    /// <summary>诊断接线：帧转换异常（业务级错误，恒记）。由宿主注入。</summary>
    public Action<string>? LogError;
    /// <summary>诊断接线：帧流统计（诊断级，仅 DEBUG 落盘）。由宿主注入。</summary>
    public Action<string>? LogDebug;
    private long _frameCount;
    /// <summary>累计投递帧数（看门狗用它判断"捕获活着但零帧"=独占全屏/静止画面）。</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private DateTime _lastFrame = DateTime.MinValue;
    private volatile int _minIntervalMs = 400;
    /// <summary>帧转换节流(毫秒)。由 TranslationEngine 每帧按 PollInterval.CaptureFloor(识别与翻译两个节拍里较快者)
    /// 刷新；volatile 保证捕获线程读到最新值。默认 400 保持旧行为。</summary>
    public int MinIntervalMs { get => _minIntervalMs; set => _minIntervalMs = value; }

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

    // ── 窗口尺寸事件驱动：WGC 会话按"建会话那一刻"的窗口尺寸交帧，游戏切分辨率后不自己更新，
    //    帧就锁死在旧分辨率上（帧数照涨、尺寸不变），非得重建会话才恢复。这里用 WinEventHook
    //    监听目标窗口尺寸变化，变了才 Stop+Start 重建，事件驱动、不轮询、不靠 watchdog 提醒。──
    private IntPtr _hookedHwnd; // 当前要盯的游戏窗口；钩线程据此装/解钩
    private (int W, int H) _captureSize;
    private volatile bool _isRestarting;
    // WinEventProc 委托须保活整个实例生命周期。单一实例+永不置空=函数指针恒有效，
    // 杜绝"A callback was made on a garbage collected delegate"。
    private readonly NativeMethods.WinEventProc _winEventProc;
    // 专用消息泵线程：WINEVENT_OUTOFCONTEXT 钩子必须装在带消息循环的线程上，系统才把事件投递过来。
    // 看门狗/首捕跑在无线程池消息循环的线程上，钩子装了也收不到——"改分辨率不重建"的根因就在这。
    private readonly object _hookGate = new();
    private Thread? _hookThread;
    private volatile bool _hookThreadLive;

    public CaptureService()
    {
        _winEventProc = (hook, dwEvent, targetHwnd, idObject, idChild, dwEventThread, dwTimeStamp) =>
        {
            if (targetHwnd != _hookedHwnd) return; // 只处理当前盯住的窗口；换窗/重建前后的过期回调丢弃
            OnWindowSizeEvent(dwEvent, targetHwnd);
        };
    }

    private static (int W, int H) GetWindowSize(IntPtr hwnd)
    {
        var r = NativeMethods.GetWindowScreenRect(hwnd);
        return (r.Width, r.Height);
    }

    /// <summary>盯住游戏窗口尺寸：记 _hookedHwnd，确保专用消息泵线程在跑（线程自己装钩+泵消息）。</summary>
    private void HookSizeEvents(IntPtr hwnd)
    {
        _hookedHwnd = hwnd;
        EnsureHookThread();
    }

    /// <summary>解除尺寸监听：_hookedHwnd 置 0，钩线程自行解钩。线程留着复用，下次 Start 直接装钩。</summary>
    private void UnhookSizeEvents()
    {
        _hookedHwnd = IntPtr.Zero;
    }

    private void EnsureHookThread()
    {
        lock (_hookGate)
        {
            if (_hookThread is { IsAlive: true }) return;
            _hookThreadLive = true;
            _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "CaptureHook" };
            _hookThread.SetApartmentState(ApartmentState.STA); // WinEventHook 需 STA 消息循环
            _hookThread.Start();
        }
    }

    /// <summary>钩线程：按 _hookedHwnd 装/解钩，并持续泵消息。钩子事件经系统隐藏窗口投递到本线程，不泵消息收不到。</summary>
    private void HookThreadMain()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            while (_hookThreadLive)
            {
                IntPtr target = _hookedHwnd;
                if (target != IntPtr.Zero && hook == IntPtr.Zero)
                {
                    hook = NativeMethods.SetWinEventHook(
                        NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_RESIZED,
                        IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_winEventProc),
                        0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
                }
                else if (target == IntPtr.Zero && hook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWinEvent(hook);
                    hook = IntPtr.Zero;
                }
                NativeMethods.MSG msg;
                while (NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
                Thread.Sleep(20); // 无消息时让出，避免空转
            }
        }
        finally
        {
            if (hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(hook);
        }
    }

    /// <summary>尺寸/显隐事件回调：读当前窗口尺寸，与建会话基准不同 → 游戏切了分辨率，Stop+Start 重建。</summary>
    private void OnWindowSizeEvent(uint dwEvent, IntPtr hwnd)
    {
        if (hwnd != _hookedHwnd) return; // 过期钩子（窗口已换/已重建），丢弃
        if (_isRestarting) return;
        if (dwEvent == NativeMethods.EVENT_OBJECT_HIDE) return; // 隐藏/最小化由 watchdog 管，这里不重建
        var (w, h) = GetWindowSize(hwnd);
        if (w <= 0 || h <= 0) return;
        if (w == _captureSize.W && h == _captureSize.H) return; // 没变，不动
        _isRestarting = true;
        try
        {
            LogDebug?.Invoke($"[capture][resize] 窗口尺寸 {w}x{h} ≠ 捕获基准 {_captureSize.W}x{_captureSize.H}，重建捕获会话以刷新分辨率");
            Stop();
            Start(hwnd);
        }
        catch (Exception ex) { LogError?.Invoke($"[capture][resize] 尺寸变化重建失败: {ex.Message}"); }
        finally { _isRestarting = false; }
    }

    public bool Start(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Hwnd = hwnd;

        IntPtr itemPtr = CreateItemForWindow(hwnd, IID_IGraphicsCaptureItem);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        _device = CreateDirect3DDevice();
        // 帧池上限=窗口所在显示器的物理尺寸（取不到才退回窗口自身尺寸 item.Size）。
        // 若用 item.Size（创建那一刻的窗口尺寸）做上限：游戏从窗口态切全屏后，新尺寸超出帧池上限，
        // WGC 交不出新分辨率的帧，帧就"锁"在旧分辨率上，非得 F9 重建会话才恢复——这就是"分辨率不实时"的根因。
        // 显示器尺寸是窗口能达到的最大尺寸，任何窗口/全屏都装得下，WGC 逐帧交真实尺寸，分辨率永远最新。
        var (monW, monH) = NativeMethods.GetMonitorSize(hwnd);
        var maxSize = monW > 0 && monH > 0 ? new Windows.Graphics.SizeInt32(monW, monH) : item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, maxSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
        // 记录建会话时的窗口尺寸，挂尺寸事件钩：变了就重建会话（见 OnWindowSizeEvent）。
        _captureSize = GetWindowSize(hwnd);
        HookSizeEvents(hwnd);
        return true;
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        int errNo = 0;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            long n = Interlocked.Increment(ref _frameCount);

            var now = DateTime.Now;
            if ((now - _lastFrame).TotalMilliseconds < _minIntervalMs) return;   // 节流：取帧已释放(using),跳过转换
            _lastFrame = now;

            using var sbmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface, BitmapAlphaMode.Premultiplied);
            var sk = ToSkBitmap(sbmp);
            // 帧流统计（诊断级，每 200 投递帧一条，含尺寸，避免 4K 高频刷屏）。
            if (n % 200 == 0) LogDebug?.Invoke($"[capture][count] 已投递={n}");
            FrameArrived?.Invoke(sk);
        }
        catch (Exception ex)
        {
            // 每 100 次报一条，防 4K 高频刷屏；首条必报——"零帧到达"多半死在这里。
            errNo = (int)Interlocked.Increment(ref _convErrors);
            if (errNo <= 3 || errNo % 100 == 0)
                LogError?.Invoke($"[capture][exception] #{errNo}: {ex.Message}");
        }
    }
    private long _convErrors;

    private static unsafe SKBitmap ToSkBitmap(SoftwareBitmap sbmp)
    {
        using var conv = SoftwareBitmap.Convert(sbmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var buf = conv.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buf.CreateReference();
        var desc = buf.GetPlaneDescription(0);
        int stride = desc.Stride;

        // 关键：不能用 Marshal.GetIUnknownForObject(reference)——那是 CsWinRT RCW 包装的 IUnknown，
        // 对未投影的 IMemoryBufferByteAccess 会返回 E_NOINTERFACE(0x80004002→InvalidCastException)。
        // 必须取底层原始 COM 指针 IInspectable.ThisPtr 再 QueryInterface，才命中真实对象。
        // vtable 槽位: [0]QueryInterface [1]AddRef [2]Release [3]GetBuffer。
        Guid iid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        byte* src; uint cap;
        IntPtr raw = ((WinRT.IInspectable)reference).ThisPtr;
        int hr = Marshal.QueryInterface(raw, ref iid, out IntPtr byteAccess);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            void** vtbl = *(void***)byteAccess;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)vtbl[3];
            int hr2 = getBuffer(byteAccess, &src, &cap);
            if (hr2 != 0) Marshal.ThrowExceptionForHR(hr2);
        }
        finally { Marshal.Release(byteAccess); }

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
        UnhookSizeEvents(); // 先解尺寸事件钩，防重建期间误触发
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        _hookThreadLive = false; // 钩线程退出循环并解钩
        Stop();
    }
}
