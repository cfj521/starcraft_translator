# Phase 2 翻译层 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OCR 出的韩文经"术语表→缓存→活跃 LLM provider"翻成中文并原位上屏；支持 DashScope(API Key) 与 ChatGPT 订阅(Codex OAuth) 两个可切换 provider。

**Architecture:** Core 放纯逻辑与 HTTP 客户端（裸 HttpClient，可用假 handler 单测）；App 放 DPAPI 密钥存储、OAuth 登录流、托盘入口、管线接线。单活跃 provider，失败降级回原文。

**Tech Stack:** .NET 10 / C# 13、`System.Net.Http`、`System.Text.Json`、`System.Security.Cryptography.ProtectedData`(DPAPI)、xUnit。

**Spec:** `docs/superpowers/specs/2026-07-02-phase2-translation-design.md`

## Global Constraints

- TFM `net10.0-windows10.0.19041.0`；Core 工程无 UI/WinRT 依赖（不引用 WPF/Windows.*）。
- Provider 走 `ITranslationProvider` 接口，DI（Microsoft.Extensions.DependencyInjection）组织。
- 新代码统一命名空间 `ScrTranslator.Core.Translation`（及 `.Translation.Providers` / `.Translation.Auth`）；不动既有 `RoiConfig`/`RoiStore`/`.Ocr` 类型。
- 密钥/token 仅本地 DPAPI(`CurrentUser`) 加密落盘；除翻译请求外不外传。
- 玩家 ID 不翻（Phase 2 只翻 Game Name ROI，天然满足）。
- 失败/超时/未登录/未配置 → 降级显示原文韩文，不崩、不阻塞取帧。
- 模型 id 为待核实默认值：DashScope 用占位 `qwen-flash`（实现期核官方表）、ChatGPT 用 `gpt-5`（实现期确认 codex 接受的 id）。这些是**默认常量**，错了只影响真实网络调用、不影响单测。
- 每个 Core 组件必须有 xUnit 单测且 `dotnet test` 绿；App 的网络/浏览器/托盘/1455 监听为手动验收，不强求单测。
- 中文提交信息，提交末尾附 `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。

---

## 文件结构

**Core（`src/ScrTranslator.Core/Translation/`）**
- `ITranslationProvider.cs` — 翻译提供方接口。
- `ITokenSource.cs` — ChatGPT 有效 token 抽象。
- `TranslationSettings.cs` — 活跃引擎/模型/超时（非密钥）。
- `Glossary.cs` — 解析 `docs/GLOSSARY.md`，精确命中。
- `TranslationCache.cs` — 有界 LRU（韩→中）。
- `TranslationService.cs` — 编排：术语→缓存→provider→写缓存。
- `Providers/OpenAiCompatibleChatClient.cs` — `/chat/completions` 裸 HTTP。
- `Providers/CodexResponsesClient.cs` — `/responses` SSE 流式。
- `Providers/DashScopeProvider.cs` / `Providers/ChatGptSubscriptionProvider.cs` — `ITranslationProvider` 实现。
- `Auth/OAuthPkce.cs` — PKCE(S256) 纯函数、authorize URL 组装。
- `Auth/JwtClaims.cs` — 解 access_token 声明（不验签）、刷新时机判定。

**Core 测试（`tests/ScrTranslator.Core.Tests/Translation/`）**：每个上面组件对应一个 `*Tests.cs`。

**App（`src/ScrTranslator.App/`）**
- `Secrets/SecretStore.cs` — DPAPI 加解密落盘。
- `Auth/OpenAiOAuth.cs` — 实现 `ITokenSource`：PKCE 登录（浏览器+TcpListener 1455 回调）+ 刷新，token 经 SecretStore 持久化。
- `Auth/DashScopeKeyWindow.xaml(.cs)` — 小输入框。
- `TrayMenu` 扩展（现有托盘代码内）：登录/填 Key/选引擎。
- `Phase1Probe.cs` 接 `TranslationService`；`App.xaml.cs` DI 注册。

---

## 组 A：Core 翻译引擎（全单测）

### Task A1: ITranslationProvider / ITokenSource / TranslationSettings

**Files:**
- Create: `src/ScrTranslator.Core/Translation/ITranslationProvider.cs`
- Create: `src/ScrTranslator.Core/Translation/ITokenSource.cs`
- Create: `src/ScrTranslator.Core/Translation/TranslationSettings.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationSettingsTests.cs`

**Interfaces:**
- Produces:
  - `interface ITranslationProvider { string Name { get; } Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct); }`
  - `interface ITokenSource { Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct); }`
  - `enum TranslationEngine { DashScope, ChatGpt }`
  - `sealed record TranslationSettings { TranslationEngine Active; string DashScopeModel; string ChatGptModel; int TimeoutSeconds; static TranslationSettings Default; }`

- [ ] **Step 1: 写失败测试**

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationSettingsTests
{
    [Fact]
    public void Default_HasSaneValues()
    {
        var s = TranslationSettings.Default;
        Assert.Equal(TranslationEngine.DashScope, s.Active);
        Assert.False(string.IsNullOrWhiteSpace(s.DashScopeModel));
        Assert.False(string.IsNullOrWhiteSpace(s.ChatGptModel));
        Assert.InRange(s.TimeoutSeconds, 5, 30);
    }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter TranslationSettingsTests`，预期编译失败（类型不存在）。

- [ ] **Step 3: 写实现**

```csharp
// ITranslationProvider.cs
namespace ScrTranslator.Core.Translation;
public interface ITranslationProvider
{
    string Name { get; }
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct);
}
```
```csharp
// ITokenSource.cs
namespace ScrTranslator.Core.Translation;
public interface ITokenSource
{
    Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct);
}
```
```csharp
// TranslationSettings.cs
namespace ScrTranslator.Core.Translation;
public enum TranslationEngine { DashScope, ChatGpt }
public sealed record TranslationSettings
{
    public TranslationEngine Active { get; init; } = TranslationEngine.DashScope;
    public string DashScopeModel { get; init; } = "qwen-flash";
    public string ChatGptModel { get; init; } = "gpt-5";
    public int TimeoutSeconds { get; init; } = 10;
    public static TranslationSettings Default => new();
}
```

- [ ] **Step 4: 运行确认通过** — `dotnet test --filter TranslationSettingsTests`，预期 PASS。
- [ ] **Step 5: 提交** — `git add -A && git commit -m "feat(core): 翻译层接口与 TranslationSettings"`

---

### Task A2: Glossary（解析 GLOSSARY.md，精确命中）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/Glossary.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/GlossaryTests.cs`
- Test asset: `tests/ScrTranslator.Core.Tests/assets/glossary_sample.md`

