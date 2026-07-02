# Phase 4 多区域 + 校准 UI + 配置窗口 + 热键 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** 把单 ROI 硬编码 + 两个硬编码热键，升级为可配置的多区域翻译器：多 ROI（聊天/房间列表/地图）按类型/列翻不翻（含玩家 ID 开关）、多标签配置窗、可自定义全局热键，设置与 ROI 按分辨率持久化。

**Architecture:** M1 数据基座（Core，TDD，可自动验收）——ROI v2 列定义 + 内容范围门控 + `AppSettings`/`SettingsStore` 持久化 + 热键默认/冲突。M2/M3/M4 为 WPF（配置窗/校准窗/热键系统），依赖 M1，验收靠**用户实机可视**（本 plan 给结构化任务 + XAML 线框 + 手动验收清单，不强求 TDD 粒度）。

**Tech Stack:** .NET 10 + WPF，xUnit，`System.Text.Json`，Win32 `RegisterHotKey`。解决方案 `ScrTranslator.slnx`，无参 `dotnet build`/`dotnet test`。

## Global Constraints

- **必须在 Windows 编译运行**；`dotnet build`/`dotnet test`/`dotnet run` 已在 PATH（dotnet 10.0.301）。
- TFM `net10.0-windows10.0.19041.0`；Core 无 UI/WinRT 依赖，可单测。
- **对齐 Phase 2/3 现状**（见 spec §1）：主引擎单选(DashScope/ChatGPT)+`TwoLevel` 开关（非 FastOnly/Hybrid/LlmOnly）；仅 PaddleOCR；密钥走 `SecretStore`(DPAPI)、`settings.json` **只存非密钥**。
- **玩家 ID 默认不翻**：`Translation.TranslatePlayerId` 默认 `false`；RoomList 的 `PlayerId` 列据此开关；房间名/地图列据各自内容范围开关。
- **向后兼容**：旧 `rois.json`（无 `columns`）读出 `Columns=null` → 整块按 RoomName 翻（等同 Phase 2）。未知 `type`→`Chat`、未知 `kind`→`Other`（不翻），均记日志。
- **防 Save-without-Load**：`App.xaml.cs` 启动必须 `SettingsStore.Load()` 填充 `SettingsHolder.Current`。
- **纯读屏**，不注入/不改游戏（沿用）。
- Git commit 结尾必须带：`Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。
- **UI（M2/M3/M4）验收 = 用户实机可视**；M1 验收 = `dotnet build` 0 错误 + `dotnet test` 全绿。跳过实机 e2e（用户暂缓）。

## 依赖与实现顺序

`M1a`(ROI+门控) → `M1b`(设置/热键基座+适配) →（**M3 校准** → **M2 配置窗** → **M4 热键接线**）。
理由：M2 的"区域"页有"进入校准"按钮打开 M3；M4 的 `Calibrate`/`OpenConfig` 动作需 M3/M2 窗口存在。故窗口先行、热键接线最后。M2 的"热键"页只读展示绑定（读 M1b 的 `HotkeyDefaults`/`SettingsStore`），录制/重绑交互随 M4。

---

# Stage M1a — ROI v2 + 内容范围门控（Core, TDD）

## Task 1: ROI 模型 v2 + rois.json v2 解析/保存

**Files:**
- Modify: `src/ScrTranslator.Core/Roi/RoiModels.cs`
- Modify: `src/ScrTranslator.Core/Roi/RoiStore.cs`
- Test: `tests/ScrTranslator.Core.Tests/RoiStoreTests.cs`

**Interfaces:**
- Produces: `enum RoiType { Chat, RoomList, MapInfo }`、`enum ColumnKind { RoomName, MapName, PlayerId, Other }`、`record RoiColumn(double RelStart, double RelEnd, ColumnKind Kind)`、`Roi(..., IReadOnlyList<RoiColumn>? Columns = null)`、`RoiType RoiTypes.Parse(string)`、`ColumnKind ColumnKinds.Parse(string)`、`RoiStore.Save(RoiConfig, string path)`。
- Consumes: 现有 `RelRect`/`Roi`/`SceneBucket`/`RoiConfig`/`RoiStore.Load`。

- [ ] **Step 1: Write failing tests**

在 `RoiStoreTests.cs` 增（新建一个临时 rois v2 文件用 `Path.GetTempFileName`）：

```csharp
    [Fact]
    public void Load_OldRois_NoColumns_ColumnsNull()
    {
        // 复用现有 assets/rois 或写临时旧格式(无 columns)
        var json = "{\"scenes\":{\"s\":{\"buckets\":[{\"resolution\":\"1920x1080\",\"rois\":[" +
                   "{\"name\":\"房间\",\"type\":\"RoomList\",\"rect\":[0.1,0.2,0.3,0.4]}]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var cfg = RoiStore.Load(path);
        var roi = cfg.Scenes["s"][0].Rois[0];
        Assert.Null(roi.Columns);
        Assert.Equal("RoomList", roi.Type);
    }

    [Fact]
    public void Load_V2_ParsesColumns()
    {
        var json = "{\"scenes\":{\"s\":{\"buckets\":[{\"resolution\":\"1920x1080\",\"rois\":[" +
                   "{\"name\":\"房间\",\"type\":\"RoomList\",\"rect\":[0.1,0.2,0.3,0.4]," +
                   "\"columns\":[{\"start\":0.0,\"end\":0.3,\"kind\":\"PlayerId\"}," +
                   "{\"start\":0.3,\"end\":1.0,\"kind\":\"RoomName\"}]}]}]}}}";
        var path = Path.GetTempFileName(); File.WriteAllText(path, json);
        var roi = RoiStore.Load(path).Scenes["s"][0].Rois[0];
        Assert.NotNull(roi.Columns);
        Assert.Equal(2, roi.Columns!.Count);
        Assert.Equal(ColumnKind.PlayerId, roi.Columns[0].Kind);
        Assert.Equal(0.3, roi.Columns[1].RelStart);
    }

    [Fact]
    public void RoiTypes_UnknownType_FallsBackChat()
        => Assert.Equal(RoiType.Chat, RoiTypes.Parse("nonsense"));

    [Fact]
    public void ColumnKinds_UnknownKind_FallsBackOther()
        => Assert.Equal(ColumnKind.Other, ColumnKinds.Parse("nonsense"));

    [Fact]
    public void Save_Then_Load_RoundTrips_WithColumns()
    {
        var roi = new Roi("房间", "RoomList", new RelRect(0.1, 0.2, 0.3, 0.4),
            new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId), new RoiColumn(0.3, 1.0, ColumnKind.RoomName) });
        var cfg = new RoiConfig(new Dictionary<string, IReadOnlyList<SceneBucket>>
            { ["s"] = new[] { new SceneBucket("1920x1080", new[] { roi }) } });
        var path = Path.GetTempFileName();
        RoiStore.Save(cfg, path);
        var back = RoiStore.Load(path).Scenes["s"][0].Rois[0];
        Assert.Equal("RoomList", back.Type);
        Assert.Equal(2, back.Columns!.Count);
        Assert.Equal(ColumnKind.RoomName, back.Columns[1].Kind);
        Assert.Equal(1.0, back.Columns[1].RelEnd);
    }
