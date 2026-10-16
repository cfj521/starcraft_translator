# SC:R Translator — 星际争霸：重制版实时翻译软件

一个 Windows 桌面软件，实时识别《星际争霸：重制版》(StarCraft: Remastered) 游戏画面中的**韩文**（联机聊天、战网房间名、玩家 ID、地图信息），翻译成**中文**并以悬浮窗覆盖显示在游戏上。

> 主要场景：在韩服/国际服联机时，看懂对手与房间里的韩文聊天、房间名和地图信息。

## 一句话技术方案

`抓取 SC:R 窗口画面 → 场景识别(图像指纹，判 寻找房间/房间内/游戏内/其他界面) → 区域(校准 ROI + 内置布局预设)裁剪 → 变化检测 + 内容闸门(含谚文才翻) → 韩文 OCR(PaddleOCR) → 翻译(术语表/缓存命中直出，否则走 provider 池回退链：本地引擎→云端机器翻译/LLM，一批一请求) → 透明置顶悬浮窗显示中文(带半透明黑底)`

## 技术栈

- **.NET 10 (LTS) + WPF**（透明置顶、点击穿透悬浮窗）
- 屏幕捕获：`Windows.Graphics.Capture`（抓窗口，非独占全屏）
- OCR：**PaddleOCR**（RapidOcrNet + 韩文 mobile ONNX 模型，随包分发，**无需系统韩文语言包**）
- 翻译：**provider 池**——分两组，**内部引擎**（本地 Hy-MT2，腾讯翻译模型、纯 C# 进程内 CPU 推理、离线/不限流）与**外部引擎**（阿里云**免费机器翻译** ＋ 自定义模型 / OpenAI 订阅（ChatGPT/Codex OAuth）/ OpenAI API（sk- 密钥）/ 阿里云 DashScope **LLM**）。内部/外部**单选**（二选一），组内可拖拽排序，选中组内从上到下取第一个「启用且可用」的 provider，不可用或额度耗尽自动跳到组内下一个。术语表/LRU 缓存命中直出（零请求），未命中一批一次请求。本地权重路径进 settings.json，云端密钥走 DPAPI 本地加密。
- 平台：**仅 Windows 10 1803+ / Windows 11**（WPF + Graphics.Capture 必须在 Windows 端编译运行）

## 文档导航

| 文档 | 内容 |
|------|------|
| [docs/PRD.md](docs/PRD.md) | 产品需求：目标、场景、功能/非功能需求、验收标准 |
| [docs/TECH_DESIGN.md](docs/TECH_DESIGN.md) | 技术设计：架构、模块、数据流、关键难点方案 |
| [docs/UI_DESIGN.md](docs/UI_DESIGN.md) | 界面设计：配置窗口、悬浮层、托盘、校准、热键 |
| [docs/ROADMAP.md](docs/ROADMAP.md) | 分阶段开发计划与每阶段验收 |
| [docs/E2E_CHECKLIST.md](docs/E2E_CHECKLIST.md) | **实机验收清单**（Phase 2/4/5 合并，接真实 SC:R 时逐项勾） |
| [docs/GLOSSARY.md](docs/GLOSSARY.md) | 星际韩文术语表（翻译引擎使用） |
| [CLAUDE.md](CLAUDE.md) | 给 Claude Code 的项目说明与开发约定 |

## 当前状态

🚧 **Phase 0–6 代码完成**（main，版本 0.2.0）：端到端管线可用——捕获 → **场景识别（图像指纹，非 OCR）** → 校准 ROI（多区域/列定义/按分辨率）→ PaddleOCR → 术语表/缓存/provider 池翻译（免费机器翻译→LLM 回退，单级，一批一请求）→ 悬浮上屏；配置窗口六页、托盘、五个全局热键（可重绑/冲突检测/恢复默认）；异常恢复（窗口丢失自动重连/最小化清屏/分辨率切换提示）、开机自启、启动自动翻译、单文件打包（`tools/publish.ps1`）。Core 单测 256/256 绿。

场景识别：三目标场景统一命名 `browsing`(寻找房间) / `room`(房间内) / `ingame`(游戏内，按种族分套参考) + 第四态 `other`(其他界面不翻译)。参考数据 `scene-refs.json` 由 `tools/SceneRefGen` 从截图生成，**不要手改**（带 sha256 覆盖"几何+数据"，改了直接整份拒用）。实测同场景 100% 命中、跨场景最高 25.2%、阈值 90%。

区域来源：内置 `layout-presets.json` 是识别区域的**唯一来源**——每个场景按宽高比档（16:9/16:10/4:3）固定了**聊天框**，寻找房间另有**房间名列表区域**，房间内另有**房间名称/地图名称**。这些面板位置固定，出厂内置、不必校准；配置页"区域" tab 与校准窗都**仅 DEBUG 模式可用**，且**直接读写出厂文件**（不再有用户自定义框，也没有 APPDATA 覆盖层）。`tools/LayoutCheck` 可把预设画到截图上核验落位。

⏳ 待办：**实机 e2e 验收**（真实 SC:R + 登录/Key + 上屏中文，需用户；详见 ROADMAP Phase 2/4/5 验收项）。

## 许可

本软件以 **MIT License** 开源（见根目录 [LICENSE](LICENSE)）。可自由使用、复制、修改、合并、发布、再许可、商用，唯一条件是保留本许可与版权声明。