**Interfaces:**
- Produces: `sealed class Glossary { static Glossary LoadFromMarkdown(string path); static Glossary FromEntries(IEnumerable<(string ko,string zh)>); bool TryGet(string koreanLine, out string chinese); int Count { get; } }`
- 规整键：`Trim()` 后原样比较（韩文大小写无意义）；空/空白输入不命中。
- 解析规则：只认 `| a | b | c |` 表格行；跳过表头行（含 `韩文`）与分隔行（`|---|`）；韩文单元格按 ` / ` 拆多键；中文取该单元格 ` / ` 首段并 `Trim()`。

- [ ] **Step 1: 建测试资产** `tests/ScrTranslator.Core.Tests/assets/glossary_sample.md`：

```markdown
# sample
| 韩文 | 中文 | 备注 |
|------|------|------|
| 저그 | 虫族 | Zerg |
| 인구수 / 인구 | 人口 | supply |
| 러시 | 速攻 / rush | |
非表格行应被忽略
```

- [ ] **Step 2: 写失败测试**

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class GlossaryTests
{
    private static Glossary Load() =>
        Glossary.LoadFromMarkdown(Path.Combine(AppContext.BaseDirectory, "assets", "glossary_sample.md"));

    [Fact] public void Hit_SingleKey() { Assert.True(Load().TryGet("저그", out var zh)); Assert.Equal("虫族", zh); }
    [Fact] public void Hit_TrimsWhitespace() { Assert.True(Load().TryGet("  저그 ", out var zh)); Assert.Equal("虫族", zh); }
    [Fact] public void Hit_SlashAlternateKey() { Assert.True(Load().TryGet("인구", out var zh)); Assert.Equal("人口", zh); }
    [Fact] public void Zh_TakesFirstSlashSegment() { Assert.True(Load().TryGet("러시", out var zh)); Assert.Equal("速攻", zh); }
    [Fact] public void Miss_ReturnsFalse() { Assert.False(Load().TryGet("없음", out _)); }
    [Fact] public void Miss_Empty() { Assert.False(Load().TryGet("", out _)); }
    [Fact] public void MissingFile_EmptyGlossary() { Assert.Equal(0, Glossary.LoadFromMarkdown("nope.md").Count); }
}
```

- [ ] **Step 3: 运行确认失败** — `dotnet test --filter GlossaryTests`，预期编译失败。

- [ ] **Step 4: 写实现**

```csharp
using System.IO;
namespace ScrTranslator.Core.Translation;
public sealed class Glossary
{
    private readonly Dictionary<string, string> _map;
    private Glossary(Dictionary<string, string> map) => _map = map;
    public int Count => _map.Count;

    public static Glossary FromEntries(IEnumerable<(string ko, string zh)> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ko, zh) in entries)
        {
            var k = ko.Trim();
            if (k.Length > 0 && !map.ContainsKey(k)) map[k] = zh.Trim();
        }
        return new Glossary(map);
    }

    public static Glossary LoadFromMarkdown(string path)
    {
        if (!File.Exists(path)) return new Glossary(new(StringComparer.Ordinal));
        var entries = new List<(string, string)>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '|') continue;
            var cells = line.Trim('|').Split('|');
            if (cells.Length < 2) continue;
            var ko = cells[0].Trim();
            var zh = cells[1].Trim();
            if (ko.Length == 0 || zh.Length == 0) continue;
            if (ko.Contains("韩文") || ko.StartsWith("---") || ko.StartsWith(":--")) continue; // 表头/分隔
            var zhFirst = zh.Split('/')[0].Trim();
            foreach (var koPart in ko.Split('/'))
            {
                var k = koPart.Trim();
                if (k.Length > 0) entries.Add((k, zhFirst));
            }
        }
        return FromEntries(entries);
    }

    public bool TryGet(string koreanLine, out string chinese)
    {
        chinese = "";
        if (string.IsNullOrWhiteSpace(koreanLine)) return false;
        return _map.TryGetValue(koreanLine.Trim(), out chinese!);
    }
}
```

- [ ] **Step 5: 运行确认通过** — `dotnet test --filter GlossaryTests`，预期 7 PASS。
- [ ] **Step 6: 提交** — `git add -A && git commit -m "feat(core): Glossary 解析 GLOSSARY.md 精确命中"`

---

### Task A3: TranslationCache（有界 LRU）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/TranslationCache.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationCacheTests.cs`

**Interfaces:**
- Produces: `sealed class TranslationCache { TranslationCache(int capacity=512); bool TryGet(string ko, out string zh); void Set(string ko, string zh); int Count { get; } }`
- 键 `Trim()`；容量满淘汰最久未用；`Set`/`TryGet` 都刷新最近使用。线程安全（内部 lock，管线在取帧线程调用）。

- [ ] **Step 1: 写失败测试**

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationCacheTests
{
    [Fact] public void SetThenGet() { var c = new TranslationCache(); c.Set("저그","虫族"); Assert.True(c.TryGet("저그", out var v)); Assert.Equal("虫族", v); }
    [Fact] public void Miss() { Assert.False(new TranslationCache().TryGet("x", out _)); }
    [Fact] public void TrimKey() { var c = new TranslationCache(); c.Set(" 저그 ","虫族"); Assert.True(c.TryGet("저그", out _)); }
    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var c = new TranslationCache(2);
        c.Set("a","1"); c.Set("b","2");
        Assert.True(c.TryGet("a", out _));   // a 变最近
        c.Set("c","3");                       // 淘汰 b
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(2, c.Count);
    }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter TranslationCacheTests`。

- [ ] **Step 3: 写实现**

```csharp
namespace ScrTranslator.Core.Translation;
public sealed class TranslationCache
{
    private readonly int _cap;
    private readonly object _lock = new();
    private readonly LinkedList<(string k, string v)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string k, string v)>> _idx = new(StringComparer.Ordinal);
    public TranslationCache(int capacity = 512) => _cap = Math.Max(1, capacity);
    public int Count { get { lock (_lock) return _idx.Count; } }

    public bool TryGet(string ko, out string zh)
    {
        zh = "";
        if (string.IsNullOrWhiteSpace(ko)) return false;
        var key = ko.Trim();
        lock (_lock)
        {
            if (!_idx.TryGetValue(key, out var node)) return false;
            _lru.Remove(node); _lru.AddFirst(node);
            zh = node.Value.v; return true;
        }
    }

    public void Set(string ko, string zh)
    {
        if (string.IsNullOrWhiteSpace(ko)) return;
        var key = ko.Trim();
        lock (_lock)
        {
            if (_idx.TryGetValue(key, out var existing)) { _lru.Remove(existing); }
            var node = new LinkedListNode<(string, string)>((key, zh));
            _lru.AddFirst(node); _idx[key] = node;
            while (_idx.Count > _cap)
            {
                var last = _lru.Last!; _lru.RemoveLast(); _idx.Remove(last.Value.k);
            }
        }
    }
}
```

- [ ] **Step 4: 运行确认通过** — `dotnet test --filter TranslationCacheTests`。
- [ ] **Step 5: 提交** — `git commit -m "feat(core): TranslationCache 有界 LRU"`

---

### Task A4: OpenAiCompatibleChatClient（DashScope 用，假 handler 单测）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/Providers/OpenAiCompatibleChatClient.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/OpenAiCompatibleChatClientTests.cs`
- Test helper: `tests/ScrTranslator.Core.Tests/Translation/StubHttpMessageHandler.cs`

