# 技术设计文档 — SC:R 实时翻译软件

> 配合 [PRD.md](PRD.md) 阅读。本文聚焦"怎么实现"。

## 1. 技术选型总览

| 维度 | 选型 | 理由 |
|------|------|------|
| 运行时 | .NET 10 | 最新 LTS（支持至 2028）、现代、WPF 支持好 |
| UI / 悬浮层 | WPF | 透明窗口、矢量渲染、CJK 字体好 |
| 屏幕捕获 | `Windows.Graphics.Capture` (WinRT) | 抓指定窗口、性能好、官方 API；DXGI Desktop Duplication 备选 |
| OCR | PaddleOCR / RapidOcrNet (ONNX Runtime) + 韩文 mobile 模型 | 韩文识别准、随包分发、**无需系统语言包**（已取代原 `Windows.Media.Ocr` 方案） |
| LLM 翻译（Phase 2 起） | **OpenAI 订阅（ChatGPT/Codex OAuth+Responses）** / **阿里云 DashScope（OpenAI 兼容 Chat）**，单活跃可切换 | 懂游戏黑话、可加术语表与上下文。**不再用 Claude / Papago / 百度**。详见 `docs/superpowers/specs/2026-07-02-phase2-translation-design.md` |
| 配置/序列化 | System.Text.Json | 内置 |
| 依赖注入 | Microsoft.Extensions.DependencyInjection | 模块解耦 |
| 日志 | Microsoft.Extensions.Logging + Serilog(可选) | 排障 |

> WinRT API 在 .NET 10 中通过 `net10.0-windows10.0.19041.0` 目标框架 + CsWinRT 调用。

---

## 2. 总体架构

```
                    ┌──────────────────────────────────────┐
                    │   SC:R 窗口 (窗口化/无边框运行)         │
                    └──────────────────────────────────────┘
                                     │ HWND
            ┌────────────────────────┼─────────────────────────┐
            │                  CaptureService                   │
            │     Windows.Graphics.Capture → 每帧 Bitmap        │
            └────────────────────────┬─────────────────────────┘
                                     │ 整窗帧
                    ┌────────────────▼─────────────────┐
                    │           RoiManager              │  按分辨率加载 ROI 配置
                    │   裁出: 聊天框 / 房间名列表 / 地图  │
                    └────────────────┬─────────────────┘
                                     │ 每个 ROI 的子图
                    ┌────────────────▼─────────────────┐
                    │         ChangeDetector            │  帧差 / 感知哈希
                    │   未变 → 跳过；变了 → 下游          │
                    └────────────────┬─────────────────┘
                                     │ 变化的 ROI
                    ┌────────────────▼─────────────────┐
                    │           OcrService              │  IOcrEngine (可插拔)
                    │   ROI 子图 → 韩文文本 + 行位置     │
                    └────────────────┬─────────────────┘
                                     │ OcrResult
                    ┌────────────────▼─────────────────┐
                    │       TranslationService          │
                    │  ① 术语表/缓存命中 → 直出          │
                    │  ② FastProvider 异步 → 临时态      │
                    │  ③ LlmProvider 异步 → 最终态       │
                    └────────────────┬─────────────────┘
                                     │ TranslationResult (事件)
                    ┌────────────────▼─────────────────┐
                    │         OverlayRenderer           │  WPF 透明置顶+点击穿透
                    │   贴近 ROI 显示中文(临时/最终态)   │
                    └───────────────────────────────────┘

   Pipeline 编排以上循环；SceneRecognizer(3.9) / Settings / Calibration / Hotkey 为横切支撑。
```

---

## 3. 模块详述

### 3.1 CaptureService
- **职责**：根据 SC:R 窗口句柄(HWND)，用 `GraphicsCaptureItem.CreateForWindow` 创建捕获会话，输出帧（`Direct3D11CaptureFramePool`）。
- **关键点**：
  - 通过进程名/窗口标题查找 SC:R 窗口（`StarCraft`，进程 `StarCraft.exe`）。
  - 帧转 `SoftwareBitmap` / `System.Drawing.Bitmap` 供下游裁剪。
  - 控制抓帧频率（非每帧，按轮询间隔，如 300–500ms 一次，聊天区可更快）。
  - 窗口丢失/最小化时暂停并重试。
