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
  ▢ 悬浮层: 显示          (F10)
  ─────────────────────────
  ✎ 进入校准…             (F8)
  ⚙ 配置…                 (Ctrl+F9)
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
> 实现对齐（Phase 2–4）：**无"快引擎/LLM 三模式"与 Papago/Claude**。实际 = 主引擎单选（DashScope / ChatGPT 订阅）**单活跃** + **两级翻译 opt-in 开关**；密钥/登录走 `SecretStore`(DPAPI) 与 OAuth，不进 `settings.json`。

```
引擎（单活跃，托盘可快速切换）
  主引擎          (•) DashScope（阿里云）  ( ) ChatGPT（订阅）
                  [ 设置阿里 Key… ]        [ 登录 ChatGPT… ]
  模型            DashScope [ qwen-flash ]   ChatGPT [ gpt-5 ]
  [ ] 两级翻译（opt-in）  快态(默认 DashScope qwen-flash) 先出临时译文
                         → 主引擎精译后**原地替换**；临时灰斜体 / 终态白

内容范围                                              ┌── 热键可绑
  [✓] 翻译聊天
  [✓] 翻译房间名
  [✓] 翻译地图信息
  [ ] 翻译玩家 ID            (默认关闭)         (F7) ┘
        ⓘ 玩家 ID 多为专有名词，通常不翻译。开启后"房间名列表"
          里的 ID 列也会被翻译。可用热键 F7 随时切换。

质量与省流
  [✓] 启用术语表 (星际黑话)        [ 编辑术语表… ]   ← 命中直出，无网络（始终启用）
  [✓] 启用翻译缓存                 ← 进程内 LRU，容量固定，无 TTL；只存最终态（始终启用）
  [ ] LLM 节流/合并(防刷屏)        ← 后续
  [ ] 保留上下文供 LLM 参考        ← 后续
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

### 3.4 区域 ROI
```
已定义区域 (当前分辨率 1920×1080)         [ + 新建 ] [ 进入校准 ]
┌───────────────┬────────┬──────────┬────────┐
│ 名称           │ 类型    │ 位置     │ 操作    │
├───────────────┼────────┼──────────┼────────┤
│ 对战聊天框      │ Chat    │ 左下     │ 编辑 删 │
│ 房间名列表      │ RoomList│ 中部     │ 编辑 删 │
│ 地图信息        │ MapInfo │ 右侧     │ 编辑 删 │
└───────────────┴────────┴──────────┴────────┘
  RoomList 列定义: [ID列: 不翻] [房间名列: 翻] [地图列: 翻]
                   ← "ID列是否翻译"联动 3.2 的"翻译玩家ID"开关
```

### 3.5 热键 Hotkeys
（详见第 4 节，本页用于查看/重绑）
```
功能                     当前热键        操作
────────────────────────────────────────────
开/关 翻译                F9            [ 修改 ]
显/隐 悬浮层              F10           [ 修改 ]
进入校准                  F8            [ 修改 ]
打开配置                  Ctrl+F9       [ 修改 ]
切换"翻译玩家 ID"         F7            [ 修改 ]
暂停/继续                 F11           [ 修改 ]
切换 DEBUG 面板           F6            [ 修改 ]
复制诊断信息              Ctrl+F6       [ 修改 ]
                                 [ 恢复默认热键 ]
点[修改]后按下新组合键即可录制；冲突会高亮提示。
```

### 3.6 外观 Appearance
```
悬浮层
  字体            [ 微软雅黑 ▼ ]   字号 [ 18 ]
  文字颜色        [■ 白]   描边/底色 [■ 半透明黑]
  不透明度        [──────●──]  80%
  显示模式        ( ) 仅译文   (•) 原文+译文双行
  临时态(快引擎)样式  [ 灰色/半透明 ]  最终态(LLM) [ 正常色 ]
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
| 显/隐 悬浮层 | `F10` | 临时隐藏译文看原画面 |
| 进入校准 | `F8` | 切到校准窗口框选区域 |
| 打开配置 | `Ctrl+F9` | 打开配置窗口 |
| **切换"翻译玩家 ID"** | `F7` | 直接开关 ID 翻译，无需进配置 |
| 暂停/继续 | `F11` | 暂停捕获与翻译（省资源） |
| 切换 DEBUG 面板 | `F6` | 左上角显示/隐藏诊断面板（场景识别/帧/ROI/翻译状态） |
| 复制诊断信息 | `Ctrl+F6` | 把面板同源的诊断快照拷到剪贴板（面板纯展示、永久点击穿透，不抢焦点） |

