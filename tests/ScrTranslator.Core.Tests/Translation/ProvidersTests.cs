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
        // System.Text.Json 默认把非 ASCII 转义成 \uXXXX,原始 body 无字面中韩文;
        // 必须解析 JSON 取 system 段(解码转义)再断言。system = messages[0].content。
        var sys = JsonDocument.Parse(stub.LastBody!).RootElement
            .GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("저그=虫族", sys);        // 术语提示进了 system prompt
        Assert.Contains("自定义游戏房间列表", sys); // 场景也进了
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
        // 同理解析:Codex 请求体的 system 段 = instructions 字段。
        var instructions = JsonDocument.Parse(stub.LastBody!).RootElement
            .GetProperty("instructions").GetString();
        Assert.Contains("저그=虫族", instructions);
    }
}
