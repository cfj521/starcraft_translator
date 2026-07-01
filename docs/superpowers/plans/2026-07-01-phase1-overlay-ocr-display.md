# Phase 1 增量：OCR 韩文就地上屏 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Phase1Probe 识别出的韩文渲染到悬浮层、贴近其在游戏窗口中的位置（日志保留）。

**Architecture:** Core 加纯函数 `ScreenMapper`（帧像素→物理屏幕矩形，TDD）；App 把 `OverlayWindow` 改为覆盖主屏的透明点穿 Canvas + `ShowLines`；`CaptureService` 暴露 `Hwnd`；`Phase1Probe` 在日志之外，把每行 OCR 经 子图→帧→屏幕 映射后 `Dispatcher.InvokeAsync` 到 OverlayWindow 显示。

**Tech Stack:** .NET 10 WPF、Windows.Graphics.Capture、SkiaSharp、xUnit。

参考 spec：`docs/superpowers/specs/2026-07-01-phase1-overlay-ocr-display-design.md`

## Global Constraints

- 只显示 OCR 韩文原文（不翻译；翻译 → Phase 2）。日志继续照常写，格式保持直观。
- 坐标链（**易漏**）：`OcrLine.Bounds` 相对**裁剪子图**；映射前必须 `+ 该 ROI 的 roiPx.Left/Top`（`RoiStore.ToPixels` 结果）得到帧内坐标，再 `ScreenMapper.MapFrameToScreen`。
- **DPI 换算在 `ShowLines`（UI 线程）内做**（`VisualTreeHelper.GetDpi` 须 UI 线程调用）；`OverlayItem` 携带**物理屏幕像素**坐标，`ScreenMapper` 保持纯物理、可测。（精化 spec：spec 把 DIP 换算放在 Phase1Probe，本计划移入 ShowLines 以满足 UI 线程要求。）
- UI 更新用 `overlay.Dispatcher.InvokeAsync(...)`（不阻塞 OCR 线程）。
- 悬浮层点击穿透 `WS_EX_TRANSPARENT|WS_EX_LAYERED` 复用 Phase 0 `NativeMethods.EnableClickThrough`，机制不变；SC:R 须无边框/窗口化。
- 目标框架 `net10.0-windows10.0.19041.0`；Core 无 UI 依赖。

---

## Task 1: ScreenMapper（Core, TDD）

**Files:**
- Create: `src/ScrTranslator.Core/ScreenMapper.cs`
- Test: `tests/ScrTranslator.Core.Tests/ScreenMapperTests.cs`

**Interfaces:**
- Produces: `static RectangleF ScreenMapper.MapFrameToScreen(RectangleF frameBounds, Size frameSize, Rectangle windowScreenRect)`（命名空间 `ScrTranslator.Core`，与 `RoiStore` 一致）

- [ ] **Step 1: 写失败测试 `ScreenMapperTests.cs`**

```csharp
using System.Drawing;
using ScrTranslator.Core;
using Xunit;

namespace ScrTranslator.Core.Tests;

public class ScreenMapperTests
{
    [Fact]
    public void MapFrameToScreen_TranslatesAndScales()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(10, 10, 20, 20), new Size(100, 100), new Rectangle(500, 300, 200, 200));
        Assert.Equal(520f, r.X);
        Assert.Equal(320f, r.Y);
        Assert.Equal(40f, r.Width);
        Assert.Equal(40f, r.Height);
    }

    [Fact]
    public void MapFrameToScreen_ScaleOne_PureTranslation()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(5, 7, 10, 10), new Size(800, 600), new Rectangle(100, 50, 800, 600));
        Assert.Equal(105f, r.X);
        Assert.Equal(57f, r.Y);
    }

    [Fact]
    public void MapFrameToScreen_ZeroFrameSize_NoDivideByZero()
    {
        var r = ScreenMapper.MapFrameToScreen(
            new RectangleF(1, 1, 1, 1), new Size(0, 0), new Rectangle(0, 0, 100, 100));
        Assert.Equal(1f, r.X);
        Assert.Equal(1f, r.Y);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ScrTranslator.Core.Tests`
