# Phase 4 设计 — 多区域 + 校准 UI + 配置窗口 + 热键

> 状态：设计定稿（用户睡眠中，已授权自主推进；本 Phase **仅出 spec + plan，不实现**——UI 需实机可视验收，留用户在场时做）。以隔离 subagent 审为质量门。
> 日期：2026-07-02。分支基线：`phase4-multiregion-ui`（从 `main`=`a13a297`，即 Phase 1+2+3 已合入）。
> UX 基准：`docs/UI_DESIGN.md`（**含过时技术项，本 spec 已对齐 Phase 2/3 现状**，见 §1）。关联：`docs/ROADMAP.md` Phase 4、`docs/UI_DESIGN.md` §6 数据模型。

## 0. 目标与验收

**目标**：从"单 ROI 硬编码 + 两个全局热键"升级为**可配置的多区域翻译器**——用户能用校准窗拖框定义多个 ROI（聊天/房间列表/地图信息）、按类型/列决定翻不翻（含玩家 ID 开关）、在多标签配置窗调所有设置、用可自定义的全局热键操作；设置与 ROI 按分辨率持久化。

**验收**（Phase 4 完成判据；UI 项为**用户实机可视验收**，非本次自动化）：
1. **数据基座(M1) Core 单测全绿**：ROI v2（列定义）、`AppSettings` JSON 往返、内容范围翻译门控（ROI 类型/列 → 翻不翻；`TranslatePlayerId` 决定 ID 列）、热键绑定持久化 + 冲突检测。
2. **配置窗(M2)**：托盘双击 / `Ctrl+F9` 打开六页多标签窗（常规/翻译/OCR/区域/热键/外观 + 关于），改值→应用/确定生效并落盘、取消丢弃；密钥字段仍走 SecretStore（DPAPI），不写明文。
3. **校准窗(M3)**：`F8` 进入全屏半透明可交互覆盖，拖框→命名/选类型；RoomList 额外划列并逐列设翻/不翻；按当前分辨率存 `rois.json`；`Esc` 取消 / `Enter` 保存。
4. **热键(M4)**：默认 F9(翻译开关)/F10(悬浮显隐)/F8(校准)/Ctrl+F9(配置)/F7(切换玩家 ID)/F11(暂停)，可在热键页录制重绑、冲突高亮、持久化、改后即时重注册；切换类触发给托盘/悬浮短提示。
5. **换分辨率**：无匹配分辨率 bucket 时提示"请重新校准"（不崩、不乱摆）。
6. **回归**：Phase 2/3 行为不破（默认单 ROI 房间列表仍工作；两级开关、引擎切换、glossary/cache 照常）。

## 1. 与 UI_DESIGN.md 的对齐（务必先读——UX 参 UI_DESIGN，技术以此处为准）

`UI_DESIGN.md` 写于早期，以下技术项**已过时**，本 Phase 按 Phase 2/3 现状对齐：

| UI_DESIGN 旧写法 | Phase 4 采用（现状） |
|---|---|
| 引擎模式 FastOnly/Hybrid/LlmOnly | **主引擎单选**（DashScope / ChatGPT 订阅）+ **两级翻译开关**(`TwoLevel`,默认关)。"翻译"页据此渲染，不用三模式单选。 |
| 快引擎 Papago / LLM Claude | **DashScope(API Key)** + **ChatGPT 订阅(OAuth 登录)**。密钥/登录走 `SecretStore`(DPAPI) + `OpenAiOAuth`(已实现)。 |
| OCR: Windows 内置 / PaddleOCR 二选 | **仅 PaddleOCR**（已实现，随包模型）。OCR 页去掉"Windows OCR/语言包"项，保留轮询频率/预处理/变化检测灵敏度。 |
| 缓存"时长 X 分钟" | 现 `TranslationCache` 为 LRU 无 TTL。**YAGNI**：Phase 4 不加 TTL，缓存项显示为"启用/容量"或标注"进程内 LRU"。 |
| 保留上下文 N 条 / LLM 节流 ms | 现未实现 recent-context / 独立节流器（Phase 3 YAGNI）。**Phase 4 不新增**，这两项在翻译页**置灰标注"后续"**或不显示。 |
| 外观"临时态/最终态样式" | 已实现（Phase 3：临时=灰斜体 0.65、最终=白）。外观页可**只读展示或最小暴露**（改色留后续）。 |

