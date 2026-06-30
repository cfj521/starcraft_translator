# Phase 0 设计 — 工程骨架 + 透明置顶悬浮窗

> 日期：2026-07-01　|　对应 [ROADMAP.md](../../ROADMAP.md) Phase 0
> 目标：地基。一个能浮在游戏上、点击穿透的透明置顶窗 + 系统托盘退出。

## 1. 目标与范围

**做什么**
- 建立解决方案与核心三工程骨架（.NET 10）。
- WPF 透明、无边框、置顶悬浮窗，窗口里画一行测试中文。
- 通过 P/Invoke 实现**点击穿透**（鼠标事件透到下层窗口）。
- 系统托盘图标：显示/隐藏悬浮窗、退出。
- 用 DI/Host 组织启动。
- 建立单元测试工程并跑通至少一个真实测试（点击穿透位运算）。
- `git init` 建本地仓库。

**不做什么（留给后续 Phase）**
- 不做屏幕捕获、OCR、翻译（Phase 1+）。
- 不做 Overlay/Common 独立工程拆分（按需在后续 Phase 拆）。
- 不做配置窗口、校准、热键、多区域（Phase 4）。
- 悬浮窗暂不跟随游戏窗口位置（Phase 1+ 接入捕获后再做）。

## 2. 技术选型（Phase 0 相关）

| 维度 | 选型 |
|------|------|
| 运行时 | .NET 10 (LTS)，已确认本机 SDK 10.0.301 |
| 目标框架 | `net10.0-windows10.0.19041.0` |
| UI | WPF（`UseWPF=true`） |
| 托盘 | `H.NotifyIcon.Wpf`（NuGet） |
| DI/启动 | `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection` |
| 测试 | xUnit |

## 3. 工程结构

```
starcraft_translator/
├─ ScrTranslator.sln
├─ src/
│  ├─ ScrTranslator.App/          # WPF 启动、悬浮窗、托盘
│  │   TFM: net10.0-windows10.0.19041.0  UseWPF=true  OutputType=WinExe
│  └─ ScrTranslator.Core/         # 业务逻辑（无 UI 依赖）
│      TFM: net10.0-windows10.0.19041.0
└─ tests/
   └─ ScrTranslator.Core.Tests/   # xUnit
       TFM: net10.0-windows10.0.19041.0
```

- 引用关系：`App → Core`；`Core.Tests → Core`。
- Core 用 `-windows` 框架是为后续放 Capture/OCR（WinRT）；项目本就只在 Windows 编译/运行/测试，无损失。
- Overlay/Common 等工程在后续 Phase 按需拆分（TECH_DESIGN 第 5 节为最终形态）。

## 4. 组件设计

### 4.1 Core：点击穿透位运算（可测纯逻辑）
为让 Phase 0 也有真正的 TDD 单元，把"在原扩展样式上叠加点击穿透位"的纯位运算放到 Core：

```csharp
namespace ScrTranslator.Core.Interop;

// Win32 扩展窗口样式常量与点击穿透位计算（纯逻辑，可单测）
public static class WindowStyles
{
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED     = 0x00080000;

    // 在已有扩展样式上叠加“点击穿透”所需位；幂等
    public static int ApplyClickThrough(int exStyle)
        => exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED;

    // 移除点击穿透位（为后续“校准模式需可交互”预留；Phase 0 仅测试用）
    public static int RemoveClickThrough(int exStyle)
        => exStyle & ~(WS_EX_TRANSPARENT | WS_EX_LAYERED);
}
```

### 4.2 App：原生互操作封装
`ScrTranslator.App/Interop/NativeMethods.cs`：封装 `GetWindowLong`/`SetWindowLong`（`GWL_EXSTYLE`）的 P/Invoke。提供 `EnableClickThrough(IntPtr hwnd)`：读当前 exStyle → `WindowStyles.ApplyClickThrough` → 写回。

