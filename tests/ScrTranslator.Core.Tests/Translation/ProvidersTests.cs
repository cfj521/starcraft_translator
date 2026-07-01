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
