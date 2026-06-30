# CLAUDE.md — 项目说明（给 Claude Code）

## 这个项目是什么
《星际争霸：重制版》(SC:R) **实时屏幕翻译软件**：抓游戏窗口画面 → 区域 OCR 韩文 → 两级翻译(快引擎+Claude LLM) → 透明置顶悬浮窗显示中文。主要翻译**联机聊天、战网房间名、地图信息**（韩→中）；**玩家 ID 不翻译**。

## 先读这些文档
- 需求：`docs/PRD.md`
- 架构与实现方案：`docs/TECH_DESIGN.md`
- 界面设计（配置窗口/悬浮层/托盘/校准/热键）：`docs/UI_DESIGN.md`
- 开发计划（按 Phase 推进）：`docs/ROADMAP.md`
- 韩文术语表：`docs/GLOSSARY.md`

**当前进度**：设计完成，待开始 **Phase 0**。

## 技术栈与环境
- **.NET 10 (LTS) + WPF**，目标框架 `net10.0-windows10.0.19041.0`，`UseWPF=true`。
- 屏幕捕获 `Windows.Graphics.Capture`、OCR `Windows.Media.Ocr`（WinRT，经 CsWinRT）。
- **仅 Windows 10 1803+ / 11**。⚠️ **必须在 Windows 端编译运行**——WSL 不支持 WPF / Graphics.Capture / WinRT。
- 编译/运行用 `dotnet build` / `dotnet run`（或 Visual Studio 2022）。OCR 需系统安装**韩文 OCR 语言包**。

## 关键约束（务必遵守）
1. SC:R 须以**窗口化/无边框**运行，独占全屏会盖住悬浮窗——代码与文档都要体现这一前提。
2. 悬浮窗必须**点击穿透**（`WS_EX_TRANSPARENT|WS_EX_LAYERED`），不能抢游戏焦点。
3. **玩家 ID 默认不翻译**，但这是**可配置项** `Translation.TranslatePlayerId`（默认 false，可用热键 F7 切换）；房间列表按列区分，ID 列据此开关决定。
4. 翻译走**两级**：快引擎临时态 → Claude LLM 最终态原地替换；命中术语表/缓存直出，省费用。
5. API Key 本地 **DPAPI 加密**存储，除翻译请求外不上传数据。
6. **纯读屏**——不注入游戏进程、不改游戏文件、不读游戏内存（降低封号风险）。

## 代码约定
- 业务逻辑放 `ScrTranslator.Core`（无 UI 依赖，可单测）；UI/悬浮分离。
- OCR、翻译引擎都走接口（`IOcrEngine`、`ITranslationProvider`）可插拔。
- 目录结构见 `docs/TECH_DESIGN.md` 第 5 节。
- 用 DI（Microsoft.Extensions.DependencyInjection）组织模块。

## Claude API 使用
- LLM 翻译用 Claude；模型 id 与用法以 `claude-api` 技能/官方文档为准（速度档 `claude-haiku-4-5`，质量档 `claude-sonnet-4-6`）。需要时先查 `claude-api` 技能，不要凭记忆写模型 id/参数。

## 回复语言
- 用**中文**回复。
