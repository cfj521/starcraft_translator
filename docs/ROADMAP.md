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
**目标**：看得见韩文文本（先打印到控制台/调试输出）。
- [ ] 按进程/标题找到 SC:R 窗口句柄
- [ ] `Windows.Graphics.Capture` 抓该窗口画面
- [ ] 简单"框选一个区域"工具，得到 ROI 矩形
- [ ] 裁出 ROI 子图，`Windows.Media.Ocr` 识别韩文（需装韩文 OCR 语言包）
- [ ] 把识别出的韩文 + 位置打印出来

**验收**：框住游戏里一段韩文（或先用韩文图片/记事本验证），控制台稳定打印出正确韩文。

## Phase 2 — 接 LLM 翻译 + 悬浮显示中文（端到端 MVP）🚀
**目标**：最小可用闭环。**方向已改**：不用 Papago/百度快翻，改 **OpenAI 订阅 + 阿里云 DashScope 两个 LLM provider，单活跃可切换**（见 `docs/superpowers/specs/2026-07-02-phase2-translation-design.md`）。
- [x] 实现 `ITranslationProvider` + 两 provider（DashScope=OpenAI兼容Chat；ChatGPT订阅=Codex OAuth+Responses流式），单活跃可切换
- [x] OCR 文本 → 翻译 → 悬浮窗在 ROI 位置显示中文（Phase1Probe 接 `TranslationService`）
- [x] 变化检测 + 缓存（LRU），避免重复翻译同一句
- [x] 术语表精确命中直出（`Glossary` 读 `GLOSSARY.md`）
- [x] API Key / OAuth token 走 **DPAPI 加密**（`SecretStore`）+ 最小托盘配置入口（登录/填Key/选引擎）
- [ ] **实机 e2e 验收（待用户）**：真实 SC:R + 网络 + OAuth 登录 + 上屏中文

**验收**：游戏房间里出现一句韩文，数秒内悬浮显示出中文。代码完成于分支 `phase2-translation`（46 Core 单测绿），待实机 e2e。

## Phase 3 — LLM 两级翻译 + 术语表 + 缓存 🧠
**目标**：翻译质量到位。
- [x] 两级编排：快态（DashScope qwen-flash）临时态 → 质量态（Active 引擎）最终态原地替换（视觉区分：临时灰斜体/最终白）。默认关闭(opt-in)，托盘可开。
- [x] system prompt 注入术语表 + 场景（`PromptBuilder`）
- [x] 加载 `GLOSSARY.md`/数据文件，命中条目直出（Phase 2 已完成）
- [x] LLM 节流/合并，防刷屏（复用变化检测+单飞+缓存存最终态；两级不加压）
- [x] Core 层单测：编排(单/两级)、prompt 组装、术语枚举、ctx 注入

**验收**：常见黑话(`ㅋㅋ`、`gg`、`러시`、`앞마당` 等)翻译准确；普通句子通顺；临时态→最终态可见替换。

## Phase 4 — 多区域 + 校准 UI + 配置窗口 🎛️
**目标**：好用、可配置。按 [UI_DESIGN.md](UI_DESIGN.md) 实现。
- [ ] 多 ROI 管理：聊天 / 房间列表 / 地图信息
- [ ] 房间列表列定义：房间名/地图列翻；ID 列据配置 `TranslatePlayerId`(默认 false) 决定
- [ ] 校准模式 UI：拖框定义区域、命名、选类型、划列、按分辨率保存
- [ ] 配置窗口（多标签页）：常规 / 翻译(含**翻译玩家 ID 开关**) / OCR / 区域 / 热键 / 外观
- [ ] 系统托盘菜单 + 双击打开配置
- [ ] 全局热键：开关翻译(F9) / 显隐悬浮(F10) / 校准(F8) / 配置(Ctrl+F9) / **切换翻译玩家 ID(F7)** / 暂停(F11)，可自定义

**验收**：换分辨率后重校准即可用；默认玩家 ID 原样、开启开关或按 F7 后 ID 被翻；配置窗口各项生效、热键可改且冲突有提示。

## Phase 5 — 性能 / 稳定性 / 打包 📦
**目标**：可发布。
- [ ] 变化检测与轮询频率调优，降空闲占用
- [ ] 异常恢复（窗口丢失、最小化、分辨率切换）
- [ ] 首次使用引导（提示窗口化运行 + 校准）
- [ ] 打包（单文件发布 / 安装器）、图标、版本号
- [ ] （可选）PaddleOCR 引擎接入作为高精度选项

**验收**：连续游戏 1 小时稳定；冷启动到可用 < 1 分钟；安装包能在干净 Windows 上运行。

---

## 里程碑

- **M1 = Phase 0–2 完成**：端到端能用的最小翻译器。
- **M2 = Phase 3–4 完成**：质量与易用性达标的 v0.1。
- **M3 = Phase 5 完成**：可发布版本。
