# 开发路线图 — SC:R 实时翻译软件

> 每个 Phase 都以"可验证的产出"为目标。建议逐 Phase 验收后再进下一阶段。

## Phase 0 — 工程骨架 + 透明置顶悬浮窗 🏗️
**目标**：地基。一个能浮在游戏上、点击穿透的透明窗口。
- [x] 在 Windows 装 .NET 10 SDK（`dotnet --version` 可用，已确认 10.0.301）
- [x] 建解决方案与工程骨架（可先单工程，目标框架 `net10.0-windows10.0.19041.0`，`UseWPF=true`）
- [x] WPF 透明置顶窗：`AllowsTransparency`、`Topmost`、无边框、透明背景
- [x] P/Invoke 实现**点击穿透**（`WS_EX_TRANSPARENT|WS_EX_LAYERED`）
- [x] 窗口上画一行测试中文，确认浮在其它窗口之上且不挡鼠标
- [x] 系统托盘图标 + 退出

**验收**：开着记事本/游戏，悬浮窗里的中文浮在最上层，鼠标能点穿到下层。

## Phase 1 — 窗口捕获 + 框选区域 + 韩文 OCR 🔍
**目标**：看得见韩文文本（先打印到控制台/调试输出）。✅ 完成（实现路径与原计划略有出入，如下）。
- [x] 按进程/标题找到 SC:R 窗口句柄（`WindowFinder`，进程名可配置 `General.TargetProcess`）
- [x] `Windows.Graphics.Capture` 抓该窗口画面（`CaptureService`，手写 WinRT/D3D11 互操作）
- [x] "框选一个区域"工具（落地于 Phase 4 的 `CalibrationWindow`：拖框/命名/选类型/划列/按分辨率存 `rois.json`）
- [x] 裁出 ROI 子图，**PaddleOCR**（RapidOcrNet + 韩文 mobile 模型，随包分发）识别韩文——**取代原 `Windows.Media.Ocr` 方案，无需系统语言包**
- [x] 把识别出的韩文 + 位置打印出来（`FileLogger` + 悬浮上屏）

**验收**：框住游戏里一段韩文，悬浮窗稳定显示翻译。⏳ 实机验收待用户（历史开发中以韩文样图单测 + 桌面实跑验证 OCR）。

## Phase 2 — 接 LLM 翻译 + 悬浮显示中文（端到端 MVP）🚀
**目标**：最小可用闭环。**方向已改**：不用 Papago/百度快翻，改 **OpenAI 订阅 + 阿里云 DashScope 两个 LLM provider，单活跃可切换**（见 `docs/superpowers/specs/2026-07-02-phase2-translation-design.md`）。
- [x] 实现 `ITranslationProvider` + 两 provider（DashScope=OpenAI兼容Chat；ChatGPT订阅=Codex OAuth+Responses流式），单活跃可切换
- [x] OCR 文本 → 翻译 → 悬浮窗在 ROI 位置显示中文（Phase1Probe 接 `TranslationService`）
- [x] 变化检测 + 缓存（LRU），避免重复翻译同一句
- [x] 术语表精确命中直出（`Glossary` 读 `GLOSSARY.md`）
- [x] API Key / OAuth token 走 **DPAPI 加密**（`SecretStore`）+ 最小托盘配置入口（登录/填Key/选引擎）
- [ ] **实机 e2e 验收（待用户）**：真实 SC:R + 网络 + OAuth 登录 + 上屏中文

**验收**：游戏房间里出现一句韩文，数秒内悬浮显示出中文。代码已完成并合入开发主线（`phase4-multiregion-ui`），Core 单测全绿，待实机 e2e。

## Phase 3 — LLM 两级翻译 + 术语表 + 缓存 🧠
**目标**：翻译质量到位。
- [x] 两级编排：快态（DashScope qwen-flash）临时态 → 质量态（Active 引擎）最终态原地替换（视觉区分：临时灰斜体/最终白）。默认关闭(opt-in)，托盘可开。
- [x] system prompt 注入术语表 + 场景（`PromptBuilder`）
- [x] 加载 `GLOSSARY.md`/数据文件，命中条目直出（Phase 2 已完成）
- [x] LLM 节流/合并，防刷屏（复用变化检测+单飞+缓存存最终态；两级不加压）
- [x] Core 层单测：编排(单/两级)、prompt 组装、术语枚举、ctx 注入

**验收**：常见黑话(`ㅋㅋ`、`gg`、`러시`、`앞마당` 等)翻译准确；普通句子通顺；临时态→最终态可见替换。

## Phase 4 — 多区域 + 校准 UI + 配置窗口 🎛️
**目标**：好用、可配置。按 [UI_DESIGN.md](UI_DESIGN.md) 实现。✅ 代码完成（M1 数据基座 / M2 配置窗 / M3 校准窗 / M4 热键系统），实机可视验收待用户。
- [x] 多 ROI 管理：聊天 / 房间名列表 / 地图信息（`rois.json` v2，按类型门控）
- [x] 房间名列表列定义：房间名/地图列翻；ID 列据配置 `TranslatePlayerId`(默认 false) 决定（`TranslationScope` 纯判定 + `Phase1Probe` 逐行接线）
- [x] 校准模式 UI：拖框定义区域、命名、选类型、划列、按分辨率保存（`CalibrationWindow`，保存后热重载无需重启）
- [x] 配置窗口（左侧竖直标签 6 页 + 关于）：常规 / 翻译(含**翻译玩家 ID 开关**) / OCR / 区域 / 热键 / 外观；应用/确定/取消；密钥只走 SecretStore
- [x] 系统托盘菜单 + 双击打开配置
- [x] 全局热键：开关翻译(F9) / 显隐悬浮(F10) / 校准(F8) / 配置(Ctrl+F9) / **切换翻译玩家 ID(F7)** / 暂停(F11)，可自定义（录制式重绑 + 冲突检测 + 恢复默认 + 注册失败标注）