规则：
- 热键可在 **3.5 热键** 页自定义；录制式绑定，检测冲突。
- 与游戏自身快捷键尽量错开（SC:R 常用 F1–F4 编队，故默认用 F7–F11）。
- 切换类热键触发后，托盘图标/悬浮层给短暂提示（如 "玩家ID翻译: 开"）。

---

## 5. 校准窗口 Calibration

进入校准（F8）后，全屏半透明覆盖，可交互：

```
┌──────────────── 校准模式 (Esc 退出 / Enter 保存) ─────────────┐
│  在游戏画面上拖动鼠标框选区域，松开后命名并选择类型。           │
│                                                               │
│        ┌─────────────┐                                        │
│        │ 房间名列表    │ ← 已框区域，可拖角调整                  │
│        └─────────────┘                                        │
│                          ┌───────────┐                        │
│  正在框选… → 弹出: 名称[ 对战聊天框 ] 类型[ Chat ▼ ] [确定]     │
│                                                               │
│  RoomList 类型额外: 用竖线划分列 → 每列选 [翻译/不翻译]          │
└───────────────────────────────────────────────────────────────┘
```

- 框选 → 弹出小面板填 名称 / 类型；RoomList 额外划列并逐列设"翻译/不翻"。
- 当前分辨率作为配置 key，换分辨率提示重新校准。
- 保存写入 `rois.json`（见 TECH_DESIGN 3.2）。

---

## 6. 配置数据模型（与界面对应，实现对齐版）

> 与代码一致：`src/ScrTranslator.Core/Settings/AppSettings.cs` + `Translation/TranslationSettings.cs` + `Roi/RoiModels.cs`。
> **实际字段**（非早期设想）：无 `EngineMode(FastOnly/Hybrid/LlmOnly)`，改 `Active` 单选 + `TwoLevel` 开关；密钥字段**不在** `AppSettings` 内（走 `SecretStore`/DPAPI）。

```csharp
class AppSettings {
    GeneralSettings General;      // TargetProcess / AutoStart / StartMinimized / AutoTranslateOnLaunch
    TranslationSettings Translation;
    OcrSettings Ocr;              // SceneDetectPollMs=200 / TranslatePollMs=500（两个正交节拍；旧 ChatPollMs/OtherPollMs 与预处理/灵敏度字段均已移除，见 §3.3 注）
    AppearanceSettings Appearance;// FontFamily / FontSize / Opacity / ShowOriginalAndTranslation
    Dictionary<string, HotkeyBinding> Hotkeys;  // 功能名 → (Modifiers, Vk)，默认 F7–F11/Ctrl+F9
    // ROI 配置单独存 rois.json（RoiStore），按分辨率 bucket 分组，RoomList 可含列定义
}

class TranslationSettings {
    TranslationEngine Active;     // DashScope | ChatGpt（单活跃）
    string DashScopeModel;        // 默认 qwen-flash
    string ChatGptModel;          // 默认 gpt-5
    int TimeoutSeconds;           // 整轮(快+质量共用)超时，默认 10
    bool TwoLevel;                // 两级编排 opt-in，默认 false
    TranslationEngine FastEngine; // 快态引擎，默认 DashScope
    string FastModel;             // 快态模型，默认 qwen-flash
    bool TranslateChat   = true;
    bool TranslateRoomName = true;
    bool TranslateMapInfo  = true;
    bool TranslatePlayerId = false;  // ★ 对应"翻译玩家ID"开关 / F7
}
```

> 实现时：`TranslationService` 决定某条文本是否翻译，依据 = ROI 类型对应的开关；
> 若该文本来自 RoomList 的 ID 列，则取 `TranslatePlayerId`。API Key 字段持久化前用 DPAPI 加密（`SecretStore`，与 `settings.json` 分离）。
