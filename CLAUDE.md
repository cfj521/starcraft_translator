# CLAUDE.md — 项目说明（给 Claude Code）

## 这个项目是什么
《星际争霸：重制版》(SC:R) **实时屏幕翻译软件**：抓游戏窗口画面 → 区域 OCR 韩文 → LLM 翻译（术语表/缓存命中直出，否则调 provider）→ 透明置顶悬浮窗显示中文。主要翻译**联机聊天、战网房间名、地图信息**（韩→中）；**玩家 ID 不翻译**。**翻译提供方 = OpenAI 订阅（ChatGPT/Codex OAuth）+ 阿里云 DashScope，单活跃可切换（不再用 Claude）**。

## 先读这些文档
- 需求：`docs/PRD.md`
- 架构与实现方案：`docs/TECH_DESIGN.md`
- 界面设计（配置窗口/悬浮层/托盘/校准/热键）：`docs/UI_DESIGN.md`
- 开发计划（按 Phase 推进）：`docs/ROADMAP.md`
- 韩文术语表：`docs/GLOSSARY.md`

**当前进度**：Phase 0–6 代码完成（分支 `phase4-multiregion-ui`，版本 0.4.0）。管线：捕获(WGC→SKBitmap) → **场景识别（图像指纹，非 OCR）** → 区域（校准 ROI ＋ 内置布局预设叠加）→ 变化检测 ＋ 内容闸门（含谚文才翻）→ PaddleOCR → 单级翻译（术语表/缓存直出，未命中一批一请求）→ 悬浮上屏（每条带 80% 黑底）。场景统一命名 browsing/room/ingame + other 四态；参考 `scene-refs.json`（含 sha256 覆盖几何+数据）由 `tools/SceneRefGen` 生成，实测同场景 100%、跨场景最高 25.2%、阈值 90%。布局预设 `layout-presets.json`（聊天框 + 房间列表网格，按分辨率量得）+ 覆盖层 `layout-overrides.json`，`tools/LayoutCheck` 可核验落位。两个正交节拍：`SceneDetectPollMs`=200 / `TranslatePollMs`=500（捕获闸门取较快值）。其余：配置窗六页、托盘、8 个热键、看门狗（窗口丢失重连/最小化清屏/分辨率提示/零帧与卡死诊断）、开机自启、启动自动翻译、DEBUG 面板（F6）+ 诊断复制（Ctrl+F6）、单实例守护、分级日志 `logs/app.log`、单文件打包。已移除：两级翻译、OCR 关键词场景判定、假设置（预处理/灵敏度/ChatPollMs）。Core 单测 207/207 绿。**剩余：实机 e2e 验收（真实 SC:R，需用户）**——按 `docs/E2E_CHECKLIST.md` 逐项勾，尤其 §4 场景识别与 §5 布局预设/校准。发布产物在 `publish/`（已 gitignore）。

## 技术栈与环境
- **.NET 10 (LTS) + WPF**，目标框架 `net10.0-windows10.0.19041.0`，`UseWPF=true`。
- 屏幕捕获 `Windows.Graphics.Capture`（WinRT，经 CsWinRT）。OCR 用 **PaddleOCR（RapidOcrNet + 韩文 mobile 模型，随包分发）**，非 Windows.Media.Ocr——**无需系统韩文语言包**。
- **仅 Windows 10 1803+ / 11**。⚠️ **必须在 Windows 端编译运行**——WSL 不支持 WPF / Graphics.Capture / WinRT。
- 编译/运行用 `dotnet build` / `dotnet run`（或 Visual Studio 2022）。解决方案文件是 `ScrTranslator.slnx`。

