# Phase 3 设计 — 两级翻译编排 + 术语表注入 prompt + 防刷屏

> 状态：设计定稿（用户睡眠中，已授权自主推进；以隔离 subagent 审为质量门，非重大分歧不中断）。
> 日期：2026-07-02。分支基线：`phase2-translation`（Phase 2 已完成、未合 main、e2e 暂缓）。
> 关联：`docs/ROADMAP.md` Phase 3、`docs/TECH_DESIGN.md` §3.5 第 5 点、Phase 2 spec `2026-07-02-phase2-translation-design.md`。

## 0. 目标与验收

**目标**：把 Phase 2 的"单级、一次返回"翻译，升级为**两级编排**——先用**快态 provider**给出临时译文（视觉上标记为"临时"），再用**质量 provider**给出最终译文并在**同一位置原地替换**（视觉恢复正常）。同时把**术语表注入 system prompt**（让长句也用统一术语），并确保**防刷屏/控费**（不因两级而翻倍消耗）。

**验收**（Phase 3 完成的判据，e2e 由用户后续实机验，非本次）：
1. Core 单测全绿：`TieredTranslator` 两级/单级编排、`PromptBuilder` 注入、`Glossary.Entries` 枚举、provider 携带 `TranslationContext` 发请求。
2. 开启"两级翻译"后（托盘勾选），同一句韩文先出临时中文（灰/半透明），随后原地替换为最终中文（正常白）；命中术语表/缓存的行**直接**出最终态（无临时态闪烁）。
3. 关闭"两级翻译"时，行为与 Phase 2 **完全一致**（单级、一次上屏、默认状态）。
4. 术语表内的黑话短词（如 `앞마당`/`러시`）即使出现在长房间名里，最终译文也用术语表统一译法（因 glossary 已注入 prompt）。
5. 重复内容不因两级而多花 API：缓存存**最终态**，重复行下次直接命中直出，不再走快态+质量态两次请求。

## 1. 关键决策（自主定，均可逆，用户醒后可否决）

1. **两级默认关闭（opt-in）**。`TranslationSettings.TwoLevel` 默认 `false`，托盘勾选开启。理由：两级对**每个未命中新行**要发**两次**请求（快态+质量态），与项目一贯"省费用"取向冲突；Phase 2（已实现的单级路径）作为安全默认，两级作为可选增强，代码内建齐、随时可开。**这是 Phase 3 的头等特性但默认不自动烧钱**——若用户希望默认开，一行改默认值即可。
2. **两级的"快/质量"= 两个 `ITranslationProvider` 实例**，编排层只认接口、不认具体家。App 组合根决定二者解析到谁：
   - **快态**默认 = DashScope `qwen-flash`（便宜、快）。
   - **质量态**默认 = 当前"主引擎"（`Active`）及其模型（DashScope 或 ChatGPT 订阅）。
   - **推荐配置**（醒后按需调，非本次强制）：主引擎设 ChatGPT `gpt-5`（质量）+ 快态 DashScope `qwen-flash`；或主引擎 DashScope 但把质量模型设为 `qwen-plus`。若快、质量解析到**同一 provider+同一 model**，两级退化为"两次相同请求"——无害但无意义，文档提示用户避免。
3. **缓存存最终态**。快态结果**不入缓存**（临时、可能不准）；质量态成功后写缓存。重复行下次经 glossary/cache 命中 → 直接最终态。这既保证质量又控费（快态只在"首次见到某行"付费）。
4. **术语表注入 = 把术语条目表拼进 system prompt**（带长度上界），两 provider 共用一个 `PromptBuilder`。**不做**分词/模糊匹配注入（YAGNI）；exact-match 直出仍走 `Glossary.TryGet`（Phase 2 已有），注入只是让 provider 对"未 exact 命中但含术语的长句"也用统一译法。
5. **场景(Scene)注入 = 固定标签**（当前只有 `custom_games_listing`）。**最近上下文(recent context)注入暂不做**（YAGNI）：房间列表各行无上下文连续性，聊天 ROI 是 Phase 4；留 `TranslationContext` 结构位以便后续扩展。
6. **防刷屏 = 复用 Phase 2 既有机制 + 两级不加压**。现有：`_lastShot` 400ms 取帧间隔 + `_busy` 单飞(single-flight，一帧未处理完丢弃后续帧) + 变化检测(`_lastJoined` 相同则跳过) + 缓存直出。两级新增的唯一风险是"一帧处理更久（快+质量串行）"，期间新帧被单飞丢弃——对准静态的房间列表可接受。**不新增**独立节流器/去抖器（YAGNI）；仅确保缓存存最终态使重复内容零请求。

