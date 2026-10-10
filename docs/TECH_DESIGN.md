# 技术设计文档 — SC:R 实时翻译软件

> 配合 [PRD.md](PRD.md) 阅读。本文聚焦"怎么实现"。

## 1. 技术选型总览

| 维度 | 选型 | 理由 |
|------|------|------|
| 运行时 | .NET 10 | 最新 LTS（支持至 2028）、现代、WPF 支持好 |
| UI / 悬浮层 | WPF | 透明窗口、矢量渲染、CJK 字体好 |
| 屏幕捕获 | `Windows.Graphics.Capture` (WinRT) | 抓指定窗口、性能好、官方 API；DXGI Desktop Duplication 备选 |
| OCR | PaddleOCR / RapidOcrNet (ONNX Runtime) + 韩文 mobile 模型 | 韩文识别准、随包分发、**无需系统语言包**（已取代原 `Windows.Media.Ocr` 方案） |
| 翻译 provider 池 | 免费机器翻译：**腾讯云 MT**（TC3-HMAC-SHA256）/ **阿里云 MT**（RPC 签名）＋ LLM：**OpenAI 订阅（ChatGPT/Codex OAuth+Responses）** / **阿里云 DashScope（OpenAI 兼容 Chat）**；池可拖拽排序，回退链先免费后 LLM | 免费额度兜底 + 懂游戏黑话、可加术语表与上下文。**不再用 Claude / Papago / 百度**。详见 `docs/superpowers/specs/2026-07-02-phase2-translation-design.md` |
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
                    │          SceneRecognizer          │  图像指纹：每场景两块静态区，块均值+容差命中比例
                    │   每 200ms 一次，纯内存不跑 OCR     │  见 3.9；带迟滞切换，第四态=其他界面
                    └────────────────┬─────────────────┘
                                     │ 当前场景（决定用哪套区域、是否翻译）
                    ┌────────────────▼─────────────────┐
                    │    区域来源 = 校准 ROI ＋ 布局预设  │  rois.json + layout-presets.json
                    │   裁出: 聊天框 / 房间名列表 / 地图  │  按 场景→分辨率 取，两者叠加
                    └────────────────┬─────────────────┘
                                     │ 每个区域的子图
                    ┌────────────────▼─────────────────┐
                    │     变化检测 ＋ 内容闸门           │  整批文本与上帧相同→跳过(省 API)
                    │  无谚文的行不进翻译(英文 UI 是噪声) │
                    └────────────────┬─────────────────┘
                                     │ 待翻译的韩文行
                    ┌────────────────▼─────────────────┐
                    │           OcrService              │  IOcrEngine (可插拔)
                    │   ROI 子图 → 韩文文本 + 行位置     │
                    └────────────────┬─────────────────┘
                                     │ OcrResult
                    ┌────────────────▼─────────────────┐
                     │      TranslationOrchestrator      │
                     │  ① 术语表/缓存命中 → 直出（零请求）  │
                     │  ② 未命中整批 → 活跃 LLM 一次请求   │
                    └────────────────┬─────────────────┘
                                     │ TranslationResult (事件)
                    ┌────────────────▼─────────────────┐
                    │         OverlayRenderer           │  WPF 透明置顶+点击穿透
                     │  贴近 ROI 显示中文(只渲染真翻出的行) │
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

### 3.2 区域来源：校准 ROI ＋ 布局预设

**两个来源，运行时合并**（`Phase1Probe.EffectiveRois`）：

1. **用户校准的 ROI** —— `rois.json`：`场景 → 分辨率 bucket → Roi[]`。
   每个 ROI = { Name, Type(Chat/RoomList/MapInfo), Rect(相对 0~1), Columns? }。
   存相对比例、按分辨率分桶，界面上一律换算成**整数像素**显示（用户读百分比读不动）。
2. **出厂布局预设** —— `layout-presets.json`：各场景在各分辨率下的**聊天框** +
   寻找房间的**房间列表网格**（列表框 / 行高 / 房间名列 / 地图名列）。

**为什么要有预设**：这些面板在给定分辨率下位置和行高是死的（列表行距实测 1080p=38px、
2K=51px、4K=76px，严格等比），让用户每次拖框既没准头也没必要。数值由截图量得：
列表用**绿色人数槽的 y 周期**定行、**表头亮字团**定列；游戏内聊天用 `ingame-w/h` 两张专用图
定出单行最大宽度与最大行数。2K/4K 等比推算后用叠加图目视核验。

