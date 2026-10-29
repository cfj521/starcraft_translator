# OCR DirectML GPU 迁移 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 OCR 从 onnxruntime CPU 切到 DirectML GPU（4090），CPU 基本不动，韩文识别不变，免装 CUDA。

**Architecture:** 不绕开 RapidOcrNet——它自带 `InitModels(..., SessionOptions)` 重载。只需在
`PaddleOcrEngine` 里构造一个带 `AppendExecutionProvider_DML(0)` 的 `SessionOptions` 传进去；
`IntraOpNumThreads` 限住 rec 回退 CPU 算子的线程。csproj 加 DirectML 包、排除 RapidOcrNet
传递来的 CPU 版原生 onnxruntime（避免同名 DLL 冲突）。

**Tech Stack:** .NET 10 (net10.0-windows10.0.19041.0)、RapidOcrNet 2.0.0、
Microsoft.ML.OnnxRuntime.DirectML 1.24.4、onnxruntime DirectML。

**Spec:** `docs/superpowers/specs/2026-10-16-ocr-dml-gpu-design.md`（含全部调研、模型身份、
DML 实证、RapidOcr API 发现、打包约束——实现前必读）

## Global Constraints

- 不动 `IOcrEngine` 接口；不动 `App.xaml.cs` 的 DI 传参（`OcrThreads` 继续传 `numThreads`）。
- 不动 LineStabilizer / 编辑距离（闪烁修复保留）。
- 模型文件与 `dict.txt` 一行不动（同一套 PP-OCRv5 韩文模型）。
- **构建前先** `taskkill /F /IM ScrTranslator.App.exe`（无 .sln，分别构建）。
- 构建：`src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`；
  测试：`tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`（分开跑）。
- 这是 GPU 集成改动：**编译通过 + 实机 F9 OCR 正常 + CPU 下降**才算完成，不是单测通过。

## Review Focus

1. **CPU 版 `onnxruntime.dll` 残留**：csproj 排除没写对 → 输出目录里两个 `onnxruntime.dll`，
   DML 版被影子、静默退回 CPU（看起来"能跑"但 CPU 没降）。→ Task 1 检查输出目录只有一份原生 DLL。
2. **版本 roll-forward**：RapidOcrNet 针对 Managed 1.24.3 构建，加载 1.24.4 若绑定失败 →
   启动时 `FileNotFoundException`/`BadImageFormatException`。→ Task 2 构建 + Task 3 启动验证。
3. **DML 初始化失败**：4090 无 DirectML 运行库或设备索引错 → `AppendExecutionProvider_DML` 抛异常。
   → Task 3 启动验证（DML 运行库由 `Microsoft.AI.DirectML` 传递依赖提供）。
4. **rec 回退算子线程没限住**：`IntraOpNumThreads` 没设或被忽略 → rec 回退算子仍拉多核。
   → Task 3 实机看 CPU（有韩文翻译内容时多数核不应 50%+）。
5. **韩文识别回归**：换引擎后 det/rec/cls 任一输出异常 → 韩文识别错/漏。→ Task 3 F9 实机看韩→中。

---

### Task 1: csproj 加 DirectML 包 + 排除 CPU 版原生 onnxruntime

**Files:**
- Modify: `src/ScrTranslator.Core/ScrTranslator.Core.csproj`（PackageReference 段）

**Interfaces:**
- Produces: 输出目录里**只有一份** `onnxruntime.dll`（DML 版 1.24.4）；托管 API 由
  `Microsoft.ML.OnnxRuntime.Managed` 1.24.4 提供。Task 2 依赖此状态。

- [ ] **Step 1: 改 csproj**

在 `<ItemGroup>`（含 `RapidOcrNet` 那个）里，`RapidOcrNet` 之后加两行：

```xml
<!-- 新增：DirectML 执行提供者 + DML 版原生 onnxruntime -->
<PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
<!-- 排除 RapidOcrNet 传递依赖的 CPU 版原生 onnxruntime，避免同名 DLL 冲突；
     托管 API 仍由 Microsoft.ML.OnnxRuntime.Managed 提供 -->
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.24.3" ExcludeAssets="runtime" />
```

- [ ] **Step 2: taskkill 后构建**