## 2. 架构与分层

### Core 改动（`ScrTranslator.Core/Translation/`）

**新增类型**
- `TranslationStage`（enum）：`Direct`（术语/缓存命中，无临时态）、`Provisional`（快态）、`Final`（质量态/最终）。
- `LineTranslation`（record）：`int Index`（对应输入 `koreanLines` 下标）、`string Text`、`TranslationStage Stage`。
- `TranslationContext`（record）：`string Scene`、`string? GlossaryHint`、`IReadOnlyList<string>? RecentContext`（暂恒为 null，占位）。
- `PromptBuilder`（static）：`BuildSystemPrompt(TranslationContext ctx)` → 在 `BatchProtocol` 的基础 system prompt 上追加场景说明 + 术语表提示（`GlossaryHint`）。基础批量协议措辞仍复用 `BatchProtocol`。
- `TieredTranslator`：**取代 `TranslationService`** 的编排器，回调式 API 支持一次(单级)或两次(两级)上屏（见 §3）。

**修改类型**
- `ITranslationProvider.TranslateAsync` 增 `TranslationContext ctx` 参数（见 §3）；`DashScopeProvider`/`ChatGptSubscriptionProvider` 用 `PromptBuilder.BuildSystemPrompt(ctx)` 替代写死的 `BatchProtocol.SystemPrompt`。
- `Glossary` 增 `IReadOnlyCollection<KeyValuePair<string,string>> Entries { get; }`（供 `PromptBuilder` 拼术语提示；对已有解析逻辑无影响）。
- `TranslationSettings` 增 `TwoLevel`(bool=false)、`FastEngine`(TranslationEngine=DashScope)、`FastModel`(string="qwen-flash")；`Active`/`DashScopeModel`/`ChatGptModel`/`TimeoutSeconds` 语义不变（`Active`+其模型 = 质量态/单级用）。

**删除**
- `TranslationService`（被 `TieredTranslator` 取代）及其测试文件——重写为 `TieredTranslator` 测试（单级模式覆盖原有全部保证）。

### App 改动
- `Phase1Probe.OnFrame`：由"一次 await 拿 list → 一次上屏"改为"订阅 `TieredTranslator` 的分级回调 → 每级把当前快照 zip 位置 → `OverlayWindow.ShowLines` 重绘"（见 §4）。保留帧内 `metas`（位置）不变。
- `OverlayWindow`：`OverlayItem` 增 `bool Provisional`；`ShowLines` 对 `Provisional` 用灰/半透明样式，其余用现有白粗体（见 §5）。
- DI（`App.xaml.cs`）：把 `TranslationService` 注册换成 `TieredTranslator`；新增"快态 provider"解析（`Func<ITranslationProvider>`，据 `FastEngine`/`FastModel`）；`Phase1Probe` 注入 `TieredTranslator`。
- 托盘（`TrayService`）：新增可勾选项 **"两级翻译（快译临时→精译定稿）"** → 切 `TranslationSettings.TwoLevel`，切换后 `OnTranslationStateChanged`（复位变化检测，让静止画面按新模式立即重译）。

> Core 命名空间沿用 `ScrTranslator.Core.Translation`；不动 Phase 1 既有类型。

## 3. 接口与数据流

### 3.1 ITranslationProvider（加 ctx）
```csharp
public interface ITranslationProvider
{
    string Name { get; }
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct);
}
```
- provider 内：`var sys = PromptBuilder.BuildSystemPrompt(ctx);` 其余（`BatchProtocol.BuildUser`/`Parse`、行数对齐、失败回原文）不变。
- 空输入仍直接返回空数组。

### 3.2 TieredTranslator（回调式编排）
```csharp
public sealed class TieredTranslator
{
    public TieredTranslator(
        Glossary glossary,
        TranslationCache cache,
        Func<ITranslationProvider> fastProvider,     // 快态
        Func<ITranslationProvider> qualityProvider,  // 质量态/单级
        Func<TranslationSettings> settings);

    // onStage 会被调用 1 次(单级/全部命中)或最多 2 次(两级)。
    // 每次回调携带“当前全量快照”(长度 == koreanLines.Count，逐行 index 对应)。
    // 回调可能在后台线程触发；调用方负责 marshal 到 UI 线程。
    // 返回 TranslateOutcome(见下)，供调用方决定是否提交变化检测。
    public Task<TranslateOutcome> TranslateAsync(
        IReadOnlyList<string> koreanLines,
        TranslationContext ctx,
        Action<IReadOnlyList<LineTranslation>> onStage,
        CancellationToken ct);
}
```