```

- [ ] **Step 2: Run tests → fail**

Run: `dotnet test --filter "FullyQualifiedName~RoiStoreTests"`
Expected: 编译失败（`RoiType`/`ColumnKind`/`RoiColumn`/`Columns`/`RoiTypes`/`ColumnKinds`/`Save` 不存在）。

- [ ] **Step 3: Implement**

`RoiModels.cs` 追加：

```csharp
public enum RoiType { Chat, RoomList, MapInfo }
public enum ColumnKind { RoomName, MapName, PlayerId, Other }
public record RoiColumn(double RelStart, double RelEnd, ColumnKind Kind);

public static class RoiTypes
{
    public static RoiType Parse(string s) => s switch
    {
        "Chat" => RoiType.Chat,
        "RoomList" => RoiType.RoomList,
        "MapInfo" => RoiType.MapInfo,
        _ => RoiType.Chat, // 未知兜底(调用方可另记日志)
    };
}
public static class ColumnKinds
{
    public static ColumnKind Parse(string s) => s switch
    {
        "RoomName" => ColumnKind.RoomName,
        "MapName" => ColumnKind.MapName,
        "PlayerId" => ColumnKind.PlayerId,
        "Other" => ColumnKind.Other,
        _ => ColumnKind.Other, // 未知兜底=不翻,最安全
    };
}
```

改 `Roi` 记录加可选 `Columns`：
```csharp
public record Roi(string Name, string Type, RelRect Rect, IReadOnlyList<RoiColumn>? Columns = null);
```

`RoiStore.cs`：`Load` 内解析 roi 时增 columns（在 `rois.Add(...)` 处替换）：
```csharp
                    IReadOnlyList<RoiColumn>? columns = null;
                    if (roiEl.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<RoiColumn>();
                        foreach (var c in colsEl.EnumerateArray())
                            list.Add(new RoiColumn(
                                c.GetProperty("start").GetDouble(),
                                c.GetProperty("end").GetDouble(),
                                ColumnKinds.Parse(c.GetProperty("kind").GetString() ?? "Other")));
                        columns = list;
                    }
                    rois.Add(new Roi(
                        roiEl.GetProperty("name").GetString()!,
                        roiEl.GetProperty("type").GetString()!,
                        rect, columns));
```

新增 `RoiStore.Save`（用 `System.Text.Json` 手写对象树，含 columns）：
```csharp
    public static void Save(RoiConfig cfg, string jsonPath)
    {
        var scenes = new Dictionary<string, object>();
        foreach (var (scene, buckets) in cfg.Scenes)
        {
            scenes[scene] = new
            {
                buckets = buckets.Select(b => new
                {
                    resolution = b.Resolution,
                    rois = b.Rois.Select(r => r.Columns is null
                        ? (object)new { name = r.Name, type = r.Type, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H } }
                        : new { name = r.Name, type = r.Type, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H },
                                columns = r.Columns.Select(c => new { start = c.RelStart, end = c.RelEnd, kind = c.Kind.ToString() }) })
                })
            };
        }
        var root = new { scenes };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(root,
            new JsonSerializerOptions { WriteIndented = true }));
    }
```
（`Save` 用了 LINQ `.Select`——在 `RoiStore.cs` 顶部**显式加** `using System.Linq;`；文件已有 `using System.Drawing; using System.Text.Json;`。）

- [ ] **Step 4: Run tests → pass**

Run: `dotnet test --filter "FullyQualifiedName~RoiStoreTests"`
Expected: PASS（旧 3 条 + 新 5 条）。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Roi tests/ScrTranslator.Core.Tests/RoiStoreTests.cs
git commit -m "feat(core): ROI v2 列定义(RoiColumn/ColumnKind)+rois.json v2 解析/Save+未知类型/列兜底

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: 内容范围门控 TranslationScope + HitColumn

**Files:**
- Create: `src/ScrTranslator.Core/Roi/TranslationScope.cs`
- Test: `tests/ScrTranslator.Core.Tests/TranslationScopeTests.cs`（新）

**Interfaces:**
- Produces: `record ContentScopeSettings(bool Chat, bool RoomName, bool MapInfo, bool PlayerId)`、`TranslationScope.ShouldTranslate(RoiType, ColumnKind?, ContentScopeSettings) → bool`、`TranslationScope.HitColumn(IReadOnlyList<RoiColumn>?, double relX) → ColumnKind?`。
- Consumes: `RoiType`/`ColumnKind`/`RoiColumn`（Task 1）。

- [ ] **Step 1: Write failing tests**

创建 `tests/ScrTranslator.Core.Tests/TranslationScopeTests.cs`：

```csharp
using ScrTranslator.Core;
namespace ScrTranslator.Core.Tests;
public class TranslationScopeTests
{
    private static readonly ContentScopeSettings All = new(true, true, true, true);
    private static readonly ContentScopeSettings Default = new(Chat: true, RoomName: true, MapInfo: true, PlayerId: false);

