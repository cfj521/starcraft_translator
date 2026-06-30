# Phase 1：窗口捕获 + ROI 裁剪 + 韩文 OCR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 跑通 捕获 SC:R 窗口 → 按 rois.json 裁剪 → PaddleOCR(RapidOcrNet) 识别韩文 → 写日志 的管线。

**Architecture:** 纯逻辑(RoiStore)与 OCR 引擎放 Core 可测；WinRT 捕获(CaptureService)与编排(Phase1Probe)放 App 手动验收。OCR 用 RapidOcrNet(OnnxRuntime+SkiaSharp，无 OpenCV)，加载韩文 PP-OCR ONNX 模型。

**Tech Stack:** .NET 10 (net10.0-windows10.0.19041.0)、WPF、Windows.Graphics.Capture(CsWinRT)、RapidOcrNet、SkiaSharp、xUnit。

参考 spec：`docs/superpowers/specs/2026-07-01-phase1-capture-ocr-design.md`

## Global Constraints

- 目标框架 `net10.0-windows10.0.19041.0`；Core 无 WPF/窗口依赖、可单测；OCR 走 `IOcrEngine` 接口。
- OCR 图像类型用 SkiaSharp `SKBitmap`（不引入 System.Drawing 图像/ OpenCV）。
- 模型文件提交进 git `models/`，csproj 拷到输出目录。
- 日志写文件（自写 StreamWriter sink，不引重日志库），路径 `logs/phase1-ocr.log`。
- ROI 坐标相对比例(0~1)、按分辨率分桶；匹配规则：精确 `WxH` → 最近宽高比 → 第一桶+warning。
- 玩家 ID 不翻译属后续 Phase；本期只识别+打印，不翻译。
- 捕获目标 SC:R（进程 `StarCraft.exe`），须窗口化运行。

---

## Task 1: ROI 模型与 RoiStore（Core, TDD）

**Files:**
- Create: `src/ScrTranslator.Core/Roi/RoiModels.cs`
- Create: `src/ScrTranslator.Core/Roi/RoiStore.cs`
- Test: `tests/ScrTranslator.Core.Tests/RoiStoreTests.cs`

**Interfaces:**
- Produces:
  - `record RelRect(double X, double Y, double W, double H)`
  - `record Roi(string Name, string Type, RelRect Rect)`
  - `record SceneBucket(string Resolution, IReadOnlyList<Roi> Rois)`
  - `record RoiConfig(IReadOnlyDictionary<string, IReadOnlyList<SceneBucket>> Scenes)`
  - `static class RoiStore`：
    - `RoiConfig Load(string jsonPath)`
    - `System.Drawing.Rectangle ToPixels(RelRect rel, int width, int height)`（换算+clamp 到 [0,w]×[0,h]）
    - `SceneBucket SelectBucket(RoiConfig cfg, string scene, int width, int height)`

- [ ] **Step 1: 写失败测试 `RoiStoreTests.cs`**

