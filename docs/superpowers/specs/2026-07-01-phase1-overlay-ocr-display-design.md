# 设计：Phase 1 增量 — OCR 韩文就地上屏（悬浮层显示，日志保留）

**日期**：2026-07-01
**状态**：设计待批准
**前置**：Phase 1 代码已完成（分支 `phase1-capture-ocr`，捕获→ROI→OCR→日志）；本设计在其上增量。

## 目标

把 Phase1Probe 识别出的**韩文文本，渲染到悬浮层、贴近其在游戏窗口中的位置**（白字 + 黑色 `DropShadowEffect` 阴影——复用 Phase 0 已有效果，非真正描边）；**日志继续照常写**（保持直观易读）。即"日志 + 上屏"并存，不是替换。

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
- `OverlayWindow` 改造：从固定 600×80 单行 → **覆盖主屏的透明、置顶、点穿窗**（`Left=0 Top=0 Width/Height=主屏 DIP 尺寸`，`SystemParameters.PrimaryScreen*`），**移除 Phase 0 的固定测试 TextBlock**，改内含一个 `Canvas`。新增 `void ShowLines(IReadOnlyList<OverlayItem> items)`：清空 Canvas → 对每个 item 造一个带描边 TextBlock（复用 Phase 0 的白字+DropShadow 样式）、`Canvas.SetLeft/SetTop` 摆到其 DIP 坐标。`record OverlayItem(string Text, double DipLeft, double DipTop)`——本期只用左上角定位、丢弃宽高（TextBlock 按内容自适应即可显示原文；Phase 2 若需精确覆盖原文区域再补宽高字段）。必须在 UI 线程调用。点穿(`WS_EX_TRANSPARENT|WS_EX_LAYERED`)与置顶保持 Phase 0 机制不变。
- 窗口矩形获取：`NativeMethods.GetWindowRect(hwnd)`（user32 P/Invoke）→ `Rectangle`（物理屏幕像素）。
- DPI 换算：物理屏幕像素 → WPF DIP，**首选 `VisualTreeHelper.GetDpi(overlay)`**（返回 `DpiScale`，与窗口绑定、更直接）；`dip = physical / DpiScale.DpiScaleX`（Y 同理）。**调用时机**：须在 OverlayWindow 已加载进视觉树后（如 `Loaded` 之后）取，否则可能返回 0。
- `Phase1Probe` 改造：识别循环里，除现有 `_log.Log(...)` 外，收集 `(text, subBounds)`；每帧结束按 §数据流 的 ★步骤（子图 bounds + 当前 ROI 的 `roiPx.Left/Top` → 帧内坐标）→ `ScreenMapper` → DPI 换算成 `OverlayItem` 列表，用 `overlay.Dispatcher.InvokeAsync(() => overlay.ShowLines(items))`（异步、不阻塞 OCR 线程）编组到 UI 线程；空结果 `ShowLines([])` 清屏。
- **hwnd 来源(单一真相)**：`CaptureService` 暴露 `public IntPtr Hwnd { get; }`（`Start(hwnd)` 时记录）；`Phase1Probe` 从注入的 `CaptureService.Hwnd` 取，**不自己再 FindByProcess**（避免两次查询拿到不同句柄）。Phase1Probe 注入 `CaptureService` 与 `OverlayWindow`。

## 数据流

```
帧(物理px) → ROI 裁剪(得 roiPx = RoiStore.ToPixels(...)) → 对子图 OCR
  → 每行(韩文, 子图内 bounds subBounds)
  → [日志] _log.Log(...)                （保留）
  → ★子图→帧: frameBounds = subBounds 平移 (+roiPx.Left, +roiPx.Top)
  → ScreenMapper.MapFrameToScreen(frameBounds, frameSize=(frame.Width,frame.Height), GetWindowRect(hwnd)) → 物理屏幕矩形
  → 物理→DIP 换算 → OverlayItem(text, dipLeft, dipTop)
  → overlay.Dispatcher.InvokeAsync(() => overlay.ShowLines(items))  → Canvas 摆放
```
**★关键(易漏)**：`OcrLine.Bounds` 是相对**裁剪子图**左上角的坐标，不是帧内坐标。映射到屏幕前**必须先加上该 ROI 在帧内的像素偏移** `roiPx.Left/Top`（`roiPx` 即本 ROI 的 `RoiStore.ToPixels` 结果），得到帧内坐标再交给 `ScreenMapper`。漏这步会导致文字整体偏移 ≈ ROI 左上角偏移。

## 坐标映射细节

- 捕获帧尺寸 = 被捕获窗口的物理像素尺寸（`GraphicsCaptureItem.Size`，即 `frame.ContentSize`）。
- OCR bounds = 帧内像素。
- `GetWindowRect` = 窗口在屏幕的物理像素矩形。
- 若帧尺寸 == 窗口物理尺寸 → scale≈1，纯平移；保留 scale 以防差异。
- **前提**：SC:R 须**无边框/窗口化**（否则整窗含标题栏，OCR 客户区坐标与整窗有偏移）——本就是项目硬约束。
- 帧尺寸随窗口大小变化，用**当帧的 `frame.Width/Height`** 作 frameSize，不用缓存值。

## 线程

OCR 在线程池线程（CaptureService `CreateFreeThreaded` + Phase1Probe 异步）。更新 WPF UI 必须切 UI 线程：用 `overlay.Dispatcher.InvokeAsync(...)`（异步，不阻塞 OCR 线程；同步 `Invoke` 在 UI 忙时会阻塞捕获侧）。`ShowLines` 只在 UI 线程执行。

## 日志

保留现有 `FileLogger` 与 `[roi] "文本" bounds=... conf=...` 格式（直观)。可加一行汇总（如每帧 `[probe] 上屏 N 行`），便于 debug 对照上屏与识别。

## 测试

- **ScreenMapper（TDD, xUnit）**：帧像素→物理屏幕矩形换算（含 scale≠1、平移、边界）。
- OverlayWindow `ShowLines` / DPI 换算 / Dispatcher / GetWindowRect：需真实窗口，手动验收。

## 验收标准

1. `dotnet test` 全绿（新增 ScreenMapper 单测 + 原有测试）。
2. SC:R 无边框/窗口化 + 自定义游戏列表；托盘开探测：**悬浮层在 Game Name 列韩文位置显示出对应韩文**（白字带阴影、点击穿透不挡操作），随游戏刷新更新；同时日志照常记录。**位置精度**：上屏文字在视觉上与对应韩文行重叠或紧贴（同行竖直偏差不超过约一行文字高度）。
3. 连续多帧不崩、悬浮不闪退、不抢焦点。

## 风险

- **坐标映射准度**（本期主要风险，实机才能定论）：窗口 rect + DPI 缩放 + 客户区 vs 整窗；文字贴不贴得准是验收重点。偏移则微调映射（如引入客户区偏移 `GetClientRect`+`ClientToScreen`）。
- 覆盖主屏的透明窗性能：文本条数少（房间名列几行），可忽略。
- 多显示器 / DPI 混合：本期只保证主屏/单 DPI，多屏留后续。