**合并规则：叠加，不顶替。** 校准的 RoomList 框可能是用户有意框在别处（例如右侧详情面板的
Game Name / Map Name 两行），隐式吞掉会让已有校准莫名失效。不想要重复条目就在配置页删掉，
行为可解释、不搞优先级魔法。

**区域改写全部走纯函数、定位失败返回 null**：`RoiStore.ReplaceRoi`（改名/类型/像素矩形）、
`RoiStore.RemoveRoi`（删一条）、`RoiStore.MoveRoi`（挪到另一场景的同分辨率桶，没有就新建）。
三者都按 `(场景, 分辨率, 序号)` 定位，**定位不到就返回 null 让调用方报错**，
绝不"差不多就行"地改到别的场景或别的分辨率上——那会让用户的校准莫名其妙坏掉且极难查。

**列定义**：只有 RoomList 型 ROI 有列。列 = { Kind(RoomName/MapName/PlayerId/Other), 相对框宽的起止 }。
运行时按每行 OCR 文本在框内的水平位置命中列（`TranslationScope.HitColumn`），再由「翻译」页
四个开关决定翻不翻；**未命中任何列一律不翻**（最安全）。房间列表网格的两列之间隔着 Game Type，
所以列必须带 x 起点，只给宽度会把第二列顶到第一列右边、正好盖住 Game Type。

**两层文件（预设与场景参考同一套纪律）**：

| 层 | 位置 | 谁写 | 说明 |
|---|---|---|---|
| 出厂层 | exe 旁 `layout-presets.json` / `scene-refs.json` | 只有量测/生成工具 | 程序**永不改写**；`scene-refs.json` 另带 sha256（覆盖几何+数据），改一个坐标即整份拒用 |
| 覆盖层 | `%APPDATA%\ScrTranslator\layout-overrides.json` / `scene-overrides.json` | 用户在配置页编辑 | 同 (场景,分辨率) 覆盖胜出；删掉即恢复出厂值。升级 exe 不丢用户数据 |

**分辨率必须精确匹配**，缺该档就回落到校准 ROI —— 绝不借用邻近分辨率的数值（UI 非严格等比缩放，
借了会整体偏移）。改完预设配置页会连着调 `probe.ReloadRois()` + `probe.ReloadLayoutPresets()`，
否则 DI 单例里的 store 还是旧值，症状是"我明明改了，框没变"。

**核验工具** `tools/LayoutCheck`：把预设画到真实截图上（复用运行时同一套 `LayoutPresetStore` +
`ToRois()`），所以"核验通过"等价于"实机就是这个框"。游戏改版后跑一次即可定位哪个框歪了。

### 3.3 变化检测与内容闸门（实现在 `Phase1Probe.OnFrame` 内，无独立类）
- **职责**：避免对同一内容重复 OCR/翻译（省 CPU 与 API 费用）。
- **做法**：本帧所有待译行拼成一个字符串 `_lastJoined`，与上一帧相同则整批跳过；
  **只有整批都真翻出来（AllResolved）才提交该状态**，否则下一帧重试 —— 否则一次失败会把错误内容永久"钉"在屏幕上。
- **内容闸门**：`KoreanText.ContainsHangul` —— 整行不含谚文（地图名、`Top vs Bottom`、表头…）直接不进翻译。
- 注：早期设计写的"缩略图 + 感知哈希(aHash/dHash)"与独立的 `ChangeDetector` 类**从未实现**——
  精确文本比对已够用且无误判风险，感知哈希反而要给"文字变了但像素没大变"的场景调阈值。

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

### 3.5 翻译编排（`TranslationOrchestrator`）
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
  4. **翻译 provider 池**（`ITranslationProvider`：腾讯云/阿里云机器翻译 ＋ OpenAI 订阅 / 阿里云 DashScope LLM）按序回退批量调用 → 上屏中文。**单级**（术语/缓存未命中即走 provider 池回退链，取第一个「启用且可用」）。
  5. **单级（原"两级编排"已移除）**：曾实现"快引擎出临时态（灰斜体）→ 主力引擎定稿原地替换"，但这是很窄的翻译任务（游戏聊天/房间名短句），低价模型单发就够好，两级只让每个未命中批次多打一次请求、并多一套临时态视觉与状态分支 → 整体删除（含 `TwoLevel/FastEngine/FastModel` 三字段、`TranslationStage`、托盘与配置页开关）。现由 `TranslationOrchestrator` 编排：术语表 → 缓存 → **provider 池回退链**，一批一次请求。
