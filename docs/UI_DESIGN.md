# 界面设计说明 — SC:R 实时翻译软件

> 配合 [PRD.md](PRD.md)、[TECH_DESIGN.md](TECH_DESIGN.md) 阅读。本文定义各界面的结构、字段与交互。
> 下方为 ASCII 线框（mockup），实现用 WPF；尺寸/配色后续在样式阶段细化。

## 1. 界面总览

软件由四类界面组成：

| 界面 | 形态 | 作用 |
|------|------|------|
| **悬浮层 Overlay** | 透明置顶 / 点击穿透 | 在游戏上显示译文（运行时主界面，无边框、不可交互） |
| **托盘菜单 Tray** | 系统托盘右键菜单 | 快速开关、打开配置、校准、退出 |
| **配置窗口 Settings** | 普通窗口（多标签页） | 所有设置项：翻译/OCR/区域/热键/外观/常规 |
| **校准窗口 Calibration** | 全屏半透明可交互覆盖 | 拖框定义 ROI、命名、选类型/列 |

主程序常驻托盘，不占任务栏主窗口；配置窗口按需打开。

---

## 2. 托盘菜单 Tray

```
  SC:R Translator              ●  (图标: 绿=翻译开 / 灰=关)
  ─────────────────────────
  ▶ 翻译: 开启            (F9)        ← 点击切换 开/关
  ▢ 悬浮层: 显示                        ← 点击切换 显/隐（纯托盘，无热键）
  ─────────────────────────
  ✎ 进入校准…         (Ctrl+F9)
  ⚙ 配置…                     (F10)
  ─────────────────────────
  状态: 已连接 SC:R 窗口 (1920×1080)   ← 灰色，仅显示
  ─────────────────────────
  退出
```

- 双击托盘图标 = 打开配置窗口。
- 菜单项右侧显示当前绑定热键，提示用户。

---

## 3. 配置窗口 Settings（多标签页）

窗口左侧标签 / 顶部标签均可，建议左侧竖直标签。共 6 页：**常规 / 翻译 / OCR / 区域 / 热键 / 外观**，外加底部 **关于**。

```
┌─ SC:R Translator — 配置 ───────────────────────────────────┐
│ ┌─────────┐                                                 │
│ │ ▸ 常规   │   [ 当前页内容区 ]                              │
│ │   翻译   │                                                 │
│ │   OCR    │                                                 │
│ │   区域   │                                                 │
│ │   热键   │                                                 │
│ │   外观   │                                                 │
│ └─────────┘                                                 │
│                                       [ 应用 ]  [ 确定 ] [取消]│
└────────────────────────────────────────────────────────────┘
```

### 3.1 常规 General
```
游戏窗口
  目标进程        [ StarCraft.exe        ▼ ]   [ 重新检测 ]
  当前状态        ● 已连接 (1920×1080 无边框)
  ⚠ 提示: 请以"窗口化/无边框"模式运行游戏，独占全屏无法悬浮。

启动
  [✓] 开机自启
  [✓] 启动后最小化到托盘
  [ ] 启动游戏时自动开启翻译

语言
  界面语言        [ 简体中文 ▼ ]
  源语言 → 目标   [ 韩文 ▼ ] → [ 简体中文 ▼ ]
```

### 3.2 翻译 Translation  ★（含"翻译玩家 ID"）
> 实现对齐（Phase 2–6）：**无"快引擎/LLM 三模式"与 Papago/Claude**。实际 = **provider 池列表**（4 provider：阿里云免费机器翻译 ＋ OpenAI 订阅 / OpenAI API / 阿里云 DashScope LLM），**可拖拽排序、每行启用/禁用**，从上到下取第一个「启用且可用」，不可用或额度耗尽跳下一个（先免费后 LLM）。**单级**。密钥/登录走 `SecretStore`(DPAPI) 与 OAuth，不进 `settings.json`。