- **接口**：`event Action<CaptureFrame> FrameArrived;` `Start(IntPtr hwnd)` / `Stop()`。

### 3.2 RoiManager
- **职责**：管理用户定义的区域。每个 ROI = { Name, Type(Chat/RoomList/MapInfo/Custom), Rect(相对窗口客户区的比例或像素), 分辨率 }。
- **关键点**：
  - 坐标用**相对比例**(0~1) 存储，适配窗口缩放；或按分辨率存多套并自动匹配当前分辨率。
  - 房间名列表型 ROI 可定义**列**（ID 列 vs 房间名列 vs 地图列），用于"哪些翻哪些不翻"。
- **配置**：`rois.json`。

### 3.3 ChangeDetector
- **职责**：对每个 ROI 子图判断是否变化，避免重复 OCR/翻译（省 CPU 和 API 费用）。
- **方案**：缩略图 + 感知哈希(aHash/dHash) 或像素差比例；阈值可调。聊天区因滚动，建议结合"OCR 文本去重"做二级判断。

### 3.4 OcrService（可插拔）
- **接口**：
  ```csharp
  public interface IOcrEngine {
      Task<OcrResult> RecognizeAsync(Bitmap image, string lang = "ko");
  }
  public record OcrLine(string Text, RectangleF Bounds, float Confidence);
  public record OcrResult(IReadOnlyList<OcrLine> Lines);
  ```
- **实现**：
  - `WindowsOcrEngine`：`OcrEngine.TryCreateFromLanguage(new Language("ko"))`。需系统装韩文 OCR 语言包。
  - `PaddleOcrEngine`（后续）：ONNX 模型(det+rec, korean)。
- **预处理**：灰度/二值化/放大，提升小字识别率。

### 3.5 TranslationService
- **接口**：
  ```csharp
  public interface ITranslationProvider {
      Task<string> TranslateAsync(string text, string from="ko", string to="zh");
  }
  ```
- **编排逻辑**（每条文本）：
  1. **预处理/分流**：若该 ROI 列为"ID 列" 且配置 `TranslatePlayerId=false`（默认）→ 不翻译，原样输出；开关打开则照常翻译。聊天/房间名/地图列各有独立开关。
  2. **术语表**：整句或分词命中 GLOSSARY → 直接映射（无网络）。
  3. **缓存**：`MemoryCache<原文, 译文>` 命中 → 直出。
  4. **翻译 provider**（`ITranslationProvider`，OpenAI 订阅 / 阿里云 DashScope 单活跃）批量调用 → 上屏中文。**Phase 2 为单级**（术语/缓存未命中即调当前活跃 provider）。
  5. **两级编排（Phase 3 已落地，默认关闭）**：快态 provider（DashScope qwen-flash）临时态 → 质量态 provider（Active 引擎）最终态原地替换；术语经 `PromptBuilder` 注入 system prompt。见 `TieredTranslator`。缓存存最终态；快态永不入缓存。
- **LLM Prompt 要点**：系统提示设定"星际争霸韩服游戏聊天翻译"角色；附术语表；要求口语化、简短、保留游戏黑话语气；ID/数字/英文缩写不乱翻。
- **限流**：LLM 请求合并/节流，避免刷屏时狂发请求。

### 3.6 OverlayRenderer
- **职责**：WPF 透明置顶窗，渲染译文。
- **关键实现**：
  - 窗口：`AllowsTransparency=true`、`WindowStyle=None`、`Topmost=true`、`Background=Transparent`。
  - **点击穿透**：扩展样式加 `WS_EX_TRANSPARENT | WS_EX_LAYERED`（P/Invoke `SetWindowLong`）。
  - 跟随游戏窗口位置/大小（监听窗口移动，或定时校正）。
  - 聊天区：滚动 `ItemsControl`，保留最近 N 条；临时态用半透明/灰色，最终态正常色。
  - 房间名列表：译文叠在对应行旁。

