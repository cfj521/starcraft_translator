# 移除用户自定义 ROI 层 + 预设 override 层 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删掉用户自定义 ROI 整层和预设 override 层，预设成为识别区域唯一来源并直接读写出厂文件；配置页"区域" tab 与校准窗只在 DEBUG 模式可用、直接读写预设。

**Architecture:** `RelRect` 保留并移到 Core 根；`LayoutPreset` 删 `ToRois()` 加 `Parts()`（返回 `(name, RelRect)`）；`LayoutPresetStore` 改单文件读写（删 override 层）；翻译管线只吃 `Parts()`；校准窗/配置页/RoiEditWindow 改吃 `RelRect`；删 `Roi`/`RoiConfig`/`RoiStore`/列式翻译死代码/rois.json。DEBUG 门控复用 `General.DebugMode`。

**Tech Stack:** C# / .NET 10 / WPF / SkiaSharp（仅 LayoutCheck 工具）。

**Spec:** `docs/superpowers/specs/2026-10-16-remove-roi-layer-design.md`

## Global Constraints

- 无 `.sln`。构建：`dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`。测试：`dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`。运行前 `taskkill /F /IM ScrTranslator.App.exe`。
- `RelRect` namespace 保持 `ScrTranslator.Core`（移到 Core 根，不随文件夹变）。
- `layout-presets.json` 从只读变可读写；不再写 APPDATA override 层。
- DEBUG 门控复用 `AppSettings.General.DebugMode`（F8 切换）。
- `DocsConsistencyTests.LiveSymbols`/`RetiredSymbols` 是 CI 约束，删符号必须同步。
- `opencode.json` 保持未跟踪。
- `RelRect` 的 `ToPixels` 做成扩展方法（Core），管线与工具共用。

## Review Focus

这五个失败模式 spec 暗示但没有现成测试覆盖，最可能咬到用的人：

1. **`DebugMode=false` 时按 Ctrl+F9（校准热键）** → 不应打开校准窗。期望：无操作/打日志。门控是 `OpenCalibration` 入口一行守卫（Task 5），属一行 guard，靠编译+人工核（Task 8 复核无旁路），不单抽纯函数测。
2. **`DebugMode=false` 时打开配置页** → "区域" tab 不可见。期望：tab `Visibility=Hidden`。测试加到 Task 4（可见性判定抽成纯函数测）。
3. **游戏宽高比无任何预设档** → `Find` 返回 null。期望：`presetParts()` 空、不崩、该场景不翻译。测试加到 Task 2。
4. **`layout-presets.json` 加载失败**（损坏/缺失）→ 触发 `NoRoi`。期望：提示"预设加载失败"而非"尚未校准"。测试加到 Task 8（消息串抽成可测）。
5. **编辑预设 part 并保存** → 写的是出厂 `layout-presets.json`（不是 APPDATA override）。期望：出厂文件被改、运行时重载生效。测试加到 Task 3。

---

## File Structure

| 文件 | 责任 | 动作 |
|---|---|---|
| `src/ScrTranslator.Core/RelRect.cs` | `RelRect` record + `ToPixels` 扩展 | 建（从 RoiModels 移入 + 加扩展） |
| `src/ScrTranslator.Core/Scene/LayoutPresets.cs` | `LayoutPreset`（`Parts()`）+ `LayoutPresetStore`（单文件读写） | 改（删 ToRois/override，加 Parts/Save） |
| `src/ScrTranslator.App/TranslationEngine.cs` | 翻译管线，只吃预设 parts | 改（删 _cfg/RoiStore，EffectiveRois→presetParts） |
| `src/ScrTranslator.App/Calibration/CalibrationWindow.xaml(.cs)` | 校准窗，仅预设框 | 改（删用户框/列 UI，BoxToRoi→RelRect） |
| `src/ScrTranslator.App/Config/SettingsWindow.xaml(.cs)` | 配置页，区域 tab 仅预设+debug 门控 | 改 |
| `src/ScrTranslator.App/Config/RoiEditWindow.xaml(.cs)` | 预设 part 编辑窗 | 改（Roi→(sceneId,partName,RelRect)） |
| `src/ScrTranslator.App/App.xaml.cs` | DI、热键、托盘、NoRoi 消息 | 改 |
| `tools/LayoutCheck/Program.cs` | 预设核验工具 | 改（ToRois→Parts） |
| `src/ScrTranslator.Core/Roi/{RoiModels,RoiStore,TranslationScope}.cs` | 用户 ROI 层 | 删 |
| `src/ScrTranslator.App/rois.json` | 用户 ROI 数据 | 删 |
| `tests/.../{RoiStoreTests,RoiStoreEditTests,TranslationScopeTests}.cs` | 用户 ROI 测试 | 删 |
| `tests/.../Scene/LayoutPresetTests.cs` | 预设测试 | 改（删 override，加 Parts/Save） |
| `tests/.../DocsConsistencyTests.cs` | 文档一致性 | 改（LiveSymbols/RetiredSymbols） |
| `CLAUDE.md`/`README.md`/`docs/{E2E_CHECKLIST,ROADMAP,UI_DESIGN,TECH_DESIGN}.md` | 文档 | 改 |