    [Fact] public void Chat_TakesChatFlag()
    {
        Assert.True(TranslationScope.ShouldTranslate(RoiType.Chat, null, All));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.Chat, null, All with { Chat = false }));
    }
    [Fact] public void MapInfo_TakesMapFlag()
        => Assert.False(TranslationScope.ShouldTranslate(RoiType.MapInfo, null, All with { MapInfo = false }));

    [Fact] public void RoomList_RoomNameColumn_TakesRoomNameFlag()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.RoomName, Default));
    [Fact] public void RoomList_MapNameColumn_TakesMapInfoFlag()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.MapName, Default));
    [Fact] public void RoomList_PlayerIdColumn_TakesPlayerIdFlag_DefaultOff()
    {
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.PlayerId, Default));
        Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.PlayerId, Default with { PlayerId = true }));
    }
    [Fact] public void RoomList_OtherOrNullColumn_False()
    {
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.Other, All));
        Assert.False(TranslationScope.ShouldTranslate(RoiType.RoomList, null, All)); // 有列语义但未命中列
    }

    [Fact] public void HitColumn_NoColumns_Null()
        => Assert.Null(TranslationScope.HitColumn(null, 0.5));
    [Fact] public void HitColumn_HitsInterval_HalfOpen()
    {
        var cols = new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId), new RoiColumn(0.3, 1.0, ColumnKind.RoomName) };
        Assert.Equal(ColumnKind.PlayerId, TranslationScope.HitColumn(cols, 0.1));
        Assert.Equal(ColumnKind.RoomName, TranslationScope.HitColumn(cols, 0.3)); // [start,end) → 0.3 属第二列
        Assert.Equal(ColumnKind.RoomName, TranslationScope.HitColumn(cols, 0.99));
    }
    [Fact] public void HitColumn_Miss_Null()
    {
        var cols = new[] { new RoiColumn(0.0, 0.3, ColumnKind.PlayerId) };
        Assert.Null(TranslationScope.HitColumn(cols, 0.5));
    }

    // RoomList 无列定义 → 整块按 RoomName(兼容 Phase 2)
    [Fact] public void RoomList_NoColumnsSemantics_ControllerPassesRoomName()
        => Assert.True(TranslationScope.ShouldTranslate(RoiType.RoomList, ColumnKind.RoomName, Default));
}
```

- [ ] **Step 2: Run → fail**

Run: `dotnet test --filter "FullyQualifiedName~TranslationScopeTests"`
Expected: 编译失败。

- [ ] **Step 3: Implement**

创建 `src/ScrTranslator.Core/Roi/TranslationScope.cs`：

```csharp
namespace ScrTranslator.Core;

/// <summary>内容范围开关（对应配置"翻译聊天/房间名/地图/玩家ID"）。</summary>
public record ContentScopeSettings(bool Chat, bool RoomName, bool MapInfo, bool PlayerId);

/// <summary>纯判定：某行是否翻译，及其所属列。无 UI 依赖，可单测。</summary>
public static class TranslationScope
{
    public static bool ShouldTranslate(RoiType type, ColumnKind? column, ContentScopeSettings s) => type switch
    {
        RoiType.Chat => s.Chat,
        RoiType.MapInfo => s.MapInfo,
        RoiType.RoomList => column switch
        {
            ColumnKind.RoomName => s.RoomName,
            ColumnKind.MapName => s.MapInfo,
            ColumnKind.PlayerId => s.PlayerId,
            _ => false, // Other / null(未命中列) → 不翻
        },
        _ => false,
    };

    /// <summary>relX = 行在 ROI 内的水平相对位置(0..1)；返回命中列语义,无列/未命中→null。区间取 [start,end)。</summary>
    public static ColumnKind? HitColumn(IReadOnlyList<RoiColumn>? columns, double relX)
    {
        if (columns is null) return null;
        foreach (var c in columns)
            if (relX >= c.RelStart && relX < c.RelEnd) return c.Kind;
        return null;
    }
}
```

> 注：`ShouldTranslate` 对 RoomList `null` 列返回 false；但 `Roi.Columns==null`（无列语义、整块）的兼容情形由调用方(Phase1Probe, Task 8)传 `ColumnKind.RoomName` 处理（见 Task 8）——本函数只管"给定列语义"的判定。

- [ ] **Step 4: Run → pass**

Run: `dotnet test --filter "FullyQualifiedName~TranslationScopeTests"`
Expected: PASS（11/11）。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Roi/TranslationScope.cs tests/ScrTranslator.Core.Tests/TranslationScopeTests.cs
git commit -m "feat(core): 内容范围门控 TranslationScope.ShouldTranslate + HitColumn(列命中)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

# Stage M1b — 设置/热键基座 + 适配（Core+App, TDD/回归）

## Task 3: TranslationSettings 增 4 内容范围字段

**Files:**
- Modify: `src/ScrTranslator.Core/Translation/TranslationSettings.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationSettingsTests.cs`

**Interfaces:**
- Produces: `TranslationSettings.{TranslateChat=true, TranslateRoomName=true, TranslateMapInfo=true, TranslatePlayerId=false}`。

- [ ] **Step 1: Write failing test**

在 `TranslationSettingsTests.cs` 加：
```csharp
    [Fact]
    public void Default_ContentScope_PlayerIdOff_OthersOn()
    {
        var s = TranslationSettings.Default;
        Assert.True(s.TranslateChat);
        Assert.True(s.TranslateRoomName);
        Assert.True(s.TranslateMapInfo);
        Assert.False(s.TranslatePlayerId); // 玩家 ID 默认不翻
    }