```
Provider 池（拖动调整顺序；勾选=启用；配置…按行填各家密钥/登录）
  [✓] 阿里云机器翻译   [免费]   未配置        [ 配置… ]
  [✓] OpenAI LLM(ChatGPT 订阅)  [LLM]  未登录    [ 配置… ]
  [✓] 阿里云 LLM(DashScope)  [LLM]  未配置      [ 配置… ]
  [✓] OpenAI LLM(API)  [LLM]  未配置      [ 配置… ]
        [ 恢复默认顺序 ]
  模型            DashScope [ qwen3.8-flash ]   ChatGPT [ gpt-6-luna ]   OpenAI API [ gpt-6-luna ]

内容范围
  [✓] 翻译聊天
  [✓] 翻译房间名
  [✓] 翻译地图信息
  [ ] 翻译玩家 ID            (默认关闭)   ← 纯配置项，无热键
        ⓘ 玩家 ID 多为专有名词，通常不翻译。开启后"房间名列表"
          里的 ID 列也会被翻译。

质量与省流
  [✓] 启用术语表 (星际黑话)        [ 编辑术语表… ]   ← 命中直出，无网络（始终启用）
  [✓] 启用翻译缓存                 ← 进程内 LRU，容量固定，无 TTL；只存最终态（始终启用）
  ⓘ LLM 节流（429 限流冷却）     已实现、静默——被限流自动冷却并让位池内下一个 provider（详见 日志 页）
```

> **"翻译玩家 ID"** 落地为配置项 `Translation.TranslatePlayerId`（默认 `false`）。
> RoomList 型 ROI 的 ID 列是否翻译，取此开关；房间名/地图列始终按各自勾选项翻译。

### 3.3 OCR
> 实现对齐：OCR **仅 PaddleOCR**（RapidOcrNet + 韩文 mobile ONNX 模型随包分发），已取代 Windows 内置 OCR 方案，**无需系统语言包**，识别语言固定韩文。

```
引擎            PaddleOCR（当前唯一实现；内置 OCR 方案已废弃）
识别语言        韩文 (ko)（固定，无需语言包检查）
轮询节拍
  场景检测      每 [ 200 ] ms 识别一次（只比几块静态区，很便宜）
  翻译          每 [ 500 ] ms 跑一轮 OCR+翻译
```
> 注：两个节拍**正交**、改后即时生效（各自钳制 100–5000ms）。捕获侧帧节流自动取两者较快值（`PollInterval.CaptureFloor`）——否则识别配 200ms 也会被翻译的 500ms 上游拖平。原"聊天区/其它区域"两值设计已废弃：其他界面根本不翻译，给它配一个翻译节拍属于假旋钮。原"图像预处理/变化检测灵敏度"控件为占位假设置——RapidOcrNet 不暴露对应旋钮、变化检测为精确文本比对；实测原生分辨率裁条放大 2x 无收益（avgConf .987 直读 vs .982 放大），已整体移除（UI_DESIGN 与 settings.json 字段同步删除，旧文件多余字段读取时自动忽略）。

### 3.4 区域（仅预设，DEBUG 模式）

数据有唯一来源：**出厂布局预设**（`layout-presets.json`）。"区域" tab **仅 DEBUG 模式可见**，编辑**直接读写出厂文件**（不再有 APPDATA 覆盖层）。识别区域共四类：聊天 / 房间名列表（寻找房间）/ 房间名称 / 地图名称（房间内）。

```
当前分辨率 3840×2160                       [ 进入校准 (Ctrl+F9) ]  ← 仅 DEBUG 模式

已定义区域（预设 parts）：                   [ 编辑选中 ] [ 删除选中 ]
┌──────────┬──────────────────┬───────────┐
│ 场景      │ 名称              │ 是否设置   │
├──────────┼──────────────────┼───────────┤
│ 寻找房间  │ chat             │ ✓        │
│ 寻找房间  │ roomNameList     │ ✓        │
│ 房间内    │ chat             │ ✓        │
│ 房间内    │ gameName         │ ✓        │
│ 房间内    │ mapName          │ ✓        │
└──────────┴──────────────────┴───────────┘
   ↑ 双击任意行 = 编辑；预设 part 可删（置 null 持久化）
```

> 三条口径：① 本 tab **仅 DEBUG 模式可见**（识别区域是预设固定的，普通用户不必动）；
> ② 编辑**直写出厂 `layout-presets.json`**（没有覆盖层，"删掉恢复出厂"无意义）；
> ③ 预设 part 可删（置 null 持久化）——出厂文件就是唯一数据，"删"是合法操作。