**Interfaces:**
- Produces: `sealed class OpenAiCompatibleChatClient { OpenAiCompatibleChatClient(HttpClient http, string baseUrl, Func<string> apiKeyGetter); Task<string> CompleteAsync(string model, string system, string user, CancellationToken ct); }`
- POST `{baseUrl}/chat/completions`；头 `Authorization: Bearer {apiKeyGetter()}`（每次调用读取，支持运行时改 key）；body `{model, messages:[{role:system},{role:user}], stream:false}`；返回 `choices[0].message.content`。非 2xx 抛 `HttpRequestException`。

- [ ] **Step 1: 建 StubHttpMessageHandler**

```csharp
using System.Net;
namespace ScrTranslator.Core.Tests.Translation;
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest; public string? LastBody;
    private readonly HttpStatusCode _code; private readonly string _resp;
    public StubHttpMessageHandler(string resp, HttpStatusCode code = HttpStatusCode.OK) { _resp = resp; _code = code; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_code) { Content = new StringContent(_resp) };
    }
}
```

- [ ] **Step 2: 写失败测试**

```csharp
using System.Net;
using System.Text.Json;
using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task Complete_ParsesContent_AndSendsBearer()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"虫族\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "KEY");
        var txt = await client.CompleteAsync("qwen-flash", "sys", "저그", CancellationToken.None);
        Assert.Equal("虫族", txt);
        Assert.Equal("Bearer KEY", stub.LastRequest!.Headers.Authorization!.ToString());
        Assert.Equal("https://x/v1/chat/completions", stub.LastRequest.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("qwen-flash", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task NonSuccess_Throws()
    {
        var stub = new StubHttpMessageHandler("bad", HttpStatusCode.Unauthorized);
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("m","s","u", CancellationToken.None));
    }
}
```

- [ ] **Step 3: 运行确认失败** — `dotnet test --filter OpenAiCompatibleChatClientTests`。

- [ ] **Step 4: 写实现**

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Providers;
public sealed class OpenAiCompatibleChatClient
{
    private readonly HttpClient _http; private readonly string _base; private readonly Func<string> _key;
    public OpenAiCompatibleChatClient(HttpClient http, string baseUrl, Func<string> apiKeyGetter)
    { _http = http; _base = baseUrl.TrimEnd('/'); _key = apiKeyGetter; }

    public async Task<string> CompleteAsync(string model, string system, string user, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            stream = false
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/chat/completions")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key());
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"chat/completions {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
```

- [ ] **Step 5: 运行确认通过** — `dotnet test --filter OpenAiCompatibleChatClientTests`。
- [ ] **Step 6: 提交** — `git commit -m "feat(core): OpenAiCompatibleChatClient(裸HTTP,假handler单测)"`

---

### Task A5: CodexResponsesClient（ChatGPT 订阅 SSE 流式）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/Providers/CodexResponsesClient.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/CodexResponsesClientTests.cs`

**Interfaces:**
- Produces: `sealed class CodexResponsesClient { CodexResponsesClient(HttpClient http); Task<string> CompleteAsync(string model, string system, string user, string accessToken, string accountId, CancellationToken ct); }`
- POST `https://chatgpt.com/backend-api/codex/responses`；头 `Authorization: Bearer {accessToken}` + `ChatGPT-Account-ID: {accountId}` + `originator: codex_cli_rs`；body `{model, instructions:system, input:[{role:user,content:[{type:input_text,text:user}]}], stream:true, store:false}`；读 SSE 累积所有 `response.output_text.delta` 的 `delta`；非 2xx 抛。
- SSE 解析：按行读，`data: {json}`；对每个 data JSON，若 `type=="response.output_text.delta"` 取 `delta` 追加；`data: [DONE]` 或 `type=="response.completed"` 结束。

- [ ] **Step 1: 写失败测试**（stub 返回一段 SSE 文本）

```csharp
using System.Net;
using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class CodexResponsesClientTests
{
    private const string Sse =
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"虫\"}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"族\"}\n\n" +
        "data: {\"type\":\"response.completed\"}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Accumulates_Deltas_AndSendsHeaders()
    {
        var stub = new StubHttpMessageHandler(Sse);
        var client = new CodexResponsesClient(new HttpClient(stub));
        var txt = await client.CompleteAsync("gpt-5", "sys", "저그", "TOK", "ACC", CancellationToken.None);
        Assert.Equal("虫族", txt);
        Assert.Equal("Bearer TOK", stub.LastRequest!.Headers.Authorization!.ToString());
        Assert.Contains(stub.LastRequest.Headers, h => h.Key == "ChatGPT-Account-ID" && string.Join("", h.Value) == "ACC");
        Assert.Contains(stub.LastRequest.Headers, h => h.Key == "originator" && string.Join("", h.Value) == "codex_cli_rs");
        Assert.Contains("input_text", stub.LastBody);
    }

    [Fact]
    public async Task NonSuccess_Throws()
    {
        var stub = new StubHttpMessageHandler("no", HttpStatusCode.Forbidden);
        var client = new CodexResponsesClient(new HttpClient(stub));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("m","s","u","t","a", CancellationToken.None));
    }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter CodexResponsesClientTests`。

- [ ] **Step 3: 写实现**

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Providers;
public sealed class CodexResponsesClient
{
    private const string Url = "https://chatgpt.com/backend-api/codex/responses";
    private readonly HttpClient _http;
    public CodexResponsesClient(HttpClient http) => _http = http;

    public async Task<string> CompleteAsync(string model, string system, string user,
        string accessToken, string accountId, CancellationToken ct)
    {
        var payload = new
        {
            model,
            instructions = system,
            input = new object[] { new { role = "user", content = new object[] { new { type = "input_text", text = user } } } },
            stream = true,
            store = false
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);
        req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"codex/responses {(int)resp.StatusCode}: {err}");
        }
        var sb = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "response.output_text.delta" && doc.RootElement.TryGetProperty("delta", out var d))
                    sb.Append(d.GetString());
                else if (type == "response.completed") break;
            }
            catch (JsonException) { /* 忽略非 JSON 心跳行 */ }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 运行确认通过** — `dotnet test --filter CodexResponsesClientTests`。
- [ ] **Step 5: 提交** — `git commit -m "feat(core): CodexResponsesClient SSE 流式(ChatGPT 订阅)"`

---

### Task A6: 两个 Provider（DashScope / ChatGpt）+ 批量协议