### 3.7 Pipeline / Orchestrator
- 串起 Capture → Roi → Change → Ocr → Translate → Overlay，用事件/Channel 解耦，异步队列处理，避免阻塞捕获线程。

### 3.8 横切
- **Settings**：`appsettings.json` + 用户配置；API Key 用 DPAPI(`ProtectedData`) 加密。
- **Calibration**：进入校准模式时，悬浮层变为可交互的框选 UI，用户拖框定义 ROI 并命名/选类型。
- **Hotkey**：全局热键（`RegisterHotKey`）。

### 3.9 SceneRecognizer（场景识别 = 图像指纹，不用 OCR）
- **职责**：判断当前帧属于 `browsing`(寻找房间) / `room`(房间内) / `ingame`(游戏内) 哪一个，或三者之外的 `other`(其他界面：主菜单/加载/单人/街机/设置)。场景决定用哪套 ROI、以及**翻不翻**。
- **为什么不用 OCR 关键词**：客户端 UI 虽是英文，但 cue 会随版本/语言变，且要为一帧跑 2 次 OCR（慢、和翻译抢引擎）。实测整帧感知哈希也不行——游戏内视口占大半画面且一直在变，两张游戏内截图整帧指纹只有 62/144 命中。
- **方案**：每个场景自带**两块静态区域**的"块均值 RGB 指纹"，逐块带容差比对，命中率最高者胜出。
  - `browsing` / `room`：顶部金属边框左右两角 `(0,0,250,110)`、`(1650,0,270,110)`（1080p 基准）。
  - `ingame`：底部**种族装饰板** `(420,930,120,120)`、`(1200,910,120,120)`。游戏内除装饰板外全是会变的（视口/小地图/单位面板），所以装饰板是唯一锚点；也因此**必须按种族分套参考**(`ingame_t/p/z`)——顺带就得到了种族信息。
  - 块均值(4×4，随分辨率等比放大)而非逐像素：SC:R 贴图自带噪点，逐像素存原始数据要 2.47MB，块均值后 114KB，且噪点被平均掉，跨场景命中率反而从 37% 降到 24%。
- **实测判别力**：同场景两样本 100% 命中；跨场景/跨种族最高 25.2%；阈值 90% → 余量 65 个百分点。
- **参考数据**：`src/ScrTranslator.App/scene-refs.json`（只读，随包分发）由 `tools/SceneRefGen` 从 `docs/scenes pictures/` 的截图生成，带 **SHA-256 完整性校验（覆盖几何+数据）**，被改坏/截断则整份拒用并降级为"其他界面"。用户现场采集的参考写 `%APPDATA%\ScrTranslator\scene-overrides.json`，同 (场景,分辨率) 覆盖出厂值。
- **关键约束**：**分辨率必须精确匹配**——块均值网格随分辨率变，借用邻近分辨率参考会整片错位；所以参考按分辨率分套，缺哪个分辨率就明确报"该分辨率无参考"（保持原场景不动，不硬猜）。
- **迟滞**：新场景需连续 2 次命中才切换；连续 3 次"全部不达标"才落到 `other`（防过场动画/弹窗瞬时干扰）。
- **接口**：`SceneRecognizer.Recognize(FramePixels) → SceneRecognition{Status, GameScene, Race, HitRatio, Scores[]}`。`FramePixels` 是 Core 侧的只读像素视图（BGRA 偏移由 App 捕获层提供），Core 不依赖任何图像库，纯函数可单测。

---

## 4. 数据模型（核心）

```csharp
enum RoiType { Chat, RoomList, MapInfo, Custom }
enum TranslationState { Pending, Fast, Final, Glossary, NoTranslate }

record RoiConfig(string Name, RoiType Type, RectangleF RelRect, List<RoiColumn>? Columns);
record RoiColumn(string Name, float RelX, float RelWidth, bool Translate); // ID列 Translate=false

record OcrLine(string Text, RectangleF Bounds, float Confidence);
record TranslationItem(string SourceText, string? TargetText, TranslationState State, RoiType Roi);
```