> 注意 32/64 位：使用 `GetWindowLongPtr`/`SetWindowLongPtr` 以兼容 64 位；通过 `WindowInteropHelper` 取 HWND。

### 4.3 App：悬浮窗 OverlayWindow
- XAML 属性：`AllowsTransparency=True`、`WindowStyle=None`、`Topmost=True`、`Background=Transparent`、`ShowInTaskbar=False`、`ResizeMode=NoResize`。
- 在 `SourceInitialized` 事件中调用 `NativeMethods.EnableClickThrough(hwnd)`。
- 内容：一行测试中文（如 `星际翻译 测试 가나다`），带描边/阴影（`DropShadowEffect` 或双 TextBlock 叠加描边）确保任意背景可读。
- 初始尺寸/位置：屏幕上方居中的一个固定矩形即可（Phase 0 不跟随游戏窗口）。

### 4.4 App：托盘 TrayService
- 用 `H.NotifyIcon.TaskbarIcon`，菜单项：`显示/隐藏悬浮窗`、`退出`。
- `退出` → `Application.Current.Shutdown()`。
- 一个临时图标（可用纯色生成的 .ico 或内置资源）。

### 4.5 App：启动与 DI
- `App.xaml.cs` 的 `OnStartup`：构建 `IHost`，向容器注册 `OverlayWindow`、`TrayService`，启动后显示悬浮窗与托盘。
- `ShutdownMode=OnExplicitShutdown`（关掉悬浮窗不退出进程，由托盘“退出”控制）。

## 5. 数据流

```
App.OnStartup
  └─ build Host (DI: OverlayWindow, TrayService)
       ├─ OverlayWindow.Show()
       │     └─ SourceInitialized → NativeMethods.EnableClickThrough(hwnd)
       │            └─ GetWindowLongPtr → WindowStyles.ApplyClickThrough → SetWindowLongPtr
       └─ TrayService.Initialize()  (托盘图标 + 菜单)
             ├─ “显示/隐藏” → OverlayWindow.Show()/Hide()
             └─ “退出” → Application.Shutdown()
```

## 6. 测试策略

**单元测试（xUnit，自动）— `WindowStylesTests`**
- `ApplyClickThrough` 正确置位 `WS_EX_TRANSPARENT` 与 `WS_EX_LAYERED`。
- 保留原有其它位（如传入已有样式，不丢失）。
- 幂等：连续两次 `ApplyClickThrough` 结果相同。
- `RemoveClickThrough` 清掉这两位且不影响其它位。

**手动验收（需人工观察，ROADMAP 规定）**
- 开记事本，悬浮窗中文浮在其上、始终置顶。
- 鼠标在中文区域点击/拖动，事件点穿到记事本（记事本获得焦点/响应）。
- 托盘“显示/隐藏”切换悬浮窗；“退出”结束进程。

## 7. 依赖（NuGet）

| 工程 | 包 |
|------|----|
| App | `H.NotifyIcon.Wpf`、`Microsoft.Extensions.Hosting` |
| Core.Tests | `xunit`、`xunit.runner.visualstudio`、`Microsoft.NET.Test.Sdk` |

## 8. 验收标准（Definition of Done）

- [ ] `dotnet build ScrTranslator.sln` 成功（0 错误）。
- [ ] `dotnet test` 通过（`WindowStylesTests` 全绿）。
- [ ] `dotnet run --project src/ScrTranslator.App` 启动后：悬浮窗中文置顶可见、点击穿透、托盘可显隐/退出（手动验收）。
- [ ] `git init` 完成，首次提交包含骨架与 spec。

## 9. 风险与备注

- **点击穿透 + WS_EX_LAYERED**：分层窗口需确保有内容渲染，否则可能不可见；用非全透明的文字/描边即可。
- **托盘图标资源**：Phase 0 用占位 .ico，正式图标留待 Phase 5。
- **AllowsTransparency 性能**：Phase 0 内容极少，无碍；大面积透明的性能在 Phase 5 调优。