```csharp
using System.Drawing;
using ScrTranslator.Core.Roi;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class RoiStoreTests
{
    [Fact]
    public void ToPixels_ConvertsRelativeToPixels()
    {
        var rect = RoiStore.ToPixels(new RelRect(0.25, 0.5, 0.2, 0.1), 1920, 1080);
        Assert.Equal(new Rectangle(480, 540, 384, 108), rect);
    }

    [Fact]
    public void ToPixels_ClampsWhenExceedingBounds()
    {
        // x=0.9,w=0.2 → 越界，宽度应被裁到边界内
        var rect = RoiStore.ToPixels(new RelRect(0.9, 0.0, 0.2, 0.1), 1000, 1000);
        Assert.Equal(900, rect.X);
        Assert.Equal(100, rect.Width); // 900+200=1100 → 裁到 1000
    }

    [Fact]
    public void SelectBucket_ExactResolutionMatch()
    {
        var cfg = MakeConfig();
        var b = RoiStore.SelectBucket(cfg, "scene_a", 1920, 1080);
        Assert.Equal("1920x1080", b.Resolution);
    }

    [Fact]
    public void SelectBucket_NoExact_PicksNearestAspectRatio()
    {
        var cfg = MakeConfig(); // 含 1920x1080(16:9) 与 1280x960(4:3)
        var b = RoiStore.SelectBucket(cfg, "scene_a", 2560, 1440); // 16:9
        Assert.Equal("1920x1080", b.Resolution);
    }

    [Fact]
    public void Load_ParsesScenesAndRois()
    {
        string json = """
        { "scenes": { "scene_a": { "buckets": [
          { "resolution": "1920x1080", "rois": [
            { "name": "game_name", "type": "RoomList", "rect": [0.18, 0.28, 0.22, 0.40] }
          ] } ] } } }
        """;
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var cfg = RoiStore.Load(path);
        var roi = cfg.Scenes["scene_a"][0].Rois[0];
        Assert.Equal("game_name", roi.Name);
        Assert.Equal(0.22, roi.Rect.W, 3);
    }

    private static RoiConfig MakeConfig()
    {
        var roi = new Roi("game_name", "RoomList", new RelRect(0.1, 0.1, 0.2, 0.2));
        var scenes = new Dictionary<string, IReadOnlyList<SceneBucket>>
        {
            ["scene_a"] = new List<SceneBucket>
            {
                new("1920x1080", new[] { roi }),
                new("1280x960", new[] { roi }),
            }
        };
        return new RoiConfig(scenes);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ScrTranslator.Core.Tests`
Expected: 编译失败（`RoiStore`/`RelRect` 等不存在，CS0246）。红灯。

- [ ] **Step 3: 写 `RoiModels.cs`**

```csharp
namespace ScrTranslator.Core.Roi;

public record RelRect(double X, double Y, double W, double H);
public record Roi(string Name, string Type, RelRect Rect);
public record SceneBucket(string Resolution, IReadOnlyList<Roi> Rois);
public record RoiConfig(IReadOnlyDictionary<string, IReadOnlyList<SceneBucket>> Scenes);
```

- [ ] **Step 4: 写 `RoiStore.cs`**