## 关键约束（务必遵守）
1. SC:R 须以**窗口化/无边框**运行，独占全屏会盖住悬浮窗——代码与文档都要体现这一前提。
2. 悬浮窗必须**点击穿透**（`WS_EX_TRANSPARENT|WS_EX_LAYERED`），不能抢游戏焦点。
3. **玩家 ID 默认不翻译**，但这是**可配置项** `Translation.TranslatePlayerId`（默认 false，可用热键 F7 切换）；房间列表按列区分，ID 列据此开关决定。
4. 翻译提供方 **OpenAI 订阅 + 阿里云 DashScope 单活跃可切换**（托盘/配置选定）；命中术语表/缓存直出，省费用。**翻译为单级：一批未命中只发一次请求**（曾实现过"两级=快引擎临时态→主力定稿原地替换"，因任务很窄、低价模型单发已够好而移除；勿再引入）。
5. API Key / OAuth token 本地 **DPAPI 加密**存储（`SecretStore`），除翻译/OAuth 请求外不上传数据。
6. **纯读屏**——不注入游戏进程、不改游戏文件、不读游戏内存（降低封号风险）。
7. **场景名一律 `browsing` / `room` / `ingame`**（枚举 `GameScene`、`rois.json` 键、提示词、文档同一套）；`other`=游戏前台但不属三场景，此态**不翻译**。老校准文件的 `custom_games_listing`/`waiting_room`/`in_game` 由 `GameSceneIds.Normalize` 兼容。
8. **`src/ScrTranslator.App/scene-refs.json` 是生成物，禁止手改**：它是场景识别的出厂参考（含 sha256 覆盖"几何+数据"），改一个坐标就会整份被拒用。要变区域/补分辨率 → 改 `tools/SceneRefGen/Program.cs` 的 `SceneDefs` 并重跑该工具（它会顺便打印全量判定矩阵，判错退出码非 0）。用户现场采集写 `%APPDATA%\ScrTranslator\scene-overrides.json`，不写回出厂文件。
   **`docs/scenes pictures/` 已 gitignore，不要提议把它加进版本库**（近百 MB，用户已明确否决）：截图只当 tools/SceneRefGen 的本机输入，入库的只有生成出来的 scene-refs.json。需要新截图直接找用户要，别去动忽略规则。
8b. **`src/ScrTranslator.App/layout-presets.json` 是量测产物**（界面布局预设：各场景各分辨率的聊天框 + 寻找房间的房间列表网格）。这些面板位置固定，**运行时优先用预设、不需要校准**；校准结果与预设**叠加**而非互相顶替（否则用户已有的框会莫名失效）。坐标是该分辨率的**整数像素**，分辨率必须精确匹配、绝不借用邻近档。用户在配置页改预设写进 `%APPDATA%\ScrTranslator\layout-overrides.json`（同 (场景,分辨率) 覆盖，删掉即恢复出厂），**出厂文件永不改写**。改完用 `tools/LayoutCheck` 把框画到截图上核验——它复用运行时同一套 `LayoutPresetStore` + `ToRois()`，核验结果不会与实机脱节。
8c. **校准的前置与顺序不可回退**：① 进校准必须游戏已启动且**在前台**（校准窗是半透明覆盖层且靠抓屏检测场景，看不见画面时框出来全是凭空的）；② 校准窗**自己抓帧做场景检测**，不依赖 F9 是否开启，检测出的场景才决定框存进哪个桶；③ 主管线里**场景识别必须在 `_roiAvailable` 判断之前**——`_roiAvailable` 是识别的结果，放后面会让"其他界面/未校准场景"变成只进不出的死状态（已犯过一次）。
9. 场景截图命名（`docs/scenes pictures/`）：分辨率 `1`=1920x1080 / `2`=2560x1440 / `4`=3840x2160；`ingame` 再带种族字母 `t`/`p`/`z`。必须是**无损 PNG + 1:1 物理像素**（JPG 或缩放图会让指纹整体失效）。`ingame-w*`/`ingame-h*` 是量聊天框用的，不参与识别。
10. **改了代码要同步改文档**：`dotnet test` 里的 `DocsConsistencyTests` 会拦四件事——代码里的类/文件/工具名文档一次都没提、已删除的名字以"现役"口吻出现在文档里、标为退役的名字又回到源码、文档指向不存在的路径。新增功能时在 `LiveSymbols` 表加一行，删除功能时把它移进 `RetiredSymbols`（历史/兼容说明所在行需带"已移除/旧/曾/→"等退役标记）。这张表就是活文档的索引，别让它过期。

## 代码约定
- 业务逻辑放 `ScrTranslator.Core`（无 UI 依赖，可单测）；UI/悬浮分离。
- OCR、翻译引擎都走接口（`IOcrEngine`、`ITranslationProvider`）可插拔。
- 目录结构见 `docs/TECH_DESIGN.md` 第 5 节。
- 用 DI（Microsoft.Extensions.DependencyInjection）组织模块。

## LLM 提供方（应用翻译用，非 Claude Code 本身）
- **不再用 Claude**。两个 provider，裸 HttpClient 实现（`ScrTranslator.Core.Translation.Providers`）：
  - **阿里云 DashScope**：OpenAI 兼容端点 `.../compatible-mode/v1` 的 Chat Completions，API Key 鉴权（默认模型待核实官方表，如 `qwen-flash`）。
  - **OpenAI 订阅（ChatGPT/Codex）**：Codex OAuth（PKCE + 本地 1455 回调 + refresh）→ Responses 流式 API `https://chatgpt.com/backend-api/codex`（默认 `gpt-5`，实现期确认 codex 接受的 id）。参考实现见 `D:\sanyan\projects\news-videos-workflow`（勿搬真实密钥）。
- 模型 id 若需改，按各家官方模型表核实，不要凭记忆写。

## 回复语言
- 用**中文**回复。
