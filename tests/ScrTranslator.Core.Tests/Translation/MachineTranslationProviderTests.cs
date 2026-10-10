using System.Net;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.Core.Tests.Translation;

/// <summary>两个免费机器翻译 provider（腾讯云 / 阿里云）+ 各自签名器的行为测试。
/// 签名器是纯计算，用已知向量（SHA-256("abc")）验证正确性；provider 用 StubHttpMessageHandler 端到端跑。</summary>
public class MachineTranslationProviderTests
{
    private static readonly TranslationContext Ctx = new("browsing", null);

    // ── 腾讯云签名器 ────────────────────────────────────────────────────

    [Fact]
    public void Tencent_Sha256Hex_KnownVector()
    {
        // SHA-256("abc") 的标准值，验证哈希实现正确。
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TencentMtSigner.Sha256Hex("abc"));
    }

    [Fact]
    public void Tencent_Sign_ProducesValidAuthorization()
    {
        var (auth, ts, date) = TencentMtSigner.Sign("secretId", "secretKey", "{\"a\":1}", 1700000000L);
        Assert.Equal("1700000000", ts);
        Assert.Equal("2023-11-14", date); // 1700000000 UTC = 2023-11-14
        // 结构：TC3-HMAC-SHA256 Credential=<id>/<date>/tmt/tc3_request, SignedHeaders=..., Signature=<64 hex>
        Assert.StartsWith("TC3-HMAC-SHA256 Credential=secretId/2023-11-14/tmt/tc3_request, ", auth);
        Assert.Contains("SignedHeaders=content-type;host, ", auth);
        var sig = auth.Split("Signature=")[1];
        Assert.Matches("^[0-9a-f]{64}$", sig);
    }

    [Fact]
    public void Tencent_Sign_IsDeterministic_AndKeySensitive()
    {
        var a = TencentMtSigner.Sign("id", "key1", "payload", 1700000000L).Authorization;
        var b = TencentMtSigner.Sign("id", "key1", "payload", 1700000000L).Authorization;
        var c = TencentMtSigner.Sign("id", "key2", "payload", 1700000000L).Authorization;
        Assert.Equal(a, b);                 // 同输入同输出
        Assert.NotEqual(a, c);             // 换 key 签名必变
    }

    // ── 阿里云签名器 ────────────────────────────────────────────────────

    [Fact]
    public void Aliyun_PercentEncode_SpecialChars()
    {
        Assert.Equal("%20", AliyunMtSigner.PercentEncode("+"));   // + → %20
        Assert.Equal("%2A", AliyunMtSigner.PercentEncode("*"));   // * → %2A
        Assert.Equal("~", AliyunMtSigner.PercentEncode("~"));     // %7E → ~
        Assert.Equal("abc123", AliyunMtSigner.PercentEncode("abc123")); // 字母数字不变
        Assert.Equal("a%20b", AliyunMtSigner.PercentEncode("a b"));    // 空格 → %20
    }

    [Fact]
    public void Aliyun_Canonicalize_SortsByKeyAndEncodes()
    {
        var p = new Dictionary<string, string> { ["SourceText"] = "가 나", ["Action"] = "Translate" };
        var canonical = AliyunMtSigner.Canonicalize(p);
        // Action 在 SourceText 前（字典序），且值被阿里云式编码（空格 → %20）
        Assert.Equal("Action=Translate&SourceText=%EA%B0%80%20%EB%82%98", canonical);
    }

    [Fact]
    public void Aliyun_ComputeSignature_Base64AndDeterministic()
    {
        var s1 = AliyunMtSigner.ComputeSignature("POST&%2F&x", "secret&");
        var s2 = AliyunMtSigner.ComputeSignature("POST&%2F&x", "secret&");
        var s3 = AliyunMtSigner.ComputeSignature("POST&%2F&y", "secret&");
        Assert.Equal(s1, s2);
        Assert.NotEqual(s1, s3);
        Convert.FromBase64String(s1); // 不抛 = 合法 Base64
    }

    // ── 腾讯云 provider 端到端 ──────────────────────────────────────────

    [Fact]
    public async Task Tencent_TranslatesBatch_ViaTargetTextList()
    {
        var stub = new StubHttpMessageHandler("{\"Response\":{\"TargetTextList\":[\"甲\",\"乙\"],\"RequestId\":\"r\"}}");
        var p = new TencentMtProvider(new HttpClient(stub), () => ("id", "key"));
        var r = await p.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);
        Assert.Equal(new[] { "甲", "乙" }, r);
        Assert.Equal("tencent", p.Name);
        // TC3-HMAC-SHA256 签名头被塞进请求，且打到正确 host
        Assert.Contains("TC3-HMAC-SHA256", stub.LastRequest!.Headers.ToString());
        Assert.Contains("tencentcloud.com", stub.LastRequest.RequestUri!.ToString());
        Assert.Contains("X-TC-Action", stub.LastRequest.Headers.ToString());
    }

    [Fact]
    public async Task Tencent_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"Response\":{\"TargetTextList\":[\"甲\"]}}"); // 只有 1 行，输入 2 行
        var input = new[] { "가", "나" };
        var p = new TencentMtProvider(new HttpClient(stub), () => ("id", "key"));
        var r = await p.TranslateAsync(input, Ctx, CancellationToken.None);
        Assert.Same(input, r);
    }

    [Fact]
    public async Task Tencent_QuotaError_MarksExhausted()
    {
        var stub = new StubHttpMessageHandler(
            "{\"Response\":{\"Error\":{\"Code\":\"LimitExceeded\",\"Message\":\"quota\"}}}",
            HttpStatusCode.Forbidden);
        var p = new TencentMtProvider(new HttpClient(stub), () => ("id", "key"));
        await Assert.ThrowsAsync<ProviderQuotaExhaustedException>(
            () => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(ProviderState.QuotaExhausted, p.GetStatus().State);
    }

    [Fact]
    public void Tencent_GetStatus_NoCredsVsAvailable()
    {
        Assert.Equal(ProviderState.NoCredentials,
            new TencentMtProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => ("", "")).GetStatus().State);
        Assert.Equal(ProviderState.Available,
            new TencentMtProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => ("id", "key")).GetStatus().State);
    }

    // ── 阿里云 provider 端到端 ──────────────────────────────────────────

    [Fact]
    public async Task Aliyun_TranslatesBatch_JoinsAndSplitsByNewline()
    {
        // JSON 里 \n 是换行的转义；GetString 解码成真实换行后按行拆回。
        var stub = new StubHttpMessageHandler("{\"Translated\":\"甲\\n乙\",\"RequestId\":\"r\"}");
        var p = new AliyunMtProvider(new HttpClient(stub), () => ("ak", "sk"));
        var r = await p.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);
        Assert.Equal(new[] { "甲", "乙" }, r);
        Assert.Equal("aliyun", p.Name);
        var url = stub.LastRequest!.RequestUri!.ToString();
        Assert.Contains("mt.aliyuncs.com", url);
        Assert.Contains("Action=Translate", url);
        Assert.Contains("Signature=", url);
    }

    [Fact]
    public async Task Aliyun_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"Translated\":\"甲\"}"); // 只拆出 1 行，输入 2 行
        var input = new[] { "가", "나" };
        var p = new AliyunMtProvider(new HttpClient(stub), () => ("ak", "sk"));
        var r = await p.TranslateAsync(input, Ctx, CancellationToken.None);
        Assert.Same(input, r);
    }

    [Fact]
    public async Task Aliyun_QuotaError_MarksExhausted()
    {
        var stub = new StubHttpMessageHandler(
            "{\"Code\":\"QuotaExhausted\",\"Message\":\"free quota used\"}",
            HttpStatusCode.Forbidden);
        var p = new AliyunMtProvider(new HttpClient(stub), () => ("ak", "sk"));
        await Assert.ThrowsAsync<ProviderQuotaExhaustedException>(
            () => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(ProviderState.QuotaExhausted, p.GetStatus().State);
    }

    [Fact]
    public void Aliyun_GetStatus_NoCredsVsAvailable()
    {
        Assert.Equal(ProviderState.NoCredentials,
            new AliyunMtProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => ("", "")).GetStatus().State);
        Assert.Equal(ProviderState.Available,
            new AliyunMtProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => ("ak", "sk")).GetStatus().State);
    }
}