```csharp
using System.Drawing;
using System.Text.Json;

namespace ScrTranslator.Core.Roi;

public static class RoiStore
{
    public static Rectangle ToPixels(RelRect rel, int width, int height)
    {
        int x = (int)Math.Round(rel.X * width);
        int y = (int)Math.Round(rel.Y * height);
        int w = (int)Math.Round(rel.W * width);
        int h = (int)Math.Round(rel.H * height);
        x = Math.Clamp(x, 0, width);
        y = Math.Clamp(y, 0, height);
        w = Math.Clamp(w, 0, width - x);
        h = Math.Clamp(h, 0, height - y);
        return new Rectangle(x, y, w, h);
    }

    public static SceneBucket SelectBucket(RoiConfig cfg, string scene, int width, int height)
    {
        var buckets = cfg.Scenes[scene];
        string key = $"{width}x{height}";
        foreach (var b in buckets)
            if (b.Resolution == key) return b;

        double target = (double)width / height;
        SceneBucket? best = null;
        double bestDiff = double.MaxValue;
        foreach (var b in buckets)
        {
            var parts = b.Resolution.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int bw) && int.TryParse(parts[1], out int bh) && bh != 0)
            {
                double diff = Math.Abs((double)bw / bh - target);
                if (diff < bestDiff) { bestDiff = diff; best = b; }
            }
        }
        return best ?? buckets[0];
    }

    public static RoiConfig Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var scenes = new Dictionary<string, IReadOnlyList<SceneBucket>>();
        foreach (var sceneProp in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            var bucketList = new List<SceneBucket>();
            foreach (var bucketEl in sceneProp.Value.GetProperty("buckets").EnumerateArray())
            {
                string res = bucketEl.GetProperty("resolution").GetString()!;
                var rois = new List<Roi>();
                foreach (var roiEl in bucketEl.GetProperty("rois").EnumerateArray())
                {
                    var r = roiEl.GetProperty("rect");
                    var rect = new RelRect(
                        r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble());
                    rois.Add(new Roi(
                        roiEl.GetProperty("name").GetString()!,
                        roiEl.GetProperty("type").GetString()!,
                        rect));
                }
                bucketList.Add(new SceneBucket(res, rois));
            }
            scenes[sceneProp.Name] = bucketList;
        }
        return new RoiConfig(scenes);
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/ScrTranslator.Core.Tests`
Expected: PASS（5 个 RoiStore 测试 + Phase 0 的 4 个 WindowStyles 测试 全绿）。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat(core): RoiStore — rois.json 解析/相对像素换算/分辨率分桶(TDD)"
```

---

## Task 2: OCR 接口 + PaddleOcrEngine + 韩文模型（Core，集成测试）

**Files:**
- Create: `src/ScrTranslator.Core/Ocr/OcrModels.cs`
- Create: `src/ScrTranslator.Core/Ocr/PaddleOcrEngine.cs`
- Create: `models/` 下韩文模型（det.onnx / rec.onnx / dict.txt）
- Create: `tests/ScrTranslator.Core.Tests/assets/korean-sample.png`（韩文样图）
- Test: `tests/ScrTranslator.Core.Tests/PaddleOcrEngineTests.cs`
- Modify: `src/ScrTranslator.Core/ScrTranslator.Core.csproj`（加包 + 拷模型）

**Interfaces:**
- Consumes: 无（独立）。
- Produces:
  - `record OcrLine(string Text, RectangleF Bounds, float Confidence)`
  - `record OcrResult(IReadOnlyList<OcrLine> Lines)`
  - `interface IOcrEngine { Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default); }`
  - `class PaddleOcrEngine : IOcrEngine, IDisposable`，构造 `PaddleOcrEngine(string modelsDir)`

- [ ] **Step 1: 加 RapidOcrNet 包并确认包 id**

Run: `dotnet add src/ScrTranslator.Core package RapidOcrNet`
Expected: 成功写入 PackageReference（拉入 Microsoft.ML.OnnxRuntime + SkiaSharp）。
若 `RapidOcrNet` 解析失败，改试 `RapidOCR.Net`（同源 NuGet），记录实际 id。

- [ ] **Step 2: 下载韩文模型到 `models/`**

PowerShell（在仓库根；URL 用 HuggingFace `monkt/paddleocr-onnx`，失败则改用 `SWHL/RapidOCR`）：

```powershell
New-Item -ItemType Directory -Force models | Out-Null
$base = "https://huggingface.co/monkt/paddleocr-onnx/resolve/main"
Invoke-WebRequest "$base/detection/v5/det.onnx"        -OutFile models/det.onnx
Invoke-WebRequest "$base/languages/korean/rec.onnx"    -OutFile models/rec.onnx
Invoke-WebRequest "$base/languages/korean/dict.txt"    -OutFile models/dict.txt
```

验证三文件存在且非空：`Get-ChildItem models | Select-Object Name,Length`。
（cls 角度模型本期不用。若上述路径失效，到 https://huggingface.co/monkt/paddleocr-onnx/tree/main 或 https://huggingface.co/SWHL/RapidOCR/tree/main 找 korean rec.onnx + dict.txt + v5 det.onnx，文件名以实际为准并相应改名为 det/rec/dict。）

- [ ] **Step 3: csproj 拷模型到输出**

在 `src/ScrTranslator.Core/ScrTranslator.Core.csproj` 的 `<Project>` 内加（模型在仓库根 `models/`，用相对链接）：

```xml
  <ItemGroup>
    <None Include="..\..\models\**\*" Link="models\%(RecursiveDir)%(Filename)%(Extension)">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 4: 写 `OcrModels.cs`**

```csharp
using System.Drawing;
using SkiaSharp;

namespace ScrTranslator.Core.Ocr;

public record OcrLine(string Text, RectangleF Bounds, float Confidence);
public record OcrResult(IReadOnlyList<OcrLine> Lines);

public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default);
}
```

- [ ] **Step 5: 写失败集成测试 `PaddleOcrEngineTests.cs`**

