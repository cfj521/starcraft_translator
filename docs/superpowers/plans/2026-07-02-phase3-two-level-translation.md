# Phase 3 两级翻译编排 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Phase 2 的单级翻译升级为两级编排（快态临时→质量态原地替换），并把术语表注入 system prompt，控费防刷屏。

**Architecture:** Core 新增回调式编排器 `TieredTranslator`（取代 `TranslationService`）：术语/缓存命中直出（Direct），未命中行两级翻译——快态 provider 出临时态（回调第 1 次，不入缓存），质量态 provider 出最终态并原地替换（回调第 2 次，入缓存）。`ITranslationProvider` 增 `TranslationContext` 参数，两 provider 经共享 `PromptBuilder` 注入场景+术语。App 侧 `Phase1Probe` 改订阅分级回调按位置重绘，`OverlayWindow` 用样式区分临时/最终态，托盘加"两级翻译"开关。两级默认关闭（opt-in）。

**Tech Stack:** .NET 10 + WPF，xUnit，裸 HttpClient。解决方案 `ScrTranslator.slnx`，无参 `dotnet build`/`dotnet test` 自动识别。

## Global Constraints

- **必须在 Windows 端编译运行**；`dotnet build`/`dotnet test`/`dotnet run` 已在 PATH（dotnet 10.0.301）。
- TFM `net10.0-windows10.0.19041.0`；Core 无 UI/WinRT 依赖，可单测。
- **引用相等 = 降级**：provider 批量失败**不抛异常**，而是 `BatchProtocol.Parse(...) ?? lines` 原样返回传入列表引用；编排器的 try/catch 在异常/超时时也回落该输入列表。故"真实译文 ⟺ `!ReferenceEquals(zh[j], missText[j])`"，凡引用相等一律**不入缓存**。
- **缓存存最终态**：快态结果**永不入缓存**；质量态真实译文 / 质量降级时回落的快态真实译文才入缓存。
- **两级默认关闭**：`TranslationSettings.TwoLevel` 默认 `false`；关闭时行为须与 Phase 2 逐字节一致（一次质量态请求、一次上屏、无 Provisional 样式）。
- **玩家 ID 不翻**（沿用 Phase 2，本 Phase 不涉及 ID 列）。
- Git commit 结尾必须带：`Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。
- 跳过实机 e2e（用户指示暂缓）；验收以 `dotnet build` 0 错误 + `dotnet test` 全绿为准。

---

## File Structure

**Core（`src/ScrTranslator.Core/Translation/`）**
- `TranslationContext.cs`（新）— 场景 + 术语提示 + 上下文占位。
- `PromptBuilder.cs`（新）— 组装 system prompt（基础协议 + 场景 + 术语提示）+ 构造受界术语提示串。
- `TieredTranslator.cs`（新）— 回调式两级/单级编排器 + `TranslationStage`/`LineTranslation`/`TranslateOutcome`。
- `ITranslationProvider.cs`（改）— `TranslateAsync` 增 `TranslationContext ctx`。
- `Providers/DashScopeProvider.cs`、`Providers/ChatGptSubscriptionProvider.cs`（改）— 用 `PromptBuilder.BuildSystemPrompt(ctx)`。
- `Glossary.cs`（改）— 增 `Entries`。
- `TranslationSettings.cs`（改）— 增 `TwoLevel`/`FastEngine`/`FastModel`。
- `TranslationService.cs`（Task 3 临时改，Task 6 删除）。

**App（`src/ScrTranslator.App/`）**
- `OverlayWindow.xaml.cs`（改）— `OverlayItem` 增 `Provisional`；`ShowLines` 样式区分。
- `Phase1Probe.cs`（改）— 回调流程 + `_glossaryHint` + 注入 `TieredTranslator`/`Glossary`。
- `Services/TrayService.cs`（改）— "两级翻译"勾选。
- `App.xaml.cs`（改）— DI 换 `TieredTranslator` + 快态 provider 解析。

**Tests（`tests/ScrTranslator.Core.Tests/Translation/`）**
- `GlossaryTests.cs`、`TranslationSettingsTests.cs`、`ProvidersTests.cs`、`TranslationServiceTests.cs`（改）。
- `PromptBuilderTests.cs`、`TieredTranslatorTests.cs`（新）。
- Task 6 删除 `TranslationServiceTests.cs`。

---

## Task 1: Glossary.Entries 枚举

**Files:**
- Modify: `src/ScrTranslator.Core/Translation/Glossary.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/GlossaryTests.cs`

**Interfaces:**
- Produces: `Glossary.Entries` → `IReadOnlyCollection<KeyValuePair<string,string>>`（去重后的 ko→zh 条目；空术语表返回空集合）。

- [ ] **Step 1: Write the failing test**

在 `GlossaryTests.cs` 末尾（`}` 前）加：

```csharp
    [Fact] public void Entries_ExposesDedupedPairs()
    {
        var g = Load();
        Assert.Contains(g.Entries, kv => kv.Key == "저그" && kv.Value == "虫族");
        Assert.Equal(g.Count, g.Entries.Count);
    }
    [Fact] public void Entries_EmptyGlossary_IsEmpty()
    {
        Assert.Empty(Glossary.LoadFromMarkdown("nope.md").Entries);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GlossaryTests.Entries"`
Expected: 编译失败（`Glossary` 无 `Entries`）。

- [ ] **Step 3: Write minimal implementation**

在 `Glossary.cs` 的 `public int Count => _map.Count;` 下一行加：

```csharp
    public IReadOnlyCollection<KeyValuePair<string, string>> Entries => _map;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~GlossaryTests"`
Expected: PASS（含原有 Glossary 测试）。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Translation/Glossary.cs tests/ScrTranslator.Core.Tests/Translation/GlossaryTests.cs
git commit -m "feat(core): Glossary.Entries 暴露去重术语条目(供 prompt 注入)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: TranslationContext + PromptBuilder

**Files:**
- Create: `src/ScrTranslator.Core/Translation/TranslationContext.cs`
- Create: `src/ScrTranslator.Core/Translation/PromptBuilder.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/PromptBuilderTests.cs`（新）

**Interfaces:**
- Produces:
  - `record TranslationContext(string Scene, string? GlossaryHint, IReadOnlyList<string>? RecentContext = null)`
  - `PromptBuilder.BuildSystemPrompt(TranslationContext ctx) → string`
  - `PromptBuilder.BuildGlossaryHint(IEnumerable<KeyValuePair<string,string>> entries, int maxChars = 1200) → string`
  - `const int PromptBuilder.GlossaryHintMaxChars = 1200`
- Consumes: `ScrTranslator.Core.Translation.Providers.BatchProtocol.SystemPrompt`（基础协议措辞）。

- [ ] **Step 1: Write the failing test**

创建 `tests/ScrTranslator.Core.Tests/Translation/PromptBuilderTests.cs`：

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class PromptBuilderTests
{
    [Fact]
    public void SystemPrompt_IncludesSceneAndGlossaryHint()
    {
        var ctx = new TranslationContext("custom_games_listing", "저그=虫族; 러시=速攻");
        var sys = PromptBuilder.BuildSystemPrompt(ctx);
        Assert.Contains("自定义游戏房间列表", sys);
        Assert.Contains("저그=虫族", sys);
    }

    [Fact]
    public void SystemPrompt_NullHint_NoGlossarySection_NoCrash()
    {
        var sys = PromptBuilder.BuildSystemPrompt(new TranslationContext("custom_games_listing", null));
        Assert.Contains("自定义游戏房间列表", sys);
        Assert.DoesNotContain("统一采用", sys);
    }

    [Fact]
    public void SystemPrompt_UnknownScene_UsesRawLabel()
    {
        var sys = PromptBuilder.BuildSystemPrompt(new TranslationContext("chat_room", null));
        Assert.Contains("chat_room", sys);
    }

    [Fact]
    public void GlossaryHint_JoinsPairs_WithSemicolon()
    {
        var hint = PromptBuilder.BuildGlossaryHint(new[]
        {
            new KeyValuePair<string,string>("저그","虫族"),
            new KeyValuePair<string,string>("러시","速攻"),
        });
        Assert.Equal("저그=虫族; 러시=速攻", hint);
    }

    [Fact]
    public void GlossaryHint_ExceedingMax_TruncatesWithEllipsis()
    {
        var many = Enumerable.Range(0, 500)
            .Select(i => new KeyValuePair<string,string>("한국어단어" + i, "中文" + i));
        var hint = PromptBuilder.BuildGlossaryHint(many, maxChars: 100);
        Assert.True(hint.Length <= 101, $"len={hint.Length}");
        Assert.EndsWith("…", hint);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PromptBuilderTests"`
Expected: 编译失败（`TranslationContext`/`PromptBuilder` 不存在）。

- [ ] **Step 3: Write minimal implementation**

创建 `src/ScrTranslator.Core/Translation/TranslationContext.cs`：

```csharp
namespace ScrTranslator.Core.Translation;

/// <summary>翻译上下文：场景 + 术语提示（已定界）+ 最近上下文（暂占位，恒 null）。</summary>
public sealed record TranslationContext(
    string Scene,
    string? GlossaryHint,
    IReadOnlyList<string>? RecentContext = null);
```

创建 `src/ScrTranslator.Core/Translation/PromptBuilder.cs`：

```csharp
using System.Text;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.Core.Translation;

/// <summary>组装翻译 system prompt：基础批量协议 + 场景 + 术语提示。两 provider 共用。</summary>
public static class PromptBuilder
{
    public const int GlossaryHintMaxChars = 1200;

    public static string BuildSystemPrompt(TranslationContext ctx)
    {
        var sb = new StringBuilder(BatchProtocol.SystemPrompt);
        sb.Append(" 当前场景：").Append(SceneText(ctx.Scene)).Append('。');
        if (!string.IsNullOrWhiteSpace(ctx.GlossaryHint))
            sb.Append(" 遇到下列术语请统一采用给定译法：").Append(ctx.GlossaryHint);
        return sb.ToString();
    }

    /// <summary>把术语条目拼成 "ko=zh; ko=zh"，超过 maxChars 截断并以 … 结尾。截断责任单一在此。</summary>
    public static string BuildGlossaryHint(
        IEnumerable<KeyValuePair<string, string>> entries, int maxChars = GlossaryHintMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var kv in entries)
        {
            var piece = (sb.Length == 0 ? "" : "; ") + kv.Key + "=" + kv.Value;
            if (sb.Length + piece.Length > maxChars) { sb.Append('…'); break; }
            sb.Append(piece);
        }
        return sb.ToString();
    }

    private static string SceneText(string scene) => scene switch
    {
        "custom_games_listing" => "自定义游戏房间列表",
        _ => scene,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PromptBuilderTests"`
Expected: PASS（5/5）。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Translation/TranslationContext.cs src/ScrTranslator.Core/Translation/PromptBuilder.cs tests/ScrTranslator.Core.Tests/Translation/PromptBuilderTests.cs
git commit -m "feat(core): PromptBuilder+TranslationContext(场景+术语注入 system prompt)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: ITranslationProvider 增 ctx，两 provider 走 PromptBuilder

**Files:**
- Modify: `src/ScrTranslator.Core/Translation/ITranslationProvider.cs`
- Modify: `src/ScrTranslator.Core/Translation/Providers/DashScopeProvider.cs`
- Modify: `src/ScrTranslator.Core/Translation/Providers/ChatGptSubscriptionProvider.cs`
- Modify: `src/ScrTranslator.Core/Translation/TranslationService.cs`（临时兼容，Task 6 删）
- Test: `tests/ScrTranslator.Core.Tests/Translation/ProvidersTests.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationServiceTests.cs`（更新 fake 签名）

**Interfaces:**
- Produces: `ITranslationProvider.TranslateAsync(IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct)`
- Consumes: `PromptBuilder.BuildSystemPrompt(ctx)`（Task 2）。

- [ ] **Step 1: Write the failing test**

改 `ProvidersTests.cs`：文件顶部类内加一个共享 ctx 字段，并把所有 `TranslateAsync(x, CancellationToken.None)` 改成带 ctx；再加一条断言 ctx 注入。完整替换 `ProvidersTests` 类体为：

```csharp
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class ProvidersTests
{
    private static readonly TranslationContext Ctx =
        new("custom_games_listing", "저그=虫族");

    [Fact]
    public async Task DashScope_TranslatesBatch()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\\n[2] 乙\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var p = new DashScopeProvider(client, () => "qwen-flash");
        var r = await p.TranslateAsync(new[]{"가","나"}, Ctx, CancellationToken.None);
        Assert.Equal(new[]{"甲","乙"}, r);
        Assert.Equal("dashscope", p.Name);
    }

    [Fact]
    public async Task DashScope_InjectsGlossaryHintIntoSystemPrompt()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        await new DashScopeProvider(client, () => "m").TranslateAsync(new[]{"가"}, Ctx, CancellationToken.None);
        Assert.Contains("저그=虫族", stub.LastBody);       // 术语提示进了请求体
        Assert.Contains("自定义游戏房间列表", stub.LastBody); // 场景也进了
    }

    [Fact]
    public async Task DashScope_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var input = new[]{"가","나"};
        var r = await new DashScopeProvider(client, () => "m").TranslateAsync(input, Ctx, CancellationToken.None);
        Assert.Same(input, r); // 降级：原样返回传入引用
    }

    [Fact]
    public async Task Empty_ReturnsEmpty()
    {
        var stub = new StubHttpMessageHandler("{}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var r = await new DashScopeProvider(client, () => "m").TranslateAsync(Array.Empty<string>(), Ctx, CancellationToken.None);
        Assert.Empty(r);
    }

    private sealed class FakeTokens : ITokenSource
    {
        public Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct)
            => Task.FromResult(("TOK", "ACC"));
    }

    [Fact]
    public async Task ChatGpt_TranslatesBatch_ViaTokenSourceAndSse()
    {
        var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"[1] 甲\\n[2] 乙\"}\n\n" +
                  "data: [DONE]\n\n";
        var stub = new StubHttpMessageHandler(sse);
        var client = new CodexResponsesClient(new HttpClient(stub));
        var p = new ChatGptSubscriptionProvider(client, new FakeTokens(), () => "gpt-5");
        var r = await p.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);
        Assert.Equal(new[] { "甲", "乙" }, r);
        Assert.Equal("chatgpt", p.Name);
        Assert.Equal("Bearer TOK", stub.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ChatGpt_InjectsGlossaryHintIntoRequest()
    {
        var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"[1] 甲\"}\n\ndata: [DONE]\n\n";
        var stub = new StubHttpMessageHandler(sse);
        var client = new CodexResponsesClient(new HttpClient(stub));
        var p = new ChatGptSubscriptionProvider(client, new FakeTokens(), () => "gpt-5");
        await p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);
        Assert.Contains("저그=虫族", stub.LastBody);
    }
}
```

（注：`DashScope_WrongCount` 现改用 `Assert.Same` 明确验证"返回传入引用"——这是编排器识别降级的契约。）

改 `TranslationServiceTests.cs` 的 `FakeProvider.TranslateAsync` 签名以保持编译：

```csharp
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
```

并把两处 `svc.TranslateAsync(new[]{...}, CancellationToken.None)` 保持不变（`TranslationService` 签名不变，仅内部实现改）。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ProvidersTests"`
Expected: 编译失败（`ITranslationProvider.TranslateAsync` 仍是旧签名）。

- [ ] **Step 3: Write minimal implementation**

`ITranslationProvider.cs` 改签名：

```csharp
namespace ScrTranslator.Core.Translation;
public interface ITranslationProvider
{
    string Name { get; }
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx, CancellationToken ct);
}
```

`DashScopeProvider.cs` 改 `TranslateAsync`：

```csharp
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx), BatchProtocol.BuildUser(lines), ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
```

`ChatGptSubscriptionProvider.cs` 改 `TranslateAsync`：

```csharp
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (token, account) = await _tokens.GetAsync(ct);
        var reply = await _client.CompleteAsync(_model(), PromptBuilder.BuildSystemPrompt(ctx), BatchProtocol.BuildUser(lines), token, account, ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
```

`TranslationService.cs` 内 `_active().TranslateAsync(missText, ct)` 改为传默认 ctx（临时兼容，Task 6 整体删除本类）：

```csharp
            try { translated = await _active().TranslateAsync(missText, new TranslationContext("custom_games_listing", null), ct); }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ProvidersTests|FullyQualifiedName~TranslationServiceTests"`
Expected: PASS。再 `dotnet build` 确认整解决方案 0 错误（App 经 `TranslationService` 间接用 provider，签名未外泄）。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ITranslationProvider 增 TranslationContext,两 provider 经 PromptBuilder 注入术语/场景

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: TranslationSettings 增两级/快态字段

**Files:**
- Modify: `src/ScrTranslator.Core/Translation/TranslationSettings.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationSettingsTests.cs`

**Interfaces:**
- Produces: `TranslationSettings.TwoLevel`(bool=false)、`.FastEngine`(TranslationEngine=DashScope)、`.FastModel`(string="qwen-flash")。

- [ ] **Step 1: Write the failing test**

在 `TranslationSettingsTests.cs` 的 `Default_HasSaneValues` 后加：

```csharp
    [Fact]
    public void Default_TwoLevelDisabled_FastIsDashScopeFlash()
    {
        var s = TranslationSettings.Default;
        Assert.False(s.TwoLevel);                                   // 默认关闭，防意外双请求
        Assert.Equal(TranslationEngine.DashScope, s.FastEngine);
        Assert.Equal("qwen-flash", s.FastModel);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~TranslationSettingsTests"`
Expected: 编译失败（无 `TwoLevel`/`FastEngine`/`FastModel`）。

- [ ] **Step 3: Write minimal implementation**

`TranslationSettings.cs` 在 `TimeoutSeconds` 行后加三个属性：

```csharp
    public bool TwoLevel { get; init; } = false;
    public TranslationEngine FastEngine { get; init; } = TranslationEngine.DashScope;
    public string FastModel { get; init; } = "qwen-flash";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~TranslationSettingsTests"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Translation/TranslationSettings.cs tests/ScrTranslator.Core.Tests/Translation/TranslationSettingsTests.cs
git commit -m "feat(core): TranslationSettings 增 TwoLevel/FastEngine/FastModel(默认关闭两级)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: TieredTranslator 编排器（单级 + 两级）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/TieredTranslator.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TieredTranslatorTests.cs`（新）

**Interfaces:**
- Consumes: `Glossary`、`TranslationCache`、`ITranslationProvider`(带 ctx)、`TranslationSettings`、`TranslationContext`。
- Produces:
  - `enum TranslationStage { Direct, Provisional, Final }`
  - `record LineTranslation(int Index, string Text, TranslationStage Stage)`
  - `record TranslateOutcome(bool AllResolved)`
  - `class TieredTranslator(Glossary, TranslationCache, Func<ITranslationProvider> fast, Func<ITranslationProvider> quality, Func<TranslationSettings> settings)`
  - `Task<TranslateOutcome> TieredTranslator.TranslateAsync(IReadOnlyList<string> koreanLines, TranslationContext ctx, Action<IReadOnlyList<LineTranslation>> onStage, CancellationToken ct)`

- [ ] **Step 1: Write the failing test**

创建 `tests/ScrTranslator.Core.Tests/Translation/TieredTranslatorTests.cs`：

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;

public class TieredTranslatorTests
{
    private static readonly TranslationContext Ctx = new("custom_games_listing", null);

    private sealed class FakeProvider : ITranslationProvider
    {
        public enum Mode { Real, Throw, Degrade } // Degrade=原样返回传入引用(静默降级)
        public Mode Behavior = Mode.Real;
        public string Prefix = "T:";
        public int Calls;
        public string Name => "fake";
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
        {
            Calls++;
            if (Behavior == Mode.Throw) throw new InvalidOperationException();
            if (Behavior == Mode.Degrade) return Task.FromResult(lines); // 引用相等
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => Prefix + l).ToArray());
        }
    }

    private static (TieredTranslator t, List<IReadOnlyList<LineTranslation>> snaps, TranslationCache cache)
        Make(FakeProvider fast, FakeProvider quality, bool twoLevel, Glossary? glo = null, TranslationCache? cache = null)
    {
        glo ??= Glossary.FromEntries(System.Array.Empty<(string,string)>());
        cache ??= new TranslationCache();
        var settings = TranslationSettings.Default with { TwoLevel = twoLevel };
        var t = new TieredTranslator(glo, cache, () => fast, () => quality, () => settings);
        return (t, new List<IReadOnlyList<LineTranslation>>(), cache);
    }

    // ---- 单级 ----
    [Fact]
    public async Task Single_GlossaryCache_ShortCircuit_OneCallback()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var cache = new TranslationCache(); cache.Set("나", "缓存中");
        var q = new FakeProvider();
        var (t, snaps, _) = Make(new FakeProvider(), q, twoLevel: false, glo, cache);
        var outcome = await t.TranslateAsync(new[] { "저그", "나", "다" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal(new[] { "虫族", "缓存中", "T:다" }, snaps[0].Select(x => x.Text));
        Assert.Equal(1, q.Calls);
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Single_ProviderThrows_FallsBackOriginal_NotResolved_NotCached()
    {
        var (t, snaps, cache) = Make(new FakeProvider(), new FakeProvider { Behavior = FakeProvider.Mode.Throw }, twoLevel: false);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal("가", snaps[0][0].Text);
        Assert.False(outcome.AllResolved);
        Assert.False(cache.TryGet("가", out _));
    }

    [Fact]
    public async Task Single_ProviderSilentDegrade_NotCached_NotResolved()
    {
        var (t, snaps, cache) = Make(new FakeProvider(), new FakeProvider { Behavior = FakeProvider.Mode.Degrade }, twoLevel: false);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("가", snaps[0][0].Text);
        Assert.False(outcome.AllResolved);
        Assert.False(cache.TryGet("가", out _));
    }

    // ---- 两级 ----
    [Fact]
    public async Task Two_EmitsProvisionalThenFinal_CachesQualityNotFast()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Prefix = "Q:" };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal(2, snaps.Count);
        Assert.Equal(TranslationStage.Provisional, snaps[0][0].Stage);
        Assert.Equal("F:가", snaps[0][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.Equal("Q:가", snaps[1][0].Text);
        Assert.True(cache.TryGet("가", out var c) && c == "Q:가"); // 缓存质量态
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_HitAndMiss_FirstSnapshotMixesDirectAndProvisional()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var (t, snaps, _) = Make(new FakeProvider { Prefix = "F:" }, new FakeProvider { Prefix = "Q:" }, twoLevel: true, glo);
        await t.TranslateAsync(new[] { "저그", "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal(2, snaps.Count);
        Assert.Equal(TranslationStage.Direct, snaps[0][0].Stage);        // 术语命中即 Direct
        Assert.Equal(TranslationStage.Provisional, snaps[0][1].Stage);   // miss 为临时
        Assert.All(snaps[1], x => Assert.Equal(TranslationStage.Final, x.Stage));
    }

    [Fact]
    public async Task Two_QualityThrows_FastReal_PromotesFast_Cached_Resolved()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("F:가", snaps[1][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.True(cache.TryGet("가", out var c) && c == "F:가");
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_QualitySilentDegrade_FastReal_PromotesFast_Cached_Resolved()
    {
        var fast = new FakeProvider { Prefix = "F:" };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Degrade }; // 静默降级(不抛)
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("F:가", snaps[1][0].Text);          // 回落快态,严禁把原文当译文
        Assert.True(cache.TryGet("가", out var c) && c == "F:가");
        Assert.True(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_BothFail_OriginalFinal_NotCached_NotResolved()
    {
        var fast = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var quality = new FakeProvider { Behavior = FakeProvider.Mode.Throw };
        var (t, snaps, cache) = Make(fast, quality, twoLevel: true);
        var outcome = await t.TranslateAsync(new[] { "가" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Equal("가", snaps[1][0].Text);
        Assert.Equal(TranslationStage.Final, snaps[1][0].Stage);
        Assert.False(cache.TryGet("가", out _));
        Assert.False(outcome.AllResolved);
    }

    [Fact]
    public async Task Two_AllHit_SingleDirectCallback()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var fast = new FakeProvider(); var quality = new FakeProvider();
        var (t, snaps, _) = Make(fast, quality, twoLevel: true, glo);
        var outcome = await t.TranslateAsync(new[] { "저그" }, Ctx, s => snaps.Add(s), CancellationToken.None);
        Assert.Single(snaps);
        Assert.Equal(TranslationStage.Direct, snaps[0][0].Stage);
        Assert.Equal(0, fast.Calls); Assert.Equal(0, quality.Calls);
        Assert.True(outcome.AllResolved);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~TieredTranslatorTests"`
Expected: 编译失败（`TieredTranslator`/`LineTranslation` 等不存在）。

- [ ] **Step 3: Write minimal implementation**

创建 `src/ScrTranslator.Core/Translation/TieredTranslator.cs`：

```csharp
namespace ScrTranslator.Core.Translation;

public enum TranslationStage { Direct, Provisional, Final }
public sealed record LineTranslation(int Index, string Text, TranslationStage Stage);
public sealed record TranslateOutcome(bool AllResolved);

/// <summary>
/// 两级/单级翻译编排。术语/缓存命中直出(Direct)；未命中行——两级时先快态(Provisional,不缓存)
/// 再质量态(Final,缓存);单级时仅质量态(Final)。onStage 回调携带全量快照,调用方负责 marshal UI。
/// "引用相等 == 降级"贯穿:凡回落传入引用的行一律不缓存。
/// </summary>
public sealed class TieredTranslator
{
    private readonly Glossary _glossary;
    private readonly TranslationCache _cache;
    private readonly Func<ITranslationProvider> _fast;
    private readonly Func<ITranslationProvider> _quality;
    private readonly Func<TranslationSettings> _settings;

    public TieredTranslator(Glossary glossary, TranslationCache cache,
        Func<ITranslationProvider> fastProvider, Func<ITranslationProvider> qualityProvider,
        Func<TranslationSettings> settings)
    {
        _glossary = glossary; _cache = cache;
        _fast = fastProvider; _quality = qualityProvider; _settings = settings;
    }

    public async Task<TranslateOutcome> TranslateAsync(
        IReadOnlyList<string> koreanLines, TranslationContext ctx,
        Action<IReadOnlyList<LineTranslation>> onStage, CancellationToken ct)
    {
        int n = koreanLines.Count;
        var text = new string[n];
        var stage = new TranslationStage[n];
        var missIdx = new List<int>();
        var missText = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) { text[i] = g; stage[i] = TranslationStage.Direct; }
            else if (_cache.TryGet(ko, out var c)) { text[i] = c; stage[i] = TranslationStage.Direct; }
            else { missIdx.Add(i); missText.Add(ko); }
        }

        if (missText.Count == 0)
        {
            onStage(Snapshot(text, stage));
            return new TranslateOutcome(true);
        }

        bool twoLevel = _settings().TwoLevel;
        IReadOnlyList<string>? fastZh = null;
        if (twoLevel)
        {
            fastZh = await CallAsync(_fast(), missText, ctx, ct);
            for (int j = 0; j < missIdx.Count; j++)
            {
                text[missIdx[j]] = fastZh[j];
                stage[missIdx[j]] = TranslationStage.Provisional;
            }
            onStage(Snapshot(text, stage)); // 临时态；快态不入缓存
        }

        var qualZh = await CallAsync(_quality(), missText, ctx, ct);
        bool allResolved = true;
        for (int j = 0; j < missIdx.Count; j++)
        {
            int i = missIdx[j];
            string final; bool cacheIt;
            if (!ReferenceEquals(qualZh[j], missText[j])) { final = qualZh[j]; cacheIt = true; }
            else if (fastZh is not null && !ReferenceEquals(fastZh[j], missText[j])) { final = fastZh[j]; cacheIt = true; }
            else { final = missText[j]; cacheIt = false; allResolved = false; }
            text[i] = final; stage[i] = TranslationStage.Final;
            if (cacheIt) _cache.Set(missText[j], final);
        }
        onStage(Snapshot(text, stage));
        return new TranslateOutcome(allResolved);
    }

    private static async Task<IReadOnlyList<string>> CallAsync(
        ITranslationProvider p, IReadOnlyList<string> lines, TranslationContext ctx, CancellationToken ct)
    {
        try { return await p.TranslateAsync(lines, ctx, ct); }
        catch { return lines; } // 异常 → 回落传入引用(引用相等 == 降级)
    }

    private static IReadOnlyList<LineTranslation> Snapshot(string[] text, TranslationStage[] stage)
    {
        var list = new LineTranslation[text.Length];
        for (int i = 0; i < text.Length; i++) list[i] = new LineTranslation(i, text[i], stage[i]);
        return list;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~TieredTranslatorTests"`
Expected: PASS（10/10）。再 `dotnet test`（全量）确认无回归。

- [ ] **Step 5: Commit**

```bash
git add src/ScrTranslator.Core/Translation/TieredTranslator.cs tests/ScrTranslator.Core.Tests/Translation/TieredTranslatorTests.cs
git commit -m "feat(core): TieredTranslator 两级/单级编排(快态临时→质量态终态,引用相等守卫)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: App 接线（Overlay 样式 + Phase1Probe 回调 + DI + 托盘开关，删 TranslationService）

**Files:**
- Modify: `src/ScrTranslator.App/OverlayWindow.xaml.cs`
- Modify: `src/ScrTranslator.App/Phase1Probe.cs`
- Modify: `src/ScrTranslator.App/Services/TrayService.cs`
- Modify: `src/ScrTranslator.App/App.xaml.cs`
- Delete: `src/ScrTranslator.Core/Translation/TranslationService.cs`
- Delete: `tests/ScrTranslator.Core.Tests/Translation/TranslationServiceTests.cs`

**Interfaces:**
- Consumes: `TieredTranslator`、`LineTranslation`、`TranslationStage`、`TranslateOutcome`、`TranslationContext`、`PromptBuilder.BuildGlossaryHint`、`Glossary.Entries`、`TranslationSettings.{TwoLevel,FastEngine,FastModel}`。

> 本任务以**编译 + 全量测试绿**为验收（WPF 上屏为实机项，暂缓）。逐子步骤做完统一构建。

- [ ] **Step 1: OverlayItem 增 Provisional，ShowLines 样式区分**

`OverlayWindow.xaml.cs`：把 record 改为

```csharp
public record OverlayItem(string Text, double ScreenLeft, double ScreenTop, bool Provisional = false);
```

`ShowLines` 内构造 `TextBlock` 处，按 `Provisional` 调整前景/透明度/斜体。替换 `foreach` 体为：

```csharp
        foreach (var it in items)
        {
            var tb = new TextBlock
            {
                Text = it.Text,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = it.Provisional ? new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) : Brushes.White,
                FontStyle = it.Provisional ? FontStyles.Italic : FontStyles.Normal,
                Opacity = it.Provisional ? 0.65 : 1.0,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
            };
            Canvas.SetLeft(tb, it.ScreenLeft / dpi.DpiScaleX);
            Canvas.SetTop(tb, it.ScreenTop / dpi.DpiScaleY);
            RootCanvas.Children.Add(tb);
        }
```

- [ ] **Step 2: Phase1Probe 改回调流程 + glossaryHint**

`Phase1Probe.cs`：

改 using（增 `System.Linq` 已隐含；确保 `using ScrTranslator.Core.Translation;` 在）。

改字段与构造函数（把 `TranslationService _translator` 换成 `TieredTranslator _tiered`，新增 `Glossary` 注入与 `_glossaryHint`）：

```csharp
    private readonly TieredTranslator _tiered;
    private readonly SettingsHolder _settings;
    private readonly string _glossaryHint;
    private const string Scene = "custom_games_listing";
```
（删除原 `private readonly TranslationService _translator;`）

构造函数签名与体：

```csharp
    public Phase1Probe(CaptureService capture, IOcrEngine ocr, FileLogger log, OverlayWindow overlay,
        string roisJsonPath, TieredTranslator tiered, SettingsHolder settings, Glossary glossary)
    {
        _capture = capture; _ocr = ocr; _log = log; _overlay = overlay;
        _cfg = RoiStore.Load(roisJsonPath);
        _tiered = tiered; _settings = settings;
        _glossaryHint = PromptBuilder.BuildGlossaryHint(glossary.Entries);
        _capture.FrameArrived += OnFrame;
    }
```

替换 `OnFrame` 中"变化检测之后到上屏"的整段（从 `var engine = ...` 到 `_ = _overlay.Dispatcher.InvokeAsync(...ShowLines(items));`）为：

```csharp
            var engine = _settings.Current.Active;
            var ctx = new TranslationContext(Scene, _glossaryHint);
            int timeout = Math.Max(1, _settings.Current.TimeoutSeconds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

            void Render(IReadOnlyList<LineTranslation> snap)
            {
                var items = new List<OverlayItem>(snap.Count);
                foreach (var ln in snap)
                {
                    var (frameBounds, _) = metas[ln.Index];
                    var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
                    items.Add(new OverlayItem(ln.Text, scr.X, scr.Y, ln.Stage == TranslationStage.Provisional));
                }
                _ = _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowLines(items));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await _tiered.TranslateAsync(koLines, ctx, Render, cts.Token);
            sw.Stop();
            for (int k = 0; k < koLines.Count; k++)
                _log.Log($"[{metas[k].roiName}][{k}] \"{koLines[k]}\" engine={engine} 两级={_settings.Current.TwoLevel} {sw.ElapsedMilliseconds}ms");

            // 仅当无“总失败”行(AllResolved)才提交变化检测,否则下一帧同内容重试。
            if (outcome.AllResolved) _lastJoined = joined;
            _log.Log($"[probe] 上屏完成 AllResolved={outcome.AllResolved}");
```

（注：`frameSize`、`winRect`、`metas`、`koLines`、`joined` 均为 `OnFrame` 内已存在的局部变量；`metas` 元素为 `(RectangleF frameBounds, string roiName)`。删除原先对 `zh[k]`、`items.Add(...)`、`_lastJoined = joined;` 无条件提交的旧代码。）

- [ ] **Step 3: TrayService 加"两级翻译"开关**

`TrayService.cs`：

加字段：`private MenuItem? _twoLevelItem;`

在 `Initialize()` 里 `engineMenu` 之后、`exitItem` 之前，构造并插入勾选项：

```csharp
        _twoLevelItem = new MenuItem { Header = "两级翻译（快译临时→精译定稿）", IsCheckable = true };
        _twoLevelItem.Click += (_, _) => ToggleTwoLevel();
        RefreshTwoLevelCheck();
```

并在 `menu.Items.Add(engineMenu);` 之后加 `menu.Items.Add(_twoLevelItem);`

加两个方法（放在 `RefreshEngineChecks` 附近）：

```csharp
    private void ToggleTwoLevel()
    {
        _settings.Current = _settings.Current with { TwoLevel = !_settings.Current.TwoLevel };
        RefreshTwoLevelCheck();
        OnTranslationStateChanged?.Invoke(); // 切换后让静止画面按新模式立即重译
    }

    private void RefreshTwoLevelCheck()
    {
        if (_twoLevelItem is not null) _twoLevelItem.IsChecked = _settings.Current.TwoLevel;
    }
```

- [ ] **Step 4: App.xaml.cs — DI 换 TieredTranslator + 快态 provider，删 TranslationService 注册**

`App.xaml.cs`：把这段（Phase 2 的 TranslationService 注册）

```csharp
        builder.Services.AddSingleton(sp => new TranslationService(
            sp.GetRequiredService<Glossary>(),
            sp.GetRequiredService<TranslationCache>(),
            () => sp.GetRequiredService<SettingsHolder>().Current.Active == TranslationEngine.DashScope
                ? (ITranslationProvider)sp.GetRequiredService<DashScopeProvider>()
                : sp.GetRequiredService<ChatGptSubscriptionProvider>()));
```

替换为（质量态解析同旧 Active 逻辑；快态默认 DashScope+FastModel，用同一 `OpenAiCompatibleChatClient` 新建一个绑 FastModel 的 DashScopeProvider）：

```csharp
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsHolder>();
            var dashClient = sp.GetRequiredService<OpenAiCompatibleChatClient>();
            var chatGpt = sp.GetRequiredService<ChatGptSubscriptionProvider>();
            var dashQuality = sp.GetRequiredService<DashScopeProvider>();               // 绑 Current.DashScopeModel
            var dashFast = new DashScopeProvider(dashClient, () => settings.Current.FastModel);

            ITranslationProvider Quality() =>
                settings.Current.Active == TranslationEngine.DashScope ? dashQuality : chatGpt;
            ITranslationProvider Fast() =>
                settings.Current.FastEngine == TranslationEngine.DashScope ? dashFast : chatGpt;

            return new TieredTranslator(
                sp.GetRequiredService<Glossary>(),
                sp.GetRequiredService<TranslationCache>(),
                Fast, Quality,
                () => settings.Current);
        });
```

把 `Phase1Probe` 注册改为注入 `TieredTranslator` + `Glossary`：

```csharp
        builder.Services.AddSingleton(sp => new Phase1Probe(
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<IOcrEngine>(),
            sp.GetRequiredService<FileLogger>(),
            sp.GetRequiredService<OverlayWindow>(),
            Path.Combine(baseDir, "rois.json"),
            sp.GetRequiredService<TieredTranslator>(),
            sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<Glossary>()));
```

- [ ] **Step 5: 删除 TranslationService 及其测试**

```bash
git rm src/ScrTranslator.Core/Translation/TranslationService.cs tests/ScrTranslator.Core.Tests/Translation/TranslationServiceTests.cs
```

- [ ] **Step 6: 构建 + 全量测试**

Run: `dotnet build`
Expected: 0 错误（若报 `TranslationService` 仍被引用，搜 `grep -rn TranslationService src/` 清理残留）。

Run: `dotnet test`
Expected: 全绿（Phase 2 的 46 条中减去 TranslationService 的 2 条、加上 Task1/2/4/5 新增；无失败）。

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): 接入 TieredTranslator 两级上屏(临时态灰/终态白)+托盘两级开关;删 TranslationService

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: 文档回写 + 收尾

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `docs/TECH_DESIGN.md`

**Interfaces:** 无代码接口。

- [ ] **Step 1: ROADMAP Phase 3 勾项**

`docs/ROADMAP.md` 的 Phase 3 段，把已完成项打勾：

```markdown
- [x] 两级编排：快态（DashScope qwen-flash）临时态 → 质量态（Active 引擎）最终态原地替换（视觉区分：临时灰斜体/最终白）。默认关闭(opt-in)，托盘可开。
- [x] system prompt 注入术语表 + 场景（`PromptBuilder`）
- [x] LLM 节流/合并，防刷屏（复用变化检测+单飞+缓存存最终态；两级不加压）
- [x] Core 层单测：编排(单/两级)、prompt 组装、术语枚举、ctx 注入
```

（"临时态→最终态可见替换"为实机验收项，标注待用户 e2e。）

- [ ] **Step 2: TECH_DESIGN §3.5 标注两级已落地**

`docs/TECH_DESIGN.md` 第 5 点（`两级编排（Phase 3 目标）...当前 Phase 2 未做。`）改为：

```markdown
  5. **两级编排（Phase 3 已落地，默认关闭）**：快态 provider（DashScope qwen-flash）临时态 → 质量态 provider（Active 引擎）最终态原地替换；术语经 `PromptBuilder` 注入 system prompt。见 `TieredTranslator`。缓存存最终态；快态永不入缓存。
```

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md docs/TECH_DESIGN.md
git commit -m "docs: 回写 Phase 3 两级编排已落地(勾 ROADMAP + TECH_DESIGN §3.5)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## 最终整支审（收尾，非 Task）

7 个 Task 全绿后，按 `superpowers:requesting-code-review` 派 opus 隔离审全分支 diff（对照本 plan 与 spec）；修掉 Important 项；记录延后 Minor。跳过实机 e2e（用户暂缓）。更新记忆 `project-status.md`：Phase 3 完成、两级默认关闭、待用户 e2e。

## 验收对照（self-check 用）

| Spec 验收项 | 对应 Task |
|---|---|
| Core 单测全绿(编排/PromptBuilder/Entries/ctx 注入) | Task 1,2,3,5 |
| 两级：临时(灰)→最终(白)原地替换;命中直出无闪 | Task 5(逻辑)+Task 6(样式) |
| 关闭两级 == Phase 2 行为 | Task 4(默认 false)+Task 5(单级路径) |
| 术语注入 prompt 统一长句译法 | Task 2,3 |
| 缓存存最终态,重复零双请求 | Task 5(缓存语义) |
