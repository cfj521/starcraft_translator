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