```csharp
using ScrTranslator.Core.Ocr;
using SkiaSharp;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class PaddleOcrEngineTests
{
    private static string ModelsDir =>
        Path.Combine(AppContext.BaseDirectory, "models");
    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "assets", "korean-sample.png");

    [Fact]
    public async Task RecognizeAsync_ReadsKoreanFromSampleImage()
    {
        Assert.True(File.Exists(Path.Combine(ModelsDir, "rec.onnx")),
            "缺韩文模型，请先执行 Task2 Step2 下载到 models/");
        using var engine = new PaddleOcrEngine(ModelsDir);
        using var bmp = SKBitmap.Decode(SamplePath);
        var result = await engine.RecognizeAsync(bmp);

        string all = string.Concat(result.Lines.Select(l => l.Text));
        Assert.Contains("로", all); // 样图含韩文字符“로”
        Assert.All(result.Lines, l => Assert.True(l.Confidence > 0.6f));
    }
}
```

样图 `tests/ScrTranslator.Core.Tests/assets/korean-sample.png`：自制一张含韩文“로템”等字样的 PNG（或从 `docs/scenes pictures/custom games listing.png` 裁出 Game Name 区域）。csproj 需拷 assets：
在 `tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj` 加：

```xml
  <ItemGroup>
    <None Include="assets\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
```

- [ ] **Step 6: 跑测试确认失败**

Run: `dotnet test tests/ScrTranslator.Core.Tests --filter PaddleOcrEngineTests`
Expected: 失败（`PaddleOcrEngine` 不存在，CS0246）。红灯。

- [ ] **Step 7: 写 `PaddleOcrEngine.cs`**

```csharp
using System.Drawing;
using RapidOcrNet;
using SkiaSharp;

namespace ScrTranslator.Core.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly RapidOcr _ocr;

    public PaddleOcrEngine(string modelsDir)
    {
        _ocr = new RapidOcr();
        _ocr.InitModels(
            detPath: Path.Combine(modelsDir, "det.onnx"),
            clsPath: null,
            recPath: Path.Combine(modelsDir, "rec.onnx"),
            keysPath: Path.Combine(modelsDir, "dict.txt"));
    }

    public Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var res = _ocr.Detect(image, RapidOcrOptions.Default);
            var lines = new List<OcrLine>();
            foreach (var block in res.TextBlocks)
            {
                float conf = block.CharScores is { Length: > 0 }
                    ? block.CharScores.Average()
                    : 0f;
                lines.Add(new OcrLine(block.Text, ToRect(block.BoxPoints), conf));
            }
            return new OcrResult(lines);
        }, ct);

    private static RectangleF ToRect(SKPointI[] pts)
    {
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        float maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    public void Dispose() => _ocr.Dispose();
}
```

> 注：`RapidOcrOptions.Default`、`block.BoxPoints` 元素类型、`InitModels` 是否接受 `clsPath: null` 以实际 API 为准；编译报错时按 RapidOcrNet 真实签名微调（如 BoxPoints 为 `SKPoint[]` 则改 `ToRect` 参数类型；无 `Default` 则用 `new RapidOcrOptions()`）。这是本任务的已知适配点。

- [ ] **Step 8: 跑测试确认通过**

Run: `dotnet test tests/ScrTranslator.Core.Tests --filter PaddleOcrEngineTests`
Expected: PASS（识别出含“로”的韩文，置信度 > 0.6）。若识别为空/乱码，先确认模型文件正确(det=v5、rec/dict=korean)、样图清晰、必要时放大样图。

- [ ] **Step 9: 提交**

```bash
git add -A
git commit -m "feat(core): PaddleOcrEngine(RapidOcrNet) + 韩文模型 + 集成测试"
```

---

## Task 3: CaptureService（App，Windows.Graphics.Capture）

**Files:**
- Create: `src/ScrTranslator.App/Capture/WindowFinder.cs`
- Create: `src/ScrTranslator.App/Capture/CaptureService.cs`
- Modify: `src/ScrTranslator.App/ScrTranslator.App.csproj`（加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` 供像素拷贝；WinRT Capture API 随 TFM/CsWinRT 已具备）

**Interfaces:**
- Consumes: 无。
- Produces:
  - `static class WindowFinder { IntPtr FindByProcess(string processName); }`
  - `class CaptureService : IDisposable { event Action<SKBitmap>? FrameArrived; bool Start(IntPtr hwnd); void Stop(); }`

> WinRT 互操作复杂、依赖真实窗口，不做单测；编译通过 + Task 4 端到端手动验收。

- [ ] **Step 1: 写 `WindowFinder.cs`**

```csharp
using System.Diagnostics;

