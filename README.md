# SC:R Translator — 星际争霸：重制版实时翻译软件

一个 Windows 桌面软件，实时识别《星际争霸：重制版》(StarCraft: Remastered) 游戏画面中的**韩文**（联机聊天、战网房间名、玩家 ID、地图信息），翻译成**中文**并以悬浮窗覆盖显示在游戏上。

> 主要场景：在韩服/国际服联机时，看懂对手与房间里的韩文聊天、房间名和地图信息。

## 一句话技术方案

`抓取 SC:R 窗口画面 → 区域(ROI)裁剪 → 变化检测 → 韩文 OCR(PaddleOCR) → 翻译(术语表/缓存直出，否则 LLM；可选两级=快态先行、终态原地替换) → 透明置顶悬浮窗显示中文`

## 技术栈

- **.NET 10 (LTS) + WPF**（透明置顶、点击穿透悬浮窗）
- 屏幕捕获：`Windows.Graphics.Capture`（抓窗口，非独占全屏）
- OCR：**PaddleOCR**（RapidOcrNet + 韩文 mobile ONNX 模型，随包分发，**无需系统韩文语言包**）
- 翻译：**OpenAI 订阅（ChatGPT/Codex OAuth+Responses）** 与 **阿里云 DashScope（OpenAI 兼容）** 单活跃可切换；术语表/LRU 缓存命中直出；可选两级编排（快态 qwen-flash 临时显示 → 质量态原地替换）。密钥走 DPAPI 本地加密。
- 平台：**仅 Windows 10 1803+ / Windows 11**（WPF + Graphics.Capture 必须在 Windows 端编译运行）

## 文档导航

| 文档 | 内容 |
|------|------|
| [docs/PRD.md](docs/PRD.md) | 产品需求：目标、场景、功能/非功能需求、验收标准 |
| [docs/TECH_DESIGN.md](docs/TECH_DESIGN.md) | 技术设计：架构、模块、数据流、关键难点方案 |
| [docs/UI_DESIGN.md](docs/UI_DESIGN.md) | 界面设计：配置窗口、悬浮层、托盘、校准、热键 |
| [docs/ROADMAP.md](docs/ROADMAP.md) | 分阶段开发计划与每阶段验收 |
| [docs/GLOSSARY.md](docs/GLOSSARY.md) | 星际韩文术语表（翻译引擎使用） |
| [CLAUDE.md](CLAUDE.md) | 给 Claude Code 的项目说明与开发约定 |

## 当前状态

🚧 **Phase 0–5 代码完成**（开发分支 `phase4-multiregion-ui`，版本 0.4.0）：端到端管线可用——捕获 → 校准 ROI（多区域/列定义/按分辨率）→ PaddleOCR → 术语表/缓存/LLM 翻译（可选两级）→ 悬浮上屏；配置窗口六页、托盘、六个全局热键（可重绑/冲突检测/恢复默认）；异常恢复（窗口丢失自动重连/最小化清屏/分辨率切换提示）、开机自启、启动自动翻译、单文件打包（`tools/publish.ps1`）。Core 单测 99/99 绿。

⏳ 待办：**实机 e2e 验收**（真实 SC:R + 登录/Key + 上屏中文，需用户；详见 ROADMAP Phase 2/4/5 验收项）。

## 重要约束

- ⚠️ SC:R 需以**窗口化 / 无边框窗口**模式运行，否则独占全屏会盖住悬浮窗。
- ⚠️ 必须在 **Windows 端**编译运行（WSL 不支持 WPF / Graphics.Capture）。
- ⚠️ 玩家 ID 是专有名词，**默认不翻译**（可在配置中开启"翻译玩家 ID"或按 F7 切换）；房间名 / 地图信息 / 聊天默认翻译。