**其余 UI_DESIGN 结构（托盘布局、六页配置、校准流程、热键表、数据模型 §6）作为本 Phase 的 UX 蓝图直接采用。** 凡本 spec 未覆盖的像素级样式，实现期按 UI_DESIGN 线框近似即可。

## 2. 范围分解（4 个子模块，M1 为基座）

Phase 4 体量大，拆 4 个子模块，**M1 数据基座**是唯一重 Core/可单测部分，且是其余三者的地基；M2/M3/M4 为 WPF，均依赖 M1。**实现顺序建议：M1 →（M3、M4）→ M2**。注：M2/M3/M4 并非完全独立——**M2 的"区域"页有"进入校准"按钮会打开 M3 的 `CalibrationWindow`**（M2→M3 依赖）。故若先做 M2，该按钮先 stub/置灰，待 M3 落地再接；或按上面顺序先出 M3。plan 需显式处理此依赖，不可假设 M2 能脱离 M3 完整实现。

**M1 内部可再分两批**（供 plan 排期，二者较解耦）：**M1a** = ROI v2（列定义）+ `rois.json` v2 解析/`Save` + 内容范围门控（`TranslationScope`/`HitColumn`）；**M1b** = `AppSettings`/`SettingsStore`/`HotkeyDefaults`/`HotkeyConflict` + 各调用点适配（`Current.Translation.*`）+ 启动加载接线。

- **M1 数据基座（Core 为主，TDD）**：ROI 模型 v2（RoomList 列定义 + 逐列翻译标志）+ `rois.json` schema v2 + 旧格式兼容；`AppSettings` 模型 + `settings.json` 持久化（非密钥）+ 热键绑定持久化；**内容范围翻译门控**（按 ROI 类型/列决定哪些行翻、ID 列取 `TranslatePlayerId`）；`Phase1Probe` 接入门控（多 ROI 遍历）。
- **M2 配置窗口（WPF）**：六页多标签 + 关于；绑定 `AppSettings`；应用/确定/取消；托盘双击 + `Ctrl+F9` 打开。密钥/登录复用现有入口。
- **M3 校准窗口（WPF）**：全屏半透明可交互；拖框/命名/选类型/RoomList 划列；按分辨率写 `rois.json`；`F8`/`Esc`/`Enter`。
- **M4 热键系统（App）**：`HotkeyService` 扩为按绑定表注册全部功能键，rebindable + 冲突检测 + 持久化 + 改后重注册；托盘/悬浮短提示。

## 3. M1 数据基座（详设，可单测）

### 3.1 ROI 模型 v2（`ScrTranslator.Core.Roi`）

现状：`Roi(string Name, string Type, RelRect Rect)`，`RoiConfig{Scenes: dict<scene, list<SceneBucket>>}`，`SceneBucket(Resolution, Rois)`。

新增（**加字段，保持向后可读**）：
```csharp
public enum RoiType { Chat, RoomList, MapInfo }          // 字符串化存盘("Chat"/"RoomList"/"MapInfo")
public enum ColumnKind { RoomName, MapName, PlayerId, Other }

// RoomList 型 ROI 的列定义：把 ROI 宽度按相对边界切成若干竖列，每列一种语义 + 是否翻译。
public record RoiColumn(double RelStart, double RelEnd, ColumnKind Kind);  // 0..1 相对 ROI 自身宽度

public record Roi(string Name, string Type, RelRect Rect,
    IReadOnlyList<RoiColumn>? Columns = null);           // 仅 RoomList 用；其它类型为 null
```
- `Type` 仍存字符串（兼容旧 `rois.json`）；解析时映射到 `RoiType`，未知串按 `Chat` 兜底并记日志。
- **`kind` 同理兜底**：`columns[].kind` 字符串映射到 `ColumnKind`，**未知串按 `Other` 兜底并记日志**（`Other` 不翻，最安全——不会误把未知列当内容/ID 列翻）。
- `Columns` 为可选：旧 `rois.json`（无 columns 字段）读出来 `Columns=null` → 视为"整块翻译，无列语义"（等同 Phase 2 现状）。
- **列翻译标志不单列布尔**：`ColumnKind` 决定——`RoomName`/`MapName` 属"内容列"（按对应内容范围开关翻），`PlayerId` 属 ID 列（按 `TranslatePlayerId`），`Other` 不翻。这样"ID 列是否翻译"= `TranslatePlayerId`，与 UI_DESIGN §3.4 联动一致，无需在 ROI 里再存布尔。

