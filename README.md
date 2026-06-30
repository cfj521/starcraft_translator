# SC:R Translator — 星际争霸：重制版实时翻译软件

一个 Windows 桌面软件，实时识别《星际争霸：重制版》(StarCraft: Remastered) 游戏画面中的**韩文**（联机聊天、战网房间名、玩家 ID、地图信息），翻译成**中文**并以悬浮窗覆盖显示在游戏上。

> 主要场景：在韩服/国际服联机时，看懂对手与房间里的韩文聊天、房间名和地图信息。

## 一句话技术方案

`抓取 SC:R 窗口画面 → 区域(ROI)截图 → 变化检测 → 韩文 OCR → 两级翻译(快引擎+LLM) → 透明置顶悬浮窗显示中文`

## 技术栈

- **.NET 10 (LTS) + WPF**（透明置顶、点击穿透悬浮窗）
- 屏幕捕获：`Windows.Graphics.Capture`（抓窗口，非独占全屏）
- OCR：Windows 内置 OCR（韩文）→ 可升级 PaddleOCR / RapidOCR(ONNX)
- 翻译：两级混合 = 快引擎(Papago/百度) 秒出 + Claude LLM(+术语表) 回填
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

📋 设计阶段完成，待 **Phase 0**（工程骨架 + 透明置顶悬浮窗）开始。详见 [ROADMAP](docs/ROADMAP.md)。

## 重要约束

- ⚠️ SC:R 需以**窗口化 / 无边框窗口**模式运行，否则独占全屏会盖住悬浮窗。
- ⚠️ 必须在 **Windows 端**编译运行（WSL 不支持 WPF / Graphics.Capture）。
- ⚠️ 玩家 ID 是专有名词，**默认不翻译**（可在配置中开启"翻译玩家 ID"或按 F7 切换）；房间名 / 地图信息 / 聊天默认翻译。