namespace ScrTranslator.App.Capture;

public static class WindowFinder
{
    /// <summary>按进程名找主窗口句柄；找不到返回 IntPtr.Zero。</summary>
    public static IntPtr FindByProcess(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            if (p.MainWindowHandle != IntPtr.Zero)
                return p.MainWindowHandle;
        }
        return IntPtr.Zero;
    }
}
```

- [ ] **Step 2: 写 `CaptureService.cs`**

用 Windows.Graphics.Capture + CsWinRT。关键链路：HWND → `GraphicsCaptureItem`(经 interop) → D3D11 设备 → `Direct3D11CaptureFramePool.CreateFreeThreaded` → `FrameArrived` → `SoftwareBitmap.CreateCopyFromSurfaceAsync` → BGRA 像素 → `SKBitmap`。

```csharp
using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace ScrTranslator.App.Capture;

public sealed class CaptureService : IDisposable
{
    public event Action<SKBitmap>? FrameArrived;

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        PreserveSig = false)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public bool Start(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 1) 由 HWND 建 GraphicsCaptureItem（interop）
        var factory = GraphicsCaptureItemInteropFactory();
        Guid itemGuid = typeof(GraphicsCaptureItem).GUID;
        IntPtr itemPtr = factory.CreateForWindow(hwnd, ref itemGuid);
        var item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        // 2) 建 D3D11 设备并包装为 IDirect3DDevice
        _device = CreateDirect3DDevice();

        // 3) 帧池（FreeThreaded 免 DispatcherQueue）
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
        return true;
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        using var sbmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        var skbmp = ToSkBitmap(sbmp);
        if (skbmp is not null) FrameArrived?.Invoke(skbmp);
    }

    private static unsafe SKBitmap ToSkBitmap(SoftwareBitmap sbmp)
    {
        using var conv = SoftwareBitmap.Convert(sbmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var buf = conv.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buf.CreateReference();
        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* src, out uint cap);
        Buffer.MemoryCopy(src, (void*)sk.GetPixels(), sk.ByteCount, cap);
        return sk;
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess { unsafe void GetBuffer(out byte* buffer, out uint capacity); }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        // 用 D3D11CreateDevice 建设备 → 取 IDXGIDevice →
        // CreateDirect3D11DeviceFromDXGIDevice → Marshal.GetObjectForIUnknown as IDirect3DDevice
        throw new NotImplementedException("见 Step 2 说明，按 CsWinRT 互操作实现");
    }

    private static IGraphicsCaptureItemInterop GraphicsCaptureItemInteropFactory()
    {
        // 取 GraphicsCaptureItem 的 activation factory 并 QI 到 IGraphicsCaptureItemInterop
        throw new NotImplementedException("见 Step 2 说明");
    }

    public void Stop()
    {
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _device = null;
    }

    public void Dispose() => Stop();
}
```

> **实现说明（本任务的核心适配点，CsWinRT 互操作样板）**：上面 `CreateDirect3DDevice()` 与 `GraphicsCaptureItemInteropFactory()`、像素拷贝是 WinRT 互操作样板，参考微软官方 `Win32CaptureSample` / `ScreenCapture` 示例落实：
> - D3D 设备：`D3D11CreateDevice(BGRA_SUPPORT)` → `IDXGIDevice` → `CreateDirect3D11DeviceFromDXGIDevice` → `Marshal.GetObjectForIUnknown(ptr)` 转 `IDirect3DDevice`。
> - interop 工厂：`WindowsRuntimeMarshal`/`GetActivationFactory(typeof(GraphicsCaptureItem))` QI 到 `IGraphicsCaptureItemInterop`。
> - 像素拷贝：`SoftwareBitmap.CopyToBuffer(sk.GetPixels()…)` 直接写入 `SKBitmap` 像素缓冲（替换上面 `GetPixelArray` 占位）。
> 该任务以"能编译 + Task 4 端到端真实捕获出非空帧"为验收，互操作细节在实现期对照官方示例敲定。

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/ScrTranslator.App`
Expected: 成功（接口与样板编译通过；互操作实现完成后无 NotImplemented 残留）。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "feat(app): CaptureService — Graphics.Capture 捕获窗口帧转 SKBitmap"
```

---

## Task 4: 文件日志 + Phase1Probe + 托盘菜单 + rois.json（App，端到端）

**Files:**
- Create: `src/ScrTranslator.App/Logging/FileLogger.cs`（轻量 StreamWriter sink）
- Create: `src/ScrTranslator.App/Phase1Probe.cs`
- Create: `src/ScrTranslator.App/rois.json`（拷到输出）
- Modify: `src/ScrTranslator.App/Services/TrayService.cs`（加"开始/停止 Phase1 探测"菜单）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（DI 注册 CaptureService/PaddleOcrEngine/Phase1Probe）
- Modify: `src/ScrTranslator.App/ScrTranslator.App.csproj`（拷 rois.json）

**Interfaces:**
- Consumes: `CaptureService`(T3)、`PaddleOcrEngine`/`IOcrEngine`(T2)、`RoiStore`(T1)。
- Produces: `class Phase1Probe { void Start(); void Stop(); }`

- [ ] **Step 1: 写 `FileLogger.cs`**

```csharp
namespace ScrTranslator.App.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _w = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Log(string msg)
    {
        lock (_lock) _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose() => _w.Dispose();
}
```

- [ ] **Step 2: 写 `rois.json`（场景1 单 ROI，数值实测微调）**

`src/ScrTranslator.App/rois.json`：

```json
{
  "scenes": {
    "custom_games_listing": {
      "buckets": [
        {
          "resolution": "1920x1080",
          "rois": [
            { "name": "game_name_column", "type": "RoomList", "rect": [0.18, 0.27, 0.24, 0.42] }
          ]
        }
      ]
    }
  }
}
```

csproj 加拷贝（`src/ScrTranslator.App/ScrTranslator.App.csproj`）：

```xml
  <ItemGroup>
    <None Update="rois.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
