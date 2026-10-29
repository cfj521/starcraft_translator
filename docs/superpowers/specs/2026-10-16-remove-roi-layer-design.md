# 移除用户自定义 ROI 层 + 预设 override 层，预设转 debug 门控直接读写

## 意图与原因

之前设计让用户可以自定义翻译区域（`Roi` 类 + rois.json），并为预设做了 override 覆盖层
（layout-overrides.json）。重新评估后：

- **用户自定义区域必要性不大**：游戏中真正可翻译的区域很少（聊天框、房间名列表、房间/地图名），
  出厂预设已全覆盖，让用户再画框既没准头也没必要。
- **override 层多余**：预设出厂值就是唯一权威，"出厂只读 + APPDATA 覆盖"的双层结构没有价值，
  还引入"出厂永不被碰"的复杂不变式。
- **预设成为识别区域唯一来源**，直接读写出厂文件 `layout-presets.json`。
- **配置页"区域" tab 默认隐藏**，仅 DEBUG 模式可见，直接读写预设。
- **校准功能仅 DEBUG 模式可用**，直接读写预设（不再有"用户框"概念）。

## 目标 / 非目标 / 反目标

**目标**
- 删除用户自定义 ROI 层：`Roi`、`RoiConfig`、`RoiStore`、rois.json、列式翻译（`RoiColumn`/`ColumnKind`/`ColumnKinds`/`TranslationScope`）。
- 删除预设 override 层：`layout-overrides.json`、`LayoutPresetStore` 的 `Merge`/`SaveOverride`/`RemoveOverride`/`OverrideCount`/`OverridesPath`。
- 预设改为直接读写出厂文件 `layout-presets.json`。
- 配置页"区域" tab：仅 DEBUG 模式可见，只列预设 parts，直接读写。
- 校准窗：仅 DEBUG 模式可用，只有预设框（无用户框），直写预设。
- 翻译管线：只吃预设 parts，去掉 `_cfg`/`RoiStore`/`ReloadRois`。

**非目标**
- 不动场景识别（scene-refs / scene-overrides 双层保留，那是另一套独立机制）。
- 不动 `RelRect`（`ResolutionAnalyzer` 的内容区矩形用它，与 ROI 无关）。
- 不动预设的"按宽高比分档"匹配逻辑（`Find` 的比例就近匹配保留）。
- 不改预设出厂数值本身。

**反目标（做错了会怎样）**
- 把 `Roi` 删了却忘了改管线/校准/配置页的 ROI 消费点 → 编译红。
- override 删了但 `RestoreRegionDefaults` 还在删 override 文件 → 运行时行为错乱。
- 区域 tab 没门控 debug → 普通用户看到不该有的页。
- 文档一致性测试（`DocsConsistencyTests`）没同步 → 测试红。

## 约束

- `RelRect` 必留（`ResolutionAnalyzer`、`LayoutPreset` 共用）。
- `layout-presets.json` 从"只读出厂"变为"可读写"——这是本设计刻意打破的旧不变式。
- DEBUG 门控复用 `AppSettings.General.DebugMode`（F8 切换，已存在）。
- `DocsConsistencyTests.LiveSymbols` 是 CI 约束：删掉的符号要么从表里去掉，要么文档带退役标记。

## 设计

### 1. 删除的类型与文件

| 符号 / 文件 | 处置 | 原因 |
|---|---|---|
| `Roi` record | 删 | 用户自定义区域实体；预设改用自己的 parts |
| `RoiConfig` | 删 | 用户 ROI 配置容器 |
| `RoiColumn` / `ColumnKind` / `ColumnKinds` | 删 | 列式翻译，运行时从不读（死代码） |
| `RoiStore`（整个文件） | 删 | 用户 ROI 存取（rois.json） |
| `TranslationScope`（整个文件） | 删 | `HitColumn` 列判定，运行时零调用 |
| `rois.json`（App 目录下出厂种子） | 删 | 用户 ROI 数据 |
| `LayoutPreset.ToRois()` | 删 | 预设→`Roi` 桥；改出 parts 方法 |

`RelRect` 保留，从 `Roi/RoiModels.cs` 移到 `src/ScrTranslator.Core/RelRect.cs`（通用类型，
放 Core 根而非 `Scene/`），namespace 不变 `ScrTranslator.Core`，`Roi/RoiModels.cs` 整个删除。
`Roi/` 目录下 `RoiStore.cs`、`TranslationScope.cs`、`RoiModels.cs` 全删后，空目录一并删。

### 2. `LayoutPreset` 改造

- 删 `ToRois()`。新增 `IReadOnlyList<(string Name, RelRect Rect)> Parts()`，按非空返回
  `(“chat”, Chat)`、`("roomNameList", RoomNameList)`、`("gameName", GameName)`、`("mapName", MapName)`。
  名字不再带 `%preset%` 前缀（那是为了与"用户框"区分，现在只有预设）。