### 3.5 热键 Hotkeys
（详见第 4 节，本页用于查看/重绑）
```
功能                     当前热键        操作
────────────────────────────────────────────
开/关 翻译                F9            [ 修改 ]
进入校准                  Ctrl+F9       [ 修改 ]
打开配置                  F10           [ 修改 ]
切换 DEBUG 面板           F8            [ 修改 ]
复制诊断信息              Ctrl+F8       [ 修改 ]
                          [ 恢复默认热键 ]
点[修改]后按下新组合键即可录制；冲突会高亮提示。可绑范围 F6–F12（可加 Ctrl/Alt/Shift）。
```

### 3.6 外观 Appearance
```
悬浮层
  字体            [ 微软雅黑 ▼ ]   字号 [ 18 ]
  文字颜色        [■ 白]   描边/底色 [■ 半透明黑]
  不透明度        [──────●──]  80%      ← 整条（文字+底衬）的统一透明度
  文字底衬        [──────●──]  80%      ← 每条译文一块圆角黑底的 alpha，0=不画；纯白字压不住浅色游戏画面（已实现）
  显示模式        ( ) 仅译文   (•) 原文+译文双行

聊天悬浮
  最多保留        [ 6 ] 条历史
  单条停留        [ 8 ] 秒后淡出
  位置           [ 贴聊天框上方 ▼ ]
```

### 3.7 关于 About
版本号、开源许可、第三方组件、使用须知（窗口化运行、纯读屏不改游戏、封号风险说明）。

---

## 4. 热键设计 Hotkeys

全局热键（`RegisterHotKey`），游戏内也生效。默认绑定：

| 功能 | 默认热键 | 说明 |
|------|----------|------|
| 开/关 翻译 | `F9` | 总开关，等于托盘"翻译" |
| 进入校准 | `Ctrl+F9` | 切到校准窗口框选区域 |
| 打开配置 | `F10` | 打开配置窗口 |
| 切换 DEBUG 面板 | `F8` | 左上角显示/隐藏诊断面板（场景识别/帧/ROI/翻译状态） |
| 复制诊断信息 | `Ctrl+F8` | 把面板同源的诊断快照拷到剪贴板（面板纯展示、永久点击穿透，不抢焦点） |

规则：
- 热键可在 **3.5 热键** 页自定义；录制式绑定，检测冲突。可绑范围 **F6–F12**（可加 Ctrl/Alt/Shift）。
- 与游戏自身快捷键尽量错开（SC:R 常用 F1–F5 编队，故默认从 F8 起）。
- 切换类热键触发后，托盘图标给短暂提示（如 "DEBUG 面板: 开"）。
- 悬浮层显隐 / 翻译玩家 ID / 暂停 已改为纯配置项，不再占热键。

---

## 5. 校准窗口 Calibration

### 5.0 进入前置（F10）

校准窗是**半透明覆盖层**，且要靠抓屏做场景检测 —— 看不见游戏画面时框出来的东西全是凭空的。
所以 `App.xaml.cs OpenCalibration()` 先挡一道，任一不满足就日志说明原因（不弹气泡，见 配置→日志 页）、**不进校准**：

| 情况 | 提示 |
|------|------|
| 找不到目标进程窗口 | "未找到游戏窗口（进程 StarCraft）。请先启动 SC:R（窗口化/无边框）。" |
| 游戏已最小化 | "游戏处于最小化状态，请先还原窗口再按 F10。" |
| 游戏不是前台窗口 | "游戏不在前台，校准会看不清要对齐的画面。请点一下游戏窗口再按 F10。" |

### 5.1 场景自己检测，不依赖 F9

打开时校准窗自己抓一帧，交给
`SceneRecognizer` 做图像指纹识别 —— **和翻译开关无关**：没按 F9 也能定准当前场景，
于是预设框一定落到对的场景（而不是全塞进"寻找房间"）。
检测失败才退回打开方传进来的场景值，并记日志。

顶部提示常驻显示 `场景【游戏内】 · 分辨率 3840x2160 · 拖动预设框调整 (Esc 退出 / Enter 保存)`。

### 5.2 预设框（可交互）

当前场景/宽高比档的布局预设被建成**可交互编辑框**（彩色边框 + 标签：
`(预设)聊天区域`、`(预设)房间名列表区域`、`(预设)房间名称区域`、`(预设)地图名称区域`），
用户直接在游戏画面上**选中 / 整框平移 / 拖角改大小**，Enter **直写**出厂 `layout-presets.json`。
不再有"只读参考框"——也不能再画新区域、划竖线分列（可翻译区域出厂已固定）。

