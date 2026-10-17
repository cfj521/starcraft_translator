using System.IO;
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
    public async Task CustomModel_TranslatesBatch_AndName()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\\n[2] 乙\"}}]}");
        var p = new CustomModelProvider(new HttpClient(stub), () => "local-model", () => "https://localhost:8080/v1", () => "sk-local");
        var r = await p.TranslateAsync(new[]{"가","나"}, Ctx, CancellationToken.None);
        Assert.Equal(new[]{"甲","乙"}, r);
        Assert.Equal("custom_model", p.Name);
        Assert.Equal(EngineKind.LLM, p.Kind);
    }

    [Fact]
    public void CustomModel_NoUrlOrKey_IsNoCreds()
    {
        // URL 或 KEY 任一为空即未配置（编排器跳过、不发请求）
        var pNoUrl = new CustomModelProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => "m", () => "", () => "sk");
        Assert.Equal(ProviderState.NoCredentials, pNoUrl.GetStatus().State);
        Assert.False(pNoUrl.GetStatus().Usable);
        Assert.Contains("自定义模型", pNoUrl.GetStatus().Detail);

        var pNoKey = new CustomModelProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => "m", () => "https://x/v1", () => "");
        Assert.Equal(ProviderState.NoCredentials, pNoKey.GetStatus().State);
        Assert.False(pNoKey.GetStatus().Usable);
    }

    [Fact]
    public void CustomModel_WithUrlAndKey_IsAvailable()
    {
        var p = new CustomModelProvider(new HttpClient(new StubHttpMessageHandler("{}")), () => "m", () => "https://x/v1", () => "sk");
        Assert.Equal(ProviderState.Available, p.GetStatus().State);
        Assert.True(p.GetStatus().Usable);
    }

    [Fact]
    public async Task CustomModel_429_ThrowsRateLimit_AndEntersCooldown()
    {
        var stub = new StubHttpMessageHandler("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2));
        var p = new CustomModelProvider(new HttpClient(stub), () => "m", () => "https://x/v1", () => "sk");
        await Assert.ThrowsAsync<RateLimitException>(() => p.TranslateAsync(new[] { "가" }, Ctx, CancellationToken.None));
        Assert.Equal(ProviderState.RateLimited, p.GetStatus().State);
        Assert.Contains("限流冷却", p.GetStatus().Detail);
    }

    [Fact]
    public async Task CustomModel_WrongCount_FallsBackToOriginal()
    {
        var stub = new StubHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"[1] 甲\"}}]}");
        var input = new[]{"가","나"};
        var p = new CustomModelProvider(new HttpClient(stub), () => "m", () => "https://x/v1", () => "sk");
        var r = await p.TranslateAsync(input, Ctx, CancellationToken.None);
        Assert.Same(input, r); // 降级：原样返回传入引用
    }

    [Fact]
    public async Task CustomModel_Empty_ReturnsEmpty()
    {
        var stub = new StubHttpMessageHandler("{}");
        var p = new CustomModelProvider(new HttpClient(stub), () => "m", () => "https://x/v1", () => "sk");
        var r = await p.TranslateAsync(Array.Empty<string>(), Ctx, CancellationToken.None);
        Assert.Empty(r);
    }

    [Fact]
    public void HyMt2_NameAndKind()
    {
        var p = new HyMt2Provider(() => @"D:\models\Tencent\Hy-MT2\Hy-MT2-1.8B-2Bit.gguf", () => "cpu");
        Assert.Equal("hy_mt2", p.Name);
        Assert.Equal(EngineKind.LLM, p.Kind);
    }

    [Fact]
    public void KoreanOnlyProtector_ProtectsNonKorean_AndRestores()
    {
        // 非韩文串（英文/数字/标点）抽成占位符（占位符前后各加一个空格让模型分词）；空格/韩文原样。
        // 回填把占位符还原成原串，并把保护时加的空格收成单空格+trim（所以回填文本会在原 token 旁多一个空格，与原文不完全逐字）。
        var (protectedLines, tokens) = KoreanOnlyProtector.Protect(new[] { "안녕? A1, 안녕!", "순수한 한글" });
        Assert.Equal(new[] { "?", "A1,", "!" }, tokens[0]);
        Assert.Contains("⟨0⟩", protectedLines[0]);
        Assert.Contains("⟨1⟩", protectedLines[0]);
        Assert.Contains("⟨2⟩", protectedLines[0]);
        Assert.Contains("안녕", protectedLines[0]);
        Assert.Equal("순수한 한글", protectedLines[1]); // 纯韩文无占位符
        Assert.Empty(tokens[1]);
        var restored = KoreanOnlyProtector.Restore(protectedLines[0], tokens[0])!;
        Assert.Contains("?", restored);
        Assert.Contains("A1,", restored);
        Assert.Contains("!", restored);
        Assert.Contains("안녕", restored);
        Assert.Null(KoreanOnlyProtector.Restore("안녕 ⟨0⟩ 안녕", tokens[0])); // ⟨1⟩/⟨2⟩ 丢失 → null
        Assert.True(KoreanOnlyProtector.HasTokens(tokens));
    }

    [Fact]
    public void KoreanOnlyProtector_ParseAndRestore_RestoresAndEchoesBack()
    {
        var original = new[] { "안녕 A", "안녕 B" };
        var (protectedLines, tokens) = KoreanOnlyProtector.Protect(original);
        // 正常：译文保留占位符 → 还原成原文的英文/标点
        Assert.Equal(new[] { "你好 A", "你好 B" },
            KoreanOnlyProtector.ParseAndRestore("[1] 你好 ⟨0⟩\n[2] 你好 ⟨0⟩", original, protectedLines, tokens));
        // 回声：用真实的保护行构造回复（引擎把占位符原文吐回来）→ 回退原文的**新字符串**（值相等、引用不等，编排器据此判 Resolved、不再每拍重试）
        string echoReply = "[1] " + protectedLines[0] + "\n[2] " + protectedLines[1];
        var echoed = KoreanOnlyProtector.ParseAndRestore(echoReply, original, protectedLines, tokens);
        Assert.Equal(original[0], echoed![0]);
        Assert.NotSame(original[0], echoed![0]);
        // Parse 失败（行数不符）→ null（整批回退原文）
        Assert.Null(KoreanOnlyProtector.ParseAndRestore("[1] 你好", original, protectedLines, tokens));
    }

    [Fact]
    public void KoreanOnlyProtector_ParseAndRestore_InstructionEcho_FallsBackToOriginal()
    {
        // 房间名含英文+符号：MAP핵+… → 保护成 ⟨0⟩핵⟨1⟩…（MAP→⟨0⟩、+→⟨1⟩）。
        var original = new[] { "MAP핵+다기능문의는맵이름" };
        var (protectedLines, tokens) = KoreanOnlyProtector.Protect(original);
        Assert.Contains("⟨0⟩", protectedLines[0]);
        Assert.Contains("⟨1⟩", protectedLines[0]);
        // 模型没翻译韩文，而是把占位符指令（"行内的 ⟨N⟩ 是占位符…请原样保留，不要翻译或修改"）改述一遍当"译文"输出：
        var echoReply = "[1] 诸如⟨0⟩和⟨1⟩这样的标记是英语单词、数字和标点符号的占位符；请保持它们原样，不要进行翻译或修改。";
        var echoed = KoreanOnlyProtector.ParseAndRestore(echoReply, original, protectedLines, tokens);
        // 检出指令回声 → 回退原文的**新字符串**（值相等、引用不等 → 编排器判 Resolved、缓存原文、不再每拍重试），
        // 而不是回填后的指令回声（"诸如MAP和+…占位符…"）被当译文显示出去。
        Assert.Equal(original[0], echoed![0]);
        Assert.NotSame(original[0], echoed![0]);
    }

    [Fact]
    public void KoreanOnlyProtector_IsInstructionEcho_DetectsEchoedInstruction()
    {
        // 模型把占位符指令（中文版 PromptBuilder / 英文版 Hy-MT2 PlaceholderHint 翻成中文）改述当译文 → 含"占位符"。
        Assert.True(KoreanOnlyProtector.IsInstructionEcho(
            "诸如MAP和+这样的标记是英语单词、数字和标点符号的占位符；请保持它们原样，不要进行翻译或修改。"));
        // 正常译文/原文/专名回声都不含"占位符" → 不是指令回声。
        Assert.False(KoreanOnlyProtector.IsInstructionEcho("MAP核心+多功能询问的是地图名字"));
        Assert.False(KoreanOnlyProtector.IsInstructionEcho("MAP핵+다기능문의는맵이름"));
        Assert.False(KoreanOnlyProtector.IsInstructionEcho("你好世界"));
    }

    [Fact]
    public void HyMt2_EmptyPath_IsNoCreds()
    {
        var p = new HyMt2Provider(() => "", () => "cpu");
        Assert.Equal(ProviderState.NoCredentials, p.GetStatus().State);
        Assert.False(p.GetStatus().Usable);
        Assert.Contains("Hy-MT2", p.GetStatus().Detail);
    }

    [Fact]
    public void HyMt2_MissingFile_IsNoCreds()
    {
        // 用临时目录里绝不存在的文件名，保证"文件不存在"（避免撞上真实权重路径）。
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".gguf");
        var p = new HyMt2Provider(() => missing, () => "cpu");
        Assert.Equal(ProviderState.NoCredentials, p.GetStatus().State);
        Assert.False(p.GetStatus().Usable);
    }

    [Fact]
    public void HyMt2_ExistingFile_IsAvailable()
    {
        string f = Path.GetTempFileName(); // 创建临时文件（GetStatus 只查存在性，不加载权重）
        try
        {
            var p = new HyMt2Provider(() => f, () => "cpu");
            Assert.Equal(ProviderState.Available, p.GetStatus().State);
            Assert.True(p.GetStatus().Usable);
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact]
    public async Task HyMt2_EmptyLines_ReturnsEmpty_WithoutLoadingModel()
    {
        // 空批直接返回空，不触碰模型（懒加载不触发）。路径给个不存在的文件也无妨。
        var p = new HyMt2Provider(() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".gguf"), () => "cpu");
        var r = await p.TranslateAsync(Array.Empty<string>(), Ctx, CancellationToken.None);
        Assert.Empty(r);
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