- `LayoutPresetStore`：
  - `Load(factoryPath)` 单参数，删 `overridesPath`/`Merge`/`OverrideCount`。
  - 新增 `Save(preset)`：按 `(Scene, Resolution)` upsert 直写出厂文件（沿用现有 `ToJson` 手写格式）。
  - 删 `SaveOverride`/`RemoveOverride`/`OverridesPath`/`FactoryPath`（只有一个文件了，路径存一个字段）。
  - `Find`/`HasScene`/`Presets`/`IsUsable`/`LoadError` 保留。

### 3. 翻译管线（`TranslationEngine`）

- 删 `_cfg`/`_roisJsonPath`/`ReloadRois()`/`RoiStore.SelectRois`/`RoiStore.ToPixels`（`ToPixels` 逻辑内联或移到 `RelRect` 扩展）。
- `EffectiveRois(rois, w, h)` → `presetParts()`：`_layout.Find(SceneId, w, h)?.Parts() ?? empty`。
  返回 `IReadOnlyList<(string Name, RelRect Rect)>`。
- `RefreshRoiDerivedFlags`：`_roiAvailable = _layout.HasScene(SceneId)`；`_anySceneCalibrated = _layout.Presets.Count > 0`。
  `NoRoi` 分支基本变死路（预设恒存在），保留枚举值但不触发。**但 `App.xaml.cs` 里 `NoRoi` 的提示文案
  "尚未校准区域：按 Ctrl+F9 框选游戏聊天/房间区域后再按 F9" 要改成"预设加载失败"之类**——因为现在触发
  `NoRoi` 只可能是 `layout-presets.json` 加载失败，不再是"没校准"。
- **`ReloadRois` 的调用点**：`App.xaml.cs` L302（校准保存后）与 L332（配置保存后）都是
  `probe.ReloadRois(); probe.ReloadLayoutPresets();` 连调。删 `ReloadRois` 后这两处改成只调
  `probe.ReloadLayoutPresets();`。
- OCR 循环把 `foreach (var roi in EffectiveRois(...))` 改为遍历 `(Name, Rect)`，`ToPixels(Rect, ...)`。
- 调试面板 `_lastEffectiveRoiNames`/`_cumTranslated` 仍按 parts 名累加（键不变）。
- `ReloadLayoutPresets()` 保留（校准/配置保存后重载），只是单文件。

### 4. 校准窗（`CalibrationWindow`）

- 删用户框概念：`LoadExisting`（读 rois.json）整段删；`BoxToRoi` **的 DIP→物理像素→相对坐标换算逻辑保留**
  （预设框照样要它），只把返回类型从 `Roi` 改成 `RelRect`（不再包 `Roi`，不再带 `Columns`）。
  `Roi`/`RoiConfig` 引用删。
- 只留预设框：`DrawPresetBoxes` 建 4 个预设框（chat/roomNameList/gameName/mapName，按场景可用项）。
- `SaveAndClose` 只写预设：把每个预设框的 rel rect 折回 `LayoutPreset` 对应字段，`LayoutPresetStore.Save(preset)` 直写出厂文件。
- 删列 UI 死代码（`ColumnKindOptions`/`CreateColumnUi`/`BuildColumnPanel`/divider 全套，从未被调用）。
- 构造参数去掉 `roisJsonPath`。
- **DEBUG 门控**：`OpenCalibration`（App.xaml.cs）入口处查 `General.DebugMode`，false 则不开窗（打日志/无操作）。托盘"区域校准设置"项与 Ctrl+F9 热键动作都走同一门控。

### 5. 配置页"区域" tab（`SettingsWindow`）

- **DEBUG 门控**：tab 仅在 `General.DebugMode` 为 true 时可见。实现：构造/加载时按 `DebugMode` 决定该 `TabItem` 的 `Visibility`（或从 `PagesTab` 移除/加入）。"关于"页的 `DebugModeCheck` 切换后需刷新该 tab 可见性。
- tab 内容只列预设 parts（chat/roomNameList/gameName/mapName，按场景），不再有用户 ROI 行。
  `RoiRow` 简化或替换为预设行结构；`LoadRoiList` 只走 `_layout.Find(...)`。
- 编辑：双击预设行打开改造后的 `RoiEditWindow`（见下），写回 `LayoutPresetStore.Save(preset)` 直写出厂文件。
- 删 `OnEditRoiClick`（用户 ROI 分支）/`CommitRois`/`RestoreRegionDefaults`（删 override 文件那段）/`FindPreset` 旧逻辑。
  `OnDeleteRoiClick` 改为：**预设 part 可删**（置 null 持久化）——现在只有预设行，删除即把该 part 从预设里去掉，
  配得上"直接读写"。不再"拒绝删除、移到画面外停用"。
- `进入校准` 按钮保留（tab 本身已 debug 门控）。

### 6. `RoiEditWindow` 改造

- 输入从 `Roi` 改为 `(string sceneId, string partName, RelRect rect)`；`Result` 改为 `(string SceneId, string PartName, RelRect Rect)`。
- 删 `Columns` 相关（本来就没有列 UI）。`isPreset` 参数可删（现在全是预设）。
- 仅改 rect（场景/part 名固定，因为预设 parts 是定死的）。

### 7. 热键 / DI / 托盘