**Files:**
- Create: `src/ScrTranslator.Core/Translation/Providers/BatchProtocol.cs`（组 prompt + 解析 `[i]`）
- Create: `src/ScrTranslator.Core/Translation/Providers/DashScopeProvider.cs`
- Create: `src/ScrTranslator.Core/Translation/Providers/ChatGptSubscriptionProvider.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/BatchProtocolTests.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/ProvidersTests.cs`

**Interfaces:**
- Produces:
  - `static class BatchProtocol { const string SystemPrompt; string BuildUser(IReadOnlyList<string> lines); IReadOnlyList<string>? Parse(string reply, int expected); }`（`Parse` 行数/序号不符返回 null）
  - `sealed class DashScopeProvider(OpenAiCompatibleChatClient client, Func<string> modelGetter) : ITranslationProvider`（Name="dashscope"）
  - `sealed class ChatGptSubscriptionProvider(CodexResponsesClient client, ITokenSource tokens, Func<string> modelGetter) : ITranslationProvider`（Name="chatgpt"）
- Provider.TranslateAsync：空入参→空；否则 `BuildUser`→client 调用→`Parse`；Parse 返回 null（行数不符）→**整批回原文**（返回入参本身）。

- [ ] **Step 1: 写 BatchProtocol 失败测试**

```csharp
using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class BatchProtocolTests
{
    [Fact] public void Build_NumbersLines()
    { var u = BatchProtocol.BuildUser(new[]{"가","나"}); Assert.Contains("[1]", u); Assert.Contains("[2]", u); Assert.Contains("가", u); }
    [Fact] public void Parse_Good()
    { var r = BatchProtocol.Parse("[1] 甲\n[2] 乙", 2); Assert.Equal(new[]{"甲","乙"}, r); }
    [Fact] public void Parse_WrongCount_Null()
    { Assert.Null(BatchProtocol.Parse("[1] 甲", 2)); }
    [Fact] public void Parse_MissingIndex_Null()
    { Assert.Null(BatchProtocol.Parse("[1] 甲\n[3] 丙", 2)); }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter BatchProtocolTests`。

- [ ] **Step 3: 写 BatchProtocol 实现**

```csharp
using System.Text;
using System.Text.RegularExpressions;
namespace ScrTranslator.Core.Translation.Providers;
public static class BatchProtocol
{
    public const string SystemPrompt =
        "你是《星际争霸：重制版》联机界面的韩译中助手。把每行韩文翻成简体中文。" +
        "输入每行形如 [i] 原文；只输出译文行，格式 [i] 译文，行数与序号必须与输入一一对应。" +
        "玩家ID、纯英文/数字/符号原样保留。不要解释、不要多余内容。";

    public static string BuildUser(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++) sb.Append('[').Append(i + 1).Append("] ").AppendLine(lines[i]);
        return sb.ToString().TrimEnd();
    }

    private static readonly Regex Row = new(@"^\s*\[(\d+)\]\s?(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<string>? Parse(string reply, int expected)
    {
        var found = new Dictionary<int, string>();
        foreach (var raw in reply.Split('\n'))
        {
            var m = Row.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out var idx)) found[idx] = m.Groups[2].Value.Trim();
        }
        if (found.Count != expected) return null;
        var res = new string[expected];
        for (int i = 1; i <= expected; i++) { if (!found.TryGetValue(i, out var v)) return null; res[i - 1] = v; }
        return res;
    }
}
```

- [ ] **Step 4: 写 Provider 失败测试**（用假 client：直接构造 OpenAiCompatibleChatClient 套 StubHttpMessageHandler）

```csharp
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class ProvidersTests
{
    [Fact]
    public async Task DashScope_TranslatesBatch()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\\n[2] 乙\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var p = new DashScopeProvider(client, () => "qwen-flash");
        var r = await p.TranslateAsync(new[]{"가","나"}, CancellationToken.None);
        Assert.Equal(new[]{"甲","乙"}, r);
        Assert.Equal("dashscope", p.Name);
    }

    [Fact]
    public async Task DashScope_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var p = new DashScopeProvider(client, () => "m");
        var r = await p.TranslateAsync(new[]{"가","나"}, CancellationToken.None);
        Assert.Equal(new[]{"가","나"}, r); // 回原文
    }

    [Fact]
    public async Task Empty_ReturnsEmpty()
    {
        var stub = new StubHttpMessageHandler("{}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var r = await new DashScopeProvider(client, () => "m").TranslateAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Empty(r);
    }
}
```

- [ ] **Step 5: 运行确认失败** — `dotnet test --filter "ProvidersTests|BatchProtocolTests"`。

- [ ] **Step 6: 写 Provider 实现**

```csharp
// DashScopeProvider.cs
namespace ScrTranslator.Core.Translation.Providers;
public sealed class DashScopeProvider : ITranslationProvider
{
    private readonly OpenAiCompatibleChatClient _client; private readonly Func<string> _model;
    public DashScopeProvider(OpenAiCompatibleChatClient client, Func<string> modelGetter) { _client = client; _model = modelGetter; }
    public string Name => "dashscope";
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var reply = await _client.CompleteAsync(_model(), BatchProtocol.SystemPrompt, BatchProtocol.BuildUser(lines), ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
}
```
```csharp
// ChatGptSubscriptionProvider.cs
namespace ScrTranslator.Core.Translation.Providers;
public sealed class ChatGptSubscriptionProvider : ITranslationProvider
{
    private readonly CodexResponsesClient _client; private readonly ITokenSource _tokens; private readonly Func<string> _model;
    public ChatGptSubscriptionProvider(CodexResponsesClient client, ITokenSource tokens, Func<string> modelGetter)
    { _client = client; _tokens = tokens; _model = modelGetter; }
    public string Name => "chatgpt";
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return Array.Empty<string>();
        var (token, account) = await _tokens.GetAsync(ct);
        var reply = await _client.CompleteAsync(_model(), BatchProtocol.SystemPrompt, BatchProtocol.BuildUser(lines), token, account, ct);
        return BatchProtocol.Parse(reply, lines.Count) ?? lines;
    }
}
```

- [ ] **Step 7: 运行确认通过** — `dotnet test --filter "ProvidersTests|BatchProtocolTests"`。
- [ ] **Step 8: 提交** — `git commit -m "feat(core): 批量协议 + DashScope/ChatGpt 两 provider"`

---

### Task A7: TranslationService（编排：术语→缓存→provider→写缓存）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/TranslationService.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/TranslationServiceTests.cs`

**Interfaces:**
- Produces: `sealed class TranslationService { TranslationService(Glossary glossary, TranslationCache cache, Func<ITranslationProvider> activeProvider); Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct); }`
- 逐行：Glossary 命中→直出；否则 Cache 命中→直出；剩余未命中收集成批 → provider.TranslateAsync → 写 Cache → 回填对应位置。provider 抛异常 → 未命中行降级回原文（不抛）。保持输出与输入等长同序。

- [ ] **Step 1: 写失败测试**（用假 provider 记录被请求的行）