- **LLM Prompt 要点**：系统提示设定"星际争霸韩服游戏聊天翻译"角色；附术语表；要求口语化、简短、保留游戏黑话语气；ID/数字/英文缩写不乱翻。
- **限流**：LLM 请求合并/节流，避免刷屏时狂发请求。

### 3.6 OverlayRenderer（`OverlayWindow`）
- **职责**：WPF 透明置顶窗，把"这一帧翻出来的中文"叠在对应位置上。
- **窗口**：`AllowsTransparency=true`、`WindowStyle=None`、`Topmost=true`、`Background=Transparent`、
  `ShowInTaskbar=false`、`ShowActivated=false`（不抢游戏焦点）。
- **点击穿透**：扩展样式加 `WS_EX_TRANSPARENT | WS_EX_LAYERED`（P/Invoke `SetWindowLongPtr`），
  在 `SourceInitialized` 拿句柄后开启。
- **渲染方式（与早期设想不同）**：不是滚动列表/`ItemsControl`，而是一张铺满主屏的 `Canvas`，
  每帧 `Children.Clear()` 后按**当前帧**的译文逐条摆放。没有"保留最近 N 条历史"——
  悬浮层的语义是"就地替换画面上的字"，历史聊天由游戏自己滚动。
- **每条一个底衬**：`Border`（黑底 alpha = `Appearance.BackplateOpacity`，默认 0.8，圆角 3 + padding）。
  逐条包而不是整窗铺一层：整窗铺会把游戏画面糊掉一大片，而译文只占几个小点。
  整体不透明度 `Appearance.Opacity` 加在"整条"外层，文字自身 Opacity 只表达相对深浅——
  否则会出现 0.8×0.8 的双重衰减。
- **坐标**：条目带物理屏幕像素坐标，内部按 DPI 换算成 DIP。
- **显隐**：由探测按"游戏是否前台"叠加计算，用 `Visibility`（不用 Show/Hide，避免与游戏抢焦点振荡）。
- **原文+译文双行**（`Appearance.ShowOriginalAndTranslation`）：原文小字(0.72×、灰)在上。
- **只渲染真翻出来的行**：provider 失败时编排回落原文，这类"文本==原文"的行不上屏
  （显示假原文比什么都不显示更误导）。

### 3.7 主管线（`Phase1Probe`）
不是 Channel 队列，而是**捕获事件驱动 + 单飞**：`CaptureService.FrameArrived → OnFrame`，
`_busy` 用 `Interlocked` 保证同时只有一帧在处理（未处理完的帧直接丢弃），避免堆积与阻塞捕获线程。

一帧内的顺序（**这个顺序是踩过坑的**）：
1. 两个独立时钟：`detectDue`(默认 200ms) / `translateDue`(默认 500ms)，都不到点直接丢帧；
   捕获侧帧节流取两者**较快值**（否则识别被翻译的慢节拍拖平，那个 200ms 是假的）。
2. **场景识别**——必须在 `_roiAvailable` 判断**之前**：`_roiAvailable` 是识别的结果，
   放后面会让"其他界面/未校准场景"变成只进不出的死状态（实测症状：识别几次后永远停在其他界面）。
3. ROI 可用性判断 → 逐 ROI 裁剪 → OCR → 按类型/列 + 内容闸门（含谚文才翻）过滤。
4. 变化检测：本批文本与上帧相同则跳过翻译（省 API）；只有整批都真翻出来才提交该状态，否则下帧重试。
5. 翻译（`TranslationOrchestrator`）→ 上屏。

另有 2s 看门狗：窗口丢失自动重连、最小化清屏、还原重译、分辨率切换提示、零帧/卡死诊断。

### 3.8 横切
- **Settings**：`%APPDATA%\ScrTranslator\settings.json`（`SettingsStore`，缺文件/损坏→默认值不崩）。
  密钥与 OAuth token 另存 `secrets.dat`，走 DPAPI(`ProtectedData`, CurrentUser)，**不进 settings.json**。
- **区域与预设**：`rois.json` 与两个覆盖层（`scene-overrides.json` / `layout-overrides.json`）同在
  `%APPDATA%\ScrTranslator`；出厂层在 exe 旁、只读。见 3.2 的两层表。
- **Calibration**：独立的可交互全屏覆盖窗（悬浮层本身始终点击穿透，不变成编辑态）。
  进入前要求游戏在前台，并自己抓帧检测场景，见 UI_DESIGN 第 5 节。
- **Hotkey**：全局热键 5 个（`RegisterHotKey`，可录制重绑 + 冲突检测 + 恢复默认）。
- **日志**：`logs/app.log`，分级（DEBUG 开=全量诊断，关=仅业务），UTF-8 带 BOM，超限轮转。