```
┌──────────────── 校准模式 (Esc 退出 / Enter 保存) ─────────────────────────┐
│ 场景【寻找房间】 · 分辨率 3840x2160 · 拖动预设框调整                        │
│ ╔═══════════════════════════════════╗                                     │
│ ║ 房间名列表区域(预设) ← 彩色边框，可移动/缩放 ║  ┌───────────────┐          │
│ ╚═══════════════════════════════════╝  │ 聊天区域(预设) ║          │
│                                          └───────────────┘          │
└──────────────────────────────────────────────────────────────────────────┘
```

### 5.3 交互细则

- **只有预设框**：校准窗只把当前场景的预设区域建成可交互框，不再允许画新区域（可翻译区域出厂已全覆盖）。
- **框操作**：点框选中、拖框体整框平移、拖角柄改大小（不许缩成线）。
- **场景检测**：校准窗自己抓帧做场景检测，不依赖 F9；检测出的场景决定框写进哪个场景的预设。
- **保存**：Enter **直写**出厂 `layout-presets.json`（DIP→物理像素→相对坐标换算抽成 `RelRect.FromCanvasDip` 便于测），并回调 `probe.ReloadLayoutPresets()` 让运行中的探测立即生效；**Esc 取消**不保存。

---

## 6. 配置数据模型（与界面对应，实现对齐版）

> 与代码一致：`src/ScrTranslator.Core/Settings/AppSettings.cs` + `Translation/TranslationSettings.cs` + `RelRect.cs`。
> **实际字段**（非早期设想）：无 `EngineMode(FastOnly/Hybrid/LlmOnly)`，也无两级开关——**provider 池**（`Providers` 有序列表，可拖拽排序/启用禁用）、单级；原单活跃 `Active`（`TranslationEngine`）已改 provider 池；密钥字段**不在** `AppSettings` 内（走 `SecretStore`/DPAPI）。

```csharp
class AppSettings {
    GeneralSettings General;      // TargetProcess / AutoStart / StartMinimized / AutoTranslateOnLaunch
    TranslationSettings Translation;
    OcrSettings Ocr;              // SceneDetectPollMs=200 / TranslatePollMs=500（两个正交节拍；旧 ChatPollMs/OtherPollMs 与预处理/灵敏度字段均已移除，见 §3.3 注）
    AppearanceSettings Appearance;// FontFamily / FontSize / Opacity / BackplateOpacity(文字底衬 0.8) / ShowOriginalAndTranslation
    Dictionary<string, HotkeyBinding> Hotkeys;  // 功能名 → (Modifiers, Vk)，默认 F9/Ctrl+F9/F10/F8/Ctrl+F8
    // 识别区域现为唯一来源：出厂 layout-presets.json（直读直写，DEBUG 模式；RelRect 相对 0~1）
    // 场景识别参考保留覆盖层（scene-refs.json + scene-overrides.json），见 TECH_DESIGN 3.9
}

class TranslationSettings {
    IReadOnlyList<TranslationProviderEntry> Providers;  // 有序 provider 池（默认 阿里云→OpenAI LLM(订阅)→阿里云 LLM(DashScope)→OpenAI LLM(API)；原 TranslationEngine Active 已移除）
    string DashScopeModel;        // 默认 qwen3.8-flash
    string ChatGptModel;          // 默认 gpt-6-luna
    string OpenAiApiModel;        // 默认 gpt-6-luna
    int TimeoutSeconds;           // 整轮(快+质量共用)超时，默认 10

    bool TranslateChat   = true;
    bool TranslateRoomName = true;
    bool TranslateMapInfo  = true;
    bool TranslatePlayerId = false;  // ★ 对应"翻译玩家ID"开关（纯配置项，无热键）
}
```

> 实现：`TranslateChat`/`TranslateRoomName`/`TranslateMapInfo`/`TranslatePlayerId` 四个开关**不再被管线消费**（列式翻译已随用户 ROI 层移除，识别区域是预设、整块必翻）；
> 另有一道**内容闸门** `KoreanText.ContainsHangul`：整行不含谚文（地图名、`Top vs Bottom`、表头…）
> 直接不进翻译管线 —— 提示词要求"纯英文原样保留"，送过去只会拿回同样的英文，白花请求还显示成噪声。
> API Key / OAuth token 持久化前用 DPAPI 加密（`SecretStore`，与 `settings.json` 分离）。