Expected: 编译失败（`ScreenMapper` 不存在，CS0246）。红灯。

- [ ] **Step 3: 写 `ScreenMapper.cs`**

```csharp
using System.Drawing;

namespace ScrTranslator.Core;

/// <summary>把帧内像素矩形映射为物理屏幕像素矩形（纯逻辑，可单测）。DIP 换算在 UI 层做。</summary>
public static class ScreenMapper
{
    public static RectangleF MapFrameToScreen(RectangleF frameBounds, Size frameSize, Rectangle windowScreenRect)
    {
        float sx = frameSize.Width == 0 ? 1f : (float)windowScreenRect.Width / frameSize.Width;
        float sy = frameSize.Height == 0 ? 1f : (float)windowScreenRect.Height / frameSize.Height;
        return new RectangleF(
            windowScreenRect.Left + frameBounds.X * sx,
            windowScreenRect.Top + frameBounds.Y * sy,
            frameBounds.Width * sx,
            frameBounds.Height * sy);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ScrTranslator.Core.Tests`
Expected: PASS（3 个新 ScreenMapper + 原有测试全绿）。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): ScreenMapper 帧像素→物理屏幕矩形映射(TDD)"
```

---

## Task 2: OverlayWindow 改全屏 Canvas + ShowLines（App）

**Files:**
- Modify: `src/ScrTranslator.App/OverlayWindow.xaml`（整体替换）
- Modify: `src/ScrTranslator.App/OverlayWindow.xaml.cs`（整体替换）

**Interfaces:**
- Produces:
  - `record OverlayItem(string Text, double ScreenLeft, double ScreenTop)`（物理屏幕像素坐标）
  - `void OverlayWindow.ShowLines(IReadOnlyList<OverlayItem> items)`（UI 线程调用，内部物理→DIP 换算）

> 无单测（WPF 窗口，需真实渲染）；编译 + Task 3 端到端手动验收覆盖。

- [ ] **Step 1: 替换 `OverlayWindow.xaml`**

```xml
<Window x:Class="ScrTranslator.App.OverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ScrTranslator Overlay"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        Topmost="True"
        ShowInTaskbar="False"
        ResizeMode="NoResize"
        WindowStartupLocation="Manual"
        Left="0" Top="0">
    <Canvas x:Name="RootCanvas"/>
</Window>
```

- [ ] **Step 2: 替换 `OverlayWindow.xaml.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ScrTranslator.App.Interop;

namespace ScrTranslator.App;

/// <summary>一条上屏文本：物理屏幕像素坐标（左上角）。</summary>
public record OverlayItem(string Text, double ScreenLeft, double ScreenTop);

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;   // DIP
        Height = SystemParameters.PrimaryScreenHeight; // DIP
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableClickThrough(hwnd);
    }

    /// <summary>清屏并按物理屏幕坐标（内部换算为 DIP）摆放文本。必须在 UI 线程调用。</summary>
    public void ShowLines(IReadOnlyList<OverlayItem> items)
    {
        RootCanvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var it in items)
        {
            var tb = new TextBlock
            {
                Text = it.Text,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
            };
            Canvas.SetLeft(tb, it.ScreenLeft / dpi.DpiScaleX);
            Canvas.SetTop(tb, it.ScreenTop / dpi.DpiScaleY);
            RootCanvas.Children.Add(tb);
        }
    }
}
```

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/ScrTranslator.App`
Expected: 0 错误（原有 CS9191 warning 可留）。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "feat(app): OverlayWindow 改全屏透明 Canvas + ShowLines(物理坐标→DIP)"
```

---

## Task 3: CaptureService.Hwnd + GetWindowRect + Phase1Probe 上屏接线（App, 端到端）

**Files:**
- Modify: `src/ScrTranslator.App/Capture/CaptureService.cs`（加 `Hwnd` 属性）
- Modify: `src/ScrTranslator.App/Interop/NativeMethods.cs`（加 `GetWindowScreenRect`）
- Modify: `src/ScrTranslator.App/Phase1Probe.cs`（上屏接线）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（Phase1Probe 注入 OverlayWindow）

**Interfaces:**
- Consumes: `ScreenMapper.MapFrameToScreen`（T1）、`OverlayWindow.ShowLines`/`OverlayItem`（T2）
- Produces: `CaptureService.Hwnd`(IntPtr)、`NativeMethods.GetWindowScreenRect(IntPtr)→Rectangle`

- [ ] **Step 1: CaptureService 暴露 Hwnd**

在 `src/ScrTranslator.App/Capture/CaptureService.cs` 加属性（与 `_device` 等字段并列处加公开属性）：

```csharp
    public IntPtr Hwnd { get; private set; }