```

- [ ] **Step 3: 写 `Phase1Probe.cs`**

```csharp
using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Logging;
using ScrTranslator.Core.Ocr;
using ScrTranslator.Core.Roi;
using SkiaSharp;

namespace ScrTranslator.App;

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly FileLogger _log;
    private readonly RoiConfig _cfg;
    private const string Scene = "custom_games_listing";
    private DateTime _lastShot = DateTime.MinValue;
    private volatile bool _busy;

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, string roisJsonPath)
    {
        _capture = capture; _ocr = ocr; _log = log;
        _cfg = RoiStore.Load(roisJsonPath);
        _capture.FrameArrived += OnFrame;
    }

    public void Start()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) { _log.Log("[probe] 未找到 StarCraft 窗口"); return; }
        if (!_capture.Start(hwnd)) _log.Log("[probe] 捕获启动失败");
        else _log.Log("[probe] 已开始捕获");
    }

    public void Stop() { _capture.Stop(); _log.Log("[probe] 已停止"); }

    private async void OnFrame(SKBitmap frame)
    {
        // 节流 ~400ms + 防重入
        if (_busy || (DateTime.Now - _lastShot).TotalMilliseconds < 400) { frame.Dispose(); return; }
        _busy = true; _lastShot = DateTime.Now;
        try
        {
            var bucket = RoiStore.SelectBucket(_cfg, Scene, frame.Width, frame.Height);
            foreach (var roi in bucket.Rois)
            {
                Rectangle px = RoiStore.ToPixels(roi.Rect, frame.Width, frame.Height);
                if (px.Width <= 0 || px.Height <= 0) continue;
                using var sub = new SKBitmap(px.Width, px.Height);
                frame.ExtractSubset(sub, new SKRectI(px.Left, px.Top, px.Right, px.Bottom));
                var res = await _ocr.RecognizeAsync(sub);
                foreach (var line in res.Lines)
                    _log.Log($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                if (res.Lines.Count == 0)
                    _log.Log($"[{roi.Name}] (空)");
            }
        }
        catch (Exception ex) { _log.Log($"[probe] 异常: {ex.Message}"); }
        finally { frame.Dispose(); _busy = false; }
    }

    public void Dispose() { _capture.FrameArrived -= OnFrame; }
}
```

- [ ] **Step 4: 托盘加菜单项（`TrayService.cs`）**

在 `Initialize()` 的菜单构建里，"退出"之前加：

```csharp
        var probeItem = new MenuItem { Header = "开始/停止 Phase1 探测" };
        probeItem.Click += (_, _) => _toggleProbe?.Invoke();
        menu.Items.Add(probeItem);
        menu.Items.Add(new Separator());