```

- [ ] **Step 2: Run → fail** (`dotnet test --filter "FullyQualifiedName~TranslationSettingsTests"`)

- [ ] **Step 3: Implement** — 在 `TranslationSettings` 加：
```csharp
    public bool TranslateChat { get; init; } = true;
    public bool TranslateRoomName { get; init; } = true;
    public bool TranslateMapInfo { get; init; } = true;
    public bool TranslatePlayerId { get; init; } = false;
```

- [ ] **Step 4: Run → pass**

- [ ] **Step 5: Commit**
```bash
git add src/ScrTranslator.Core/Translation/TranslationSettings.cs tests/ScrTranslator.Core.Tests/Translation/TranslationSettingsTests.cs
git commit -m "feat(core): TranslationSettings 增内容范围4开关(玩家ID默认关)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: AppSettings 聚合 + 子设置 + 热键默认（含 VK/MOD 常量入 Core）

**Files:**
- Create: `src/ScrTranslator.Core/Settings/AppSettings.cs`（含 `GeneralSettings`/`OcrSettings`/`AppearanceSettings`/`HotkeyBinding`/`HotkeyDefaults`/`HotkeyKeys` 常量）
- Test: `tests/ScrTranslator.Core.Tests/Settings/AppSettingsTests.cs`（新）

**Interfaces:**
- Produces: `AppSettings{General,Translation,Ocr,Appearance,Hotkeys}`；`GeneralSettings`/`OcrSettings`/`AppearanceSettings`（默认见 spec §3.4）；`record HotkeyBinding(uint Modifiers, uint Vk)`；`static HotkeyKeys`（`VK_F7..F11`、`MOD_NONE/CONTROL/ALT/SHIFT`）；`static HotkeyDefaults.Map : IReadOnlyDictionary<string,HotkeyBinding>`（键：`ToggleTranslate/ToggleOverlay/Calibrate/OpenConfig/TogglePlayerId/TogglePause`）。
- Consumes: `TranslationSettings`（Task 3）。

- [ ] **Step 1: Write failing tests**

创建 `tests/ScrTranslator.Core.Tests/Settings/AppSettingsTests.cs`：
```csharp
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class AppSettingsTests
{
    [Fact] public void Defaults_AreSane()
    {
        var a = new AppSettings();
        Assert.Equal("StarCraft", a.General.TargetProcess);
        Assert.True(a.General.StartMinimized);
        Assert.Equal(300, a.Ocr.ChatPollMs);
        Assert.Equal(800, a.Ocr.OtherPollMs);
        Assert.InRange(a.Appearance.Opacity, 0.0, 1.0);
        Assert.Equal(TranslationEngine.DashScope, a.Translation.Active); // 复用 TranslationSettings 默认
    }

    [Fact] public void HotkeyDefaults_HasAllSixFunctions_WithExactBindings()
    {
        var m = HotkeyDefaults.Map;
        Assert.Equal(HotkeyKeys.VK_F9, m["ToggleTranslate"].Vk);
        Assert.Equal(HotkeyKeys.VK_F10, m["ToggleOverlay"].Vk);
        Assert.Equal(HotkeyKeys.VK_F8, m["Calibrate"].Vk);
        Assert.Equal(HotkeyKeys.VK_F9, m["OpenConfig"].Vk);
        Assert.Equal(HotkeyKeys.MOD_CONTROL, m["OpenConfig"].Modifiers);
        Assert.Equal(HotkeyKeys.VK_F7, m["TogglePlayerId"].Vk);
        Assert.Equal(HotkeyKeys.VK_F11, m["TogglePause"].Vk);
        Assert.Equal(6, m.Count);
    }
}
```

- [ ] **Step 2: Run → fail** (`dotnet test --filter "FullyQualifiedName~AppSettingsTests"`)

- [ ] **Step 3: Implement**

创建 `src/ScrTranslator.Core/Settings/AppSettings.cs`：
```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Settings;

public sealed record GeneralSettings
{
    public string TargetProcess { get; init; } = "StarCraft";
    public bool AutoStart { get; init; } = false;
    public bool StartMinimized { get; init; } = true;
    public bool AutoTranslateOnLaunch { get; init; } = false;
}
public sealed record OcrSettings
{
    public int ChatPollMs { get; init; } = 300;
    public int OtherPollMs { get; init; } = 800;
    public bool Upscale { get; init; } = true;
    public bool Grayscale { get; init; } = true;
    public bool Binarize { get; init; } = false;
    public int ChangeSensitivity { get; init; } = 5; // 0..10
}
public sealed record AppearanceSettings
{
    public string FontFamily { get; init; } = "Microsoft YaHei";
    public int FontSize { get; init; } = 18;
    public double Opacity { get; init; } = 0.8;
    public bool ShowOriginalAndTranslation { get; init; } = false;
}
public sealed record HotkeyBinding(uint Modifiers, uint Vk);

public static class HotkeyKeys
{
    public const uint VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;
    public const uint MOD_NONE = 0x0, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
}
public static class HotkeyDefaults
{
    public static IReadOnlyDictionary<string, HotkeyBinding> Map { get; } =
        new Dictionary<string, HotkeyBinding>
        {
            ["ToggleTranslate"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F9),
            ["ToggleOverlay"]   = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F10),
            ["Calibrate"]       = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8),
            ["OpenConfig"]      = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9),
            ["TogglePlayerId"]  = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["TogglePause"]     = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F11),
        };
}

public sealed record AppSettings
{
    public GeneralSettings General { get; init; } = new();
    public TranslationSettings Translation { get; init; } = TranslationSettings.Default;
    public OcrSettings Ocr { get; init; } = new();
    public AppearanceSettings Appearance { get; init; } = new();
    public IReadOnlyDictionary<string, HotkeyBinding> Hotkeys { get; init; } = HotkeyDefaults.Map;
}
```

