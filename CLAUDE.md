# CLAUDE.md — 项目说明（给 Claude Code）

## 这个项目是什么
《星际争霸：重制版》(SC:R) **实时屏幕翻译软件**：抓游戏窗口画面 → 区域 OCR 韩文 → LLM 翻译（术语表/缓存命中直出，否则调 provider）→ 透明置顶悬浮窗显示中文。主要翻译**联机聊天、战网房间名、地图信息**（韩→中）；**玩家 ID 不翻译**。**翻译提供方 = OpenAI 订阅（ChatGPT/Codex OAuth）+ 阿里云 DashScope，单活跃可切换（不再用 Claude）**。

## 先读这些文档
- 需求：`docs/PRD.md`
- 架构与实现方案：`docs/TECH_DESIGN.md`
- 界面设计（配置窗口/悬浮层/托盘/校准/热键）：`docs/UI_DESIGN.md`
- 开发计划（按 Phase 推进）：`docs/ROADMAP.md`
- 韩文术语表：`docs/GLOSSARY.md`

**当前进度**：Phase 0（骨架/悬浮窗）、Phase 1（捕获+ROI+PaddleOCR+上屏）、Phase 2（翻译层：两 provider 单活跃 + 缓存 + DPAPI + 术语表 + 托盘入口）、Phase 3（两级编排 opt-in，默认关闭）、**Phase 4（多区域门控/校准窗/配置窗六页/热键系统 RegisterAll+Rebind+冲突检测）代码全部完成**，在分支 `phase4-multiregion-ui`；Core 单测 92/92 绿。**实机 e2e 验收（真实 SC:R）待用户**。部分设置字段（轮询频率、图像预处理、变化灵敏度、开机自启、原文+译文显示）仅存盘未接线，标注"（后续生效）"——属 Phase 5 范围（性能调优/异常恢复/首次引导/打包）。

## 技术栈与环境
- **.NET 10 (LTS) + WPF**，目标框架 `net10.0-windows10.0.19041.0`，`UseWPF=true`。
- 屏幕捕获 `Windows.Graphics.Capture`（WinRT，经 CsWinRT）。OCR 用 **PaddleOCR（RapidOcrNet + 韩文 mobile 模型，随包分发）**，非 Windows.Media.Ocr——**无需系统韩文语言包**。
- **仅 Windows 10 1803+ / 11**。⚠️ **必须在 Windows 端编译运行**——WSL 不支持 WPF / Graphics.Capture / WinRT。
- 编译/运行用 `dotnet build` / `dotnet run`（或 Visual Studio 2022）。解决方案文件是 `ScrTranslator.slnx`。

## 关键约束（务必遵守）
1. SC:R 须以**窗口化/无边框**运行，独占全屏会盖住悬浮窗——代码与文档都要体现这一前提。
2. 悬浮窗必须**点击穿透**（`WS_EX_TRANSPARENT|WS_EX_LAYERED`），不能抢游戏焦点。
3. **玩家 ID 默认不翻译**，但这是**可配置项** `Translation.TranslatePlayerId`（默认 false，可用热键 F7 切换）；房间列表按列区分，ID 列据此开关决定。
4. 翻译提供方 **OpenAI 订阅 + 阿里云 DashScope 单活跃可切换**（托盘/配置选定）；命中术语表/缓存直出，省费用。**两级编排（快态→终态原地替换）是 Phase 3 目标，Phase 2 为单级**。
5. API Key / OAuth token 本地 **DPAPI 加密**存储（`SecretStore`），除翻译/OAuth 请求外不上传数据。
6. **纯读屏**——不注入游戏进程、不改游戏文件、不读游戏内存（降低封号风险）。

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