```

并在 `Start(IntPtr hwnd)` 方法体开头（`if (hwnd == IntPtr.Zero) return false;` 之后）加：

```csharp
        Hwnd = hwnd;
```

- [ ] **Step 2: NativeMethods 加 GetWindowScreenRect**

在 `src/ScrTranslator.App/Interop/NativeMethods.cs` 的类内追加：

```csharp
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
```

（文件顶部已有 `using System.Runtime.InteropServices;`。）

- [ ] **Step 3: Phase1Probe 上屏接线**

`src/ScrTranslator.App/Phase1Probe.cs` 整体替换为（在现有基础上加 OverlayWindow 注入 + 上屏，保留日志与 Interlocked/节流/Dispose）：

```csharp
using System.Drawing;
using ScrTranslator.App.Capture;
using ScrTranslator.App.Interop;
using ScrTranslator.App.Logging;
using ScrTranslator.Core;
using ScrTranslator.Core.Ocr;
using SkiaSharp;

namespace ScrTranslator.App;

public sealed class Phase1Probe : IDisposable
{
    private readonly CaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly FileLogger _log;
    private readonly OverlayWindow _overlay;
    private readonly RoiConfig _cfg;
    private const string Scene = "custom_games_listing";
    private DateTime _lastShot = DateTime.MinValue;
    private int _busy; // 0=空闲 1=处理中

    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, OverlayWindow overlay, string roisJsonPath)
    {
        _capture = capture; _ocr = ocr; _log = log; _overlay = overlay;
        _cfg = RoiStore.Load(roisJsonPath);
        _capture.FrameArrived += OnFrame;
    }

    public bool Start()
    {
        var hwnd = WindowFinder.FindByProcess("StarCraft");
        if (hwnd == IntPtr.Zero) { _log.Log("[probe] 未找到 StarCraft 窗口"); return false; }
        if (!_capture.Start(hwnd)) { _log.Log("[probe] 捕获启动失败"); return false; }
        _log.Log("[probe] 已开始捕获");
        return true;
    }

    public void Stop()
    {
        _capture.Stop();
        _log.Log("[probe] 已停止");
        _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(System.Array.Empty<OverlayItem>()));
    }

    private async void OnFrame(SKBitmap frame)
    {
        if ((DateTime.Now - _lastShot).TotalMilliseconds < 400
            || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            frame.Dispose();
            return;
        }
        _lastShot = DateTime.Now;
        try
        {
            var frameSize = new Size(frame.Width, frame.Height);
            var winRect = NativeMethods.GetWindowScreenRect(_capture.Hwnd);
            if (winRect.IsEmpty)
            {
                _log.Log("[probe] 获取窗口矩形失败，跳过本帧上屏");
                return; // finally 仍会 Dispose(frame) 并复位 _busy
            }
            var items = new List<OverlayItem>();
            var bucket = RoiStore.SelectBucket(_cfg, Scene, frame.Width, frame.Height);
            foreach (var roi in bucket.Rois)
            {
                Rectangle px = RoiStore.ToPixels(roi.Rect, frame.Width, frame.Height);
                if (px.Width <= 0 || px.Height <= 0) continue;
                using var sub = new SKBitmap(px.Width, px.Height);
                if (!frame.ExtractSubset(sub, new SKRectI(px.Left, px.Top, px.Right, px.Bottom)))
                {
                    _log.Log($"[{roi.Name}] 裁剪失败 rect={px}");
                    continue;
                }
                var res = await _ocr.RecognizeAsync(sub);
                foreach (var line in res.Lines)
                {
                    _log.Log($"[{roi.Name}] \"{line.Text}\" bounds={line.Bounds} conf={line.Confidence:F2}");
                    // 子图坐标 + ROI 帧内偏移 → 帧内坐标
                    var frameBounds = new RectangleF(
                        line.Bounds.X + px.Left, line.Bounds.Y + px.Top,
                        line.Bounds.Width, line.Bounds.Height);
                    var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
                    items.Add(new OverlayItem(line.Text, scr.X, scr.Y));
                }
                if (res.Lines.Count == 0)
                    _log.Log($"[{roi.Name}] (空)");
            }
            _log.Log($"[probe] 上屏 {items.Count} 行");
            _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(items));
        }
        catch (Exception ex) { _log.Log($"[probe] 异常: {ex.Message}"); }
        finally { frame.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }

    // 仅退订帧回调；CaptureService 是 DI 单例，其 Stop/Dispose 由容器管理（有意设计）。
    public void Dispose() { _capture.FrameArrived -= OnFrame; }
}
```

- [ ] **Step 4: App.xaml.cs 更新 Phase1Probe 注册（加 OverlayWindow 参数）**

`src/ScrTranslator.App/App.xaml.cs` 中 Phase1Probe 的 `AddSingleton` 工厂改为多注入一个 `OverlayWindow`：

```csharp
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            Path.Combine(baseDir, "rois.json")));
```

（`OverlayWindow` 已在 Phase 0 注册为单例，直接取。其余不变。）

- [ ] **Step 5: 整解构建 + 回归测试**

Run: `dotnet build`
Expected: 0 错误。
Run: `dotnet test`
Expected: 全绿（新增 ScreenMapper 3 + 原有 = 13）。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat(app): OCR 韩文上屏(Phase1Probe→ScreenMapper→OverlayWindow)；CaptureService.Hwnd + GetWindowScreenRect"
```