```

并给 `TrayService` 加一个回调字段 + 构造或属性注入：

```csharp
    public Action? _toggleProbe; // 由 App 启动时赋值：在 Start/Stop Phase1Probe 间切换
```

> 保持 Phase 0 既有"显示/隐藏悬浮窗""退出"两项不变，新项插在其间。

- [ ] **Step 5: App.xaml.cs DI 接线**

`OnStartup` 中追加注册与探测开关（在现有 OverlayWindow/TrayService 注册之后）：

```csharp
        string baseDir = AppContext.BaseDirectory;
        builder.Services.AddSingleton(new FileLogger(Path.Combine(baseDir, "logs", "phase1-ocr.log")));
        builder.Services.AddSingleton<IOcrEngine>(_ =>
            new PaddleOcrEngine(Path.Combine(baseDir, "models")));
        builder.Services.AddSingleton<CaptureService>();
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            Path.Combine(baseDir, "rois.json")));
```

构建 host 后、显示 overlay/tray 时，接线托盘开关：

```csharp
        var probe = _host.Services.GetRequiredService<Phase1Probe>();
        bool running = false;
        _trayService.Initialize();
        _trayService._toggleProbe = () =>
        {
            if (running) { probe.Stop(); } else { probe.Start(); }
            running = !running;
        };
```

需要的 using：`ScrTranslator.App.Logging; ScrTranslator.Core.Ocr; ScrTranslator.App.Capture;`。

- [ ] **Step 6: 整解决方案构建**

Run: `dotnet build`
Expected: 成功，0 错误。

- [ ] **Step 7: 端到端手动验收**

1. 窗口化启动 SC:R，进"自定义游戏"列表界面。
2. `dotnet run --project src/ScrTranslator.App`。
3. 托盘右键 →"开始/停止 Phase1 探测"。
4. 看 `src/ScrTranslator.App/bin/Debug/net10.0-windows10.0.19041.0/logs/phase1-ocr.log`：
   - 应**稳定打印出 Game Name 列的韩文房间名** + bounds + conf，连续多帧不崩。
5. 若 ROI 框偏/识别空：调 `rois.json` 的 rect 数值(相对当前分辨率)，重跑。
6. 验收通过后再次点菜单停止。

- [ ] **Step 8: 更新 ROADMAP 并提交**

`docs/ROADMAP.md` 的 Phase 1 章节，把对应项 `[ ]`→`[x]`（窗口句柄、Graphics.Capture、区域、OCR 韩文、打印）。

```bash
git add -A
git commit -m "feat(app): Phase1Probe 端到端(捕获→ROI→OCR→日志)+托盘开关+rois.json；更新ROADMAP"
```

---

## 验收（对应 spec）

1. `dotnet test` 全绿：RoiStore 单测(T1) + PaddleOcrEngine 对内置韩文样图集成测试(T2)。
2. SC:R 自定义游戏列表场景，托盘开启探测，`logs/phase1-ocr.log` 稳定打印正确韩文房间名 + 位置 + 置信度(T4 Step7)。
3. 连续多帧不崩、不卡死捕获线程(节流+防重入，T4 Phase1Probe)。

## 已知适配点（实现期解决，非占位）

- RapidOcrNet 精确包 id / `RapidOcrOptions` / `BoxPoints` 元素类型 / `InitModels(clsPath:null)` —— 按真实 API 微调(T2 Step1/7)。
- Graphics.Capture 的 CsWinRT 互操作样板(D3D 设备、interop 工厂、像素拷贝) —— 对照微软官方示例落实(T3 Step2)。
- 韩文模型下载 URL 失效时改用 SWHL/RapidOCR(T2 Step2)。
- rois.json 的 rect 具体数值靠真实窗口实测微调(T4 Step7)。