```
taskkill /F /IM ScrTranslator.App.exe
dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release
```
Expected: 0 error。

- [ ] **Step 3: 检查输出目录只有一份原生 onnxruntime.dll**

查看 `src/ScrTranslator.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/`（或对应输出根）：
`onnxruntime.dll` 应只有**一份**（来自 DirectML）。若出现两份或仍是 CPU 版 → 排除没生效，回到 Step 1 检查 `ExcludeAssets`。
Expected: 仅一份 `onnxruntime.dll`。

- [ ] **Step 4: Commit**

```bash
git add src/ScrTranslator.Core/ScrTranslator.Core.csproj
git commit -m "build(ocr): add DirectML provider, exclude CPU onnxruntime native"
```

---

### Task 2: PaddleOcrEngine 走 DML SessionOptions

**Files:**
- Modify: `src/ScrTranslator.Core/Ocr/PaddleOcrEngine.cs`（构造函数 + using）

**Interfaces:**
- Consumes: Task 1 的 DirectML 包（`AppendExecutionProvider_DML` 扩展方法、`SessionOptions`、
  `IntraOpNumThreads`）。
- Produces: `PaddleOcrEngine(modelsDir, numThreads)` 构造时创建 DML `SessionOptions` 并传给
  `RapidOcr.InitModels(det, cls, rec, keys, sessionOptions)`。`IOcrEngine.RecognizeAsync`
  行为不变（仍返回 `OcrResult`）。

- [ ] **Step 1: 加 using**

文件顶部加 `using Microsoft.ML.OnnxRuntime;`。

- [ ] **Step 2: 构造 DML SessionOptions 并传给 InitModels**

构造函数里，文件检查之后、`new RapidOcr()` 之前：

```csharp
var so = new SessionOptions();
so.AppendExecutionProvider_DML(0);                 // DirectML GPU（设备 0 = 4090），免装 CUDA
so.IntraOpNumThreads = Math.Max(1, numThreads);    // 限住回退 CPU 算子的线程，避免拉满全核
```

把原来的 `_ocr.InitModels(detPath: det, clsPath: cls, recPath: rec, keysPath: keys, numThread: Math.Max(1, numThreads));`
替换为：
```csharp
_ocr.InitModels(detPath: det, clsPath: cls, recPath: rec, keysPath: keys, sessionOptions: so);
```

- [ ] **Step 3: taskkill 后构建**

```
taskkill /F /IM ScrTranslator.App.exe
dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release
```
Expected: 0 error。

- [ ] **Step 4: Commit**

```bash
git add src/ScrTranslator.Core/Ocr/PaddleOcrEngine.cs
git commit -m "feat(ocr): route OCR through DirectML GPU via SessionOptions"
```

---

### Task 3: 实机 E2E 验证（F9 OCR 正常 + CPU 下降）

**Files:** 无代码改动；实机验证。

**Interfaces:**
- Consumes: Task 1 + Task 2 的完整构建。
- Produces: 用户确认 OCR 识别正常、CPU 下降。这是本 plan 的**最终验收**。

- [ ] **Step 1: 启动 App**

构建产物启动 `ScrTranslator.App`（Release）。确认启动无 `FileNotFoundException`/
`BadImageFormatException`（排除 Task 2 的 Review Focus #2 版本绑定问题）。
Expected: 正常启动，无 onnxruntime 相关异常。

- [ ] **Step 2: F9 开翻译，验韩文识别**

SC:R 里出现韩文内容，F9 开启翻译。核对韩→中识别正确（无漏字、无错字）。
排除 Review Focus #5（韩文回归）。
Expected: 韩文正确翻译成中文。

- [ ] **Step 3: 看 CPU 占用**

有翻译内容时看任务管理器：多数核不应再 50%+（整体从 ~55% 显著下降）。rec 回退算子线程已被
`IntraOpNumThreads` 限住。排除 Review Focus #1/#4。
Expected: CPU 明显低于改前的 ~55%。

- [ ] **Step 4: 确认闪烁修复仍在**

观察翻译文字是否稳定（不再整行周期性消失/重现）。排除回归。
Expected: 无闪烁。

- [ ] **Step 5: 报告结果**

向用户报告：识别正确性、CPU 数值、是否有 rec 回退告警（正常）。由用户决定是否提交/收尾。