**验收**：换分辨率后重校准即可用；默认玩家 ID 原样、开启开关或按 F7 后 ID 被翻；配置窗口各项生效、热键可改且冲突有提示。⏳ 用户实机可视。

## Phase 5 — 性能 / 稳定性 / 打包 📦
**目标**：可发布。
- [x] 变化检测与轮询频率调优（`Ocr.ChatPollMs/OtherPollMs` 接线捕获/探测节流，即时生效；预处理/灵敏度字段实测无收益→已从 UI 与 OcrSettings 移除，见 UI_DESIGN §3.3 注）
- [x] 异常恢复（看门狗 2s：窗口丢失→释放会话+自动重连新句柄并气泡提示；最小化→清空悬浮防旧译文滞留，还原→重译；分辨率切换→等比映射兜底+一次性提示按 F8 重校准；F11 暂停期间不执行恢复动作）
- [x] 首次使用引导（F9 启动翻译的开/关与失败原因托盘气泡直出：未校准→按 F8、未找到窗口→提示窗口化运行、系统不支持捕获→说明要求）
- [x] 开机自启（`General.AutoStart` 接线 HKCU Run 键，保存设置即写/删；开发期指向 Debug exe）/ 启动自动翻译（`AutoTranslateOnLaunch`：程序启动即开翻译，看门狗等待游戏窗口出现自动续译）；启动最小化=程序无主窗口恒为真（复选框只读说明）
- [x] 打包（`tools/publish.ps1`：win-x64 框架依赖单文件 27.6MB + models/GLOSSARY/rois 随附 + zip；图标 `tools/make-icon.ps1` 生成 app.ico 并设为 ApplicationIcon；版本号 csproj `<Version>` 驱动"关于"页显示）；安装器暂以 zip 替代
- [x] ~~（可选）PaddleOCR 引擎接入~~ —— 已成为 Phase 1 起的主引擎

**验收**：连续游戏 1 小时稳定；冷启动到可用 < 1 分钟；安装包能在干净 Windows 上运行。

## Phase 6 — 场景识别：图像指纹替换 OCR 🛰️
**目标**：让"现在该翻哪个界面"判得准、判得快、判得可解释。详见 `docs/TECH_DESIGN.md` §3.9。
- [x] 场景统一命名 `browsing`(寻找房间) / `room`(房间内) / `ingame`(游戏内) + 第四态 `other`(其他界面，不翻译)；老 `rois.json` 的 `custom_games_listing`/`waiting_room`/`in_game` 加载时自动映射，用户不必重校准
- [x] 参考生成器 `tools/SceneRefGen`：读 `docs/scenes pictures/` 截图 → 出 `scene-refs.json` → **自带全量验证矩阵**（判定有错退出码非 0，区域选坏当场发现）
- [x] 指纹方案：每场景两块静态区、块均值 RGB(4×4，随分辨率等比放大)、逐块容差 + 命中比例；**区域由参考自带**（不存在对所有场景都静态的区域）
- [x] `ingame` 按种族分套(`ingame_t/p/z`)——游戏内除底部装饰板外全是动态内容，装饰板是唯一稳定锚点；顺带识别出种族
- [x] 实测判别力：同场景两样本 100%，跨场景/跨种族最高 25.2%，阈值 90%（余量 65pp）；三分辨率 15/15 样本判定正确
- [x] 加载器 `SceneRefStore`（Core，不依赖图像库）+ `SceneRecognizer` 纯函数 + `FramePixels` 只读像素视图
- [x] 完整性：`integrity.sha256` 覆盖**几何+数据**，手改一个 rect 坐标也会整份拒用并降级为"其他界面"（而不是拿错位指纹误判）
- [x] 数据分层：出厂层 exe 旁只读；用户现场采集写 `%APPDATA%\ScrTranslator\scene-overrides.json`，同 (场景,分辨率) 覆盖胜出，升级 exe 不丢
- [x] 识别不再跑 OCR → 从 async 降为同步纯内存（<1ms），不再与翻译请求抢 OCR 线程
- [x] DEBUG 面板改显示：当前场景+种族、判定摘要、**每个候选各多少分**、参考加载状态与参数（替换原"识别文本"行）
- [x] 单测 153/153（新增 53：键归一/指纹/识别四态/校验和/覆盖层合并 + 出厂文件回归）
- [ ] 校准窗增加"采集当前场景指纹"（现场补新分辨率/新场景参考的 UI）
- [ ] 按识别到的种族选聊天 ROI（装饰板宽度随种族不同）
- [ ] 实机验收：4K 下三场景 + 主菜单/加载来回切，看 `other` 态进出是否正确

---

## 里程碑

- **M1 = Phase 0–2 完成**：端到端能用的最小翻译器。
- **M2 = Phase 3–4 完成**：质量与易用性达标的 v0.1。
- **M3 = Phase 5 完成**：可发布版本。