```csharp
using ScrTranslator.Core.Translation;
namespace ScrTranslator.Core.Tests.Translation;
public class TranslationServiceTests
{
    private sealed class FakeProvider : ITranslationProvider
    {
        public List<string> Requested = new(); public bool Throw;
        public string Name => "fake";
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException();
            Requested.AddRange(lines);
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => "T:" + l).ToArray());
        }
    }

    [Fact]
    public async Task GlossaryAndCache_ShortCircuit_OnlyMissesHitProvider()
    {
        var glo = Glossary.FromEntries(new[] { ("저그", "虫族") });
        var cache = new TranslationCache(); cache.Set("나", "缓存中");
        var fake = new FakeProvider();
        var svc = new TranslationService(glo, cache, () => fake);
        var r = await svc.TranslateAsync(new[] { "저그", "나", "다" }, CancellationToken.None);
        Assert.Equal(new[] { "虫族", "缓存中", "T:다" }, r);
        Assert.Equal(new[] { "다" }, fake.Requested);          // 只有未命中的 다 走 provider
        Assert.True(cache.TryGet("다", out var c)); Assert.Equal("T:다", c); // 结果入缓存
    }

    [Fact]
    public async Task ProviderThrows_FallsBackToOriginal()
    {
        var svc = new TranslationService(Glossary.FromEntries(Array.Empty<(string,string)>()),
            new TranslationCache(), () => new FakeProvider { Throw = true });
        var r = await svc.TranslateAsync(new[] { "가", "나" }, CancellationToken.None);
        Assert.Equal(new[] { "가", "나" }, r);
    }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter TranslationServiceTests`。

- [ ] **Step 3: 写实现**

```csharp
namespace ScrTranslator.Core.Translation;
public sealed class TranslationService
{
    private readonly Glossary _glossary; private readonly TranslationCache _cache; private readonly Func<ITranslationProvider> _active;
    public TranslationService(Glossary glossary, TranslationCache cache, Func<ITranslationProvider> activeProvider)
    { _glossary = glossary; _cache = cache; _active = activeProvider; }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct)
    {
        var result = new string[koreanLines.Count];
        var missIdx = new List<int>(); var missText = new List<string>();
        for (int i = 0; i < koreanLines.Count; i++)
        {
            var ko = koreanLines[i];
            if (_glossary.TryGet(ko, out var g)) result[i] = g;
            else if (_cache.TryGet(ko, out var c)) result[i] = c;
            else { missIdx.Add(i); missText.Add(ko); }
        }
        if (missText.Count > 0)
        {
            IReadOnlyList<string> translated;
            try { translated = await _active().TranslateAsync(missText, ct); }
            catch { translated = missText; } // 降级回原文
            for (int j = 0; j < missIdx.Count; j++)
            {
                var zh = j < translated.Count ? translated[j] : missText[j];
                result[missIdx[j]] = zh;
                if (!ReferenceEquals(zh, missText[j]) && zh != missText[j]) _cache.Set(missText[j], zh);
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: 运行确认通过** — `dotnet test --filter TranslationServiceTests`。
- [ ] **Step 5: 提交** — `git commit -m "feat(core): TranslationService 编排(术语→缓存→provider→降级)"`

---

### Task A8: OAuthPkce + JwtClaims（Core 纯函数）

**Files:**
- Create: `src/ScrTranslator.Core/Translation/Auth/OAuthPkce.cs`
- Create: `src/ScrTranslator.Core/Translation/Auth/JwtClaims.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/OAuthPkceTests.cs`
- Test: `tests/ScrTranslator.Core.Tests/Translation/JwtClaimsTests.cs`

**Interfaces:**
- Produces:
  - `static class OAuthPkce { const string ClientId="app_EMoamEEZ73f0CkXaXp7hrann"; const string AuthBase="https://auth.openai.com"; const string RedirectUri="http://localhost:1455/auth/callback"; const string Scope="openid profile email offline_access"; const string Originator="codex_cli_rs"; (string verifier,string challenge) GenPkce(); string BuildAuthorizeUrl(string challenge, string state); }`
  - `static class JwtClaims { record Claims(long? Exp, string AccountId, string PlanType, string Email); Claims Parse(string jwt); bool NeedsRefresh(long? exp, DateTimeOffset now, int marginSeconds=300); }`
- challenge = BASE64URL(SHA256(verifier))；authorize URL 空格编码 `%20`（用 `Uri.EscapeDataString`）。JwtClaims.Parse 解 payload 段 base64url，读 `exp` 与 `https://api.openai.com/auth`.{chatgpt_account_id,chatgpt_plan_type} 与 profile.email。

- [ ] **Step 1: 写失败测试**

```csharp
using System.Security.Cryptography;
using System.Text;
using ScrTranslator.Core.Translation.Auth;
namespace ScrTranslator.Core.Tests.Translation;
public class OAuthPkceTests
{
    [Fact]
    public void GenPkce_ChallengeIsSha256OfVerifier()
    {
        var (verifier, challenge) = OAuthPkce.GenPkce();
        var expected = Base64UrlNoPad(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
        Assert.True(verifier.Length >= 43);
    }
    [Fact]
    public void AuthorizeUrl_HasParamsAndEncodesSpaceAs20()
    {
        var url = OAuthPkce.BuildAuthorizeUrl("CH", "ST");
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?", url);
        Assert.Contains("code_challenge=CH", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=ST", url);
        Assert.Contains("scope=openid%20profile%20email%20offline_access", url);
        Assert.DoesNotContain("+", url);
    }
    private static string Base64UrlNoPad(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
}
```
```csharp
using System.Text;
using ScrTranslator.Core.Translation.Auth;
namespace ScrTranslator.Core.Tests.Translation;
public class JwtClaimsTests
{
    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+','-').Replace('/','_');
    [Fact]
    public void Parse_ReadsExpAccountEmail()
    {
        var payload = "{\"exp\":1782600454,\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"ACC\",\"chatgpt_plan_type\":\"plus\"},\"https://api.openai.com/profile\":{\"email\":\"a@b.com\"}}";
        var jwt = "h." + B64Url(payload) + ".s";
        var c = JwtClaims.Parse(jwt);
        Assert.Equal(1782600454, c.Exp);
        Assert.Equal("ACC", c.AccountId);
        Assert.Equal("plus", c.PlanType);
        Assert.Equal("a@b.com", c.Email);
    }
    [Fact]
    public void NeedsRefresh_WithinMargin_True()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Assert.True(JwtClaims.NeedsRefresh(1200, now, 300));   // 剩 200s < 300
        Assert.False(JwtClaims.NeedsRefresh(2000, now, 300));  // 剩 1000s
        Assert.True(JwtClaims.NeedsRefresh(null, now, 300));
    }
}
```

- [ ] **Step 2: 运行确认失败** — `dotnet test --filter "OAuthPkceTests|JwtClaimsTests"`。

- [ ] **Step 3: 写实现**

