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

    [Fact]
    public async Task Status429_ThrowsRateLimit_WithRetryAfter()
    {
        // 429 不是一般失败：要单独识别成 RateLimitException 并带上服务端的 Retry-After，provider 靠它进冷却。
        var stub = new StubHttpMessageHandler("slow down", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(9));
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.CompleteAsync("m", "s", "u", CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(9), ex.RetryAfter);
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task Status429_WithoutHeader_RetryAfterIsNull()
    {
        var stub = new StubHttpMessageHandler("slow down", HttpStatusCode.TooManyRequests);
        var client = new OpenAiCompatibleChatClient(new HttpClient(stub), "https://x/v1", () => "K");
        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.CompleteAsync("m", "s", "u", CancellationToken.None));
        Assert.Null(ex.RetryAfter); // 没带头 → provider 改走指数退避
    }
}