- [ ] **Step 4: Run → pass**

- [ ] **Step 5: Commit**
```bash
git add src/ScrTranslator.Core/Settings/AppSettings.cs tests/ScrTranslator.Core.Tests/Settings/AppSettingsTests.cs
git commit -m "feat(core): AppSettings 聚合(General/Ocr/Appearance/Hotkeys)+热键默认+VK/MOD常量入Core

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: SettingsStore（settings.json 持久化，只存非密钥）

**Files:**
- Create: `src/ScrTranslator.Core/Settings/SettingsStore.cs`
- Test: `tests/ScrTranslator.Core.Tests/Settings/SettingsStoreTests.cs`（新）

**Interfaces:**
- Produces: `SettingsStore.Save(AppSettings, string path)`、`SettingsStore.Load(string path) → AppSettings`（缺文件/损坏→默认）。
- Consumes: `AppSettings`（Task 4）。

- [ ] **Step 1: Write failing tests**
```csharp
using ScrTranslator.Core.Settings;
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Settings;
public class SettingsStoreTests
{
    [Fact] public void Save_Then_Load_RoundTrips()
    {
        var s = new AppSettings
        {
            Translation = TranslationSettings.Default with { TwoLevel = true, TranslatePlayerId = true },
            Ocr = new OcrSettings { ChatPollMs = 250 },
        };
        var path = Path.GetTempFileName();
        SettingsStore.Save(s, path);
        var back = SettingsStore.Load(path);
        Assert.True(back.Translation.TwoLevel);
        Assert.True(back.Translation.TranslatePlayerId);
        Assert.Equal(250, back.Ocr.ChatPollMs);
        Assert.Equal(6, back.Hotkeys.Count);
    }

    [Fact] public void Load_MissingFile_ReturnsDefault()
        => Assert.Equal("StarCraft", SettingsStore.Load("nope_no_file.json").General.TargetProcess);

    [Fact] public void Load_CorruptJson_ReturnsDefault()
    {
        var path = Path.GetTempFileName(); File.WriteAllText(path, "{ not valid json ");
        Assert.Equal(TranslationEngine.DashScope, SettingsStore.Load(path).Translation.Active);
    }

    [Fact] public void Saved_Json_ContainsNoSecretKeyField()
    {
        var path = Path.GetTempFileName();
        SettingsStore.Save(new AppSettings(), path);
        var json = File.ReadAllText(path);
        // 断言不含任何密钥字段名(AppSettings 本就不含,回归保护防未来误加)
        Assert.DoesNotContain("ApiKey", json);
        Assert.DoesNotContain("accessToken", json);
        Assert.DoesNotContain("refreshToken", json);
    }
}
```

- [ ] **Step 2: Run → fail**

- [ ] **Step 3: Implement**
```csharp
using System.Text.Json;
namespace ScrTranslator.Core.Settings;

/// <summary>settings.json 读写(只存非密钥；密钥走 SecretStore/DPAPI)。缺文件/损坏→默认,不崩。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        // Hotkeys 是 IReadOnlyDictionary<string,HotkeyBinding> —— STJ 原生支持 string 键字典。
    };

    public static void Save(AppSettings settings, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(settings, Opts));

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            return s ?? new AppSettings();
        }
        catch { return new AppSettings(); } // 损坏/反序列化失败 → 默认
    }
}
```
> 注：`AppSettings`/子 record 用 `{ get; init; }` + 无参构造，STJ 可反序列化。`IReadOnlyDictionary` 属性 STJ 能反序列化为字典。若 `HotkeyBinding` 定位构造参数反序列化有问题，改其为 `{ get; init; }` 属性或加 `[JsonConstructor]`——实现时以测试为准（`Save_Then_Load_RoundTrips` 会暴露）。

- [ ] **Step 4: Run → pass**（若 round-trip 因 record 定位参数失败，按上注调整 `HotkeyBinding` 为 init 属性后再绿）

- [ ] **Step 5: Commit**
```bash
git add src/ScrTranslator.Core/Settings/SettingsStore.cs tests/ScrTranslator.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "feat(core): SettingsStore settings.json 往返(只存非密钥,缺/损坏→默认)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: HotkeyConflict 冲突检测

**Files:**
- Create: `src/ScrTranslator.Core/Settings/HotkeyConflict.cs`
- Test: `tests/ScrTranslator.Core.Tests/Settings/HotkeyConflictTests.cs`（新）

**Interfaces:**
- Produces: `HotkeyConflict.Find(IReadOnlyDictionary<string,HotkeyBinding>) → IReadOnlyList<IReadOnlyList<string>>`（每组=绑到同一 (Mod,Vk) 的功能名，≥2 个才算一组）。