### 3.2 rois.json schema v2 + 兼容

现 `RoiStore.Load` 读 `scenes[].buckets[].rois[]{name,type,rect[4]}`。v2 在 roi 对象**可选**加 `columns:[{start,end,kind}]`。`Load` 改为：roi 有 `columns` 则解析为 `RoiColumn` 列表，无则 `Columns=null`。**旧文件零改动即可读**（新字段缺省）。新增 `RoiStore.Save(RoiConfig, path)`（校准窗写盘用）——序列化含 columns。

### 3.3 内容范围翻译门控（核心新逻辑，可单测）

新增纯函数（Core，`ScrTranslator.Core.Roi` 或 `.Translation`）：给定一条 OCR 文本所属的 ROI（类型 + 该文本命中的列）与当前内容范围设置，判定**是否翻译**：
```csharp
public static class TranslationScope
{
    // 返回 true = 该行应翻译并叠加译文；false = 不翻译且**不叠加覆盖项**(游戏内原文本就可见,
    // 悬浮层对该行不渲染任何东西——玩家 ID 等即以游戏原样呈现,不被韩文覆盖遮挡)。
    public static bool ShouldTranslate(RoiType type, ColumnKind? column, ContentScopeSettings s);
}
public record ContentScopeSettings(bool Chat, bool RoomName, bool MapInfo, bool PlayerId);
```
规则：
- `Chat` 型 ROI → 取 `s.Chat`。
- `MapInfo` 型 → 取 `s.MapInfo`。
- `RoomList` 型：按命中列的 `ColumnKind`——`RoomName`→`s.RoomName`；`MapName`→`s.MapInfo`；`PlayerId`→`s.PlayerId`；`Other`/`null`→false（不翻）。若 RoomList 无列定义(`Columns=null`)→整块按 `s.RoomName`（兼容 Phase 2 房间名 ROI）。

**列命中判定**（纯函数，可单测）：给定一条 OCR line 的帧内水平中心 x（相对 ROI 宽度 0..1），落在哪个 `RoiColumn` 的 `[RelStart,RelEnd)`：
```csharp
public static ColumnKind? HitColumn(IReadOnlyList<RoiColumn>? columns, double relX);
```
无列→null；未命中任何列→null。

### 3.4 AppSettings 模型 + settings.json 持久化（非密钥）