### 3.9 SceneRecognizer（场景识别 = 图像指纹，不用 OCR）
- **职责**：判断当前帧属于 `browsing`(寻找房间) / `room`(房间内) / `ingame`(游戏内) 哪一个，或三者之外的 `other`(其他界面：主菜单/加载/单人/街机/设置)。场景决定用哪套 ROI、以及**翻不翻**。
- **为什么不用 OCR 关键词**：客户端 UI 虽是英文，但 cue 会随版本/语言变，且要为一帧跑 2 次 OCR（慢、和翻译抢引擎）。实测整帧感知哈希也不行——游戏内视口占大半画面且一直在变，两张游戏内截图整帧指纹只有 62/144 命中。
- **方案**：每个场景自带**两块静态区域**的"块均值 RGB 指纹"，逐块带容差比对，命中率最高者胜出。
  - `browsing` / `room`：顶部金属边框左右两角 `(0,0,250,110)`、`(1650,0,270,110)`（1080p 基准）。
  - `ingame`：底部**种族装饰板** `(420,930,120,120)`、`(1200,910,120,120)`。游戏内除装饰板外全是会变的（视口/小地图/单位面板），所以装饰板是唯一锚点；也因此**必须按种族分套参考**(`ingame_t/p/z`)——顺带就得到了种族信息。
  - 块均值(4×4，随分辨率等比放大)而非逐像素：SC:R 贴图自带噪点，逐像素存原始数据要 2.47MB，块均值后 114KB，且噪点被平均掉，跨场景命中率反而从 37% 降到 24%。
- **实测判别力**：同场景两样本 100% 命中；跨场景/跨种族最高 25.2%；阈值 90% → 余量 65 个百分点。
- **参考数据**：`src/ScrTranslator.App/scene-refs.json`（只读，随包分发）由 `tools/SceneRefGen` 从 `docs/scenes pictures/` 的截图生成，带 **SHA-256 完整性校验（覆盖几何+数据，算法单一实现在 `SceneRefIntegrity`，生成端与加载端共用，不可能写岔）**，被改坏/截断则整份拒用并降级为"其他界面"。用户现场采集的参考写 `%APPDATA%\ScrTranslator\scene-overrides.json`，同 (场景,分辨率) 覆盖出厂值。
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

## 5. 项目目录结构（实际落地）

```
starcraft_translator/
├─ src/
│  ├─ ScrTranslator.App/            # WPF 启动、悬浮层、托盘、配置窗、校准窗、DEBUG 面板
│  │  ├─ Capture/                   # CaptureService(WGC→SKBitmap) + FramePixelsSource
│  │  ├─ Calibration/               # CalibrationWindow（拖框/划列/预设参考框/操作条）
│  │  ├─ Config/                    # SettingsWindow + RoiEditWindow + PresetEditWindow/ColumnEditWindow
│  │  ├─ Auth/ Secrets/ Services/   # OAuth、DPAPI 密钥、托盘/热键/自启
│  │  ├─ Phase1Probe.cs             # 主管线：帧 → 场景 → ROI → OCR → 翻译 → 上屏 + 看门狗
│  │  ├─ scene-refs.json            # 场景识别像素参考（生成物，只读）
│  │  └─ layout-presets.json        # 界面布局预设（量测产物，只读）
│  └─ ScrTranslator.Core/           # 业务逻辑（无 UI、无图像库依赖，可单测）
│     ├─ Roi/                       # RoiStore(含 Replace/Remove/MoveRoi)、TranslationScope、RelRect
│     ├─ Scene/                     # SceneRefStore、SceneFingerprint、SceneRecognizer、SceneRefCapture、LayoutPresets、Scene
│     ├─ Ocr/                       # IOcrEngine + PaddleOcrEngine(RapidOcrNet)
│     ├─ Translation/               # ITranslationProvider、四 provider(腾讯/阿里云 MT + OpenAI/DashScope LLM)、Glossary、Cache、
│     │                             #   TranslationOrchestrator(单级 provider 池回退)、PromptBuilder、BatchProtocol
│     ├─ Settings/                  # AppSettings、SettingsStore、PollInterval(两节拍)
│     └─ KoreanText.cs              # 谚文判定：翻译入口的内容闸门
├─ tests/ScrTranslator.Core.Tests/  # 238 条；含 SceneRefsShippingTests / LayoutPresetTests 回归
├─ tools/
│  ├─ SceneRefGen/                  # 截图 → scene-refs.json（含全量判定矩阵，判错退出码非 0）
│  ├─ LayoutCheck/                  # 把布局预设画到截图上核验
│  ├─ publish.ps1  make-icon.ps1
├─ models/                          # PaddleOCR 韩文 mobile ONNX（det/rec/cls/dict，≈18MB，随包分发）
├─ docs/                            # PRD / TECH_DESIGN / UI_DESIGN / ROADMAP / E2E_CHECKLIST / GLOSSARY
└─ ScrTranslator.slnx
```