- [ ] **Step 1: Write failing tests**
```csharp
using ScrTranslator.Core.Settings;
namespace ScrTranslator.Core.Tests.Settings;
public class HotkeyConflictTests
{
    [Fact] public void Defaults_NoConflict()
        => Assert.Empty(HotkeyConflict.Find(HotkeyDefaults.Map));

    [Fact] public void SameKey_TwoFunctions_OneConflictGroup()
    {
        var m = new Dictionary<string, HotkeyBinding>
        {
            ["A"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["B"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F7),
            ["C"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F8),
        };
        var groups = HotkeyConflict.Find(m);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Contains("A", groups[0]); Assert.Contains("B", groups[0]);
    }

    [Fact] public void DifferentModifiers_SameVk_NoConflict()
    {
        var m = new Dictionary<string, HotkeyBinding>
        {
            ["A"] = new(HotkeyKeys.MOD_NONE, HotkeyKeys.VK_F9),
            ["B"] = new(HotkeyKeys.MOD_CONTROL, HotkeyKeys.VK_F9),
        };
        Assert.Empty(HotkeyConflict.Find(m));
    }
}
```

- [ ] **Step 2: Run → fail**

- [ ] **Step 3: Implement**
```csharp
namespace ScrTranslator.Core.Settings;
public static class HotkeyConflict
{
    public static IReadOnlyList<IReadOnlyList<string>> Find(IReadOnlyDictionary<string, HotkeyBinding> bindings)
        => bindings
            .GroupBy(kv => (kv.Value.Modifiers, kv.Value.Vk))
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<string>)g.Select(kv => kv.Key).ToList())
            .ToList();
}
```
（需 `using System.Linq;`。）

- [ ] **Step 4: Run → pass**

- [ ] **Step 5: Commit**
```bash
git add src/ScrTranslator.Core/Settings/HotkeyConflict.cs tests/ScrTranslator.Core.Tests/Settings/HotkeyConflictTests.cs
git commit -m "feat(core): HotkeyConflict.Find 检测重复绑定(同 Mod+Vk 成组)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: SettingsHolder 持 AppSettings + 调用点适配 + 启动 Load

**Files:**
- Modify: `src/ScrTranslator.Core/Translation/SettingsHolder.cs`
- Modify: `src/ScrTranslator.App/App.xaml.cs`
- Modify: `src/ScrTranslator.App/Services/TrayService.cs`
- Modify: `src/ScrTranslator.App/Phase1Probe.cs`
- Modify: `src/ScrTranslator.App/Services/HotkeyService.cs`（VK/MOD 常量改引用 Core `HotkeyKeys`，删本地重复常量）

**Interfaces:**
- Consumes: `AppSettings`/`SettingsStore`/`HotkeyKeys`（Task 4/5）。
- Produces: `SettingsHolder.Current : AppSettings`。所有旧 `Current.Xxx`(TranslationSettings) 用法迁到 `Current.Translation.Xxx`。

> 本任务以**编译 0 错误 + 全量测试绿（回归）**为验收。行为须与 Phase 2/3 等价。

- [ ] **Step 1: SettingsHolder 改持 AppSettings**

`SettingsHolder.cs`：
```csharp
using ScrTranslator.Core.Settings;
namespace ScrTranslator.Core.Translation;
public sealed class SettingsHolder
{
    public AppSettings Current { get; set; } = new AppSettings();
}
```

- [ ] **Step 2: 适配所有调用点（`Current.Xxx` → `Current.Translation.Xxx`）**

搜索 `grep -rn "Current" src/ScrTranslator.App`（**用宽 `Current` 而非 `\.Current\.`**——后者会漏掉把 `Current` 整体当对象传的站点）。已知需改站点：
- `TrayService.SetEngine`/`RefreshEngineChecks`/`ToggleTwoLevel`：`Current.<field>` → `Current.Translation.<field>`；写回 `Current = Current with {…}` → `Current = Current with { Translation = Current.Translation with {…} }`。
- `Phase1Probe.OnFrame`：`_settings.Current.Active/TimeoutSeconds/TwoLevel` → `_settings.Current.Translation.*`。
- `App.xaml.cs` DI：`settings.Current.DashScopeModel/FastModel/Active` → `settings.Current.Translation.*`。
- **`App.xaml.cs` 传给 `TieredTranslator` 构造的最后一个实参 `() => settings.Current`**（是 `Func<TranslationSettings>`，`Current` 现变 `AppSettings` 会类型不匹配编译错，grep `\.Current\.` 抓不到它）→ 改 `() => settings.Current.Translation`。
> 通则：凡把 `settings.Current`/`_settings.Current` **整体**当 `TranslationSettings` 用的地方（不止 `.Current.<field>`）都要补 `.Translation`。

- [ ] **Step 3: HotkeyService 用 Core 常量**

`HotkeyService.cs` 删除本地 `VK_F7..F11`/`MOD_*` 常量，改引用 `ScrTranslator.Core.Settings.HotkeyKeys`（`App.xaml.cs` 里 `HotkeyService.VK_F9` 等改 `HotkeyKeys.VK_F9`）。保留 `Register`/`WndProc`/`Dispose` 不变（M4 再重构注册逻辑）。

- [ ] **Step 4: App.xaml.cs 启动 Load + Save 接线**

DI 注册 `SettingsHolder` 改为从磁盘加载（防 Save-without-Load）：
```csharp
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        builder.Services.AddSingleton(new SettingsHolder { Current = SettingsStore.Load(settingsPath) });
```
并提供一个保存入口（供托盘/配置窗改设置后落盘）——最小：在 `App` 暴露 `void SaveSettings()` 或注册一个 `Action` 保存委托；托盘 `SetEngine`/`ToggleTwoLevel` 末尾调它。（DI 传 `settingsPath` 给 `TrayService` 或一个 `Func`。实现时选一种，保持"改设置即落盘"。）

- [ ] **Step 5: Build + 全量测试**

Run: `dotnet build` → 0 错误（若报某处漏改 `Current.`，按报错逐个补）。
Run: `dotnet test` → 全绿（无回归；Core 新测 + 旧测）。

- [ ] **Step 6: Commit**
```bash
git add -A
git commit -m "refactor: SettingsHolder 持 AppSettings;调用点迁 Current.Translation.*;启动 SettingsStore.Load;HotkeyService 用 Core 常量

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Phase1Probe 多 ROI + 内容范围门控接线