**编排算法**：
1. 逐行判定来源：`Glossary.TryGet` → `Direct`（Final 文本）；否则 `cache.TryGet` → `Direct`；否则记为 miss。
2. **组装基线快照** `snap[]`：命中行填其中文+`Direct`；miss 行暂填**原文韩文**+（两级时 `Provisional`占位 / 单级时不先发）。
**"真实译文"判定（贯穿全文，关键）**：provider **不靠抛异常**表示批量失败——`BatchProtocol.Parse(...) ?? lines` 在解析失败(缺/重/行数不符)时**原样返回传入的 `missText` 列表引用**；本编排器调用 provider 的 try/catch 在异常/超时时也把该行置回 `missText` 元素。因此对第 j 个 miss 行，**"真实译文" ⟺ `!ReferenceEquals(zh[j], missText[j])`**。凡引用相等即视为该行降级（无论异常还是静默解析失败），**一律不入缓存**。此判定与 Phase 2 单级(`TranslationService.cs:28-29`)一致。

3. **单级**（`settings.TwoLevel == false`）：
   - 若有 miss：`quality = qualityProvider(); zh = await quality.TranslateAsync(missText, ctx, ct)`（异常/超时 → catch → `zh = missText` 降级）。
   - 把 miss 行填 `zh`、Stage=`Final`（降级回原文的行 Stage 也记 `Final`，视觉上不再"临时"）。
   - 逐行：**真实译文**（`!ReferenceEquals(zh[j], missText[j])`）→ 写 `cache`；否则不写且该行计入 `AllResolved=false`。
   - **回调一次**（全量快照）。
4. **两级**（`TwoLevel == true`）且有 miss：
   - **快态**：`fast = fastProvider(); fastZh = await fast.TranslateAsync(missText, ctx, ct)`（异常/超时 → `fastZh = missText`）。把 miss 行填 `fastZh`、Stage=`Provisional`（`Direct` 命中行保持 Final 样式）。**回调第 1 次**（临时态快照）。快态结果**一律不入缓存**（临时、可能不准）。
     - 若某行快态非真实译文（引用相等原文），该行临时态即显示韩文（Provisional 样式）；仍照常进质量态。
   - **质量态**：`quality = qualityProvider(); qualZh = await quality.TranslateAsync(missText, ctx, ct)`。逐 miss 行 j 按下述**统一规则**定最终值与缓存（Stage 一律 `Final`）：
     - **质量态真实**（`!ReferenceEquals(qualZh[j], missText[j])`）→ `final=qualZh[j]`，**写 cache**。
     - 否则**回落快态**：快态真实（`!ReferenceEquals(fastZh[j], missText[j])`）→ `final=fastZh[j]`（提升为 Final 样式），**写 cache**（有译文好过反复请求；记"质量态该行降级、用快态兜底"日志）。
     - 否则（质量、快态均非真实）→ `final=missText[j]`（原文韩文，Final 样式），**不写 cache**，该行计入 `AllResolved=false`。
   - **回调第 2 次**（最终态快照）。
     > 注：**"质量态成功"绝不等于"未抛异常"**——静默批量降级(返回原 list)必须靠引用相等识别，否则会把原文韩文当最终译文缓存并误判 `AllResolved=true`，致该内容永不重译（违反验收 5）。
5. **全部命中**（无 miss）：直接回调一次全量 `Direct` 快照（单/两级同），`AllResolved=true`。

**返回值语义**：`TranslateAsync` 的 `Task` 完成 == 编排结束（最后一次回调已发出）。调用方据"最后一次快照是否含 miss 行降级回原文（即出现原文韩文 & 未缓存）"决定是否提交变化检测（见 §4）。为此 `TieredTranslator` 额外通过 `Task<TranslateOutcome>` 返回一个轻量结果：
```csharp
public sealed record TranslateOutcome(bool AllResolved);
// AllResolved=false 当且仅当存在“最终仍是原文韩文且未入缓存”的行(总失败)。
```