```csharp
// OAuthPkce.cs
using System.Security.Cryptography;
using System.Text;
namespace ScrTranslator.Core.Translation.Auth;
public static class OAuthPkce
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string AuthBase = "https://auth.openai.com";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string Scope = "openid profile email offline_access";
    public const string Originator = "codex_cli_rs";

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string verifier, string challenge) GenPkce()
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string BuildAuthorizeUrl(string challenge, string state)
    {
        string E(string s) => Uri.EscapeDataString(s);
        var q = $"response_type=code&client_id={E(ClientId)}&redirect_uri={E(RedirectUri)}" +
                $"&scope={E(Scope)}&code_challenge={E(challenge)}&code_challenge_method=S256" +
                $"&state={E(state)}&originator={E(Originator)}";
        return $"{AuthBase}/oauth/authorize?{q}";
    }
}
```
```csharp
// JwtClaims.cs
using System.Text;
using System.Text.Json;
namespace ScrTranslator.Core.Translation.Auth;
public static class JwtClaims
{
    public sealed record Claims(long? Exp, string AccountId, string PlanType, string Email);

    public static Claims Parse(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return new Claims(null, "", "", "");
        var json = Encoding.UTF8.GetString(FromB64Url(parts[1]));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        long? exp = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var ev) ? ev : null;
        string acc = "", plan = "", email = "";
        if (root.TryGetProperty("https://api.openai.com/auth", out var auth))
        {
            acc = auth.TryGetProperty("chatgpt_account_id", out var a) ? a.GetString() ?? "" : "";
            plan = auth.TryGetProperty("chatgpt_plan_type", out var p) ? p.GetString() ?? "" : "";
        }
        if (root.TryGetProperty("https://api.openai.com/profile", out var prof) && prof.TryGetProperty("email", out var em))
            email = em.GetString() ?? "";
        else if (root.TryGetProperty("email", out var em2)) email = em2.GetString() ?? "";
        return new Claims(exp, acc, plan, email);
    }

    public static bool NeedsRefresh(long? exp, DateTimeOffset now, int marginSeconds = 300)
    {
        if (exp is null) return true;
        return exp.Value - now.ToUnixTimeSeconds() < marginSeconds;
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
```

- [ ] **Step 4: 运行确认通过** — `dotnet test --filter "OAuthPkceTests|JwtClaimsTests"`。
- [ ] **Step 5: 提交** — `git commit -m "feat(core): OAuthPkce + JwtClaims 纯函数(可单测)"`

---

### Task A9: 组 A 全量回归

- [ ] **Step 1:** `dotnet build` 预期 0 错误。
- [ ] **Step 2:** `dotnet test` 预期全绿（原 14 + 新增约 25+）。
- [ ] **Step 3:** 若红，按 systematic-debugging 修到绿再继续。

---

## 组 B：App 接线（DPAPI 可测；OAuth/托盘/管线手动验收）

### Task B1: SecretStore（DPAPI 加解密落盘）

**Files:**
- Modify: `src/ScrTranslator.App/ScrTranslator.App.csproj`（加包 `System.Security.Cryptography.ProtectedData`）
- Create: `src/ScrTranslator.App/Secrets/SecretStore.cs`
- Create: `src/ScrTranslator.App/Secrets/SecretData.cs`
- Test: `tests/ScrTranslator.Core.Tests/...` **不适用**（App 工程无测试项目）；改为在 App 内提供纯函数 `SecretCodec`（加解密往返）并**临时**手测。

> 说明：现无 App 测试工程。为可验证，把 DPAPI 往返做成 `SecretStore.Save/Load` 用真实文件，B4 手动验收时连带验证；此处仅保证 build 通过 + 一次 round-trip 冒烟（B4）。

- [ ] **Step 1: 加 NuGet 包**

Modify `ScrTranslator.App.csproj` 的 `<ItemGroup>` 加：
```xml
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="9.0.0" />
```

- [ ] **Step 2: 写 SecretData + SecretStore**

```csharp
// Secrets/SecretData.cs
namespace ScrTranslator.App.Secrets;
public sealed class OpenAiTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ExpiresAt { get; set; } = "";   // ISO8601
    public string PlanType { get; set; } = "";
    public string Email { get; set; } = "";
}
public sealed class SecretData
{
    public string DashScopeApiKey { get; set; } = "";
    public OpenAiTokens OpenAi { get; set; } = new();
}
```
```csharp
// Secrets/SecretStore.cs
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ScrTranslator.App.Secrets;
public sealed class SecretStore
{
    private readonly string _path;
    public SecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScrTranslator", "secrets.dat");
    }
    public SecretData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SecretData();
            var enc = File.ReadAllBytes(_path);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<SecretData>(json) ?? new SecretData();
        }
        catch { return new SecretData(); } // 损坏/无法解密→空
    }
    public void Save(SecretData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, enc);
    }
}
```

- [ ] **Step 3: build** — `dotnet build src/ScrTranslator.App` 预期 0 错误。
- [ ] **Step 4: 提交** — `git commit -m "feat(app): SecretStore DPAPI 加密存密钥/token"`

---

### Task B2: OpenAiOAuth（实现 ITokenSource：登录流 + 刷新）

**Files:**
- Create: `src/ScrTranslator.App/Auth/OpenAiOAuth.cs`

**Interfaces:**
- Consumes: `OAuthPkce`、`JwtClaims`（Core）、`SecretStore`（App）。
- Produces: `sealed class OpenAiOAuth : ITokenSource { OpenAiOAuth(SecretStore store, HttpClient http); Task<bool> LoginAsync(CancellationToken ct); Task<(string,string)> GetAsync(CancellationToken ct); (bool loggedIn,string email,string plan) Status(); void Logout(); }`
- `GetAsync`：读 SecretStore；`JwtClaims.NeedsRefresh` → `refresh_tokens`(POST `/oauth/token`, body `grant_type=refresh_token&client_id&refresh_token&scope=SCOPE`) → 写回；返回 (accessToken, accountId)。未登录抛 `InvalidOperationException("未登录 ChatGPT 订阅")`。
- `LoginAsync`：`GenPkce`→`Process.Start` 打开 authorize URL→`TcpListener` 127.0.0.1:1455 收一次 GET 解 `code`/`state`(校验 state)→ `exchange_code`(POST `/oauth/token`, grant=authorization_code + code + verifier + redirect_uri + client_id) → `JwtClaims.Parse` 填 OpenAiTokens → `store.Save`。超时/端口占用→返回 false。

- [ ] **Step 1: 写实现**（无自动化测试；逻辑复刻参考 `openai_oauth.py`）

