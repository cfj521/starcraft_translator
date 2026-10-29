# 全项目 Bug Review 计划（2026-10-29）

## 0. 目标与非目标

**目标**：以"找 bug"为导向，系统性审查 `src/`（App 4481 行 + Core 2886 行）与 `tools/`（1064 行），产出**可复现、有代码证据**的问题清单。测试项目（2958 行 / 255 用例）视为既有防线，只补洞、不重写。

**非目标**：架构重写、纯性能调优（除非直接导致功能错误）、UI 美化、代码风格。

**产出**：按严重度分级的问题清单，每条含 `文件:行`、触发条件、现象、证据、建议修法。**默认只报不改**，用户逐条点头后才修。

## 1. 严重度定义

| 级别 | 含义 | 例子 |
|---|---|---|
| P0 | 崩溃 / 死锁 / 数据损坏 / 密钥泄漏 | UI 线程 `.Result` 死锁、TCS 永不完成、异常路径泄漏句柄 |
| P1 | 功能错误、状态卡死、错误翻译上屏 | 场景识别顺序错、provider 回退卡住、协议行号错位 |
| P2 | 边界/健壮性：异常输入、极端分辨率、文件损坏 | JSON 缺字段、0/负尺寸、非 16:9 缩放 |
| P3 | 可维护性、潜在隐患、文档-代码契约漂移 | 裸 `catch`、重复代码、注释过期 |

## 2. 风险画像（基于首轮侦察）

| 模块 | 行数 | 主要风险 | 初始优先级 |
|---|---|---|---|
| `TranslationEngine.cs` | 850 | 截图 TCS、`async void`、`lock(_shotLock)`、管线顺序 | P0 |
| `SettingsWindow.xaml.cs` | 1091 | UI 线程 `.Result`、文件读写、事件解绑 | P0/P1 |
| `CalibrationWindow.xaml.cs` | 499 | UI 线程 `.Result`、场景检测、覆盖层 | P0/P1 |
| `CaptureService.cs` | 314 | `async void`、D3D 帧池、窗口尺寸变化重建 | P0 |
| `HyMt2Provider.cs` | 197 | 模型懒加载/卸载竞态、`Thread.Sleep` 忙等 | P1 |
| `SceneRefStore` / `SceneRefCapture` / `LayoutPresets` | 338+133+239 | 几何缩放、完整性校验、原子写 | P1 |
| 翻译链（Orchestrator/Cache/RateLimit/Batch） | ~250 | 回退、并发、429 冷却、协议解析 | P1 |
| OCR（`PaddleOcrEngine`） | 129 | 生命周期、ONNX 资源释放 | P2 |
| 设置/密钥/热键/自启 | ~400 | 持久化损坏、DPAPI 失败、注册泄漏 | P2 |
| `tools/` | 1064 | 离线校验工具，影响面小 | P3 |

## 3. 审查维度（按 bug 类型）

### 3.1 并发与生命周期（最高优先）
- `async void` 异常逃逸与重入（`OnFrame`、`OnFrameArrived`）。
- UI/STA 线程 `.Result` / `.Wait()` 死锁（已定位 `CalibrationWindow.xaml.cs:187`、`SettingsWindow.xaml.cs:463`、`TranslationEngine.cs:649`）。
- `lock` 顺序、重入、跨线程访问 UI 对象/SKBitmap。
- `TaskCompletionSource` 永不完成（异常分支、取消、订阅遗漏）。
- 定时器/事件订阅未解绑 → 重复处理、内存泄漏。
- CancellationToken 传递与泄漏。

### 3.2 资源与内存
- `IDisposable`（SKBitmap、D3D 对象、Stream、ONNX session）在异常路径的释放。
- 截图缓冲区复用竞态：一帧被多处读/释放。
- 大对象与本地模型卸载。

### 3.3 状态机与管线顺序
- 场景识别 → `_roiAvailable` → 内容闸门的顺序（CLAUDE.md 明示曾犯过错）。
- 窗口丢失/最小化/分辨率变化/场景切换时的重置。
- provider 池回退状态：无凭据、额度耗尽、429 冷却后能否恢复。

### 3.4 数值与几何
- `RelRect` 与分辨率缩放：非 16:9、极端宽高比、0/负值、NaN。
- 块均值指纹、`centerCrop`、`race-hud` 区域。
- 边界裁剪越界（`ClampTo`）。

### 3.5 协议与解析
- `BatchProtocol` 编号回填、占位符保护、指令回声。
- JSON 缺字段/类型错误/超大值的容错。
- provider 响应异常：空响应、多余行、非 UTF8、截断。

### 3.6 持久化与安全
- 设置文件原子写、损坏降级、并发写。
- DPAPI 失败路径、密钥是否进入日志/异常信息。
- OAuth 本地回调端口占用、state/PKCE 校验。

### 3.7 兼容与契约
- 出厂 `scene-refs.json` 新格式与解析器的真实一致性（刚改过，重点回归）。
- `DocsConsistencyTests` 之外的隐性契约。
- 老配置/老校准文件迁移。

## 4. 执行方式

- **阶段 A 静态通读**：两个并行 subagent —— ① App 管线/UI/捕获；② Core 内核/翻译/场景。每条 finding 必须给出代码证据，禁止臆测。
- **阶段 B 动态验证**：对 P0/P1 写**临时**复现测试或小工具（不改生产代码），验证死锁、TCS 不完成、异常路径泄漏。
- **阶段 C 交叉复核**：P0/P1 由另一 agent 独立复核，压误报。
- **阶段 D 汇总**：分级、排序、给修法；标注"需实机验证"项。

## 5. 分批交付

| 批次 | 范围 | 预期产出 |
|---|---|---|
| 1 | 并发与生命周期（P0 热点） | 死锁/崩溃/TCS 清单 |
| 2 | 管线、场景、几何（刚重构） | 回归与边界 bug |
| 3 | 翻译 provider、协议、OCR | 回退/解析/资源 bug |
| 4 | 持久化、安全、工具、文档契约 | 健壮性与兼容问题 |

每批产出后**停下来给用户过目**，避免一次性倾倒巨量报告。

## 6. 具体检查清单（首批）

- `TranslationEngine.cs:642-690`：`lock(_shotLock)` + `t.Result` + `async void OnFrame`。
- `CalibrationWindow.xaml.cs:187`：`tcs.Task.Result`（UI 线程死锁）。
- `SettingsWindow.xaml.cs:463`：`tcs.Task.Result`；`:549` 直接写预设；`:1104` 异常处理。
- `CaptureService.cs:109/146/202/222`：`lock`、`Thread.Sleep(20)`、`async void`、帧池重建。
- `HyMt2Provider.cs:53-146`：状态机、卸载竞态、`Thread.Sleep(10)` 忙等。
- `PaddleOcrEngine.cs:57/95/135/139`：`Task.Run` 初始化、`Dispose` 甩后台线程。
- `AliyunMtProvider.cs:72`：`catch(Exception){return null}` 吞错；`:84` 额度日期。
- `SceneRefStore.cs` / `SceneRefCapture.cs` / `LayoutPresets.cs`：I/O、原子写、缩放。
- `TranslationOrchestrator.cs:85`、`TranslationCache`、`RateLimitCooldown`：并发与回退。
- `SettingsStore` / `SecretStore` / `AutoStartService` / `HotkeyService` / `TrayService`。

## 7. 需要确认

- 是否允许为复现 P0/P1 添加**临时测试**（跑完删除，不进提交）？
- `tools/` 是否纳入（默认 P3 轻扫）？
- 报告后是否直接修，还是先只出清单？
