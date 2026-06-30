# 设计：Phase 1 — 窗口捕获 + ROI 裁剪 + 韩文 OCR（打印验证）

**日期**：2026-07-01
**状态**：设计待批准
**前置**：Phase 0 已完成（工程骨架 + 悬浮窗 + 托盘）

## 目标

跑通最高风险的新管线：**捕获 SC:R 窗口 → 按 rois.json 裁剪区域 → PaddleOCR 识别韩文 → 写日志（韩文 + 位置 + 置信度）**。验证“Windows.Graphics.Capture + PaddleOCR(ONNX) 在真实 SC:R 韩文上到底行不行”。

## 范围

**本期做（in scope）**：
- 按进程/标题找到 SC:R 窗口句柄
- `Windows.Graphics.Capture` 捕获该窗口画面，转可裁剪位图
- `rois.json`：相对比例(0~1)矩形，按分辨率/宽高比分桶；**Phase 1 只固定打一个场景**（自定义游戏列表的 Game Name 列）
- `IOcrEngine` + `PaddleOcrEngine`（RapidOCR.Net 封装，PP-OCR mobile det + 韩文 rec + 韩文字典）
- 把识别结果（每行文本 + 包围盒 + 置信度）写**日志文件**

**本期不做（out of scope，注明去向）**：
- 场景识别（自动判断当前界面）→ **Phase 1.5 单独立项**
- 多场景 ROI 自动切换 → Phase 1.5
- 变化检测 → Phase 2
- 翻译、悬浮窗显示译文 → Phase 2/3（本期悬浮窗仍是 Phase 0 测试字）
- 交互式拖框校准 UI、多分辨率自动适配 → Phase 4

## 已确认决策

- **捕获目标**：直接锁定 SC:R（`StarCraft.exe`/窗口标题），需游戏窗口化运行。
- **ROI 坐标**：相对百分比(0~1)，按分辨率/宽高比分桶存；Phase 1 只用当前分辨率这一套。
- **OCR 封装**：`RapidOCR.Net`（BobLd/RapidOcrNet，NuGet `RapidOCR.Net`）——OnnxRuntime + SkiaSharp，**无 OpenCV / 无 System.Drawing**。加载自定义模型：PP-OCR mobile det + 韩文 rec + 韩文字典(+可选角度 cls)。
- **模型文件**：~22MB，**提交进 git 仓库** `models/`，构建时拷到输出目录。
- **输出**：写**日志文件**（`Microsoft.Extensions.Logging` + 文件 sink）。
- **测试场景**：自定义游戏列表（custom games listing）的 Game Name 列（静态韩文房间名，区域清晰）。

## 与上游文档的偏离（已与用户确认，本 Phase 以本 spec 为准）

本 spec 在以下点偏离 ROADMAP / CLAUDE.md / TECH_DESIGN，均为有意决策，后续应回写更新这些文档：

- **OCR 引擎**：ROADMAP Phase 1 与 CLAUDE.md 原写 `Windows.Media.Ocr`（TECH_DESIGN 把 PaddleOCR 列为"后续/Phase 5"）。**本 Phase 跳过 Windows.Media.Ocr，直接用 PaddleOCR(ONNX)**。理由：游戏字体偏小、系统 OCR 韩文识别率已知不足；模型随包分发免装系统语言包。原文档中 Windows.Media.Ocr 作为 Phase 1 基线的描述在本 Phase 作废。
- **ROI 获取方式**：ROADMAP Phase 1 第 3 项是"交互式框选工具"。**本 Phase 以预写的静态 `rois.json` 替代**，不另建交互工具；交互式拖框校准 UI → Phase 4。ROADMAP Phase 1 验收（控制台稳定打印正确韩文）由本 spec §验收 #2/#3 覆盖。
- **CaptureService 位置**：TECH_DESIGN §5 把它放 `Core/Capture/`。**本 spec 迁至 App 层**，因其 WinRT 依赖无法在非 Windows 环境单测（符合 CLAUDE.md "Core 无 UI 依赖可单测"）。
- **FrameArrived 签名**：TECH_DESIGN §3.1 为 `event Action<CaptureFrame>`。**本 Phase 简化为 `event Action<SKBitmap>`**，不传帧元数据；Phase 2 变化检测若需时间戳/帧序号，届时再扩展接口。

