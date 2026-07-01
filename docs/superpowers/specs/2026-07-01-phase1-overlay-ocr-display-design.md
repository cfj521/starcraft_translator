# 设计：Phase 1 增量 — OCR 韩文就地上屏（悬浮层显示，日志保留）

**日期**：2026-07-01
**状态**：设计待批准
**前置**：Phase 1 代码已完成（分支 `phase1-capture-ocr`，捕获→ROI→OCR→日志）；本设计在其上增量。

## 目标

把 Phase1Probe 识别出的**韩文文本，渲染到悬浮层、贴近其在游戏窗口中的位置**；**日志继续照常写**（保持直观易读）。即"日志 + 上屏"并存，不是替换。

## 范围

**本期做**：
- OCR 每行韩文 → 映射到屏幕坐标 → 悬浮层在该位置显示（白字+黑描边）
- 每帧刷新：清旧文本、摆新文本；空结果清空
- 帧像素 → 屏幕坐标映射（含窗口位置 + 缩放/DPI）
- 日志保留并保持可读

**本期不做**（去向）：
- **翻译**（现只显示识别到的韩文原文；翻译引擎 → Phase 2/3）
- 场景识别 → Phase 1.5
- 变化检测 → Phase 2

## 已确认决策

- **只显示 OCR 韩文原文**（不翻译）。
- **文字位置**：放在每行韩文**原位**（大致盖住/紧贴原文）。
- **日志保留**：OCR 结果继续写 `logs/phase1-ocr.log`，格式保持直观；上屏是额外输出。
- 分支：继续 `phase1-capture-ocr`（未合并/未验收，一起验收）。

## 架构与 Core/App 拆分

**Core（可单测）**：
- `ScreenMapper.MapFrameToScreen(RectangleF frameBounds, Size frameSize, Rectangle windowScreenRect) → RectangleF`：把帧内像素矩形映射为**物理屏幕像素矩形**。逻辑：`scaleX = windowScreenRect.W / frameSize.W`、`scaleY = windowScreenRect.H / frameSize.H`；`screenX = windowScreenRect.Left + frameBounds.X * scaleX`（Y 同理），宽高按 scale 缩放。纯函数 → **TDD**。

**App（WinRT/WPF，手动验收）**：
- `OverlayWindow` 改造：从固定 600×80 单行 → **覆盖主屏的透明、置顶、点穿窗**（`Left=0 Top=0 Width/Height=主屏 DIP 尺寸`，`SystemParameters.PrimaryScreen*`），**移除 Phase 0 的固定测试 TextBlock**，改内含一个 `Canvas`。新增 `void ShowLines(IReadOnlyList<OverlayItem> items)`：清空 Canvas → 对每个 item 造一个带描边 TextBlock（复用 Phase 0 的白字+DropShadow 样式）、`Canvas.SetLeft/SetTop` 摆到其 DIP 坐标。`record OverlayItem(string Text, double DipLeft, double DipTop)`。必须在 UI 线程调用。点穿(`WS_EX_TRANSPARENT|WS_EX_LAYERED`)与置顶保持 Phase 0 机制不变。
- 窗口矩形获取：`NativeMethods.GetWindowRect(hwnd)`（user32 P/Invoke）→ `Rectangle`（物理屏幕像素）。
- DPI 换算：物理屏幕像素 → WPF DIP，用 `VisualTreeHelper.GetDpi(overlay)` 或 `PresentationSource` 的 `CompositionTarget.TransformFromDevice`；`dip = physical / (dpi/96)`。
- `Phase1Probe` 改造：识别循环里，除现有 `_log.Log(...)` 外，收集 `(text, 帧内 bounds)`；每帧结束把这些经 `ScreenMapper` + DPI 换算成 `OverlayItem` 列表，用 `overlay.Dispatcher.Invoke(() => overlay.ShowLines(items))` 编组到 UI 线程更新；空结果 `ShowLines([])` 清屏。Phase1Probe 需注入 `OverlayWindow` 与目标 `hwnd`（Start 时 FindByProcess 得到的 hwnd 存起来供映射）。

## 数据流

```
帧(物理px) → ROI 裁剪 → OCR → 每行(韩文, 帧内 bounds)
  → [日志] _log.Log(...)                （保留）
  → ScreenMapper.MapFrameToScreen(bounds, frameSize, GetWindowRect(hwnd)) → 物理屏幕矩形
  → 物理→DIP 换算 → OverlayItem(text, dipLeft, dipTop)
  → overlay.Dispatcher.Invoke(overlay.ShowLines(items))  → Canvas 摆放
```

## 坐标映射细节

- 捕获帧尺寸 = 被捕获窗口的物理像素尺寸（`GraphicsCaptureItem.Size`，即 `frame.ContentSize`）。
- OCR bounds = 帧内像素。
- `GetWindowRect` = 窗口在屏幕的物理像素矩形。
- 若帧尺寸 == 窗口物理尺寸 → scale≈1，纯平移；保留 scale 以防差异。
- **前提**：SC:R 须**无边框/窗口化**（否则整窗含标题栏，OCR 客户区坐标与整窗有偏移）——本就是项目硬约束。
- 帧尺寸随窗口大小变化，用**当帧的 `frame.Width/Height`** 作 frameSize，不用缓存值。

## 线程

OCR 在线程池线程（CaptureService `CreateFreeThreaded` + Phase1Probe 异步）。更新 WPF UI 必须切 UI 线程：`overlay.Dispatcher.Invoke(...)`。`ShowLines` 只在 UI 线程执行。

## 日志

保留现有 `FileLogger` 与 `[roi] "文本" bounds=... conf=...` 格式（直观)。可加一行汇总（如每帧 `[probe] 上屏 N 行`），便于 debug 对照上屏与识别。

## 测试

- **ScreenMapper（TDD, xUnit）**：帧像素→物理屏幕矩形换算（含 scale≠1、平移、边界）。
- OverlayWindow `ShowLines` / DPI 换算 / Dispatcher / GetWindowRect：需真实窗口，手动验收。

## 验收标准

1. `dotnet test` 全绿（新增 ScreenMapper 单测 + 原有测试）。
2. SC:R 无边框/窗口化 + 自定义游戏列表；托盘开探测：**悬浮层在 Game Name 列韩文位置显示出对应韩文**（白字带描边、点击穿透不挡操作），随游戏刷新更新；同时日志照常记录。
3. 连续多帧不崩、悬浮不闪退、不抢焦点。

## 风险

- **坐标映射准度**（本期主要风险，实机才能定论）：窗口 rect + DPI 缩放 + 客户区 vs 整窗；文字贴不贴得准是验收重点。偏移则微调映射（如引入客户区偏移 `GetClientRect`+`ClientToScreen`）。
- 覆盖主屏的透明窗性能：文本条数少（房间名列几行），可忽略。
- 多显示器 / DPI 混合：本期只保证主屏/单 DPI，多屏留后续。