现 `TranslationSettings`(record) 已有 Active/DashScopeModel/ChatGptModel/TimeoutSeconds/TwoLevel/FastEngine/FastModel。Phase 4 **扩展**它加内容范围 + OCR + 外观 + 常规 + 热键，聚合为 `AppSettings`：
```csharp
public sealed record AppSettings
{
    public GeneralSettings General { get; init; } = new();
    public TranslationSettings Translation { get; init; } = TranslationSettings.Default; // 复用+扩内容范围
    public OcrSettings Ocr { get; init; } = new();
    public AppearanceSettings Appearance { get; init; } = new();
    public IReadOnlyDictionary<string, HotkeyBinding> Hotkeys { get; init; } = HotkeyDefaults.Map;
}
```
- `TranslationSettings` 增：`bool TranslateChat=true`、`TranslateRoomName=true`、`TranslateMapInfo=true`、`TranslatePlayerId=false`。（保留 Phase 2/3 现有字段与默认不变。）
- `GeneralSettings`：`string TargetProcess="StarCraft"`、`bool AutoStart=false`、`bool StartMinimized=true`、`bool AutoTranslateOnLaunch=false`。
- `OcrSettings`：`int ChatPollMs=300`、`int OtherPollMs=800`、`bool Upscale=true`、`bool Grayscale=true`、`bool Binarize=false`、`int ChangeSensitivity=5`（0..10）。
- `AppearanceSettings`：`string FontFamily="Microsoft YaHei"`、`int FontSize=18`、`double Opacity=0.8`、`bool ShowOriginalAndTranslation=false`。
- `HotkeyBinding(uint Modifiers, uint Vk)`；`HotkeyDefaults.Map` = 六功能默认（见 §6）。**VK/MOD 常量单一来源**：现 `VK_F7..F11`/`MOD_*` 在 App 的 `HotkeyService`；随 `HotkeyBinding`/`HotkeyDefaults` 一起**移到 Core**（纯数值常量，App 侧引用 Core），避免两份 Win32 魔数各自漂移。
- **持久化**：`SettingsStore`（Core 无 UI；用 `System.Text.Json`）读写 `%AppData%/ScrTranslator/settings.json`；**只存非密钥**（密钥仍 SecretStore/DPAPI）。文件缺失/损坏 → 返回 `AppSettings` 默认（不崩）。往返测试保证 round-trip 稳定。
- `SettingsHolder` 升级为持有 `AppSettings`（`Current`），保留其"运行时可变"语义；托盘/配置窗改 `Current` 后调 `SettingsStore.Save` + 触发 `OnTranslationStateChanged`（复位变化检测，已实现）。
- **启动加载（关键，防 Save-without-Load）**：`App.xaml.cs` 组合根在构造/注册 `SettingsHolder` 时**必须**调 `SettingsStore.Load()` 作为初始 `Current`（缺文件即默认）。否则会出现"写了 settings.json 却从不读回、每次重启静默复位"——M1 的 `SettingsStore` 往返单测覆盖不到这条 App 接线，须在 §9 交付物显式落地。

> **兼容**：`SettingsHolder.Current` 现返回 `TranslationSettings`；升级为 `AppSettings` 会改动 Phase 2/3 已接线处（`TrayService`/`Phase1Probe`/`App.xaml.cs` 用 `Current.Xxx`）。M1 一并改为 `Current.Translation.Xxx`，保持行为等价（**回归验收项 6**）。

### 3.5 Phase1Probe 接入多 ROI + 门控

现 `OnFrame` 遍历单 scene 的 bucket.Rois，对每 ROI 整块 OCR→全部行送翻译。M1 改为：对每 ROI，OCR 得行后，按 §3.3 判定每行是否翻译——
- 需翻译的行 → 收集送 `TieredTranslator`（原逻辑）。
- 不翻译的行 → **不叠加覆盖项**（不送 provider、悬浮层不为其渲染；游戏内原文保持可见）。RoomList 列语义据 `HitColumn`（用 line 帧内中心 x 相对 ROI 宽度）。
- `ContentScopeSettings` 由 `Current.Translation` 映射。ROI 类型由 `Roi.Type` 映射。
- 保持单飞/节流/变化检测/超时/AllResolved 提交语义不变。

> 门控是纯 Core 判定（`TranslationScope`/`HitColumn` 可单测）；`Phase1Probe` 只做编排接线（不可单测部分，随 M1 build 验证 + 用户 e2e）。

## 4. M2 配置窗口（WPF，实机验收）

