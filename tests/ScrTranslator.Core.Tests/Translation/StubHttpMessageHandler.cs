using System.Net;
using System.Net.Http.Headers;
namespace ScrTranslator.Core.Tests.Translation;
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest; public string? LastBody;
    private readonly Func<HttpResponseMessage> _next;
    private readonly string _resp; private readonly HttpStatusCode _code; private readonly TimeSpan? _retryAfter;

    /// <summary>每次请求都返回同一响应（code/body 固定）；retryAfter 非空时响应带 Retry-After 头（429 限流测试用）。</summary>
    public StubHttpMessageHandler(string resp, HttpStatusCode code = HttpStatusCode.OK, TimeSpan? retryAfter = null)
    { _resp = resp; _code = code; _retryAfter = retryAfter; _next = () => Make(_code, _resp, _retryAfter); }

    /// <summary>按序返回不同响应（队列耗尽后退回 200 "{}"，防止意外多打请求时报 IndexOutOfRange 掩盖真问题）。</summary>
    public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
    { _resp = ""; _code = HttpStatusCode.OK; _retryAfter = null;
      var q = new Queue<HttpResponseMessage>(responses);
      _next = () => q.Count > 0 ? q.Dequeue() : Make(HttpStatusCode.OK, "{}", null); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
        return _next();
    }

    /// <summary>造一个带 body（和可选 Retry-After 头）的响应。</summary>
    public static HttpResponseMessage Make(HttpStatusCode code, string body, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (retryAfter is not null) r.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return r;
    }
}