**Files:**
- Modify: `src/ScrTranslator.App/Phase1Probe.cs`
- （可选）Modify: `src/ScrTranslator.App/rois.json`（示例加 RoomList 列定义，供实机验；非必需）

**Interfaces:**
- Consumes: `TranslationScope.ShouldTranslate`/`HitColumn`（Task 2）、`RoiTypes.Parse`（Task 1）、`Current.Translation` 内容范围（Task 3/7）。

> 验收=编译 0 错误 + 全量测试绿（纯判定逻辑已在 Task 2 单测；本任务是编排接线，实机门控效果由用户 e2e）。

- [ ] **Step 1: OnFrame 逐行门控**

在 `OnFrame` 里，对每个 ROI 的每条 OCR line，计算是否翻译：
- `var roiType = RoiTypes.Parse(roi.Type);`
- 计算行的 ROI 内相对水平位置：`double relX = (line.Bounds.X + line.Bounds.Width / 2.0) / Math.Max(1, px.Width);`（`line.Bounds` 已是 ROI 裁剪子图内的相对坐标——见 Phase 3 代码 `frameBounds = line.Bounds.X + px.Left`，故此处**不再减 px.Left**；`px` 为该 ROI 像素矩形，`px.Width` 为其宽）
- `ColumnKind? col = roi.Columns is null ? (roiType == RoiType.RoomList ? ColumnKind.RoomName : (ColumnKind?)null) : TranslationScope.HitColumn(roi.Columns, relX);`
  （无列的 RoomList → 视作 RoomName 整块；Chat/MapInfo 无列语义传 null，由 ShouldTranslate 的类型分支决定。）
- `var scope = new ContentScopeSettings(t.TranslateChat, t.TranslateRoomName, t.TranslateMapInfo, t.TranslatePlayerId);`（`t = _settings.Current.Translation`）
- `bool translate = TranslationScope.ShouldTranslate(roiType, col, scope);`
- **仅把 `translate==true` 的行**收进 `koLines`/`metas`（不翻的行不进翻译、不叠加覆盖项）。其余流程（变化检测 `joined`、`TieredTranslator`、Render、AllResolved 提交）不变。

- [ ] **Step 2: Build + 全量测试** → 0 错误 / 全绿。

