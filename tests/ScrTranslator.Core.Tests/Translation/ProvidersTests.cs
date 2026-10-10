using System.Net;
using System.Text.Json;
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
        var p = new DashScopeProvider(client, () => "qwen-flash", () => "key");
        var r = await p.TranslateAsync(new[]{"가","나"}, Ctx, CancellationToken.None);
        Assert.Equal(new[]{"甲","乙"}, r);
        Assert.Equal("dashscope", p.Name);
    }

    [Fact]
    public async Task DashScope_InjectsGlossaryHintIntoSystemPrompt()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        await new DashScopeProvider(client, () => "m", () => "key").TranslateAsync(new[]{"가"}, Ctx, CancellationToken.None);
        // System.Text.Json 默认把非 ASCII 转义成 \uXXXX,原始 body 无字面中韩文;
        // 必须解析 JSON 取 system 段(解码转义)再断言。system = messages[0].content。
        var sys = JsonDocument.Parse(stub.LastBody!).RootElement
            .GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("저그=虫族", sys);        // 术语提示进了 system prompt
        Assert.Contains("寻找房间", sys); // 场景也进了
    }

    [Fact]
    public async Task DashScope_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var input = new[]{"가","나"};
        var r = await new DashScopeProvider(client, () => "m", () => "key").TranslateAsync(input, Ctx, CancellationToken.None);
        Assert.Same(input, r); // 降级：原样返回传入引用
    }

    [Fact]
    public async Task Empty_ReturnsEmpty()
    {
        var stub = new StubHttpMessageHandler("{}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var r = await new DashScopeProvider(client, () => "m", () => "key").TranslateAsync(Array.Empty<string>(), Ctx, CancellationToken.None);
        Assert.Empty(r);
    }

    [Fact]
    public async Task DashScope_429_ThrowsRateLimit_AndEntersCooldown()
    {
        // 429 带 Retry-After：provider 进冷却（GetStatus 报 RateLimited，编排器会跳过它），
        // 异常仍重抛给编排器回退链，消息里带冷却时长与结束时刻。
        var stub = new StubHttpMessageHandler("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(3));
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var p = new DashScopeProvider(client, () => "m", () => "key");
        var ex = await Assert.ThrowsAsync<RateLimitException>(() => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(3), ex.RetryAfter);
        Assert.Contains("冷却", ex.Message);
        Assert.Equal(ProviderState.RateLimited, p.GetStatus().State);
        Assert.False(p.GetStatus().Usable);
        Assert.Contains("限流冷却", p.GetStatus().Detail);
    }

    [Fact]
    public async Task DashScope_429ThenSuccess_ResetsCooldown()
    {
        // 第一次 429 进冷却，第二次成功 → 冷却清零、退避归零，GetStatus 回到 Available。
        var ok = StubHttpMessageHandler.Make(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var limited = StubHttpMessageHandler.Make(HttpStatusCode.TooManyRequests, "limited");
        var stub = new StubHttpMessageHandler(new HttpResponseMessage[] { limited, ok });
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var p = new DashScopeProvider(client, () => "m", () => "key");
        await Assert.ThrowsAsync<RateLimitException>(() => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(ProviderState.RateLimited, p.GetStatus().State);
        var r = await p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None);
        Assert.Equal(new[] { "甲" }, r);
        Assert.Equal(ProviderState.Available, p.GetStatus().State);
    }

    [Fact]
    public async Task OpenAiApi_TranslatesBatch_AndName()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\\n[2] 乙\"}}]}");
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://api.openai.com/v1", () => "sk-test");
        var p = new OpenAiApiProvider(client, () => "gpt-4o", () => "sk-test");
        var r = await p.TranslateAsync(new[]{"가","나"}, Ctx, CancellationToken.None);
        Assert.Equal(new[]{"甲","乙"}, r);
        Assert.Equal("openai_api", p.Name);
    }

    [Fact]
    public void OpenAiApi_NoKey_IsNoCreds()
    {
        var client = new OpenAiCompatibleChatClient(new HttpClient(new StubHttpMessageHandler("{}")), "https://api.openai.com/v1", () => "sk-test");
        var p = new OpenAiApiProvider(client, () => "gpt-4o", () => "");
        Assert.Equal(ProviderState.NoCredentials, p.GetStatus().State);
        Assert.False(p.GetStatus().Usable);
        Assert.Contains("OpenAI API Key", p.GetStatus().Detail);
    }

    [Fact]
    public void OpenAiApi_WithKey_IsAvailable()
    {
        var client = new OpenAiCompatibleChatClient(new HttpClient(new StubHttpMessageHandler("{}")), "https://api.openai.com/v1", () => "sk-test");
        var p = new OpenAiApiProvider(client, () => "gpt-4o", () => "sk-test");
        Assert.Equal(ProviderState.Available, p.GetStatus().State);
        Assert.True(p.GetStatus().Usable);
    }

    [Fact]
    public async Task ChatGpt_429_ThrowsRateLimit_AndEntersCooldown()
    {
        var stub = new StubHttpMessageHandler("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2));
        var client = new CodexResponsesClient(new HttpClient(stub));
        var p = new ChatGptSubscriptionProvider(client, new FakeTokens(), () => "gpt-5");
        await Assert.ThrowsAsync<RateLimitException>(() => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(ProviderState.RateLimited, p.GetStatus().State);
        Assert.Contains("限流冷却", p.GetStatus().Detail);
    }

    private sealed class FakeTokens : ITokenSource
    {
        public Task<(string accessToken, string accountId)> GetAsync(CancellationToken ct)
            => Task.FromResult(("TOK", "ACC"));
        public bool IsLoggedIn() => true;
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
        // 同理解析:Codex 请求体的 system 段 = instructions 字段。
        var instructions = JsonDocument.Parse(stub.LastBody!).RootElement
            .GetProperty("instructions").GetString();
        Assert.Contains("저그=虫族", instructions);
    }
}