### 3.3 PromptBuilder
```csharp
public static class PromptBuilder
{
    // 基础协议(逐行[i]、行数对应、ID/英数原样)复用 BatchProtocol.SystemPrompt 措辞，
    // 追加：场景说明 + 术语表提示(“遇到下列术语请统一译法：ko=zh; ...”，按上界截断)。
    public static string BuildSystemPrompt(TranslationContext ctx);
    public const int GlossaryHintMaxChars = 1200;  // 术语提示上界，超出截断并省略
}
```
- `GlossaryHint` 由 **App 侧**从 `Glossary.Entries` 构造（`ko=zh` 用 `; ` 连接，截到 `GlossaryHintMaxChars`），放进 `TranslationContext` 传入。截断时结尾加 `…`，并可日志记"术语表超上界已截断 N 条"。**`PromptBuilder` 视传入 `GlossaryHint` 为已定界字符串、不再二次截断**（截断责任单一在 App 侧，避免双实现）；`GlossaryHintMaxChars` 常量放 `PromptBuilder` 仅作共享上界来源。
- Scene → 人类可读中文（如 `custom_games_listing` → "自定义游戏房间列表"）。未知 scene 用原样字符串。

## 4. Phase1Probe.OnFrame 新流程（替换 Phase 2 单次上屏）

不变：取帧节流(400ms)、单飞(`_busy`)、窗口矩形有效性检查、逐 ROI 裁剪 OCR、`metas`(位置)收集、`joined` 变化检测比较、`ScreenMapper` 映射。

变化：
1. OCR 得 `koLines` + `metas`（与 Phase 2 同）。
2. `joined == _lastJoined` → 跳过（同 Phase 2）。
3. 构造 `ctx = new TranslationContext("custom_games_listing", _glossaryHint)`（`_glossaryHint` 启动时从 Glossary 预构造一次，缓存到字段）。
4. 定义本地 `Render(IReadOnlyList<LineTranslation> snap)`：把 snap 按 `Index` zip `metas` → `OverlayItem(text, x, y, Provisional: stage==Provisional)` 列表 → `_overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(items))`。
5. `var outcome = await _tiered.TranslateAsync(koLines, ctx, Render, cts.Token);`（`cts` = 每请求超时 `TimeoutSeconds`，同 Phase 2）。
   - 注：两级会调用 `Render` 两次（临时→最终），单级一次。
6. **变化检测提交**：仅当 `outcome.AllResolved`（无总失败行）才 `_lastJoined = joined`；否则不提交（下一帧同内容重试，避免"总失败却被判未变而永不重译"）——与 Phase 2 careful 语义一致。
7. 日志：每行记 `原文 | 最终译文 | 阶段 | 引擎 | 耗时`；两级额外记快态耗时/质量态耗时。
8. `Stop()`/异常/`finally`：同 Phase 2（`_lastJoined=""` 复位、Dispose 帧、复位 `_busy`）。

**回调线程/顺序**：`TieredTranslator` 顺序 await 快态再质量态，故两次回调按序发生；`Render` 内 `InvokeAsync` 保持提交顺序，最终态覆盖临时态。单飞保证同一时刻只有一帧在跑，无跨帧竞争。

## 5. Overlay 视觉区分（临时态 vs 终态）

- `OverlayItem(string Text, double ScreenLeft, double ScreenTop, bool Provisional = false)`。
- `ShowLines`：
  - `Provisional == false`（Final/Direct）：现有样式——白、粗体、黑色阴影。
  - `Provisional == true`：**半透明 + 略暗**（如 `Foreground = 浅灰(#DDDDDD)`、`Opacity = 0.65`、可选 `FontStyle = Italic`），保留阴影以便暗底可读。
- 目的：用户一眼能分辨"这条还在精译中"。最终态回调重绘时整屏清空重排（`RootCanvas.Children.Clear()`，同 Phase 2），临时态自然被最终态替换。
- **不做**逐字淡入/动画（YAGNI）。

## 6. 错误处理与降级（在 Phase 2 基础上补两级分支）

> "失败/非真实"在下表统一指**异常、超时、或静默批量降级（provider 返回原 list，引用相等原文）**——三者按 §3.2"真实译文"判定一视同仁。

| 场景 | 行为 |
|---|---|
| 快态该行非真实(异常/超时/静默降级) | 临时态显示原文韩文(Provisional 样式)，继续走质量态；快态一律不入缓存 |
| 质量态该行非真实、快态真实 | 快态结果提升为 Final 样式 + 写缓存 + 记"质量态降级、快态兜底"日志 |
| 快态与质量态该行均非真实 | 最终显示原文韩文(Final 样式)，不写缓存，`AllResolved=false`(不提交变化检测,下帧重试) |
| 质量引擎=ChatGPT 且未登录 | `qualityProvider().TranslateAsync` 抛"未登录" → 该行走"质量态非真实"分支；托盘可提示登录(沿用 Phase 2) |
| 快态引擎未配置(如 DashScope 无 key) | 快态抛错 → 走"快态非真实"分支（临时态显示韩文） |
| 单级(TwoLevel=false) | 完全等同 Phase 2：一次质量态请求，失败回原文 |
| 术语表/缓存命中 | 不走网络，`Direct` Final 直出（两级也无临时态闪烁） |

