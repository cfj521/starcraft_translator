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