- [ ] **Step 7: 端到端手动验收（需真实 SC:R）**

1. SC:R 无边框/窗口化，进"自定义游戏"列表。
2. `dotnet run --project src/ScrTranslator.App`。
3. 托盘右键 →"开始/停止 Phase1 探测"。
4. 悬浮层应在 **Game Name 列韩文位置**显示对应韩文（白字带阴影、不挡鼠标操作），随游戏刷新；`logs/phase1-ocr.log` 同时记录。
5. 若文字位置偏移：先看日志 bounds 是否合理；再核对是否 SC:R 无边框、是否单 DPI/主屏；必要时把映射基准从 `GetWindowRect`（整窗）改 `GetClientRect+ClientToScreen`（客户区）。
6. 停止 → 悬浮层清空。

---

## 验收（对应 spec）

1. `dotnet test` 全绿（ScreenMapper TDD + 原有）→ Task 1/3。
2. 悬浮层在韩文位置显示韩文、点穿不挡、随刷新更新、日志照常 → Task 3 Step 7（手动，需 SC:R）。
3. 连续多帧不崩、不抢焦点 → Interlocked/节流/InvokeAsync（Task 3）。

## 已知适配点（实现期/实机解决，非占位）

- 坐标映射准度（整窗 vs 客户区、DPI、多屏）→ Task 3 Step 5/7 实机微调。
- `VisualTreeHelper.GetDpi(this)` 需窗口已加载；OverlayWindow Phase 0 起即 Show，`ShowLines` 在其后调用，满足。