## 架构与 Core/App 拆分

遵循 CLAUDE.md：业务逻辑放 Core（无 UI 依赖、可单测）；WinRT/窗口相关放 App。

**Core（`ScrTranslator.Core`，可单测）**：
- `Roi` 模型 + `RoiStore`：读 `rois.json`；相对比例(0~1)↔像素矩形换算；按帧尺寸裁剪并 clamp 到边界。→ **TDD**
- `IOcrEngine`（接口，**精化 TECH_DESIGN §3.4**：图像类型用 `SKBitmap` 取代 `System.Drawing.Bitmap`，与 RapidOCR.Net/SkiaSharp 一致并避开 System.Drawing；去掉 `lang` 参数——语言由加载的韩文模型固定，无需每次传）：
  ```csharp
  public interface IOcrEngine {
      Task<OcrResult> RecognizeAsync(SKBitmap image, CancellationToken ct = default);
  }
  public record OcrLine(string Text, RectangleF Bounds, float Confidence);
  public record OcrResult(IReadOnlyList<OcrLine> Lines);
  ```
- `PaddleOcrEngine`（实现 `IOcrEngine`，封装 RapidOCR.Net）：构造时从 models 目录加载 det/rec/dict/cls；`RecognizeAsync` 调用 RapidOCR.Net 推理，映射为 `OcrResult`。→ **集成测试**（内置韩文样图）

**App（`ScrTranslator.App`，WinRT，手动验收）**：
- `CaptureService`：
  - 按 `StarCraft.exe`/标题找窗口 HWND；
  - 经 `IGraphicsCaptureItemInterop.CreateForWindow(hwnd)` 建 `GraphicsCaptureItem`；
  - 建 D3D11 设备 + `Direct3D11CaptureFramePool.CreateFreeThreaded`（免 DispatcherQueue，适配 WPF）；
  - 帧 `Direct3D11CaptureFrame.Surface` → `SoftwareBitmap.CreateCopyFromSurfaceAsync` → 像素 → `SKBitmap`；
  - 控制抓帧节流（如 400ms 一次）；窗口丢失/最小化时暂停重试。
  - 接口：`event Action<SKBitmap> FrameArrived; Start(); Stop();`
- `Phase1Probe`（驱动）：订阅 `FrameArrived` → 对当前场景每个 ROI 裁剪 → `PaddleOcrEngine.RecognizeAsync` → 写日志。**触发方式**：复用 Phase 0 托盘，加一个菜单项「开始/停止 Phase1 探测」（不自动启动，避免无 SC:R 时空转）。

## 数据流

```
SC:R HWND
  → GraphicsCaptureItem → D3D11 帧 (节流 ~400ms)
  → SoftwareBitmap → SKBitmap（全窗口帧）
  → RoiStore：按当前分辨率取场景 ROI 集，每个 ROI 相对矩形 × 帧尺寸 → 像素矩形 → 裁出子图 SKBitmap
  → PaddleOcrEngine.RecognizeAsync(子图) → OcrResult{行: 文本, 包围盒, 置信度}
  → 日志：每行  [ROI名] "한국어..." bounds=(x,y,w,h) conf=0.93
```

## rois.json 格式

```json
{
  "scenes": {
    "custom_games_listing": {
      "resolution": "1920x1080",
      "rois": [
        { "name": "game_name_column", "type": "RoomList", "rect": [0.18, 0.28, 0.22, 0.40] }
      ]
    }
  }
}
```
- `rect` = [x, y, w, h]，相对比例(0~1)，相对窗口客户区。
- `resolution` 为该套坐标校准时的分辨率（分桶键）。
- **分辨率匹配规则**（RoiStore 的可测行为）：先按 `"WxH"` 字符串**精确匹配**当前窗口客户区分辨率；无精确匹配 → 选**宽高比最接近**的桶（相对坐标按比例可用）；仍无 → 取 json 第一个桶并记 warning。Phase 1 只有一个桶时走精确匹配分支，但该规则是 TDD 的明确依据。
- Phase 1 只需 `custom_games_listing` 一个场景一个 ROI；具体数值在真实窗口上实测微调。