- `App.xaml.cs` DI：删 `roisPath` 及传给 `TranslationEngine` 的 `roisJsonPath`；`LayoutPresetStore.Load` 单文件。
- `TranslationEngine` 构造删 `roisJsonPath` 参数。
- `Calibrate` 热键动作 + 托盘"区域校准设置"项 → 走 `OpenCalibration` 的 debug 门控。
- `OpenSettings` 传给 `SettingsWindow` 的参数去掉 `roisPath`。
- 托盘菜单项"区域校准设置"在 `DebugMode=false` 时隐藏或禁用（视托盘重建成本，至少动作门控）。

### 8. `tools/LayoutCheck`（已坏，必须一并处理）

`LayoutCheck/Program.cs` **当前就已编译不过**（对着早已不存在的旧 API 写死）：`p.Chat.Width`/`Height`
（`RelRect` 实为 `W`/`H`）、`p.RoomList`（实为 `RoomNameList`）、`roi.Type`（`Roi` 实为 `Name`）、
以及一整段早已不存在的 `RoomList` 网格对象（`Rows`/`RowHeight`/`Columns`/`c.ToRelative`）。它是独立工具，
不在主构建里，所以主构建一直绿，但它本身是坏的。本设计删 `ToRois()` 会让它更坏，必须处理。
**决定：改它用 `Parts()`**——画 4 个预设 part 的框（删掉网格绘制，`RoomNameList` 现在是 `RelRect` 不是网格），
让它重新编译且继续可用（预设成为唯一来源后，改坐标时用它对截图核验仍有价值）。保留 `LiveSymbols` 里的
`tools/LayoutCheck` 条目。

### 9. 测试

- 删 `RoiStoreEditTests.cs`（整文件，测 `ReplaceRoi`/`RemoveRoi`/`MoveRoi`）。
- 删 `TranslationScopeTests.cs`（整文件，测 `HitColumn`）。
- `RoiStoreTests.cs`：`RoiStore` 删了，整文件删（`ToPixels` 逻辑若内联则其测试移到 `RelRect` 或管线测试）。
- `LayoutPresetTests.cs`：删 override 相关（`SaveOverride_*`/`RemoveOverride_*`/`Override_WinsPerSceneResolution_*`/`BrokenOverride_*`/双路径 `Load`）；保留并改 `Parts()` 测试 + 出厂值 `Shipping*` 测试；新增 `Save` 直写测试。
- `DocsConsistencyTests.LiveSymbols`：删 `ReplaceRoi`/`RemoveRoi`/`MoveRoi`/`HitColumn`/`EffectiveRois`/`ReloadRois`/`layout-overrides.json` 等已删符号；`RetiredSymbols` 视文档措辞加 `Roi`/`rois.json` 等。

### 10. 文档同步（`DocsConsistencyTests` 约束）

`CLAUDE.md`/`README.md`/`docs/E2E_CHECKLIST.md`/`docs/ROADMAP.md`/`docs/UI_DESIGN.md`/`docs/TECH_DESIGN.md`
把"用户自定义 ROI + 预设 override"描述改为"预设唯一来源，debug 门控直接读写"。被删符号在文档里带退役标记或移除。

## 决定（builder 不用再猜）

- `RoiEditWindow` 保留为独立窗口，改吃 `(sceneId, partName, RelRect)`。
- 托盘"区域校准设置"项：动作走 `OpenCalibration` 的 debug 门控（false 则不开窗），不重建托盘菜单。
- `ToPixels` 做成 `RelRect` 扩展方法，管线更干净。
- 区域 tab 的 debug 门控用 `Visibility` 切换。
- 区域 tab 的"恢复默认"按钮：删 `RestoreRegionDefaults` 后无可恢复之物（无用户 ROI、无 override 层，出厂文件即权威）。**该 tab 不再显示"恢复默认"按钮**（`RestorePageDefaults` 的 `case "区域"` 删除，或该 tab 加入隐藏恢复按钮的名单）。
- 区域 tab 可见性刷新要覆盖**所有** `DebugMode` 变更路径：F8 热键、托盘、以及"关于"页的 `DebugModeCheck`。设置窗打开期间切 F8 也要刷新该 tab 的 `Visibility`。
- **预设 part 可删**（配置页删除按钮 + 校准窗删框都置 null 持久化），不再"拒绝删除、移画面外停用"。删光某场景全部 part = 该场景不翻译（`_roiAvailable` false），合法。
- `DocsConsistencyTests.LiveSymbols` 里的 `CopyFromScreen` 本就是死条目（src 里早已没有），顺手清掉。

## 我的理解（待你确认的假设）

1. **"移除 ROI 类" = 删 `Roi` 本身**，管线/校准/配置页改吃预设 parts（name + RelRect）。若你只想删"用户 ROI 层"而保留 `Roi` 作管线表示，告诉我，改动会小很多。
2. **出厂 `layout-presets.json` 变为可读写**（不再有 APPDATA 覆盖层）。
3. **校准只剩 4 个预设框**，不能再画新区域（因为可翻译区域就那几个）。
4. **debug 门控**复用现有 `General.DebugMode`（F8）。