```csharp
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScrTranslator.App.Secrets;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Auth;
namespace ScrTranslator.App.Auth;

public sealed class OpenAiOAuth : ITokenSource
{
    private const string TokenUrl = OAuthPkce.AuthBase + "/oauth/token";
    private readonly SecretStore _store; private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public OpenAiOAuth(SecretStore store, HttpClient http) { _store = store; _http = http; }

    public (bool loggedIn, string email, string plan) Status()
    {
        var t = _store.Load().OpenAi;
        return (!string.IsNullOrEmpty(t.AccessToken), t.Email, t.PlanType);
    }
    public void Logout() { var d = _store.Load(); d.OpenAi = new OpenAiTokens(); _store.Save(d); }

    public async Task<(string, string)> GetAsync(CancellationToken ct)
    {
        var data = _store.Load();
        var t = data.OpenAi;
        if (string.IsNullOrEmpty(t.AccessToken)) throw new InvalidOperationException("未登录 ChatGPT 订阅");
        long? exp = DateTimeOffset.TryParse(t.ExpiresAt, out var dt) ? dt.ToUnixTimeSeconds() : null;
        if (JwtClaims.NeedsRefresh(exp, DateTimeOffset.UtcNow))
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                data = _store.Load(); t = data.OpenAi;
                exp = DateTimeOffset.TryParse(t.ExpiresAt, out var dt2) ? dt2.ToUnixTimeSeconds() : null;
                if (JwtClaims.NeedsRefresh(exp, DateTimeOffset.UtcNow))
                {
                    if (string.IsNullOrEmpty(t.RefreshToken)) throw new InvalidOperationException("缺少 refresh_token，请重新登录");
                    var tok = await PostToken(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = OAuthPkce.ClientId,
                        ["refresh_token"] = t.RefreshToken,
                        ["scope"] = OAuthPkce.Scope,
                    }, ct);
                    ApplyTokens(data, tok); _store.Save(data); t = data.OpenAi;
                }
            }
            finally { _refreshLock.Release(); }
        }
        return (t.AccessToken, t.AccountId);
    }

    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkce.GenPkce();
        var state = Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 1455);
        try { listener.Start(); } catch { return false; } // 端口占用
        Process.Start(new ProcessStartInfo(OAuthPkce.BuildAuthorizeUrl(challenge, state)) { UseShellExecute = true });
        try
        {
            using var reg = ct.Register(listener.Stop);
            using var accept = await listener.AcceptTcpClientAsync(ct);
            var (code, gotState) = await ReadCallback(accept, ct);
            if (code is null || gotState != state) return false;
            var tok = await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthPkce.ClientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthPkce.RedirectUri,
                ["code_verifier"] = verifier,
            }, ct);
            var data = _store.Load(); ApplyTokens(data, tok); _store.Save(data);
            return true;
        }
        catch { return false; }
        finally { listener.Stop(); }
    }

    private async Task<JsonElement> PostToken(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"oauth/token {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void ApplyTokens(SecretData data, JsonElement tok)
    {
        var t = data.OpenAi;
        t.AccessToken = tok.GetProperty("access_token").GetString() ?? "";
        if (tok.TryGetProperty("refresh_token", out var r)) t.RefreshToken = r.GetString() ?? t.RefreshToken;
        if (tok.TryGetProperty("id_token", out var i)) t.IdToken = i.GetString() ?? t.IdToken;
        var c = JwtClaims.Parse(t.AccessToken);
        t.AccountId = string.IsNullOrEmpty(c.AccountId) ? t.AccountId : c.AccountId;
        t.PlanType = string.IsNullOrEmpty(c.PlanType) ? t.PlanType : c.PlanType;
        t.Email = string.IsNullOrEmpty(c.Email) ? t.Email : c.Email;
        if (c.Exp is { } exp) t.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).ToString("o");
    }

    private static async Task<(string? code, string? state)> ReadCallback(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var buf = new byte[4096];
        int n = await stream.ReadAsync(buf, ct);
        var reqLine = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0]; // GET /auth/callback?code=..&state=.. HTTP/1.1
        string? code = null, state = null;
        var qIdx = reqLine.IndexOf('?');
        if (qIdx >= 0)
        {
            var path = reqLine[(qIdx + 1)..].Split(' ')[0];
            foreach (var kv in path.Split('&'))
            {
                var p = kv.Split('=', 2);
                if (p.Length == 2 && p[0] == "code") code = Uri.UnescapeDataString(p[1]);
                if (p.Length == 2 && p[0] == "state") state = Uri.UnescapeDataString(p[1]);
            }
        }
        var html = "<html><body><h3>登录完成，可关闭本页。</h3></body></html>";
        var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n" + html;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
        return (code, state);
    }
}
```

- [ ] **Step 2: build** — `dotnet build src/ScrTranslator.App` 预期 0 错误。
- [ ] **Step 3: 提交** — `git commit -m "feat(app): OpenAiOAuth 登录流+刷新(实现 ITokenSource)"`

---

### Task B3: DashScope Key 输入框 + 托盘菜单 + DI 接线 + 管线接翻译

**Files:**
- Create: `src/ScrTranslator.App/Auth/DashScopeKeyWindow.xaml(.cs)`（简单输入框）
- Modify: `src/ScrTranslator.App/App.xaml.cs`（DI 注册 + 托盘菜单项）
- Modify: `src/ScrTranslator.App/Phase1Probe.cs`（OCR→翻译→上屏中文）
- Modify: 现有托盘代码（加"登录 ChatGPT / 设置阿里云 Key / 翻译引擎"）

**Interfaces:**
- Consumes: `TranslationService`、`OpenAiOAuth`、`SecretStore`、两 provider、`TranslationSettings`。
- Produces: `Phase1Probe` 内新增依赖 `TranslationService translator`；`OnFrame` 收集当行韩文→`translator.TranslateAsync`→`OverlayItem(中文,x,y)`；加变化检测（缓存上帧各 ROI 韩文集合）。

- [ ] **Step 1: DI 注册**（App.xaml.cs `ConfigureServices`）——注册单例：
```csharp
services.AddSingleton<SecretStore>();
services.AddSingleton(new HttpClient());
services.AddSingleton<OpenAiOAuth>();
services.AddSingleton<ITokenSource>(sp => sp.GetRequiredService<OpenAiOAuth>());
services.AddSingleton(TranslationSettings.Default); // MVP：内存态，可后续持久化
services.AddSingleton(sp => Glossary.LoadFromMarkdown(Path.Combine(AppContext.BaseDirectory, "..","..","..","..","..","docs","GLOSSARY.md")));
// 更稳妥：把 GLOSSARY.md 作为 App 内容随包拷贝；实现期改为 CopyToOutputDirectory 后用 BaseDirectory 直取。
services.AddSingleton<TranslationCache>();
services.AddSingleton(sp => new OpenAiCompatibleChatClient(sp.GetRequiredService<HttpClient>(),
    "https://dashscope.aliyuncs.com/compatible-mode/v1",
    () => sp.GetRequiredService<SecretStore>().Load().DashScopeApiKey));
services.AddSingleton(sp => new CodexResponsesClient(sp.GetRequiredService<HttpClient>()));
services.AddSingleton(sp => new DashScopeProvider(sp.GetRequiredService<OpenAiCompatibleChatClient>(),
    () => sp.GetRequiredService<TranslationSettings>().DashScopeModel));
services.AddSingleton(sp => new ChatGptSubscriptionProvider(sp.GetRequiredService<CodexResponsesClient>(),
    sp.GetRequiredService<ITokenSource>(), () => sp.GetRequiredService<TranslationSettings>().ChatGptModel));
services.AddSingleton(sp => new TranslationService(
    sp.GetRequiredService<Glossary>(), sp.GetRequiredService<TranslationCache>(),
    () => sp.GetRequiredService<TranslationSettings>().Active == TranslationEngine.DashScope
        ? sp.GetRequiredService<DashScopeProvider>()
        : sp.GetRequiredService<ChatGptSubscriptionProvider>()));
```
> 注：DashScope apiKey 在构造时读一次；改 key 后需重启或重建 client——MVP 可接受，实现期若想热更改成每次 `CompleteAsync` 前读 SecretStore（把 key 也做成 `Func<string>`）。**实现期采用 `Func<string>` 版**避免重启，见下 Step 微调。