> 早期设想的 `ScrTranslator.Overlay/`、`ScrTranslator.Common/`、`Pipeline/` 未拆出来：悬浮层与
> 配置/校准都是 WPF 窗口，和启动器同进程同工程最省事；Core 与 UI 的分离才是关键（为了能单测
> 指纹、编排、区域换算），这一条做到了。
> `docs/scenes pictures/` 是量测/生成的**本机输入**，已 gitignore（近百 MB），不入库。

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
| 刷屏性能/费用 | 变化检测 + 文本去重 + 缓存 + 术语表直出 + 一批一请求 |
| ID 是否翻译 | 配置项 `TranslatePlayerId`(默认 false)；RoomList 的 ID 列据此决定（纯配置，无热键） |
| 分辨率适配 | 相对坐标 / 按分辨率存多套 ROI |
| 判断当前是哪个界面 | **图像指纹**：每场景两块静态区，块均值 RGB + 容差命中比例（见 3.9），不用 OCR |
| 游戏内识别不稳 | 只用种族装饰板做锚点，参考按种族分套；缺该分辨率参考时报错而非硬猜 |
| 界面位置固定却要反复校准 | 布局预设内置（聊天框 + 房间列表网格，见 3.2），按分辨率精确匹配；校准与预设叠加 |
| 预设数值对不对 | `tools/LayoutCheck` 把预设画到真实截图上核验，复用运行时同一套换算 |
| 校准框不准 / 误触 | 校准窗自抓帧检测场景（不依赖 F9）；新建框最小 20 物理像素；操作条可拖走、不溢出窗口 |
| 翻译失败被当成译文 | provider 异常必须上报（不空 catch）；"文本==原文"的行不上屏；F9 时体检凭据 |

---

## 7. 第三方依赖（预估 NuGet）

- `Microsoft.Windows.CsWinRT`（WinRT 互操作，Graphics.Capture / Media.Ocr）
- `Microsoft.Extensions.DependencyInjection` / `.Hosting` / `.Logging`
- `System.Drawing.Common` 或 `SixLabors.ImageSharp`（图像处理）
- `Microsoft.Extensions.Caching.Memory`（缓存）
- 翻译：**裸 HttpClient**（不用 SDK）——腾讯云 MT（TC3-HMAC-SHA256 签名，`TranslateText`）、阿里云 MT（RPC 签名，`Translate`）、阿里云 DashScope（OpenAI 兼容 `/chat/completions`）、OpenAI 订阅（Codex OAuth + `/responses` SSE 流式）；密钥/token 走 `SecretStore`（DPAPI）
- OCR：`RapidOcrNet` + 韩文 mobile 模型（已采用）

---

## 8. 测试策略

- **可单测**（Core，当前 220 条全绿）：Glossary 匹配、缓存、ID 列过滤、LLM prompt 组装、文本去重、
  **场景指纹**（`SceneFingerprint`/`SceneRecognizer` 用合成帧跑；`SceneRefStore` 覆盖解析/校验和/覆盖层合并；
  `SceneRefCapture` 覆盖现场缩放几何/块均值指纹采集/写覆盖层与合并回退；
  `SceneRefsShippingTests` 回归验证出厂参考真的互相区分得开）、
  **布局预设**（三种相对口径各自正确、精确匹配不借用、坏数据只丢该字段并告警、
  覆盖层读写与回退、`LayoutPresetTests` 回归验证出厂三场景×三分辨率齐全且不越画面）、
  **区域改写**（`RoiStoreEditTests`：Replace/Remove/Move 只动目标条目、定位失败返回 null）、
  **内容闸门**（`KoreanTextTests`：真实 UI 英文串必须判"不含谚文"）、
  **两节拍**（`PollIntervalTests`：捕获闸门取较快值）。
- **手动验证**：捕获/悬浮/OCR/端到端、校准交互与预设落位需在真实游戏窗口上验证（见 `E2E_CHECKLIST.md`）。
  单测覆盖不到的主要是：真机帧像素与截图是否一致、DPI/缩放、前台判定、拖框手感。
