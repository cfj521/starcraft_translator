using System.Net;
using ScrTranslator.Core.Translation;
using ScrTranslator.Core.Translation.Providers;

namespace ScrTranslator.Core.Tests.Translation;

/// <summary>免费机器翻译 provider（阿里云）+ 签名器的行为测试。
/// 签名器是纯计算，用已知向量验证正确性；provider 用 StubHttpMessageHandler 端到端跑。</summary>
public class MachineTranslationProviderTests
{
    private static readonly TranslationContext Ctx = new("browsing", null);

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

    // ── 阿里云 provider 端到端 ──────────────────────────────────────────

    [Fact]
    public async Task Aliyun_TranslatesBatch_JoinsAndSplitsByNewline()
    {
        // JSON 里 \n 是换行的转义；GetString 解码成真实换行后按行拆回。
        var stub = new StubHttpMessageHandler("{\"Data\":{\"Translated\":\"甲\\n乙\"},\"RequestId\":\"r\"}");
        var p = new AliyunMtProvider(new HttpClient(stub), () => ("ak", "sk"));
        var r = await p.TranslateAsync(new[] { "가", "나" }, Ctx, CancellationToken.None);
        Assert.Equal(new[] { "甲", "乙" }, r);
        Assert.Equal("aliyun", p.Name);
        var url = stub.LastRequest!.RequestUri!.ToString();
        Assert.Contains("mt.aliyuncs.com", url);
        Assert.Contains("Action=TranslateGeneral", url);
        // Version 是 RPC 必带公共参数；缺了网关报 InvalidAction.NotFound(404)——回归守卫
        Assert.Contains("Version=2018-10-12", url);
        Assert.Contains("Signature=", url);
    }

    [Fact]
    public async Task Aliyun_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"Data\":{\"Translated\":\"甲\"}}"); // 只拆出 1 行，输入 2 行
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