- [ ] **Step 2: apiKey 热更**——A4 的 client 已是 `Func<string> apiKeyGetter`，DI 直接传 `() => sp.GetRequiredService<SecretStore>().Load().DashScopeApiKey`，改 key 后无需重启即生效（无需再重构）。

- [ ] **Step 3: DashScopeKeyWindow**（最简：TextBox + 确定；确定后 `store.Load()`→设 `DashScopeApiKey`→`store.Save()`）。

- [ ] **Step 4: 托盘菜单**（在现有托盘构建处加三项）：
  - `登录 ChatGPT（订阅）` → `await oauth.LoginAsync(...)`；完成后菜单标题显示 `已登录: {email}({plan})`。
  - `设置阿里云 Key` → 弹 DashScopeKeyWindow。
  - `翻译引擎 ▸ DashScope | ChatGPT` → 单选，改 `TranslationSettings.Active`（因是 record，需换成可变持有者：把 TranslationSettings 注册为可变包装 `SettingsHolder{ TranslationSettings Current }` 单例；Provider 的 `Func` 从 holder 读）。
    - 微调：新增 `sealed class SettingsHolder { public TranslationSettings Current { get; set; } = TranslationSettings.Default; }`，DI 单例；上面所有 `GetRequiredService<TranslationSettings>()` 改 `GetRequiredService<SettingsHolder>().Current`。

- [ ] **Step 5: Phase1Probe 接翻译**——注入 `TranslationService`；`OnFrame` 里把每个 ROI 的 OCR 行文本收集，`var zh = await translator.TranslateAsync(koLines, ct)`，用 `zh[k]` 替换 `OverlayItem` 文本（坐标不变）。**变化检测**：字段 `Dictionary<string, string> _lastRoiText`（key=roi.Name，value=该 ROI 所有行拼接）；与本帧比较，未变则跳过翻译与 `ShowLines`。

```csharp
// OnFrame 内，替换现有"直接用 line.Text 组 OverlayItem"的部分（示意）：
var koLines = new List<string>(); var metas = new List<(RectangleF bounds, Rectangle px)>();
foreach (var roi in bucket.Rois) { /* 裁剪+OCR 后 */
    foreach (var line in res.Lines) { koLines.Add(line.Text); metas.Add((line.Bounds, px)); }
}
// 变化检测
var joined = string.Join("\n", koLines);
if (joined == _lastJoined) return;   // 未变，跳过（finally 仍 Dispose+复位）
_lastJoined = joined;
var zh = koLines.Count > 0 ? await _translator.TranslateAsync(koLines, CancellationToken.None) : Array.Empty<string>();
for (int k = 0; k < koLines.Count; k++)
{
    var (bounds, px) = metas[k];
    var frameBounds = new RectangleF(bounds.X + px.Left, bounds.Y + px.Top, bounds.Width, bounds.Height);
    var scr = ScreenMapper.MapFrameToScreen(frameBounds, frameSize, winRect);
    items.Add(new OverlayItem(zh[k], scr.X, scr.Y));
    _log.Log($"[{k}] \"{koLines[k]}\" -> \"{zh[k]}\"");
}
```
> 实现期按 `Phase1Probe.cs` 现有真实结构落地（上面为示意，注意 px 属于某个 roi，需在循环内配对；保持既有 winRect 守卫与 finally）。

- [ ] **Step 6: build** — `dotnet build` 预期 0 错误。
- [ ] **Step 7: 回归** — `dotnet test`（含 A4 重构后的 client 测试）预期全绿。
- [ ] **Step 8: 提交** — `git commit -m "feat(app): 托盘登录/填Key/选引擎 + 管线接翻译上屏中文"`

---

### Task B4: 手动验收冒烟 + 文档回写（收尾）

- [ ] **Step 1: SecretStore round-trip 冒烟**（可临时 main 或调试）：Save 一个测试 SecretData → Load 断言相等 → 删测试文件。确认 DPAPI 通路。
- [ ] **Step 2: 文档回写**：
  - `docs/ROADMAP.md`：Phase 2 勾项、Phase 3 措辞（两级/术语，去掉"Claude REST"改 OpenAI 订阅+阿里云）。
  - `CLAUDE.md`：LLM 提供方段（Claude→OpenAI 订阅 + 阿里云 DashScope）。
  - `docs/TECH_DESIGN.md`：翻译层与 provider 抽象更新。
- [ ] **Step 3: 提交** — `git commit -m "docs: 回写 Phase 2 provider 方向(OpenAI订阅+阿里云)"`
- [ ] **Step 4: 实机 e2e（留用户）**：窗口化 SC:R→`dotnet run`→托盘填 DashScope Key/登录 ChatGPT→选引擎→开探测→悬浮层显示中文。偏移调 `rois.json`。

---

## Self-Review 记录

- **Spec 覆盖**：两 provider(A4/A5/A6)、单活跃可切换(B3 SettingsHolder)、变化检测+缓存(A3/B3)、DPAPI(B1)、术语表直出(A2/A7)、最小托盘入口(B3)、降级回原文(A6/A7)、OAuth(A8/B2)、上屏(B3) — 全覆盖。
- **占位符**：无 TODO/TBD 残留（模型 id 标"待核实"是有意默认，非占位）。
- **类型一致**：`ITranslationProvider.TranslateAsync`、`TranslationService` 构造、`ITokenSource.GetAsync`、`OpenAiCompatibleChatClient`（A4 起即 `Func<string> apiKeyGetter`，B3 DI 直接传闭包，无中途重构）跨任务一致。
- **已知实现期微调**（已在任务内标注）：TranslationSettings 用 `SettingsHolder` 可变包装以支持运行时切引擎（B3 Step4）；GLOSSARY.md 建议设为 App 内容随包拷贝（B3 Step1 注）。