## OCR 引擎细节

- 依赖：NuGet `RapidOCR.Net`（拉入 `Microsoft.ML.OnnxRuntime` + `SkiaSharp`，无 OpenCV）。
- 模型放 `models/`：`det.onnx`（PP-OCR mobile 检测，语言无关）、`rec_korean.onnx`（韩文识别 mobile）、`korean_dict.txt`（字符字典）、可选 `cls.onnx`（角度分类）。来源：RapidOCR/PaddleOCR 官方 ONNX 模型（korean，mobile/v4 或 v5）。
- csproj 用 `<None Include="models\**" CopyToOutputDirectory="PreserveNewest" />` 拷到输出。
- 预处理（提升小字命中）：必要时灰度/放大（RapidOCR.Net 内部已含检测+识别流水，预处理按需最小化，避免过度工程）。

## 捕获细节

- 互操作：`Windows.Graphics.Capture` 经 CsWinRT；`CreateForWindow` 需 `IGraphicsCaptureItemInterop`（COM 互操作）。
- D3D11 设备：`D3D11CreateDevice` 建设备后，通过 CsWinRT 互操作（`CreateDirect3D11DeviceFromDXGIDevice`）包装出 `IDirect3DDevice`；**不引入 SharpDX**。
- 用 `CreateFreeThreaded` 帧池，回调在线程池线程，避免 WPF 无 DispatcherQueue 的问题。
- Win10/11 捕获会给被捕获窗口加黄色边框（系统行为），可接受。

## 输出（日志）

- 复用 Phase 0 的 Host：`Microsoft.Extensions.Logging` + **自写 `StreamWriter` 文件 sink**（不引入 Serilog 等重日志库，符合轻量取向）。
- 日志条目：`[scene/roi] text="..." bounds=(x,y,w,h) conf=0.xx`；空结果/低置信度也记一行便于诊断。
- 日志路径：输出目录下 `logs/phase1-ocr.log`（`.gitignore` 已忽略 `*.log`）。

## 测试策略

- **RoiStore（TDD，xUnit）**：相对↔像素换算、clamp 越界、按分辨率选集、rect 解析。
- **PaddleOcrEngine（集成测试）**：仓库内置一张韩文样图（截自场景1 的 Game Name 区域或自制韩文 PNG）；断言识别结果**包含预期韩文子串**且置信度 > **0.6**（参考阈值，实现期按样图实测微调后写死）。验证 OCR 路径不依赖游戏在场。
- **CaptureService + 端到端**：无法单测（需真实窗口），由手动验收覆盖。

## 验收标准

1. `dotnet test` 全绿（RoiStore 单测 + PaddleOcrEngine 对内置韩文样图的集成测试）。
2. SC:R 窗口化运行 + 自定义游戏列表界面；启动 app（开关/热键触发 Phase1Probe）；`rois.json` 框住 Game Name 列。
3. `logs/phase1-ocr.log` **稳定打印出正确的韩文房间名** + 包围盒 + 置信度（连续多帧不崩、不卡死捕获线程）。

## 推迟事项（去向明确）

- 场景识别（锚点模板/感知哈希匹配 + 底部 HUD 颜色预筛）→ **Phase 1.5 单独 brainstorm**。
- 多场景 ROI 自动切换、多分辨率适配、交互校准 UI → Phase 1.5 / Phase 4。
- 变化检测、翻译、译文上屏 → Phase 2+。

## 风险与限制

- **RapidOCR.Net 加载自定义韩文模型**：需确认它支持传入自定义 det/rec/dict 路径；若 API 不便，退路是直接用 `Microsoft.ML.OnnxRuntime` + 自写 PP-OCR 前后处理（更多工作但完全可控）。
- **韩文 mobile 模型对游戏字体/小字精度**：实测可能不足；退路是换 server 档模型或加预处理放大；不行再评估其它引擎。
- **Graphics.Capture 在 WPF 的 D3D 设备/帧池接线**较繁琐，是本期主要实现风险点；用 `CreateFreeThreaded` + CsWinRT 互操作降低复杂度。
- 模型 ~22MB 进 git 使仓库变大（已接受）；Phase 5 打包可优化。