超时：快态、质量态各自受同一 `TimeoutSeconds`（默认 10s）的 `CancellationToken` 约束（两级最坏 ~2×TimeoutSeconds，但实际快态远快；可接受，Phase 5 再调）。

## 7. 测试策略

**假 provider 约定**：假 `ITranslationProvider` 必须能模拟三种返回——(a) 真实译文(新列表)、(b) 抛异常/超时、(c) **静默降级**(把传入的 `missText` 列表**原引用**返回，复刻 `Parse(...) ?? lines`)。(c) 是防回归的关键，不能只用 (a)/(b)。

**Core 单测（无网络，TDD）**：
- `TieredTranslator` 单级：术语>缓存>质量 provider 优先级；只请求未命中行；provider 调用次数；行数对应；provider 抛错→降级回原文且 `AllResolved=false`；**静默降级(c)→同样不入缓存且 `AllResolved=false`**；真实译文入缓存；回调恰好 1 次。
- `TieredTranslator` 两级：回调恰好 2 次(有 miss 时)；第 1 次含 `Provisional`(miss)+`Direct`(命中)、第 2 次全 `Final`；快态结果**不入缓存**、质量态真实译文入缓存；快态非真实→临时态原文；质量态非真实(异常)但快态真实→快态提升 Final+入缓存+`AllResolved=true`；**质量态静默降级(c)但快态真实→同样回落快态+入缓存+`AllResolved=true`（严禁把原文当译文缓存/严禁 `AllResolved` 误判 true）**；快&质量双非真实→原文 Final+不入缓存+`AllResolved=false`；全命中→回调 1 次全 `Direct`+`AllResolved=true`。
- `PromptBuilder`：输出含场景中文说明；含术语提示；空/ null `GlossaryHint` 不崩、不加术语段；**对已超界的 hint 不再二次截断**(截断责任在 App 侧，见 §3.3)。
- `Glossary.Entries`：枚举去重后条目；空术语表返回空集合。
- provider 携带 ctx：用假 `HttpMessageHandler` 断言 **DashScope** 请求 body 的 system 段包含 `PromptBuilder` 术语提示片段（证明 ctx 已注入）；`ChatGptSubscriptionProvider` 同样改用 `PromptBuilder.BuildSystemPrompt(ctx)`，用假 `CodexResponsesClient`/handler 断言其 instructions/system 段同样包含该片段（两 provider 对称覆盖）。
- `TranslationSettings` 默认值：`TwoLevel==false`、`FastEngine==DashScope`、`FastModel=="qwen-flash"`（回归保护，防误改默认导致意外双请求）。

**手动验收（无法自动化，用户实机，暂缓）**：真实两 provider 往返、临时→最终原地替换观感、托盘开关两级、术语一致性。

## 8. YAGNI / 明确不做（留后续）

最近上下文(recent context)注入、分词/模糊术语匹配、逐字/淡入动画、独立节流器/去抖器、快态结果入缓存、磁盘持久化缓存、聊天 ROI、玩家 ID 开关/F7、完整配置窗口（快/质量引擎的图形化选择留 Phase 4，本 Phase 用 `TranslationSettings` 默认值 + 托盘布尔开关）。

## 9. 交付物清单

**Core**：`TranslationStage`、`LineTranslation`、`TranslationContext`、`PromptBuilder`、`TieredTranslator`、`TranslateOutcome`；`ITranslationProvider`(+ctx)、`DashScopeProvider`/`ChatGptSubscriptionProvider`(用 PromptBuilder)、`Glossary.Entries`、`TranslationSettings`(+三字段) 的修改；删除 `TranslationService`。对应单测。
**App**：`OverlayWindow`(OverlayItem+Provisional、ShowLines 样式)、`Phase1Probe`(回调流程 + glossaryHint 字段)、`TrayService`("两级翻译"勾选)、`App.xaml.cs`(DI 换 TieredTranslator + 快态 provider 解析 + glossaryHint 注入)。
**文档回写（收尾）**：`ROADMAP.md` Phase 3 勾项、`TECH_DESIGN.md` §3.5（标注两级已落地）。