---

### Task 1: RelRect 移 Core 根 + LayoutPreset.Parts()

**Files:**
- Create: `src/ScrTranslator.Core/RelRect.cs`
- Modify: `src/ScrTranslator.Core/Roi/RoiModels.cs`（删 `RelRect`，留 Roi/RoiConfig/RoiColumn/ColumnKind/ColumnKinds）
- Modify: `src/ScrTranslator.Core/Scene/LayoutPresets.cs`（加 `Parts()`，留 `ToRois()`）
- Test: `tests/ScrTranslator.Core.Tests/Scene/LayoutPresetTests.cs`

**Interfaces:**
- Produces: `LayoutPreset.Parts() -> IReadOnlyList<(string Name, RelRect Rect)>`；`RelRect` 在 `ScrTranslator.Core`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Parts_ReturnsNonEmptyPartsAsNameRelRectPairs()
{
    var p = new LayoutPreset("room", "16:9",
        Chat: new RelRect(0.1, 0.6, 0.4, 0.2),
        GameName: new RelRect(0.5, 0.3, 0.1, 0.02));
    var parts = p.Parts();
    var names = parts.Select(t => t.Name).ToArray();
    Assert.Contains("chat", names);
    Assert.Contains("gameName", names);
    Assert.DoesNotContain("roomNameList", names); // 未设 → 不出现
    Assert.DoesNotContain("mapName", names);
    var chat = parts.First(t => t.Name == "chat").Rect;
    Assert.Equal(0.6, chat.Y, 6);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release --filter FullyQualifiedName~Parts_ReturnsNonEmptyParts`
Expected: 编译失败（`Parts` 不存在）。

- [ ] **Step 3: 移 RelRect 到 Core 根**

把 `RelRect` record 从 `Roi/RoiModels.cs` 移到新文件 `src/ScrTranslator.Core/RelRect.cs`（namespace 仍 `ScrTranslator.Core`）。`RoiModels.cs` 留其余类型。

- [ ] **Step 4: 加 `Parts()` 到 `LayoutPreset`**

按非空依次返回 `("chat", Chat)`、`("roomNameList", RoomNameList)`、`("gameName", GameName)`、`("mapName", MapName)`。名字**不带** `%preset%` 前缀。`ToRois()` 暂留（后续任务迁移调用方）。

- [ ] **Step 5: 跑测试确认通过 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。

- [ ] **Step 6: Commit**

```bash
git add src/ScrTranslator.Core/RelRect.cs src/ScrTranslator.Core/Roi/RoiModels.cs src/ScrTranslator.Core/Scene/LayoutPresets.cs tests/ScrTranslator.Core.Tests/Scene/LayoutPresetTests.cs
git commit -m "feat: RelRect 移 Core 根 + LayoutPreset.Parts()（name+RelRect，去 %preset% 前缀）"
```

---

### Task 2: 翻译管线改吃预设 parts + RelRect.ToPixels 扩展

**Files:**
- Modify: `src/ScrTranslator.Core/RelRect.cs`（加 `ToPixels` 扩展方法）
- Modify: `src/ScrTranslator.App/TranslationEngine.cs`（删 `_cfg`/`_roisJsonPath`/`ReloadRois`/`SelectRois`/`RoiStore.ToPixels`；`EffectiveRois`→`presetParts`；`RefreshRoiDerivedFlags` 简化）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（`TranslationEngine` 构造删 `roisJsonPath`；L302/L332 `probe.ReloadRois();` 删，留 `ReloadLayoutPresets`）
- Test: `tests/ScrTranslator.Core.Tests/RelRectTests.cs`（新建）

**Interfaces:**
- Produces: `RelRect.ToPixels(this RelRect, int w, int h) -> System.Drawing.Rectangle`（钳制到 [0,w]/[0,h]）；`TranslationEngine.presetParts(w,h) -> IReadOnlyList<(string Name, RelRect Rect)>`（私有）。
- Consumes: `LayoutPresetStore.Find(sceneId,w,h)`、`LayoutPreset.Parts()`（Task 1）。

- [ ] **Step 1: 写失败测试（ToPixels 扩展 + Find-null→空）**

```csharp
[Fact]
public void ToPixels_ConvertsAndClamps()
{
    var r = new RelRect(0.25, 0.5, 0.2, 0.1).ToPixels(1920, 1080);
    Assert.Equal(480, r.X); Assert.Equal(540, r.Y);
    Assert.Equal(384, r.Width); Assert.Equal(108, r.Height);
    var over = new RelRect(0.9, 0.0, 0.5, 0.5).ToPixels(1000, 1000);
    Assert.Equal(900, over.X); Assert.Equal(100, over.Width); // 钳制
}
```

（Find→null→presetParts 空 的断言在管线私有方法里不便直测，靠本扩展 + Task 3 的 `Find` 已有测试兜底；此处只测纯函数。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter FullyQualifiedName~ToPixels_ConvertsAndClamps`
Expected: 编译失败（扩展不存在）。

- [ ] **Step 3: 加 `ToPixels` 扩展方法**

`RelRect.cs` 里加 `public static Rectangle ToPixels(this RelRect r, int w, int h)`，逻辑同原 `RoiStore.ToPixels`（`Math.Round` 后 `Math.Clamp`）。

- [ ] **Step 4: 改 TranslationEngine 吃 presetParts**

删 `_cfg`/`_roisJsonPath`/`ReloadRois()`/`RoiStore.SelectRois`。`EffectiveRois(rois,w,h)` 改成 `presetParts(w,h)`：返回 `_layout.Find(SceneId, w, h)?.Parts() ?? Array.Empty<...>()`。OCR 循环 `foreach (var roi in EffectiveRois(...))` 改遍历 `(Name, Rect)`，`Rect.ToPixels(frame.Width, frame.Height)`。`RefreshRoiDerivedFlags`：`_roiAvailable = _layout.HasScene(SceneId)`；`_anySceneCalibrated = _layout.Presets.Count > 0`。`_lastEffectiveRoiNames`/`_cumTranslated` 按 parts 名。`App.xaml.cs` 构造删 `roisPath` 参数，L302/L332 删 `probe.ReloadRois();`。

- [ ] **Step 5: 跑测试 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。（CalibrationWindow/SettingsWindow/RoiEditWindow 仍用 RoiStore/Roi，本任务不动它们，故仍编译。）

- [ ] **Step 6: Commit**

```bash
git add src/ScrTranslator.Core/RelRect.cs src/ScrTranslator.App/TranslationEngine.cs src/ScrTranslator.App/App.xaml.cs tests/ScrTranslator.Core.Tests/RelRectTests.cs
git commit -m "refactor: 翻译管线只吃预设 parts（删 _cfg/RoiStore/ReloadRois）+ RelRect.ToPixels 扩展"
```

---

### Task 3: LayoutPresetStore 删 override 层 + 加 Save 直写

**Files:**
- Modify: `src/ScrTranslator.Core/Scene/LayoutPresets.cs`（删 `Merge`/`SaveOverride`/`RemoveOverride`/`OverrideCount`/`OverridesPath`/`FactoryPath`；`Load` 单参数；加 `Save`）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（`LayoutPresetStore.Load` 只传出厂路径，删 override 路径）
- Test: `tests/ScrTranslator.Core.Tests/Scene/LayoutPresetTests.cs`

**Interfaces:**
- Produces: `LayoutPresetStore.Save(LayoutPreset)`（按 `(Scene,Resolution)` upsert 直写出厂文件，复用 `ToJson`）；`Load(string factoryPath)` 单参。
- Consumes: `LayoutPreset.Parts()` 无关；`Find`/`HasScene`/`Presets`/`IsUsable` 保留。

- [ ] **Step 1: 写失败测试（Save 直写 + 回读）**

```csharp
[Fact]
public void Save_UpsertsIntoFactory_AndRoundTrips()
{
    var path = Path.GetTempFileName();
    var p = new LayoutPreset("room", "16:9", Chat: new RelRect(0.2, 0.7, 0.3, 0.1));
    LayoutPresetStore.Save(p, path);
    var store = LayoutPresetStore.Load(path);
    var found = store.Find("room", 1600, 900);
    Assert.NotNull(found);
    Assert.Equal(0.2, found!.Chat!.Value.X, 6);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter FullyQualifiedName~Save_UpsertsIntoFactory`
Expected: 编译失败（`Save` 不存在 / `Load` 还是双参）。

- [ ] **Step 3: 改 LayoutPresetStore 单文件 + 加 Save**

`Load(string factoryPath)` 单参（删 `overridesPath`/`Merge`）。加 `Save(LayoutPreset)`：读现有出厂文件→按 `(Scene,Resolution)` upsert→`ToJson` 写回（原子写，同 `RoiStore.Save` 的 tmp+Move 纪律）。删 `SaveOverride`/`RemoveOverride`/`OverrideCount`/`OverridesPath`/`FactoryPath`。`App.xaml.cs` 的 `LayoutPresetStore.Load` 只传出厂路径。

- [ ] **Step 4: 删 override 相关测试**

删 `LayoutPresetTests.cs` 里 `SaveOverride_*`/`RemoveOverride_*`/`Override_WinsPerSceneResolution_*`/`BrokenOverride_*`/双路径 `Load` 的测试。

- [ ] **Step 5: 跑测试 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。

- [ ] **Step 6: Commit**

```bash
git add src/ScrTranslator.Core/Scene/LayoutPresets.cs src/ScrTranslator.App/App.xaml.cs tests/ScrTranslator.Core.Tests/Scene/LayoutPresetTests.cs
git commit -m "refactor: LayoutPresetStore 删 override 层，加 Save 直写出厂文件"
```

---

### Task 4: RoiEditWindow 改 RelRect + 配置页区域 tab 仅预设+debug 门控

**Files:**
- Modify: `src/ScrTranslator.App/Config/RoiEditWindow.xaml.cs` + `.xaml`（输入 `Roi`→`(sceneId, partName, RelRect)`；`Result`→`(SceneId, PartName, Rect)`；删 `isPreset`/`Columns`/场景切换）
- Modify: `src/ScrTranslator.App/Config/SettingsWindow.xaml`（区域 tab 加 `Visibility` 绑定；删"恢复默认"按钮对该 tab 的适用）
- Modify: `src/ScrTranslator.App/Config/SettingsWindow.xaml.cs`（`LoadRoiList` 只走预设；删 `OnEditRoiClick` 用户分支/`OnDeleteRoiClick` 用户行/`CommitRois`/`RestoreRegionDefaults`/`FindPreset`；`RoiRow` 简化；debug 门控）
- Test: `tests/ScrTranslator.Core.Tests/SettingsWindowRegionTabTests.cs`（新建，测可见性判定纯函数）

**Interfaces:**
- Produces: `SettingsWindow.RegionTabVisible(bool debugMode) -> bool`（纯函数，便于测）；`RoiEditWindow(string sceneId, string partName, RelRect rect)`，`Result (string SceneId, string PartName, RelRect Rect)`。
- Consumes: `LayoutPresetStore.Find`、`LayoutPreset.Parts`/`Save`。

- [ ] **Step 1: 写失败测试（区域 tab 可见性判定）**

```csharp
[Fact]
public void RegionTabVisible_OnlyInDebugMode()
{
    Assert.True(SettingsWindow.RegionTabVisible(debugMode: true));
    Assert.False(SettingsWindow.RegionTabVisible(debugMode: false));
}
```

（把可见性判定抽成 `SettingsWindow` 的静态纯函数 `RegionTabVisible(bool debugMode)`，XAML 的 `Visibility` 由它驱动。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter FullyQualifiedName~RegionTabVisible`
Expected: 编译失败（`RegionTabVisible` 不存在）。

- [ ] **Step 3: 改 RoiEditWindow 吃 (sceneId, partName, RelRect)**

构造 `(string sceneId, string partName, RelRect rect)`；`Result` 改 `(string SceneId, string PartName, RelRect Rect)`。删 `isPreset`、`Columns`、场景切换（场景/part 名固定）。只改 rect（0~1 校验保留）。

- [ ] **Step 4: 改配置页区域 tab 仅预设 + debug 门控**

`LoadRoiList` 只走 `_layout.Find(sceneId, curW, curH)?.Parts()`，列 chat/roomNameList/gameName/mapName（**四个都列**，不再只两个）。删用户 ROI 行与 `OnEditRoiClick` 用户分支/`OnDeleteRoiClick` 用户行/`CommitRois`/`RestoreRegionDefaults`（删 override 文件那段）/`FindPreset`。`OnDeleteRoiClick` 改：预设 part 可删（置 null 持久化，`LayoutPresetStore.Save`）。区域 tab 的 `Visibility` 由 `RegionTabVisible(General.DebugMode)` 驱动；`DebugModeCheck` 切换后刷新。"恢复默认"按钮对区域 tab 不再显示。

- [ ] **Step 5: 跑测试 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。（CalibrationWindow 仍用 RoiStore/Roi，本任务不动，故仍编译。）

- [ ] **Step 6: Commit**

```bash
git add src/ScrTranslator.App/Config/RoiEditWindow.xaml src/ScrTranslator.App/Config/RoiEditWindow.xaml.cs src/ScrTranslator.App/Config/SettingsWindow.xaml src/ScrTranslator.App/Config/SettingsWindow.xaml.cs tests/ScrTranslator.Core.Tests/SettingsWindowRegionTabTests.cs
git commit -m "refactor: RoiEditWindow 改 RelRect + 配置页区域 tab 仅预设 parts、debug 门控、part 可删"
```

---

### Task 5: 校准窗仅预设框 + debug 门控

**Files:**
- Modify: `src/ScrTranslator.App/Calibration/CalibrationWindow.xaml.cs` + `.xaml`（删用户框/`LoadExisting`/列 UI；`BoxToRoi`→返回 `RelRect`；`SaveAndClose` 只写预设）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（`OpenCalibration` 入口查 `DebugMode`，false 不开窗）
- Test: `tests/ScrTranslator.Core.Tests/Calibration/BoxToRelRectTests.cs`（新建，测换算纯函数）

**Interfaces:**
- Produces: `CalibrationWindow.BoxToRelRect(CalibBox, System.Drawing.Rectangle gameRect, double dpiX, double dpiY) -> RelRect`（public static，便于测）。
- Consumes: `LayoutPresetStore.Find`/`Save`、`LayoutPreset.Parts`。

- [ ] **Step 1: 写失败测试（DIP→物理→相对换算）**

```csharp
[Fact]
public void BoxToRelRect_ComputesGameRelativeRect()
{
    var game = new System.Drawing.Rectangle(100, 200, 800, 600); // 物理像素
    var box = new CalibBox { CanvasRect = new System.Windows.Rect(0, 0, 80, 60) }; // DIP
    var rel = CalibrationWindow.BoxToRelRect(box, game, 1.0, 1.0);
    Assert.Equal((100 - 100) / 800.0, rel.X, 6); // 0
    Assert.Equal(60 / 600.0, rel.H, 6);          // 0.1
    Assert.Equal(80 / 800.0, rel.W, 6);          // 0.1
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter FullyQualifiedName~BoxToRelRect`
Expected: 编译失败（`BoxToRelRect` 不存在；现在叫 `BoxToRoi` 返回 `Roi`）。

- [ ] **Step 3: BoxToRoi→BoxToRelRect + 删用户框/列 UI**

`BoxToRoi` 改名 `BoxToRelRect`，返回 `RelRect`（去掉 `new Roi(box.Name, rect)`）。删 `LoadExisting`（读 rois.json）、用户框绘制、列 UI 死代码（`ColumnKindOptions`/`CreateColumnUi`/`BuildColumnPanel`/divider 全套）。`SaveAndClose` 只写预设：把每个预设框的 `BoxToRelRect` 结果折回 `LayoutPreset` 对应字段（chat/roomNameList/gameName/mapName），`LayoutPresetStore.Save(preset)` 直写。构造参数删 `roisJsonPath`。

- [ ] **Step 4: OpenCalibration debug 门控**

`App.xaml.cs` `OpenCalibration()` 入口：`if (!settings.Current.General.DebugMode) { log; return; }`。托盘"区域校准设置"项与 Ctrl+F9 热键动作都走这里。

- [ ] **Step 5: 跑测试 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。（此时 RoiStore/Roi 已无 App 内消费者，但类型还在——Task 7 删。）

- [ ] **Step 6: Commit**

```bash
git add src/ScrTranslator.App/Calibration/CalibrationWindow.xaml src/ScrTranslator.App/Calibration/CalibrationWindow.xaml.cs src/ScrTranslator.App/App.xaml.cs tests/ScrTranslator.Core.Tests/Calibration/BoxToRelRectTests.cs
git commit -m "refactor: 校准窗仅预设框、BoxToRelRect、debug 门控 OpenCalibration"
```

---

### Task 6: tools/LayoutCheck 改用 Parts()

**Files:**
- Modify: `tools/LayoutCheck/Program.cs`（`preset.ToRois()`→`preset.Parts()`；删网格绘制；`roi.Type`/`p.RoomList`/`p.Chat.Width` 等旧 API 修掉）

**Interfaces:**
- Consumes: `LayoutPresetStore.Load`/`Find`/`Parts`、`RelRect`（`X/Y/W/H`）。

- [ ] **Step 1: 改 LayoutCheck 用 Parts()**

`foreach (var roi in preset.ToRois())` → `foreach (var (name, rect) in preset.Parts())`。删 `roi.Type`/`preset.RoomList`/网格绘制段（`RoomNameList` 现在是 `RelRect` 不是网格）。`p.Chat.Width`→`p.Chat.W`。`p.RoomList`→`p.RoomNameList`。画 4 个 part 的框（无网格）。

- [ ] **Step 2: 编译该工具确认通过**

Run: `dotnet build tools/LayoutCheck/LayoutCheck.csproj -c Release`
Expected: 0 错误（之前是坏的，现在修好）。

- [ ] **Step 3: Commit**

```bash
git add tools/LayoutCheck/Program.cs
git commit -m "fix: LayoutCheck 改用 Parts()（删旧 ToRois/网格 API），重新可编译"
```

---

### Task 7: 删用户 ROI 层（Roi/RoiConfig/RoiStore/列式/rois.json/ToRois）

**Files:**
- Delete: `src/ScrTranslator.Core/Roi/RoiModels.cs`、`RoiStore.cs`、`TranslationScope.cs`、`src/ScrTranslator.Core/Roi/` 目录、`src/ScrTranslator.App/rois.json`
- Modify: `src/ScrTranslator.Core/Scene/LayoutPresets.cs`（删 `ToRois()`、`PresetNamePrefix`）
- Delete: `tests/ScrTranslator.Core.Tests/RoiStoreTests.cs`、`RoiStoreEditTests.cs`、`TranslationScopeTests.cs`

**Interfaces:**
- Produces: 无（纯删除）。前提：Task 2/4/5/6 已把管线/配置页/校准窗/LayoutCheck 全迁到 `Parts()`/`RelRect`，`Roi`/`RoiStore`/`ToRois`/列式无消费者。

- [ ] **Step 1: 删 ToRois + PresetNamePrefix**

`LayoutPresets.cs` 删 `ToRois()` 与 `PresetNamePrefix` 常量（无消费者了）。

- [ ] **Step 2: 删类型与文件**

删 `Roi`/`RoiConfig`/`RoiColumn`/`ColumnKind`/`ColumnKinds`（`RoiModels.cs`）、`RoiStore.cs`、`TranslationScope.cs`、`rois.json`、空 `Roi/` 目录。

- [ ] **Step 3: 删对应测试**

删 `RoiStoreTests.cs`/`RoiStoreEditTests.cs`/`TranslationScopeTests.cs`。

- [ ] **Step 4: 跑全量测试 + 构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误（若某处仍引用 `Roi`/`RoiStore`，编译会红，回到该消费者修）。

- [ ] **Step 5: Commit**

```bash
git add -A src/ScrTranslator.Core/Roi src/ScrTranslator.App/rois.json src/ScrTranslator.Core/Scene/LayoutPresets.cs tests/ScrTranslator.Core.Tests/RoiStoreTests.cs tests/ScrTranslator.Core.Tests/RoiStoreEditTests.cs tests/ScrTranslator.Core.Tests/TranslationScopeTests.cs
git commit -m "refactor: 删用户 ROI 层（Roi/RoiConfig/RoiStore/列式翻译/rois.json/ToRois）"
```

---

### Task 8: NoRoi 消息 + 校准热键/托盘 debug 门控确认

**Files:**
- Modify: `src/ScrTranslator.App/App.xaml.cs`（`NoRoi` 提示文案改"预设加载失败"；确认 `Calibrate` 热键动作与托盘项走 `OpenCalibration` 门控）
- Test: `tests/ScrTranslator.Core.Tests/AppProbeStartTests.cs`（新建，测 NoRoi 消息串——若文案抽成可测的静态方法）

**Interfaces:**
- Produces: `NoRoi` 提示串（若抽成 `App.NoRoiMessage` 静态属性便于测）。

- [ ] **Step 1: 写失败测试（NoRoi 消息）**

```csharp
[Fact]
public void NoRoiMessage_SaysPresetLoadFailed_NotCalibrate()
{
    var msg = App.NoRoiMessage;
    Assert.Contains("预设", msg);
    Assert.DoesNotContain("Ctrl+F9", msg); // 不再引导校准
}
```

（把 `App.xaml.cs` L237 的 `NoRoi` 文案抽成 `App.NoRoiMessage` 静态属性/方法，便于测。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter FullyQualifiedName~NoRoiMessage`
Expected: 编译失败（`NoRoiMessage` 不存在）。

- [ ] **Step 3: 改 NoRoi 文案 + 确认门控**

`NoRoi` 文案改"预设加载失败（layout-presets.json 不可用）"。确认 `Calibrate` 热键动作与托盘"区域校准设置"项都走 `OpenCalibration` 的 `DebugMode` 门控（Task 5 已加，这里复核无旁路）。

- [ ] **Step 4: 跑测试 + 全量构建**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.App/App.xaml.cs tests/ScrTranslator.Core.Tests/AppProbeStartTests.cs
git commit -m "fix: NoRoi 文案改'预设加载失败'，复核校准热键/托盘 debug 门控"
```

---

### Task 9: 文档同步 + DocsConsistencyTests

**Files:**
- Modify: `CLAUDE.md`、`README.md`、`docs/E2E_CHECKLIST.md`、`docs/ROADMAP.md`、`docs/UI_DESIGN.md`、`docs/TECH_DESIGN.md`（"用户自定义 ROI + 预设 override"→"预设唯一来源，debug 门控直接读写"）
- Modify: `tests/ScrTranslator.Core.Tests/DocsConsistencyTests.cs`（`LiveSymbols` 删 `ReplaceRoi`/`RemoveRoi`/`MoveRoi`/`HitColumn`/`EffectiveRois`/`ReloadRois`/`CopyFromScreen`/`layout-overrides.json`；`RetiredSymbols` 视措辞加 `Roi`/`rois.json`/`layout-overrides.json`）

**Interfaces:** 无。

- [ ] **Step 1: 改 DocsConsistencyTests 符号表**

`LiveSymbols` 删已删符号 + 死条目 `CopyFromScreen`。`RetiredSymbols` 加被删且文档仍会提到的名字（`Roi`、`rois.json`、`layout-overrides.json` 等，视文档措辞）。

- [ ] **Step 2: 跑 DocsConsistencyTests 看缺什么**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release --filter FullyQualifiedName~DocsConsistency`
Expected: 若文档仍"现役口吻"提到已删符号，测试会列出行号。

- [ ] **Step 3: 改文档**

把 6 个文档里"用户自定义 ROI + 预设 override"的描述改成"预设唯一来源，debug 门控直接读写"。被删符号带退役标记或移除。

- [ ] **Step 4: 跑 DocsConsistencyTests 确认通过**

Run: `dotnet test ... --filter FullyQualifiedName~DocsConsistency`
Expected: 全绿。

- [ ] **Step 5: 跑全量测试 + 构建收尾**

Run: `dotnet test tests/ScrTranslator.Core.Tests/ScrTranslator.Core.Tests.csproj -c Release`
Run: `dotnet build src/ScrTranslator.App/ScrTranslator.App.csproj -c Release`
Expected: 全绿，0 错误。

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md README.md docs/E2E_CHECKLIST.md docs/ROADMAP.md docs/UI_DESIGN.md docs/TECH_DESIGN.md tests/ScrTranslator.Core.Tests/DocsConsistencyTests.cs
git commit -m "docs: 同步'预设唯一来源、debug 门控直接读写'，清 DocsConsistency 符号表"
```