- [ ] **Step 3: Commit**
```bash
git add -A
git commit -m "feat(app): Phase1Probe 按 ROI 类型/列门控逐行翻译(不翻行不叠加;玩家ID列据开关)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

# Stage M2/M3/M4 — WPF（结构化任务，用户实机可视验收）

> 这三段是 WPF UI/交互，**无法单测**；本 plan 给每段：文件、XAML 线框、接线步骤、**手动验收清单**。实现顺序（依赖）见顶部："M1 → M3 → M2 → M4"。**建议 M1 合并且用户跑通一次 e2e 后再逐段实现**（避免在未验证基座上堆不可验证 UI）。每段结束 `dotnet build` 0 错误 + 用户按清单实机验。

## Stage M3: 校准窗口 CalibrationWindow

**Files:** Create `src/ScrTranslator.App/Calibration/CalibrationWindow.xaml(.cs)`；接线 `App.xaml.cs`（提供打开方法）/`TrayService`（"进入校准…"菜单）。

**要点（按 UI_DESIGN §5）:**
- 全屏 `AllowsTransparency=true`、半透明深色 `Background`、`WindowStyle=None`、`Topmost=true`、**可交互**（不点穿）。
- Canvas 上鼠标拖出矩形 → 松开弹小面板：`名称`TextBox + `类型`ComboBox(Chat/RoomList/MapInfo) + 确定。已有框可选中拖角/删除(Del)。
- RoomList：框内拖竖线划列，每列 ComboBox 选 `ColumnKind`(房间名/地图/玩家ID/其它)。
- 加载现有 `rois.json`(当前分辨率 bucket)显示可编辑；无该分辨率 bucket → 空白 + 提示"新建校准"。
- `Enter` 保存：把每个框(相对屏幕/客户区归一化为 `RelRect`)+ 列(相对 ROI 宽度) 组装 `RoiConfig`，`RoiStore.Save` 到 `rois.json` 的 `custom_games_listing`/`{W}x{H}`；`Esc` 取消不保存。坐标基准复用 `NativeMethods.GetWindowScreenRect`/`ScreenMapper`（与上屏一致）。

**手动验收清单:** 拖框命名存盘→重开校准能看到；RoomList 划列并逐列设翻/不翻→写入 rois.json 的 columns；Esc 不保存；换分辨率提示新建；保存后 Phase1 探测按新 ROI/列翻译（配合 Task 8）。

**Tasks:** (M3-1) 窗口壳 + 全屏半透明可交互 + F8 打开/Esc 关；(M3-2) 拖框+命名+类型小面板+已有框编辑/删；(M3-3) RoomList 划列+逐列 kind；(M3-4) 加载/保存 rois.json v2 + 分辨率 key + 提示。每 Task 结束 build 0 错误 + 增量手动验。

## Stage M2: 配置窗口 SettingsWindow

**Files:** Create `src/ScrTranslator.App/Config/SettingsWindow.xaml(.cs)`（TabControl 左侧竖直标签，6 页 + 关于）；接线 `TrayService`("配置…" + 双击)、`App.xaml.cs`(Ctrl+F9 由 M4 接)。

**要点（按 UI_DESIGN §3，技术项按 spec §1 对齐）:** DataContext=`AppSettings` 可编辑副本；`应用`=写 `SettingsHolder.Current`+`SettingsStore.Save`+触发 `OnTranslationStateChanged`；`确定`=应用并关；`取消`=丢弃关。单例(已开则激活)。
- 各页字段见 spec §4（翻译页：主引擎单选+两级开关+DashScope Key(复用 `DashScopeKeyWindow`)+ChatGPT 登录(复用 `OpenAiOAuth`)+模型 id + 内容范围4勾选(玩家ID注 F7)+术语表"编辑"(打开 GLOSSARY.md)+缓存(标 LRU)；节流/上下文置灰"后续"）。区域页列 ROI + "进入校准"(打开 M3)。热键页只读展示绑定(录制交互随 M4)。外观页字体/字号/不透明度/显示模式；临时/最终样式只读。
- 密钥字段**不进** `settings.json`（走 SecretStore）。

**手动验收清单:** 双击托盘/菜单打开；改内容范围/两级/模型→应用→落盘→重启仍在；取消丢弃；密钥经现有窗口存 SecretStore；区域页"进入校准"打开 M3；关于页信息齐。

**Tasks:** (M2-1) 窗口壳+TabControl+应用/确定/取消+单例+托盘双击/菜单打开；(M2-2) 常规+外观页；(M2-3) 翻译页(引擎/两级/密钥入口/内容范围/术语表/缓存)；(M2-4) OCR 页;(M2-5) 区域页(列表+进入校准+列定义展示)；(M2-6) 热键页只读 + 关于页。每 Task build 0 错误 + 增量手动验。

## Stage M4: 热键系统重构 + 全功能接线

**Files:** Modify `src/ScrTranslator.App/Services/HotkeyService.cs`、`App.xaml.cs`；新增录制交互到 M2 热键页(`SettingsWindow` 热键页 code-behind)。

**要点:**
- `HotkeyService` 改为：构造接受"功能名→Action"映射；`RegisterAll(IReadOnlyDictionary<string,HotkeyBinding>)` 一次注册全部；`bool Rebind(string func, HotkeyBinding b)`(注销旧+注册新,失败回滚返回 false)。
- `App.xaml.cs` 用 `Current.Hotkeys` + 动作字典注册：`ToggleTranslate`/`ToggleOverlay`(已有)、`Calibrate`→开 M3、`OpenConfig`→开 M2、`TogglePlayerId`→翻转 `Current.Translation.TranslatePlayerId`+Save+复位变化检测+提示、`TogglePause`→暂停/继续捕获。
- M2 热键页：`修改`按钮→捕获下一次按键组合→`HotkeyBinding`→`HotkeyConflict.Find` 校验→冲突高亮/提示，否则 `Rebind`+`SettingsStore.Save`。`恢复默认`→写回 `HotkeyDefaults.Map`。
- 切换类触发→托盘 `ShowBalloonTip` 或悬浮临时一行短提示(如"玩家ID翻译: 开")。

**手动验收清单:** 六键默认可用；F7 即时切玩家 ID(房间列表 ID 列随之翻/不翻)；F8 开校准；Ctrl+F9 开配置；F11 暂停/继续；热键页录制重绑→冲突高亮→保存后新键生效、重启仍在；恢复默认可用。

**Tasks:** (M4-1) HotkeyService 重构为 map 注册 + Rebind；(M4-2) 全功能动作接线(含 Calibrate/OpenConfig/TogglePlayerId/TogglePause)；(M4-3) M2 热键页录制/冲突/重绑/恢复默认 + 落盘；(M4-4) 切换提示。每 Task build 0 错误 + 增量手动验。

---

## Task 收尾: 文档回写

**Files:** Modify `docs/ROADMAP.md`(Phase 4 勾项)、`docs/UI_DESIGN.md`(技术项对齐：Papago/Claude/Windows OCR/三引擎模式 → DashScope/ChatGPT/PaddleOCR/两级开关；缓存 TTL→LRU；节流/上下文标"后续")。

- [ ] 勾 ROADMAP Phase 4 已完成项（校准 UI 的"拖框划列"、配置窗、热键 F7/F8/F11/Ctrl+F9、多 ROI/ID 列过滤）；UI 可视项标"用户实机验"。
- [ ] UI_DESIGN 技术项对齐（保留 UX 线框，改过时技术名词）。
- [ ] Commit（trailer 同上）。

---

## Self-check：Spec 验收 → Task 对照

| Spec 验收项 | 对应 |
|---|---|
| M1 Core 单测(ROI v2/门控/AppSettings 往返/热键冲突) | Task 1,2,4,5,6 |
| 内容范围翻译门控(类型/列;玩家ID) | Task 2(判定)+Task 8(接线) |
| 设置持久化+启动加载(防Save-without-Load) | Task 5,7 |
| 配置窗六页+应用/取消+密钥走SecretStore | Stage M2 |
| 校准拖框/划列/按分辨率存 | Stage M3 |
| 热键默认+录制重绑+冲突+暂停+F7 | Task 6 + Stage M4 |
| 换分辨率提示重校准 | Stage M3(加载分支) |
| 回归 Phase 2/3 不破 | Task 3,7,8(等价迁移+全量测试) |

## 执行提示

- **M1（Task 1–8）可现在按 subagent-driven-development 全自动执行**（TDD，`dotnet build`/`test` 验收）；每任务隔离审。
- **M2/M3/M4（Stage）建议 M1 合并 + 用户跑通一次 e2e 后再实现**；其 Task 无法 TDD，靠 build + 用户手动验收清单；实现前可对每 Stage 再出细化 plan。