- `SettingsWindow`（普通窗，左侧竖直标签，六页 + 关于），`DataContext` 绑 `AppSettings` 的可编辑副本；`应用`=写 Holder+Save；`确定`=应用并关；`取消`=丢弃副本关。
- 六页字段严格按 UI_DESIGN §3.1–3.7，但按 §1 对齐后的技术项渲染：
  - **常规**：目标进程 + 重新检测（`WindowFinder`）、当前连接状态、窗口化提示、开机自启/启动最小化/启动即翻。
  - **翻译**：主引擎单选(DashScope/ChatGPT 订阅) + **两级翻译开关**；DashScope Key（`设置阿里云 Key…`复用 `DashScopeKeyWindow`）；ChatGPT `登录…`（复用 `OpenAiOAuth`，显示邮箱/plan）；模型 id 文本框（DashScope/ChatGpt/Fast）；**内容范围** 4 勾选（聊天/房间名/地图/**玩家 ID(默认关,注 F7)**）；术语表启用 + `编辑术语表…`（打开 `GLOSSARY.md`，最小可为"用默认程序打开文件"）；缓存启用（标注进程内 LRU）；节流/上下文项置灰标"后续"。
  - **OCR**：轮询频率(聊天/其它 ms)、预处理(放大/灰度/二值化)、变化检测灵敏度滑杆。（去掉 Windows OCR/语言包项。）
  - **区域**：列出当前分辨率 ROI（名称/类型/位置/编辑删）、`+新建`/`进入校准`、RoomList 行展开显示列定义（ID 列翻不翻联动"翻译玩家 ID"）。
  - **热键**：六功能当前绑定 + `修改`(录制) + 冲突高亮 + `恢复默认`。（录制/冲突逻辑在 M4。）
  - **外观**：字体/字号/不透明度/显示模式（仅译文 / 原文+译文双行）；临时/最终态样式只读展示（改色留后续）。
  - **关于**：版本、许可、第三方、使用须知（窗口化、纯读屏、封号风险）。
- **打开入口**：托盘双击 + 托盘"配置…" + `Ctrl+F9`。窗口单例（已开则激活）。

## 5. M3 校准窗口（WPF，实机验收）

- `CalibrationWindow`：全屏、`AllowsTransparency`、半透明深色背景、**可交互**（非点穿）、Topmost；`F8` 打开、`Esc` 取消、`Enter` 保存。
- 交互：鼠标拖出矩形 → 松开弹小面板填 `名称`+`类型`(Chat/RoomList/MapInfo)；已有框可拖角调整/删除。
- RoomList 类型：额外在框内拖竖线划列 → 每列选 `ColumnKind`(房间名/地图/玩家 ID/其它)。
- 保存：把框(相对当前窗口/客户区归一化为 `RelRect`)+ 列(相对 ROI 宽度)写入 `rois.json` 的 `custom_games_listing` 场景、`{W}x{H}` bucket（当前分辨率 key）；`RoiStore.Save`。
- 现有 ROI 加载显示（可编辑）。换分辨率：若无当前分辨率 bucket，提示"新建校准"。
- **坐标映射**复用 Phase 1 `ScreenMapper`/`NativeMethods.GetWindowScreenRect` 基准（保持与上屏一致，避免偏移）。

## 6. M4 热键系统（App，部分可单测）

- 默认绑定 `HotkeyDefaults.Map`（`功能名 → HotkeyBinding`）：
  `ToggleTranslate=F9`、`ToggleOverlay=F10`、`Calibrate=F8`、`OpenConfig=Ctrl+F9`、`TogglePlayerId=F7`、`TogglePause=F11`。
- `HotkeyService` 改为：接受"功能名→Action"映射 + 当前 `IReadOnlyDictionary<string,HotkeyBinding>`，一次性注册全部；`Rebind(功能名, newBinding)` → 注销旧、注册新、失败(被占用)回滚并返回 false。
- **冲突检测**（纯函数，可单测）：`HotkeyConflict.Find(bindings)` 返回绑定到同一 `(Modifiers,Vk)` 的功能名组；配置窗热键页据此高亮。
- 录制：热键页 `修改` → 捕获下一次按键组合 → 生成 `HotkeyBinding` → 冲突则提示、否则 `Rebind` + 存 `settings.json`。
- 新功能动作接线：`Calibrate`→开校准窗；`OpenConfig`→开配置窗；`TogglePlayerId`→翻转 `Translation.TranslatePlayerId` + Save + 复位变化检测 + 托盘/悬浮短提示；`TogglePause`→暂停/继续捕获（`CaptureService` 停/起或 Probe 门）。
- 提示：切换类热键触发后，悬浮层顶部或托盘气泡短显（如"玩家ID翻译: 开"），2 秒淡出（最小实现：托盘 `ShowBalloonTip` 或悬浮临时一行）。

## 7. 测试策略

**M1 Core 单测（无 UI，TDD）**：
- ROI v2 解析：旧 `rois.json`（无 columns）→ `Columns=null`；v2（有 columns）→ 正确 `RoiColumn` 列表；未知 type 兜底 Chat；**未知 kind 兜底 Other（且不翻）**；`RoiStore.Save` 往返（含 columns）稳定。
- `TranslationScope.ShouldTranslate`：Chat/MapInfo 型取对应开关；RoomList 各 `ColumnKind` 分别取 RoomName/MapInfo/PlayerId 开关、Other→false；RoomList 无列→按 RoomName；`TranslatePlayerId` 默认 false 时 PlayerId 列不翻、置 true 后翻。
- `HitColumn`：命中区间、边界(`[start,end)`)、无列→null、未命中→null。
- `SettingsStore` 往返：默认值 round-trip；缺文件→默认；损坏 JSON→默认不崩；只含非密钥（断言序列化不含任何 key 字段）。
- `HotkeyConflict.Find`：无冲突→空；两功能撞同键→成组；不同修饰键不算冲突。
- `HotkeyDefaults.Map`：六功能齐、默认值精确（回归保护）。
- 回归：`SettingsHolder.Current.Translation.*` 映射后，Phase 2/3 相关 Core 测试仍绿。

**M2/M3/M4 手动验收（WPF，无法自动化，用户实机）**：配置窗各页读写+落盘、校准拖框划列存 rois、热键录制/冲突/重绑/暂停、换分辨率提示、玩家 ID F7 即时生效。

## 8. YAGNI / 明确不做（留后续）

缓存 TTL（保持 LRU）、recent-context 注入、独立 LLM 节流器、Windows OCR/语言包分支、外观改色/字体实时预览（先只读或最小暴露）、开机自启的实际注册表写入（先存开关，接线留 Phase 5 打包）、多显示器精细适配、术语表图形编辑器（先"打开文件"）、聊天悬浮滚动历史/淡出的完整动画（先最小）、界面语言切换（先固定中文）。

## 9. 交付物清单（实现期，本次仅 spec+plan）

**M1 Core**：`RoiType`/`ColumnKind`/`RoiColumn`/`Roi`(+Columns)、`RoiStore`(v2 解析+`Save`)、`TranslationScope`/`HitColumn`/`ContentScopeSettings`、`AppSettings`/`GeneralSettings`/`OcrSettings`/`AppearanceSettings`/`HotkeyBinding`/`HotkeyDefaults`、`SettingsStore`、`HotkeyConflict`、`TranslationSettings`(+4 内容范围字段)、`SettingsHolder`(持 `AppSettings`) + 单测；`Phase1Probe`/`TrayService`/`App.xaml.cs` 适配 `Current.Translation.*`；**`App.xaml.cs` 启动调 `SettingsStore.Load()` 填充 `SettingsHolder.Current`**（防 Save-without-Load）。
**M2 App**：`SettingsWindow`(+6 页 + 关于) + 打开入口接线。
**M3 App**：`CalibrationWindow` + 拖框/划列/存盘。
**M4 App**：`HotkeyService` 重构 + `Rebind` + 录制 UI + 新功能动作接线 + 提示。
**文档回写（收尾）**：`ROADMAP.md` Phase 4 勾项、`UI_DESIGN.md` 技术项对齐（Papago/Claude/Windows OCR → 现状）。

## 10. 实现分期建议（供 plan 用）

- **Stage M1（Core 基座，TDD，可自动验收）**：本 Phase 唯一能脱离实机验收的部分——优先、独立成 plan/一批任务。完成即"数据与门控就绪"。
- **Stage M2/M3/M4（WPF + 接线，需实机可视验收）**：各自独立，建议 M1 合并且用户跑通一次 e2e 后再逐个实现（避免在未验证基座上堆不可验证 UI）。plan 对这三段给结构化任务 + XAML 线框 + 手动验收清单，不强求 TDD 粒度。