---

## 5. 项目目录结构（建议）

```
starcraft_translator/
├─ src/
│  ├─ ScrTranslator.App/            # WPF 启动、主窗、托盘、设置 UI
│  ├─ ScrTranslator.Core/           # 业务逻辑（无 UI 依赖，便于测试）
│  │  ├─ Capture/                   # CaptureService
│  │  ├─ Roi/                       # RoiManager, ChangeDetector
│  │  ├─ Ocr/                       # IOcrEngine, WindowsOcrEngine, PaddleOcrEngine
│  │  ├─ Translation/               # ITranslationProvider, Fast*, Llm*, Glossary, Cache, TranslationService
│  │  └─ Pipeline/                  # Orchestrator
│  ├─ ScrTranslator.Overlay/        # 悬浮层（透明窗、点击穿透、渲染）
│  └─ ScrTranslator.Common/         # 模型、配置、工具
├─ tests/
│  └─ ScrTranslator.Core.Tests/     # Glossary/Cache/Translation 单元测试
├─ assets/                          # 术语表数据、图标
├─ docs/
└─ ScrTranslator.sln
```

> 也可先单工程起步（Phase 0），随阶段拆分。Core 与 UI 分离是为了能对翻译/术语/缓存写单测。

---

## 6. 关键技术难点与方案小结

| 难点 | 方案 |
|------|------|
| 独占全屏盖住悬浮窗 | 引导窗口化/无边框；检测前台窗口模式并提示 |
| 抓特定窗口 | `GraphicsCaptureItem.CreateForWindow(HWND)` |
| 点击穿透 | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` via SetWindowLong |
| 悬浮跟随游戏窗口 | 监听窗口位置 / 定时同步 |
| 韩文小字识别 | ROI 裁剪 + 预处理（放大/二值化）；PaddleOCR 升级 |
| 黑话翻译质量 | 术语表 + LLM 上下文 prompt |
| 刷屏性能/费用 | 变化检测 + 文本去重 + 缓存 + LLM 节流 + 两级 |
| ID 是否翻译 | 配置项 `TranslatePlayerId`(默认 false)+ 热键切换；RoomList 的 ID 列据此决定 |
| 分辨率适配 | 相对坐标 / 按分辨率存多套 ROI |
| 判断当前是哪个界面 | **图像指纹**：每场景两块静态区，块均值 RGB + 容差命中比例（见 3.9），不用 OCR |
| 游戏内识别不稳 | 只用种族装饰板做锚点，参考按种族分套；缺该分辨率参考时报错而非硬猜 |

---

## 7. 第三方依赖（预估 NuGet）

- `Microsoft.Windows.CsWinRT`（WinRT 互操作，Graphics.Capture / Media.Ocr）
- `Microsoft.Extensions.DependencyInjection` / `.Hosting` / `.Logging`
- `System.Drawing.Common` 或 `SixLabors.ImageSharp`（图像处理）
- `Microsoft.Extensions.Caching.Memory`（缓存）
- 翻译：**裸 HttpClient**（不用 SDK）——阿里云 DashScope（OpenAI 兼容 `/chat/completions`）、OpenAI 订阅（Codex OAuth + `/responses` SSE 流式）；密钥/token 走 `SecretStore`（DPAPI）
- OCR：`RapidOcrNet` + 韩文 mobile 模型（已采用）

---

## 8. 测试策略

- **可单测**：Glossary 匹配、缓存、ID 列过滤、LLM prompt 组装、文本去重、**场景指纹**（`SceneFingerprint`/`SceneRecognizer` 用合成帧跑，`SceneRefStore` 覆盖解析/校验和/覆盖层合并，`SceneRefsShippingTests` 回归验证出厂参考真的互相区分得开）。
- **手动验证**：捕获/悬浮/OCR/端到端需在真实游戏窗口上验证（见 ROADMAP 各阶段验收）。
